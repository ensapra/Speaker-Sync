using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeakerSync
{
    public class OutputSpec
    {
        public int DeviceNumber { get; set; }
        public int DelayMs { get; set; }
    }

    public class CalibrationMarker
    {
        public int DeviceNumber { get; set; }
        public long InjectionTimestamp { get; set; }
        public int EstimatedBufferDepthMs { get; set; }
    }

    public class AudioEngine
    {
        private WaveInEvent? waveIn;
        private readonly List<WaveOutEvent> players = new();
        private readonly List<DelayedWaveProvider> delayedProviders = new();
        private readonly Dictionary<int, DelayedWaveProvider> delayedProvidersByDevice = new();
        private readonly Dictionary<int, MMDevice> endpointVolumes = new();
        private CalibrationMarker? currentMarker = null;
        private readonly object markerLock = new();

        public WaveFormat? Format => waveIn?.WaveFormat;

        public void RefreshOutputEndpointMappings()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                endpointVolumes.Clear();
                for (int i = 0; i < devices.Count; i++)
                {
                    endpointVolumes[i] = devices[i];
                }
            }
            catch
            {
                endpointVolumes.Clear();
            }
        }

        public string[] GetInputDevices()
        {
            var arr = new List<string>();
            for (int i = 0; i < WaveIn.DeviceCount; i++) arr.Add(WaveIn.GetCapabilities(i).ProductName);
            return arr.ToArray();
        }

        public string[] GetOutputDevices()
        {
            var arr = new List<string>();
            for (int i = 0; i < WaveOut.DeviceCount; i++) arr.Add(WaveOut.GetCapabilities(i).ProductName);
            return arr.ToArray();
        }

        public void Start(int inputDevice, IEnumerable<OutputSpec> outputs, int sampleRate = 44100)
        {
            Stop();

            waveIn = new WaveInEvent
            {
                DeviceNumber = inputDevice,
                BufferMilliseconds = 50,
                WaveFormat = new WaveFormat(sampleRate, 16, 2)
            };

            foreach (var o in outputs)
            {
                var provider = new DelayedWaveProvider(waveIn.WaveFormat, bufferMilliseconds: Math.Max(1000, o.DelayMs + 250));
                provider.SetDelayMs(o.DelayMs);
                provider.Volume = 1.0f;

                var wo = new WaveOutEvent { DeviceNumber = o.DeviceNumber };
                wo.Init(provider);
                players.Add(wo);
                delayedProviders.Add(provider);
                delayedProvidersByDevice[o.DeviceNumber] = provider;
            }

            waveIn.DataAvailable += WaveIn_DataAvailable;
            waveIn.RecordingStopped += (s, e) => { };

            // Start recording first to fill buffers, then play all devices
            waveIn.StartRecording();
            Thread.Sleep(50); // give buffers a chance to fill
            foreach (var p in players)
            {
                try { p.Play(); }
                catch { }
            }
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            for (int i = 0; i < delayedProviders.Count; i++)
            {
                try { delayedProviders[i].AddSamples(e.Buffer, 0, e.BytesRecorded); }
                catch { }
            }
        }

        public void Stop()
        {
            try
            {
                waveIn?.StopRecording();
                waveIn?.Dispose();
            }
            catch { }
            waveIn = null;

            foreach (var p in players) { try { p.Stop(); p.Dispose(); } catch { } }
            players.Clear();
            delayedProviders.Clear();
            delayedProvidersByDevice.Clear();
            endpointVolumes.Clear();
        }

        public bool TryGetSystemOutputVolume(int deviceNumber, out float volume)
        {
            volume = 0f;
            if (!endpointVolumes.TryGetValue(deviceNumber, out var endpoint))
                return false;

            try
            {
                volume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TrySetSystemOutputVolume(int deviceNumber, float volume)
        {
            if (!endpointVolumes.TryGetValue(deviceNumber, out var endpoint))
                return false;

            try
            {
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Runtime control methods
        public void SetOutputDelay(int deviceNumber, int delayMs)
        {
            if (!delayedProvidersByDevice.TryGetValue(deviceNumber, out var provider)) return;
            provider.SetDelayMs(delayMs);
        }

        public void SetOutputVolume(int deviceNumber, float volume)
        {
            if (!delayedProvidersByDevice.TryGetValue(deviceNumber, out var provider)) return;
            float boundedVolume = Math.Clamp(volume, 0f, 2f);
            provider.Volume = boundedVolume;
            TrySetSystemOutputVolume(deviceNumber, boundedVolume);
        }

        /// <summary>
        /// Injects a marker burst into the specified output device's audio chain.
        /// Returns the injection timestamp so caller can measure when it's heard.
        /// </summary>
        public CalibrationMarker? InjectMarkerBurst(int deviceNumber)
        {
            if (!delayedProvidersByDevice.TryGetValue(deviceNumber, out var provider) || waveIn?.WaveFormat is null)
                return null;

            lock (markerLock)
            {
                currentMarker = null;
            }

            // Create a short high-amplitude burst (500ms click at 1kHz sine wave)
            var format = waveIn.WaveFormat;
            var burst = new SignalGenerator
            {
                Gain = 0.5f,
                Frequency = 1000,
                Type = SignalGeneratorType.Sin
            };

            // Generate 500ms of signal
            int sampleCount = (format.SampleRate * 500) / 1000;
            byte[] burstData = new byte[sampleCount * format.BlockAlign];
            burst.Read(burstData, 0, burstData.Length);

            // Get approximate buffer depth from the provider
            int estimatedBufferMs = 250; // Initial buffer cushion

            // Inject the burst
            provider.AddSamples(burstData, 0, burstData.Length);

            // Record marker info
            var marker = new CalibrationMarker
            {
                DeviceNumber = deviceNumber,
                InjectionTimestamp = Stopwatch.GetTimestamp(),
                EstimatedBufferDepthMs = estimatedBufferMs
            };

            lock (markerLock)
            {
                currentMarker = marker;
            }

            return marker;
        }

        /// <summary>
        /// Gets the current marker's injection timestamp (for latency measurement).
        /// </summary>
        public long? GetCurrentMarkerTimestamp()
        {
            lock (markerLock)
            {
                return currentMarker?.InjectionTimestamp;
            }
        }

        /// <summary>
        /// Clears the current calibration marker.
        /// </summary>
        public void ClearMarker()
        {
            lock (markerLock)
            {
                currentMarker = null;
            }
        }

        public void RefreshDevices()
        {
            // no-op in engine, main UI will call GetInputDevices/GetOutputDevices again
        }
    }
}
