# Whisper model bundle

The Windows portable package must contain this file next to `quill.exe`:

```text
models/ggml-large-v3-turbo-q5_0.bin
```

It is the Spanish-capable Whisper `large-v3-turbo` model quantized to Q5_0. The
model is intentionally not committed because it is larger than 500 MB. Run
`tools/Get-WhisperModel.ps1` before building a release. The publishing script
copies it into the final ZIP, so end users do not download a model on first
launch.

The optional VAD package adds this file:

```text
models/ggml-silero-v5.1.2.bin
```

Use `tools/Publish-Portable.ps1 -IncludeVad` to download and include it.
