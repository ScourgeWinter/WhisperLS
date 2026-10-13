import tempfile
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import Whisper


class ReliabilityTests(unittest.TestCase):
    def test_checkpoint_is_not_assigned_to_other_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            a, b = root / "a.mp3", root / "b.mp3"
            a.touch()
            b.touch()
            checkpoint = Whisper.TranscriptionCheckpoint(
                root / "job.whisperls.json", sources=[a], model="tiny",
                requested_device="cpu", actual_device="cpu", compute_type="int8",
                language="it", beam_size=5, vad_enabled=True, word_timestamps=False,
                output_formats=["txt"])
            checkpoint.flush()
            with self.assertRaisesRegex(ValueError, "sorgente diversi"):
                Whisper._manifest_job_arguments({
                    "sources": [str(b)], "resumeCheckpoint": str(checkpoint.path)})
            args = Whisper._manifest_job_arguments({
                "sources": [str(a)], "resumeCheckpoint": str(checkpoint.path)})
            self.assertIn("--resume-checkpoint", args)

    def test_auto_rename_preserves_interrupted_checkpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "job.whisperls.json").touch()
            self.assertEqual(Whisper.choose_available_output_name(root, "job", ["txt"]), "job (2)")

    def test_output_created_during_publication_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "job.txt"
            rename = Whisper.os.rename if Whisper.os.name == "nt" else Whisper.os.link
            def concurrent_writer(source, destination):
                output.write_text("OTHER", encoding="utf8")
                return rename(source, destination)
            publisher = "rename" if Whisper.os.name == "nt" else "link"
            with patch.object(Whisper.os, publisher, side_effect=concurrent_writer):
                with self.assertRaises(FileExistsError):
                    Whisper.write_transcription_outputs(root / "a.mp3",
                        [SimpleNamespace(text="NEW", start=0, end=1)],
                        output_name="job", output_formats=["txt"])
            self.assertEqual(output.read_text("utf8"), "OTHER")

    def test_failed_second_format_restores_overwritten_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for kind in ("txt", "srt"):
                (root / f"job.{kind}").write_text("OLD " + kind, encoding="utf8")
            replace = Whisper.os.replace
            failed = False
            def fail_second(source, destination):
                nonlocal failed
                if Path(destination).suffix == ".srt" and not failed:
                    failed = True
                    raise PermissionError("simulated locked output")
                return replace(source, destination)
            with patch.object(Whisper.os, "replace", side_effect=fail_second):
                with self.assertRaises(PermissionError):
                    Whisper.write_transcription_outputs(root / "a.mp3",
                        [SimpleNamespace(text="NEW", start=0, end=1)], output_name="job",
                        output_formats=["txt", "srt"], overwrite=True)
            for kind in ("txt", "srt"):
                self.assertEqual((root / f"job.{kind}").read_text("utf8"), "OLD " + kind)

    def test_competing_output_locks_are_rejected_and_released(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with Whisper.output_lock(root, "job"):
                with self.assertRaisesRegex(RuntimeError, "altra istanza"):
                    with Whisper.output_lock(root, "job"):
                        self.fail("lock acquired twice")
            with Whisper.output_lock(root, "job"):
                pass

    def test_amd_cpu_configuration_uses_cpu_without_cuda(self):
        self.assertEqual(Whisper.build_model_candidates("cpu"), [("cpu", "int8"), ("cpu", "float32")])

    def test_output_lock_excludes_another_process(self):
        code = (
            "import sys; from pathlib import Path; "
            "sys.path.insert(0, sys.argv[1]); import Whisper; "
            "lock = Whisper.output_lock(Path(sys.argv[2]), 'job'); "
            "lock.__enter__(); lock.__exit__(None, None, None)"
        )
        with tempfile.TemporaryDirectory() as directory:
            command = [sys.executable, "-c", code, str(Path(__file__).resolve().parents[1]), directory]
            with Whisper.output_lock(Path(directory), "job"):
                result = subprocess.run(command, capture_output=True, timeout=15)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(b"RuntimeError", result.stderr)
            result = subprocess.run(command, capture_output=True, timeout=15)
            self.assertEqual(result.returncode, 0, result.stderr)
