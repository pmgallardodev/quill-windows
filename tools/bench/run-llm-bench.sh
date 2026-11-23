#!/usr/bin/env bash
# Downloads each candidate GGUF, runs it over the corpus, then (with --delete) removes it so
# only one ~2.5 GB model is on disk at a time. A failed download or run is recorded and skipped.
#
#   tools/bench/run-llm-bench.sh [--delete] [--only m1,m2,m3] [label ...]
#
# Results land in tools/bench/results/<label>/ (gitignored); score with
#   dotnet run --project tools/bench/Quill.Bench -c Release -- score
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
CACHE="${QUILL_BENCH_CACHE:-$HOME/.cache/quill-bench}/llm"
mkdir -p "$CACHE"
DELETE=0
ONLY=()
WANT=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --delete) DELETE=1 ;;
    --only) ONLY=(--only "$2"); shift ;;
    *) WANT+=("$1") ;;
  esac
  shift
done

# label|repo/file (Hugging Face, Q4_K_M)
MODELS=(
  "qwen3-4b-instruct-2507|unsloth/Qwen3-4B-Instruct-2507-GGUF/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
  "gemma-3-4b-it|unsloth/gemma-3-4b-it-GGUF/gemma-3-4b-it-Q4_K_M.gguf"
  "phi-4-mini-instruct|unsloth/Phi-4-mini-instruct-GGUF/Phi-4-mini-instruct-Q4_K_M.gguf"
  "qwen3-8b|unsloth/Qwen3-8B-GGUF/Qwen3-8B-Q4_K_M.gguf"
)

dotnet build "$HERE/Quill.Bench" -c Release -nologo -v q || exit 1
BIN="$HERE/Quill.Bench/bin/Release/net8.0/Quill.Bench"

for entry in "${MODELS[@]}"; do
  label="${entry%%|*}"
  path="${entry#*|}"
  repo="${path%/*}"
  file="${path##*/}"
  if [[ ${#WANT[@]} -gt 0 && ! " ${WANT[*]} " =~ " $label " ]]; then continue; fi
  gguf="$CACHE/$file"
  if [[ ! -s "$gguf" ]]; then
    echo "== download $label"
    if ! curl -fSL --retry 3 -o "$gguf.part" "https://huggingface.co/$repo/resolve/main/$file"; then
      echo "!! download failed for $label" | tee -a "$HERE/results/failures.log"
      rm -f "$gguf.part"
      continue
    fi
    mv "$gguf.part" "$gguf"
  fi
  echo "== run $label"
  mkdir -p "$HERE/results"
  if ! "$BIN" summarize --model "$gguf" --label "$label" "${ONLY[@]}"; then
    echo "!! run failed for $label" | tee -a "$HERE/results/failures.log"
  fi
  if [[ $DELETE -eq 1 ]]; then rm -f "$gguf"; fi
done
