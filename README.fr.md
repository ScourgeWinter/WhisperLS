# WhisperLS 1.1.0

[Italiano](README.md) · [English](README.en.md) · **Français** · [Español](README.es.md) · [Deutsch](README.de.md) · [Português](README.pt.md)

**Transcription locale et confidentielle de fichiers audio et vidéo sous Windows.**

WhisperLS utilise [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper)
pour convertir cours, réunions et entretiens en fichiers **TXT**, **SRT** ou
**DOCX**. Après le téléchargement du modèle, la transcription s’effectue sur
votre ordinateur. Les vidéos sont acceptées directement, sans conversion audio
manuelle.

> **Nouveautés 1.1.0 :** interface et console en six langues, langue de
> transcription alignée initialement sur l’interface et nouveau gestionnaire
> de file mixte avec glisser-déposer. Voir le [changelog](CHANGELOG.md).

## Téléchargement

| Paquet | Utilisation conseillée | CUDA inclus |
|---|---|---:|
| `WhisperLS-Setup-1.1.0-Full-x64.exe` | Choix recommandé, notamment avec une carte NVIDIA | Oui |
| `WhisperLS-Setup-1.1.0-Light-x64.exe` | Processeur seul ou CUDA déjà configuré | Non |
| `WhisperLS-Portable-1.1.0-Full-x64.zip` | Utilisation sans installation | Oui |

Les paquets officiels incluent Python et FFmpeg. Le pilote NVIDIA reste à la
charge de l’utilisateur. La version n’étant pas signée numériquement, utilisez
uniquement la release officielle et, si nécessaire, vérifiez `SHA256SUMS.txt`.

## Première utilisation

1. Choisissez la langue de l’interface.
2. Cliquez sur **Choisir les pistes** et ajoutez vos fichiers.
3. Dans **Paramètres**, choisissez modèle, langue de transcription et formats.
4. Téléchargez le modèle s’il n’est pas encore installé.
5. Lancez la transcription puis ouvrez le dossier de sortie.

La langue de l’interface est mémorisée dans
`%LocalAppData%\WhisperLS\preferences.json` et peut être modifiée depuis le
drapeau de la barre supérieure. La langue de transcription reprend ce choix par
défaut, puis reste modifiable indépendamment.

## Fonctionnalités

- Audio : MP3, WAV, M4A, FLAC, AAC, OGG et WMA.
- Vidéo : MP4, MOV, MKV, AVI et WEBM.
- Sorties TXT, SRT et DOCX sélectionnables séparément.
- File mixte de tâches individuelles et de transcriptions multiparts.
- Réorganisation par glisser-déposer, fusion, séparation et suppression.
- Nom de sortie par tâche et protection contre l’écrasement.
- Plage temporelle, VAD et horodatage par mot facultatifs.
- Gestionnaire de modèles avec état local et estimation d’adéquation.
- Points de contrôle et restauration d’une file interrompue.
- Console technique colorée et mise à jour automatique intégrée.

## Développement

Windows 10 1809+, SDK .NET 10 et architecture x64 :

```powershell
dotnet build FasterWhisperGui/FasterWhisperGui.csproj -c Debug -p:Platform=x64
pytest -q
```

## Licence

MIT. Voir [LICENSE](LICENSE). Les composants tiers conservent leurs licences.
