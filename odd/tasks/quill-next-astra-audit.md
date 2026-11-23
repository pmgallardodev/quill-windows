# Quill Next — independent Astra audit, 2026-11-23

**Scope:** local production change bf72ca7..809fcb4, including the first remediation; no re-reporting of the three previously closed findings. Three independent GPT-6.2 Astra source reviews (Windows recording/UI, services/storage, distribution/bench). Severity reflects impact, not evidence of exploitation. No live targets, external advisories, release publication or remote writes. Keep README.md and .atl/ changes out.

**Delivery:** local feature branch, one active Luna writer at a time, reviewable work-unit commits. Delegated direct because behavior and regression tests span multiple files; forecast ~650–1,200 authored lines across the complete audit. No PR or remote integration selected. RDD off. Accept only observed checks and distinguish source fixes from Windows-only runtime validation.

| ID | Severity | Source at 809fcb4 | Defect / acceptance |
|---|---|---|---|
| N1 | P1 | FileSessionStore.cs:288–290,324–339 | Unreadable notes look absent; only genuine absence may yield editable empty text. |
| N2 | P1 | MeetingDetailViewModel.cs:263; NotesViewModel.cs:135; CompanionViewModel.cs:377 | Stale whole-note saves from two editors erase edits. Merge or refuse stale writes, retaining the draft. |
| S1 | P2 | HybridSearch.cs:89–99 | A hit deleted while awaiting embeddings breaks the query. Skip vanished hits, retain other results. |
| S2 | P2 | QuillServices.cs:146,272; AiPackDownloadModule.cs:65–72 | Existing meetings remain lexical-only after installation. Backfill without restart, including default path. |
| U1 | P2 | LiveRecording.cs:125–136 | Count cursor on chronological snapshots skips late segments. Forward each once. |
| U2 | P2 | TranscriptViewModel.cs:673; AppSessionActions.cs:136 | Equal timestamps resolve to first speaker. Target the selected segment without changing another. |
| U3 | P2 | MainViewModel.cs:354–359; PlayerViewModel.cs:167 | Hiding while playback opens can start private audio. Invalidate pending start on hide. |
| P1 | P1 | build-ai-pack.ps1:415–418; release.ps1:46–49 | Stable cleanup/checksums select prerelease ZIPs. Own exact version asset names only. |
| P2 | P2 | SplitModelAssembler.cs:172–176 | Fallback assembly deletes portable parts. Keep source when joined output is per-user. |
| P3 | P2 | SplitModelAssembler.cs:130–154 | Interrupted staging strands space. Safely reuse/reclaim stale reservations; protect active staging. |
| P4 | P2 | SplitModelPreparation.cs:47–49,87–92 | Cached Ready outlives output. Reassemble restored source parts without restart. |
| P5 | P2 | Publish-Portable.ps1:37–46 | Cached Whisper bypasses pinned digest. Reject incorrect bytes online/offline before packaging. |
| B1 | P2 | AudioChecks.cs:118–124; windows-latency.ps1:48–51,156 | A report users are invited to share includes transcript snippets and private paths. Share only safe measurements. |
| B2 | P3 | windows-latency.ps1:85–89,98–106 | Supplied audio skips voice fallback for diarization. Work with an installed non-Spanish voice. |
| B3 | P3 | windows-latency.ps1:48–52,156 | Child failures can end in apparent success. Return a failed final status for failed required measurements. |

## Work units
- [x] N1/N2/S1/S2 — notes, search and index lifecycle. `daf7766` — 719/719 Core; Windows cross-build clean. UI and post-install backfill runtime not exercised. `0b05e67` and final companion close guard: independent review confirmed navigation retains conflicted drafts, search captures the session once, and shutdown requires Saved. Windows runtime pending.
- [x] U1/U2/U3 — capture and playback UI. `69fef66`; 719/719 Core and Windows cross-build clean; UI tests not executed on Windows. `0b05e67` normalizes selected values and refuses ambiguous same-track mutations; independent source review confirmed. Core regression executed.
- [x] P2/P3/P4 — portable split-model lifecycle. `c37f9da` — exclusive staging truncation precedes free-space admission; independent Core run passed.
- [x] P1/P5/B1/B2/B3 — release scripts and shareable benchmark output. `cfa15bb`, `5878a27` — version-specific inventory, pinned cache, allowlisted measurements, native failure propagation. PowerShell tests authored but not run.

Checks: test-first with local synthetic data; full Core xUnit and Windows framework-dependent cross-build with cached .NET 10 SDK. Build/test bench where supported. PowerShell/Windows runtime checks cannot be claimed without their runners. No model or package downloads.

## Final verification

- 15/15 findings addressed in source. Follow-up review corrected actual notes close/navigation paths, speaker ambiguity, disappearing hits, stale staging on low disk, and failed LLM metrics. Companion's final save admission fix is in `af11021`.
- Fresh independent checks on the integrated production changes: 726 Core tests passed; Windows test project and benchmark project cross-built for win-x64 with zero warnings/errors. CRLF-aware Git diff check passed.
- PowerShell and Windows runtime unavailable: the release/benchmark PowerShell tests, WPF interaction, real audio/inference and packaging were not executed. The final test-fixture quoting correction was inspected only. No remote operations or deployments.
