# PressTalk Terminology

This file defines terms used across all project documents.

## Product Modes

1. `MVP mode`: non-TSF assistant-style voice input path for feasibility validation.
2. `IME mode`: TSF-based Windows input method path.

## Core Interaction

1. `hold-to-talk`: user presses and holds configured key to start recording.
2. `release-to-commit`: user releases key to end recording and commit final text.

## Pipeline Terms

1. `ASR`: automatic speech recognition from audio to raw text.
2. `normalization`: deterministic text cleanup and formatting.
3. `rewrite`: optional LLM-based rephrasing, disabled by default.
4. `commit`: writing final text into currently focused application.

## Compatibility Terms

1. `primary commit path`: default commit strategy for standard text controls.
2. `fallback commit path`: compatibility strategy for legacy controls and terminal-like apps.

## Milestone Terms

1. `T0`: project kickoff date.
2. `Week N`: the Nth week since `T0`.
3. `DoD`: definition of done; all deliverables and quality gates pass.

