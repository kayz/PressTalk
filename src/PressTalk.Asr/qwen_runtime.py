#!/usr/bin/env python3
import argparse
import json
import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Optional

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
    asr_final_model: str
    asr_preview_model: str
    llm_model: str
    device: str
    semantic_enabled: bool


class QwenRuntime:
    def __init__(self, config: RuntimeConfig) -> None:
        self.config = config
        self._asr_final = None
        self._asr_preview = None
        self._llm = None
        self._torch = None
        self._qwen_asr_cls = None
        self._transformers = None
        self._source_logged: set[str] = set()

    def _ensure_torch(self):
        if self._torch is not None:
            return

        import torch  # type: ignore

        self._torch = torch

    def _ensure_asr_framework(self) -> None:
        if self._qwen_asr_cls is not None:
            return

        from qwen_asr import Qwen3ASRModel  # type: ignore

        self._qwen_asr_cls = Qwen3ASRModel

    def _ensure_llm_framework(self) -> None:
        if self._transformers is not None:
            return

        import transformers  # type: ignore
        transformers.logging.set_verbosity_error()

        self._transformers = transformers

    def _torch_dtype(self):
        self._ensure_torch()
        if self.config.device == "cpu":
            return self._torch.float32
        return self._torch.float16

    @staticmethod
    def _hf_cache_root() -> Path:
        cache = os.environ.get("HF_HUB_CACHE")
        if cache:
            return Path(cache)

        hf_home = os.environ.get("HF_HOME")
        if hf_home:
            return Path(hf_home) / "hub"

        return Path.home() / ".cache" / "huggingface" / "hub"

    def _resolve_cached_snapshot(self, model_ref: str) -> Optional[str]:
        # Model path already provided by caller.
        if Path(model_ref).exists():
            return model_ref

        if "/" not in model_ref:
            return None

        repo_dir = self._hf_cache_root() / f"models--{model_ref.replace('/', '--')}"
        snapshots_dir = repo_dir / "snapshots"
        if not snapshots_dir.is_dir():
            return None

        candidates = []
        main_ref = repo_dir / "refs" / "main"
        if main_ref.exists():
            try:
                revision = main_ref.read_text(encoding="utf-8").strip()
                if revision:
                    candidates.append(snapshots_dir / revision)
            except Exception:
                pass

        try:
            snapshot_dirs = sorted(
                [p for p in snapshots_dir.iterdir() if p.is_dir()],
                key=lambda p: p.stat().st_mtime,
                reverse=True,
            )
            candidates.extend(snapshot_dirs)
        except Exception:
            return None

        seen = set()
        for candidate in candidates:
            key = str(candidate)
            if key in seen:
                continue
            seen.add(key)

            if (candidate / "config.json").exists():
                return str(candidate)

        return None

    def _resolve_model_source(self, model_ref: str) -> tuple[str, bool]:
        snapshot = self._resolve_cached_snapshot(model_ref)
        if snapshot:
            return snapshot, True
        return model_ref, False

    def _log_model_source(self, kind: str, requested: str, resolved: str, local_only: bool) -> None:
        key = f"{kind}|{requested}|{resolved}|{local_only}"
        if key in self._source_logged:
            return
        self._source_logged.add(key)
        print(
            f"[Qwen.Runtime.Py] {kind} source requested='{requested}' resolved='{resolved}' local_only={local_only}",
            file=sys.stderr,
            flush=True,
        )

    def _load_asr(self, mode: str):
        self._ensure_asr_framework()

        if mode == "preview":
            if self.config.asr_preview_model == self.config.asr_final_model:
                return self._load_asr("final")
            if self._asr_preview is None:
                model_ref, local_only = self._resolve_model_source(self.config.asr_preview_model)
                self._log_model_source("asr-preview", self.config.asr_preview_model, model_ref, local_only)
                self._asr_preview = self._qwen_asr_cls.from_pretrained(
                    pretrained_model_name_or_path=model_ref,
                    trust_remote_code=True,
                    device_map=self.config.device,
                    torch_dtype=self._torch_dtype(),
                    low_cpu_mem_usage=True,
                    local_files_only=local_only,
                )
            return self._asr_preview

        if self._asr_final is None:
            model_ref, local_only = self._resolve_model_source(self.config.asr_final_model)
            self._log_model_source("asr-final", self.config.asr_final_model, model_ref, local_only)
            self._asr_final = self._qwen_asr_cls.from_pretrained(
                pretrained_model_name_or_path=model_ref,
                trust_remote_code=True,
                device_map=self.config.device,
                torch_dtype=self._torch_dtype(),
                low_cpu_mem_usage=True,
                local_files_only=local_only,
            )
        return self._asr_final

    def _load_llm(self):
        self._ensure_llm_framework()

        if self._llm is None:
            model_ref, local_only = self._resolve_model_source(self.config.llm_model)
            self._log_model_source("llm", self.config.llm_model, model_ref, local_only)
            self._llm = self._transformers.pipeline(  # type: ignore[arg-type]
                task="text-generation",
                model=model_ref,
                trust_remote_code=True,
                device_map=self.config.device,
                model_kwargs={"torch_dtype": self._torch_dtype()},
                local_files_only=local_only,
            )
        return self._llm

    @staticmethod
    def _extract_text(result: Any) -> str:
        if hasattr(result, "text"):
            text = getattr(result, "text", None)
            if isinstance(text, str):
                return text.strip()

        if isinstance(result, str):
            return result.strip()

        if isinstance(result, dict):
            text = result.get("text")
            if isinstance(text, str):
                return text.strip()
            return str(result).strip()

        if isinstance(result, list) and result:
            first = result[0]
            if hasattr(first, "text"):
                text = getattr(first, "text", None)
                if isinstance(text, str):
                    return text.strip()
            if isinstance(first, dict):
                text = first.get("text") or first.get("generated_text")
                if isinstance(text, str):
                    return text.strip()
            return str(first).strip()

        return str(result).strip()

    def transcribe(self, audio_path: str, mode: str, language: str) -> Dict[str, Any]:
        started = time.perf_counter()
        model = self._load_asr(mode)

        if not language:
            language = "auto"

        requested_language = None if language == "auto" else language

        output = model.transcribe(
            audio=[audio_path],
            language=requested_language,
            return_time_stamps=False,
        )

        text = self._extract_text(output)
        return {
            "text": text,
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
        }

    def preload(self, include_preview: bool, include_semantic: bool) -> Dict[str, Any]:
        started = time.perf_counter()
        self._load_asr("final")

        if include_preview and self.config.asr_preview_model != self.config.asr_final_model:
            self._load_asr("preview")

        if include_semantic and self.config.semantic_enabled:
            self._load_llm()

        return {
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
            "preview_loaded": bool(include_preview),
            "semantic_loaded": bool(include_semantic and self.config.semantic_enabled),
        }

    def normalize(
        self,
        text: str,
        language: str,
        scenario: str = "default",
        preserve_structured_items: bool = False,
    ) -> Dict[str, Any]:
        if not self.config.semantic_enabled:
            return {"text": text, "duration_ms": 0.0}

        cleaned = (text or "").strip()
        if not cleaned:
            return {"text": "", "duration_ms": 0.0}

        started = time.perf_counter()
        llm = self._load_llm()

        prompt = (
            "You are a deterministic transcript light-editor.\n"
            "Rules:\n"
            "1) remove filler words and speech disfluencies;\n"
            "2) keep original meaning;\n"
            "3) keep the same language as source;\n"
            "4) only do light edits: grammar, punctuation, spacing, and sentence boundaries;\n"
            "5) keep original wording and term choices whenever possible;\n"
            "6) split very long text into readable paragraphs, but do not change content order;\n"
            "7) do NOT summarize, do NOT restructure, do NOT convert into bullet lists, do NOT add tasks.\n"
            "Output requirements:\n"
            "- return only the final cleaned text;\n"
            "- no explanations, no extra prefix.\n\n"
            f"Scenario: {scenario}\n"
            f"Preserve structured items hint: {preserve_structured_items}\n"
            f"Language hint: {language or 'auto'}\n"
            f"Transcript: {cleaned}\n"
            "Output:"
        )

        try:
            generated = llm(
                prompt,
                max_new_tokens=96,
                do_sample=False,
                return_full_text=False,
            )
            generated_text = self._extract_text(generated)
        except Exception:
            generated_text = ""

        if generated_text.startswith(("Output:", "输出:", "Normalized:", "Result:")):
            generated_text = generated_text.split(":", 1)[1].strip()

        generated_text = generated_text.strip().strip('"').strip()
        if not generated_text:
            generated_text = cleaned

        return {
            "text": generated_text,
            "duration_ms": round((time.perf_counter() - started) * 1000.0, 2),
        }


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PressTalk Qwen runtime")
    parser.add_argument("--asr-final-model", required=True)
    parser.add_argument("--asr-preview-model", required=True)
    parser.add_argument("--llm-model", required=True)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--semantic-enabled", default="1")
    return parser


def make_ok(payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    base: Dict[str, Any] = {"ok": True}
    if payload:
        base.update(payload)
    return base


def make_error(message: str) -> Dict[str, Any]:
    return {"ok": False, "error": message}


def main() -> int:
    parser = make_parser()
    args = parser.parse_args()

    runtime = QwenRuntime(
        RuntimeConfig(
            asr_final_model=args.asr_final_model,
            asr_preview_model=args.asr_preview_model,
            llm_model=args.llm_model,
            device=args.device,
            semantic_enabled=args.semantic_enabled == "1",
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
                payload = make_ok({"runtime_version": "qwen-runtime-3"})
            elif action == "preload":
                include_preview = bool(req.get("include_preview", False))
                include_semantic = bool(req.get("include_semantic", False))
                payload = make_ok(runtime.preload(include_preview, include_semantic))
            elif action == "asr":
                audio_path = req.get("audio_path")
                mode = req.get("mode", "final")
                language = req.get("language", "auto")
                if not audio_path or not isinstance(audio_path, str):
                    raise ValueError("audio_path is required")
                payload = make_ok(runtime.transcribe(audio_path, mode, language))
            elif action == "normalize":
                text = req.get("text", "")
                language = req.get("language", "auto")
                scenario = req.get("scenario", "default")
                preserve_structured_items = bool(req.get("preserve_structured_items", False))
                if not isinstance(text, str):
                    raise ValueError("text must be a string")
                payload = make_ok(runtime.normalize(text, language, scenario, preserve_structured_items))
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
