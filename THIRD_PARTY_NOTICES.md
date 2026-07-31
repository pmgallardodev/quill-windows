# Third-party notices

The Windows application is built from this repository. A portable build includes the following third-party components, each under its own license:

| Component | Purpose | License | Source |
| --- | --- | --- | --- |
| NAudio | WASAPI capture and WAV processing | Microsoft Public License (MS-PL) | https://github.com/naudio/NAudio |
| Whisper.net | .NET binding for local inference | MIT | https://github.com/sandrohanea/whisper.net |
| whisper.cpp | Native Whisper inference runtime | MIT | https://github.com/ggml-org/whisper.cpp |
| OpenAI Whisper `large-v3` model | Local Spanish speech recognition model | MIT | https://github.com/openai/whisper |
| .NET runtime | Self-contained Windows runtime | MIT | https://github.com/dotnet/runtime |

The model is downloaded during a release build and is not stored in this source repository. Consult each linked project for its complete license text and notices.
