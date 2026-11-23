# Feature: quill-next

Locator: `odd/tasks/quill-next.md` · Engram mirror: `odd/quill-next/tasks`
Design: `docs/superpowers/specs/2026-11-23-quill-next-design.md` (local, gitignored)
Branch: `feat/quill-next` (from `main` @ bf72ca7)

## Objective
Take Quill to the next level: modern UI, bundled local LLM producing structured summaries live and
after the meeting, smart speaker separation, and the full feature scope agreed on 2026-11-23.

## Problem / why
UI is poor at every level; transcription only runs after stop, so no live value; `me`/`them` is a
fixed track rule that breaks with speaker echo and cannot tell remote people apart. Research shows
users value summaries + action items, bot-free capture, notes that steer the summary, and suffer
most from "who said what".

## Constraints
- .NET 8, WPF + WPF-UI, NAudio, Whisper.net 1.9.1, LLamaSharp (Vulkan), sherpa-onnx.
- Everything local. No accounts, no calendar, no external services, no runtime downloads.
- Distribution: app ZIP + separate AI-pack ZIP, each < 2 GiB.
- Dev on macOS: official .NET 8 SDK at `~/.dotnet`. Build: `dotnet build <proj> -r win-x64`.
  `Quill.Core.Tests` (net8.0) runs on macOS; WPF tests only on Windows.
- UI copy in Spanish via resources; code/comments/identifiers in English.
- Speed is the owner's premise: maximum parallelism with isolated worktrees, disjoint ownership.

## Delivery strategy
`single-pr` on `feat/quill-next`; `main` only after owner Windows smoke test. Push/PR/merge are
owner decisions. Review: RDD off (global) → ordinary checks only.

## Tasks
Route legend: D = delegated writer in its own worktree (parallel). Parent integrates merges.

### Wave 0 — foundations (parallel)
- [x] F1 Quill.Core (net8.0) + Quill.Core.Tests: domain, ports, MeetingSummary schema + citation
      validator, session format v2 (additive), solution file. Route D.
      Evidence: ddd7197, 85759b7, b9f280d, a22bd7e, merge; parent added IInferenceScheduler port
      61028a4. Core tests 127/127 green on macOS; win-x64 build 0 errors. API: Quill.Core/README.md.
      Notes: legacy Sessions code lives in Quill.Core/Legacy (old namespaces); ai-pack.json manifest
      layout proposed by F1 must be adopted by P1.
- [ ] F0 Spike + benchmark harness: LLamaSharp/Vulkan, sherpa-onnx .NET, Whisper small live
      latency; 5 synthetic Spanish transcripts with answer keys; scoring tool. Route D.
      Partial: 31a6903 corpus, c511963 harness, merge 2081eaf. Facts: LLamaSharp 0.30.0 (net10 +
      netstandard2.0 only; 0.27.0 last net8 build), GBNF only; sherpa-onnx works (8/8 turns, ~9-10
      min CPU per audio hour on M1); Whisper pads to 30 s → longer chunks. Qwen3-4B on 1:1: 100%
      recall, 0 invented, 1 wrong owner. Remaining: full benchmark + RESULTS.md → run on owner's
      Windows PC (Mac has ~3 GB free disk, 16 GB RAM: runs fill swap/disk).
      Owner decision pending: .NET 8 support ended 2026-11-10 → migrate to net10.0?
- [x] F2 Design system with WPF-UI: tokens, typography, light/dark, Mica, string resources. Route D.
      Evidence: 609ddc2, 38e6185, merge 8d0a84e, alias fix 148166c (WPF-UI root namespace `Wpf`
      collided with `using Wpf = System.Windows`). Build win-x64: 0 errors / 0 warnings.
      Pending: Windows runtime check (theme switch, Mica, high contrast). API: UI/Theme/README.md.

### Wave A (parallel, after F1)
- [x] F3 Live transcription pipeline (ring buffer → VAD → Whisper small chunks). Route D.
      Evidence: 9483115, 93f2708, 23b5e5a, 38e2162, c752721, merged. Post-merge: Core 230/230
      green, win-x64 build 0/0. Chunks 6-9 s (≤10 s lag). App-wide InferenceScheduler lives in
      TrayApplication (F3 added minimal wiring there, +56 lines) — F4 module must reuse it.
      Pending: 13 Windows tests + manual live checks on owner's PC.
- [x] F4 LLM engine + summary use cases (live loop, final, catch-up, templates, level, language,
      map-reduce, citation validation). Route D.
      Evidence: d381a20, 48289c7, 9cc799d, merged. Core 170/170 green (post-merge, macOS);
      win-x64 builds 0 errors (agent). LLamaSharp pinned 0.27.0 (last net8; M1 upgrades).
      Pending: real-model grammar smoke test; Windows tests run; Vulkan/CPU fallback on hardware.
- [x] F5 Speaker separation (echo classifier, diarization on any track, rename, correction). Route D.
      Evidence: d7b3a81, 93bd447, a7cfa0c, merged. Post-merge: Core 260/260 green, win-x64 0/0.
      sherpa-onnx 2.0.3. Extra file speakers.resolution.json (color slot, embeddings, edit log).
      Pending: wiring SpeakersModule into app/UI; Windows audio checks; diarization timing.
      Follow-ups: ISessionStore.LoadTranscriptInfo port; Core README for Speakers/Live/Scheduling.
- [x] F6 Main window redesign (meetings by day, Summary/Transcript/Notes tabs, My tasks). Route D.
      Evidence: dad414e, af66741, e74fed4, b185f71, merged. Post-merge: Core 260/260, Windows
      projects build 0/0. 70 VM/helper tests run green in a macOS net8.0 harness (agent).
      Old SessionsWindow deleted. ISessionActions default = AI pack missing; real actions wired
      in the integration task. Pending: Windows runtime check (Mica, theme, virtualization).

### Migration (after Wave A merges, no agent touching csproj)
- [x] M1 Retarget all projects to net10.0 / net10.0-windows (owner decision 2026-11-23: .NET 8
      support ended 2026-11-10); install .NET 10 SDK in ~/.dotnet; upgrade LLamaSharp to latest
      (0.30.x) with explicit Vulkan/CPU backend selection; update README badges/requirements.
      Route: inline if mechanical, else D.
      Evidence: 4183700, 9f1b61e, merged clean. Post-merge: SDK 10.0.404, Core 354/354 on
      net10.0, Windows 0/0; agent: self-contained win-x64 publish OK (293 files, 397 MB).
      LLamaSharp 0.30.0; LlamaSharpOptions Gpu/GpuDevice/ModelPath (I1 maps settings).
      Adapter moved to Adapters/Llm (namespace kept). Gpu change needs app restart.

### Wave B (parallel)
- [x] F7 Companion panel (floating, pill, live tabs, What did I miss?, markers, notes, optional
      recording-notice button). Route D.
      Evidence: 86268c6, 5f514a4, merge c660536 (parent resolved Strings.resx append conflict;
      XML valid). Core 260/260, Windows projects build 0/0. Pending: wiring (I1), level meters
      need per-track peak/RMS from RecordingSession, Windows checks.

### Discreet mode (owner request 2026-11-23, default ON)
- [x] F14 Discreet mode: all Quill windows excluded from screen capture
      (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE, fallback WDA_MONITOR) via
      ThemeBootstrapper.Attach; no taskbar buttons / Alt+Tab while recording; neutral tray icon
      option (no red recording dot); all balloon/toast notifications suppressed (shown in the
      excluded panel instead); global hotkey to show/hide the companion panel; setting
      `privacy.discreet_mode` (default true). Route D (parallel with F8).
      Evidence: 8584ef0, 9f564fe, b9008c1, merged clean. Post-merge: Core 354/354, Windows 0/0.
      Cannot hide: tray icon/tooltip (neutral instead), shell balloons (suppressed), common
      dialogs/MessageBox. I1: set TrayController.CompanionPanel, show Notifier events in panel.

### Integration (after Wave B)
- [x] I1 Composition root: real ISessionActions (F4 summaries, F5 speakers, F8 playback), one
      app-wide InferenceScheduler, start LiveSummaryLoop + forward LiveTranscriptBuffer segments,
      show/close CompanionPanelModule around recording (CloseAsync before final summary),
      final pipeline after transcription (speakers → final summary → optional auto-export),
      tray menu items (Ajustes, Importar), per-track audio levels, Core README update. Route D.
      Evidence: 6e6ac21, 0953eca, 44f21a4, merged clean. Post-merge: Core 372/372 (net10),
      Windows 0/0, resx valid. Gaps: GPU/model change needs restart; Recent notifications not
      replayed; GPU probe = vulkan-1.dll present.
- [x] R1 Regenerating a summary must preserve user state (action-item done flags) by matching
      items; also avoid re-running the full pipeline on transcription retry when a summary
      exists unless the user asks. Route D.
      Evidence: 83d1988, 1ea6fb2, merged; parent wired FileSummaryStateStore into
      PostTranscriptionPipeline (QuillServices.cs:58). Core 390/390, Windows 0/0.
      Pending (polish): show "Regenerar" when summary.state.json outdated=true in SummaryViewModel.
- [x] F8 Audio playback from transcript, transcript editing + re-summarize, import. Route D.
      Evidence: 0c16ae0, a099b55, 3038aa8, 1fc82a3, e8f5443, merged clean. Post-merge: Core
      330/330, Windows projects 0/0, resx valid. Custom WSOLA time-stretch (NAudio has none).
      TranscriptionCoordinator: single-track sessions allowed (7 lines). I1: real
      ReassignSpeakerAsync via F5. Pending: listen to playback/speed + import on Windows.
- [x] F9 Templates editor, Markdown/Obsidian export, settings (models, AI pack path, GPU). Route D.
      Evidence: 9de94b4, e35cd57, 3127981, 749ffd4, 89ac41e, merged clean. Post-merge: Core
      309/309, Windows projects 0/0, resx valid. Wiring (tray item, AiPackLocator from settings,
      TemplateCatalog, live cadence/GPU gate, auto-export, model/GPU overrides) → I1.

### Wave C (parallel)
- [x] F10 Ask (per meeting + across meetings) + local semantic search. Route D.
      Evidence: 9986749, e22dc0d, c27f04d, 5abba69, merged clean. Post-merge: Core 573/573,
      Windows 0/0, resx valid. Hybrid BM25 + vector (RRF), file index `.quill-index/`.
      Embedding model: BGE-M3 GGUF Q8_0 (~0.6 GB). Wiring (AskModule, IApiSearch) → I2.
- [x] F11 Optional speaker-naming memory (local voice embeddings, "Sounds like Juan?"). Route D.
      Evidence: 676b487, e763ec3, 5db2bba (+3fe8acd shared with F13), 31682bb, merged clean.
      Post-merge: Core 614/614, Windows 0/0. voices.json at recordings root (never exported).
      Suggest ≥0.65 cos + 0.10 margin; auto-apply ≥0.80 (off by default).
- [x] F12 Local API (127.0.0.1 + token), loopback-only webhooks, MCP endpoint. Route D.
      Evidence: 8bd3df5, e912231, 094721d, fabaa71, c593bcf, 1ea0ade, merged clean. Post-merge:
      Core 538/538, Windows 0/0, resx valid. TcpListener on 127.0.0.1 (no http.sys/Kestrel),
      hand-written MCP subset, API off by default. Wiring → I2.
- [x] I2 Final wiring: LocalApiModule (start/restart on ApiSettingsStore.Saved, Publish events
      at recording stop / transcript / summary, dispose), F10 AskModule + IApiSearch for API/MCP,
      "Regenerar" banner when summary.state.json outdated, Core README rows (Api, Search, Ask).
      Route D after F10 + F11/F13 merge.
      Evidence: 71809e2, 28fc992, c7046be, cf03bbb, 855b88e, merged clean. Post-merge: Core
      632/632, Windows 0/0, resx valid. MCP targets spec 2025-11-25 (negotiates 2025-06-18,
      2025-03-26, 2024-11-05), stateless, 202 for notifications, 405 GET; handshake tests RED→GREEN.
- [x] F13 Opt-in in-person mode (diarize mic track, recognize owner). Route D.
      Evidence: 3fe8acd + 5db2bba, merged. Per-meeting switch in Interlocutores panel; owner =
      saved voice → loudest (≥25% talk time) → most talk. Owner voice learned from first 5 online
      meetings. Gap: switch not in import dialog (settable after import).
- [ ] P1 Packaging: app ZIP + AI-pack ZIP, AI-pack detection, README update. Route D.
      Done: 8cfc387, 62d0237, d722434, merged; build 0/0; native runtimes verified in publish.
      Scripts unexecuted (no pwsh on Mac). OPEN owner decision: Qwen3-4B Q4_K_M is 2.33 GiB >
      2 GiB GitHub asset cap → agent defaulted to UD-Q3_K_XL (1.98 GiB, unscored) in a separate
      `-llm.zip` (3 assets). No LICENSE in repo (owner's call).
      Owner decision 2026-11-23: option A — ship Qwen3-4B Q4_K_M split in two parts (3 assets).
- [ ] P1b Split-model support: Q4_K_M as default, split into parts < 2 GiB at build time,
      reassembled/loaded by Quill with SHA-256 verification; locator + adapter + scripts + README.
      Route D.
- [ ] P2 Hybrid AI-pack install (owner decision 2026-11-23, revises Q1): in-app "Descargar pack
      de IA" button (user-initiated only, never automatic) downloading pinned Hugging Face
      revisions with size cap + SHA-256 verification, resumable, progress, cancel, free-space
      check; manual ZIPs (P1b) keep working offline. README "never downloads" wording updated to
      "only when you press Download". Route D after P1b merges.

## Acceptance criteria
- Summary JSON has all sections; empty sections render "None"; every item's timestamp exists.
- Live panel updates during recording without affecting capture.
- `Me` never assigned to remote voices leaking through speakers (echo case).
- All projects build `-r win-x64` with 0 errors; `Quill.Core.Tests` green on macOS.
- Owner Windows smoke checklist passes before `main`.

## Checks
- macOS: `dotnet test Windows/Quill.Core.Tests` ; `dotnet build Windows/Quill.Windows -r win-x64`
- Windows (owner): record 5-min call (headphones and speakers), live panel, final summary,
  speakers, playback, export, AI pack missing/present.

## Progress
- 2026-11-23: design approved via conversation; branch created; Wave 0 dispatched.

- 2026-11-23: Wave A dispatched (F3-F6). Disk full (ENOSPC) stopped all agents mid-task; owner
  freed regenerable outputs (bin/obj, bench models). Resumed F3, F4, F6 with disk rules
  (SelfContained=false builds, clean bin/obj, no model downloads, stop under 800 MB). F5 queued:
  Core committed (d7b3a81, 93bd447; 157 tests green), sherpa adapter + SpeakersModule pending.

## Next step
Merge F3/F4/F6 when they return, resume F5, then Wave B.
