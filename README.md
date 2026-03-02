# PressTalk

PressTalk is a Windows-first, open-source voice input project.

Core interaction: **hold key to speak, release key to commit text**.

## Project Positioning

PressTalk has two clearly separated product stages:

1. `MVP mode` (`v0.1.0-mvp`): a reliable voice input assistant that works across common apps.
2. `IME mode` (`v0.2.0+`): a TSF-based real Windows input method.

This separation is intentional to reduce delivery risk and prove latency/stability first.

## Current Scope (MVP)

1. Windows only.
2. Local ASR for Chinese + English.
3. Rule-based text cleanup and normalization.
4. Microphone device selection.
5. Local history persistence in SQLite.
6. Compatibility commit path for editors, browsers, and terminal-like apps.
7. First-launch hotkey setup wizard (current prototype presets: `F8` to `F12`).

Current prototype limitation:

1. Runtime path uses real WASAPI capture and Qwen local ASR backend.
2. Hotkey flow and state transition are real; text commit supports `sendinput` and `paste`.
3. First run may trigger Hugging Face model download for Qwen ASR and Qwen semantic model.
4. Commit target is captured at hold-start and re-activated at hold-end (best effort).
5. `paste` mode overwrites clipboard content.
6. Captured raw audio is dumped to `%LOCALAPPDATA%\\PressTalk\\debug-audio\\` for debugging.
7. Hold-time live caption preview is best-effort and runs with preview ASR model.
8. Semantic normalization is enabled by default and auto-disables under system pressure.
9. Default ASR profile is `Qwen/Qwen3-ASR-0.6B` for faster response.

MVP explicitly does **not** include TSF registration and does **not** require LLM.

## Planned Technology Stack

1. Speech engine and settings app: `C# / .NET 8`.
2. ASR runtime target: `qwen-asr` local runtime with `Qwen/Qwen3-ASR-1.7B`.
3. Semantic normalization target: `Qwen/Qwen3-0.6B` (optional, timeout-guarded).
4. Data storage: `SQLite`.
5. IME integration (post-MVP): `C++/ATL` + TSF TIP.
6. Inter-process communication (post-MVP): `Named Pipes`.

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

## Module Responsibilities

1. `PressTalk.Contracts`: shared interfaces and DTOs.
2. `PressTalk.Engine`: hold/release state machine and pipeline orchestration.
3. `PressTalk.Audio`: capture and microphone management.
4. `PressTalk.Asr`: ASR backend abstraction and runtime integration.
5. `PressTalk.Normalize`: deterministic cleanup and normalization.
6. `PressTalk.Commit`: text commit strategy and compatibility fallback.
7. `PressTalk.Data`: history persistence and schema management.
8. `PressTalk.App`: settings/tray app entry.
9. `PressTalk.Tsf`: TSF TIP integration (implemented after MVP).

## Documents

1. [VISION.md](./VISION.md): mission, principles, and boundaries.
2. [Plan.md](./Plan.md): milestones, quality gates, and release cadence.
3. [docs/Architecture.md](./docs/Architecture.md): module boundaries and runtime flow.
4. [docs/Terminology.md](./docs/Terminology.md): canonical terms used across docs.
5. [docs/M0-Implementation.md](./docs/M0-Implementation.md): implementation details for the first phase.
6. [docs/M1-Implementation.md](./docs/M1-Implementation.md): implementation details for the core loop prototype.

## Developer Bootstrap

1. Run prerequisite check:
   - `pwsh ./scripts/check-prereqs.ps1`
2. Install Qwen local runtime dependencies:
   - `pwsh ./scripts/setup-qwen-runtime.ps1`
3. Prepare local model folder scaffold or pre-download models:
   - `pwsh ./scripts/download-model.ps1 -SkipDownload`
   - `pwsh ./scripts/download-model.ps1`
4. One-command bootstrap:
   - `pwsh ./scripts/bootstrap.ps1`
5. Build and run app (after installing .NET SDK 8):
   - `dotnet build PressTalk.sln`
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj`
6. Reset hotkey setup and rerun wizard:
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj -- --reset-hotkey`
7. Choose commit mode:
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj -- --commit-mode=paste`
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj -- --commit-mode=sendinput`
8. Run model/self-check before human test:
   - `dotnet run --project ./src/PressTalk.App/PressTalk.App.csproj -- --self-check --qwen-asr-final=Qwen/Qwen3-ASR-0.6B --qwen-asr-preview=Qwen/Qwen3-ASR-0.6B`

## License

MIT. See [LICENSE](./LICENSE).

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md).
