#!/usr/bin/env python3
import argparse
import base64
import json
import os
import re
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
    realtime_punc_model: str
    final_punc_model: str
    transcription_mode: str = "fast"
    stride_samples: int = 9600
    endpoint_silence_ms: int = 420
    endpoint_rms_threshold: float = 0.0065
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
    raw_confirmed_text: str = ""
    formatted_confirmed_text: str = ""
    realtime_punc_cache: Dict[str, Any] = field(default_factory=dict)
    silence_run_samples: int = 0
    has_detected_speech: bool = False
    started_at: float = field(default_factory=time.perf_counter)


class FunAsrRuntime:
    _FILLER_PATTERNS = [
        re.compile(r"\b(?:um+|uh+|erm+|emm+|ah+)\b", re.IGNORECASE),
        re.compile(r"(那个|然后那个|就是|就是说|呃|嗯|额|啊|欸)+"),
    ]
    _SENTENCE_ENDINGS = "。！？!?"

    def __init__(self, config: RuntimeConfig) -> None:
        self.config = config
        self.config.transcription_mode = "formatted" if str(config.transcription_mode).strip().lower() == "formatted" else "fast"
        self._auto_model_cls = None
        self._streaming_model = None
        self._realtime_punc_model = None
        self._final_punc_model = None
        self._speaker_runtime = None
        self._sessions: Dict[str, StreamingSession] = {}
        self._int8_applied = False
        self._realtime_punc_unavailable = False
        self._final_punc_unavailable = False

    @property
    def _formatting_enabled(self) -> bool:
        return self.config.transcription_mode == "formatted"

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
                disable_pbar=True,
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
                disable_pbar=True,
            )
            elapsed = (time.perf_counter() - started) * 1000.0
            print(
                f"[FunASR.Runtime.Py] speaker runtime loaded='campplus:{self.config.speaker_model}' elapsed_ms={elapsed:.1f}",
                file=sys.stderr,
                flush=True,
            )
        return self._speaker_runtime

    def _load_realtime_punc_model(self):
        if self._realtime_punc_model is not None:
            return self._realtime_punc_model
        if self._realtime_punc_unavailable:
            return None
        if not self.config.realtime_punc_model:
            self._realtime_punc_unavailable = True
            return None

        self._ensure_framework()
        started = time.perf_counter()
        try:
            self._realtime_punc_model = self._auto_model_cls(
                model=self.config.realtime_punc_model,
                disable_update=True,
                device=self.config.device,
                disable_pbar=True,
            )
            elapsed = (time.perf_counter() - started) * 1000.0
            print(
                f"[FunASR.Runtime.Py] realtime punc model loaded='{self.config.realtime_punc_model}' elapsed_ms={elapsed:.1f}",
                file=sys.stderr,
                flush=True,
            )
        except Exception as exc:
            self._realtime_punc_unavailable = True
            print(
                f"[FunASR.Runtime.Py] realtime punc model unavailable: {exc}",
                file=sys.stderr,
                flush=True,
            )
        return self._realtime_punc_model

    def _load_final_punc_model(self):
        if self._final_punc_model is not None:
            return self._final_punc_model
        if self._final_punc_unavailable:
            return None
        if not self.config.final_punc_model:
            self._final_punc_unavailable = True
            return None

        self._ensure_framework()
        started = time.perf_counter()
        try:
            self._final_punc_model = self._auto_model_cls(
                model=self.config.final_punc_model,
                disable_update=True,
                device=self.config.device,
                disable_pbar=True,
            )
            elapsed = (time.perf_counter() - started) * 1000.0
            print(
                f"[FunASR.Runtime.Py] final punc model loaded='{self.config.final_punc_model}' elapsed_ms={elapsed:.1f}",
                file=sys.stderr,
                flush=True,
            )
        except Exception as exc:
            self._final_punc_unavailable = True
            print(
                f"[FunASR.Runtime.Py] final punc model unavailable: {exc}",
                file=sys.stderr,
                flush=True,
            )
        return self._final_punc_model

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

        max_overlap = min(len(current), len(piece))
        for overlap in range(max_overlap, 0, -1):
            if current.endswith(piece[:overlap]):
                return current + piece[overlap:]
        return current + piece

    @classmethod
    def _strip_fillers(cls, text: str) -> str:
        cleaned = text
        for pattern in cls._FILLER_PATTERNS:
            cleaned = pattern.sub("", cleaned)
        cleaned = re.sub(r"\s+", " ", cleaned)
        cleaned = re.sub(r"\s*([，。！？,.!?；;：:])\s*", r"\1", cleaned)
        cleaned = re.sub(r"([\u4e00-\u9fff])\s+([\u4e00-\u9fff])", r"\1\2", cleaned)
        return cleaned.strip()

    def _append_formatted(self, session: StreamingSession, raw_piece: str) -> tuple[str, str]:
        current = session.formatted_confirmed_text
        if self._formatting_enabled:
            cleaned = self._strip_fillers(raw_piece)
            if not cleaned:
                text = current
            else:
                live_piece = self._apply_realtime_punctuation(session, cleaned)
                text = self._append_confirmed(current, live_piece)
        else:
            plain = (raw_piece or "").strip()
            text = self._append_confirmed(current, plain) if plain else current

        delta = text[len(current):] if text.startswith(current) else (raw_piece or "")
        return text, delta

    @classmethod
    def _segment_paragraphs(cls, text: str) -> str:
        parts = [part.strip() for part in re.split(r"(?<=[。！？!?])", text) if part.strip()]
        if len(parts) <= 1:
            return text

        grouped: List[str] = []
        bucket: List[str] = []
        for part in parts:
            bucket.append(part)
            joined = "".join(bucket)
            if len(joined) >= 36 or len(bucket) >= 2:
                grouped.append(joined)
                bucket = []
        if bucket:
            grouped.append("".join(bucket))

        return "\n".join(grouped)

    @classmethod
    def _normalize_punctuation(cls, text: str) -> str:
        output = text.strip()
        output = re.sub(r"\s+", " ", output)
        output = re.sub(r"\s*([，。！？,.!?；;：:])\s*", r"\1", output)
        output = re.sub(r"[，,]{2,}", "，", output)
        output = re.sub(r"[。\.]{2,}", "。", output)
        output = re.sub(r"([。！？!?])[，,]+", r"\1", output)
        output = re.sub(r"^[，,]+", "", output)
        return output

    def _apply_realtime_punctuation(self, session: StreamingSession, text: str) -> str:
        if not self._formatting_enabled or not text:
            return text
        model = self._load_realtime_punc_model()
        if model is None:
            return text

        try:
            result = model.generate(input=text, cache=session.realtime_punc_cache)
            punctuated = self._extract_text(result)
            if punctuated:
                return self._normalize_punctuation(punctuated)
        except Exception as exc:
            print(
                f"[FunASR.Runtime.Py] realtime punctuation failed: {exc}",
                file=sys.stderr,
                flush=True,
            )

        return text

    def _build_final_preview(self, text: str) -> str:
        if not self._formatting_enabled:
            return (text or "").strip()

        cleaned = self._strip_fillers(text)
        if not cleaned:
            return ""

        output = cleaned
        model = self._load_final_punc_model()
        if model is not None:
            try:
                result = model.generate(input=cleaned)
                punctuated = self._extract_text(result)
                if punctuated:
                    output = punctuated
            except Exception as exc:
                print(
                    f"[FunASR.Runtime.Py] final punctuation failed: {exc}",
                    file=sys.stderr,
                    flush=True,
                )

        output = self._normalize_punctuation(output)
        if output and output[-1] not in self._SENTENCE_ENDINGS:
            output += "。"
        return self._segment_paragraphs(output)

    @staticmethod
    def _append_pause_comma(text: str) -> str:
        if not text:
            return text
        if text[-1] in "，,。！？!?；;：:\n":
            return text
        return text + "，"

    @staticmethod
    def _rms(samples: np.ndarray) -> float:
        if samples.size == 0:
            return 0.0
        return float(np.sqrt(np.mean(np.square(samples, dtype=np.float64))))

    def _should_endpoint_flush(self, session: StreamingSession, samples: np.ndarray) -> bool:
        if samples.size == 0:
            return False

        rms = self._rms(samples)
        is_silence = rms <= self.config.endpoint_rms_threshold
        if is_silence:
            session.silence_run_samples += int(samples.size)
        else:
            session.silence_run_samples = 0
            session.has_detected_speech = True

        endpoint_samples = int(16000 * max(120, self.config.endpoint_silence_ms) / 1000.0)
        if not session.has_detected_speech:
            return False
        if session.pending_audio.size == 0:
            return False
        if session.silence_run_samples < endpoint_samples:
            return False

        return True

    def preload(self, include_speaker_diarization: bool) -> Dict[str, Any]:
        started = time.perf_counter()
        self._load_streaming_model()
        realtime_punc = self._load_realtime_punc_model() if self._formatting_enabled else None
        final_punc = self._load_final_punc_model() if self._formatting_enabled else None
        if include_speaker_diarization:
            self._load_speaker_runtime()

        return {
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
            "speaker_loaded": bool(include_speaker_diarization),
            "realtime_punc_loaded": realtime_punc is not None,
            "final_punc_loaded": final_punc is not None,
            "mode": self.config.transcription_mode,
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
                "preview_text": session.formatted_confirmed_text,
                "confirmed_text": session.formatted_confirmed_text,
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
                session.raw_confirmed_text = self._append_confirmed(session.raw_confirmed_text, piece)
                session.formatted_confirmed_text, formatted_delta = self._append_formatted(session, piece)
                delta_text += formatted_delta

        # On natural short pause, flush residual tail as sentence endpoint so the
        # last token appears without waiting for next utterance.
        if self._should_endpoint_flush(session, samples):
            tail = session.pending_audio
            session.pending_audio = np.empty(0, dtype=np.float32)
            piece = self._generate_for_chunk(session, tail, is_final=True)
            session.silence_run_samples = 0
            if piece:
                session.raw_confirmed_text = self._append_confirmed(session.raw_confirmed_text, piece)
                session.formatted_confirmed_text, formatted_delta = self._append_formatted(session, piece)
                delta_text += formatted_delta
                if not self._formatting_enabled:
                    with_comma = self._append_pause_comma(session.formatted_confirmed_text)
                    if with_comma != session.formatted_confirmed_text:
                        session.formatted_confirmed_text = with_comma
                        delta_text += "，"

        return {
            "session_id": session_id,
            "preview_text": session.formatted_confirmed_text,
            "confirmed_text": session.formatted_confirmed_text,
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

        if session.formatted_confirmed_text.strip():
            return [
                {
                    "speaker_id": "speaker-1",
                    "text": session.formatted_confirmed_text.strip(),
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
                session.raw_confirmed_text = self._append_confirmed(session.raw_confirmed_text, piece)
                previous_text = session.formatted_confirmed_text
                session.formatted_confirmed_text, _ = self._append_formatted(session, piece)
                final_delta = (
                    session.formatted_confirmed_text[len(previous_text):]
                    if session.formatted_confirmed_text.startswith(previous_text)
                    else ""
                )

        speaker_segments = self._run_speaker_diarization(session)
        confirmed_text = session.formatted_confirmed_text
        preview_text = self._build_final_preview(session.raw_confirmed_text or confirmed_text)
        del self._sessions[session_id]

        return {
            "session_id": session_id,
            "preview_text": preview_text or confirmed_text,
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
    parser.add_argument("--realtime-punc-model", default="iic/punc_ct-transformer_zh-cn-common-vad_realtime-vocab272727")
    parser.add_argument("--final-punc-model", default="iic/punc_ct-transformer_zh-cn-common-vocab272727-pytorch")
    parser.add_argument("--transcription-mode", default="fast", choices=["fast", "formatted"])
    parser.add_argument("--int8", default="0")
    parser.add_argument("--stride-samples", type=int, default=9600)
    parser.add_argument("--endpoint-silence-ms", type=int, default=420)
    parser.add_argument("--endpoint-rms-threshold", type=float, default=0.0065)
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
            realtime_punc_model=str(args.realtime_punc_model or "").strip(),
            final_punc_model=str(args.final_punc_model or "").strip(),
            transcription_mode=str(args.transcription_mode or "fast").strip().lower(),
            stride_samples=max(1600, int(args.stride_samples)),
            endpoint_silence_ms=max(120, int(args.endpoint_silence_ms)),
            endpoint_rms_threshold=max(0.001, float(args.endpoint_rms_threshold)),
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
