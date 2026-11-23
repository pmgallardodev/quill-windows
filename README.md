# quill

<p align="center">
  A local two-track meeting recorder and transcriber for Windows 11.
</p>

<p align="center">
  <img src="Windows/Quill.Windows/Assets/quill-256.png" alt="Quill icon" width="112" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-11-0078D4?logo=windows&logoColor=white&style=flat-square" alt="Windows 11" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white&style=flat-square" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Whisper-large--v3--turbo-412991?style=flat-square" alt="Whisper large-v3-turbo" />
  <img src="https://img.shields.io/badge/Audio-WASAPI-0078D4?style=flat-square" alt="WASAPI" />
  <img src="https://img.shields.io/badge/Transcription-local-0F766E?style=flat-square" alt="Local transcription" />
  <img src="https://img.shields.io/github/downloads/pmgallardodev/quill-windows/total?style=flat-square" alt="GitHub downloads" />
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
- [Sessions window](#sessions-window)
- [While recording](#while-recording)
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
| Voice-quality recording | Implemented | 16 kHz mono WAV, about 11x smaller |
| Pause, markers, device warning | Implemented | Tray menu and default-device watch |
| Titles, delete, export, search | Implemented | Sessions window |
| Participation split | Implemented | Computed from `transcript.json` |
| Meeting detection | Implemented | WASAPI capture sessions |
| Global hotkeys | Implemented | `Ctrl+Shift+R` and `Ctrl+Shift+M` |
| Sessions window | Implemented | WPF window with live per-track progress |
| Transcription toggle | Implemented | `transcription.enabled`; `doctor` reports `desactivada` when off |
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
- Portable output: WAV, JSON, Markdown, SRT, and VTT files.
- Recordings are stored at the resolution Whisper uses, not the device's.
- Pause without ending the session, and mark moments while they happen.
- Rename, read, search, export, and delete sessions from one window.
- Inline transcript reader with speaker labels, recording markers, participation share,
  and warnings when one track contains no speech.
- Shows how much each side spoke, and warns when a track captured nothing.
- Notices when a meeting app opens the microphone, so a recording is not forgotten.
- Self-contained Windows package: end users do not install .NET or download a model.
- Optional per-user startup with no administrator privileges.

## Architecture

### Components

| Component | Location | Role |
| --- | --- | --- |
| Application and CLI | `Windows/Quill.Windows/Program.cs` | `run`, `doctor`, and start-at-login commands |
| Tray UI | `Windows/Quill.Windows/UI/` | Tray state, controls, global hotkeys, and the sessions window |
| Capture | `Windows/Quill.Windows/Audio/` | WASAPI microphone/output loopback, pause, markers, and meeting detection |
| Sessions | `Windows/Quill.Windows/Sessions/` | Metadata, status, transcripts, exports, insights, and atomic file output |
| Transcription | `Windows/Quill.Windows/Transcription/` | Serial queue, 16 kHz conversion, and Whisper |
| Tests | `Windows/Quill.Windows.Tests/` | State recovery, transcription options, exports, insights, and audio normalization |
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
| optional silence skip  |
| large-v3-turbo (es)    |
+-----------+------------+
            |
            +--> status.json ------> sessions window
            |    queued/running/         (live progress)
            |    done/failed
            v
 transcript.json + transcript.md + transcribe.log
```

System audio uses global loopback. It captures everything Windows plays through the selected output device, including calls, alerts, and music.

## Requirements

### To run the portable ZIP

- Windows 11 x64.
- A CPU with AVX, AVX2, FMA, and F16C support.
- Enough free storage for the package and recordings. The bundled model is about 547 MB, and an hour of meeting is about 230 MB of audio.
- A default microphone and default audio-output device.
- Microsoft Visual C++ 2022 Redistributable when it is not already installed.

The published ZIP is self-contained: it does not require the .NET SDK or runtime and never downloads a model on first launch.

### To develop or publish

- Windows 11 x64.
- .NET SDK 10 (10.0.404 or a later 10.0 feature band, pinned by `global.json`).
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

## Sessions window

Double-click the tray icon, or select `Ver sesiones` from its menu, to see every recording and what Quill is doing with it:

```text
+-- Sesiones de quill ---------------------+
|                                          |
|  2026.08.03-1420        * GRABANDO       |
|                                          |
|  2026.08.03-1105        > transcribiendo |
|    mic     ################--------  78% |
|    system  ######----------------    31% |
|    ~2:14 restantes                       |
|                                          |
|  2026.08.02-0930        . en cola        |
|                                          |
|  2026.08.01-1600        + listo          |
|    transcrito en 4:05 - large-v3-turbo   |
|              [Abrir carpeta] [Abrir transcript] |
|                                          |
|  2026.07.31-1130        x error          |
|    no se encontro el modelo Whisper      |
|    [Abrir carpeta] [Reintentar] [Log]    |
+------------------------------------------+
```

| State | Meaning |
| --- | --- |
| `GRABANDO` | The recording this process has open right now. |
| `en cola` | Waiting for the serial transcription queue. |
| `transcribiendo` | Live per-track progress and an estimate that corrects itself as it goes. |
| `listo` | Transcript written, with the time it took and the model that produced it. |
| `error` | Transcription failed. The reason is kept, and `Reintentar` queues it again. |
| `transcripcion desactivada` | Transcription was disabled; the recording remains available and can be recovered after enabling it. |
| `incompleta` | Session folder with no `meta.json`: the process was killed before it could close the recording. Not queued. |

When `transcription.enabled` is `false`, completed recordings are kept with the
explicit `transcripcion desactivada` state. They can be opened or deleted, and are
eligible for recovery if transcription is enabled again later.

Each session can be renamed, so the list shows `Reunion con Contoso` instead of `2026.08.03-1420`. The right-hand pane reads the transcript inline, with `me` and `them` in different colours and any markers shown in place. Its activity bar shows where each side spoke, including overlaps and silence. The box at the top searches across every session at once, ignoring case and accents.

Above the transcript, a bar shows how the talking was split between `me` and `them`, measured over speech rather than over the session length, since the silences belong to neither. If one track produced no speech at all — a muted microphone, or loopback on the wrong device — it says so there, which is the only place that failure ever becomes visible.

Buttons per session: `Renombrar`, `Abrir carpeta`, `Copiar` (to the clipboard), `SRT` and `VTT` (write `transcript.srt` / `transcript.vtt` next to the recording), `Reintentar` and `Log` for failures, and `Borrar`, which asks for confirmation and is refused while a session is recording, queued, or transcribing.

Closing the window hides it; the tray icon keeps running. Progress updates come from the running transcription, so the window costs nothing while it is closed.

**Note for existing users:** double-clicking the tray icon used to start and stop the recording. It now opens this window instead, so an accidental double-click cannot cut a meeting short. Recording is still on the right-click menu.

A session marked `error` is not retried automatically on the next launch. Earlier versions re-queued every session without a `transcript.json`, so a permanently broken one was retried on every start with no way to see it.

## While recording

| Tray menu item | What it does |
| --- | --- |
| `Iniciar grabacion` / `Detener grabacion` | Starts and ends the session. |
| `Pausar grabacion` / `Reanudar grabacion` | Stops writing audio without closing the session. Both tracks pause together, so they stay aligned, and paused time is excluded from the duration. |
| `Marcar momento` | Records the current timestamp. Markers appear in place in the reader. |

The tray icon shows a red dot while recording, so an active session is visible without hovering over it.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+Shift+R` | Start or stop recording. |
| `Ctrl+Shift+M` | Mark the current moment. |

They work without Quill having focus, which is the point: during a call you cannot go looking for the tray icon. If another application already owns a combination, registration fails and that shortcut simply does not exist — the tray menu still does the same thing. Set `"global_hotkeys": false` to skip them entirely.

### Meeting detection

The most expensive failure of any recorder is forgetting to press record. Quill checks every five seconds whether a known meeting application holds the default microphone, using the WASAPI capture sessions — it does not inspect windows or titles, only which process has the microphone open.

| `recording.meeting_detection` | Behaviour |
| --- | --- |
| `ask` (default) | Shows a balloon once per call suggesting `Ctrl+Shift+R`. |
| `auto` | Starts recording by itself. |
| `off` | No detection at all. |

Detected applications: Teams, Zoom, Webex, Slack, Discord, and calls in Chrome, Edge or Firefox. It never fires while a recording is already running, and it offers each application only once per call.

If the default microphone or output device changes mid-meeting — plugging in headphones, for instance — Quill warns once with a balloon. WASAPI keeps capturing from the device it started with, so `system.wav` can go silent with nothing else to indicate it. The recording is not stopped: ending a meeting automatically would be worse than a track that may be incomplete.

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

The local language model used for summaries (LLamaSharp / llama.cpp) follows the same rule: it loads the Vulkan build when it can and falls back to the CPU build otherwise. `ai.gpu` in `config.json` accepts `auto` (default), `vulkan` or `cpu`; `ai.gpu_device` picks the Vulkan device index, and `ai.llm_model` points at a GGUF file that replaces the AI pack's model.

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
| `transcript.srt` | Subtitles, written on demand from the window. |
| `transcript.vtt` | WebVTT subtitles, written on demand from the window. |
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
  "recording": {
    "quality": "voice",
    "delete_audio_after_transcription": false,
    "meeting_detection": "ask",
    "global_hotkeys": true
  },
  "transcription": {
    "enabled": true,
    "engine": "whisper",
    "language": "es",
    "model": "ggml-large-v3-turbo-q5_0.bin",
    "threads": null,
    "vad": false,
    "prompt": null
  }
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `recording.quality` | `voice` | `voice` writes 16 kHz mono 16-bit. `native` keeps the device format, about 11x larger. |
| `recording.delete_audio_after_transcription` | `false` | Removes the WAV tracks once a transcript exists. Never runs on a failed or empty transcription. |
| `recording.meeting_detection` | `ask` | `ask`, `auto` or `off`. What to do when a meeting application opens the microphone. |
| `recording.global_hotkeys` | `true` | Registers `Ctrl+Shift+R` and `Ctrl+Shift+M` system-wide. |
| `transcription.enabled` | `true` | Enables the transcription queue and recovery. When `false`, sessions are marked `disabled` and can be retried after re-enabling it. |
| `transcription.model` | `ggml-large-v3-turbo-q5_0.bin` | File name inside `models\`, or an absolute path. |
| `transcription.threads` | half the logical cores | Inference threads. whisper.cpp otherwise caps at four. |
| `transcription.vad` | `false` | Skip silence before transcribing. Needs `transcription.vad_model` present. |
| `transcription.vad_model` | `ggml-silero-v5.1.2.bin` | Silero model, bundled by `Publish-Portable.ps1 -IncludeVad`. |
| `transcription.prompt` | `null` | Optional vocabulary and names passed to Whisper as an initial prompt. |

### Recording size

WASAPI hands over audio in the device's own format, normally 48 kHz stereo float32. Two tracks of that come to roughly **2.6 GB per hour of meeting**, and Whisper only ever reads 16 kHz mono. Quill therefore converts while recording:

| `recording.quality` | One hour of meeting |
| --- | --- |
| `voice` (default) | about 230 MB |
| `native` | about 2.6 GB |

`voice` loses nothing that transcription uses, and speech stays perfectly intelligible to listen back to. Choose `native` only if the recordings are meant for something other than a transcript.

To reclaim the audio once the transcript exists:

```json
{ "recording": { "delete_audio_after_transcription": true } }
```

The tracks are removed only after `transcript.json` is written and only when it has segments. A failed or empty transcription keeps its audio, because at that point the audio is all that is left. Note that a session whose audio was deleted can no longer be retried.

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
|   |   |-- MeetingDetector.cs
|   |   |-- MonoDownmixSampleProvider.cs
|   |   `-- RecordingSession.cs
|   |-- Infrastructure/
|   |   |-- AutoStart.cs
|   |   |-- ConfigStore.cs
|   |   `-- Doctor.cs
|   |-- Sessions/
|   |   |-- SessionCatalog.cs
|   |   |-- SessionDocuments.cs
|   |   |-- SessionInsights.cs
|   |   `-- SessionStatus.cs
|   |-- Transcription/
|   |   |-- ResumePolicy.cs
|   |   |-- TranscriptionCoordinator.cs
|   |   |-- TranscriptionOptions.cs
|   |   |-- TranscriptionUpdate.cs
|   |   `-- WhisperTranscriber.cs
|   |-- UI/
|   |   |-- GlobalHotkeys.cs
|   |   |-- InverseBooleanToVisibilityConverter.cs
|   |   |-- SessionRowViewModel.cs
|   |   |-- SessionsViewModel.cs
|   |   |-- SessionsWindow.xaml
|   |   |-- SessionsWindow.xaml.cs
|   |   `-- TrayApplication.cs
|   |-- GlobalUsings.cs
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
- The configured model when transcription is enabled, or `desactivada` when it is not.

Required manual smoke test before distribution:

1. Start a recording and confirm the tray says `GRABANDO`.
2. Play Windows audio and speak into the microphone.
3. Stop the recording and verify `mic.wav`, `system.wav`, and `meta.json`.
4. Wait for Whisper and inspect `transcript.json` and `transcript.md`.
5. Close Quill during transcription and confirm that it resumes after reopening.
6. Double-click the tray icon and confirm the sessions window lists the session with live progress.
7. Pause and resume, and confirm the tray reads `PAUSADO` and the timer stops.
8. Mark a moment and confirm it appears in the reader at the right position.
9. Rename a session and confirm the new name survives restarting Quill.
10. Confirm the participation insight shows the `me`/`them` split and warns about a silent track when applicable.
11. Export SRT and VTT and confirm the timestamps line up when played against the audio.
12. Press `Ctrl+Shift+R` and `Ctrl+Shift+M` while another application has focus and confirm both shortcuts work.
13. Set `recording.meeting_detection` to `ask`, start a supported meeting application, and confirm the reminder appears without starting a recording.
14. Confirm a failed session shows `error` after a restart and can be retried from the window.

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
