import argparse
from faster_whisper import WhisperModel
from faster_whisper.utils import _MODELS
from huggingface_hub import snapshot_download
from huggingface_hub.errors import LocalEntryNotFoundError
from pathlib import Path
from tqdm.auto import tqdm
from xml.sax.saxutils import escape
import re
import sys
import zipfile
import math
from collections import Counter

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
    "il", "i", "gli", "l", "la", "le", "d", "del", "della", "delle", "dei", "degli"
}

DEFAULT_MODEL = "medium"

def get_supported_model_names():
    return sorted(_MODELS.keys())

def get_model_repo_id(model_name: str) -> str:
    model_path = Path(model_name)
    if model_path.exists():
        return str(model_path)

    if re.match(r".*/.*", model_name):
        return model_name

    repo_id = _MODELS.get(model_name)
    if repo_id is None:
        raise ValueError(
            f"Modello non valido: {model_name}. "
            f"Valori supportati: {', '.join(_MODELS.keys())}"
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
    ms = int(round(seconds * 1000))
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
        current_words += len(item["text"].split())

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

def write_docx(path: Path, text: str) -> None:
    paragraphs = [line.strip() for line in text.splitlines()]
    if not paragraphs:
        paragraphs = [""]

    body_parts = []
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

class HFTqdm(tqdm):
    def __init__(self, *args, **kwargs):
        kwargs.pop("name", None)
        super().__init__(*args, **kwargs)

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
        "--device",
        choices=["auto", "cuda", "cpu"],
        default="auto",
        help="Dispositivo da usare. 'auto' prova prima CUDA e poi CPU.",
    )
    parser.add_argument(
        "--language",
        default="it",
        help="Lingua da forzare per la trascrizione (default: it). Usa 'auto' per auto-detect.",
    )
    parser.add_argument(
        "--beam-size",
        type=int,
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
        "--download-model",
        metavar="MODEL",
        help="Scarica un modello e termina senza trascrivere file.",
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

def create_model(model_path: str, preferred_device: str):
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
            return model, device, compute_type
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
        print(f"Uso modello locale: {model_path}")
        return str(model_path)

    repo_id = get_model_repo_id(model_name)

    if local_files_only:
        print(f"Controllo modello locale: {repo_id}")
    else:
        print(f"Download modello in corso: {repo_id}")

    downloaded_path = snapshot_download(
        repo_id,
        allow_patterns=[
            "config.json",
            "preprocessor_config.json",
            "model.bin",
            "tokenizer.json",
            "vocabulary.*",
        ],
        local_files_only=local_files_only,
        tqdm_class=HFTqdm,
    )
    if local_files_only:
        print(f"Modello disponibile: {downloaded_path}")
    else:
        print("Download modello completato.")
    return downloaded_path

def main():
    args = parse_args()

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

    if args.download_model:
        try:
            resolve_model_path_with_progress(args.download_model)
        except Exception as exc:
            print(f"Errore durante il download del modello '{args.download_model}': {exc}")
            sys.exit(1)
        return

    if not args.audio_file:
        print("Errore: devi specificare un file audio/video oppure usare --list-models.")
        sys.exit(1)

    audio_path = Path(args.audio_file)
    model_name = args.model

    if not audio_path.exists():
        print(f"File non trovato: {audio_path}")
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
    try:
        segments, info = model.transcribe(
            str(audio_path),
            language=language,
            beam_size=args.beam_size,
            vad_filter=not args.no_vad,
            word_timestamps=args.word_timestamps
        )
    except Exception as exc:
        print(f"Errore durante la trascrizione: {exc}")
        sys.exit(1)

    base = audio_path.with_suffix("")
    txt_path = base.with_name(base.name + "_trascrizione.txt")
    srt_path = base.with_name(base.name + "_trascrizione.srt")
    docx_path = base.with_name(base.name + "_trascrizione.docx")

    all_segments = []
    duration = getattr(info, "duration", None)
    if duration is not None and duration > 0:
        with tqdm(total=duration, desc="Sbobinatura", unit="s", dynamic_ncols=True) as pbar:
            last_end = 0.0
            for seg in segments:
                all_segments.append(seg)
                current_end = min(seg.end, duration)
                if current_end > last_end:
                    pbar.update(current_end - last_end)
                    last_end = current_end
            if last_end < duration:
                pbar.update(duration - last_end)
    else:
        with tqdm(desc="Sbobinatura", unit="segmento", dynamic_ncols=True) as pbar:
            for seg in segments:
                all_segments.append(seg)
                pbar.update(1)

    print(f"Dispositivo usato: {actual_device}")
    print(f"Compute type usato: {actual_compute_type}")
    print(f"Lingua rilevata: {info.language} (prob={info.language_probability:.3f})")
    print(f"Segmenti: {len(all_segments)}")
    context_paragraphs = build_context_paragraphs(all_segments)
    if not context_paragraphs:
        context_paragraphs = [linearize_segments(all_segments)]
    paragraph_text = "\n\n".join(context_paragraphs)

    # TXT
    with txt_path.open("w", encoding="utf-8") as f:
        f.write(paragraph_text + "\n")

    # SRT
    with srt_path.open("w", encoding="utf-8") as f:
        for i, seg in enumerate(all_segments, start=1):
            f.write(f"{i}\n")
            f.write(f"{format_srt_time(seg.start)} --> {format_srt_time(seg.end)}\n")
            f.write((seg.text or "").strip() + "\n\n")

    # DOCX
    write_docx(docx_path, paragraph_text)

    print(f"Creato: {txt_path}")
    print(f"Creato: {srt_path}")
    print(f"Creato: {docx_path}")
    print(f"Paragrafi: {len(context_paragraphs)}")

if __name__ == "__main__":
    main()
