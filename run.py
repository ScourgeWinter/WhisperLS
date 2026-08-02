#!/usr/bin/env python3
"""Interfaccia testuale (TUI) per Faster Whisper."""

import sys
import os
import contextlib
from pathlib import Path
import queue
import subprocess
import threading

_repo = Path(__file__).resolve().parent
sys.path.insert(0, str(_repo))

try:
    from rich import box
    from rich.align import Align
    from rich.console import Console
    from rich.panel import Panel
    from rich.progress import (
        BarColumn,
        DownloadColumn,
        Progress,
        SpinnerColumn,
        TextColumn,
        TimeElapsedColumn,
        TimeRemainingColumn,
        TransferSpeedColumn,
    )
    from rich.prompt import Confirm, IntPrompt, Prompt
    from rich.rule import Rule
    from rich.table import Table
    from rich.text import Text
except ImportError as exc:
    print("Dipendenze mancanti. Installa l'ambiente con:")
    print(f'  "{sys.executable}" -m pip install -r "{_repo / "requirements.txt"}"')
    raise SystemExit(1) from exc

console = Console(highlight=False)

def clear_screen():
    """Cancella schermo E buffer di scrollback — l'utente non può scorrere indietro."""
    sys.stdout.write("\033[2J\033[3J\033[H")
    sys.stdout.flush()

DEVICE_LABELS = {
    "auto": "Automatico (CUDA → CPU)",
    "cuda": "GPU (CUDA)",
    "cpu": "CPU",
}

LANGUAGE_OPTIONS = [
    ("it",   "Italiano"),
    ("en",   "English"),
    ("fr",   "Français"),
    ("de",   "Deutsch"),
    ("es",   "Español"),
    ("pt",   "Português"),
    ("auto", "Rilevamento automatico"),
]

MODEL_NOTES = {
    "tiny":              "velocissimo, meno accurato — per bozze o test rapidi",
    "tiny.en":           "come tiny, ottimizzato solo per inglese",
    "base":              "veloce, qualità base",
    "base.en":           "come base, ottimizzato solo per inglese",
    "small":             "buon compromesso velocità/qualità",
    "small.en":          "come small, ottimizzato solo per inglese",
    "medium":            "accurato, consigliato per uso quotidiano  [dim](default)[/dim]",
    "medium.en":         "come medium, ottimizzato solo per inglese",
    "large":             "alta qualità (v1 originale)",
    "large-v1":          "alta qualità, prima versione large",
    "large-v2":          "alta qualità, migliorato su lingue non-inglesi",
    "large-v3":          "massima qualità, meno errori su accenti e nomi propri",
    "large-v3-turbo":    "massima qualità, più veloce di large-v3",
    "turbo":             "alias di large-v3-turbo",
    "distil-large-v2":   "distillato di large-v2 — 6× più veloce, lieve calo qualità",
    "distil-large-v3":   "distillato di large-v3 — veloce con qualità quasi identica",
    "distil-large-v3.5": "distillato di large-v3 aggiornato — miglior rapporto qualità/velocità",
    "distil-medium.en":  "distillato di medium, solo inglese — molto veloce",
    "distil-small.en":   "distillato di small, solo inglese — leggerissimo",
}


# ── Helpers ────────────────────────────────────────────────────────────────────

@contextlib.contextmanager
def suppress_output():
    """Silenzia stdout e stderr (print, tqdm, log di librerie) durante le fasi gestite dalla TUI."""
    with open(os.devnull, "w") as null:
        old_out, old_err = sys.stdout, sys.stderr
        sys.stdout = sys.stderr = null
        try:
            yield
        finally:
            sys.stdout, sys.stderr = old_out, old_err


def step_rule(n: int, label: str):
    console.print()
    console.print(Rule(f"[bright_cyan]▸ {n}[/]  [white]{label}[/]", style="dim cyan"))
    console.print()


def ok(msg: str):
    console.print(f"  [bright_green]✓[/]  {msg}")


def err(msg: str):
    console.print(f"  [red]✗[/]  {msg}")


# ── Header ─────────────────────────────────────────────────────────────────────

def print_header():
    title = Text.assemble(
        ("◈  ", "bright_cyan bold"),
        ("Faster Whisper", "white bold"),
        ("  ◈", "bright_cyan bold"),
    )
    subtitle = Text("Trascrittore Audio · Video", style="dim white", justify="center")
    console.print(
        Panel(
            Align.center(Text.assemble(title, "\n", subtitle)),
            border_style="bright_cyan",
            padding=(1, 6),
            box=box.DOUBLE_EDGE,
        )
    )
    console.print()


# ── Selezione file ─────────────────────────────────────────────────────────────

def ask_file() -> Path:
    from Whisper import validate_media_path

    while True:
        raw = Prompt.ask(
            "[bright_white]File audio/video[/]  [dim](percorso o drag & drop)[/]"
        )
        raw = raw.strip().strip('"').strip("'")
        p = Path(raw)
        try:
            return validate_media_path(p)
        except (FileNotFoundError, ValueError) as exc:
            err(str(exc))
        console.print()


# ── Configurazione ─────────────────────────────────────────────────────────────

def _config_summary(cfg: dict) -> Panel:
    grid = Table.grid(padding=(0, 2))
    grid.add_column(style="dim", width=16)
    grid.add_column(style="bright_white")

    grid.add_row("Modello",          f"[bright_cyan]{cfg['model']}[/]")
    grid.add_row("Lingua",           f"[bright_cyan]{cfg['language']}[/]")
    grid.add_row("Dispositivo",      f"[bright_cyan]{DEVICE_LABELS.get(cfg['device'], cfg['device'])}[/]")
    grid.add_row("Beam size",        f"[bright_cyan]{cfg['beam_size']}[/]")
    grid.add_row("VAD",              "[green]attivo[/]"    if cfg["vad"]     else "[dim]disattivo[/]")
    grid.add_row("Word timestamps",  "[green]attivi[/]"    if cfg["word_ts"] else "[dim]disattivo[/]")

    return Panel(grid, border_style="dim cyan", padding=(0, 1))


def _check_cached_models(models: list) -> set:
    """Restituisce il set dei nomi modello già presenti in cache locale."""
    from Whisper import get_model_repo_id, get_hf_cache_repo_dir, has_complete_snapshot
    cached = set()
    for m in models:
        try:
            repo_id = get_model_repo_id(m)
            if has_complete_snapshot(get_hf_cache_repo_dir(repo_id)):
                cached.add(m)
        except Exception:
            pass
    return cached


def _choose_model(current: str) -> str:
    from Whisper import get_supported_model_names
    models = get_supported_model_names()

    clear_screen()
    print_header()
    step_rule(2, "Seleziona il modello")

    with console.status("[dim]Controllo modelli in cache...[/]", spinner="dots"):
        cached = _check_cached_models(models)

    t = Table(box=box.SIMPLE, show_header=False, padding=(0, 1))
    t.add_column("", width=3, style="dim")
    t.add_column("Nome", style="bright_white", min_width=20)
    t.add_column("Note", style="dim")
    t.add_column("", style="dim")

    for i, m in enumerate(models, 1):
        arrow = "[bright_cyan]→[/]" if m == current else " "
        if m in cached:
            status = "[green]● scaricato[/]"
        else:
            status = "[blue]○ da scaricare[/]"
        t.add_row(f"{i}.", f"{arrow} {m}", MODEL_NOTES.get(m, ""), status)
    console.print(t)
    console.print()

    while True:
        choice = Prompt.ask("Modello", default=current)
        if choice in models:
            return choice
        try:
            idx = int(choice) - 1
            if 0 <= idx < len(models):
                return models[idx]
        except ValueError:
            pass
        err("Scelta non valida — inserisci il nome o il numero.")
        console.print()


def _choose_language(current: str) -> str:
    from Whisper import validate_language

    clear_screen()
    print_header()
    step_rule(2, "Seleziona la lingua")

    t = Table(box=box.SIMPLE, show_header=False, padding=(0, 1))
    t.add_column("Codice", style="bright_cyan", width=6)
    t.add_column("Lingua", style="white")

    for code, name in LANGUAGE_OPTIONS:
        arrow = "[bright_cyan]→[/]" if code == current else " "
        t.add_row(code, f"{arrow} {name}")
    console.print(t)
    console.print()

    while True:
        choice = Prompt.ask("Lingua", default=current)
        try:
            return validate_language(choice)
        except ValueError as exc:
            err(str(exc))
        console.print()


def _choose_device(current: str) -> str:
    devices = ["auto", "cuda", "cpu"]

    clear_screen()
    print_header()
    step_rule(2, "Seleziona il dispositivo")

    t = Table(box=box.SIMPLE, show_header=False, padding=(0, 1))
    t.add_column("", width=3, style="dim")
    t.add_column("", style="white")

    for i, d in enumerate(devices, 1):
        arrow = "[bright_cyan]→[/]" if d == current else " "
        t.add_row(f"{i}.", f"{arrow} {DEVICE_LABELS[d]}")
    console.print(t)
    console.print()

    while True:
        choice = Prompt.ask("Dispositivo", default=current)
        if choice in devices:
            return choice
        try:
            idx = int(choice) - 1
            if 0 <= idx < len(devices):
                return devices[idx]
        except ValueError:
            pass
        err("Scelta non valida.")
        console.print()


OPTION_HELP = {
    "1": (
        "Modello Whisper",
        "Determina la [bright_white]dimensione della rete neurale[/] usata per la trascrizione.\n\n"
        "  [bright_cyan]tiny / base[/]   → molto veloci, meno precisi; utili per bozze o file lunghi\n"
        "  [bright_cyan]small[/]         → buon compromesso velocità/qualità\n"
        "  [bright_cyan]medium[/]        → accurato, consigliato per uso quotidiano [dim](default)[/]\n"
        "  [bright_cyan]large-v3[/]      → massima qualità, richiede più RAM/VRAM e tempo\n\n"
        "Modelli più grandi occupano più spazio su disco e richiedono più memoria.",
    ),
    "2": (
        "Lingua",
        "Forza Whisper a riconoscere l'audio come scritto in una [bright_white]lingua specifica[/].\n\n"
        "  Specifica la lingua quando la conosci già: riduce gli errori e velocizza la trascrizione.\n"
        "  Usa [bright_cyan]auto[/] solo se l'audio contiene lingue miste o non sai quale sia;\n"
        "  il rilevamento automatico è meno affidabile su audio brevi o rumorosi.",
    ),
    "3": (
        "Dispositivo",
        "Indica su quale [bright_white]hardware[/] eseguire il modello.\n\n"
        "  [bright_cyan]auto[/]   → prova prima la GPU (CUDA), poi CPU se non disponibile [dim](consigliato)[/]\n"
        "  [bright_cyan]cuda[/]   → forza GPU NVIDIA; molto più veloce, richiede driver CUDA\n"
        "  [bright_cyan]cpu[/]    → forza CPU; sempre disponibile, ma notevolmente più lento\n\n"
        "Se hai una GPU NVIDIA con CUDA installato, lascia [bright_cyan]auto[/] per usarla automaticamente.",
    ),
    "4": (
        "Beam size",
        "Controlla la [bright_white]larghezza della ricerca[/] durante la decodifica del testo.\n\n"
        "  Valori più alti → Whisper esplora più ipotesi di testo → più preciso, più lento\n"
        "  Valori più bassi → trascrizione più rapida, leggermente meno accurata\n\n"
        "  [bright_cyan]1[/]   = greedy (velocissimo, qualità ridotta)\n"
        "  [bright_cyan]5[/]   = default bilanciato\n"
        "  [bright_cyan]8–10[/] = qualità massima, sensibilmente più lento\n\n"
        "Per la maggior parte dei casi [bright_cyan]5[/] è il valore ottimale.",
    ),
    "5": (
        "VAD — Voice Activity Detection",
        "Filtro che [bright_white]individua ed elimina i silenzi[/] prima di passare l'audio al modello.\n\n"
        "  [green]Attivo[/]      → i segmenti silenziosi vengono saltati; più veloce e meno allucinazioni\n"
        "  [dim]Disattivo[/]   → l'audio viene elaborato integralmente, silenzio incluso\n\n"
        "Disattivalo solo se il file ha pochissimi silenzi o se il VAD taglia parti di audio\n"
        "che vengono erroneamente classificate come silenzio.",
    ),
    "6": (
        "Word timestamps",
        "Aggiunge al file SRT il [bright_white]timestamp preciso di ogni singola parola[/], non solo del segmento.\n\n"
        "  [green]Attivi[/]      → utili per sottotitoli sincronizzati parola per parola o karaoke\n"
        "  [dim]Disattivi[/]   → timestamp per segmento (default), file SRT più leggeri\n\n"
        "Aumenta leggermente i tempi di elaborazione. Lascia disattivato se non ti serve\n"
        "la sincronizzazione a livello di parola.",
    ),
}

def _show_help(key: str):
    if key not in OPTION_HELP:
        return
    title, body = OPTION_HELP[key]
    clear_screen()
    print_header()
    step_rule(2, "Aiuto opzione")
    console.print(
        Panel(
            body,
            title=f"[bright_cyan]?  {title}[/]",
            border_style="dim cyan",
            padding=(1, 2),
        )
    )
    console.print()
    Prompt.ask("[dim]Premi Invio per tornare al menu[/]", default="")


def configure(cfg: dict) -> dict:
    cfg = dict(cfg)

    menu = [
        ("1", "Cambia modello"),
        ("2", "Cambia lingua"),
        ("3", "Cambia dispositivo"),
        ("4", "Cambia beam size"),
        ("5", "Attiva/disattiva VAD"),
        ("6", "Attiva/disattiva word timestamps"),
        ("", ""),
        ("0", "Avvia trascrizione"),
    ]

    while True:
        clear_screen()
        print_header()
        step_rule(2, "Configura le opzioni")
        console.print(_config_summary(cfg))
        console.print()

        grid = Table.grid(padding=(0, 2))
        grid.add_column(style="dim", width=3)
        grid.add_column(style="white")
        for key, label in menu:
            if not key:
                grid.add_row("", "")
            elif key == "0":
                grid.add_row(key, f"[bright_green bold]{label}[/]")
            else:
                grid.add_row(key, label)
        grid.add_row("", "")
        grid.add_row(
            "[bright_cyan]?N[/]",
            "[dim]mostra la spiegazione di un'opzione "
            "(es. [/][bright_cyan]?4[/][dim] → cos'è il beam size)[/]",
        )
        console.print(grid)
        console.print()

        choice = Prompt.ask("Digita il numero dell'operazione da eseguire", default="0")
        console.print()

        # Aiuto contestuale: ?1 … ?6
        if choice.startswith("?"):
            _show_help(choice[1:])
            continue

        if choice == "0":
            break
        elif choice == "1":
            cfg["model"] = _choose_model(cfg["model"])
        elif choice == "2":
            cfg["language"] = _choose_language(cfg["language"])
        elif choice == "3":
            cfg["device"] = _choose_device(cfg["device"])
        elif choice == "4":
            while True:
                beam_size = IntPrompt.ask("Beam size (1-20)", default=cfg["beam_size"])
                if 1 <= beam_size <= 20:
                    cfg["beam_size"] = beam_size
                    break
                err("Il beam size deve essere compreso tra 1 e 20.")
        elif choice == "5":
            cfg["vad"] = Confirm.ask("Abilita VAD", default=cfg["vad"])
        elif choice == "6":
            cfg["word_ts"] = Confirm.ask("Abilita word timestamps", default=cfg["word_ts"])
        console.print()

    return cfg


# ── Risoluzione modello con progress bar ───────────────────────────────────────


def _resolve_model(model_name: str) -> str:
    """Risolve il percorso del modello. Se non in cache, scarica via subprocess con progress bar."""
    import Whisper
    import json

    # Controlla cache locale
    cached = False
    try:
        with suppress_output():
            Whisper.resolve_model_path_with_progress(model_name, local_files_only=True)
        cached = True
    except Exception:
        pass

    if cached:
        step_rule(3, "Trascrizione")
        console.print()
        with console.status("[bright_cyan]Caricamento modello dalla cache...[/]", spinner="dots"):
            with suppress_output():
                path = Whisper.resolve_model_path_with_progress(model_name, local_files_only=True)
        return path

    # Modello non in cache: download via subprocess
    step_rule(3, "Download modello")
    console.print()
    console.print(
        f"  [dim]Modello [/][bright_cyan]{model_name}[/]"
        f"[dim] non presente in cache — avvio download...[/]"
    )
    console.print()

    STALL_TIMEOUT = 60  # secondi senza nuovi byte prima di segnalare stallo

    whisper_script = _repo / "Whisper.py"
    child_environment = os.environ.copy()
    child_environment["WHISPERLS_PROGRESS"] = "1"
    proc = subprocess.Popen(
        [sys.executable, str(whisper_script), "--download-model", model_name],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=child_environment,
    )

    # Leggi stdout in un thread separato per non bloccare il loop Rich
    line_q: queue.Queue = queue.Queue()

    def _reader():
        for line in proc.stdout:
            line_q.put(line.strip())
        line_q.put(None)

    threading.Thread(target=_reader, daemon=True).start()

    import time as _time
    last_done_b   = 0
    last_progress = _time.monotonic()
    stall_warned  = False

    with Progress(
        SpinnerColumn(style="bright_cyan"),
        TextColumn("[bright_cyan]{task.description}"),
        BarColumn(bar_width=30, style="dim cyan", complete_style="bright_cyan"),
        TextColumn("[bright_white]{task.percentage:>5.1f}%"),
        TextColumn("[dim]│"),
        DownloadColumn(),
        TextColumn("[dim]│"),
        TransferSpeedColumn(),
        console=console,
        transient=False,
    ) as progress:
        task = progress.add_task("Download modello", total=None)
        while True:
            try:
                line = line_q.get(timeout=0.25)
            except queue.Empty:
                progress.update(task, completed=last_done_b)
                # Rilevamento stallo
                if not stall_warned and _time.monotonic() - last_progress > STALL_TIMEOUT:
                    stall_warned = True
                    progress.stop()
                    console.print()
                    console.print(
                        "  [yellow]⚠[/]  Nessun progresso da 60 secondi — il download sembra bloccato.\n"
                        "     Cause comuni:\n"
                        "       [dim]·[/] Rate-limiting di HuggingFace (connessione senza HF_TOKEN)\n"
                        "       [dim]·[/] Connessione di rete instabile o lenta\n"
                        "     Il download continua in background — attendere o premere [bright_white]Ctrl+C[/] per annullare."
                    )
                    console.print()
                    progress.start()
                continue
            if line is None:
                break
            if not line.startswith("__WHISPER_PROGRESS__"):
                continue
            try:
                data    = json.loads(line[len("__WHISPER_PROGRESS__ "):])
                total_b = int(data.get("totalBytes", 0))
                done_b  = int(data.get("downloadedBytes", 0))
                if total_b > 0:
                    progress.update(task, total=total_b, completed=done_b)
                    if done_b > last_done_b:
                        last_done_b   = done_b
                        last_progress = _time.monotonic()
                        stall_warned  = False
            except Exception:
                pass
        progress.update(task, total=max(last_done_b, 1), completed=max(last_done_b, 1))

    proc.wait()
    if proc.returncode != 0:
        raise RuntimeError(f"Download fallito (exit code {proc.returncode})")

    console.print()
    step_rule(3, "Trascrizione")
    console.print()

    with suppress_output():
        path = Whisper.resolve_model_path_with_progress(model_name, local_files_only=True)
    return path


# ── Trascrizione ───────────────────────────────────────────────────────────────

def transcribe(audio_path: Path, cfg: dict):
    import Whisper

    # Risoluzione modello
    try:
        model_path = _resolve_model(cfg["model"])
    except Exception as e:
        err(f"Errore modello: {e}")
        sys.exit(1)
    ok(f"Modello: [dim]{model_path}[/]")

    # Inizializzazione
    with console.status("[bright_cyan]Inizializzazione modello...[/]", spinner="dots"):
        try:
            with suppress_output():
                model, actual_device, actual_compute = Whisper.create_model(
                    model_path, cfg["device"]
                )
        except Exception as e:
            err(f"Errore inizializzazione: {e}")
            sys.exit(1)
    ok(
        f"Dispositivo: [bright_cyan]{actual_device}[/]  "
        f"Compute type: [bright_cyan]{actual_compute}[/]"
    )

    # Avvio trascrizione
    language = None if cfg["language"].lower() == "auto" else cfg["language"]
    with console.status("[bright_cyan]Avvio trascrizione...[/]", spinner="dots"):
        try:
            segments_gen, info = model.transcribe(
                str(audio_path),
                language=language,
                beam_size=cfg["beam_size"],
                vad_filter=cfg["vad"],
                word_timestamps=cfg["word_ts"],
            )
        except Exception as e:
            err(f"Errore trascrizione: {e}")
            sys.exit(1)

    duration = getattr(info, "duration", None)
    all_segments = []
    console.print()

    # Progress bar
    try:
        with Progress(
            SpinnerColumn(style="bright_cyan"),
            TextColumn("[bright_cyan]{task.description}"),
            BarColumn(bar_width=38, style="dim cyan", complete_style="bright_cyan"),
            TextColumn("[bright_white]{task.percentage:>5.1f}%"),
            TextColumn("[dim]│"),
            TimeElapsedColumn(),
            TextColumn("[dim]/"),
            TimeRemainingColumn(),
            console=console,
            transient=False,
        ) as progress:
            if duration and duration > 0:
                task = progress.add_task("Sbobinatura", total=duration)
                last_end = 0.0
                for seg in segments_gen:
                    all_segments.append(seg)
                    current_end = min(seg.end, duration)
                    if current_end > last_end:
                        progress.update(task, advance=current_end - last_end)
                        last_end = current_end
                progress.update(task, completed=duration)
            else:
                task = progress.add_task("Sbobinatura", total=None)
                for seg in segments_gen:
                    all_segments.append(seg)
                    progress.advance(task)
    except Exception as exc:
        err(f"Errore durante la trascrizione: {exc}")
        raise SystemExit(1) from exc

    console.print()
    ok(
        f"Lingua: [bright_cyan]{info.language}[/]  "
        f"[dim](prob={info.language_probability:.3f})[/]  "
        f"Segmenti: [bright_cyan]{len(all_segments)}[/]"
    )

    return all_segments, info


# ── Output ─────────────────────────────────────────────────────────────────────

def write_outputs(audio_path: Path, all_segments, _cfg: dict):
    import Whisper

    expected = [
        audio_path.with_name(audio_path.stem + f"_trascrizione{extension}")
        for extension in (".txt", ".srt", ".docx")
    ]
    overwrite = False
    if any(path.exists() for path in expected):
        overwrite = Confirm.ask(
            "Esistono già file di trascrizione. Vuoi sostituirli?",
            default=False,
        )
        if not overwrite:
            raise RuntimeError("Scrittura annullata: gli output esistenti sono stati conservati.")

    with console.status("[bright_cyan]Creazione output...[/]", spinner="dots"):
        return Whisper.write_transcription_outputs(
            audio_path,
            all_segments,
            word_timestamps=_cfg["word_ts"],
            overwrite=overwrite,
        )


def show_results(txt_path, srt_path, docx_path, paragraphs, audio_path):
    t = Table(box=box.SIMPLE, show_header=False, padding=(0, 1))
    t.add_column("", width=2)
    t.add_column("File", style="white")
    t.add_column("", style="dim", justify="right")

    for path in (txt_path, srt_path, docx_path):
        size = path.stat().st_size
        size_str = f"{size / 1024:.1f} KB" if size >= 1024 else f"{size} B"
        t.add_row("[bright_green]✓[/]", path.name, size_str)

    console.print()
    console.print(
        Panel(
            t,
            title="[bright_green bold]Trascrizione completata[/]",
            border_style="green",
            padding=(0, 1),
        )
    )
    console.print(
        f"\n  [dim]Paragrafi:[/] [bright_cyan]{len(paragraphs)}[/]   "
        f"[dim]Directory:[/] [white]{audio_path.parent}[/]\n"
    )


# ── Main ───────────────────────────────────────────────────────────────────────

def main():
    clear_screen()
    print_header()

    step_rule(1, "Seleziona il file da trascrivere")
    audio_path = ask_file()
    ok(f"[bright_white]{audio_path.name}[/]  [dim]{audio_path.parent}[/]")

    step_rule(2, "Configura le opzioni")
    cfg = {
        "model":    "medium",
        "language": "it",
        "device":   "auto",
        "beam_size": 5,
        "vad":      True,
        "word_ts":  False,
    }
    cfg = configure(cfg)

    all_segments, info = transcribe(audio_path, cfg)
    try:
        txt_path, srt_path, docx_path, paragraphs = write_outputs(audio_path, all_segments, cfg)
    except (OSError, RuntimeError, ValueError) as exc:
        err(str(exc))
        return
    show_results(txt_path, srt_path, docx_path, paragraphs, audio_path)


if __name__ == "__main__":
    main()
