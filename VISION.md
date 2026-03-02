# PressTalk Vision

## Mission

Build an open-source voice IME for Windows that feels as direct as keyboard typing:

- Hold a key to talk.
- Release to commit text immediately.
- Work in common apps, terminals, and editor workflows.

Canonical terms are defined in `docs/Terminology.md`.

## Product Definition

PressTalk is not a generic dictation tool.
PressTalk is a voice-first input system with two explicit stages:

1. `MVP mode`: assistant-style voice input with broad app compatibility.
2. `IME mode`: TSF-registered Windows input method.

Common invariant for both stages:

1. Voice is the only input mode.
2. Core interaction is deterministic: `press -> speak -> release -> commit`.
3. Speech and history are local by default.

## User Value

- Fast text entry when typing is inconvenient.
- Better quality output through automatic cleanup:
  - remove filler words
  - punctuation and casing normalization
  - spoken-number normalization where possible
- Full local history for retrieval and analysis.

## Design Principles

- Local-first by default: speech recognition and text post-processing run locally.
- Reliable over flashy: predictable behavior is more important than experimental features.
- Low-latency UX: short utterances should commit near-instantly after key release.
- Broad compatibility: prioritize behavior in editors, browsers, terminals, and legacy controls.
- Privacy-respecting: explicit data retention and export/delete controls.

## Non-Goals (Early Stage)

- Cloud-dependent mandatory pipeline.
- Multi-platform parity before Windows quality is proven.
- Complex multimodal UI.
- LLM-only rewrite in the default critical path.

## Release Principles

1. MVP ships first to prove feasibility and stability.
2. TSF IME integration starts only after MVP quality gates are met.
3. AI rewrite capability is opt-in and must have deterministic fallback.

## Long-Term Vision

PressTalk becomes a robust, developer-friendly voice input infrastructure:

- stable TSF-based IME on Windows
- optional cross-platform assistant mode in future
- extensible processing pipeline for domain-specific normalization
- transparent, MIT-licensed community project
