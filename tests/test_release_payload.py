"""Regression coverage for the WinUI release payload."""

import tempfile
import unittest
from pathlib import Path

from tools.build_release import validate_gui_payload


class ReleasePayloadTests(unittest.TestCase):
    def test_runtime_pri_does_not_replace_the_application_pri(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            (payload / "Microsoft.UI.pri").write_bytes(b"runtime resources")
            with self.assertRaisesRegex(RuntimeError, "WhisperLS.pri"):
                validate_gui_payload(payload)

    def test_empty_application_pri_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            (payload / "WhisperLS.pri").touch()
            with self.assertRaisesRegex(RuntimeError, "missing or empty"):
                validate_gui_payload(payload)

    def test_application_pri_is_required_before_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            (payload / "WhisperLS.pri").write_bytes(b"compiled application resources")
            (payload / "Microsoft.UI.Xaml.dll").write_bytes(b"bundled WinUI")
            (payload / "Microsoft.WindowsAppRuntime.dll").write_bytes(b"bundled runtime")
            validate_gui_payload(payload)

    def test_external_windows_runtime_is_not_sufficient(self):
        with tempfile.TemporaryDirectory() as directory:
            payload = Path(directory)
            (payload / "WhisperLS.pri").write_bytes(b"resources")
            with self.assertRaisesRegex(RuntimeError, "Windows App SDK"):
                validate_gui_payload(payload)
