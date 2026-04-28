# WhisperLS

WhisperLS is a Windows transcription tool built around `faster-whisper`.
It includes:

- a Python CLI backend (`Whisper.py`) that downloads Whisper models, transcribes audio/video, and writes TXT, SRT, and DOCX outputs;
- a WinUI 3 desktop GUI (`FasterWhisperGui`) that guides model selection and launches the Python backend.

## Requirements

- Windows 10/11
- .NET SDK 10 or later for building the WinUI app
- Python 3.10+
- Python packages from `requirements.txt`
- Optional NVIDIA CUDA runtime for GPU acceleration

## Python Setup

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

## CLI Usage

```powershell
.\.venv\Scripts\python.exe Whisper.py <audio_or_video_file> [model] --device auto --language auto --beam-size 5
```

Useful commands:

```powershell
.\.venv\Scripts\python.exe Whisper.py --list-models
.\.venv\Scripts\python.exe Whisper.py --download-model small
.\.venv\Scripts\python.exe Whisper.py --check-model small
```

Outputs are written next to the source media file as:

- `<name>_trascrizione.txt`
- `<name>_trascrizione.srt`
- `<name>_trascrizione.docx`

## GUI Build

```powershell
dotnet build FasterWhisperGui\FasterWhisperGui.csproj -p:Platform=x64
```

The x64 debug executable is produced at:

```text
FasterWhisperGui\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FasterWhisperGui.exe
```

The GUI looks upward from its executable directory until it finds `Whisper.py`, then prefers `.venv\Scripts\python.exe` from the repository root.
