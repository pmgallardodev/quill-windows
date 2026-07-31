# quill

<p align="center">
  A local two-track meeting recorder and transcriber for Windows 11.
</p>

<p align="center">
  <img src="Windows/Quill.Windows/Assets/quill-256.png" alt="Quill icon" width="112" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11-0078D4?logo=windows&logoColor=white&style=flat-square" alt="Windows 11" />
  <img src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white&style=flat-square" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Whisper-large--v3-412991?style=flat-square" alt="Whisper large-v3" />
  <img src="https://img.shields.io/badge/Audio-WASAPI-0078D4?style=flat-square" alt="WASAPI" />
  <img src="https://img.shields.io/badge/Transcription-local-0F766E?style=flat-square" alt="Local transcription" />
</p>

Quill captures microphone and Windows output as independent tracks. When a session stops, it runs Spanish Whisper `large-v3` locally and produces a timestamped transcript with deterministic speaker labels: `me` for the microphone and `them` for system audio.

```text
Microphone --------+--> mic.wav ----+                         +--> transcript.json
                   |                |                         |
Windows audio -----+--> system.wav -+--> Whisper large-v3 ---+--> transcript.md
```

> Quill is a local recording tool. It visibly indicates when a recording is active. Use it only where recording is lawful and all required notice or consent has been given.

## Table of Contents

- [Status](#status)
- [Features](#features)
- [Architecture](#architecture)
- [Requirements](#requirements)
- [Quickstart](#quickstart)
- [Commands](#commands)
- [Bundled Whisper model](#bundled-whisper-model)
- [Local data](#local-data)
- [Configuration](#configuration)
- [Project structure](#project-structure)
- [Privacy](#privacy)
- [Verification](#verification)
- [Troubleshooting](#troubleshooting)
- [Third-party notices](#third-party-notices)

## Status

The Windows application lives in `Windows/Quill.Windows`.

| Area | Status | Implementation |
| --- | --- | --- |
| System tray | Implemented | WPF + `NotifyIcon` |
| Microphone | Implemented | WASAPI capture |
| System audio | Implemented | WASAPI loopback |
| Two tracks and metadata | Implemented | WAV + `meta.json` |
| Local Spanish transcription | Implemented | Whisper.net / whisper.cpp + `large-v3` |
| Pending-job recovery | Implemented | Scan sessions without `transcript.json` |
| Start at login | Implemented | `HKCU\...\Run` value |
| Portable package | Implemented | Self-contained `win-x64` ZIP |
| Windows 11 hardware smoke test | Pending | Requires a Windows device with audio hardware |

## Features

- Separate microphone and Windows playback capture.
- Fully local Spanish transcription using Whisper `large-v3` Q5_0.
- No accounts, telemetry, API keys, or audio upload to a service.
- System-tray start/stop control, elapsed timer, and transcription status.
- Recoverable sessions: completed sessions without a transcript are queued on the next launch.
- Portable output: WAV, JSON, and Markdown files.
- Self-contained Windows package: end users do not install .NET or download a model.
- Optional per-user startup with no administrator privileges.

## Architecture

### Components

| Component | Location | Role |
| --- | --- | --- |
| Application and CLI | `Windows/Quill.Windows/Program.cs` | `run`, `doctor`, and start-at-login commands |
| Tray UI | `Windows/Quill.Windows/UI/` | Visible state, recording controls, and notifications |
| Capture | `Windows/Quill.Windows/Audio/` | WASAPI microphone and output loopback capture |
| Sessions | `Windows/Quill.Windows/Sessions/` | Metadata, transcripts, and atomic file output |
| Transcription | `Windows/Quill.Windows/Transcription/` | Serial queue, 16 kHz conversion, and Whisper |
| Model | `models/` | `large-v3` Q5_0 included at publish time |
| Packaging | `tools/` | Controlled model download and portable ZIP build |

### Session flow

```text
+------------------------+
| Quill system tray icon |
| Start recording        |
+-----------+------------+
            |
            v
+-----------+------------+
| WASAPI                 |
| mic capture + loopback |
+-----------+------------+
            |
            v
+-----------+------------+
| %USERPROFILE%\Recordings |
| <timestamp>/mic.wav    |
| <timestamp>/system.wav |
| <timestamp>/meta.json  |
+-----------+------------+
            |
            v
+-----------+------------+
| Local serial queue     |
| normalize to 16 kHz    |
| Whisper large-v3 (es)  |
+-----------+------------+
            |
            v
 transcript.json + transcript.md + transcribe.log
```

System audio uses global loopback. It captures everything Windows plays through the selected output device, including calls, alerts, and music.

## Requirements

### To run the portable ZIP

- Windows 11 x64.
- A CPU with AVX, AVX2, FMA, and F16C support.
- Enough free storage for the package and recordings. The bundled model exceeds 900 MB.
- A default microphone and default audio-output device.
- Microsoft Visual C++ 2022 Redistributable when it is not already installed.

The published ZIP is self-contained: it does not require the .NET SDK or runtime and never downloads a model on first launch.

### To develop or publish

- Windows 11 x64.
- .NET SDK 8.
- PowerShell.
- Internet access only for the initial release build to retrieve the model from the configured source.

## Quickstart

### Use a portable build

1. Extract `quill-win-x64.zip` to a folder you do not intend to move.
2. Run `quill.exe`.
3. Right-click the Quill tray icon and select `Iniciar grabacion`.
4. Select `Detener grabacion` when the meeting ends.
5. Open `Recordings` from the tray menu after the transcript notification appears.

The `large-v3` model is already in `models\` inside the ZIP. Quill does not download it during normal use.

### Build the portable ZIP

```powershell
git clone https://github.com/pmgallardodev/quill-windows.git
cd quill-windows
.\tools\Publish-Portable.ps1
```

The script downloads the model only when it is absent, publishes `quill.exe` for `win-x64`, and includes the model plus native runtime in:

```text
artifacts\quill-win-x64.zip
```

Build without allowing the script to download the model:

```powershell
.\tools\Publish-Portable.ps1 -SkipModelDownload
```

In that case, `models\ggml-large-v3-q5_0.bin` must already exist.

## Commands

| Command | Description |
| --- | --- |
| `quill.exe` | Start the system-tray application. |
| `quill.exe run --out D:\Recordings` | Use a different sessions directory. |
| `quill.exe doctor` | Check Windows 11, devices, output folder, and model. |
| `quill.exe install --launch-at-login` | Start Quill when the current user signs in. |
| `quill.exe install --uninstall` | Remove Quill's start-at-login entry. |

Start at login writes only this current-user value:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Quill
```

It does not require elevation. If you move the portable folder after enabling start at login, run the command again from its new location.

## Bundled Whisper model

The portable distribution uses `ggml-large-v3-q5_0.bin`:

| Property | Value |
| --- | --- |
| Engine | whisper.cpp through Whisper.net |
| Model | Whisper large-v3 |
| Quantization | Q5_0 |
| Inference language | `es` |
| Network during transcription | None |
| Minimum validated size | 900 MB |

Q5_0 reduces the full model size without turning it into a small model. CPU transcription time depends on audio duration and hardware. Quill processes one session at a time so a new recording is not competing for the same resources.

## Local data

By default, each session is saved at:

```text
%USERPROFILE%\Recordings\yyyy.MM.dd-HHmm\
```

| File | Contents |
| --- | --- |
| `mic.wav` | Microphone audio. |
| `system.wav` | Windows output audio. |
| `meta.json` | Start, end, duration, files, and track offsets. |
| `transcript.json` | Canonical transcript with timed segments. |
| `transcript.md` | Human-readable Markdown transcript. |
| `transcribe.log` | Transcription progress and errors. |

The tracks are transcribed independently. Their offsets are applied before they are merged by timestamp, which makes `me` and `them` labels possible without sending audio to a diarization service.

If Windows or Quill closes while transcription is running, the session retains `meta.json` but has no `transcript.json`; Quill queues it when it next starts. Transcripts are written atomically, so their presence means the output was completed.

## Configuration

Optional configuration lives at:

```text
%LOCALAPPDATA%\quill\config.json
```

Example:

```json
{
  "recordings_dir": "D:\\Recordings",
  "transcription": {
    "enabled": true,
    "engine": "whisper",
    "language": "es"
  }
}
```

The sessions directory resolves in this order:

```text
--out > recordings_dir in config.json > %USERPROFILE%\Recordings
```

Whisper is intentionally fixed to Spanish in the current implementation. The `language` field documents the configuration intent and is reserved for future explicit language selection.

## Project structure

```text
quill-windows/
|-- Windows/Quill.Windows/
|   |-- Assets/
|   |   |-- quill.ico
|   |   `-- quill-icon.svg
|   |-- Audio/
|   |   `-- RecordingSession.cs
|   |-- Infrastructure/
|   |   |-- AutoStart.cs
|   |   |-- ConfigStore.cs
|   |   `-- Doctor.cs
|   |-- Sessions/
|   |   `-- SessionDocuments.cs
|   |-- Transcription/
|   |   |-- TranscriptionCoordinator.cs
|   |   `-- WhisperTranscriber.cs
|   |-- UI/
|   |   `-- TrayApplication.cs
|   |-- Program.cs
|   `-- Quill.Windows.csproj
|-- models/
|   `-- MANIFEST.md
|-- tools/
|   |-- Get-WhisperModel.ps1
|   `-- Publish-Portable.ps1
`-- README.md
```

## Privacy

Quill for Windows is designed to remain local:

- Audio is written only to the selected session folder.
- Whisper loads the bundled model from disk and processes audio on the device.
- There are no accounts, API keys, telemetry, sync, or network requests while recording or transcribing.
- The model is downloaded only while preparing a development release, never on first use of a published ZIP.
- Start at login is optional, visible, and limited to the current user's `HKCU` key.

The model and recordings may contain sensitive information. Protect the extracted package and sessions folder with Windows access controls.

## Verification

Before creating a release, on Windows 11:

```powershell
dotnet build .\Windows\Quill.Windows\Quill.Windows.csproj -c Release -r win-x64
.\tools\Publish-Portable.ps1
```

On the target machine:

```powershell
.\quill.exe doctor
```

The output must confirm:

- Windows 11.
- A default microphone.
- A default output device for loopback.
- Access to the sessions folder.
- The bundled `large-v3` model.

Required manual smoke test before distribution:

1. Start a recording and confirm the tray says `GRABANDO`.
2. Play Windows audio and speak into the microphone.
3. Stop the recording and verify `mic.wav`, `system.wav`, and `meta.json`.
4. Wait for Whisper and inspect `transcript.json` and `transcript.md`.
5. Close Quill during transcription and confirm that it resumes after reopening.

## Troubleshooting

### `doctor` cannot find the model

The application requires this path relative to the executable:

```text
models\ggml-large-v3-q5_0.bin
```

Extract the full ZIP again. Do not run a copied `quill.exe` without its `models` folder.

### `system.wav` has no audio

WASAPI loopback records the default output device. Confirm that the call or application plays through that device in Windows Sound settings, then start a new recording.

### `mic.wav` has no audio

Check the default input device and microphone permissions in Windows Settings. Close applications using the microphone in exclusive mode before starting Quill.

### Whisper is too slow

`large-v3` prioritizes accuracy and uses CPU inference by default. Close resource-intensive applications and let Quill drain the queue. The first release does not use GPU acceleration so the portable package remains predictable.

### The portable folder was moved

Quill can run from its new location, but the start-at-login entry keeps the old path. Run this from the new folder:

```powershell
.\quill.exe install --launch-at-login
```

## Third-party notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the licenses of the runtime and model dependencies included by a portable build.
