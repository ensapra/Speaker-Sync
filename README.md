# SpeakerSync

A Windows audio routing application that captures audio from a virtual input device and reproduces it simultaneously to multiple real output devices with independent, adjustable delays and volume levels.

## Features

- **Multi-device output routing** — Play audio to multiple speakers simultaneously
- **Per-device delay control** — Set delay (0–5000 ms) for each output independently
- **Per-device volume control** — Adjust volume via slider (0–200%) or direct numeric input
- **Flexible sample rates** — Support for 44.1 kHz, 48 kHz, and 96 kHz
- **Dark theme UI** — Modern, minimalistic interface with sharp transitions
- **Runtime controls** — Change volume and delay on the fly while playing

## Prerequisites

1. **Virtual audio device** — Install [VB-CABLE](https://www.vb-audio.com/Cable/) or similar to create a virtual capture device
2. **.NET 10 SDK** — Required to build and run

### Install .NET 10

```powershell
winget install Microsoft.DotNet.SDK.10
```

Verify installation:
```powershell
dotnet --version
```

## Quick Start

1. **Set up the virtual device**: Configure VB-CABLE (or equivalent) as your capture source
2. **Build**:
   ```powershell
   cd "c:\Users\pauso\Documents\Speaker Sync\SpeakerSync"
   dotnet build
   ```
3. **Run**:
   ```powershell
   dotnet run
   # or use the helper script:
   .\run.ps1
   ```

## Usage

1. Select your **input device** (the virtual cable) from the dropdown
2. Choose a **sample rate** (44100, 48000, or 96000 Hz)
3. In the **Outputs** section:
   - Check the boxes for each output device you want to use
   - Adjust **volume** with the slider or type a value (0–200)
   - Set **delay** in milliseconds (0–5000 ms)
4. Click **Start** to begin routing audio
5. Adjust delays/volumes in real-time while playing
6. Click **Stop** to stop routing

## Technical Details

### Audio Engine
- **Capture**: Records from selected input device at chosen sample rate (16-bit, stereo)
- **Delay buffers**: Sample-accurate circular buffers provide precise, runtime-adjustable delays
- **Volume**: Per-device volume scaling applied in real-time (0.0–2.0 scale, 100 = 1.0x)
- **Playback**: Multi-threaded simultaneous playback to all selected output devices

### Latency Optimization
- Small input buffer (50 ms) and output buffer (1 second) minimize latency
- Direct circular buffer reads ensure sample-accurate timing
- Delay applied at sample granularity for precise synchronization

## Notes

- This is a prototype. For production use, consider:
  - Additional error handling and device hotplug support
  - Visual level meters and latency monitoring
  - Persistent configuration profiles
  - Advanced filtering and equalization per output
  - ASIO support for ultra-low-latency professional use

## Build & Troubleshooting

If you encounter build errors:
- Clean and rebuild:
  ```powershell
  dotnet clean
  dotnet build
  ```
- Ensure .NET 10 SDK is installed and up to date:
  ```powershell
  dotnet --list-sdks
  ```

## License

Prototype — use for testing and development.
