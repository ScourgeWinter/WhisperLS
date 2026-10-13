# WhisperLS 1.1.2

[Italiano](README.md) · **English** · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português](README.pt.md)

**Private, offline audio and video transcription for Windows.**

WhisperLS is a Windows desktop application powered by
[Faster-Whisper](https://github.com/SYSTRAN/faster-whisper). It turns lectures,
meetings, interviews and recordings into **TXT**, **SRT** or **DOCX** files.
After downloading a model, transcription runs locally: source media is not sent
to an external transcription service. Audio and video files are accepted
directly; manual video-to-audio conversion is not required.

> **New in 1.1.0:** six-language UI and console, transcription-language
> defaults aligned with the interface, and a redesigned mixed-queue manager
> with drag and drop. See the full [changelog](CHANGELOG.md).

## Download

Open the repository **Releases** page and choose:

| Package | Recommended use | CUDA included |
|---|---|---:|
| `WhisperLS-Setup-1.1.2-Full-x64.exe` | Recommended for most users and NVIDIA GPUs | Yes |
| `WhisperLS-Setup-1.1.2-Light-x64.exe` | CPU systems or machines with CUDA already configured | No |
| `WhisperLS-Portable-1.1.2-Full-x64.zip` | Run without installation | Yes |

Official packages include a private Python runtime. You do not need to install
Python, FFmpeg or dependencies manually. NVIDIA drivers are not bundled.
This unsigned release may trigger Windows SmartScreen; download it only from the
official release and verify `SHA256SUMS.txt` if required.

## First run

1. Choose the interface language: English, French, Italian, Spanish, German or Portuguese.
2. Select **Choose tracks** and add one or more audio/video files.
3. Open **Settings** and choose the model, transcription language and outputs.
4. Download the selected model if it is not installed.
5. Select **Start transcription**.
6. Open the output folder when processing completes.

The interface choice is stored in
`%LocalAppData%\WhisperLS\preferences.json`. It can be changed from the flag in
the top bar. The transcription language initially follows it and can then be
changed independently.

## Main features

- Audio: MP3, WAV, M4A, FLAC, AAC, OGG and WMA.
- Video: MP4, MOV, MKV, AVI and WEBM.
- Individually selectable TXT, SRT and DOCX outputs.
- Mixed queues containing independent jobs and multipart transcriptions.
- Drag-and-drop queue ordering, merge, split and removal controls.
- Per-job output names with safe automatic defaults and collision handling.
- Optional time-range transcription, VAD and word timestamps.
- Model Manager with local status and hardware suitability estimate.
- Persistent model reuse across queued jobs.
- Recovery checkpoints and interrupted-queue restoration.
- Color-coded technical console with stable manual scrolling.
- Background update checks with version-specific reminders.

Multipart TXT and DOCX files include a clear boundary between source tracks;
SRT output keeps a continuous timeline. Temporary checkpoint JSON files are
deleted after successful completion.

## Privacy and storage

Transcription is local after model download. Models are stored in the Hugging
Face cache; preferences and recovery state live in
`%LocalAppData%\WhisperLS`. Uninstalling normally preserves user data and model
caches unless the optional complete-removal setting is selected.

## Development

Requirements: Windows 10 1809 or newer, .NET 10 SDK and x64. Build the unpackaged
WinUI application with:

```powershell
dotnet build FasterWhisperGui/FasterWhisperGui.csproj -c Debug -p:Platform=x64
pytest -q
```

The backend can also be used from the command line; run `python Whisper.py
--help` for the complete option list. Release automation builds Full/Light
installers, Portable Full, updater payloads, manifests and checksums.

## License

MIT. See [LICENSE](LICENSE). Faster-Whisper, Whisper models and NVIDIA runtime
components retain their respective third-party licenses.
