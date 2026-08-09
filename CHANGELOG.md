# Changelog

All notable changes to WhisperLS are documented in this file. The project
follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Stabilized GitHub Actions by pinning the Ruff development dependency and
  defining an explicit lint policy.
- Separated tests, linting, WinUI compilation, and updater compilation so a
  failed check can no longer be hidden by a later successful command.
- Updated official GitHub Actions runtimes and prevented duplicate CI runs on
  release tags.
- Expanded public-repository ignore rules for local IDE and coding-agent files.

## [1.1.0] - 2026-08-09

### Added

- Interface available in Italian, English, French, Spanish, German, and
  Portuguese.
- First-run language selection with flags, persistent local storage, and an
  interface-language selector in the top bar.
- Transcription language initially follows the interface language while
  remaining independently configurable.
- Localized console output, status messages, categories, progress, warnings,
  and errors.
- Redesigned queue manager supporting independent jobs and multipart
  transcriptions within the same queue.
- Animated drag-and-drop reordering constrained to the queue area.
- Configurable output name for each job with safe automatic defaults.

### Improved

- Multiple selected tracks now start as separate jobs and are merged only when
  explicitly requested.
- More compact queue-manager layout, explicit actions, and clearer separation
  between jobs and source files.
- Native translation of selector values, including automatic device selection
  and transcription languages.
- Complete documentation in all six supported interface languages.

### Fixed

- Fixed crashes when opening the queue manager.
- Fixed first-job overlap during drag-and-drop reordering.
- Fixed clipped borders, command alignment, and drag-and-drop containment.
- Removed untranslated Italian strings from the French interface and console
  categories.

## [1.0.0] - 2026-08-02

- First public release of WhisperLS.
- Local audio and video transcription with TXT, SRT, and DOCX output.
- Full and Light installers, Portable package, integrated updater, Model
  Manager, recovery checkpoints, and CUDA/CPU support.

[Unreleased]: https://github.com/ScourgeWinter/WhisperLS/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/ScourgeWinter/WhisperLS/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ScourgeWinter/WhisperLS/releases/tag/v1.0.0
