#!/usr/bin/env bash
# Downloads the small (non-LLM) models used by the feasibility checks into
# ~/.cache/quill-bench. LLM GGUFs are fetched one at a time by run-llm-bench.sh
# because a 4B Q4_K_M file is ~2.5 GB.
set -euo pipefail
ROOT="${QUILL_BENCH_CACHE:-$HOME/.cache/quill-bench}"
mkdir -p "$ROOT/whisper" "$ROOT/sherpa"

fetch() { # url dest
  if [[ -s "$2" ]]; then echo "have $(basename "$2")"; return; fi
  echo "get  $(basename "$2")"
  curl -fsSL --retry 3 -o "$2.part" "$1" && mv "$2.part" "$2"
}

HF=https://huggingface.co
GH=https://github.com/k2-fsa/sherpa-onnx/releases/download

fetch "$HF/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"      "$ROOT/whisper/ggml-small.bin"
fetch "$HF/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin" "$ROOT/whisper/ggml-small-q5_1.bin"
fetch "$HF/ggerganov/whisper.cpp/resolve/main/ggml-small-q8_0.bin" "$ROOT/whisper/ggml-small-q8_0.bin"
fetch "$HF/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" "$ROOT/whisper/ggml-silero-v5.1.2.bin"

fetch "$GH/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" \
      "$ROOT/sherpa/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2"
fetch "$GH/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx" \
      "$ROOT/sherpa/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
if [[ ! -d "$ROOT/sherpa/sherpa-onnx-pyannote-segmentation-3-0" ]]; then
  tar -xjf "$ROOT/sherpa/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" -C "$ROOT/sherpa"
fi
ls -la "$ROOT/whisper" "$ROOT/sherpa"
