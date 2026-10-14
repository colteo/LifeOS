import contextlib
import io
import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from dataset import generate, validate
from fake_api import CONFIG, FakeApi
from qa001 import main


class CliTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.config = self.directory / "config.json"
        fsync = patch("importer.os.fsync")
        fsync.start()
        self.addCleanup(fsync.stop)
        self.config.write_text(json.dumps(CONFIG))

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = main([str(a) for a in args])
        return code, out.getvalue(), err.getvalue()

    def test_dry_run_is_default_and_never_constructs_transport(self):
        output = self.directory / "manifest.json"
        with patch("qa001.Api", side_effect=AssertionError("network")):
            code, out, _ = self.run_cli("--output", output)
        self.assertEqual(code, 0)
        preview = json.loads(out)
        self.assertEqual((preview["mode"], preview["networkCalls"]), ("dry-run", 0))
        self.assertEqual(preview["gym"]["status"], "not_importable_via_public_api")
        validate(json.loads(output.read_text(encoding="utf-8")))

    def test_same_seed_and_reference_produce_identical_files(self):
        a, b = self.directory / "a.json", self.directory / "b.json"
        self.run_cli("--output", a, "--seed", 7, "--reference-date", "2026-05-01")
        self.run_cli("--output", b, "--seed", 7, "--reference-date", "2026-05-01")
        self.assertEqual(a.read_bytes(), b.read_bytes())

    def test_existing_manifest_is_validated(self):
        tampered = generate()
        tampered["records"]["journal"][0]["payload"]["content"] = "changed"
        path = self.directory / "tampered.json"
        path.write_text(json.dumps(tampered))
        code, _, err = self.run_cli("--manifest", path, "--output", self.directory / "out.json")
        self.assertEqual(code, 1)
        self.assertIn("checksum", err)

    def test_apply_guards_fail_before_network(self):
        template = self.directory / "approval.json"
        with patch("qa001.Api", side_effect=AssertionError("network")):
            self.assertEqual(self.run_cli("--apply")[0], 1)
            self.assertEqual(self.run_cli("--apply", "--config", self.config)[0], 1)
            code, _, _ = self.run_cli("--config", self.config, "--approval-template", template, "--output", self.directory / "m.json")
            self.assertEqual(code, 0)
            approval = json.loads(template.read_text())
            self.assertEqual((approval["approved"], approval["dedicatedTestAccount"], approval["expiresAtUtc"]), (False, False, None))
            code, _, err = self.run_cli("--apply", "--config", self.config, "--approval", template)
            self.assertEqual(code, 1)
            self.assertIn("approval", err.lower())

    def test_apply_reports_and_never_prints_token_or_server_data(self):
        approval = self.directory / "approval.json"
        self.run_cli("--config", self.config, "--approval-template", approval, "--output", self.directory / "m.json")
        value = json.loads(approval.read_text())
        value.update(approved=True, dedicatedTestAccount=True,
                     expiresAtUtc=(datetime.now(timezone.utc) + timedelta(hours=1)).isoformat())
        approval.write_text(json.dumps(value))
        fake = FakeApi()
        fake.fail_at, fake.status = 5, 401
        state, report = self.directory / "state.json", self.directory / "report.json"
        args = ("--apply", "--config", self.config, "--approval", approval, "--state", state, "--report", report)
        with patch.dict(os.environ, {"LIFEOS_QA_TOKEN": "synthetic-secret-token"}), patch("qa001.Api", return_value=fake):
            code, out, err = self.run_cli(*args)
            self.assertEqual(code, 1)
            self.assertIn("HTTP 401", err)
            self.assertIn("token", err)
            partial = json.loads(report.read_text())
            self.assertEqual((partial["outcome"], partial["failure"]["httpStatus"]), ("failed", 401))
            fake.fail_at = None
            code, out, err = self.run_cli(*args)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(report.read_text())["outcome"], "complete")
        for text in (out, err, report.read_text(), state.read_text()):
            self.assertNotIn("synthetic-secret-token", text)
            self.assertNotIn("qa@example.invalid", text)

    def test_whoami_is_read_only_and_hides_email(self):
        fake = FakeApi()
        with patch.dict(os.environ, {"LIFEOS_QA_TOKEN": "t"}), patch("qa001.Api", return_value=fake):
            code, out, _ = self.run_cli("--whoami", "--config", self.config)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["userId"], CONFIG["userId"])
        self.assertNotIn("example.invalid", out)
        self.assertTrue(all(method == "GET" for method, _ in fake.calls))

    def test_remote_origin_requires_flag(self):
        remote = self.directory / "remote.json"
        remote.write_text(json.dumps({**CONFIG, "baseUrl": "https://qa.example.com"}))
        args = ("--config", remote, "--approval-template", self.directory / "a.json", "--output", self.directory / "m.json")
        self.assertEqual(self.run_cli(*args)[0], 1)
        self.assertEqual(self.run_cli(*args, "--allow-remote")[0], 0)


if __name__ == "__main__":
    unittest.main()
