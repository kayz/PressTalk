# PressTalk

PressTalk is a Windows-first, open-source voice input project.

Core interaction: **press hotkey once to start streaming, press again to stop and finalize**.

## Current Scope (MVP)

1. Windows only.
2. FunASR local streaming ASR (Chinese/English).
3. Toggle interaction with floating UI.
4. Incremental text commit to active input box with clipboard sync.
5. Optional speaker diarization (CampPlus) at session finalization.
6. Hotword customization for professional vocabulary.
7. Final-text history persistence (JSONL).

## Latest Updates (2026-03-04)

1. Runtime migrated from Qwen offline to FunASR streaming.
2. State machine and controller upgraded for 300ms chunk streaming.
3. Committers support incremental deduped text commit.
4. UI switched from hold-to-talk to toggle mode.
5. Floating UI now shows waveform animation and live transcript preview.
6. Optional speaker-separated transcript rendering is available.
7. New runtime protocol doc: `docs/funasr-streaming-protocol.md`.

## Repository Layout

```text
PressTalk/
  docs/
  models/
  scripts/
  src/
    PressTalk.App/
    PressTalk.Asr/
    PressTalk.Audio/
    PressTalk.Commit/
    PressTalk.Contracts/
    PressTalk.Data/
    PressTalk.Engine/
    PressTalk.Normalize/
    PressTalk.Tsf/               # post-MVP
  tests/
    PressTalk.Engine.Tests/
```

## Developer Bootstrap

1. Run prerequisite check:
   - `pwsh ./scripts/check-prereqs.ps1`
2. Install FunASR runtime dependencies:
   - `pwsh ./scripts/setup-funasr-runtime.ps1`
3. Optional: pre-download runtime models:
   - `pwsh ./scripts/download-model.ps1 -SkipDownload`
   - `pwsh ./scripts/download-model.ps1`
4. One-command bootstrap:
   - `pwsh ./scripts/bootstrap.ps1`
5. Build and run app:
   - `dotnet build PressTalk.sln`
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj`
6. Reset hotkey config:
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj -- --reset-hotkey`

## Runtime Notes

1. First run may download model files (about 1-2GB depending on cache).
2. Device auto-detect prefers CUDA when available.
3. Runtime logs are written to `%LOCALAPPDATA%\PressTalk\logs\`.
4. History is stored at `%LOCALAPPDATA%\PressTalk\history\history.jsonl`.
5. Optional tuning:
   - `PRESSTALK_STREAM_PUSH_MS` (default `450`)
   - `PRESSTALK_FUNASR_STRIDE_SAMPLES` (default `12800`)

## Documents

1. [VISION.md](./VISION.md)
2. [Plan.md](./Plan.md)
3. [docs/Architecture.md](./docs/Architecture.md)
4. [docs/Terminology.md](./docs/Terminology.md)
5. [docs/funasr-streaming-protocol.md](./docs/funasr-streaming-protocol.md)
6. [docs/log/2026-03-04-funasr-streaming-upgrade.md](./docs/log/2026-03-04-funasr-streaming-upgrade.md)

## License

MIT. See [LICENSE](./LICENSE).

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md).
