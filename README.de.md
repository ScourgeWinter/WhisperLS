# WhisperLS 1.1.1

[Italiano](README.md) · [English](README.en.md) · [Français](README.fr.md) · [Español](README.es.md) · **Deutsch** · [Português](README.pt.md)

**Lokale und private Audio- und Videotranskription für Windows.**

WhisperLS verwendet [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper),
um Vorlesungen, Besprechungen und Interviews als **TXT**, **SRT** oder **DOCX**
zu speichern. Nach dem Modelldownload läuft die Transkription auf dem eigenen
Computer. Videodateien werden direkt verarbeitet und müssen nicht manuell in
Audio umgewandelt werden.

> **Neu in 1.1.0:** Oberfläche und Konsole in sechs Sprachen, eine anfänglich
> an die Oberfläche angepasste Transkriptionssprache und eine neue Verwaltung
> gemischter Warteschlangen per Drag-and-drop. Siehe [Changelog](CHANGELOG.md).

## Download

| Paket | Empfohlene Verwendung | CUDA enthalten |
|---|---|---:|
| `WhisperLS-Setup-1.1.1-Full-x64.exe` | Empfohlen, insbesondere für NVIDIA-GPUs | Ja |
| `WhisperLS-Setup-1.1.1-Light-x64.exe` | CPU-Systeme oder bereits eingerichtetes CUDA | Nein |
| `WhisperLS-Portable-1.1.1-Full-x64.zip` | Verwendung ohne Installation | Ja |

Die offiziellen Pakete enthalten Python und FFmpeg. Der NVIDIA-Treiber ist nicht
enthalten. Da diese Version nicht digital signiert ist, sollte sie nur aus dem
offiziellen Release geladen und bei Bedarf mit `SHA256SUMS.txt` geprüft werden.

## Erster Start

1. Sprache der Benutzeroberfläche auswählen.
2. **Spuren auswählen** und eine oder mehrere Dateien hinzufügen.
3. Unter **Einstellungen** Modell, Transkriptionssprache und Formate wählen.
4. Das Modell herunterladen, falls es noch nicht installiert ist.
5. Transkription starten und anschließend den Ausgabeordner öffnen.

Die Oberflächensprache wird in
`%LocalAppData%\WhisperLS\preferences.json` gespeichert und über die Flagge in
der oberen Leiste geändert. Die Transkriptionssprache übernimmt sie zunächst,
kann danach aber unabhängig geändert werden.

## Funktionen

- Audio: MP3, WAV, M4A, FLAC, AAC, OGG und WMA.
- Video: MP4, MOV, MKV, AVI und WEBM.
- TXT-, SRT- und DOCX-Ausgaben einzeln auswählbar.
- Gemischte Warteschlange aus Einzelaufträgen und mehrteiligen Transkriptionen.
- Sortieren per Drag-and-drop sowie Zusammenführen, Trennen und Entfernen.
- Eigener Ausgabename pro Auftrag und Schutz vor Überschreiben.
- Optionaler Zeitbereich, VAD und Wortzeitstempel.
- Modellverwaltung mit lokalem Status und Eignungsschätzung.
- Prüfpunkte und Wiederherstellung unterbrochener Warteschlangen.
- Farbige technische Konsole und integrierter Updater.

## Entwicklung

Windows 10 1809+, .NET 10 SDK und x64:

```powershell
dotnet build FasterWhisperGui/FasterWhisperGui.csproj -c Debug -p:Platform=x64
pytest -q
```

## Lizenz

MIT. Siehe [LICENSE](LICENSE). Komponenten von Drittanbietern behalten ihre
jeweiligen Lizenzen.
