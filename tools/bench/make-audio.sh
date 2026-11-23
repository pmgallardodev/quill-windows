#!/usr/bin/env bash
# macOS only: synthesizes Spanish test audio with `say` and converts it to 16 kHz mono WAV.
# Output goes to ~/.cache/quill-bench/audio (never committed).
#   es-speech.wav      ~75 s single voice (Mónica), for Whisper chunk latency
#   two-speakers.wav   alternating Mónica / Eddy turns, for the diarization smoke check
set -euo pipefail
OUT="${QUILL_BENCH_CACHE:-$HOME/.cache/quill-bench}/audio"
mkdir -p "$OUT"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

to_wav() { ffmpeg -loglevel error -y -i "$1" -ar 16000 -ac 1 -c:a pcm_s16le "$2"; }

TEXT="Buenos días a todos. Vamos a repasar el estado del proyecto antes de la entrega del jueves. \
El equipo de pagos ha terminado la integración con la nueva pasarela y las pruebas de carga han ido bien. \
Todavía tenemos un problema con las credenciales del entorno de pruebas del proveedor de mensajes, \
que caducaron la semana pasada. Elena se encarga de hablar con ellos hoy mismo. \
Por otro lado, la integración continua está tardando demasiado, casi cuarenta minutos por ejecución, \
y eso puede retrasar la publicación. Propongo congelar los cambios en la rama principal desde el miércoles \
por la tarde. Si nadie tiene objeciones, lo dejamos así y lo revisamos en la reunión del viernes. \
También quiero recordar que las vacaciones de agosto hay que comunicarlas antes de final de mes."
say -v "Mónica" -r 175 -o "$TMP/es.aiff" "$TEXT"
to_wav "$TMP/es.aiff" "$OUT/es-speech.wav"

i=0
LIST="$TMP/list.txt"
: > "$LIST"
while IFS='|' read -r voice line; do
  say -v "$voice" -r 180 -o "$TMP/t$i.aiff" "$line"
  to_wav "$TMP/t$i.aiff" "$TMP/t$i.wav"
  echo "file '$TMP/t$i.wav'" >> "$LIST"
  i=$((i + 1))
done <<'EOF'
Mónica|Hola Jorge, ¿cómo va la revisión del módulo de notificaciones?
Eddy|Bastante bien, me faltan un par de pruebas de integración pero mañana lo tengo.
Mónica|Perfecto. ¿Has visto el problema de las credenciales del proveedor?
Eddy|Sí, caducaron. Hoy escribo al soporte para que nos las renueven.
Mónica|Genial, avísame cuando lo tengas y lo probamos juntos.
Eddy|De acuerdo. Por cierto, la integración continua sigue lentísima.
Mónica|Lo sé, lo hablamos en la reunión del viernes con todo el equipo.
Eddy|Vale, pues hasta el viernes entonces.
EOF
ffmpeg -loglevel error -y -f concat -safe 0 -i "$LIST" -c copy "$OUT/two-speakers.wav"
ls -la "$OUT"
