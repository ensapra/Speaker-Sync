using System;
using NAudio.Wave;

namespace SpeakerSync
{
    // A sample-accurate circular buffer provider for 16-bit PCM audio.
    public class DelayedWaveProvider : IWaveProvider
    {
        private readonly WaveFormat waveFormat;
        private readonly byte[] buffer;
        private int writePos;
        private int readPos;
        private int queuedBytes;
        private int delayBytes;
        private readonly object lockObj = new();
        public float Volume { get; set; } = 1.0f;

        public DelayedWaveProvider(WaveFormat format, int bufferMilliseconds = 2000)
        {
            waveFormat = format;
            int bytesPerMs = format.AverageBytesPerSecond / 1000;
            int size = bytesPerMs * Math.Max(2000, bufferMilliseconds);
            size = AlignToBlock(size);
            if (size < format.BlockAlign * 8) size = format.BlockAlign * 8;
            buffer = new byte[size];
            writePos = 0;
            readPos = 0;
            queuedBytes = 0;
            delayBytes = 0;
        }

        public WaveFormat WaveFormat => waveFormat;

        private int AlignToBlock(int value)
        {
            return ((value + waveFormat.BlockAlign - 1) / waveFormat.BlockAlign) * waveFormat.BlockAlign;
        }

        public void AddSamples(byte[] data, int offset, int count)
        {
            if (count <= 0) return;

            lock (lockObj)
            {
                int src = offset;
                int remaining = count;

                while (remaining > 0)
                {
                    int freeSpace = buffer.Length - queuedBytes;
                    if (freeSpace <= 0)
                    {
                        int discard = Math.Min(queuedBytes, Math.Max(waveFormat.BlockAlign, remaining));
                        if (discard <= 0) break;
                        AdvanceRead(discard);
                        continue;
                    }

                    int spaceToEnd = buffer.Length - writePos;
                    int chunk = Math.Min(remaining, Math.Min(spaceToEnd, freeSpace));
                    if (chunk <= 0) break;

                    Buffer.BlockCopy(data, src, buffer, writePos, chunk);
                    writePos += chunk;
                    if (writePos >= buffer.Length) writePos = 0;
                    queuedBytes += chunk;
                    src += chunk;
                    remaining -= chunk;
                }
            }
        }

        private void AdvanceRead(int bytes)
        {
            if (bytes <= 0) return;
            readPos = (readPos + bytes) % buffer.Length;
            queuedBytes -= bytes;
            if (queuedBytes < 0) queuedBytes = 0;
        }

        public void SetDelayMs(int ms)
        {
            int safeMs = Math.Max(0, ms);
            int bytesPerMs = waveFormat.AverageBytesPerSecond / 1000;
            int delay = bytesPerMs * safeMs;
            delay = AlignToBlock(delay);

            lock (lockObj)
            {
                delayBytes = delay;
            }
        }

        public int Read(byte[] destBuffer, int offset, int count)
        {
            lock (lockObj)
            {
                int availableDelayed = queuedBytes - delayBytes;
                int bytesToRead = count;
                if (availableDelayed <= 0)
                {
                    Array.Clear(destBuffer, offset, count);
                    return count;
                }

                bytesToRead = Math.Min(count, availableDelayed);
                int dest = offset;
                int remaining = bytesToRead;

                while (remaining > 0)
                {
                    int toEnd = buffer.Length - readPos;
                    int chunk = Math.Min(remaining, toEnd);
                    Buffer.BlockCopy(buffer, readPos, destBuffer, dest, chunk);
                    ApplyVolume(destBuffer, dest, chunk);

                    readPos += chunk;
                    if (readPos >= buffer.Length) readPos = 0;
                    dest += chunk;
                    remaining -= chunk;
                    queuedBytes -= chunk;
                }

                if (count > bytesToRead)
                {
                    Array.Clear(destBuffer, offset + bytesToRead, count - bytesToRead);
                }

                return count;
            }
        }

        private void ApplyVolume(byte[] data, int offset, int count)
        {
            if (Volume == 1.0f || waveFormat.BitsPerSample != 16) return;

            int sampleCount = count / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                int sampleIndex = offset + (i * 2);
                short sample = BitConverter.ToInt16(data, sampleIndex);
                int scaled = (int)(sample * Volume);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                if (scaled < short.MinValue) scaled = short.MinValue;
                BitConverter.GetBytes((short)scaled).CopyTo(data, sampleIndex);
            }
        }
    }
}
