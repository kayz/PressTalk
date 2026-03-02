# Contributing to PressTalk

## Contribution Scope

1. Follow `Plan.md` milestone boundaries.
2. Keep MVP-focused changes minimal and deterministic.
3. Propose post-MVP features through issue discussion first.

## Pull Request Rules

1. One PR should solve one clear problem.
2. Update docs when behavior, interface, or scope changes.
3. Do not commit model binaries to git.
4. Keep modules decoupled via `PressTalk.Contracts`.

## Coding Rules

1. C# code uses nullable reference types.
2. Keep business logic outside UI layer.
3. Add concise comments only where logic is non-obvious.

## Security and Privacy

1. Do not upload user audio/text in default implementation.
2. Any telemetry proposal must be opt-in and documented.
3. History storage changes must include migration notes.

