# M0 Implementation Details (Week 1)

## Scope

M0 objective is to produce a buildable foundation for MVP implementation.

In scope:

1. Repository scaffold and project skeleton.
2. Shared contracts and module boundaries.
3. Logging and configuration foundation.
4. ASR smoke path design and runnable placeholder.
5. Model management policy and scripts skeleton.
6. Development conventions and acceptance checklist.

Out of scope:

1. Full hold-to-talk implementation.
2. Real-time ASR streaming.
3. Real text commit integration.
4. TSF IME registration.

## Deliverables

1. `PressTalk.sln` and module projects under `src/`.
2. Base docs updated and terminology fixed.
3. Script placeholders for prerequisites and model setup.
4. `PressTalk.App` startup path proving dependency wiring.
5. `M0` acceptance report in pull request checklist.

## Execution Schedule (Day-Level)

Day 1:

1. Finalize terminology and document scope boundaries.
2. Initialize repository structure and baseline files.

Day 2:

1. Create `PressTalk.Contracts` interfaces and DTOs.
2. Create module project skeletons and references.

Day 3:

1. Wire `PressTalk.App` startup path with placeholder dependencies.
2. Add script stubs for prerequisite and model setup.

Day 4:

1. Review architecture boundaries and dependency direction.
2. Add issue/PR templates and contribution guidance.

Day 5:

1. Run M0 checklist review.
2. Publish M0 snapshot and open M1 task board.

## Work Packages

## WP1 Repository Skeleton

Tasks:

1. Create standard top-level folders (`docs/`, `src/`, `tests/`, `scripts/`, `models/`).
2. Add `.editorconfig`, `.gitignore`, and `LICENSE`.
3. Add `PressTalk.sln` with module projects.

Done criteria:

1. Folder and project structure matches `README.md`.
2. New contributors can identify where each module belongs.

## WP2 Contracts and Boundaries

Tasks:

1. Define core request/result DTOs in `PressTalk.Contracts`.
2. Define interface contracts for ASR, normalization, and commit.
3. Create placeholder implementations in module projects.

Done criteria:

1. No circular dependencies between modules.
2. Engine depends on contracts, not concrete implementations.

## WP3 Logging and Configuration Baseline

Tasks:

1. Define runtime options file shape (`appsettings.json` in M0 scaffold).
2. Define mandatory log fields:
   - `session_id`
   - `stage`
   - `duration_ms`
   - `target_app`
3. Wire simple startup log entry in `PressTalk.App`.

Done criteria:

1. Startup path can emit deterministic logs.
2. Option model exists and can be extended without breaking contracts.

## WP4 ASR Smoke Path Design

Tasks:

1. Keep default ASR backend as local model adapter interface.
2. Add no-op backend for integration testing.
3. Historical note: original M0 draft targeted `sherpa-onnx + SenseVoice`; current project target is Qwen ASR (see `Plan.md`).

Done criteria:

1. Engine can call ASR abstraction and receive a typed result.
2. Runtime can proceed even if real model is not wired yet.

## WP5 Model Setup Policy

Tasks:

1. Add script placeholders for environment checks and model bootstrap.
2. Reserve `models/` as local model root (excluded from git).
3. Require checksum validation before first model use.

Done criteria:

1. No large model binaries in git history.
2. Setup process is documented and script-backed.

## M0 Acceptance Checklist

1. Repository has deterministic structure and baseline governance files.
2. `README.md`, `VISION.md`, `Plan.md`, and terminology are consistent.
3. `PressTalk.sln` includes core projects for MVP path.
4. `PressTalk.App` startup path demonstrates module wiring.
5. M0 document defines clear in-scope and out-of-scope boundaries.

## Risks and Controls

1. Risk: over-design in M0 delays feature work.
Control: M0 limits implementation to interfaces and placeholders.
2. Risk: unclear contracts create rework in M1.
Control: contracts are centralized in `PressTalk.Contracts`.
3. Risk: model setup confusion for new contributors.
Control: setup scripts and model policy are part of M0 deliverables.
