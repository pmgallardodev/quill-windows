# Whisper model bundle

The Windows portable package must contain this file next to `quill.exe`:

```text
models/ggml-large-v3-q5_0.bin
```

It is the Spanish-capable Whisper `large-v3` model quantized to Q5_0. The
model is intentionally not committed because it is larger than 900 MB. Run
`tools/Get-WhisperModel.ps1` before building a release. The publishing script
copies it into the final ZIP, so end users do not download a model on first
launch.
