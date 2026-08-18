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

        public void RefreshDevices()
        {
            // no-op in engine, main UI will call GetInputDevices/GetOutputDevices again
        }
    }
}
