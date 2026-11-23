# Quill.Core

Platform-free core of Quill (`net8.0`). Domain model, ports (interfaces the
Windows adapters implement) and pure logic. It must never reference WPF, NAudio,
Whisper.net, LLamaSharp, sherpa-onnx or any Windows API, so its tests run on macOS:

```sh
dotnet test Windows/Quill.Core.Tests          # macOS / Linux / Windows
dotnet build Windows/Quill.Windows -r win-x64 # Windows app (build only on macOS)
```

`Quill.Windows` references this project. Adapters live in `Quill.Windows`, depend
on these ports, and are wired in the app composition root.

## Layout

| Namespace | Contents |
|---|---|
| `Quill.Core.Domain` | `Session`, `SessionTrack`, `TranscriptSegment`, `Track`, `TrackNames`, `SpeakerIds`, `Speaker`, `Note`, `Marker`, `Template`, `MeetingSummary` (+ `Decision`, `ActionItem`, `OpenQuestion`, `Topic`, `Risk`, `KeyFact`, `SummaryMeta`), `SummaryKind`, `SummaryLevel`, `SummarySections`, `TranscriptPass` |
| `Quill.Core.Ports` | Interfaces below + their request/result records, `SystemClock` |
| `Quill.Core.Summaries` | `MeetingSummaryJson`, `MeetingSummarySchema`, `CitationValidator`, `TimestampJsonConverter`, `JsonSchemaGbnf`; use cases `FinalSummaryBuilder`, `LiveSummaryLoop`, `CatchUpSummarizer` with `SummaryOptions` / `SummaryOutcome` |
| `Quill.Core.Transcripts` | `TranscriptWindow` (catch-up), `TranscriptChunker` (prompt lines + map-reduce chunks) |
| `Quill.Core.Storage` | `FileSessionStore` (session format v2), `NotesDocument` (notes.md) |
| `Quill.Core.Templates` | `TemplateParser` (frontmatter + markdown), `DefaultTemplates` (six built-ins embedded from `Templates/Defaults/*.md`) |
| `Quill.Core.AiPack` | `ManifestAiPackLocator` |
| `Quill.Windows.Sessions`, `Quill.Windows.Transcription` (folder `Legacy/`) | v1 session code moved here unchanged (`SessionCatalog`, `SessionMetadata`, `TranscriptDocument`, `SessionStatus`, `SessionInsights`, `TranscriptExport`, `ResumePolicy`). Namespaces kept so existing Windows code compiles untouched. Internal; visible to `quill`, `Quill.Windows.Tests`, `Quill.Core.Tests`. |

**Name clash to know about:** `Quill.Windows.Sessions.TranscriptSegment` is the
v1 on-disk record (`Speaker`, `StartMs`, `EndMs`, `Text`) still used by the
current transcription pipeline. New code uses `Quill.Core.Domain.TranscriptSegment`.
If a file needs both namespaces, alias one:
`using TranscriptSegment = Quill.Core.Domain.TranscriptSegment;`.

## Conventions

- Times inside a session are `TimeSpan` offsets from the session start (earliest
  track). Summary citations (`at`, topic `start`) use the same clock and are
  serialized as `"hh:mm:ss"`.
- Speaker ids: `"me"` (owner), `"speaker-1"`, `"speaker-2"`... (`SpeakerIds.Remote(n)`);
  v1 sessions contain `"them"`. Display names live in `speakers.json`, never in segments.
- `Track` (Microphone/System) is where audio came from, not who spoke. Do not assume
  "mic = one person" (in-person mode diarizes the mic track).
- All async ports take a `CancellationToken`; streaming uses `IAsyncEnumerable`.
- Prompt lines come from `TranscriptChunker.FormatLine`: `[hh:mm:ss] speakerId: text`.
  That bracketed timestamp is exactly what the model must cite back.

## Ports

```csharp
// Transcription
interface ITranscriber {           // final pass / import (Whisper.net turbo)
  Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(TranscriptionRequest r, IProgress<double>? p, CancellationToken ct); }
record TranscriptionRequest(string AudioPath, Track Track, string SpeakerId, TimeSpan StartOffset = default, string? Language = null);
record AudioChunk(Track Track, TimeSpan Start, ReadOnlyMemory<float> Samples); // 16 kHz mono float
interface IStreamingTranscriber : IAsyncDisposable {   // live pass (whisper small), one per recording
  ValueTask FeedAsync(AudioChunk chunk, CancellationToken ct);      // never stall capture
  ValueTask CompleteAsync(CancellationToken ct);                    // flush + end stream
  IAsyncEnumerable<TranscriptSegment> ReadSegmentsAsync(CancellationToken ct); } // Editable = false

// Language models
record LanguageModelRequest(string SystemPrompt, string UserPrompt, int MaxOutputTokens = 1024, float Temperature = 0.2f);
interface ILanguageModel {
  string ModelName { get; } int ContextTokens { get; }
  IAsyncEnumerable<string> GenerateAsync(LanguageModelRequest r, CancellationToken ct);
  Task<string> GenerateJsonAsync(LanguageModelRequest r, string jsonSchema, CancellationToken ct); }
// extension: Task<string> GenerateTextAsync(this ILanguageModel, LanguageModelRequest, CancellationToken)
interface IEmbeddingModel { string ModelName { get; } int Dimensions { get; }
  Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct); }

// Speakers
interface IDiarizer {              // any track; cluster ids are per call
  Task<DiarizationResult> DiarizeAsync(DiarizationRequest r, IProgress<double>? p, CancellationToken ct); }
record DiarizationRequest(string AudioPath, Track Track, TimeSpan StartOffset = default, int? ExpectedSpeakers = null);
record DiarizationResult(Track Track, IReadOnlyList<SpeakerTurn> Turns, IReadOnlyDictionary<int, float[]> ClusterEmbeddings);
record SpeakerTurn(TimeSpan Start, TimeSpan End, int ClusterId, float[]? Embedding = null);
interface IEchoClassifier {        // one verdict per mic segment, in order
  Task<IReadOnlyList<EchoVerdict>> ClassifyAsync(EchoClassificationRequest r, CancellationToken ct); }
record EchoClassificationRequest(IReadOnlyList<TranscriptSegment> MicrophoneSegments, IReadOnlyList<TranscriptSegment> SystemSegments,
  EnergyEnvelope? MicrophoneEnergy = null, EnergyEnvelope? SystemEnergy = null);
record EnergyEnvelope(TimeSpan FrameDuration, IReadOnlyList<float> Rms);
record EchoVerdict(int MicrophoneSegmentIndex, bool IsEcho, double Confidence, string? Reason = null);

// Storage
interface ISessionStore {          // default: Storage.FileSessionStore(recordingsRoot, clock?)
  Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken ct);               // newest first
  Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct);
  Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptAsync(string id, TranscriptPass pass, CancellationToken ct);
  Task SaveTranscriptAsync(string id, TranscriptPass pass, TranscriptInfo info, IReadOnlyList<TranscriptSegment> s, CancellationToken ct);
  Task<MeetingSummary?> LoadSummaryAsync(string id, SummaryKind kind, CancellationToken ct);
  Task SaveSummaryAsync(string id, MeetingSummary summary, CancellationToken ct);       // path from summary.Meta.Kind
  Task<IReadOnlyList<Speaker>> LoadSpeakersAsync(string id, CancellationToken ct);
  Task SaveSpeakersAsync(string id, IReadOnlyList<Speaker> speakers, CancellationToken ct);
  Task<string> LoadNotesAsync(string id, CancellationToken ct);
  Task SaveNotesAsync(string id, string markdown, CancellationToken ct); }
interface IEmbeddingIndex {
  Task UpsertAsync(IReadOnlyList<EmbeddingRecord> records, CancellationToken ct);
  Task<IReadOnlyList<EmbeddingHit>> SearchAsync(float[] query, int topK, EmbeddingFilter? filter, CancellationToken ct);
  Task DeleteSessionAsync(string sessionId, CancellationToken ct); }
record EmbeddingRecord(string Id, string SessionId, string Kind, string Text, TimeSpan? At, float[] Vector);
interface IClock { DateTimeOffset Now { get; } }   // SystemClock.Instance

// AI pack
interface IAiPackLocator { AiPackStatus Locate(); }  // default: AiPack.ManifestAiPackLocator(appDir, overrideRoot?)
record AiPackStatus(bool IsInstalled, string? Root, string? Version,
  IReadOnlyDictionary<AiPackComponent, string> Paths, IReadOnlyList<AiPackComponent> Missing); // PathOf(c), Has(c)
enum AiPackComponent { LanguageModel, LiveWhisper, DiarizationSegmentation, SpeakerEmbedding, TextEmbedding }
```

AI pack manifest (`<root>/ai-pack.json`, root defaults to `<app>/ai-pack`):
`{ "version": "1.0.0", "files": { "languageModel": "llm/x.gguf", "liveWhisper": "...", "diarizationSegmentation": "...", "speakerEmbedding": "...", "textEmbedding": "..." } }`.
Paths are relative and must stay inside the root.

## Summary pipeline building blocks

1. `TranscriptChunker.Chunk(segments, maxTokens)` → chunks of prompt text
   (tokens ≈ chars / 4; one segment is never split).
2. `ILanguageModel.GenerateJsonAsync(request, MeetingSummarySchema.For(kind))`
   (or `For(template.Sections)`). The schema covers the model output only: `meta` is
   excluded, every section is required, empty arrays mean "None".
3. `MeetingSummaryJson.Deserialize(json)`: lenient on missing/null sections, strict on
   timestamps (`at` is required and must parse) → `JsonException`.
4. `CitationValidator.Validate(summary, transcript)` drops items whose `at` is not
   inside any segment (±1 s tolerance by default) and returns them for logging.
5. Set `Meta` (template, language, level, model, generatedAt, kind) and
   `ISessionStore.SaveSummaryAsync`.

"What did I miss?": `TranscriptWindow.Last(segments, TimeSpan.FromMinutes(n), now)`.

The use cases wrap these steps. They run model calls through `IInferenceScheduler` and do
not throw for an unusable model: `LanguageModelUnavailableException` from the adapter becomes
`SummaryStatus.AiPackMissing`, and output that is still unparseable after one retry becomes
`Failed`. LLamaSharp has no schema-to-grammar converter, so adapters turn the schema into GBNF
with `JsonSchemaGbnf.Convert(schema)` (root rule `root`); it throws `NotSupportedException` for
schema features outside the subset `MeetingSummarySchema` uses. Windows wiring:
`Quill.Windows.Summaries.SummariesModule` (adapter `LlamaSharpLanguageModel`).

## Session folder format

v1 (unchanged, still written by the current recorder): `meta.json`, `mic.wav`,
`system.wav`, `transcript.json`, `transcript.md`, `status.json`, `.quill-session`,
`.quill-failed`.

v2 adds only new files, so older Quill versions keep reading the folder:

| File | Content |
|---|---|
| `summary.json` | Final `MeetingSummary` (design §7.1) |
| `live/summary.json` | Latest live summary |
| `live/transcript.json` | Live-pass transcript (same shape as `transcript.json`) |
| `speakers.json` | `{ "version": 1, "speakers": [ { "id", "track": "mic"\|"system", "displayName"? } ] }` |
| `notes.md` | Markdown; bullets `- [hh:mm:ss] text` or `- text` (`NotesDocument`) |

`transcript.json` segments may carry two optional fields, `"track"` and
`"edited"`, which v1 readers ignore. When `track` is absent it is derived from
the speaker (`"me"` → mic, anything else → system).
