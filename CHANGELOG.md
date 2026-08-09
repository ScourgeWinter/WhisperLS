# Changelog

Le modifiche rilevanti di WhisperLS sono documentate in questo file. Il
progetto segue il [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-08-09

### Novità

- Interfaccia disponibile in italiano, inglese, francese, spagnolo, tedesco e
  portoghese.
- Scelta della lingua al primo avvio tramite bandiere, memorizzazione locale e
  selettore sempre accessibile dalla barra superiore.
- Lingua di trascrizione inizialmente allineata alla lingua dell’interfaccia,
  ma modificabile in modo indipendente.
- Console, messaggi di stato, categorie, avanzamento, avvisi ed errori
  localizzati nella lingua scelta.
- Manager della coda riprogettato per combinare liberamente lavori singoli e
  trascrizioni multiparte nella stessa coda.
- Riordino tramite trascinamento animato e confinato nell’area della coda.
- Nome di output configurabile per ogni lavoro, con valori automatici sicuri.

### Miglioramenti

- Le tracce multiple vengono create come lavori separati per impostazione
  predefinita e possono essere unite solo quando richiesto dall’utente.
- Layout del manager più compatto, azioni esplicite e maggiore distinzione fra
  lavoro e file sorgente.
- Traduzione nativa delle voci dei selettori, inclusi dispositivo automatico e
  lingue di trascrizione.
- Documentazione completa disponibile nelle sei lingue supportate.

### Correzioni

- Risolti crash all’apertura del manager della coda.
- Risolte sovrapposizioni del primo lavoro durante il trascinamento.
- Corretti bordi tagliati, allineamento dei comandi e contenimento del drag and
  drop.
- Eliminati residui in italiano nella modalità francese e nelle categorie
  della console.

## [1.0.0] - 2026-08-02

- Prima release pubblica di WhisperLS.
- Trascrizione locale audio/video con output TXT, SRT e DOCX.
- Installer Full e Light, pacchetto Portable, updater integrato, Model Manager,
  checkpoint di recupero e supporto CUDA/CPU.

[1.1.0]: https://github.com/ScourgeWinter/WhisperLS/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ScourgeWinter/WhisperLS/releases/tag/v1.0.0
