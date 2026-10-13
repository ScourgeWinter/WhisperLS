# WhisperLS 1.1.2

**Italiano** · [English](README.en.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português](README.pt.md)

**Trascrizione locale di audio e video, semplice da usare e completa quando serve.**

WhisperLS è un’applicazione desktop per Windows basata su
[Faster-Whisper](https://github.com/SYSTRAN/faster-whisper). Trasforma lezioni,
riunioni, interviste e registrazioni in documenti **TXT**, **SRT** e **DOCX**.
Dopo aver scaricato il modello scelto, la trascrizione avviene sul computer:
l’audio non viene inviato a servizi esterni.

> WhisperLS legge direttamente sia file audio sia file video. Non è necessario
> convertire manualmente un MP4, MKV o MOV in audio.

> **Novità 1.1.0:** interfaccia e console in sei lingue, lingua di
> trascrizione inizialmente allineata all’interfaccia e nuovo manager per code
> miste con drag and drop. Consulta il [changelog completo](CHANGELOG.md).

## Download

Apri la sezione **Releases** del repository e scegli il pacchetto adatto:

| Pacchetto | Per chi è pensato | CUDA inclusa |
|---|---|---:|
| `WhisperLS-Setup-1.1.2-Full-x64.exe` | Scelta consigliata per la maggior parte degli utenti | Sì |
| `WhisperLS-Setup-1.1.2-Light-x64.exe` | Computer senza GPU NVIDIA o sistemi già configurati | No |
| `WhisperLS-Portable-1.1.2-Full-x64.zip` | Uso senza installazione | Sì |

Le edizioni ufficiali includono un runtime Python privato: **non devi installare
Python, FFmpeg o le dipendenze manualmente**. I modelli vengono scaricati
separatamente dal Model Manager, così puoi conservare solo quelli che utilizzi.

La prima release non è firmata digitalmente. Windows SmartScreen può quindi
mostrare “Autore sconosciuto”: verifica di aver scaricato il file dalla release
ufficiale e, se vuoi, confronta l’hash con `SHA256SUMS.txt`.

### Full o Light?

Un PC con processore AMD può trascrivere usando la **CPU**. Se possiedi una
Radeon e non una GPU NVIDIA, scegli **Light** e il dispositivo **CPU** (oppure
Automatico, che ripiega sulla CPU). Le GPU Radeon non sono accelerate dal
backend Windows precompilato incluso: l'opzione GPU usa NVIDIA/CUDA.

Da questa versione, anche Windows App SDK/WinUI è incluso nei pacchetti;
non serve installare Windows App Runtime separatamente.

- Scegli **Full** se possiedi una GPU NVIDIA o vuoi un pacchetto autonomo. Include
  cuBLAS e cuDNN; è più grande ma evita configurazioni CUDA manuali.
- Scegli **Light** se userai la CPU. Può usare una configurazione CUDA già
  disponibile nel sistema, ma non la incorpora.
- Il **driver NVIDIA** non è incluso in nessuna edizione e deve essere installato
  dal produttore. Se CUDA non è disponibile, la modalità `Automatico` passa alla
  CPU.

## Primo utilizzo

1. Al primo avvio scegli la lingua dell’interfaccia tra italiano, inglese,
   francese, spagnolo, tedesco e portoghese.
2. Premi **Scegli tracce** e seleziona uno o più file audio/video.
3. Apri **Impostazioni** e scegli modello, lingua audio e formati di output.
4. Se il modello non è ancora presente, premi **Scarica modello**.
5. Premi **Avvia trascrizione**.
6. Al termine usa **Apri cartella output**.

La cartella predefinita è quella della traccia sorgente. Puoi cambiarla dalle
impostazioni. Se un nome esiste già, WhisperLS conserva il file precedente e
crea automaticamente `(2)`, `(3)` e così via.

## Funzioni principali

### Interfaccia multilingue

- Onboarding linguistico dedicato al primo avvio.
- Interfaccia disponibile in italiano, inglese, francese, spagnolo, tedesco e
  portoghese.
- La scelta viene conservata in `%LocalAppData%\WhisperLS\preferences.json` e
  rimane valida dopo aggiornamenti e reinstallazioni standard.
- La lingua dell’interfaccia può essere cambiata in qualsiasi momento dal
  pulsante con la bandiera nella barra superiore. La lingua della trascrizione
  parte dallo stesso valore, ma può essere modificata indipendentemente.

### Audio, video e output

- Audio: MP3, WAV, M4A, FLAC, AAC, OGG e WMA.
- Video: MP4, MOV, MKV, AVI e WEBM.
- Output selezionabili singolarmente: TXT, SRT e DOCX.
- Nome output personalizzabile per ogni lavoro direttamente nel manager della coda.
- Scrittura atomica: un file finale compare solo dopo una scrittura riuscita.
- Raggruppamento automatico del testo in paragrafi leggibili.

### Più tracce e code miste

Dopo la selezione, ogni traccia parte come lavoro singolo e **Gestisci coda**
apre il manager dedicato anche quando è presente un solo file. Ogni riga
rappresenta un lavoro e può essere:

- **Singolo**: produce un output indipendente per quella traccia.
- **Multiparte**: unisce più registrazioni nello stesso documento; TXT e DOCX
  mostrano chiaramente la fine di ogni parte, mentre l’SRT mantiene una timeline
  continua.

La stessa coda può contenere contemporaneamente lavori singoli e multiparte.
Nel manager puoi cambiarne l’ordine, unire un lavoro al precedente, separare un
lavoro multiparte in elementi singoli oppure rimuoverlo. L’ordine può essere
modificato anche trascinando i lavori direttamente nella posizione desiderata.
Il worker mantiene il
modello in memoria tra lavori compatibili, evitando ricaricamenti inutili; un
errore su un lavoro non interrompe i successivi.

La coda mostra per ogni elemento gli stati **In attesa**, **In corso**,
**Completata**, **Fallita** o **Annullata**. Se WhisperLS viene chiuso durante
una coda, al riavvio compare **Ripristina coda**.

Il **Manager della coda** consente di assegnare un nome distinto a ogni lavoro.
Il campo può essere lasciato vuoto: un lavoro singolo usa automaticamente il
nome del file sorgente, mentre un lavoro multiparte usa `LavoroMultiparte`.
WhisperLS evita comunque di sovrascrivere file esistenti aggiungendo un suffisso
progressivo quando necessario.

### Recupero delle interruzioni

Durante la trascrizione viene creato un checkpoint
`<nome-output>.whisperls.json`. Contiene configurazione, sorgenti, segmenti e
ultimo timestamp elaborato ed è aggiornato atomicamente a intervalli brevi.

- Se il lavoro termina correttamente, il checkpoint viene eliminato.
- Se il processo viene interrotto, il checkpoint rimane e il pulsante
  **Riprendi** continua dall’ultimo punto valido.
- Prima della ripresa WhisperLS verifica dimensione e data di modifica delle
  sorgenti, evitando di applicare dati vecchi a un file cambiato.

Il JSON è un file tecnico temporaneo, non un quarto formato di esportazione.

### Intervallo temporale

Abilita **Solo intervallo** per trascrivere una porzione della traccia. Sono
accettati secondi, `MM:SS` e `HH:MM:SS`. L’intervallo è disponibile per una
traccia singola e per ogni lavoro della coda; non è applicabile alla modalità
multiparte aggregata.

### Modelli e hardware

Il Model Manager permette di:

- vedere quali modelli sono già presenti;
- scaricare e verificare un modello;
- disinstallarlo dalla cache;
- confrontare i modelli tramite una percentuale di **appropriatezza**.

L’appropriatezza è una stima locale basata su RAM, CPU e, quando rilevabile,
VRAM NVIDIA. Aiuta nella scelta, ma non sostituisce un benchmark.

### Impostazioni avanzate

- **Dispositivo**: automatico, GPU NVIDIA/CUDA o CPU.
- **Lingua**: lingua forzata oppure rilevamento automatico.
- **Beam size**: più ipotesi di decodifica possono aumentare la precisione ma
  richiedono più tempo; `5` è un valore bilanciato.
- **VAD**: riconosce le zone con voce e riduce l’elaborazione dei silenzi.
- **Timestamp per parola**: crea cue SRT a livello di singola parola.

Le icone informative nella finestra Impostazioni spiegano le opzioni senza
obbligare l’utente a conoscere la terminologia tecnica.

### Console tecnica

La console espandibile usa colori coerenti per informazioni, modello,
avanzamento, output, successo, avvisi ed errori. La progressione della
trascrizione occupa una sola riga aggiornata. Se scorri verso l’alto, gli
aggiornamenti non ti riportano forzatamente in fondo.

## Aggiornamenti automatici

All’avvio WhisperLS controlla in background l’ultima release stabile su GitHub.
La GUI non viene bloccata e un errore di rete non impedisce l’utilizzo del tool.
Puoi anche usare il pulsante **Controlla aggiornamenti** nella barra superiore.

Quando è disponibile una versione nuova:

1. la finestra mostra versione installata, nuova versione e changelog;
2. **Download** scarica il pacchetto mostrando percentuale e MB;
3. manifest e SHA-256 vengono verificati prima dell’estrazione;
4. **Riavvia WhisperLS per rendere effettive le modifiche** chiude l’app e
   avvia l’helper .NET;
5. l’helper conserva un backup, aggiorna soltanto i file gestiti e riavvia il
   programma;
6. al primo avvio riuscito il backup viene eliminato.

L’updater conserva l’edizione installata: una Light riceve aggiornamenti Light,
mentre Full e Portable Full continuano a ricevere il runtime CUDA completo.

L’aggiornamento può essere scaricato durante una trascrizione, ma viene applicato
solo quando il tool è inattivo. In caso di errore l’helper ripristina i file
precedenti e salva un log in `%LocalAppData%\WhisperLS\Logs`.

Se scegli **Annulla**, l’avviso ricompare all’avvio successivo. Dopo tre rinvii
puoi selezionare **Non ricordarmelo più per questa versione**: una release futura
verrà comunque proposta.

WhisperLS non usa script PowerShell per aggiornarsi.

## Dove vengono conservati i dati

| Dato | Posizione |
|---|---|
| Impostazioni e stato updater | `%LocalAppData%\WhisperLS` |
| Coda interrotta | `%LocalAppData%\WhisperLS\pending-queue.json` |
| Log updater | `%LocalAppData%\WhisperLS\Logs` |
| Modelli | cache Hugging Face dell’utente |
| Checkpoint | cartella di output scelta |
| Trascrizioni | cartella sorgente o cartella scelta |

Aggiornamenti e disinstallazione standard non eliminano dati, modelli o
trascrizioni. Il disinstallatore offre una casella separata per rimuovere anche
i dati locali di WhisperLS.

## Utilizzo da riga di comando

Per sviluppo o automazione puoi usare direttamente il backend:

```powershell
python Whisper.py "Lezione 01.mp4" medium `
  --device auto `
  --language it `
  --output-name "Reumatologia_lezione_01" `
  --auto-rename `
  --output-format txt `
  --output-format docx
```

Esempio multiparte:

```powershell
python Whisper.py "Lezione parte 1.m4a" medium `
  --input-file "Lezione parte 2.m4a" `
  --output-name "Lezione completa" `
  --output-format txt `
  --output-format srt `
  --output-format docx
```

| Opzione | Funzione | Default |
|---|---|---|
| `--input-file <file>` | aggiunge una parte alla stessa trascrizione | — |
| `--resume-checkpoint <file>` | riprende un checkpoint interrotto | — |
| `--clip-start <tempo>` | inizio dell’intervallo | inizio file |
| `--clip-end <tempo>` | fine dell’intervallo | fine file |
| `--device auto\|cuda\|cpu` | dispositivo di inferenza | `auto` |
| `--language <codice\|auto>` | lingua o rilevamento automatico | `it` |
| `--beam-size 1..20` | ampiezza della decodifica | `5` |
| `--word-timestamps` | timestamp per singola parola | disattivo |
| `--no-vad` | disabilita il filtro voce | disattivo |
| `--output-dir <cartella>` | directory di destinazione | sorgente |
| `--output-name <nome>` | nome condiviso senza estensione | `<source>_trascrizione` |
| `--auto-rename` | aggiunge `(2)`, `(3)` in caso di collisione | disattivo |
| `--output-format txt\|srt\|docx` | formato; opzione ripetibile | tutti |
| `--overwrite` | sostituisce gli output esistenti | disattivo |
| `--list-models` | elenca i modelli supportati | — |
| `--check-model <modello>` | controlla la cache senza download | — |
| `--model-status-json` | restituisce gli stati in JSON | — |
| `--download-model <modello>` | scarica e verifica un modello | — |
| `--delete-model <modello>` | rimuove un modello dalla cache | — |

`--batch-manifest` è il protocollo JSON usato internamente dalla GUI per il
worker persistente. È disponibile anche per integrazioni avanzate, ma non è
necessario per l’uso quotidiano.

## Sviluppo

### Requisiti

- Windows 10 1809 o successivo, x64;
- .NET SDK 10;
- Python 3.10 o successivo;
- dipendenze in `requirements.txt`;
- facoltativamente `requirements-full.txt` per il runtime NVIDIA completo.

### Backend

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
```

`setup-backend.ps1` esegue la stessa preparazione per l’ambiente di sviluppo;
non viene distribuito né usato dall’updater.

### Build WinUI e updater

```powershell
dotnet build FasterWhisperGui\FasterWhisperGui.csproj -p:Platform=x64
dotnet publish WhisperLS.Updater\WhisperLS.Updater.csproj -c Release -r win-x64
```

La GUI è **unpackaged**: installer e portable contengono i runtime necessari e
si avviano tramite `WhisperLS.exe`.

### Test

```powershell
python -m unittest discover -s tests -v
python -m py_compile Whisper.py run.py tools\build_release.py
ruff check Whisper.py run.py tests tools\build_release.py
dotnet build FasterWhisperGui\FasterWhisperGui.csproj -p:Platform=x64
dotnet build WhisperLS.Updater\WhisperLS.Updater.csproj -c Release
```

La workflow `ci.yml` esegue questi controlli a ogni push e pull request. Quando
viene pubblicato un tag come `v1.1.0`, `release.yml` crea automaticamente
installer Full/Light, portable Full, payload updater, manifest e hash.

### Struttura del repository

```text
FasterWhisperGui/       applicazione desktop WinUI 3
WhisperLS.Updater/      helper .NET per update, backup e rollback
installer/              definizione installer per utente
tools/build_release.py  packaging ripetibile degli asset win-x64
tests/                  test automatici del backend
Whisper.py              backend, CLI, checkpoint e worker persistente
run.py                  interfaccia testuale Rich per sviluppatori
requirements*.txt       dipendenze base e runtime NVIDIA Full
.github/workflows/      CI e pubblicazione automatica delle release
```

## Sicurezza, privacy e limiti

- La trascrizione è locale; GitHub viene contattato per controllare gli
  aggiornamenti e Hugging Face quando scarichi un modello.
- Il manifest updater accetta soltanto il repository ufficiale e impedisce
  percorsi che escano dalle directory di staging/installazione.
- Gli hash garantiscono l’integrità del download, ma non sostituiscono una firma
  digitale. La versione 1.1.0 non è firmata.
- La qualità dipende da registrazione, rumore, lingua e modello scelto.
- L’appropriatezza hardware è indicativa.

## Licenza

WhisperLS è distribuito con licenza MIT. Consulta [`LICENSE`](LICENSE).

Le dipendenze mantengono le rispettive licenze; i runtime NVIDIA della variante
Full sono soggetti ai termini di redistribuzione NVIDIA.
