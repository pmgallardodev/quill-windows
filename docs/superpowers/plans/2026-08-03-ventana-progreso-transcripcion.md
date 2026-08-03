# Transcription Progress Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Añadir una ventana WPF que lista las sesiones de grabación con su estado y muestra en vivo el progreso de la transcripción en curso, con estado persistido por sesión.

**Architecture:** El progreso efímero (porcentajes, ETA) viaja por eventos en proceso y nunca toca el disco; el estado durable (`queued`/`running`/`done`/`failed`) se escribe en un `status.json` por sesión solo en las transiciones. El disco sigue siendo la fuente de verdad: si `status.json` falta o está corrupto, `SessionCatalog` deriva el estado de qué archivos existen.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF (XAML + MVVM ligero), Windows Forms `NotifyIcon` (existente), NAudio 2.2.1, Whisper.net 1.9.1, xUnit.

## Global Constraints

- Target framework de todos los proyectos: `net8.0-windows`. No introducir un TFM distinto.
- **Los tests solo se ejecutan en Windows.** `net8.0-windows` necesita el runtime pack `Microsoft.WindowsDesktop.App`, que no existe en macOS ni Linux. El entorno de desarrollo actual es macOS: allí se puede editar y hacer `restore`/`build` gracias a `EnableWindowsTargeting`, pero `dotnet test` fallará. Todos los pasos "Run test" de este plan se ejecutan en una máquina Windows 11.
- Todos los tipos del proyecto son `internal`. El proyecto de tests accede vía `InternalsVisibleTo`.
- Escritura de archivos de estado siempre con `Sessions.AtomicFile.Write` (`Sessions/SessionDocuments.cs:86`). Nunca `File.WriteAllText` directo sobre un archivo que otro proceso pueda leer.
- Mensajes de UI y de excepción en español sin tildes, siguiendo el estilo existente (`"Iniciar grabacion"`, `"no se encontro el modelo Whisper incluido"`). Mensajes de commit y de `transcribe.log` en inglés, siguiendo el estilo existente.
- Valores válidos de `status.json.state`: `queued`, `running`, `done`, `failed`. `Recording` y `Abandoned` son estados derivados y nunca se escriben.
- No añadir dependencias nuevas al proyecto principal. `UseWPF` ya está activo.
- Spec de referencia: `docs/superpowers/specs/2026-08-03-ventana-progreso-transcripcion-design.md`.

## File Structure

**Crear:**

| Archivo | Responsabilidad |
|---|---|
| `Windows/Quill.Windows/Sessions/SessionStatus.cs` | Lee y escribe `status.json`. Serialización y nada más. |
| `Windows/Quill.Windows/Sessions/SessionCatalog.cs` | `SessionState`, `SessionSnapshot` y el escaneo del disco. Sin estado propio. |
| `Windows/Quill.Windows/Transcription/ResumePolicy.cs` | Función pura que decide si una sesión se reencola al arrancar. |
| `Windows/Quill.Windows/Transcription/TranscriptionUpdate.cs` | `TrackProgress` y `TranscriptionUpdate`, el modelo de eventos. |
| `Windows/Quill.Windows/UI/SessionRowViewModel.cs` | Una fila de la ventana: estado, progreso, ETA. |
| `Windows/Quill.Windows/UI/SessionsViewModel.cs` | Puente entre catálogo, eventos y vista. Throttling. |
| `Windows/Quill.Windows/UI/SessionsWindow.xaml` (+ `.xaml.cs`) | La ventana. |
| `Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj` | Proyecto de tests. |
| `Windows/Quill.Windows.Tests/TempDirectory.cs` | Helper de carpeta temporal desechable. |
| `Windows/Quill.Windows.Tests/SessionStatusTests.cs` | Tests de serialización y tolerancia a corrupción. |
| `Windows/Quill.Windows.Tests/ResumePolicyTests.cs` | Tests de la regla de reanudación. |
| `Windows/Quill.Windows.Tests/SessionCatalogTests.cs` | Tests de derivación de estado. |
| `Windows/Quill.Windows.Tests/WhisperTranscriberTests.cs` | Test de normalización de audio y duración. |

**Modificar:**

| Archivo | Cambio |
|---|---|
| `Windows/Quill.Windows/Quill.Windows.csproj` | `InternalsVisibleTo` para los tests. |
| `Windows/Quill.Windows/Transcription/WhisperTranscriber.cs` | `NormalizeToWhisperWav` devuelve `TimeSpan`; `TranscribeAsync` acepta `IProgress<double>`. |
| `Windows/Quill.Windows/Transcription/TranscriptionCoordinator.cs` | Eventos partidos, escritura de `status.json`, `Retry`, `ResumePending` usa `ResumePolicy`. |
| `Windows/Quill.Windows/UI/TrayApplication.cs` | Ítem de menú, doble clic abre la ventana, expone la sesión activa. |
| `README.md` | Documentar `status.json` y la ventana. |

---

### Task 1: SessionStatus y proyecto de tests

**Files:**
- Create: `Windows/Quill.Windows/Sessions/SessionStatus.cs`
- Create: `Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`
- Create: `Windows/Quill.Windows.Tests/TempDirectory.cs`
- Create: `Windows/Quill.Windows.Tests/SessionStatusTests.cs`
- Modify: `Windows/Quill.Windows/Quill.Windows.csproj`

**Interfaces:**
- Consumes: `Quill.Windows.Sessions.AtomicFile.Write(string, string)` de `Sessions/SessionDocuments.cs:88`.
- Produces: `SessionStatus` con propiedades `State`, `UpdatedAt`, `Model`, `ElapsedSeconds`, `Error`; constantes `SessionStatus.Queued/Running/Done/Failed`; `SessionStatus.PathFor(string)`; `SessionStatus.TryRead(string)` que devuelve `SessionStatus?`; método de instancia `Write(string)`.

- [ ] **Step 1: Crear el proyecto de tests**

Crear `Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../Quill.Windows/Quill.Windows.csproj" />
  </ItemGroup>
</Project>
```

Añadir al final del primer `<PropertyGroup>` de `Windows/Quill.Windows/Quill.Windows.csproj`, después de `<SelfContained>true</SelfContained>`:

```xml
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Quill.Windows.Tests" />
  </ItemGroup>
```

(El `ItemGroup` va fuera del `PropertyGroup`, inmediatamente después de cerrarlo.)

Crear `Windows/Quill.Windows.Tests/TempDirectory.cs`:

```csharp
namespace Quill.Windows.Tests;

/// <summary>Carpeta temporal que se borra al final del test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quill-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Session(string name)
    {
        var directory = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Write(string sessionDirectory, string fileName, string contents) =>
        File.WriteAllText(System.IO.Path.Combine(sessionDirectory, fileName), contents);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un test que deja un handle abierto no debe hacer fallar la suite.
        }
    }
}
```

- [ ] **Step 2: Escribir los tests que fallan**

Crear `Windows/Quill.Windows.Tests/SessionStatusTests.cs`:

```csharp
using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Windows.Tests;

public class SessionStatusTests
{
    [Fact]
    public void WriteThenTryRead_RoundTripsEveryField()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");

        new SessionStatus
        {
            State = SessionStatus.Failed,
            UpdatedAt = new DateTimeOffset(2026, 8, 1, 16, 4, 11, TimeSpan.Zero),
            Model = "ggml-large-v3-q5_0",
            ElapsedSeconds = 187.4,
            Error = "no se encontro el modelo Whisper incluido",
        }.Write(session);

        var status = SessionStatus.TryRead(session);

        Assert.NotNull(status);
        Assert.Equal(SessionStatus.Failed, status!.State);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 16, 4, 11, TimeSpan.Zero), status.UpdatedAt);
        Assert.Equal("ggml-large-v3-q5_0", status.Model);
        Assert.Equal(187.4, status.ElapsedSeconds);
        Assert.Equal("no se encontro el modelo Whisper incluido", status.Error);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenFileMissing()
    {
        using var temp = new TempDirectory();
        Assert.Null(SessionStatus.TryRead(temp.Session("2026.08.01-1600")));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenJsonIsCorrupt()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", "{ esto no es json");

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenStateIsMissing()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", """{ "updated_at": "2026-08-01T16:04:11+00:00" }""");

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenFileIsEmpty()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", string.Empty);

        Assert.Null(SessionStatus.TryRead(session));
    }

    [Fact]
    public void TryRead_KeepsUnknownState_SoTheCatalogCanDecide()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "status.json", """{ "state": "algo-del-futuro" }""");

        var status = SessionStatus.TryRead(session);

        Assert.NotNull(status);
        Assert.Equal("algo-del-futuro", status!.State);
    }

    [Fact]
    public void Write_LeavesNoTemporaryFilesBehind()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");

        new SessionStatus { State = SessionStatus.Done, UpdatedAt = DateTimeOffset.UtcNow }.Write(session);

        Assert.Equal(new[] { "status.json" }, Directory.GetFiles(session).Select(Path.GetFileName).Order());
    }
}
```

- [ ] **Step 3: Ejecutarlos para verificar que fallan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`
Expected: FAIL en compilación — `The type or namespace name 'SessionStatus' does not exist`.

- [ ] **Step 4: Implementar SessionStatus**

Crear `Windows/Quill.Windows/Sessions/SessionStatus.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Windows.Sessions;

/// <summary>
/// Estado durable de una sesion. Enriquece lo que el disco ya dice con lo que no
/// puede saber: el porque de un fallo, el modelo usado y cuanto tardo. No es la
/// fuente de verdad: si falta o esta corrupto, el estado se deriva de los archivos.
/// </summary>
internal sealed class SessionStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("elapsed_seconds")]
    public double? ElapsedSeconds { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static string PathFor(string sessionDirectory) =>
        Path.Combine(sessionDirectory, "status.json");

    /// <summary>Devuelve null ante cualquier problema. Leer el estado nunca debe lanzar.</summary>
    public static SessionStatus? TryRead(string sessionDirectory)
    {
        var path = PathFor(sessionDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var status = JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(status?.State) ? null : status;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string sessionDirectory) => AtomicFile.Write(
        PathFor(sessionDirectory),
        JsonSerializer.Serialize(this, WriteOptions));
}
```

- [ ] **Step 5: Ejecutar los tests y verificar que pasan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add Windows/Quill.Windows/Sessions/SessionStatus.cs \
        Windows/Quill.Windows/Quill.Windows.csproj \
        Windows/Quill.Windows.Tests/
git commit -m "Add SessionStatus and test project"
```

---

### Task 2: ResumePolicy

**Files:**
- Create: `Windows/Quill.Windows/Transcription/ResumePolicy.cs`
- Create: `Windows/Quill.Windows.Tests/ResumePolicyTests.cs`

**Interfaces:**
- Consumes: `SessionStatus` de la Task 1.
- Produces: `ResumePolicy.ShouldEnqueue(bool hasMetadata, bool hasTranscript, SessionStatus? status)` que devuelve `bool`.

- [ ] **Step 1: Escribir los tests que fallan**

Crear `Windows/Quill.Windows.Tests/ResumePolicyTests.cs`:

```csharp
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Windows.Tests;

public class ResumePolicyTests
{
    private static SessionStatus With(string state) =>
        new() { State = state, UpdatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void DoesNotEnqueue_WhenMetadataIsMissing()
    {
        // Sesion abandonada: sin meta.json no hay offsets ni nombres de pista.
        Assert.False(ResumePolicy.ShouldEnqueue(hasMetadata: false, hasTranscript: false, status: null));
    }

    [Fact]
    public void DoesNotEnqueue_WhenTranscriptAlreadyExists()
    {
        Assert.False(ResumePolicy.ShouldEnqueue(hasMetadata: true, hasTranscript: true, status: null));
    }

    [Fact]
    public void Enqueues_WhenThereIsNoStatusFile()
    {
        // Sesion anterior a esta version.
        Assert.True(ResumePolicy.ShouldEnqueue(hasMetadata: true, hasTranscript: false, status: null));
    }

    [Fact]
    public void Enqueues_WhenStatusIsRunning()
    {
        // Un "running" huerfano no es un fallo: la app se cerro a mitad.
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Running)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsQueued()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Queued)));
    }

    [Fact]
    public void DoesNotEnqueue_WhenStatusIsFailed()
    {
        // El bucle de reintentos se corta aqui: reintentar es decision del usuario.
        Assert.False(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Failed)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsDoneButTranscriptIsGone()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With(SessionStatus.Done)));
    }

    [Fact]
    public void Enqueues_WhenStatusIsUnknown()
    {
        Assert.True(ResumePolicy.ShouldEnqueue(true, false, With("algo-del-futuro")));
    }
}
```

- [ ] **Step 2: Ejecutarlos para verificar que fallan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~ResumePolicyTests"`
Expected: FAIL en compilación — `The name 'ResumePolicy' does not exist`.

- [ ] **Step 3: Implementar ResumePolicy**

Crear `Windows/Quill.Windows/Transcription/ResumePolicy.cs`:

```csharp
using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

/// <summary>
/// Decide si una sesion se reencola al arrancar. Solo un "failed" explicito
/// detiene el reintento; cualquier otro estado se reintenta, igual que antes.
/// </summary>
internal static class ResumePolicy
{
    public static bool ShouldEnqueue(bool hasMetadata, bool hasTranscript, SessionStatus? status)
    {
        if (!hasMetadata || hasTranscript)
        {
            return false;
        }

        return status?.State != SessionStatus.Failed;
    }
}
```

- [ ] **Step 4: Ejecutar los tests y verificar que pasan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~ResumePolicyTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add Windows/Quill.Windows/Transcription/ResumePolicy.cs \
        Windows/Quill.Windows.Tests/ResumePolicyTests.cs
git commit -m "Add ResumePolicy to stop retrying permanently failed sessions"
```

---

### Task 3: SessionCatalog

**Files:**
- Create: `Windows/Quill.Windows/Sessions/SessionCatalog.cs`
- Create: `Windows/Quill.Windows.Tests/SessionCatalogTests.cs`

**Interfaces:**
- Consumes: `SessionStatus` (Task 1), `SessionMetadata` de `Sessions/SessionDocuments.cs:7`.
- Produces: `enum SessionState { Recording, Abandoned, Queued, Running, Done, Failed }`; `record SessionSnapshot(string Directory, string Name, DateTimeOffset Started, TimeSpan Duration, SessionState State, string? Model, string? Error, TimeSpan? TranscriptionElapsed)`; `SessionCatalog.Scan(string recordingsRoot, string? activeSessionDirectory)` que devuelve `IReadOnlyList<SessionSnapshot>`.

- [ ] **Step 1: Escribir los tests que fallan**

Crear `Windows/Quill.Windows.Tests/SessionCatalogTests.cs`:

```csharp
using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Windows.Tests;

public class SessionCatalogTests
{
    private const string Meta = """
        {
          "started": "2026-08-01T16:00:00+00:00",
          "ended": "2026-08-01T16:42:00+00:00",
          "duration_seconds": 2520,
          "files": { "mic": "mic.wav", "system": "system.wav" },
          "start_offset_ms": { "mic": 0, "system": 120 }
        }
        """;

    [Fact]
    public void Scan_ReturnsEmpty_WhenRootDoesNotExist()
    {
        Assert.Empty(SessionCatalog.Scan(Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}"), null));
    }

    [Fact]
    public void Scan_MarksActiveSessionAsRecording_EvenWithoutMetadata()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.03-1420");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, session));

        Assert.Equal(SessionState.Recording, snapshot.State);
    }

    [Fact]
    public void Scan_MarksSessionWithoutMetadataAsAbandoned_WhenItIsNotTheActiveOne()
    {
        // RecordingSession solo escribe meta.json en Stop(). Un cierre brusco deja
        // la carpeta sin metadatos para siempre: no es una grabacion en curso.
        using var temp = new TempDirectory();
        temp.Session("2026.07.31-1130");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, activeSessionDirectory: null));

        Assert.Equal(SessionState.Abandoned, snapshot.State);
    }

    [Fact]
    public void Scan_MarksSessionAsDone_WhenTranscriptExists()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", "{}");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Done, snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(2520), snapshot.Duration);
    }

    [Fact]
    public void Scan_TranscriptWins_OverAStatusThatSaysFailed()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.01-1600");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "transcript.json", "{}");
        temp.Write(session, "status.json", """{ "state": "failed", "error": "obsoleto" }""");

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Done, snapshot.State);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void Scan_MarksSessionAsQueued_WhenOnlyMetadataExists()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Queued, snapshot.State);
    }

    [Fact]
    public void Scan_ReadsFailedStateWithItsErrorAndModel()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.07.31-1130");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", """
            {
              "state": "failed",
              "model": "ggml-large-v3-q5_0",
              "elapsed_seconds": 12.5,
              "error": "no se encontro el modelo"
            }
            """);

        var snapshot = Assert.Single(SessionCatalog.Scan(temp.Path, null));

        Assert.Equal(SessionState.Failed, snapshot.State);
        Assert.Equal("no se encontro el modelo", snapshot.Error);
        Assert.Equal("ggml-large-v3-q5_0", snapshot.Model);
        Assert.Equal(TimeSpan.FromSeconds(12.5), snapshot.TranscriptionElapsed);
    }

    [Fact]
    public void Scan_FallsBackToQueued_WhenStatusStateIsUnknown()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", """{ "state": "algo-del-futuro" }""");

        Assert.Equal(SessionState.Queued, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_FallsBackToQueued_WhenStatusIsCorrupt()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", Meta);
        temp.Write(session, "status.json", "{ roto");

        Assert.Equal(SessionState.Queued, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_TreatsCorruptMetadataAsAbandoned()
    {
        using var temp = new TempDirectory();
        var session = temp.Session("2026.08.02-0930");
        temp.Write(session, "meta.json", "{ roto");

        Assert.Equal(SessionState.Abandoned, Assert.Single(SessionCatalog.Scan(temp.Path, null)).State);
    }

    [Fact]
    public void Scan_OrdersNewestFirst()
    {
        using var temp = new TempDirectory();
        temp.Session("2026.07.31-1130");
        temp.Session("2026.08.03-1420");
        temp.Session("2026.08.01-1600");

        var names = SessionCatalog.Scan(temp.Path, null).Select(snapshot => snapshot.Name).ToArray();

        Assert.Equal(new[] { "2026.08.03-1420", "2026.08.01-1600", "2026.07.31-1130" }, names);
    }
}
```

- [ ] **Step 2: Ejecutarlos para verificar que fallan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~SessionCatalogTests"`
Expected: FAIL en compilación — `The name 'SessionCatalog' does not exist`.

- [ ] **Step 3: Implementar SessionCatalog**

Crear `Windows/Quill.Windows/Sessions/SessionCatalog.cs`:

```csharp
using System.Text.Json;

namespace Quill.Windows.Sessions;

internal enum SessionState
{
    /// <summary>La grabacion que este proceso tiene abierta ahora mismo.</summary>
    Recording,

    /// <summary>Carpeta sin meta.json que no es la grabacion en curso: se interrumpio sin cerrar.</summary>
    Abandoned,

    Queued,
    Running,
    Done,
    Failed,
}

internal sealed record SessionSnapshot(
    string Directory,
    string Name,
    DateTimeOffset Started,
    TimeSpan Duration,
    SessionState State,
    string? Model,
    string? Error,
    TimeSpan? TranscriptionElapsed);

/// <summary>
/// Deriva el estado de cada sesion a partir del disco. Sin estado propio: cada
/// llamada vuelve a mirar los archivos, de modo que borrar status.json a mano
/// degrada el detalle pero nunca rompe el resultado.
/// </summary>
internal static class SessionCatalog
{
    public static IReadOnlyList<SessionSnapshot> Scan(string recordingsRoot, string? activeSessionDirectory)
    {
        if (!Directory.Exists(recordingsRoot))
        {
            return Array.Empty<SessionSnapshot>();
        }

        return Directory.EnumerateDirectories(recordingsRoot)
            .Select(directory => Describe(directory, activeSessionDirectory))
            .OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static SessionSnapshot Describe(string directory, string? activeSessionDirectory)
    {
        var metadata = TryReadMetadata(directory);
        var status = SessionStatus.TryRead(directory);
        var hasTranscript = File.Exists(Path.Combine(directory, "transcript.json"));
        var state = DetermineState(directory, activeSessionDirectory, metadata is not null, hasTranscript, status);

        return new SessionSnapshot(
            directory,
            Path.GetFileName(directory),
            metadata?.Started ?? Directory.GetCreationTimeUtc(directory),
            TimeSpan.FromSeconds(metadata?.DurationSeconds ?? 0),
            state,
            status?.Model,
            state == SessionState.Failed ? status?.Error : null,
            status?.ElapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    private static SessionState DetermineState(
        string directory,
        string? activeSessionDirectory,
        bool hasMetadata,
        bool hasTranscript,
        SessionStatus? status)
    {
        if (IsSameDirectory(directory, activeSessionDirectory))
        {
            return SessionState.Recording;
        }

        if (!hasMetadata)
        {
            return SessionState.Abandoned;
        }

        if (hasTranscript)
        {
            return SessionState.Done;
        }

        // Un "done" sin transcript.json significa que el transcript se borro: hay que rehacerlo.
        return status?.State switch
        {
            SessionStatus.Running => SessionState.Running,
            SessionStatus.Failed => SessionState.Failed,
            _ => SessionState.Queued,
        };
    }

    private static bool IsSameDirectory(string directory, string? other) =>
        other is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            StringComparison.OrdinalIgnoreCase);

    private static SessionMetadata? TryReadMetadata(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "meta.json"))
                ? SessionMetadata.Read(directory)
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Ejecutar los tests y verificar que pasan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~SessionCatalogTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add Windows/Quill.Windows/Sessions/SessionCatalog.cs \
        Windows/Quill.Windows.Tests/SessionCatalogTests.cs
git commit -m "Add SessionCatalog to derive session state from disk"
```

---

### Task 4: Progreso en WhisperTranscriber

**Files:**
- Modify: `Windows/Quill.Windows/Transcription/WhisperTranscriber.cs:22-71`
- Create: `Windows/Quill.Windows.Tests/WhisperTranscriberTests.cs`

**Interfaces:**
- Produces: `WhisperTranscriber.NormalizeToWhisperWav(string inputPath, string outputPath)` pasa de `private static void` a `internal static TimeSpan`; `TranscribeAsync(string audioPath, string speaker, long offsetMilliseconds, IProgress<double>? progress, CancellationToken cancellationToken)`.

- [ ] **Step 1: Escribir el test que falla**

Crear `Windows/Quill.Windows.Tests/WhisperTranscriberTests.cs`. Genera un WAV sintético con NAudio, así que no necesita hardware de audio ni el modelo Whisper:

```csharp
using NAudio.Wave;
using Quill.Windows.Transcription;
using Xunit;

namespace Quill.Windows.Tests;

public class WhisperTranscriberTests
{
    [Fact]
    public void NormalizeToWhisperWav_ReturnsSourceDuration_AndWrites16kHzMono()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteSilence(input, new WaveFormat(44_100, 16, 2), TimeSpan.FromSeconds(2));

        var duration = WhisperTranscriber.NormalizeToWhisperWav(input, output);

        Assert.Equal(2d, duration.TotalSeconds, precision: 1);

        using var reader = new WaveFileReader(output);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(2d, reader.TotalTime.TotalSeconds, precision: 1);
    }

    [Fact]
    public void NormalizeToWhisperWav_AcceptsMonoInput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "source.wav");
        var output = Path.Combine(temp.Path, "normalized.wav");
        WriteSilence(input, new WaveFormat(48_000, 16, 1), TimeSpan.FromSeconds(1));

        var duration = WhisperTranscriber.NormalizeToWhisperWav(input, output);

        Assert.Equal(1d, duration.TotalSeconds, precision: 1);
        using var reader = new WaveFileReader(output);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
    }

    private static void WriteSilence(string path, WaveFormat format, TimeSpan duration)
    {
        using var writer = new WaveFileWriter(path, format);
        var buffer = new byte[format.AverageBytesPerSecond];
        var remaining = (long)(format.AverageBytesPerSecond * duration.TotalSeconds);
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            writer.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }
}
```

- [ ] **Step 2: Ejecutarlo para verificar que falla**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~WhisperTranscriberTests"`
Expected: FAIL en compilación — `'WhisperTranscriber.NormalizeToWhisperWav(string, string)' is inaccessible due to its protection level`.

- [ ] **Step 3: Modificar WhisperTranscriber**

Sustituir el cuerpo de `TranscribeAsync` y `NormalizeToWhisperWav` en `Windows/Quill.Windows/Transcription/WhisperTranscriber.cs` por:

```csharp
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath,
        string speaker,
        long offsetMilliseconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            $".{Path.GetFileNameWithoutExtension(audioPath)}.{Guid.NewGuid():N}.whisper.wav");
        try
        {
            var total = NormalizeToWhisperWav(audioPath, normalizedPath);
            using var processor = _factory.CreateBuilder().WithLanguage("es").Build();
            await using var audio = File.OpenRead(normalizedPath);
            var segments = new List<TranscriptSegment>();
            await foreach (var result in processor.ProcessAsync(audio).WithCancellation(cancellationToken))
            {
                segments.Add(new TranscriptSegment(
                    speaker,
                    (long)result.Start.TotalMilliseconds + offsetMilliseconds,
                    (long)result.End.TotalMilliseconds + offsetMilliseconds,
                    result.Text.Trim()));

                if (progress is not null && total > TimeSpan.Zero)
                {
                    progress.Report(Math.Clamp(result.End / total, 0d, 1d));
                }
            }

            progress?.Report(1d);
            return segments;
        }
        finally
        {
            File.Delete(normalizedPath);
        }
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Convierte a 16 kHz mono y devuelve la duracion del original. AudioFileReader
    /// ya la conoce, y es mas fiable que meta.json porque las pistas tienen offsets
    /// distintos y pueden no durar lo mismo.
    /// </summary>
    internal static TimeSpan NormalizeToWhisperWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var duration = reader.TotalTime;
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source);
        }
        else if (source.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException($"canales no compatibles: {source.WaveFormat.Channels}");
        }

        var resampled = new WdlResamplingSampleProvider(source, 16_000);
        WaveFileWriter.CreateWaveFile16(outputPath, resampled);
        return duration;
    }
```

- [ ] **Step 4: Ejecutar los tests y verificar que pasan**

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj --filter "FullyQualifiedName~WhisperTranscriberTests"`
Expected: PASS, 2 tests. `TranscriptionCoordinator.cs` todavía no compila porque llama a `TranscribeAsync` con la firma antigua — se arregla en la Task 5. Si el build falla por eso, añadir temporalmente `null,` antes de `cancellationToken` en `TranscriptionCoordinator.cs:129`.

- [ ] **Step 5: Commit**

```bash
git add Windows/Quill.Windows/Transcription/WhisperTranscriber.cs \
        Windows/Quill.Windows/Transcription/TranscriptionCoordinator.cs \
        Windows/Quill.Windows.Tests/WhisperTranscriberTests.cs
git commit -m "Report per-track transcription progress from Whisper"
```

---

### Task 5: Eventos y estado en TranscriptionCoordinator

**Files:**
- Create: `Windows/Quill.Windows/Transcription/TranscriptionUpdate.cs`
- Modify: `Windows/Quill.Windows/Transcription/TranscriptionCoordinator.cs` (archivo completo)
- Modify: `Windows/Quill.Windows/UI/TrayApplication.cs:159-180`

**Interfaces:**
- Consumes: `SessionState` y `SessionStatus` (Tasks 1 y 3), `ResumePolicy.ShouldEnqueue` (Task 2), `TranscribeAsync(..., IProgress<double>?, CancellationToken)` (Task 4).
- Produces: `record TrackProgress(string Track, double Fraction, bool Completed)`; `record TranscriptionUpdate(string SessionDirectory, SessionState State, int Queued, IReadOnlyList<TrackProgress> Tracks, string? Error, TimeSpan Elapsed)`; eventos `TranscriptionCoordinator.StateChanged` y `TranscriptionCoordinator.ProgressChanged`, ambos `Action<TranscriptionUpdate>?`; método `Retry(string sessionDirectory)`.

- [ ] **Step 1: Crear el modelo de eventos**

Crear `Windows/Quill.Windows/Transcription/TranscriptionUpdate.cs`:

```csharp
using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

internal sealed record TrackProgress(string Track, double Fraction, bool Completed);

/// <summary>
/// Se publica por dos eventos distintos con contratos de coste opuestos:
/// StateChanged unas cinco veces por sesion, ProgressChanged cientos por minuto.
/// Un consumidor que hace trabajo caro por evento se suscribe solo al primero.
/// </summary>
internal sealed record TranscriptionUpdate(
    string SessionDirectory,
    SessionState State,
    int Queued,
    IReadOnlyList<TrackProgress> Tracks,
    string? Error,
    TimeSpan Elapsed);
```

- [ ] **Step 2: Reescribir TranscriptionCoordinator**

Sustituir el contenido completo de `Windows/Quill.Windows/Transcription/TranscriptionCoordinator.cs` por:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Quill.Windows.Infrastructure;
using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

internal sealed class TranscriptionCoordinator : IDisposable
{
    private readonly string _recordingsRoot;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private WhisperTranscriber? _transcriber;

    public TranscriptionCoordinator(string recordingsRoot)
    {
        _recordingsRoot = recordingsRoot;
        _worker = Task.Run(DrainAsync);
    }

    /// <summary>Transiciones de estado. Unas cinco por sesion: seguro para trabajo caro.</summary>
    public event Action<TranscriptionUpdate>? StateChanged;

    /// <summary>Avance dentro de una pista. Cientos por minuto: el consumidor debe limitar su ritmo.</summary>
    public event Action<TranscriptionUpdate>? ProgressChanged;

    public void ResumePending()
    {
        if (!ConfigStore.Load().Transcription.Enabled || !Directory.Exists(_recordingsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_recordingsRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var hasMetadata = File.Exists(Path.Combine(directory, "meta.json"));
            var hasTranscript = File.Exists(Path.Combine(directory, "transcript.json"));
            if (ResumePolicy.ShouldEnqueue(hasMetadata, hasTranscript, SessionStatus.TryRead(directory)))
            {
                Enqueue(directory);
            }
        }
    }

    public void Enqueue(string sessionDirectory)
    {
        if (!ConfigStore.Load().Transcription.Enabled ||
            !_pending.TryAdd(sessionDirectory, 0))
        {
            return;
        }

        WriteStatus(sessionDirectory, SessionStatus.Queued, null, null);
        _queue.Writer.TryWrite(sessionDirectory);
        StateChanged?.Invoke(new TranscriptionUpdate(
            sessionDirectory, SessionState.Queued, _pending.Count, Array.Empty<TrackProgress>(), null, TimeSpan.Zero));
    }

    /// <summary>Vuelve a encolar una sesion que fallo. Es la unica forma de reintentar un "failed".</summary>
    public void Retry(string sessionDirectory)
    {
        if (!Directory.Exists(sessionDirectory))
        {
            return;
        }

        try
        {
            File.Delete(SessionStatus.PathFor(sessionDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(sessionDirectory, $"could not clear status.json: {exception.Message}");
        }

        Enqueue(sessionDirectory);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        var workerCompleted = false;
        try
        {
            workerCompleted = _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutdown only needs to leave a session without transcript.json so it resumes later.
        }
        finally
        {
            if (workerCompleted)
            {
                _transcriber?.Dispose();
            }
            _shutdown.Dispose();
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var sessionDirectory in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                _pending.TryRemove(sessionDirectory, out _);
                var clock = Stopwatch.StartNew();
                var tracks = new List<TrackProgress>();

                WriteStatus(sessionDirectory, SessionStatus.Running, null, null);
                PublishState(sessionDirectory, SessionState.Running, tracks, null, clock.Elapsed);
                try
                {
                    await TranscribeSessionAsync(sessionDirectory, tracks, clock, _shutdown.Token);
                    WriteStatus(sessionDirectory, SessionStatus.Done, clock.Elapsed.TotalSeconds, null);
                    PublishState(sessionDirectory, SessionState.Done, tracks, null, clock.Elapsed);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Log(sessionDirectory, $"transcription failed: {exception}");
                    WriteStatus(sessionDirectory, SessionStatus.Failed, clock.Elapsed.TotalSeconds, exception.Message);
                    PublishState(sessionDirectory, SessionState.Failed, tracks, exception.Message, clock.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task TranscribeSessionAsync(
        string sessionDirectory,
        List<TrackProgress> tracks,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var metadata = SessionMetadata.Read(sessionDirectory);
        var transcriber = _transcriber ??= new WhisperTranscriber(ConfigStore.ModelPath);
        var merged = new List<TranscriptSegment>();

        foreach (var (name, speaker) in new[] { ("mic", "me"), ("system", "them") })
        {
            if (!metadata.Files.TryGetValue(name, out var fileName))
            {
                continue;
            }

            var audioPath = Path.Combine(sessionDirectory, fileName);
            if (!File.Exists(audioPath))
            {
                Log(sessionDirectory, $"missing track: {fileName}");
                continue;
            }

            // Las pistas se procesan en serie, asi que un solo hilo escribe en la lista.
            var index = tracks.Count;
            tracks.Add(new TrackProgress(name, 0d, false));
            var reporter = new TrackReporter(fraction =>
            {
                tracks[index] = new TrackProgress(name, fraction, false);
                ProgressChanged?.Invoke(new TranscriptionUpdate(
                    sessionDirectory, SessionState.Running, _pending.Count, tracks.ToArray(), null, clock.Elapsed));
            });

            try
            {
                Log(sessionDirectory, $"transcribing {fileName} with Whisper large-v3");
                var offset = metadata.StartOffsetMs.GetValueOrDefault(name);
                merged.AddRange(await transcriber.TranscribeAsync(audioPath, speaker, offset, reporter, cancellationToken));
                tracks[index] = new TrackProgress(name, 1d, true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log(sessionDirectory, $"skipping {fileName}: {exception.Message}");
                tracks[index] = tracks[index] with { Completed = true };
            }
        }

        merged.Sort(static (left, right) => left.StartMs.CompareTo(right.StartMs));
        new TranscriptDocument
        {
            Engine = "whisper.cpp",
            Model = "large-v3-q5_0",
            CreatedAt = DateTimeOffset.UtcNow,
            Segments = merged,
        }.Write(sessionDirectory);
        Log(sessionDirectory, $"done: {merged.Count} segments");
    }

    /// <summary>
    /// Fallar al escribir el estado no puede interrumpir una transcripcion: el
    /// estado siempre es rederivable de que archivos existen.
    /// </summary>
    private static void WriteStatus(string sessionDirectory, string state, double? elapsedSeconds, string? error)
    {
        try
        {
            new SessionStatus
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Model = Path.GetFileNameWithoutExtension(ConfigStore.ModelPath),
                ElapsedSeconds = elapsedSeconds,
                Error = error,
            }.Write(sessionDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(sessionDirectory, $"could not write status.json: {exception.Message}");
        }
    }

    private void PublishState(
        string sessionDirectory,
        SessionState state,
        List<TrackProgress> tracks,
        string? error,
        TimeSpan elapsed) =>
        StateChanged?.Invoke(new TranscriptionUpdate(
            sessionDirectory, state, _pending.Count, tracks.ToArray(), error, elapsed));

    private static void Log(string sessionDirectory, string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(sessionDirectory, "transcribe.log"),
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Perder una linea de log no justifica abortar la transcripcion.
        }
    }

    private sealed class TrackReporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
```

Nota: se elimina el record `TranscriptionStatus` que estaba al final del archivo.

- [ ] **Step 3: Adaptar TrayApplication al nuevo evento**

En `Windows/Quill.Windows/UI/TrayApplication.cs`, sustituir la línea 61:

```csharp
        _transcription.StatusChanged += status => _application.Dispatcher.BeginInvoke(() => ShowTranscriptionStatus(status));
```

por:

```csharp
        _transcription.StateChanged += update => _application.Dispatcher.BeginInvoke(() => ShowTranscriptionStatus(update));
```

Y sustituir el método `ShowTranscriptionStatus` (líneas 159-180) por:

```csharp
    private void ShowTranscriptionStatus(TranscriptionUpdate update)
    {
        var name = Path.GetFileName(update.SessionDirectory);
        switch (update.State)
        {
            case SessionState.Queued:
            case SessionState.Running:
                _icon.Text = update.Queued > 0
                    ? $"quill - transcribiendo ({update.Queued} en cola)"
                    : "quill - transcribiendo";
                break;

            case SessionState.Done:
                _icon.Text = "quill - listo";
                _icon.ShowBalloonTip(4_000, "quill", $"Transcripcion lista: {name}", Forms.ToolTipIcon.Info);
                break;

            case SessionState.Failed:
                _icon.Text = "quill - error de transcripcion";
                _icon.ShowBalloonTip(6_000, "quill - error", $"{name}: {update.Error}", Forms.ToolTipIcon.Error);
                break;
        }
    }
```

Añadir el `using` que falta al principio del archivo, junto a los existentes:

```csharp
using Quill.Windows.Sessions;
```

- [ ] **Step 4: Compilar y correr toda la suite**

Run: `dotnet build Windows/Quill.Windows/Quill.Windows.csproj -c Debug`
Expected: BUILD SUCCESSFUL

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`
Expected: PASS, 28 tests.

- [ ] **Step 5: Commit**

```bash
git add Windows/Quill.Windows/Transcription/ Windows/Quill.Windows/UI/TrayApplication.cs
git commit -m "Split transcription events by frequency and persist session status"
```

---

### Task 6: ViewModels

**Files:**
- Create: `Windows/Quill.Windows/UI/SessionRowViewModel.cs`
- Create: `Windows/Quill.Windows/UI/SessionsViewModel.cs`

**Interfaces:**
- Consumes: `SessionCatalog.Scan` y `SessionSnapshot` (Task 3), `TranscriptionCoordinator.StateChanged`/`ProgressChanged`/`Retry` y `TranscriptionUpdate` (Task 5).
- Produces: `SessionRowViewModel` con propiedades públicas `Name`, `StateText`, `StateGlyph`, `DurationText`, `ElapsedText`, `ErrorText`, `MicProgress`, `SystemProgress`, `EtaText`, `IsRunning`, `IsFailed`, `IsDone`, `HasError`, `Directory`, y el método `Apply(TranscriptionUpdate)`; `SessionsViewModel` con `ObservableCollection<SessionRowViewModel> Sessions`, comandos `RetryCommand`, `OpenFolderCommand`, `OpenTranscriptCommand`, `OpenLogCommand`, y métodos `Refresh()`, `SetActiveSession(string?)`, `Start()`, `Stop()`.

- [ ] **Step 1: Crear SessionRowViewModel**

Crear `Windows/Quill.Windows/UI/SessionRowViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;

namespace Quill.Windows.UI;

internal sealed class SessionRowViewModel : INotifyPropertyChanged
{
    private SessionState _state;
    private double _micProgress;
    private double _systemProgress;
    private TimeSpan _elapsed;
    private string? _error;
    private TimeSpan? _transcriptionElapsed;

    public SessionRowViewModel(SessionSnapshot snapshot)
    {
        Directory = snapshot.Directory;
        Name = snapshot.Name;
        Duration = snapshot.Duration;
        Model = snapshot.Model;
        _state = snapshot.State;
        _error = snapshot.Error;
        _transcriptionElapsed = snapshot.TranscriptionElapsed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Directory { get; }

    public string Name { get; }

    public TimeSpan Duration { get; }

    public string? Model { get; }

    public string DurationText => Duration > TimeSpan.Zero ? Format(Duration) : "--:--";

    public bool IsRunning => _state == SessionState.Running;

    public bool IsFailed => _state == SessionState.Failed;

    public bool IsDone => _state == SessionState.Done;

    public bool HasError => IsFailed && !string.IsNullOrWhiteSpace(_error);

    public string? ErrorText => _error;

    public double MicProgress => _micProgress * 100d;

    public double SystemProgress => _systemProgress * 100d;

    public string StateGlyph => _state switch
    {
        SessionState.Recording => "●",
        SessionState.Abandoned => "⚠",
        SessionState.Queued => "⏳",
        SessionState.Running => "⚙",
        SessionState.Done => "✓",
        SessionState.Failed => "✗",
        _ => "?",
    };

    public string StateText => _state switch
    {
        SessionState.Recording => "GRABANDO",
        SessionState.Abandoned => "incompleta",
        SessionState.Queued => "en cola",
        SessionState.Running => "transcribiendo",
        SessionState.Done => "listo",
        SessionState.Failed => "error",
        _ => string.Empty,
    };

    public string ElapsedText => _transcriptionElapsed is { } elapsed
        ? $"transcrito en {Format(elapsed)}{(Model is null ? string.Empty : $" - {Model}")}"
        : string.Empty;

    /// <summary>
    /// Estimacion a partir del avance observado: se autocorrige y no necesita saber
    /// nada del modelo ni del hardware. Oculta por debajo del 5 % para no mostrar
    /// numeros absurdos en los primeros segundos.
    /// </summary>
    public string EtaText
    {
        get
        {
            var fraction = (_micProgress + _systemProgress) / 2d;
            if (!IsRunning || fraction < 0.05d || _elapsed <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var remaining = TimeSpan.FromSeconds(_elapsed.TotalSeconds / fraction * (1d - fraction));
            return $"~{Format(remaining)} restantes";
        }
    }

    public void Apply(TranscriptionUpdate update)
    {
        _state = update.State;
        _error = update.Error;
        _elapsed = update.Elapsed;

        foreach (var track in update.Tracks)
        {
            if (track.Track == "mic")
            {
                _micProgress = track.Fraction;
            }
            else if (track.Track == "system")
            {
                _systemProgress = track.Fraction;
            }
        }

        if (update.State == SessionState.Done)
        {
            _micProgress = 1d;
            _systemProgress = 1d;
            _transcriptionElapsed = update.Elapsed;
        }

        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
                 {
                     nameof(StateText), nameof(StateGlyph), nameof(IsRunning), nameof(IsFailed),
                     nameof(IsDone), nameof(HasError), nameof(ErrorText), nameof(MicProgress),
                     nameof(SystemProgress), nameof(EtaText), nameof(ElapsedText),
                 })
        {
            Raise(property);
        }
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{value.Minutes}:{value.Seconds:D2}";
}
```

- [ ] **Step 2: Crear SessionsViewModel**

Crear `Windows/Quill.Windows/UI/SessionsViewModel.cs`:

```csharp
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Quill.Windows.Sessions;
using Quill.Windows.Transcription;

namespace Quill.Windows.UI;

internal sealed class SessionsViewModel : IDisposable
{
    private readonly string _recordingsRoot;
    private readonly TranscriptionCoordinator _transcription;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pump;
    private readonly ConcurrentDictionary<string, TranscriptionUpdate> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSessionDirectory;

    public SessionsViewModel(string recordingsRoot, TranscriptionCoordinator transcription, Dispatcher dispatcher)
    {
        _recordingsRoot = recordingsRoot;
        _transcription = transcription;
        _dispatcher = dispatcher;

        RetryCommand = new RelayCommand(directory => _transcription.Retry((string)directory!));
        OpenFolderCommand = new RelayCommand(directory => Open((string)directory!));
        OpenTranscriptCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcript.md")));
        OpenLogCommand = new RelayCommand(directory => Open(Path.Combine((string)directory!, "transcribe.log")));

        // El coordinador publica sin restriccion. Acumulamos aqui y volcamos a la
        // vista a ritmo fijo: marshalar cada segmento al dispatcher la atascaria.
        _pump = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pump.Tick += (_, _) => Drain();

        _transcription.StateChanged += OnUpdate;
        _transcription.ProgressChanged += OnUpdate;
    }

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    public ICommand RetryCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand OpenTranscriptCommand { get; }

    public ICommand OpenLogCommand { get; }

    public void Start() => _pump.Start();

    public void Stop() => _pump.Stop();

    public void SetActiveSession(string? sessionDirectory)
    {
        _activeSessionDirectory = sessionDirectory;
        Refresh();
    }

    /// <summary>Reconstruye la lista leyendo el disco. Solo al abrir la ventana o al pulsar refrescar.</summary>
    public void Refresh()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Refresh);
            return;
        }

        Sessions.Clear();
        foreach (var snapshot in SessionCatalog.Scan(_recordingsRoot, _activeSessionDirectory))
        {
            Sessions.Add(new SessionRowViewModel(snapshot));
        }

        Drain();
    }

    public void Dispose()
    {
        _transcription.StateChanged -= OnUpdate;
        _transcription.ProgressChanged -= OnUpdate;
        _pump.Stop();
    }

    private void OnUpdate(TranscriptionUpdate update) =>
        _inFlight[update.SessionDirectory] = update;

    private void Drain()
    {
        foreach (var row in Sessions)
        {
            if (_inFlight.TryGetValue(row.Directory, out var update))
            {
                row.Apply(update);
            }
        }
    }

    private static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
```

- [ ] **Step 3: Compilar**

Run: `dotnet build Windows/Quill.Windows/Quill.Windows.csproj -c Debug`
Expected: BUILD SUCCESSFUL

- [ ] **Step 4: Commit**

```bash
git add Windows/Quill.Windows/UI/SessionRowViewModel.cs Windows/Quill.Windows/UI/SessionsViewModel.cs
git commit -m "Add view models for the sessions window"
```

---

### Task 7: La ventana y su integración con la bandeja

**Files:**
- Create: `Windows/Quill.Windows/UI/SessionsWindow.xaml`
- Create: `Windows/Quill.Windows/UI/SessionsWindow.xaml.cs`
- Modify: `Windows/Quill.Windows/UI/TrayApplication.cs`

**Interfaces:**
- Consumes: `SessionsViewModel` y `SessionRowViewModel` (Task 6).
- Produces: `SessionsWindow(SessionsViewModel viewModel)`; en `TrayController`, el método privado `ShowSessions()`.

- [ ] **Step 1: Crear la ventana**

Crear `Windows/Quill.Windows/UI/SessionsWindow.xaml`:

```xml
<Window x:Class="Quill.Windows.UI.SessionsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Sesiones de quill"
        Height="560" Width="520"
        WindowStartupLocation="CenterScreen"
        Background="#FFFAFAFA">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisibility" />
    </Window.Resources>

    <DockPanel Margin="16">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Refrescar" Padding="14,6" Click="OnRefresh" />
        </StackPanel>

        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Sessions}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Background="White" BorderBrush="#FFE0E0E0" BorderThickness="1"
                                CornerRadius="6" Padding="12" Margin="0,0,0,8">
                            <StackPanel>
                                <DockPanel>
                                    <TextBlock Text="{Binding StateGlyph}" FontSize="14" Margin="0,0,8,0" />
                                    <TextBlock Text="{Binding StateText}" DockPanel.Dock="Right"
                                               Foreground="#FF666666" />
                                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                                </DockPanel>

                                <TextBlock Text="{Binding DurationText}" Foreground="#FF999999"
                                           FontSize="11" Margin="22,2,0,0" />

                                <StackPanel Margin="22,8,0,0"
                                            Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}">
                                    <DockPanel Margin="0,0,0,4">
                                        <TextBlock Text="mic" Width="52" Foreground="#FF666666" FontSize="11" />
                                        <ProgressBar Value="{Binding MicProgress, Mode=OneWay}" Maximum="100" Height="8" />
                                    </DockPanel>
                                    <DockPanel Margin="0,0,0,4">
                                        <TextBlock Text="system" Width="52" Foreground="#FF666666" FontSize="11" />
                                        <ProgressBar Value="{Binding SystemProgress, Mode=OneWay}" Maximum="100" Height="8" />
                                    </DockPanel>
                                    <TextBlock Text="{Binding EtaText}" Foreground="#FF999999" FontSize="11" />
                                </StackPanel>

                                <TextBlock Text="{Binding ErrorText}" Foreground="#FFB00020" FontSize="11"
                                           TextWrapping="Wrap" Margin="22,6,0,0"
                                           Visibility="{Binding HasError, Converter={StaticResource BoolToVisibility}}" />

                                <TextBlock Text="{Binding ElapsedText}" Foreground="#FF999999" FontSize="11"
                                           Margin="22,6,0,0"
                                           Visibility="{Binding IsDone, Converter={StaticResource BoolToVisibility}}" />

                                <StackPanel Orientation="Horizontal" Margin="22,8,0,0">
                                    <Button Content="Abrir carpeta" Padding="10,4" Margin="0,0,6,0"
                                            Command="{Binding DataContext.OpenFolderCommand,
                                                      RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding Directory}" />
                                    <Button Content="Ver transcript" Padding="10,4" Margin="0,0,6,0"
                                            Visibility="{Binding IsDone, Converter={StaticResource BoolToVisibility}}"
                                            Command="{Binding DataContext.OpenTranscriptCommand,
                                                      RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding Directory}" />
                                    <Button Content="Reintentar" Padding="10,4" Margin="0,0,6,0"
                                            Visibility="{Binding IsFailed, Converter={StaticResource BoolToVisibility}}"
                                            Command="{Binding DataContext.RetryCommand,
                                                      RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding Directory}" />
                                    <Button Content="Log" Padding="10,4"
                                            Visibility="{Binding IsFailed, Converter={StaticResource BoolToVisibility}}"
                                            Command="{Binding DataContext.OpenLogCommand,
                                                      RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding Directory}" />
                                </StackPanel>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </DockPanel>
</Window>
```

Crear `Windows/Quill.Windows/UI/SessionsWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;

namespace Quill.Windows.UI;

internal sealed partial class SessionsWindow : Window
{
    private readonly SessionsViewModel _viewModel;

    public SessionsWindow(SessionsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Es una app de bandeja: cerrar oculta, no destruye.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            _viewModel.Stop();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Solo el apagado de la aplicacion cierra la ventana de verdad.</summary>
    public bool AllowClose { get; set; }

    public void ShowRefreshed()
    {
        _viewModel.Refresh();
        _viewModel.Start();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _viewModel.Refresh();
}
```

- [ ] **Step 2: Integrar en TrayApplication**

En `Windows/Quill.Windows/UI/TrayApplication.cs`, dentro de `TrayController`, añadir dos campos junto a los existentes:

```csharp
    private readonly SessionsViewModel _sessionsViewModel;
    private SessionsWindow? _sessionsWindow;
```

En el constructor, sustituir el bloque del menú (líneas 41-46) por:

```csharp
        var menu = new Forms.ContextMenuStrip();
        _toggle = new Forms.ToolStripMenuItem("Iniciar grabacion", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripMenuItem("Ver sesiones", null, (_, _) => ShowSessions()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir grabaciones", null, (_, _) => OpenRecordings()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (_, _) => _application.Shutdown()));
```

Sustituir la línea 55 para que el doble clic abra la ventana en lugar de grabar:

```csharp
        _icon.DoubleClick += (_, _) => ShowSessions();
```

Después de `_transcription = new TranscriptionCoordinator(recordingsRoot);` y antes de `_transcription.ResumePending();`, añadir:

```csharp
        _sessionsViewModel = new SessionsViewModel(recordingsRoot, _transcription, _application.Dispatcher);
```

Añadir el método `ShowSessions` junto a `OpenRecordings`:

```csharp
    private void ShowSessions()
    {
        _sessionsWindow ??= new SessionsWindow(_sessionsViewModel);
        _sessionsWindow.ShowRefreshed();
    }
```

En `StartRecording`, después de `_ticker.Start();`, añadir:

```csharp
            _sessionsViewModel.SetActiveSession(_session.Directory);
```

En `StopRecording`, dentro del bloque `try` después de `session.Stop();`, añadir:

```csharp
            _sessionsViewModel.SetActiveSession(null);
```

En `Dispose`, antes de `_transcription.Dispose();`, añadir:

```csharp
        if (_sessionsWindow is not null)
        {
            _sessionsWindow.AllowClose = true;
            _sessionsWindow.Close();
            _sessionsWindow = null;
        }

        _sessionsViewModel.Dispose();
```

- [ ] **Step 3: Compilar y correr la suite**

Run: `dotnet build Windows/Quill.Windows/Quill.Windows.csproj -c Debug`
Expected: BUILD SUCCESSFUL

Si el build falla con `SessionsWindow does not contain a definition for InitializeComponent`, el SDK no recogió el XAML por convención. Añadir a `Quill.Windows.csproj` un `ItemGroup` con `<Page Update="UI\SessionsWindow.xaml" Generator="MSBuild:Compile" SubType="Designer" />`.

Run: `dotnet test Windows/Quill.Windows.Tests/Quill.Windows.Tests.csproj`
Expected: PASS, 28 tests.

- [ ] **Step 4: Smoke test manual en Windows 11**

1. `dotnet run --project Windows/Quill.Windows/Quill.Windows.csproj`
2. Doble clic en el icono de bandeja → se abre la ventana y **no** empieza ninguna grabación.
3. Clic derecho → `Iniciar grabacion`. La ventana muestra la sesión como `GRABANDO`.
4. `Detener grabacion`. La sesión pasa a `en cola` y luego a `transcribiendo`, con las barras avanzando y una ETA que converge.
5. Cerrar la ventana con la X → se oculta, el icono sigue vivo.
6. Al terminar, la sesión muestra `listo` con `transcrito en M:SS - ggml-large-v3-q5_0`.

- [ ] **Step 5: Commit**

```bash
git add Windows/Quill.Windows/UI/
git commit -m "Add sessions window with live transcription progress"
```

---

### Task 8: Documentación

**Files:**
- Modify: `README.md:147-149` (Quickstart), `README.md:216-224` (tabla de archivos), `README.md:258-287` (estructura), `README.md:324-331` (smoke test)

**Interfaces:**
- Consumes: todo lo anterior. No produce interfaces nuevas.

- [ ] **Step 1: Actualizar la tabla de archivos locales**

En `README.md`, en la tabla de la sección "Local data", añadir una fila después de la de `meta.json`:

```markdown
| `status.json` | Transcription state, model used, and elapsed time. |
```

- [ ] **Step 2: Documentar el estado persistido**

En la misma sección, sustituir el párrafo que empieza por `If Windows or Quill closes while transcription is running` por:

```markdown
If Windows or Quill closes while transcription is running, the session retains `meta.json`
but has no `transcript.json`; Quill queues it when it next starts. Transcripts are written
atomically, so their presence means the output was completed.

`status.json` records the outcome so it survives a restart. A session marked `failed` is
not queued again automatically: open the sessions window and select `Reintentar`. The file
is a convenience, not the source of truth — deleting it makes Quill derive the state from
which files exist, exactly as it did before.
```

- [ ] **Step 3: Actualizar el Quickstart**

Sustituir los pasos 3-5 de "Use a portable build":

```markdown
3. Right-click the Quill tray icon and select `Iniciar grabacion`.
4. Select `Detener grabacion` when the meeting ends.
5. Double-click the tray icon to open the sessions window and watch transcription progress.
```

- [ ] **Step 4: Actualizar la estructura del proyecto**

En el bloque de "Project structure", sustituir las entradas de `Sessions/`, `Transcription/` y `UI/` por:

```text
|   |-- Sessions/
|   |   |-- SessionCatalog.cs
|   |   |-- SessionDocuments.cs
|   |   `-- SessionStatus.cs
|   |-- Transcription/
|   |   |-- ResumePolicy.cs
|   |   |-- TranscriptionCoordinator.cs
|   |   |-- TranscriptionUpdate.cs
|   |   `-- WhisperTranscriber.cs
|   |-- UI/
|   |   |-- SessionRowViewModel.cs
|   |   |-- SessionsViewModel.cs
|   |   |-- SessionsWindow.xaml
|   |   `-- TrayApplication.cs
```

Y añadir después del bloque `Windows/Quill.Windows/`:

```text
|-- Windows/Quill.Windows.Tests/
```

- [ ] **Step 5: Actualizar la tabla de estado y el smoke test**

En la tabla "Status", añadir una fila después de "Pending-job recovery":

```markdown
| Sessions window | Implemented | WPF window with live per-track progress |
```

En "Verification", añadir al final de la lista del smoke test manual:

```markdown
6. Double-click the tray icon and confirm the sessions window lists the session with live progress.
7. Confirm a failed session shows `error` after a restart and can be retried from the window.
```

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "Document status.json and the sessions window"
```

---

## Self-Review

**Cobertura del spec:**

| Requisito del spec | Task |
|---|---|
| `status.json` con `state`, `updated_at`, `model`, `elapsed_seconds`, `error` | 1 |
| `State` como string tolerante a valores desconocidos | 1, 3 |
| `SessionCatalog` con `Recording`/`Abandoned` y `activeSessionDirectory` | 3 |
| Precedencia de derivación del estado | 3 |
| Orden descendente por nombre | 3 |
| `NormalizeToWhisperWav` devuelve `TimeSpan` | 4 |
| `TranscribeAsync` con `IProgress<double>` | 4 |
| `StateChanged` / `ProgressChanged` partidos por frecuencia | 5 |
| `TranscriptionStatus` eliminado y sustituido por `TranscriptionUpdate` | 5 |
| `Retry(string)` | 5 |
| Regla de reanudación con `ResumePolicy` | 2, 5 |
| Escritura de `status.json` en transiciones | 5 |
| Fallo al escribir `status.json` no interrumpe | 5 |
| Throttling con `DispatcherTimer` de 250 ms | 6 |
| ETA a partir del avance observado, desde el 5 % | 6 |
| Ventana XAML + MVVM | 7 |
| Ítem "Ver sesiones" y doble clic | 7 |
| `TrayApplication` expone la sesión activa | 7 |
| Cerrar oculta (`e.Cancel = true` + `Hide()`) | 7 |
| Comandos comprueban existencia antes de actuar | 6 (`Open`), 5 (`Retry`) |
| Tests de las tres piezas de lógica pura | 1, 2, 3 |
| Criterios de éxito 1-7 | 7 (smoke test), 1-3 (tests) |

**Coherencia de tipos:** `SessionState` se define en la Task 3 y se consume en 5, 6 y 7 con los mismos seis valores. `TrackProgress.Track` usa las claves `"mic"` y `"system"`, que son las mismas del `foreach` de `TranscribeSessionAsync` y las que `SessionRowViewModel.Apply` compara. `SessionStatus.PathFor` se define en la Task 1 y se usa en `Retry` (Task 5). `TranscribeAsync` gana el parámetro `IProgress<double>?` en la Task 4 y se llama con `reporter` en la Task 5.

**Nota de ejecución:** la Task 4 deja `TranscriptionCoordinator.cs` temporalmente roto entre sus pasos 3 y 5 porque la firma de `TranscribeAsync` cambia antes de que la Task 5 reescriba a su llamador. El paso 4 de esa tarea incluye la corrección de una línea para mantener el árbol compilable en cada commit.
