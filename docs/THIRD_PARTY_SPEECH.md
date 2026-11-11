# Optional speech components

Arni Lite can download these components on the user's request. They are not part of the base installer.

- **whisper.cpp**, ggml-org: [project and MIT license](https://github.com/ggml-org/whisper.cpp/tree/v1.9.2). CPU executable and ggml/whisper DLLs from the official Windows release archive. Copyright 2023–2026 The ggml authors; see upstream [LICENSE](https://github.com/ggml-org/whisper.cpp/blob/v1.9.2/LICENSE).
- **Whisper base** weights by OpenAI, converted to ggml by ggerganov: [model distribution](https://huggingface.co/ggerganov/whisper.cpp), [original Whisper and MIT license](https://github.com/openai/whisper).

The original MIT notices are embedded in the Windows module and written into the optional engine directory on installation. Keep component notices and pinned checksums updated when changing the optional engine/model. No SDL player, server, Python runtime or GPU library is extracted by the client.
