#!/usr/bin/env python3
import argparse
import base64
import json
import os
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

import numpy as np

os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ.setdefault("PYTHONUTF8", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


@dataclass
class RuntimeConfig:
    streaming_model: str
    device: str
    int8: bool
    speaker_model: str
    stride_samples: int = 9600
    encoder_chunk_look_back: int = 4
    decoder_chunk_look_back: int = 1
    chunk_size: List[int] = field(default_factory=lambda: [0, 10, 5])


@dataclass
class StreamingSession:
    session_id: str
    language: str
    hotwords: List[str]
    enable_speaker_diarization: bool
    cache: Dict[str, Any] = field(default_factory=dict)
    pending_audio: np.ndarray = field(default_factory=lambda: np.empty(0, dtype=np.float32))
    full_audio: np.ndarray = field(default_factory=lambda: np.empty(0, dtype=np.float32))
    confirmed_text: str = ""
    started_at: float = field(default_factory=time.perf_counter)


class FunAsrRuntime:
    def __init__(self, config: RuntimeConfig) -> None:
        self.config = config
        self._auto_model_cls = None
        self._streaming_model = None
        self._speaker_runtime = None
        self._sessions: Dict[str, StreamingSession] = {}
        self._int8_applied = False

    def _ensure_framework(self):
        if self._auto_model_cls is not None:
            return
        from funasr import AutoModel  # type: ignore

        self._auto_model_cls = AutoModel

    def _load_streaming_model(self):
        self._ensure_framework()
        if self._streaming_model is None:
            started = time.perf_counter()
            self._streaming_model = self._auto_model_cls(
                model=self.config.streaming_model,
                disable_update=True,
                device=self.config.device,
            )
            self._apply_int8_quantization()
            elapsed = (time.perf_counter() - started) * 1000.0
            print(
                f"[FunASR.Runtime.Py] streaming model loaded='{self.config.streaming_model}' device='{self.config.device}' elapsed_ms={elapsed:.1f}",
                file=sys.stderr,
                flush=True,
            )
        return self._streaming_model

    def _load_speaker_runtime(self):
        self._ensure_framework()
        if self._speaker_runtime is None:
            started = time.perf_counter()
            # CampPlus diarization is executed only at session finalization to avoid
            # adding latency to live streaming chunks.
            self._speaker_runtime = self._auto_model_cls(
                model=self.config.streaming_model,
                spk_model=self.config.speaker_model,
                disable_update=True,
                device=self.config.device,
            )
            elapsed = (time.perf_counter() - started) * 1000.0
            print(
                f"[FunASR.Runtime.Py] speaker runtime loaded='campplus:{self.config.speaker_model}' elapsed_ms={elapsed:.1f}",
                file=sys.stderr,
                flush=True,
            )
        return self._speaker_runtime

    def _apply_int8_quantization(self) -> None:
        if self._int8_applied or not self.config.int8:
            return

        if self.config.device.lower().startswith("cuda"):
            print(
                "[FunASR.Runtime.Py] int8 quantization skipped on CUDA device",
                file=sys.stderr,
                flush=True,
            )
            self._int8_applied = True
            return

        try:
            import torch  # type: ignore

            module = getattr(self._streaming_model, "model", None)
            if module is None:
                module = getattr(self._streaming_model, "asr_model", None)

            if module is None:
                print(
                    "[FunASR.Runtime.Py] int8 quantization skipped: model module not found",
                    file=sys.stderr,
                    flush=True,
                )
                self._int8_applied = True
                return

            quantized = torch.ao.quantization.quantize_dynamic(
                module,
                {torch.nn.Linear},
                dtype=torch.qint8,
            )
            if hasattr(self._streaming_model, "model"):
                self._streaming_model.model = quantized
            elif hasattr(self._streaming_model, "asr_model"):
                self._streaming_model.asr_model = quantized

            print(
                "[FunASR.Runtime.Py] int8 dynamic quantization applied",
                file=sys.stderr,
                flush=True,
            )
        except Exception as exc:
            print(
                f"[FunASR.Runtime.Py] int8 quantization failed: {exc}",
                file=sys.stderr,
                flush=True,
            )
        finally:
            self._int8_applied = True

    @staticmethod
    def _decode_pcm16(audio_base64: str) -> np.ndarray:
        raw = base64.b64decode(audio_base64)
        if not raw:
            return np.empty(0, dtype=np.float32)
        pcm = np.frombuffer(raw, dtype=np.int16)
        return pcm.astype(np.float32) / 32768.0

    @staticmethod
    def _extract_text(output: Any) -> str:
        if isinstance(output, str):
            return output.strip()

        if isinstance(output, dict):
            text = output.get("text")
            if isinstance(text, str):
                return text.strip()

        if isinstance(output, list) and output:
            first = output[0]
            if isinstance(first, str):
                return first.strip()
            if isinstance(first, dict):
                text = first.get("text")
                if isinstance(text, str):
                    return text.strip()
                # Some pipelines return sentence-level info only.
                sentence_info = first.get("sentence_info")
                if isinstance(sentence_info, list):
                    text_chunks = [str(x.get("text", "")).strip() for x in sentence_info if isinstance(x, dict)]
                    merged = "".join(x for x in text_chunks if x)
                    if merged:
                        return merged

        return str(output).strip()

    @staticmethod
    def _extract_speaker_segments(output: Any) -> List[Dict[str, Any]]:
        segments: List[Dict[str, Any]] = []
        if isinstance(output, list):
            for item in output:
                if not isinstance(item, dict):
                    continue
                sentence_info = item.get("sentence_info")
                if not isinstance(sentence_info, list):
                    continue
                for sentence in sentence_info:
                    if not isinstance(sentence, dict):
                        continue
                    text = str(sentence.get("text", "")).strip()
                    if not text:
                        continue
                    speaker_raw = sentence.get("spk", sentence.get("speaker", "speaker-1"))
                    speaker_id = str(speaker_raw) if speaker_raw is not None else "speaker-1"
                    start_ms = int(sentence.get("start", sentence.get("start_ms", 0)) or 0)
                    end_ms = int(sentence.get("end", sentence.get("end_ms", start_ms)) or start_ms)
                    segments.append(
                        {
                            "speaker_id": speaker_id,
                            "text": text,
                            "start_ms": max(0, start_ms),
                            "end_ms": max(start_ms, end_ms),
                        }
                    )
        return segments

    @staticmethod
    def _append_confirmed(current: str, piece: str) -> str:
        if not piece:
            return current
        if not current:
            return piece
        if piece.startswith(current):
            return piece
        if current.endswith(piece):
            return current
        return current + piece

    def preload(self, include_speaker_diarization: bool) -> Dict[str, Any]:
        started = time.perf_counter()
        self._load_streaming_model()
        if include_speaker_diarization:
            self._load_speaker_runtime()

        return {
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
            "speaker_loaded": bool(include_speaker_diarization),
        }

    def start_streaming_session(
        self,
        session_id: str,
        language: str,
        hotwords: List[str],
        enable_speaker_diarization: bool,
    ) -> Dict[str, Any]:
        self._load_streaming_model()
        if enable_speaker_diarization:
            self._load_speaker_runtime()

        if session_id in self._sessions:
            raise ValueError(f"session already exists: {session_id}")

        session = StreamingSession(
            session_id=session_id,
            language=(language or "auto").strip() or "auto",
            hotwords=[x.strip() for x in hotwords if isinstance(x, str) and x.strip()],
            enable_speaker_diarization=enable_speaker_diarization,
        )
        self._sessions[session_id] = session
        return {"session_id": session_id}

    def _generate_for_chunk(self, session: StreamingSession, chunk: np.ndarray, is_final: bool) -> str:
        model = self._streaming_model
        if model is None:
            raise RuntimeError("streaming model is not initialized")

        language = None if session.language == "auto" else session.language
        hotword_text = " ".join(session.hotwords) if session.hotwords else None

        kwargs = {
            "input": chunk,
            "cache": session.cache,
            "is_final": is_final,
            "chunk_size": self.config.chunk_size,
            "encoder_chunk_look_back": self.config.encoder_chunk_look_back,
            "decoder_chunk_look_back": self.config.decoder_chunk_look_back,
        }
        if language:
            kwargs["language"] = language
        if hotword_text:
            kwargs["hotword"] = hotword_text

        result = model.generate(**kwargs)
        return self._extract_text(result)

    def push_audio_chunk(
        self,
        session_id: str,
        audio_base64: str,
        _sample_rate: int,
    ) -> Dict[str, Any]:
        session = self._sessions.get(session_id)
        if session is None:
            raise ValueError(f"unknown session: {session_id}")

        started = time.perf_counter()
        samples = self._decode_pcm16(audio_base64)
        if samples.size == 0:
            return {
                "session_id": session_id,
                "preview_text": session.confirmed_text,
                "confirmed_text": session.confirmed_text,
                "delta_text": "",
                "is_final": False,
                "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
                "speaker_segments": [],
            }

        session.pending_audio = np.concatenate([session.pending_audio, samples])
        session.full_audio = np.concatenate([session.full_audio, samples])

        delta_text = ""
        while session.pending_audio.size >= self.config.stride_samples:
            chunk = session.pending_audio[: self.config.stride_samples]
            session.pending_audio = session.pending_audio[self.config.stride_samples :]
            piece = self._generate_for_chunk(session, chunk, is_final=False)
            if piece:
                session.confirmed_text = self._append_confirmed(session.confirmed_text, piece)
                delta_text += piece

        return {
            "session_id": session_id,
            "preview_text": session.confirmed_text,
            "confirmed_text": session.confirmed_text,
            "delta_text": delta_text,
            "is_final": False,
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
            "speaker_segments": [],
        }

    def _run_speaker_diarization(self, session: StreamingSession) -> List[Dict[str, Any]]:
        if not session.enable_speaker_diarization or session.full_audio.size == 0:
            return []

        runtime = self._speaker_runtime
        if runtime is None:
            return []

        language = None if session.language == "auto" else session.language
        try:
            kwargs = {"input": session.full_audio}
            if language:
                kwargs["language"] = language
            output = runtime.generate(**kwargs)
            segments = self._extract_speaker_segments(output)
            if segments:
                return segments
        except Exception as exc:
            print(
                f"[FunASR.Runtime.Py] speaker diarization failed: {exc}",
                file=sys.stderr,
                flush=True,
            )

        if session.confirmed_text.strip():
            return [
                {
                    "speaker_id": "speaker-1",
                    "text": session.confirmed_text.strip(),
                    "start_ms": 0,
                    "end_ms": int((time.perf_counter() - session.started_at) * 1000.0),
                }
            ]
        return []

    def end_streaming_session(self, session_id: str) -> Dict[str, Any]:
        session = self._sessions.get(session_id)
        if session is None:
            raise ValueError(f"unknown session: {session_id}")

        started = time.perf_counter()
        final_delta = ""
        if session.pending_audio.size > 0:
            piece = self._generate_for_chunk(session, session.pending_audio, is_final=True)
            session.pending_audio = np.empty(0, dtype=np.float32)
            if piece:
                session.confirmed_text = self._append_confirmed(session.confirmed_text, piece)
                final_delta = piece

        speaker_segments = self._run_speaker_diarization(session)
        confirmed_text = session.confirmed_text
        del self._sessions[session_id]

        return {
            "session_id": session_id,
            "preview_text": confirmed_text,
            "confirmed_text": confirmed_text,
            "delta_text": final_delta,
            "is_final": True,
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
            "speaker_segments": speaker_segments,
        }


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PressTalk FunASR streaming runtime")
    parser.add_argument("--streaming-model", required=True)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--speaker-model", required=True)
    parser.add_argument("--int8", default="0")
    return parser


def make_ok(payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    data: Dict[str, Any] = {"ok": True}
    if payload:
        data.update(payload)
    return data


def make_error(message: str) -> Dict[str, Any]:
    return {"ok": False, "error": message}


def main() -> int:
    args = make_parser().parse_args()
    runtime = FunAsrRuntime(
        RuntimeConfig(
            streaming_model=args.streaming_model,
            device=args.device,
            int8=args.int8 == "1",
            speaker_model=args.speaker_model,
        )
    )

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        request_id = None
        try:
            req = json.loads(line)
            request_id = req.get("request_id")
            action = req.get("action")

            if action == "ping":
                payload = make_ok({"runtime_version": "funasr-streaming-runtime-1"})
            elif action == "preload":
                include_speaker = bool(req.get("include_speaker_diarization", False))
                payload = make_ok(runtime.preload(include_speaker))
            elif action == "start_streaming_session":
                session_id = req.get("session_id")
                if not isinstance(session_id, str) or not session_id.strip():
                    raise ValueError("session_id is required")
                language = str(req.get("language", "auto"))
                hotwords = req.get("hotwords", [])
                if hotwords is None:
                    hotwords = []
                if not isinstance(hotwords, list):
                    raise ValueError("hotwords must be an array")
                enable_speaker = bool(req.get("enable_speaker_diarization", False))
                payload = make_ok(
                    runtime.start_streaming_session(
                        session_id.strip(),
                        language,
                        hotwords,
                        enable_speaker,
                    )
                )
            elif action == "push_audio_chunk":
                session_id = req.get("session_id")
                audio_base64 = req.get("audio_base64")
                sample_rate = int(req.get("sample_rate", 16000))
                if not isinstance(session_id, str) or not session_id.strip():
                    raise ValueError("session_id is required")
                if not isinstance(audio_base64, str) or not audio_base64.strip():
                    raise ValueError("audio_base64 is required")
                payload = make_ok(runtime.push_audio_chunk(session_id.strip(), audio_base64, sample_rate))
            elif action == "end_streaming_session":
                session_id = req.get("session_id")
                if not isinstance(session_id, str) or not session_id.strip():
                    raise ValueError("session_id is required")
                payload = make_ok(runtime.end_streaming_session(session_id.strip()))
            elif action == "shutdown":
                payload = make_ok({"message": "bye"})
                if request_id:
                    payload["request_id"] = request_id
                print(json.dumps(payload, ensure_ascii=False), flush=True)
                return 0
            else:
                raise ValueError(f"unsupported action: {action}")
        except Exception as exc:  # pragma: no cover
            payload = make_error(str(exc))

        if request_id:
            payload["request_id"] = request_id
        print(json.dumps(payload, ensure_ascii=False), flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
