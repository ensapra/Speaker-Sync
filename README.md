# Speaker Sync

Speaker Sync is a small Windows desktop utility made to solve a very specific problem: when a PC and its Bluetooth speakers fall out of sync, the audio arrives late and the sound feels off. This app lets you route one input stream to multiple outputs and compensate for delay on each speaker independently.

It was built to help with that frustrating "Bluetooth speakers are slightly behind the PC" issue, and it ended up becoming a practical tool for pairing multiple outputs with manual timing correction.

This project is fully vibecoded: quick, personal, practical, and built to solve a real-world annoyance without overengineering it.

## What it does

- Captures audio from a selected input device
- Sends that audio to multiple selected output devices
- Lets you set a custom delay for each output in milliseconds
- Lets you adjust the volume of each output independently
- Works as a simple sync tool for devices that drift out of alignment
- Saves your configuration automatically so it can be restored when you reopen the app

## Why this exists

I just had this problem where my Bluetooth speakers and the PC were out of sync. The sound was slightly delayed, and it felt inconsistent enough to be annoying. I wanted a quick way to fix the mismatch without buying more gear or dealing with fiddly audio settings, so I built this.

## Free to use

This project is released under the MIT License and is free for anyone to use, modify, and share.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Features

- Route audio from one input to multiple outputs
- Independent delay per output device
- Independent volume per output device
- Built for Windows with WPF and NAudio
- Supports multiple sample rates: 44.1 kHz, 48 kHz, and 96 kHz
- Automatically saves your output settings and restores them on launch

## Prerequisites

- Windows 10 or newer
- .NET 10 SDK
- A virtual audio source such as VB-CABLE or similar if you want to route desktop audio into the app

## Install .NET 10

```powershell
winget install Microsoft.DotNet.SDK.10
```

Check the installation:

```powershell
dotnet --version
```

## Build

From the repo root:

```powershell
cd "c:\Users\pauso\Documents\Speaker Sync"
dotnet build
```

## Run

```powershell
cd "c:\Users\pauso\Documents\Speaker Sync"
dotnet run
```

You can also use the included helper script if you prefer:

```powershell
.\run.ps1
```

## How to use it

1. Select your input device.
2. Select your output devices.
3. Choose a sample rate.
4. Adjust each output's volume and delay.
5. Click Start to begin routing audio.
6. Use Auto Sync to calibrate the per-device delays.
7. Keep the app running while you listen and tweak the delay values as needed.

## Notes

This is a practical, personal utility rather than a polished commercial product. It is intentionally simple, direct, and focused on solving the real problem it was made for.

If you want to improve it, contribute, or adapt it to your own setup, the project is fully open and free to use.

## Repository status

This repository is ready to be shared publicly on GitHub.
