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
  <img src="https://img.shields.io/badge/Whisper-large--v3--turbo-412991?style=flat-square" alt="Whisper large-v3-turbo" />
  <img src="https://img.shields.io/badge/Audio-WASAPI-0078D4?style=flat-square" alt="WASAPI" />
  <img src="https://img.shields.io/badge/Transcription-local-0F766E?style=flat-square" alt="Local transcription" />
</p>

Quill captures microphone and Windows output as independent tracks. When a session stops, it runs Spanish Whisper `large-v3-turbo` locally and produces a timestamped transcript with deterministic speaker labels: `me` for the microphone and `them` for system audio.

```text
Microphone --------+--> mic.wav ----+                         +--> transcript.json
                   |                |                         |
Windows audio -----+--> system.wav -+--> large-v3-turbo ------+--> transcript.md
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
| Local Spanish transcription | Implemented | Whisper.net / whisper.cpp + `large-v3-turbo` |
| Model selection | Implemented | `transcription.model` in `config.json` |
| GPU acceleration | Implemented | `Whisper.net.Runtime.Vulkan`, falls back to CPU |
| Silence skipping | Optional | Silero VAD, off by default |
| Pending-job recovery | Implemented | Scan sessions without `transcript.json` |
| Sessions window | Implemented | WPF window with live per-track progress |
| Start at login | Implemented | `HKCU\...\Run` value |
| Portable package | Implemented | Self-contained `win-x64` ZIP |
| Windows 11 hardware smoke test | Pending | Requires a Windows device with audio hardware |

## Features

- Separate microphone and Windows playback capture.
- Fully local Spanish transcription using Whisper `large-v3-turbo` Q5_0.
- Any whisper.cpp model can be selected, including one outside the package.
- Uses the GPU through Vulkan when one is present, and CPU when it is not.
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
| Model | `models/` | `large-v3-turbo` Q5_0 included at publish time |
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
| large-v3-turbo (es)    |
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
- Enough free storage for the package and recordings. The bundled model is about 547 MB.
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
5. Double-click the tray icon to open the sessions window and watch transcription progress.

The `large-v3-turbo` model is already in `models\` inside the ZIP. Quill does not download it during normal use.

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

In that case, `models\ggml-large-v3-turbo-q5_0.bin` must already exist.

Bundle the Silero VAD model too, so `"vad": true` can be enabled later:

```powershell
.\tools\Publish-Portable.ps1 -IncludeVad
```

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

The portable distribution uses `ggml-large-v3-turbo-q5_0.bin`:

| Property | Value |
| --- | --- |
| Engine | whisper.cpp through Whisper.net |
| Model | Whisper large-v3-turbo |
| Quantization | Q5_0 |
| File | `ggml-large-v3-turbo-q5_0.bin`, about 547 MB |
| Inference language | `es` |
| Network during transcription | None |

`large-v3-turbo` prunes the decoder from 32 layers to 4. It is several times faster than `large-v3` at a comparable accuracy for Spanish, and takes up half the disk space. Q5_0 reduces the size further without turning it into a small model.

Quill processes one session at a time so a new recording is not competing for the same resources.

### Using a different model

Any whisper.cpp `.bin` works. A bare file name is looked up in `models\` next to the executable; an absolute path is used as-is, so a model can be tried without copying it into the package:

```json
{
  "transcription": {
    "model": "D:\\models\\ggml-large-v3-q5_0.bin"
  }
}
```

Download another one into `models\` with:

```powershell
.\tools\Get-WhisperModel.ps1 -Model ggml-large-v3-q5_0.bin
```

The sessions window records which model produced each transcript and how long it took, so two models can be compared on the same recording.

### GPU acceleration

The package includes both the CPU and the Vulkan runtimes. Whisper.net probes them in order and falls back to the next one when a driver is missing, so a machine without a usable GPU keeps working on CPU with no configuration. `transcribe.log` records the thread count used for each track.

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
| `status.json` | Transcription state, model used, and elapsed time. |
| `transcript.json` | Canonical transcript with timed segments. |
| `transcript.md` | Human-readable Markdown transcript. |
| `transcribe.log` | Transcription progress and errors. |

The tracks are transcribed independently. Their offsets are applied before they are merged by timestamp, which makes `me` and `them` labels possible without sending audio to a diarization service.

If Windows or Quill closes while transcription is running, the session retains `meta.json` but has no `transcript.json`; Quill queues it when it next starts. Transcripts are written atomically, so their presence means the output was completed.

`status.json` records the outcome so it survives a restart. A session marked `failed` is not queued again automatically: open the sessions window and select `Reintentar`. The file is a convenience, not the source of truth — deleting it makes Quill derive the state from which files exist, exactly as it did before.

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
    "language": "es",
    "model": "ggml-large-v3-turbo-q5_0.bin",
    "threads": null,
    "vad": false
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `model` | `ggml-large-v3-turbo-q5_0.bin` | File name inside `models\`, or an absolute path. |
| `threads` | half the logical cores | Inference threads. whisper.cpp otherwise caps at four. |
| `vad` | `false` | Skip silence before transcribing. Needs `vad_model` present. |
| `vad_model` | `ggml-silero-v5.1.2.bin` | Silero model, bundled by `Publish-Portable.ps1 -IncludeVad`. |

### Skipping silence

Because Quill records two tracks, each one is mostly silence: `mic.wav` is quiet while the other side talks, and `system.wav` is quiet while you do. A one-hour meeting therefore produces two hours of audio to transcribe, roughly half of which is silence that Whisper processes anyway.

Setting `"vad": true` runs Silero voice activity detection first and transcribes only the speech ranges, remapping each timestamp back to the original timeline so the `me` and `them` labels stay aligned.

It is off by default because it changes how the audio is chunked, which can change where segments break. Compare a transcript both ways on a recording you know before relying on it.

The sessions directory resolves in this order:

```text
--out > recordings_dir in config.json > %USERPROFILE%\Recordings
```

The `language` field is passed straight to Whisper, so a value other than `es` will work, but the `me` and `them` labelling and the bundled model were chosen with Spanish meetings in mind.

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
|   |   |-- SessionCatalog.cs
|   |   |-- SessionDocuments.cs
|   |   `-- SessionStatus.cs
|   |-- Transcription/
|   |   |-- ResumePolicy.cs
|   |   |-- TranscriptionCoordinator.cs
|   |   |-- TranscriptionOptions.cs
|   |   |-- TranscriptionUpdate.cs
|   |   `-- WhisperTranscriber.cs
|   |-- UI/
|   |   |-- SessionRowViewModel.cs
|   |   |-- SessionsViewModel.cs
|   |   |-- SessionsWindow.xaml
|   |   `-- TrayApplication.cs
|   |-- Program.cs
|   `-- Quill.Windows.csproj
|-- Windows/Quill.Windows.Tests/
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
dotnet test .\Windows\Quill.Windows.Tests\Quill.Windows.Tests.csproj
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
- The configured model.

Required manual smoke test before distribution:

1. Start a recording and confirm the tray says `GRABANDO`.
2. Play Windows audio and speak into the microphone.
3. Stop the recording and verify `mic.wav`, `system.wav`, and `meta.json`.
4. Wait for Whisper and inspect `transcript.json` and `transcript.md`.
5. Close Quill during transcription and confirm that it resumes after reopening.
6. Double-click the tray icon and confirm the sessions window lists the session with live progress.
7. Confirm a failed session shows `error` after a restart and can be retried from the window.

## Troubleshooting

### `doctor` cannot find the model

By default the application requires this path relative to the executable:

```text
models\ggml-large-v3-turbo-q5_0.bin
```

Extract the full ZIP again. Do not run a copied `quill.exe` without its `models` folder. If `config.json` sets `transcription.model`, `doctor` reports the path it actually resolved.

### `system.wav` has no audio

WASAPI loopback records the default output device. Confirm that the call or application plays through that device in Windows Sound settings, then start a new recording.

### `mic.wav` has no audio

Check the default input device and microphone permissions in Windows Settings. Close applications using the microphone in exclusive mode before starting Quill.

### Whisper is too slow

Run `quill.exe doctor` first: it reports the model in use and how many threads will run. Then, in rough order of impact:

- Confirm the bundled `large-v3-turbo` is in use and not a `large-v3` set in `config.json`.
- Set `"vad": true` to skip the silence in each track.
- Raise `"threads"` if the machine has cores to spare.
- Check `transcribe.log`: Whisper.net falls back to CPU when no Vulkan driver is present, and updating the graphics driver can enable the GPU path.

Quill still processes one session at a time, so a queued recording waits for the current one.

### The portable folder was moved

Quill can run from its new location, but the start-at-login entry keeps the old path. Run this from the new folder:

```powershell
.\quill.exe install --launch-at-login
```

## Third-party notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the licenses of the runtime and model dependencies included by a portable build.
