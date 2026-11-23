# Third-party notices

The Windows application is built from this repository. Its two release assets include the following third-party components, each under its own license. Consult each linked project for its complete license text, copyright lines and notices.

## App ZIP (`quill-<version>-win-x64.zip`)

| Component | Purpose | License | Source |
| --- | --- | --- | --- |
| .NET runtime and WPF | Self-contained Windows runtime and UI framework | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| NAudio | WASAPI capture, WAV processing and playback | Microsoft Public License (MS-PL) | https://github.com/naudio/NAudio |
| Whisper.net (+ CPU and Vulkan runtimes) | .NET binding for local speech recognition | MIT | https://github.com/sandrohanea/whisper.net |
| whisper.cpp / ggml | Native Whisper inference runtime | MIT | https://github.com/ggml-org/whisper.cpp |
| LLamaSharp (+ CPU and Vulkan backends) | .NET binding for the local language model | MIT | https://github.com/SciSharp/LLamaSharp |
| llama.cpp / ggml | Native LLM and embedding runtime | MIT | https://github.com/ggml-org/llama.cpp |
| sherpa-onnx | Speaker diarization and speaker embeddings | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime | Native runtime used by sherpa-onnx | MIT | https://github.com/microsoft/onnxruntime |
| WPF-UI | Fluent design system controls | MIT | https://github.com/lepoco/wpfui |
| OpenAI Whisper `large-v3-turbo` model (ggml Q5_0) | Final Spanish speech recognition | MIT | https://github.com/openai/whisper, https://huggingface.co/ggerganov/whisper.cpp |
| Silero VAD model (optional, `-IncludeVad`) | Silence skipping | MIT | https://github.com/snakers4/silero-vad |

Other NuGet dependencies resolved transitively are listed in `quill.deps.json` inside the package, with their licenses on https://www.nuget.org.

## AI pack ZIPs (`quill-ai-pack-<version>*.zip`)

The AI pack carries its own generated `THIRD-PARTY-NOTICES.md`, built from `tools/ai-pack.models.json`. With the default models:

| Model | Purpose | License | Source |
| --- | --- | --- | --- |
| Qwen3-4B-Instruct-2507 (GGUF UD-Q3_K_XL) | Summaries, catch-up and Ask | Apache-2.0 | https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507 |
| OpenAI Whisper `small` (ggml Q5_1) | Live transcription | MIT | https://github.com/openai/whisper |
| pyannote segmentation 3.0 (ONNX export by sherpa-onnx) | Speaker segmentation | MIT | https://huggingface.co/pyannote/segmentation-3.0 |
| 3D-Speaker CAM++ zh/en advanced (ONNX) | Speaker embeddings and voice memory | Apache-2.0 | https://github.com/modelscope/3D-Speaker |
| BGE-M3 (GGUF Q8_0) | Semantic search embeddings | MIT | https://huggingface.co/BAAI/bge-m3 |

Models are downloaded on a developer machine while building a release and are not stored in this source repository. Quill never downloads them at run time.
