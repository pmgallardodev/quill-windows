# Ventana de sesiones con progreso de transcripción

Fecha: 2026-08-03
Estado: aprobado, pendiente de plan de implementación

## Problema

Toda la visibilidad del proceso de transcripción vive hoy en el tooltip del icono de
bandeja y en globos de notificación (`UI/TrayApplication.cs:159-180`). El modelo de
estado es binario:

```csharp
internal sealed record TranscriptionStatus(string SessionDirectory, int Queued, bool Finished, string? Error);
```

De ahí se derivan tres carencias:

1. **No hay noción de avance.** Una transcripción de una hora muestra el mismo texto en
   el minuto 1 y en el minuto 40. El usuario no puede distinguir "va lento" de "se colgó".
2. **Los errores son efímeros.** El mensaje de fallo viaja al tooltip y muere ahí. Nada
   en disco recuerda que una sesión falló ni por qué.
3. **`ResumePending()` reintenta en bucle.** El método (`Transcription/TranscriptionCoordinator.cs:32-39`)
   reencola toda sesión con `meta.json` y sin `transcript.json`. Una sesión que falla de
   forma permanente —`meta.json` corrupto, modelo ausente en `ConfigStore.ModelPath`— se
   reintenta en cada arranque indefinidamente, y el usuario solo lo percibe como un globo
   de error que desaparece en seis segundos.

El `transcribe.log` sí registra el progreso (`TranscriptionCoordinator.cs:148`), pero hay
que abrirlo a mano con el Explorador de archivos.

## Alcance

Una ventana WPF que lista las sesiones del disco con su estado, y actualiza en vivo el
porcentaje de la que se está transcribiendo.

Incluye:

- Progreso real por pista (`mic`, `system`) durante la transcripción.
- Historial de sesiones con estado: grabando, en cola, transcribiendo, listo, error.
- Estado persistido en `status.json` por sesión.
- Reintentar una sesión fallida desde la ventana.
- ETA estimada a partir del avance observado.
- Abrir el `transcribe.log` de una sesión.
- Corrección del bucle de reintentos de `ResumePending()`.

Queda fuera:

- **Lector de transcripts dentro de la app.** Los botones abren `transcript.md` con la
  aplicación predeterminada del sistema, como hoy.
- **Selección y aceleración de modelos.** Es un segundo entregable con su propio spec
  (ver "Trabajo posterior").

## Decisiones tomadas

| Decisión | Elección | Razón |
|---|---|---|
| Persistencia del estado | `status.json` por sesión | Mantiene la carpeta de sesión autocontenida y portable, como promete el README. Un índice global se desincroniza al mover o borrar carpetas a mano, y se complica con `--out`. |
| Origen de las actualizaciones | Eventos en proceso | `FileSystemWatcher` rebota con la escritura atómica (`.tmp` + `File.Move` genera `Created`, `Changed` y `Renamed` por escritura). El polling obligaría a escribir en disco por cada segmento. |
| Gesto de doble clic | Abre la ventana | Convencional en apps de bandeja y, sobre todo, elimina el riesgo de cortar una reunión por un doble clic accidental. Grabar/detener queda en el menú contextual. |
| Tecnología de UI | XAML + MVVM ligero | Una lista con estados variables y barras de progreso animadas es el caso para el que existe el data binding de WPF. `UseWPF` ya está activo, así que no añade dependencias. |

### Separación entre progreso efímero y estado durable

Es la decisión estructural de la que se derivan las demás:

| | Progreso efímero | Estado durable |
|---|---|---|
| Qué es | `mic 78 %`, ETA, spinner | `queued` / `running` / `done` / `failed` + error |
| Dónde vive | Memoria, evento en proceso | `status.json` en disco |
| Frecuencia | Muchas veces por segundo | Solo en transiciones (5-6 por sesión) |
| Si se cierra la app | Se pierde, y es irrelevante | Sobrevive — es su razón de ser |

`status.json` **no** es la fuente de verdad. El disco sigue mandando: si el archivo
desaparece o se corrompe, `SessionCatalog` deriva el estado de qué archivos existen,
igual que hoy. `status.json` solo enriquece con lo que el disco no puede saber: el porqué
de un fallo, el modelo usado y cuánto tardó.

## Componentes nuevos

| Archivo | Responsabilidad | Depende de |
|---|---|---|
| `Sessions/SessionStatus.cs` | Lee y escribe `status.json`. Serialización y nada más. | `AtomicFile` |
| `Sessions/SessionCatalog.cs` | Escanea la carpeta y devuelve las sesiones con su estado. Función pura sobre el disco, sin estado propio. | `SessionStatus`, `SessionMetadata` |
| `UI/SessionsWindow.xaml` + `.xaml.cs` | La ventana. | `SessionsViewModel` |
| `UI/SessionsViewModel.cs` | Puente entre el catálogo, los eventos del coordinador y la vista. | `SessionCatalog`, `TranscriptionCoordinator` |
| `UI/SessionRowViewModel.cs` | Una fila: estado, progreso por pista, ETA, comandos. | — |

### `SessionStatus`

Espeja el patrón de `Sessions/SessionDocuments.cs`: mismo estilo de `[JsonPropertyName]`
y misma escritura atómica vía `AtomicFile.Write`.

```csharp
internal sealed class SessionStatus
{
    [JsonPropertyName("state")]           public required string State { get; init; }
    [JsonPropertyName("updated_at")]      public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("model")]           public string? Model { get; init; }
    [JsonPropertyName("elapsed_seconds")] public double? ElapsedSeconds { get; init; }
    [JsonPropertyName("error")]           public string? Error { get; init; }

    public static SessionStatus? TryRead(string sessionDirectory);
    public void Write(string sessionDirectory);
}
```

`State` es `string` y no `enum` de forma deliberada: un `status.json` escrito por una
versión futura con un estado desconocido debe degradarse a "estado no reconocido", no
reventar la deserialización de toda la ventana. `SessionCatalog` traduce el string al
enum interno y trata cualquier valor desconocido como si el archivo no existiera.

Valores válidos de `state`: `queued`, `running`, `done`, `failed`.

`model` y `elapsed_seconds` son los dos números necesarios para comparar modelos con
datos en el entregable posterior.

Ejemplo:

```json
{
  "state": "failed",
  "updated_at": "2026-08-01T16:04:11Z",
  "model": "large-v3-q5_0",
  "elapsed_seconds": 187.4,
  "error": "no se encontro el modelo Whisper incluido"
}
```

### `SessionCatalog`

`SessionState` vive en `Sessions/` porque lo usan tanto el catálogo como los eventos de
transcripción.

```csharp
internal enum SessionState { Recording, Abandoned, Queued, Running, Done, Failed }

internal sealed record SessionSnapshot(
    string Directory,
    string Name,
    DateTimeOffset Started,
    TimeSpan Duration,
    SessionState State,
    string? Model,
    string? Error,
    TimeSpan? TranscriptionElapsed);

internal static class SessionCatalog
{
    public static IReadOnlyList<SessionSnapshot> Scan(
        string recordingsRoot,
        string? activeSessionDirectory);
}
```

Derivación del estado, en orden de precedencia:

```
es activeSessionDirectory            → Recording
sin meta.json                        → Abandoned
transcript.json existe               → Done
status.json legible y reconocido     → su estado
en cualquier otro caso               → Queued
```

`Recording` **no** se deriva de la ausencia de `meta.json`. `RecordingSession` solo
escribe ese archivo en `Stop()` (`Audio/RecordingSession.cs:119`), así que una sesión
interrumpida por un cierre brusco —crash, corte de luz, `taskkill`— queda con los WAV y
sin `meta.json` de forma permanente. Si `Recording` se dedujera de esa ausencia, la
ventana mostraría una fila «grabando» fantasma para siempre. Solo el proceso actual sabe
qué sesión está grabando de verdad, y por eso `Scan` recibe esa ruta.

`Abandoned` se muestra como sesión incompleta, sin barra de progreso y sin encolarse para
transcripción. La única acción disponible es abrir su carpeta. Reparar la cabecera de un
WAV que `WaveFileWriter` nunca cerró queda fuera de alcance.

`Recording` y `Abandoned` son estados **derivados**: nunca se escriben en `status.json`,
cuyos únicos valores válidos siguen siendo `queued`, `running`, `done` y `failed`.

Ordena por nombre de carpeta descendente (`yyyy.MM.dd-HHmm` ordena cronológicamente como
texto), de modo que lo más reciente queda arriba.

## Cambios en el código existente

### `Transcription/WhisperTranscriber.cs`

`NormalizeToWhisperWav` pasa de `void` a devolver `TimeSpan`. El valor ya está disponible
en `AudioFileReader.TotalTime` y hoy se descarta. Es más correcto que
`meta.json.duration_seconds` porque las pistas tienen offsets distintos
(`start_offset_ms`) y pueden no durar lo mismo.

```csharp
private static TimeSpan NormalizeToWhisperWav(string inputPath, string outputPath)

public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
    string audioPath,
    string speaker,
    long offsetMilliseconds,
    IProgress<double>? progress,   // nuevo
    CancellationToken cancellationToken)
```

Dentro del `await foreach` existente (`WhisperTranscriber.cs:37`), una línea:
`progress?.Report(result.End / total)`, acotada a `[0, 1]`.

### `Transcription/TranscriptionCoordinator.cs`

El record `TranscriptionStatus` se elimina y lo sustituye `TranscriptionUpdate`. El evento
único se parte en dos según su frecuencia:

```csharp
internal sealed record TrackProgress(string Track, double Fraction, bool Completed);

internal sealed record TranscriptionUpdate(
    string SessionDirectory,
    SessionState State,
    int Queued,
    IReadOnlyList<TrackProgress> Tracks,
    string? Error,
    TimeSpan Elapsed);

public event Action<TranscriptionUpdate>? StateChanged;     // ~5 veces por sesión
public event Action<TranscriptionUpdate>? ProgressChanged;  // cientos por minuto
```

Ambos llevan el mismo tipo de dato pero tienen contratos de coste opuestos. Un consumidor
que hace trabajo caro por evento —marshalling al hilo de UI, escritura a disco— se
suscribe solo al primero. Fusionarlos obligaría a cada consumidor a implementar su propio
throttling.

`TranscribeSessionAsync` escribe `status.json` en cada transición: `running` al empezar,
`done` o `failed` al terminar. `Enqueue` escribe `queued`.

Método nuevo:

```csharp
public void Retry(string sessionDirectory);
```

Borra el `status.json` en estado `failed` y reencola. Comprueba que el directorio existe
antes de actuar.

### Regla de reanudación en `ResumePending()`

Sustituye al reencolado incondicional actual y elimina la necesidad de un contador de
intentos con umbral:

```
sin meta.json      → sesión abandonada   → NO encolar (comportamiento actual)
status "running"   → cierre interrumpido → reencolar
status "failed"    → falló de verdad     → NO reencolar; mostrar ✗ y [Reintentar]
sin status.json    → sesión antigua      → reencolar
```

Un `running` huérfano no significa fallo, significa interrupción: la app se cerró a mitad
de la transcripción. Debe reencolarse. Un `failed` es una decisión que el usuario debe
tomar explícitamente.

El primer caso conserva la comprobación que ya hace `ResumePending()` hoy
(`File.Exists(meta.json)`): sin metadatos no hay offsets ni nombres de pista, así que no
hay nada que transcribir.

### `UI/TrayApplication.cs`

- Ítem "Ver sesiones" en el menú contextual, entre "Iniciar grabacion" y "Abrir grabaciones".
- `_icon.DoubleClick` pasa de `ToggleAsync` a abrir la ventana (`TrayApplication.cs:55`).
- Se suscribe únicamente a `StateChanged`; el tooltip conserva el comportamiento actual.
- Mantiene una sola instancia de la ventana y la reutiliza.
- Expone la ruta de la sesión que está grabando (`_session?.Directory`) para que
  `SessionCatalog.Scan` distinga `Recording` de `Abandoned`, y notifica al ViewModel al
  iniciar y detener una grabación.

## Flujo de datos

```
Whisper (hilo del pool)
     │  result.End / duración
     ▼
WhisperTranscriber ──IProgress<double>──► TranscriptionCoordinator
                                                │
                    ┌───────────────────────────┼──────────────────────┐
                    ▼                           ▼                      ▼
              status.json                 StateChanged           ProgressChanged
             (transiciones)                     │                (alta frecuencia)
                    │                           ▼                      │
                    │                    TrayApplication               │
                    │                       (tooltip)                  │
                    ▼                                                  ▼
          SessionCatalog.Scan ─────────────────────────────► SessionsViewModel
          (una sola vez, al abrir)                          (DispatcherTimer 250 ms)
                                                                       │
                                                                       ▼
                                                               SessionsWindow
```

La ventana lee el disco una vez al abrirse para construir el historial. A partir de ahí
se alimenta solo de eventos. Sin watchers, sin polling del sistema de archivos.

### Throttling

El coordinador publica `ProgressChanged` sin restricción: invocar un delegado en memoria
es barato y, con la ventana cerrada, es una comprobación de `null`.

El `SessionsViewModel` acumula el último valor recibido por sesión y vuelca a la vista con
un `DispatcherTimer` de 250 ms. Sin esto, una transcripción larga encolaría miles de
mensajes en el dispatcher de WPF y la ventana se atascaría.

### ETA

Se calcula del propio avance observado, sin conocer el modelo ni el hardware:

```
factor   = transcurrido / fracción
restante = factor × (1 − fracción)
```

Se autocorrige conforme avanza. Se muestra a partir del 5 % para no enseñar estimaciones
absurdas en los primeros segundos.

El progreso global de la sesión es la media de las fracciones de las pistas ponderada por
la duración de cada una.

## Manejo de errores

La ventana no debe ser nunca el motivo por el que algo se rompe:

| Situación | Comportamiento |
|---|---|
| `status.json` corrupto o con estado desconocido | `TryRead` devuelve `null`; el estado se deriva del disco. La ventana sigue funcionando. |
| Fallo al **escribir** `status.json` | Se anota en `transcribe.log` y la transcripción continúa. El estado siempre es rederivable de qué archivos existen. |
| Sesión borrada a mano con la ventana abierta | Los comandos comprueban `Directory.Exists` antes de actuar; al refrescar, la fila desaparece. |
| Cierre de la ventana | `e.Cancel = true` y `Hide()`. Es una app de bandeja: cerrar oculta, no destruye. |
| `Dispose` de la app con la ventana abierta | La ventana se cierra de verdad y se desuscribe de los eventos. |

## Pruebas

`Windows/Quill.Windows.Tests/` con xUnit, cubriendo las tres piezas donde vive la lógica
real. Todas son verificables sobre carpetas temporales, sin audio ni GPU:

- **`SessionCatalog.Scan`** — cada combinación de archivos presentes produce el estado
  esperado, incluidos los casos límite: carpeta vacía, `meta.json` sin `transcript.json`,
  `status.json` con estado desconocido, y carpeta sin `meta.json` con y sin coincidir con
  `activeSessionDirectory` (`Recording` frente a `Abandoned`).
- **`SessionStatus`** — ida y vuelta de serialización; tolerancia a JSON corrupto, a
  archivo vacío y a estados no reconocidos.
- **Regla de reanudación** — `running` reencola, `failed` no, ausencia de `status.json`
  reencola.

**Limitación conocida:** el proyecto es `net8.0-windows`, así que los tests solo se
ejecutan en Windows. El entorno de desarrollo actual es macOS, donde tampoco se puede
ejecutar la aplicación. Aislar la lógica en un `Quill.Core` multiplataforma sería posible,
pero es un refactor que este trabajo no requiere.

La UI y Whisper se siguen verificando con el smoke test manual que el README documenta en
la sección "Verification".

## Criterios de éxito

1. Con una transcripción en curso, la ventana muestra el porcentaje de cada pista
   avanzando y una ETA que converge.
2. Cerrar y reabrir la aplicación conserva el estado `failed` de una sesión, con su
   mensaje de error visible.
3. Una sesión en `failed` no se reencola sola en el arranque; el botón Reintentar la
   vuelve a procesar.
4. Cerrar la aplicación a mitad de una transcripción deja la sesión en `running`, y el
   siguiente arranque la reencola.
5. Borrar `status.json` a mano no rompe la ventana: el estado se deriva del disco.
6. El doble clic en el icono de bandeja abre la ventana y no altera ninguna grabación.
7. Una carpeta de sesión sin `meta.json` que no sea la grabación en curso se muestra como
   abandonada, no como «grabando», y no se encola para transcripción.

## Trabajo posterior

Segundo entregable, con su propio spec: **velocidad de transcripción**. Con `model` y
`elapsed_seconds` ya registrados por sesión, la ventana permite medir el antes y el
después en vez de estimarlo a ojo.

Palancas identificadas, todas disponibles en Whisper.net 1.9.1, la versión ya referenciada
en `Quill.Windows.csproj:19-20`:

| Palanca | Ganancia estimada |
|---|---|
| `large-v3-turbo-q5_0` en lugar de `large-v3-q5_0` | ~5-8× más rápido; el ZIP baja de 1.1 GB a 547 MB |
| VAD Silero (`WhisperVadProcessor`) | ~2×, porque el diseño de dos pistas hace que cada una sea mayoritariamente silencio |
| `.WithThreads(Environment.ProcessorCount)` | ~1.5-2×; hoy corre con los 4 hilos por defecto |
| `Whisper.net.Runtime.Vulkan` | ~3-10× donde haya GPU, con caída automática a CPU si no la hay |

Prerrequisito de ese trabajo: el nombre del modelo está incrustado en cuatro sitios y hay
que extraerlo a uno solo — `Infrastructure/ConfigStore.cs:51`, `Quill.Windows.csproj:28,32,36`
y `tools/Get-WhisperModel.ps1:3,10`.
