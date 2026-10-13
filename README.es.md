# WhisperLS 1.1.2

[Italiano](README.md) · [English](README.en.md) · [Français](README.fr.md) · **Español** · [Deutsch](README.de.md) · [Português](README.pt.md)

**Transcripción local y privada de audio y vídeo para Windows.**

WhisperLS utiliza [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper)
para convertir clases, reuniones y entrevistas en archivos **TXT**, **SRT** o
**DOCX**. Una vez descargado el modelo, la transcripción se ejecuta en el equipo.
Los vídeos se procesan directamente, sin conversión manual a audio.

> **Novedades de 1.1.0:** interfaz y consola en seis idiomas, idioma de
> transcripción alineado inicialmente con la interfaz y un nuevo gestor de
> colas mixtas con arrastrar y soltar. Consulta el [changelog](CHANGELOG.md).

## Descarga

| Paquete | Uso recomendado | CUDA incluido |
|---|---|---:|
| `WhisperLS-Setup-1.1.2-Full-x64.exe` | Opción recomendada, especialmente con GPU NVIDIA | Sí |
| `WhisperLS-Setup-1.1.2-Light-x64.exe` | CPU o sistemas con CUDA ya configurado | No |
| `WhisperLS-Portable-1.1.2-Full-x64.zip` | Uso sin instalación | Sí |

Los paquetes oficiales incluyen Python y FFmpeg. El controlador NVIDIA no está
incluido. Como esta versión no tiene firma digital, descárgala únicamente desde
la release oficial y comprueba `SHA256SUMS.txt` si lo necesitas.

## Primer inicio

1. Elige el idioma de la interfaz.
2. Pulsa **Elegir pistas** y añade uno o varios archivos.
3. En **Ajustes**, selecciona modelo, idioma de transcripción y formatos.
4. Descarga el modelo si todavía no está instalado.
5. Inicia la transcripción y abre la carpeta de salida al finalizar.

El idioma se guarda en `%LocalAppData%\WhisperLS\preferences.json` y puede
cambiarse desde la bandera de la barra superior. El idioma de transcripción lo
sigue de forma predeterminada, pero puede modificarse por separado.

## Funciones

- Audio: MP3, WAV, M4A, FLAC, AAC, OGG y WMA.
- Vídeo: MP4, MOV, MKV, AVI y WEBM.
- Salidas TXT, SRT y DOCX seleccionables por separado.
- Cola mixta con trabajos individuales y transcripciones multiparte.
- Reordenación mediante arrastrar y soltar, unión, separación y eliminación.
- Nombre de salida por trabajo y protección contra sobrescritura.
- Intervalo temporal, VAD y marcas por palabra opcionales.
- Gestor de modelos con estado local y estimación de idoneidad.
- Puntos de control y restauración de colas interrumpidas.
- Consola técnica en colores y actualizador integrado.

## Desarrollo

Windows 10 1809+, SDK de .NET 10 y x64:

```powershell
dotnet build FasterWhisperGui/FasterWhisperGui.csproj -c Debug -p:Platform=x64
pytest -q
```

## Licencia

MIT. Consulta [LICENSE](LICENSE). Los componentes de terceros conservan sus
propias licencias.
