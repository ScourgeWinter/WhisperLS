import argparse
from faster_whisper import WhisperModel
from huggingface_hub import scan_cache_dir, snapshot_download
from huggingface_hub.errors import LocalEntryNotFoundError
from pathlib import Path
from tqdm import tqdm
from xml.sax.saxutils import escape
import os
import re
import sys
import zipfile
import math
from collections import Counter
from types import SimpleNamespace
import json
import threading
import time
import shutil
import uuid
import hashlib
from contextlib import contextmanager
from datetime import datetime, timezone
from huggingface_hub import constants as hf_constants

os.environ.setdefault("HF_HUB_DOWNLOAD_TIMEOUT", "30")

TOKEN_RE = re.compile(r"[A-Za-zÀ-ÖØ-öø-ÿ']+")
TOPIC_STOPWORDS = {
    "a", "ad", "al", "allo", "ai", "agli", "all", "agl", "alla", "alle", "con", "col",
    "da", "dal", "dallo", "dai", "dagli", "dall", "dagl", "dalla", "dalle", "di", "del",
    "dello", "dei", "degli", "dell", "degl", "della", "delle", "in", "nel", "nello", "nei",
    "negli", "nell", "negl", "nella", "nelle", "su", "sul", "sullo", "sui", "sugli", "sull",
    "sugl", "sulla", "sulle", "per", "tra", "fra", "e", "ed", "o", "oppure", "ma", "anche",
    "come", "che", "chi", "cui", "non", "no", "si", "se", "sua", "suo", "suoi", "sue",
    "mia", "mio", "miei", "mie", "tuo", "tuoi", "tua", "tue", "nostro", "nostri", "nostra",
    "nostre", "vostro", "vostri", "vostra", "vostre", "loro", "io", "tu", "lui", "lei",
    "noi", "voi", "mi", "ti", "ci", "vi", "gli", "le", "lo", "la", "li", "un", "una", "uno",
    "il", "i", "l", "d"
}

DEFAULT_MODEL = "medium"
SUPPORTED_MODELS = {
    "tiny.en": "Systran/faster-whisper-tiny.en",
    "tiny": "Systran/faster-whisper-tiny",
    "base.en": "Systran/faster-whisper-base.en",
    "base": "Systran/faster-whisper-base",
    "small.en": "Systran/faster-whisper-small.en",
    "small": "Systran/faster-whisper-small",
    "medium.en": "Systran/faster-whisper-medium.en",
    "medium": "Systran/faster-whisper-medium",
    "large-v1": "Systran/faster-whisper-large-v1",
    "large-v2": "Systran/faster-whisper-large-v2",
    "large-v3": "Systran/faster-whisper-large-v3",
    "large": "Systran/faster-whisper-large-v3",
    "distil-large-v2": "Systran/faster-distil-whisper-large-v2",
    "distil-medium.en": "Systran/faster-distil-whisper-medium.en",
    "distil-small.en": "Systran/faster-distil-whisper-small.en",
    "distil-large-v3": "Systran/faster-distil-whisper-large-v3",
    "distil-large-v3.5": "distil-whisper/distil-large-v3.5-ct2",
    "large-v3-turbo": "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
    "turbo": "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
}
SUPPORTED_MEDIA_EXTENSIONS = {
    ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".wma",
    ".mp4", ".mov", ".mkv", ".avi", ".webm",
}

WINDOWS_RESERVED_NAMES = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{index}" for index in range(1, 10)),
    *(f"LPT{index}" for index in range(1, 10)),
}
_MODEL_INSTANCE_CACHE = {}
_EVENT_CONTEXT = {}
_BATCH_CONTROL_PATH = None


class BatchJobCancelled(Exception):
    """Richiesta cooperativa di saltare il solo lavoro corrente della coda."""


def check_batch_control() -> None:
    if _BATCH_CONTROL_PATH is None or not _BATCH_CONTROL_PATH.exists():
        return
    try:
        command = json.loads(_BATCH_CONTROL_PATH.read_text(encoding="utf-8"))
        if (
            command.get("action") == "cancel-current"
            and str(command.get("jobId")) == str(_EVENT_CONTEXT.get("jobId"))
        ):
            _BATCH_CONTROL_PATH.unlink(missing_ok=True)
            raise BatchJobCancelled("Lavoro corrente annullato dall'utente.")
    except json.JSONDecodeError:
        return

def get_supported_model_names():
    return sorted(SUPPORTED_MODELS)

def get_model_repo_id(model_name: str) -> str:
    model_path = Path(model_name)
    if model_path.exists():
        return str(model_path)

    if re.match(r"[^/]+/[^/]+", model_name):
        return model_name

    repo_id = SUPPORTED_MODELS.get(model_name)
    if repo_id is None:
        raise ValueError(
            f"Modello non valido: {model_name}. "
            f"Valori supportati: {', '.join(SUPPORTED_MODELS)}"
        )

    return repo_id

def normalize_spacing(text: str) -> str:
    text = re.sub(r"\s+", " ", text).strip()
    text = re.sub(r"\s+([,.;:!?])", r"\1", text)
    text = re.sub(r"([A-Za-zÀ-ÖØ-öø-ÿ])\s+[’']\s+([A-Za-zÀ-ÖØ-öø-ÿ])", r"\1'\2", text)
    text = re.sub(r"\(\s+", "(", text)
    text = re.sub(r"\s+\)", ")", text)
    return text

def format_srt_time(seconds: float) -> str:
    ms = max(0, int(round(seconds * 1000)))
    h = ms // 3600000
    ms %= 3600000
    m = ms // 60000
    ms %= 60000
    s = ms // 1000
    ms %= 1000
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"

def linearize_segments(segments) -> str:
    parts = []
    for seg in segments:
        text = (seg.text or "").strip()
        if text:
            parts.append(text)

    return normalize_spacing(" ".join(parts))

def tokenize_for_topic(text: str):
    tokens = []
    for raw in TOKEN_RE.findall(text.lower()):
        token = raw.strip("'")
        if len(token) < 3:
            continue
        if token in TOPIC_STOPWORDS:
            continue
        tokens.append(token)
    return tokens

def cosine_similarity(a: Counter, b: Counter) -> float:
    if not a and not b:
        return 1.0
    if not a or not b:
        return 0.0

    dot = 0
    for token, value in a.items():
        dot += value * b.get(token, 0)
    norm_a = math.sqrt(sum(v * v for v in a.values()))
    norm_b = math.sqrt(sum(v * v for v in b.values()))
    if norm_a == 0 or norm_b == 0:
        return 0.0
    return dot / (norm_a * norm_b)

def build_context_paragraphs(
    segments,
    *,
    window_size: int = 3,
    pause_split_sec: float = 3.2,
    sim_threshold: float = 0.16,
    min_words_per_paragraph: int = 70,
    max_words_per_paragraph: int = 230
):
    items = []
    for seg in segments:
        text = (seg.text or "").strip()
        if not text:
            continue
        items.append(
            {
                "text": text,
                "start": getattr(seg, "start", 0.0),
                "end": getattr(seg, "end", 0.0),
                "tokens": tokenize_for_topic(text),
            }
        )

    if not items:
        return []
    if len(items) == 1:
        return [normalize_spacing(items[0]["text"])]

    candidate_splits = set()
    for i in range(1, len(items)):
        pause = max(0.0, items[i]["start"] - items[i - 1]["end"])
        if pause >= pause_split_sec:
            candidate_splits.add(i)
            continue

        left_tokens = []
        right_tokens = []
        left_start = max(0, i - window_size)
        right_end = min(len(items), i + window_size)

        for j in range(left_start, i):
            left_tokens.extend(items[j]["tokens"])
        for j in range(i, right_end):
            right_tokens.extend(items[j]["tokens"])

        sim = cosine_similarity(Counter(left_tokens), Counter(right_tokens))
        prev_ends_sentence = items[i - 1]["text"].rstrip().endswith((".", "?", "!", "…"))
        if prev_ends_sentence and sim < sim_threshold:
            candidate_splits.add(i)

    paragraphs = []
    current_texts = []
    current_words = 0
    for i, item in enumerate(items):
        if current_texts and i in candidate_splits and current_words >= min_words_per_paragraph:
            paragraph = normalize_spacing(" ".join(current_texts))
            if paragraph:
                paragraphs.append(paragraph)
            current_texts = []
            current_words = 0

        current_texts.append(item["text"])
        current_words += len(normalize_spacing(item["text"]).split())

        if (
            current_words >= max_words_per_paragraph
            and item["text"].rstrip().endswith((".", "?", "!", "…"))
        ):
            paragraph = normalize_spacing(" ".join(current_texts))
            if paragraph:
                paragraphs.append(paragraph)
            current_texts = []
            current_words = 0

    if current_texts:
        paragraph = normalize_spacing(" ".join(current_texts))
        if paragraph:
            paragraphs.append(paragraph)

    return paragraphs

def format_track_end_label(index: int, source_name: str) -> str:
    return f"FINE TRACCIA AUDIO N. {index} — {source_name}"


def format_track_end_separator(index: int, source_name: str) -> str:
    return f"//////////////////// {format_track_end_label(index, source_name)} ////////////////////"


def write_docx(path: Path, text: str, *, track_sections=None) -> None:
    body_parts = []
    if track_sections:
        for section_index, (source_name, paragraphs) in enumerate(track_sections, start=1):
            for paragraph in paragraphs or [""]:
                if paragraph:
                    body_parts.append(
                        f'<w:p><w:r><w:t xml:space="preserve">{escape(paragraph)}</w:t></w:r></w:p>'
                    )
                else:
                    body_parts.append("<w:p/>")

            if section_index < len(track_sections):
                label = escape(format_track_end_label(section_index, source_name))
                body_parts.append(
                    '<w:p><w:pPr><w:pBdr>'
                    '<w:top w:val="single" w:sz="8" w:space="8" w:color="29B6FF"/>'
                    '<w:bottom w:val="single" w:sz="8" w:space="8" w:color="29B6FF"/>'
                    '</w:pBdr><w:spacing w:before="240" w:after="240"/>'
                    '<w:jc w:val="center"/></w:pPr>'
                    '<w:r><w:rPr><w:b/><w:color w:val="29B6FF"/></w:rPr>'
                    f'<w:t xml:space="preserve">{label}</w:t></w:r></w:p>'
                )
                body_parts.append('<w:p><w:r><w:br w:type="page"/></w:r></w:p>')
    else:
        paragraphs = [line.strip() for line in text.splitlines()]
        if not paragraphs:
            paragraphs = [""]
        for line in paragraphs:
            if line:
                body_parts.append(
                    f'<w:p><w:r><w:t xml:space="preserve">{escape(line)}</w:t></w:r></w:p>'
                )
            else:
                body_parts.append("<w:p/>")

    document_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
        "<w:body>"
        + "".join(body_parts)
        + '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" '
        'w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr>'
        "</w:body></w:document>"
    )

    content_types_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/word/document.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
        "</Types>"
    )

    rels_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
        'Target="word/document.xml"/>'
        "</Relationships>"
    )

    document_rels_xml = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
    )

    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as docx:
        docx.writestr("[Content_Types].xml", content_types_xml)
        docx.writestr("_rels/.rels", rels_xml)
        docx.writestr("word/document.xml", document_xml)
        docx.writestr("word/_rels/document.xml.rels", document_rels_xml)


def validate_language(language: str) -> str:
    normalized = language.strip().lower()
    if normalized == "auto":
        return normalized
    if not re.fullmatch(r"[a-z]{2,3}", normalized):
        raise ValueError(
            "La lingua deve essere 'auto' oppure un codice ISO di 2-3 lettere (es. it, en, ast)."
        )
    return normalized


def positive_beam_size(value: str) -> int:
    beam_size = int(value)
    if not 1 <= beam_size <= 20:
        raise ValueError("Il beam size deve essere compreso tra 1 e 20.")
    return beam_size


def parse_time_offset(value: str) -> float:
    normalized = value.strip()
    if not normalized:
        raise ValueError("L'intervallo temporale non può essere vuoto.")
    parts = normalized.split(":")
    try:
        if len(parts) == 1:
            seconds = float(parts[0])
        elif len(parts) == 2:
            seconds = int(parts[0]) * 60 + float(parts[1])
        elif len(parts) == 3:
            seconds = int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
        else:
            raise ValueError
    except ValueError as exc:
        raise ValueError(
            "Usa secondi, MM:SS oppure HH:MM:SS per l'intervallo temporale."
        ) from exc
    if seconds < 0:
        raise ValueError("L'intervallo temporale non può essere negativo.")
    return seconds


def validate_media_path(path: Path) -> Path:
    if not path.exists():
        raise FileNotFoundError(f"File non trovato: {path}")
    if not path.is_file():
        raise ValueError(f"Il percorso non è un file: {path}")
    if path.suffix.lower() not in SUPPORTED_MEDIA_EXTENSIONS:
        supported = ", ".join(sorted(SUPPORTED_MEDIA_EXTENSIONS))
        raise ValueError(f"Formato non supportato: {path.suffix or '(senza estensione)'}. Formati: {supported}")
    return path


def validate_output_name(value: str) -> str:
    """Valida un nome base portabile e sicuro per Windows, senza estensione."""
    name = (value or "").strip()
    if not name:
        raise ValueError("Il nome dell'output non può essere vuoto.")
    if name.endswith((" ", ".")):
        raise ValueError("Il nome dell'output non può terminare con spazio o punto.")
    if any(character in name for character in '<>:"/\\|?*'):
        raise ValueError("Il nome dell'output contiene caratteri non validi per Windows.")
    if Path(name).suffix:
        raise ValueError("Inserisci il nome dell'output senza estensione.")
    if name.split(".", 1)[0].upper() in WINDOWS_RESERVED_NAMES:
        raise ValueError("Il nome dell'output è riservato da Windows.")
    return name


def choose_available_output_name(
    output_directory: Path,
    requested_name: str,
    output_formats,
) -> str:
    """Aggiunge (2), (3), ... quando uno degli output richiesti esiste già."""
    base_name = validate_output_name(requested_name)
    candidate = base_name
    index = 2
    while (output_directory / f"{candidate}.whisperls.json").exists() or any(
        (output_directory / f"{candidate}.{kind}").exists() for kind in output_formats
    ):
        candidate = f"{base_name} ({index})"
        index += 1
    return candidate


def _write_srt(path: Path, segments, *, word_timestamps: bool) -> None:
    cues = []
    if word_timestamps:
        for seg in segments:
            words = getattr(seg, "words", None) or []
            for word in words:
                text = (getattr(word, "word", "") or "").strip()
                start = getattr(word, "start", None)
                end = getattr(word, "end", None)
                if text and start is not None and end is not None:
                    cues.append((float(start), float(end), text))

    if not cues:
        cues = [
            (float(seg.start), float(seg.end), (seg.text or "").strip())
            for seg in segments
            if (seg.text or "").strip()
        ]

    with path.open("w", encoding="utf-8", newline="\n") as output:
        for index, (start, end, text) in enumerate(cues, start=1):
            output.write(f"{index}\n")
            output.write(f"{format_srt_time(start)} --> {format_srt_time(end)}\n")
            output.write(text + "\n\n")


@contextmanager
def output_lock(directory: Path, name: str):
    """OS-owned locks survive neither a crash nor process termination."""
    directory.mkdir(parents=True, exist_ok=True)
    lock_key = hashlib.sha256(os.path.normcase(name).encode("utf-8")).hexdigest()
    with (directory / f".whisperls-output-{lock_key}.lock").open("a+b") as lock:
        if lock.tell() == 0:
            lock.write(b"0")
            lock.flush()
        lock.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise RuntimeError("Un'altra istanza sta scrivendo questo output.") from exc
        try:
            yield
        finally:
            lock.seek(0)
            if os.name == "nt":
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def write_transcription_outputs(audio_path: Path, segments, **kwargs):
    directory = kwargs.get("output_dir") or audio_path.parent
    name = validate_output_name(kwargs.get("output_name") or audio_path.stem + "_trascrizione")
    with output_lock(directory, name):
        return _write_transcription_outputs_unlocked(audio_path, segments, **kwargs)


def _write_transcription_outputs_unlocked(
    audio_path: Path,
    segments,
    *,
    output_dir: Path | None = None,
    word_timestamps: bool = False,
    overwrite: bool = False,
    output_formats=None,
    track_sections=None,
    output_name: str | None = None,
):
    if track_sections is not None:
        track_sections = list(track_sections)
    selected_formats = tuple(
        dict.fromkeys(
            ("txt", "srt", "docx") if output_formats is None else output_formats
        )
    )
    unsupported_formats = set(selected_formats) - {"txt", "srt", "docx"}
    if unsupported_formats:
        raise ValueError(
            "Formato di output non supportato: " + ", ".join(sorted(unsupported_formats))
        )
    if not selected_formats:
        raise ValueError("Seleziona almeno un formato di output.")

    output_directory = output_dir or audio_path.parent
    output_directory.mkdir(parents=True, exist_ok=True)
    base_name = validate_output_name(output_name or audio_path.stem + "_trascrizione")
    final_paths = {
        "txt": output_directory / f"{base_name}.txt",
        "srt": output_directory / f"{base_name}.srt",
        "docx": output_directory / f"{base_name}.docx",
    }

    existing = [final_paths[kind] for kind in selected_formats if final_paths[kind].exists()]
    if existing and not overwrite:
        names = ", ".join(path.name for path in existing)
        raise FileExistsError(
            f"Output già esistente ({names}). Usa --overwrite per sostituirlo."
        )

    docx_track_sections = None
    if track_sections:
        docx_track_sections = []
        context_paragraphs = []
        text_sections = []
        for source_name, section_segments in track_sections:
            section_paragraphs = build_context_paragraphs(section_segments)
            if not section_paragraphs:
                linear_text = linearize_segments(section_segments)
                section_paragraphs = [linear_text] if linear_text else []
            docx_track_sections.append((source_name, section_paragraphs))
            context_paragraphs.extend(section_paragraphs)
            text_sections.append("\n\n".join(section_paragraphs))

        txt_parts = []
        for index, ((source_name, _), section_text) in enumerate(
            zip(track_sections, text_sections), start=1
        ):
            txt_parts.append(section_text)
            if index < len(track_sections):
                txt_parts.append(format_track_end_separator(index, source_name))
        paragraph_text = "\n\n".join(txt_parts)
    else:
        context_paragraphs = build_context_paragraphs(segments)
        if not context_paragraphs:
            linear_text = linearize_segments(segments)
            context_paragraphs = [linear_text] if linear_text else []
        paragraph_text = "\n\n".join(context_paragraphs)

    transaction_id = uuid.uuid4().hex
    temporary_paths = {
        kind: output_directory / f".whisperls-{transaction_id}.{kind}.tmp"
        for kind, path in final_paths.items()
        if kind in selected_formats
    }
    backups = {}
    published = []
    cleanup_backups = True
    try:
        if "txt" in temporary_paths:
            temporary_paths["txt"].write_text(
                paragraph_text + "\n", encoding="utf-8", newline="\n"
            )
        if "srt" in temporary_paths:
            _write_srt(temporary_paths["srt"], segments, word_timestamps=word_timestamps)
        if "docx" in temporary_paths:
            write_docx(
                temporary_paths["docx"],
                paragraph_text,
                track_sections=docx_track_sections,
            )
        for kind in selected_formats:
            final_path = final_paths[kind]
            if overwrite and final_path.exists():
                backup = output_directory / f".whisperls-{transaction_id}.{kind}.bak"
                shutil.copy2(final_path, backup)
                backups[kind] = backup
            if overwrite:
                os.replace(temporary_paths[kind], final_path)
            elif os.name == "nt":
                # Windows rename fails atomically if the destination already exists.
                os.rename(temporary_paths[kind], final_path)
            else:
                os.link(temporary_paths[kind], final_path)
                temporary_paths[kind].unlink()
            published.append(kind)
    except BaseException:
        cleanup_backups = False
        for kind in reversed(published):
            if kind in backups:
                os.replace(backups[kind], final_paths[kind])
            else:
                final_paths[kind].unlink(missing_ok=True)
        cleanup_backups = True
        raise
    finally:
        for temporary_path in temporary_paths.values():
            try:
                temporary_path.unlink(missing_ok=True)
            except OSError:
                pass
        if cleanup_backups:
            for backup in backups.values():
                backup.unlink(missing_ok=True)

    return (
        final_paths["txt"] if "txt" in selected_formats else None,
        final_paths["srt"] if "srt" in selected_formats else None,
        final_paths["docx"] if "docx" in selected_formats else None,
        context_paragraphs,
    )

class HFTqdm(tqdm):
    _lock = threading.Lock()
    _bars: dict = {}
    _completed_current: float = 0.0
    _completed_total: float = 0.0
    _last_emit: float = 0.0
    _heartbeat: "threading.Thread | None" = None

    def __init__(self, *args, **kwargs):
        kwargs.pop("name", None)
        kwargs["disable"] = False
        super().__init__(*args, **kwargs)
        self._progress_key = id(self)
        self._closing = False
        self._progress_counted = False
        with self._lock:
            if HFTqdm._heartbeat is None or not HFTqdm._heartbeat.is_alive():
                HFTqdm._heartbeat = _DownloadHeartbeat()
                HFTqdm._heartbeat.start()
        self._register_progress()

    def display(self, *args, **kwargs):
        return

    def clear(self, *args, **kwargs):
        return

    def refresh(self, *args, **kwargs):
        return

    def set_description(self, desc=None, refresh=True):
        super().set_description(desc=desc, refresh=False)

    def update(self, n=1):
        result = super().update(n)
        self._register_progress()
        return result

    def close(self):
        self._closing = True
        self._register_progress()
        with self._lock:
            key = getattr(self, "_progress_key", id(self))
            bar = self._bars.pop(key, None)
            if bar is not None and not self._progress_counted:
                HFTqdm._completed_current += min(bar["current"], bar["total"])
                HFTqdm._completed_total += bar["total"]
                self._progress_counted = True
        HFTqdm._emit_progress(force=True)
        super().close()

    def _register_progress(self, *, force: bool = False) -> None:
        total = self.total
        current = self.n
        if total is None or total <= 0:
            return

        with self._lock:
            self._bars[getattr(self, "_progress_key", id(self))] = {
                "current": max(0, float(current)),
                "total": max(1, float(total)),
                "unit": self.unit or "",
            }

        if not force:
            now = time.monotonic()
            if now - HFTqdm._last_emit < 0.3:
                return

        HFTqdm._emit_progress()

    @classmethod
    def _emit_progress(cls, *, force: bool = False) -> None:
        with cls._lock:
            bars = list(cls._bars.values())
            completed_current = cls._completed_current
            completed_total = cls._completed_total
            if not bars and completed_total <= 0:
                return

        byte_bars = [bar for bar in bars if bar["unit"] == "B"]
        selected_bars = byte_bars if byte_bars else bars
        total_value = completed_total + sum(bar["total"] for bar in selected_bars)
        current_value = completed_current + sum(min(bar["current"], bar["total"]) for bar in selected_bars)
        if total_value <= 0:
            return

        percent = max(0.0, min(100.0, (current_value / total_value) * 100.0))
        with cls._lock:
            cls._last_emit = time.monotonic()
        print(
            "__WHISPER_PROGRESS__ "
            + json.dumps(
                {
                    "phase": "download",
                    "percent": percent,
                    "downloadedBytes": current_value,
                    "totalBytes": total_value,
                },
                ensure_ascii=False,
            ),
            flush=True,
        )

    @classmethod
    def _reset(cls) -> None:
        with cls._lock:
            cls._bars.clear()
            cls._completed_current = 0.0
            cls._completed_total = 0.0
            cls._last_emit = 0.0


class _DownloadHeartbeat(threading.Thread):
    def __init__(self) -> None:
        super().__init__(daemon=True)

    def run(self) -> None:
        while True:
            time.sleep(0.5)
            HFTqdm._emit_progress()

def validate_model_snapshot(model_path: str) -> None:
    path = Path(model_path)
    if not path.exists():
        raise LocalEntryNotFoundError(f"Cartella modello non trovata: {path}")

    weight_files = list(path.glob("model.bin")) + list(path.glob("*.safetensors"))
    complete_weight_files = [file for file in weight_files if file.is_file() and file.stat().st_size > 0]
    if not complete_weight_files:
        raise LocalEntryNotFoundError(
            f"Modello incompleto: manca model.bin o un file .safetensors in {path}"
        )
    if not (path / "config.json").is_file():
        raise LocalEntryNotFoundError(f"Modello incompleto: manca config.json in {path}")
    tokenizer_files = [path / "tokenizer.json", *path.glob("vocabulary.*")]
    if not any(file.is_file() and file.stat().st_size > 0 for file in tokenizer_files):
        raise LocalEntryNotFoundError(
            f"Modello incompleto: manca tokenizer.json o vocabulary.* in {path}"
        )

def get_hf_cache_repo_dir(repo_id: str) -> Path:
    repo_cache_name = "models--" + repo_id.replace("/", "--")
    return Path(hf_constants.HUGGINGFACE_HUB_CACHE) / repo_cache_name

def has_complete_snapshot(repo_cache_dir: Path) -> bool:
    snapshots_dir = repo_cache_dir / "snapshots"
    if not snapshots_dir.exists():
        return False

    for snapshot_dir in snapshots_dir.iterdir():
        if not snapshot_dir.is_dir():
            continue

        try:
            validate_model_snapshot(str(snapshot_dir))
            return True
        except LocalEntryNotFoundError:
            continue

    return False

def parse_args():
    supported_models = get_supported_model_names()
    model_help = (
        f"Modello Whisper da usare (default: {DEFAULT_MODEL}). "
        f"Modelli disponibili: {', '.join(supported_models)}."
    )
    parser = argparse.ArgumentParser(
        description="Trascrive file audio/video con faster-whisper e genera TXT, SRT e DOCX."
    )
    parser.add_argument(
        "audio_file",
        nargs="?",
        help="Percorso del file audio o video da trascrivere.",
    )
    parser.add_argument(
        "model",
        nargs="?",
        default=DEFAULT_MODEL,
        help=model_help,
    )
    parser.add_argument(
        "--input-file",
        action="append",
        dest="input_files",
        help=(
            "Aggiunge una parte audio/video alla stessa trascrizione. "
            "Ripeti l'opzione rispettando l'ordine delle parti."
        ),
    )
    parser.add_argument(
        "--resume-checkpoint",
        type=Path,
        help="Riprende una trascrizione dal checkpoint .whisperls.json indicato.",
    )
    parser.add_argument(
        "--clip-start",
        type=parse_time_offset,
        help="Inizio dell'intervallo: secondi, MM:SS oppure HH:MM:SS.",
    )
    parser.add_argument(
        "--clip-end",
        type=parse_time_offset,
        help="Fine dell'intervallo: secondi, MM:SS oppure HH:MM:SS.",
    )
    parser.add_argument(
        "--device",
        choices=["auto", "cuda", "cpu"],
        default="auto",
        help="Dispositivo da usare. 'auto' prova prima CUDA e poi CPU.",
    )
    parser.add_argument(
        "--language",
        type=validate_language,
        default="it",
        help="Lingua da forzare per la trascrizione (default: it). Usa 'auto' per auto-detect.",
    )
    parser.add_argument(
        "--beam-size",
        type=positive_beam_size,
        default=5,
        help="Beam size per la trascrizione (default: 5).",
    )
    parser.add_argument(
        "--word-timestamps",
        action="store_true",
        help="Abilita i timestamp parola per parola.",
    )
    parser.add_argument(
        "--no-vad",
        action="store_true",
        help="Disabilita il voice activity detection filter.",
    )
    parser.add_argument(
        "--list-models",
        action="store_true",
        help="Mostra i modelli supportati ed esce.",
    )
    parser.add_argument(
        "--check-model",
        metavar="MODEL",
        help="Verifica se un modello è già presente nella cache locale senza scaricarlo.",
    )
    parser.add_argument(
        "--model-status-json",
        action="store_true",
        help="Restituisce in JSON lo stato locale di tutti i modelli supportati.",
    )
    parser.add_argument(
        "--download-model",
        metavar="MODEL",
        help="Scarica un modello e termina senza trascrivere file.",
    )
    parser.add_argument(
        "--delete-model",
        metavar="MODEL",
        help="Rimuove dalla cache locale il repository associato al modello.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        help="Cartella in cui salvare gli output (default: cartella del file sorgente).",
    )
    parser.add_argument(
        "--output-name",
        help="Nome base condiviso dagli output, senza estensione.",
    )
    parser.add_argument(
        "--auto-rename",
        action="store_true",
        help="Evita collisioni aggiungendo automaticamente (2), (3), ... al nome.",
    )
    parser.add_argument(
        "--batch-manifest",
        type=Path,
        help="Esegue una coda persistente descritta da un manifest JSON.",
    )
    parser.add_argument(
        "--output-format",
        action="append",
        choices=("txt", "srt", "docx"),
        dest="output_formats",
        help=(
            "Formato da generare. Ripeti l'opzione per più formati; "
            "se omessa vengono creati TXT, SRT e DOCX."
        ),
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Sostituisce eventuali file di output già esistenti.",
    )
    return parser.parse_args()

def build_model_candidates(preferred_device: str):
    device_map = {
        "auto": [
            ("cuda", "float16"),
            ("cuda", "int8_float16"),
            ("cpu", "int8"),
            ("cpu", "float32"),
        ],
        "cuda": [
            ("cuda", "float16"),
            ("cuda", "int8_float16"),
        ],
        "cpu": [
            ("cpu", "int8"),
            ("cpu", "float32"),
        ],
    }
    return device_map[preferred_device]


def emit_progress(phase: str, percent: float, **details) -> None:
    if os.environ.get("WHISPERLS_PROGRESS") != "1":
        return
    payload = {
        "phase": phase,
        "percent": max(0.0, min(100.0, percent)),
        **_EVENT_CONTEXT,
        **details,
    }
    print("__WHISPER_PROGRESS__ " + json.dumps(payload, ensure_ascii=False), flush=True)

def emit_result(operation: str, status: str, **details) -> None:
    if os.environ.get("WHISPERLS_PROGRESS") != "1":
        return
    payload = {"operation": operation, "status": status, **_EVENT_CONTEXT, **details}
    print("__WHISPER_RESULT__ " + json.dumps(payload, ensure_ascii=False), flush=True)


class TranscriptionCheckpoint:
    """Checkpoint JSON atomico, conservato solo finché il lavoro è incompleto."""

    VERSION = 2
    FLUSH_INTERVAL_SECONDS = 5.0
    FLUSH_INTERVAL_SEGMENTS = 20

    def __init__(
        self,
        path: Path,
        *,
        sources,
        model: str,
        requested_device: str,
        actual_device: str,
        compute_type: str,
        language: str,
        beam_size: int,
        vad_enabled: bool,
        word_timestamps: bool,
        output_formats,
        output_name: str | None = None,
        clip_start: float | None = None,
        clip_end: float | None = None,
    ) -> None:
        self.path = path
        self.temporary_path = path.with_name(f".{path.name}.{os.getpid()}.tmp")
        self.segments = []
        self.languages = []
        self.part_states = {}
        self.current_part = 0
        self.part_count = len(sources)
        self.progress = 0.0
        self.last_timestamp = 0.0
        self._segments_since_flush = 0
        self._last_flush = 0.0
        self.metadata = {
            "version": self.VERSION,
            "status": "in_progress",
            "sources": [self._source_metadata(source) for source in sources],
            "configuration": {
                "model": model,
                "requestedDevice": requested_device,
                "actualDevice": actual_device,
                "computeType": compute_type,
                "language": language,
                "beamSize": beam_size,
                "vad": vad_enabled,
                "wordTimestamps": word_timestamps,
                "outputFormats": list(output_formats),
                "outputName": output_name or f"{Path(sources[0]).stem}_trascrizione",
                "clipStart": clip_start,
                "clipEnd": clip_end,
            },
        }

    @classmethod
    def from_file(cls, path: Path):
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValueError(f"Checkpoint non leggibile: {exc}") from exc
        if payload.get("version") != cls.VERSION:
            raise ValueError(
                "Versione del checkpoint non supportata. "
                "Avvia una nuova trascrizione."
            )
        if payload.get("status") != "in_progress":
            raise ValueError("Il checkpoint non rappresenta un lavoro interrotto.")

        sources = payload.get("sources") or []
        if not sources:
            raise ValueError("Il checkpoint non contiene file sorgente.")
        for source in sources:
            source_path = Path(source.get("path", ""))
            if not source_path.is_file():
                raise ValueError(f"File sorgente non trovato: {source_path}")
            stat = source_path.stat()
            if (
                stat.st_size != source.get("size")
                or stat.st_mtime_ns != source.get("modifiedNs")
            ):
                raise ValueError(f"Il file sorgente è cambiato: {source_path.name}")

        instance = cls.__new__(cls)
        instance.path = path
        instance.temporary_path = path.with_name(f".{path.name}.{os.getpid()}.tmp")
        instance.segments = list(payload.get("segments") or [])
        instance.languages = list(payload.get("languages") or [])
        instance.part_states = dict(payload.get("partStates") or {})
        instance.current_part = int(payload.get("currentPart") or 0)
        instance.part_count = int(payload.get("parts") or len(sources))
        instance.progress = float(payload.get("progress") or 0.0)
        instance.last_timestamp = float(payload.get("lastTimestamp") or 0.0)
        instance._segments_since_flush = 0
        instance._last_flush = time.monotonic()
        instance.metadata = {
            "version": payload["version"],
            "status": "in_progress",
            "sources": sources,
            "configuration": payload.get("configuration") or {},
        }
        return instance, payload

    @staticmethod
    def _source_metadata(source: Path) -> dict:
        stat = source.stat()
        return {
            "path": str(source.resolve()),
            "name": source.name,
            "size": stat.st_size,
            "modifiedNs": stat.st_mtime_ns,
            "modifiedUtc": datetime.fromtimestamp(
                stat.st_mtime,
                tz=timezone.utc,
            ).isoformat(),
        }

    @staticmethod
    def _word_payload(word) -> dict:
        return {
            "start": getattr(word, "start", None),
            "end": getattr(word, "end", None),
            "text": getattr(word, "word", ""),
            "probability": getattr(word, "probability", None),
        }

    def add_segment(
        self,
        segment,
        *,
        part: int,
        source_name: str,
        progress: float | None = None,
        source_start: float | None = None,
        source_end: float | None = None,
        timeline_offset: float = 0.0,
    ) -> None:
        self.segments.append(
            {
                "part": part,
                "source": source_name,
                "start": float(getattr(segment, "start", 0.0)),
                "end": float(getattr(segment, "end", 0.0)),
                "sourceStart": source_start,
                "sourceEnd": source_end,
                "timelineOffset": timeline_offset,
                "text": getattr(segment, "text", ""),
                "avgLogProbability": getattr(segment, "avg_logprob", None),
                "compressionRatio": getattr(segment, "compression_ratio", None),
                "noSpeechProbability": getattr(segment, "no_speech_prob", None),
                "words": [
                    self._word_payload(word)
                    for word in (getattr(segment, "words", None) or [])
                ],
            }
        )
        self.current_part = part
        if progress is not None:
            self.progress = max(self.progress, min(100.0, progress))
        self.last_timestamp = max(
            self.last_timestamp,
            float(getattr(segment, "end", 0.0)),
        )
        self._segments_since_flush += 1
        now = time.monotonic()
        if (
            self._segments_since_flush >= self.FLUSH_INTERVAL_SEGMENTS
            or now - self._last_flush >= self.FLUSH_INTERVAL_SECONDS
        ):
            self.flush()

    def complete_part(
        self,
        *,
        part: int,
        language: str,
        probability: float,
        duration: float,
        timeline_offset: float,
    ) -> None:
        self.current_part = part
        self.progress = part / max(1, self.part_count) * 100
        self.languages.append(
            {"part": part, "code": language, "probability": probability}
        )
        self.part_states[str(part)] = {
            "completed": True,
            "duration": duration,
            "timelineOffset": timeline_offset,
        }
        self.flush()

    def flush(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            **self.metadata,
            "updatedUtc": datetime.now(timezone.utc).isoformat(),
            "progress": self.progress,
            "currentPart": self.current_part,
            "parts": self.part_count,
            "lastTimestamp": self.last_timestamp,
            "languages": self.languages,
            "partStates": self.part_states,
            "segments": self.segments,
        }
        try:
            self.temporary_path.write_text(
                json.dumps(payload, ensure_ascii=False, indent=2),
                encoding="utf-8",
                newline="\n",
            )
            os.replace(self.temporary_path, self.path)
        finally:
            self.temporary_path.unlink(missing_ok=True)
        self._segments_since_flush = 0
        self._last_flush = time.monotonic()

    def remove(self) -> None:
        self.temporary_path.unlink(missing_ok=True)
        self.path.unlink(missing_ok=True)


def checkpoint_segment_to_namespace(payload: dict):
    words = [
        SimpleNamespace(
            start=word.get("start"),
            end=word.get("end"),
            word=word.get("text", ""),
            probability=word.get("probability"),
        )
        for word in (payload.get("words") or [])
    ]
    return SimpleNamespace(
        start=float(payload.get("start") or 0.0),
        end=float(payload.get("end") or 0.0),
        text=payload.get("text", ""),
        words=words,
        avg_logprob=payload.get("avgLogProbability"),
        compression_ratio=payload.get("compressionRatio"),
        no_speech_prob=payload.get("noSpeechProbability"),
        source_start=payload.get("sourceStart"),
        source_end=payload.get("sourceEnd"),
        timeline_offset=float(payload.get("timelineOffset") or 0.0),
    )

def create_model(model_path: str, preferred_device: str):
    cache_key = (str(Path(model_path).resolve()), preferred_device)
    cached = _MODEL_INSTANCE_CACHE.get(cache_key)
    if cached is not None:
        print(
            f"Modello già inizializzato: riuso {cached[1]} "
            f"con compute_type={cached[2]}."
        )
        return cached
    last_error = None
    for device, compute_type in build_model_candidates(preferred_device):
        try:
            print(f"Tento inizializzazione modello su {device} con compute_type={compute_type}...")
            model = WhisperModel(
                model_path,
                device=device,
                compute_type=compute_type
            )
            print(f"Modello inizializzato su {device} con compute_type={compute_type}.")
            result = (model, device, compute_type)
            _MODEL_INSTANCE_CACHE[cache_key] = result
            return result
        except Exception as exc:
            last_error = exc
            print(
                f"Configurazione non disponibile ({device}, {compute_type}): "
                f"{exc.__class__.__name__}: {exc}"
            )

    raise RuntimeError(
        "Impossibile inizializzare il modello con le configurazioni disponibili."
    ) from last_error

def resolve_model_path_with_progress(model_name: str, *, local_files_only: bool = False) -> str:
    model_path = Path(model_name)
    if model_path.exists():
        validate_model_snapshot(str(model_path))
        print(f"Uso modello locale: {model_path}")
        return str(model_path)

    repo_id = get_model_repo_id(model_name)

    allow_patterns = [
        "config.json",
        "preprocessor_config.json",
        "model.bin",
        "*.safetensors",
        "tokenizer.json",
        "vocabulary.*",
    ]

    if local_files_only:
        print(f"Controllo modello locale: {repo_id}")
    else:
        try:
            downloaded_path = snapshot_download(
                repo_id,
                allow_patterns=allow_patterns,
                local_files_only=True,
                tqdm_class=HFTqdm,
            )
            validate_model_snapshot(downloaded_path)
            print(f"Modello disponibile: {downloaded_path}")
            return downloaded_path
        except Exception:
            pass
        print(f"Download modello in corso: {repo_id}")
        HFTqdm._reset()

    downloaded_path = snapshot_download(
        repo_id,
        allow_patterns=allow_patterns,
        local_files_only=local_files_only,
        tqdm_class=HFTqdm,
    )
    validate_model_snapshot(downloaded_path)
    if local_files_only:
        print(f"Modello disponibile: {downloaded_path}")
    else:
        print("Download modello completato.")
    return downloaded_path

def delete_cached_model(model_name: str):
    repo_id = get_model_repo_id(model_name)
    cache_info = scan_cache_dir()
    cached_repo = next(
        (
            repo
            for repo in cache_info.repos
            if repo.repo_type == "model" and repo.repo_id == repo_id
        ),
        None,
    )
    if cached_repo is None:
        return None

    revision_hashes = [revision.commit_hash for revision in cached_repo.revisions]
    if not revision_hashes:
        return None

    deletion = cache_info.delete_revisions(*revision_hashes)
    freed_bytes = deletion.expected_freed_size
    deletion.execute()
    return repo_id, freed_bytes


def _manifest_job_arguments(job: dict) -> list[str]:
    sources = [str(path) for path in (job.get("sources") or [])]
    if not sources and not job.get("resumeCheckpoint"):
        raise ValueError("Un lavoro della coda non contiene file sorgente.")
    arguments = []
    if job.get("resumeCheckpoint"):
        _, checkpoint = TranscriptionCheckpoint.from_file(Path(job["resumeCheckpoint"]))
        expected = [os.path.normcase(str(Path(path).resolve())) for path in sources]
        actual = [os.path.normcase(str(Path(item["path"]).resolve())) for item in checkpoint["sources"]]
        if sources and expected != actual:
            raise ValueError("Il checkpoint appartiene a file sorgente diversi dal lavoro selezionato.")
        arguments.extend(["--resume-checkpoint", str(job["resumeCheckpoint"])])
    else:
        arguments.extend([sources[0], str(job.get("model") or DEFAULT_MODEL)])
        for source in sources[1:]:
            arguments.extend(["--input-file", source])
    for flag, key in (
        ("--device", "device"),
        ("--language", "language"),
        ("--beam-size", "beamSize"),
        ("--output-dir", "outputDirectory"),
        ("--output-name", "outputName"),
        ("--clip-start", "clipStart"),
        ("--clip-end", "clipEnd"),
    ):
        value = job.get(key)
        if value is not None and value != "":
            arguments.extend([flag, str(value)])
    if job.get("wordTimestamps"):
        arguments.append("--word-timestamps")
    if job.get("vad") is False:
        arguments.append("--no-vad")
    if job.get("overwrite"):
        arguments.append("--overwrite")
    if job.get("autoRename", True):
        arguments.append("--auto-rename")
    for output_format in job.get("outputFormats") or ("txt", "srt", "docx"):
        arguments.extend(["--output-format", str(output_format)])
    return arguments


def run_batch_manifest(path: Path) -> None:
    global _EVENT_CONTEXT, _BATCH_CONTROL_PATH
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        jobs = payload.get("jobs") or []
        control_path = payload.get("controlPath")
        if not jobs:
            raise ValueError("Il manifest non contiene lavori.")
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Errore manifest coda: {exc}")
        emit_result("batch", "error", message=str(exc))
        sys.exit(1)

    previous_argv = sys.argv[:]
    _BATCH_CONTROL_PATH = Path(control_path) if control_path else None
    os.environ["WHISPERLS_BATCH_WORKER"] = "1"
    failures = []
    try:
        for index, job in enumerate(jobs, start=1):
            source = next(iter(job.get("sources") or []), "checkpoint")
            _EVENT_CONTEXT = {
                "jobIndex": index,
                "jobCount": len(jobs),
                "jobId": str(job.get("id") or index),
                "jobSource": str(source),
            }
            emit_result("batch-job", "started")
            emit_progress("batch-job", 0, status="started")
            try:
                sys.argv = [previous_argv[0], *_manifest_job_arguments(job)]
                main()
                emit_result("batch-job", "success")
                emit_progress("batch-job", 100, status="success")
            except SystemExit as exc:
                code = int(exc.code or 0)
                if code:
                    failures.append(index)
                    status = "cancelled" if code == 4 else "error"
                    emit_result("batch-job", status, exitCode=code)
                    emit_progress("batch-job", 100, status=status, exitCode=code)
            except Exception as exc:
                failures.append(index)
                print(f"Errore imprevisto nel lavoro {index}: {exc}")
                emit_result("batch-job", "error", message=str(exc))
                emit_progress("batch-job", 100, status="error", message=str(exc))
        _EVENT_CONTEXT = {}
        emit_result(
            "batch",
            "partial" if failures else "success",
            jobs=len(jobs),
            failedJobs=failures,
        )
    finally:
        sys.argv = previous_argv
        _EVENT_CONTEXT = {}
        _BATCH_CONTROL_PATH = None
        os.environ.pop("WHISPERLS_BATCH_WORKER", None)
    if failures:
        sys.exit(3)

def main():
    args = parse_args()

    if args.batch_manifest:
        run_batch_manifest(args.batch_manifest)
        return

    if args.list_models:
        print("Modelli supportati:")
        for model_name in get_supported_model_names():
            default_marker = " (default)" if model_name == DEFAULT_MODEL else ""
            print(f"- {model_name}{default_marker}")
        return

    if args.check_model:
        try:
            resolve_model_path_with_progress(args.check_model, local_files_only=True)
        except LocalEntryNotFoundError:
            print(f"Modello non presente in cache: {args.check_model}")
            sys.exit(2)
        except Exception as exc:
            print(f"Errore durante il controllo del modello '{args.check_model}': {exc}")
            sys.exit(1)
        return

    if args.model_status_json:
        statuses = {}
        for model_name in get_supported_model_names():
            try:
                resolve_model_path_with_progress(model_name, local_files_only=True)
                statuses[model_name] = True
            except LocalEntryNotFoundError:
                statuses[model_name] = False
            except Exception:
                statuses[model_name] = False
        print(json.dumps(statuses, ensure_ascii=False, separators=(",", ":")))
        return

    if args.download_model:
        try:
            resolve_model_path_with_progress(args.download_model)
        except Exception as exc:
            print(f"Errore durante il download del modello '{args.download_model}': {exc}")
            sys.exit(1)
        return

    if args.delete_model:
        try:
            deletion_result = delete_cached_model(args.delete_model)
            if deletion_result is None:
                print(f"Modello non presente in cache: {args.delete_model}")
                sys.exit(2)
            repo_id, freed_bytes = deletion_result
            print(
                json.dumps(
                    {
                        "deleted": True,
                        "model": args.delete_model,
                        "repo_id": repo_id,
                        "freed_bytes": freed_bytes,
                    },
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
            )
        except Exception as exc:
            print(f"Errore durante la disinstallazione del modello '{args.delete_model}': {exc}")
            sys.exit(1)
        return

    resume_payload = None
    checkpoint = None
    if args.resume_checkpoint:
        try:
            checkpoint, resume_payload = TranscriptionCheckpoint.from_file(
                args.resume_checkpoint
            )
            configuration = resume_payload["configuration"]
            audio_paths = [
                Path(source["path"])
                for source in resume_payload["sources"]
            ]
            model_name = configuration["model"]
            args.device = configuration.get("requestedDevice", "auto")
            args.language = configuration.get("language", "auto")
            args.beam_size = int(configuration.get("beamSize", 5))
            args.no_vad = not bool(configuration.get("vad", True))
            args.word_timestamps = bool(configuration.get("wordTimestamps", False))
            args.output_formats = list(
                configuration.get("outputFormats") or ("txt", "srt", "docx")
            )
            args.output_name = configuration.get("outputName")
            args.output_dir = args.resume_checkpoint.parent
            args.overwrite = True
            args.clip_start = configuration.get("clipStart")
            args.clip_end = configuration.get("clipEnd")
            print(f"Ripresa checkpoint: {args.resume_checkpoint.name}")
        except (KeyError, TypeError, ValueError) as exc:
            print(f"Errore durante il recupero del checkpoint: {exc}")
            sys.exit(1)
    elif not args.audio_file:
        print("Errore: devi specificare un file audio/video oppure usare --list-models.")
        sys.exit(1)

    if resume_payload is None:
        audio_paths = [Path(args.audio_file)]
        audio_paths.extend(Path(path) for path in (args.input_files or []))
        audio_paths = list(dict.fromkeys(audio_paths))
        model_name = args.model
    if len(audio_paths) > 1 and (args.clip_start is not None or args.clip_end is not None):
        print("Errore: l'intervallo temporale è disponibile per una sola traccia alla volta.")
        sys.exit(1)
    if args.clip_start is not None and args.clip_end is not None:
        if args.clip_end <= args.clip_start:
            print("Errore: la fine dell'intervallo deve essere successiva all'inizio.")
            sys.exit(1)
    audio_path = audio_paths[0]

    try:
        for current_audio_path in audio_paths:
            validate_media_path(current_audio_path)
    except (FileNotFoundError, ValueError) as exc:
        print(f"Errore: {exc}")
        sys.exit(1)

    try:
        model_path = resolve_model_path_with_progress(model_name)
    except Exception as exc:
        print(f"Errore durante la risoluzione del modello '{model_name}': {exc}")
        sys.exit(1)

    try:
        model, actual_device, actual_compute_type = create_model(model_path, args.device)
    except Exception as exc:
        print(f"Errore durante l'inizializzazione del modello: {exc}")
        sys.exit(1)

    language = None if args.language.lower() == "auto" else args.language
    output_directory = args.output_dir or audio_path.parent
    output_formats = tuple(args.output_formats or ("txt", "srt", "docx"))
    requested_output_name = args.output_name or f"{audio_path.stem}_trascrizione"
    try:
        output_name = validate_output_name(requested_output_name)
        if args.auto_rename and resume_payload is None and not args.overwrite:
            output_name = choose_available_output_name(
                output_directory,
                output_name,
                output_formats,
            )
    except ValueError as exc:
        print(f"Errore: {exc}")
        sys.exit(1)
    checkpoint_path = args.resume_checkpoint or (
        output_directory / f"{output_name}.whisperls.json"
    )
    if checkpoint is None:
        try:
            checkpoint = TranscriptionCheckpoint(
                checkpoint_path,
                sources=audio_paths,
                model=model_name,
                requested_device=args.device,
                actual_device=actual_device,
                compute_type=actual_compute_type,
                language=args.language,
                beam_size=args.beam_size,
                vad_enabled=not args.no_vad,
                word_timestamps=args.word_timestamps,
                output_formats=output_formats,
                output_name=output_name,
                clip_start=args.clip_start,
                clip_end=args.clip_end,
            )
            # Reserve the checkpoint before any write; another instance must not replace it.
            checkpoint_path.parent.mkdir(parents=True, exist_ok=True)
            checkpoint_path.touch(exist_ok=False)
            checkpoint.flush()
            print("Checkpoint di recupero attivo.")
        except OSError as exc:
            print(f"Errore durante la creazione del checkpoint di recupero: {exc}")
            sys.exit(1)

    all_segments = []
    track_sections = []
    timeline_offset = 0.0
    detected_languages = []
    part_count = len(audio_paths)
    cached_segments_by_part = {}
    cached_languages = {}
    part_states = {}
    if resume_payload is not None:
        for segment_payload in resume_payload.get("segments") or []:
            part = int(segment_payload.get("part") or 1)
            cached_segments_by_part.setdefault(part, []).append(segment_payload)
        cached_languages = {
            int(item.get("part") or 0): (
                item.get("code") or "unknown",
                float(item.get("probability") or 0.0),
            )
            for item in (resume_payload.get("languages") or [])
        }
        part_states = resume_payload.get("partStates") or {}

    for part_index, current_audio_path in enumerate(audio_paths):
        part_number = part_index + 1
        cached_payloads = cached_segments_by_part.get(part_number, [])
        cached_segments = [
            checkpoint_segment_to_namespace(item) for item in cached_payloads
        ]
        state = part_states.get(str(part_number)) or {}
        if state.get("completed"):
            all_segments.extend(cached_segments)
            track_sections.append((current_audio_path.name, cached_segments))
            detected_languages.append(cached_languages.get(part_number, ("unknown", 0.0)))
            timeline_offset = float(state.get("timelineOffset") or timeline_offset) + float(
                state.get("duration") or 0.0
            )
            print(
                f"Parte recuperata {part_number}/{part_count}: "
                f"{current_audio_path.name}"
            )
            continue

        print(f"Parte {part_number}/{part_count}: {current_audio_path.name}")
        current_part_segments = list(cached_segments)
        all_segments.extend(cached_segments)
        source_resume_position = max(
            (
                float(item.get("sourceEnd"))
                for item in cached_payloads
                if item.get("sourceEnd") is not None
            ),
            default=0.0,
        )
        requested_clip_start = max(args.clip_start or 0.0, source_resume_position)
        requested_clip_end = args.clip_end
        transcribe_kwargs = {
            "language": language,
            "beam_size": args.beam_size,
            "vad_filter": not args.no_vad,
            "word_timestamps": args.word_timestamps,
        }
        if requested_clip_start > 0 or requested_clip_end is not None:
            transcribe_kwargs["clip_timestamps"] = (
                [requested_clip_start, requested_clip_end]
                if requested_clip_end is not None
                else [requested_clip_start]
            )
        try:
            if requested_clip_end is not None and requested_clip_start >= requested_clip_end:
                segments = []
                info = SimpleNamespace(
                    language=language or cached_languages.get(part_number, ("unknown", 0))[0],
                    language_probability=cached_languages.get(part_number, ("", 0))[1],
                    duration=requested_clip_end,
                )
            else:
                segments, info = model.transcribe(
                    str(current_audio_path),
                    **transcribe_kwargs,
                )
        except Exception as exc:
            print(
                f"Errore durante la trascrizione della parte "
                f"{part_number} ({current_audio_path.name}): {exc}"
            )
            sys.exit(1)

        detected_languages.append(
            (info.language, getattr(info, "language_probability", 0.0))
        )
        duration = getattr(info, "duration", None)
        interval_start = args.clip_start or 0.0
        interval_end = args.clip_end or duration
        last_end = requested_clip_start
        try:
            with tqdm(
                total=duration if duration and duration > 0 else None,
                initial=requested_clip_start if duration and duration > 0 else 0,
                desc=f"Sbobinatura {part_index + 1}/{part_count}",
                unit="s" if duration and duration > 0 else "segmento",
                dynamic_ncols=True,
                disable=os.environ.get("WHISPERLS_PROGRESS") == "1",
            ) as pbar:
                for seg in segments:
                    check_batch_control()
                    segment_end = float(getattr(seg, "end", 0.0))
                    segment_start = float(getattr(seg, "start", 0.0))
                    shifted_words = []
                    for word in getattr(seg, "words", None) or []:
                        shifted_words.append(
                            SimpleNamespace(
                                word=getattr(word, "word", ""),
                                start=(
                                    float(word.start) + timeline_offset
                                    if getattr(word, "start", None) is not None
                                    else None
                                ),
                                end=(
                                    float(word.end) + timeline_offset
                                    if getattr(word, "end", None) is not None
                                    else None
                                ),
                                probability=getattr(word, "probability", None),
                            )
                        )
                    shifted_segment = SimpleNamespace(
                        start=segment_start + timeline_offset,
                        end=segment_end + timeline_offset,
                        text=getattr(seg, "text", ""),
                        words=shifted_words,
                        avg_logprob=getattr(seg, "avg_logprob", None),
                        compression_ratio=getattr(seg, "compression_ratio", None),
                        no_speech_prob=getattr(seg, "no_speech_prob", None),
                    )
                    all_segments.append(shifted_segment)
                    current_part_segments.append(shifted_segment)
                    checkpoint_progress = None
                    if duration and duration > 0:
                        progress_end = min(segment_end, interval_end or duration)
                        progress_span = max(0.001, (interval_end or duration) - interval_start)
                        local_progress = max(
                            0.0,
                            min(1.0, (progress_end - interval_start) / progress_span),
                        )
                        checkpoint_progress = (
                            (part_index + local_progress)
                            / part_count
                            * 100
                        )
                    checkpoint.add_segment(
                        shifted_segment,
                        part=part_index + 1,
                        source_name=current_audio_path.name,
                        progress=checkpoint_progress,
                        source_start=segment_start,
                        source_end=segment_end,
                        timeline_offset=timeline_offset,
                    )
                    if duration and duration > 0:
                        current_end = min(segment_end, interval_end or duration)
                        if current_end > last_end:
                            pbar.update(current_end - last_end)
                            last_end = current_end
                            emit_progress(
                                "transcription",
                                (
                                    (part_index + max(
                                        0.0,
                                        min(
                                            1.0,
                                            (last_end - interval_start)
                                            / max(0.001, (interval_end or duration) - interval_start),
                                        ),
                                    ))
                                    / part_count
                                    * 100
                                ),
                                part=part_index + 1,
                                parts=part_count,
                            )
                    else:
                        pbar.update(1)
                        last_end = max(last_end, segment_end)
                effective_end = interval_end or duration
                if duration and duration > 0 and effective_end and last_end < effective_end:
                    pbar.update(effective_end - last_end)
        except BatchJobCancelled as exc:
            print(f"Warning: {exc}")
            sys.exit(4)
        except Exception as exc:
            print(
                f"Errore durante la trascrizione della parte "
                f"{part_index + 1} ({current_audio_path.name}): {exc}"
            )
            sys.exit(1)

        part_duration = float(interval_end) if interval_end is not None else (
            float(duration) if duration and duration > 0 else last_end
        )
        track_sections.append((current_audio_path.name, current_part_segments))
        part_timeline_offset = timeline_offset
        effective_part_duration = max(part_duration, last_end)
        emit_progress(
            "transcription",
            ((part_index + 1) / part_count) * 100,
            part=part_index + 1,
            parts=part_count,
        )
        checkpoint.complete_part(
            part=part_index + 1,
            language=detected_languages[-1][0],
            probability=detected_languages[-1][1],
            duration=effective_part_duration,
            timeline_offset=part_timeline_offset,
        )
        timeline_offset += effective_part_duration
        print(
            f"Parte completata {part_index + 1}/{part_count}: "
            f"{current_audio_path.name}"
        )

    print(f"Dispositivo usato: {actual_device}")
    print(f"Compute type usato: {actual_compute_type}")
    for index, (detected_language, probability) in enumerate(detected_languages, start=1):
        print(f"Lingua parte {index}: {detected_language} (prob={probability:.3f})")
    print(f"Segmenti: {len(all_segments)}")
    try:
        txt_path, srt_path, docx_path, context_paragraphs = write_transcription_outputs(
            audio_path,
            all_segments,
            output_dir=args.output_dir,
            word_timestamps=args.word_timestamps,
            overwrite=args.overwrite,
            output_formats=args.output_formats,
            track_sections=track_sections,
            output_name=output_name,
        )
    except (OSError, ValueError) as exc:
        print(f"Errore durante la scrittura degli output: {exc}")
        sys.exit(1)

    output_paths = tuple(
        path for path in (txt_path, srt_path, docx_path) if path is not None
    )
    for output_path in output_paths:
        print(f"Creato: {output_path}")
    print(f"Paragrafi: {len(context_paragraphs)}")
    if any(not path.is_file() or path.stat().st_size <= 0 for path in output_paths):
        print("Errore: verifica finale degli output non superata.")
        sys.exit(1)
    try:
        checkpoint.remove()
        print("Checkpoint temporaneo eliminato.")
    except OSError as exc:
        print(f"Warning: impossibile eliminare il checkpoint temporaneo: {exc}")
    emit_result(
        "transcription",
        "success",
        outputs=[str(path) for path in output_paths],
        segments=len(all_segments),
        paragraphs=len(context_paragraphs),
        parts=part_count,
        device=actual_device,
        computeType=actual_compute_type,
        languages=[
            {"code": code, "probability": probability}
            for code, probability in detected_languages
        ],
    )
    if (
        os.environ.get("WHISPERLS_PROGRESS") == "1"
        and os.environ.get("WHISPERLS_BATCH_WORKER") != "1"
    ):
        # Gli output sono già chiusi e verificati. L'uscita immediata evita crash
        # sporadici dei finalizer nativi CUDA durante il teardown dell'interprete.
        sys.stdout.flush()
        sys.stderr.flush()
        os._exit(0)

if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    main()
