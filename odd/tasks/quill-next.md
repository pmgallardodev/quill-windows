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
- [ ] F1 Quill.Core (net8.0) + Quill.Core.Tests: domain, ports, MeetingSummary schema + citation
      validator, session format v2 (additive), solution file. Route D.
- [ ] F0 Spike + benchmark harness: LLamaSharp/Vulkan, sherpa-onnx .NET, Whisper small live
      latency; 5 synthetic Spanish transcripts with answer keys; scoring tool. Route D.
- [x] F2 Design system with WPF-UI: tokens, typography, light/dark, Mica, string resources. Route D.
      Evidence: 609ddc2, 38e6185, merge 8d0a84e, alias fix 148166c (WPF-UI root namespace `Wpf`
      collided with `using Wpf = System.Windows`). Build win-x64: 0 errors / 0 warnings.
      Pending: Windows runtime check (theme switch, Mica, high contrast). API: UI/Theme/README.md.

### Wave A (parallel, after F1)
- [ ] F3 Live transcription pipeline (ring buffer → VAD → Whisper small chunks). Route D.
- [ ] F4 LLM engine + summary use cases (live loop, final, catch-up, templates, level, language,
      map-reduce, citation validation). Route D.
- [ ] F5 Speaker separation (echo classifier, diarization on any track, rename, correction). Route D.
- [ ] F6 Main window redesign (meetings by day, Summary/Transcript/Notes tabs, My tasks). Route D.

### Wave B (parallel)
- [ ] F7 Companion panel (floating, pill, live tabs, What did I miss?, markers, notes, optional
      recording-notice button). Route D.
- [ ] F8 Audio playback from transcript, transcript editing + re-summarize, import. Route D.
- [ ] F9 Templates editor, Markdown/Obsidian export, settings (models, AI pack path, GPU). Route D.

### Wave C (parallel)
- [ ] F10 Ask (per meeting + across meetings) + local semantic search. Route D.
- [ ] F11 Optional speaker-naming memory (local voice embeddings, "Sounds like Juan?"). Route D.
- [ ] F12 Local API (127.0.0.1 + token), loopback-only webhooks, MCP endpoint. Route D.
- [ ] F13 Opt-in in-person mode (diarize mic track, recognize owner). Route D.
- [ ] P1 Packaging: app ZIP + AI-pack ZIP, AI-pack detection, README update. Route D.

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

## Next step
Merge Wave 0, then dispatch Wave A.
