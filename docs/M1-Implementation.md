# M1 Implementation Details (Week 2)

## Scope

M1 objective is to deliver a working core loop prototype for hold-to-talk flow.

In scope:

1. Hold/release session state machine with deterministic transitions.
2. Pipeline orchestration from captured audio to text commit.
3. No-op friendly adapters so the full flow can run without external model dependencies.
4. Baseline tests for state transitions and orchestration behavior.
5. First-launch hotkey setup and global hold key hook prototype.

Out of scope:

1. Modifier-combination hotkeys in hook mode (prototype uses single key presets).
2. Real WASAPI streaming capture.
3. Real TSF commit path.
4. Full ASR model runtime integration.

## Deliverables

1. `SessionStateMachine` implementation with explicit transition rules.
2. `HoldToTalkController` for `press -> release -> process` orchestration.
3. Test coverage for legal/illegal transitions and end-to-end happy path.
4. App startup demo path that executes one simulated hold-to-talk cycle.

## Work Packages

## WP1 State Model

Tasks:

1. Define states: `Idle`, `Recording`, `Recognizing`, `Committing`, `Completed`, `Failed`.
2. Define triggers: `Press`, `Release`, `AsrComplete`, `CommitComplete`, `Error`, `Reset`.
3. Enforce valid transitions and reject invalid ones.

Done criteria:

1. Transition table is encoded in code and covered by tests.
2. Invalid transitions produce deterministic failure.

## WP2 Orchestration

Tasks:

1. Implement controller that starts capture on `Press`.
2. Stop capture and trigger pipeline on `Release`.
3. Persist session outcome and expose lifecycle events.

Done criteria:

1. One simulated session completes from press to commit.
2. Controller always returns to `Idle` after completion or reset.

## WP3 Baseline Tests

Tasks:

1. Test state machine legal transitions.
2. Test invalid transitions.
3. Test orchestration happy path with fake dependencies.

Done criteria:

1. Tests run in CI and pass.
2. Failure cases are clear and reproducible.

## Acceptance Checklist

1. M1 code compiles with no errors.
2. State machine and controller tests pass.
3. Demo app can run one simulated session.
4. Boundaries remain compatible with M2 real audio/ASR integration.
