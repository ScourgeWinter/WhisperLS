# WhisperLS 1.1.0

[Italiano](README.md) · [English](README.en.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · **Português**

**Transcrição local e privada de áudio e vídeo para Windows.**

O WhisperLS utiliza o
[Faster-Whisper](https://github.com/SYSTRAN/faster-whisper) para transformar
aulas, reuniões, entrevistas e gravações em ficheiros **TXT**, **SRT** ou
**DOCX**. Depois de transferir um modelo, a transcrição é executada no próprio
computador. Os vídeos são processados diretamente, sem conversão manual para
áudio.

> **Novidades da versão 1.1.0:** interface e consola em seis idiomas, idioma de
> transcrição inicialmente alinhado com a interface e um novo gestor de filas
> mistas com arrastar e largar. Consulte o [changelog](CHANGELOG.md).

## Transferência

| Pacote | Utilização recomendada | CUDA incluído |
|---|---|---:|
| `WhisperLS-Setup-1.1.0-Full-x64.exe` | Opção recomendada, sobretudo com GPU NVIDIA | Sim |
| `WhisperLS-Setup-1.1.0-Light-x64.exe` | CPU ou sistemas com CUDA já configurado | Não |
| `WhisperLS-Portable-1.1.0-Full-x64.zip` | Utilização sem instalação | Sim |

Os pacotes oficiais incluem Python e FFmpeg. O controlador NVIDIA não está
incluído. Como esta versão não possui assinatura digital, transfira-a apenas da
release oficial e verifique `SHA256SUMS.txt` se necessário.

## Primeiro arranque

1. Escolha o idioma da interface.
2. Selecione **Escolher faixas** e adicione um ou mais ficheiros.
3. Em **Definições**, escolha modelo, idioma da transcrição e formatos de saída.
4. Transfira o modelo caso ainda não esteja instalado.
5. Inicie a transcrição e abra a pasta de saída quando terminar.

O idioma da interface é guardado em
`%LocalAppData%\WhisperLS\preferences.json` e pode ser alterado através da
bandeira na barra superior. O idioma da transcrição acompanha-o inicialmente,
mas pode depois ser alterado de forma independente.

## Funcionalidades

- Áudio: MP3, WAV, M4A, FLAC, AAC, OGG e WMA.
- Vídeo: MP4, MOV, MKV, AVI e WEBM.
- Saídas TXT, SRT e DOCX selecionáveis individualmente.
- Fila mista com trabalhos individuais e transcrições com várias partes.
- Reordenação por arrastar e largar, união, separação e remoção.
- Nome de saída por trabalho e proteção contra substituição acidental.
- Intervalo temporal, VAD e marcas temporais por palavra opcionais.
- Gestor de modelos com estado local e estimativa de adequação.
- Pontos de controlo e restauro de filas interrompidas.
- Consola técnica colorida e atualizador integrado.

## Desenvolvimento

Windows 10 1809+, SDK .NET 10 e arquitetura x64:

```powershell
dotnet build FasterWhisperGui/FasterWhisperGui.csproj -c Debug -p:Platform=x64
python -m pytest -q
```

## Licença

MIT. Consulte [LICENSE](LICENSE). Os componentes de terceiros mantêm as
respetivas licenças.
