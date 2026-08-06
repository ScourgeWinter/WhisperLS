import tempfile
import unittest
import zipfile
import io
import json
import os
import sys
from contextlib import redirect_stdout
from unittest.mock import Mock, patch
from pathlib import Path
from types import SimpleNamespace

import Whisper


def segment(text, start, end, words=None):
    return SimpleNamespace(text=text, start=start, end=end, words=words or [])


class WhisperTests(unittest.TestCase):
    def test_batch_manifest_reuses_process_and_preserves_job_arguments(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            first = root / "prima.mp3"
            first_continuation = root / "prima-parte-2.mp3"
            second = root / "seconda.mp3"
            first.touch()
            first_continuation.touch()
            second.touch()
            manifest = root / "queue.json"
            manifest.write_text(
                json.dumps(
                    {
                        "jobs": [
                            {
                                "id": "a",
                                "sources": [str(first), str(first_continuation)],
                                "model": "medium",
                                "outputName": "Prima",
                            },
                            {"id": "b", "sources": [str(second)], "model": "medium", "outputName": "Seconda"},
                        ]
                    }
                ),
                encoding="utf-8",
            )
            captured = []
            with patch.object(Whisper, "main", side_effect=lambda: captured.append(sys.argv[1:])), patch.dict(
                os.environ, {"WHISPERLS_PROGRESS": "0"}
            ):
                Whisper.run_batch_manifest(manifest)
            self.assertEqual(len(captured), 2)
            self.assertIn("Prima", captured[0])
            self.assertIn("--input-file", captured[0])
            self.assertIn(str(first_continuation), captured[0])
            self.assertIn("Seconda", captured[1])
            self.assertNotIn("WHISPERLS_BATCH_WORKER", os.environ)

    def test_batch_control_cancels_only_matching_job(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            control = Path(temp_dir) / "control.json"
            control.write_text(
                json.dumps({"action": "cancel-current", "jobId": "job-2"}),
                encoding="utf-8",
            )
            Whisper._BATCH_CONTROL_PATH = control
            Whisper._EVENT_CONTEXT = {"jobId": "job-1"}
            Whisper.check_batch_control()
            self.assertTrue(control.exists())
            Whisper._EVENT_CONTEXT = {"jobId": "job-2"}
            with self.assertRaises(Whisper.BatchJobCancelled):
                Whisper.check_batch_control()
            self.assertFalse(control.exists())
            Whisper._BATCH_CONTROL_PATH = None
            Whisper._EVENT_CONTEXT = {}

    def test_output_name_validation_and_collision_numbering(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            (output_dir / "Lezione_trascrizione.txt").write_text("esiste", encoding="utf-8")
            self.assertEqual(
                Whisper.choose_available_output_name(
                    output_dir,
                    "Lezione_trascrizione",
                    ("txt", "docx"),
                ),
                "Lezione_trascrizione (2)",
            )
            with self.assertRaises(ValueError):
                Whisper.validate_output_name("CON")
            with self.assertRaises(ValueError):
                Whisper.validate_output_name("cartella/nome")

    def test_success_result_protocol(self):
        output = io.StringIO()
        with patch.dict(os.environ, {"WHISPERLS_PROGRESS": "1"}):
            with redirect_stdout(output):
                Whisper.emit_result("transcription", "success", paragraphs=9)

        line = output.getvalue().strip()
        self.assertTrue(line.startswith("__WHISPER_RESULT__ "))
        payload = json.loads(line.removeprefix("__WHISPER_RESULT__ "))
        self.assertEqual(payload["operation"], "transcription")
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["paragraphs"], 9)

    def test_normalize_spacing(self):
        self.assertEqual(Whisper.normalize_spacing("  l ' acqua ,  sì "), "l'acqua, sì")

    def test_srt_time_rounding_and_negative_clamp(self):
        self.assertEqual(Whisper.format_srt_time(61.2346), "00:01:01,235")
        self.assertEqual(Whisper.format_srt_time(-1), "00:00:00,000")

    def test_argument_validation(self):
        self.assertEqual(Whisper.positive_beam_size("5"), 5)
        self.assertEqual(Whisper.validate_language("IT"), "it")
        with self.assertRaises(ValueError):
            Whisper.positive_beam_size("0")
        with self.assertRaises(ValueError):
            Whisper.validate_language("nonsense")
        self.assertEqual(Whisper.parse_time_offset("01:02:03.5"), 3723.5)
        self.assertEqual(Whisper.parse_time_offset("12:30"), 750)

    def test_multiple_input_files_preserve_part_order(self):
        argv = [
            "Whisper.py",
            "parte-01.mp3",
            "medium",
            "--input-file",
            "parte-02.mp3",
            "--input-file",
            "parte-03.mp3",
        ]
        with patch.object(sys, "argv", argv):
            args = Whisper.parse_args()

        self.assertEqual(args.audio_file, "parte-01.mp3")
        self.assertEqual(args.input_files, ["parte-02.mp3", "parte-03.mp3"])

    def test_local_model_requires_complete_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(Exception):
                Whisper.resolve_model_path_with_progress(directory, local_files_only=True)

    def test_context_paragraph_split(self):
        result = Whisper.build_context_paragraphs(
            [
                segment("Prima frase completa.", 0, 2),
                segment("Secondo argomento diverso.", 7, 9),
            ],
            min_words_per_paragraph=1,
        )
        self.assertEqual(len(result), 2)

    def test_atomic_outputs_and_word_level_srt(self):
        words = [
            SimpleNamespace(word=" Ciao", start=0.0, end=0.4),
            SimpleNamespace(word=" mondo", start=0.4, end=0.9),
        ]
        segments = [segment("Ciao mondo", 0.0, 0.9, words)]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "audio.mp3"
            source.write_bytes(b"test")
            txt, srt, docx, paragraphs = Whisper.write_transcription_outputs(
                source,
                segments,
                word_timestamps=True,
            )

            self.assertEqual(paragraphs, ["Ciao mondo"])
            self.assertIn("00:00:00,000 --> 00:00:00,400\nCiao", srt.read_text("utf-8"))
            self.assertIn("00:00:00,400 --> 00:00:00,900\nmondo", srt.read_text("utf-8"))
            self.assertEqual(txt.read_text("utf-8"), "Ciao mondo\n")
            with zipfile.ZipFile(docx) as archive:
                self.assertIsNone(archive.testzip())

            with self.assertRaises(FileExistsError):
                Whisper.write_transcription_outputs(source, segments)

    def test_generates_only_selected_output_formats(self):
        segments = [segment("Solo testo", 0.0, 1.0)]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "audio.mp3"
            source.write_bytes(b"test")

            txt, srt, docx, paragraphs = Whisper.write_transcription_outputs(
                source,
                segments,
                output_formats=("txt",),
            )

            self.assertEqual(paragraphs, ["Solo testo"])
            self.assertIsNotNone(txt)
            self.assertTrue(txt.is_file())
            self.assertIsNone(srt)
            self.assertIsNone(docx)
            self.assertFalse((root / "audio_trascrizione.srt").exists())
            self.assertFalse((root / "audio_trascrizione.docx").exists())

    def test_multipart_outputs_use_format_specific_track_boundaries(self):
        first = [segment("Conclusione della prima parte.", 0.0, 2.0)]
        second = [segment("Inizio della seconda parte.", 2.0, 4.0)]
        combined = first + second
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "lezione-01.mp3"
            source.write_bytes(b"test")

            txt, srt, docx, paragraphs = Whisper.write_transcription_outputs(
                source,
                combined,
                track_sections=[
                    ("lezione-01.mp3", first),
                    ("lezione-02.mp3", second),
                ],
            )

            separator = (
                "//////////////////// FINE TRACCIA AUDIO N. 1 — "
                "lezione-01.mp3 ////////////////////"
            )
            txt_content = txt.read_text("utf-8")
            self.assertIn(separator, txt_content)
            self.assertLess(txt_content.index("prima parte"), txt_content.index(separator))
            self.assertLess(txt_content.index(separator), txt_content.index("seconda parte"))
            self.assertEqual(
                paragraphs,
                ["Conclusione della prima parte.", "Inizio della seconda parte."],
            )

            self.assertNotIn("FINE TRACCIA", srt.read_text("utf-8"))
            with zipfile.ZipFile(docx) as archive:
                document_xml = archive.read("word/document.xml").decode("utf-8")
            self.assertIn("FINE TRACCIA AUDIO N. 1 — lezione-01.mp3", document_xml)
            self.assertIn('<w:jc w:val="center"/>', document_xml)
            self.assertIn('<w:br w:type="page"/>', document_xml)

    def test_rejects_empty_output_format_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "audio.mp3"
            source.write_bytes(b"test")
            with self.assertRaises(ValueError):
                Whisper.write_transcription_outputs(
                    source,
                    [segment("Test", 0.0, 1.0)],
                    output_formats=(),
                )

    def test_transcription_checkpoint_is_atomic_and_removable(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "lezione.mp3"
            source.write_bytes(b"audio")
            checkpoint_path = root / "lezione_trascrizione.whisperls.json"
            checkpoint = Whisper.TranscriptionCheckpoint(
                checkpoint_path,
                sources=[source],
                model="medium",
                requested_device="auto",
                actual_device="cpu",
                compute_type="int8",
                language="it",
                beam_size=5,
                vad_enabled=True,
                word_timestamps=False,
                output_formats=("txt", "docx"),
            )

            checkpoint.flush()
            checkpoint.add_segment(
                SimpleNamespace(
                    start=0.0,
                    end=2.5,
                    text="Testo del segmento",
                    words=[],
                    avg_logprob=-0.2,
                    compression_ratio=1.1,
                    no_speech_prob=0.01,
                ),
                part=1,
                source_name=source.name,
                progress=50,
            )
            checkpoint.complete_part(
                part=1,
                language="it",
                probability=0.98,
                duration=2.5,
                timeline_offset=0.0,
            )

            payload = json.loads(checkpoint_path.read_text("utf-8"))
            self.assertEqual(payload["status"], "in_progress")
            self.assertEqual(payload["progress"], 100)
            self.assertEqual(payload["segments"][0]["text"], "Testo del segmento")
            self.assertEqual(payload["configuration"]["outputFormats"], ["txt", "docx"])
            self.assertFalse(any(root.glob(".*.tmp")))

            checkpoint.remove()
            self.assertFalse(checkpoint_path.exists())

    def test_successful_main_removes_transcription_checkpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "lezione.mp3"
            source.write_bytes(b"audio")
            fake_model = Mock()
            fake_model.transcribe.return_value = (
                [segment("Trascrizione completata.", 0.0, 2.0)],
                SimpleNamespace(language="it", language_probability=0.99, duration=2.0),
            )
            argv = [
                "Whisper.py",
                str(source),
                "medium",
                "--device",
                "cpu",
                "--output-format",
                "txt",
            ]

            with patch.object(sys, "argv", argv), patch.object(
                Whisper,
                "resolve_model_path_with_progress",
                return_value="model",
            ), patch.object(
                Whisper,
                "create_model",
                return_value=(fake_model, "cpu", "int8"),
            ), patch.dict(os.environ, {"WHISPERLS_PROGRESS": "0"}), redirect_stdout(
                io.StringIO()
            ):
                Whisper.main()

            self.assertTrue((root / "lezione_trascrizione.txt").is_file())
            self.assertFalse(
                (root / "lezione_trascrizione.whisperls.json").exists()
            )

    def test_main_resumes_checkpoint_from_last_source_timestamp(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "lezione.mp3"
            source.write_bytes(b"audio")
            checkpoint_path = root / "lezione_trascrizione.whisperls.json"
            checkpoint = Whisper.TranscriptionCheckpoint(
                checkpoint_path,
                sources=[source],
                model="medium",
                requested_device="cpu",
                actual_device="cpu",
                compute_type="int8",
                language="it",
                beam_size=5,
                vad_enabled=True,
                word_timestamps=False,
                output_formats=("txt",),
            )
            checkpoint.add_segment(
                segment("Prima parte.", 0.0, 2.0),
                part=1,
                source_name=source.name,
                source_start=0.0,
                source_end=2.0,
                timeline_offset=0.0,
                progress=50,
            )
            checkpoint.flush()

            fake_model = Mock()
            fake_model.transcribe.return_value = (
                [segment("Seconda parte.", 2.0, 4.0)],
                SimpleNamespace(language="it", language_probability=0.99, duration=4.0),
            )
            argv = ["Whisper.py", "--resume-checkpoint", str(checkpoint_path)]
            with patch.object(sys, "argv", argv), patch.object(
                Whisper,
                "resolve_model_path_with_progress",
                return_value="model",
            ), patch.object(
                Whisper,
                "create_model",
                return_value=(fake_model, "cpu", "int8"),
            ), patch.dict(os.environ, {"WHISPERLS_PROGRESS": "0"}), redirect_stdout(
                io.StringIO()
            ):
                Whisper.main()

            kwargs = fake_model.transcribe.call_args.kwargs
            self.assertEqual(kwargs["clip_timestamps"], [2.0])
            output = (root / "lezione_trascrizione.txt").read_text("utf-8")
            self.assertIn("Prima parte.", output)
            self.assertIn("Seconda parte.", output)
            self.assertFalse(checkpoint_path.exists())

    def test_main_applies_selected_time_interval(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "lezione.mp3"
            source.write_bytes(b"audio")
            fake_model = Mock()
            fake_model.transcribe.return_value = (
                [segment("Intervallo.", 30.0, 40.0)],
                SimpleNamespace(language="it", language_probability=0.99, duration=120.0),
            )
            argv = [
                "Whisper.py",
                str(source),
                "medium",
                "--output-format",
                "txt",
                "--clip-start",
                "00:30",
                "--clip-end",
                "01:00",
            ]
            with patch.object(sys, "argv", argv), patch.object(
                Whisper,
                "resolve_model_path_with_progress",
                return_value="model",
            ), patch.object(
                Whisper,
                "create_model",
                return_value=(fake_model, "cpu", "int8"),
            ), patch.dict(os.environ, {"WHISPERLS_PROGRESS": "0"}), redirect_stdout(
                io.StringIO()
            ):
                Whisper.main()

            self.assertEqual(
                fake_model.transcribe.call_args.kwargs["clip_timestamps"],
                [30.0, 60.0],
            )

    def test_main_resumes_multipart_after_completed_part(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = root / "parte-01.mp3"
            second = root / "parte-02.mp3"
            first.write_bytes(b"audio-1")
            second.write_bytes(b"audio-2")
            checkpoint_path = root / "parte-01_trascrizione.whisperls.json"
            checkpoint = Whisper.TranscriptionCheckpoint(
                checkpoint_path,
                sources=[first, second],
                model="medium",
                requested_device="cpu",
                actual_device="cpu",
                compute_type="int8",
                language="it",
                beam_size=5,
                vad_enabled=True,
                word_timestamps=False,
                output_formats=("txt",),
            )
            checkpoint.add_segment(
                segment("Prima traccia.", 0.0, 2.0),
                part=1,
                source_name=first.name,
                source_start=0.0,
                source_end=2.0,
                timeline_offset=0.0,
                progress=25,
            )
            checkpoint.complete_part(
                part=1,
                language="it",
                probability=0.99,
                duration=3.0,
                timeline_offset=0.0,
            )
            checkpoint.add_segment(
                segment("Seconda traccia iniziale.", 3.0, 4.0),
                part=2,
                source_name=second.name,
                source_start=0.0,
                source_end=1.0,
                timeline_offset=3.0,
                progress=75,
            )
            checkpoint.flush()

            fake_model = Mock()
            fake_model.transcribe.return_value = (
                [segment("Seconda traccia finale.", 1.0, 2.0)],
                SimpleNamespace(language="it", language_probability=0.99, duration=2.0),
            )
            argv = ["Whisper.py", "--resume-checkpoint", str(checkpoint_path)]
            with patch.object(sys, "argv", argv), patch.object(
                Whisper,
                "resolve_model_path_with_progress",
                return_value="model",
            ), patch.object(
                Whisper,
                "create_model",
                return_value=(fake_model, "cpu", "int8"),
            ), patch.dict(os.environ, {"WHISPERLS_PROGRESS": "0"}), redirect_stdout(
                io.StringIO()
            ):
                Whisper.main()

            self.assertEqual(fake_model.transcribe.call_count, 1)
            self.assertEqual(
                fake_model.transcribe.call_args.kwargs["clip_timestamps"],
                [1.0],
            )
            output = (root / "parte-01_trascrizione.txt").read_text("utf-8")
            self.assertIn("Prima traccia.", output)
            self.assertIn("Seconda traccia iniziale.", output)
            self.assertIn("Seconda traccia finale.", output)
            self.assertFalse(checkpoint_path.exists())


if __name__ == "__main__":
    unittest.main()
