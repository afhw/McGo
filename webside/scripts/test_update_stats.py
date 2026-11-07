import gzip
from contextlib import closing
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

import update_stats as stats


def event(second=0, ip="192.0.2.10", target="/", method="GET", status=200, agent="test-agent"):
    return f'{ip} - - [07/Nov/2026:12:00:{second:02d} +0800] "{method} {target} HTTP/1.1" {status} 123 "-" "{agent}"\n'


class StatsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.logs = self.root / "logs"
        self.logs.mkdir()
        self.log = self.logs / "access.log"
        self.output = self.root / "public" / "stats.json"
        self.state = self.root / "private" / "stats.sqlite3"
        self.pattern = str(self.logs / "access.log*")

    def write(self, text, path=None):
        (path or self.log).write_text(text, encoding="utf-8", newline="")

    def run_update(self):
        return stats.update_stats(self.pattern, self.output, self.state)

    def db_contents(self):
        with closing(sqlite3.connect(self.state)) as db:
            return list(db.iterdump())

    def assert_preserved(self, callback, exception):
        output, state = self.output.read_bytes(), self.db_contents()
        with self.assertRaises(exception):
            callback()
        self.assertEqual(self.output.read_bytes(), output)
        self.assertEqual(self.db_contents(), state)

    def test_repeated_scan_and_identical_events_keep_multiplicity(self):
        self.write(event() * 3)
        self.assertEqual(self.run_update()["page_views"], 3)
        self.assertEqual(self.run_update()["page_views"], 3)
        self.write(event() * 5)
        result = self.run_update()
        self.assertEqual(result["page_views"], 5)
        self.assertEqual(result["unique_visitors"], 1)
        self.assertEqual(self.run_update()["page_views"], 5)

    def test_identical_events_across_distinct_files_are_added(self):
        self.write(event() * 2)
        self.write(event() * 3, self.logs / "access.log.1")
        self.assertEqual(self.run_update()["page_views"], 5)
        self.assertEqual(self.run_update()["page_views"], 5)

    def test_entire_event_is_fingerprinted(self):
        self.write(event() + event(agent="another-agent") + event(target="/?source=other"))
        self.assertEqual(self.run_update()["page_views"], 3)

    def test_rotation_compression_deletion_and_truncation_keep_history(self):
        self.write(event() * 3)
        initial = self.run_update()
        rotated = self.logs / "access.log.1"
        self.log.rename(rotated)
        self.write(event(1, ip="192.0.2.11"))
        self.assertEqual(self.run_update()["page_views"], 4)
        compressed = self.logs / "access.log.1.gz"
        compressed.write_bytes(gzip.compress(rotated.read_bytes()))
        self.assertEqual(self.run_update()["page_views"], 4)
        rotated.unlink()
        self.assertEqual(self.run_update()["page_views"], 4)
        compressed.unlink()
        self.assertEqual(self.run_update()["page_views"], 4)
        self.write(event(2, ip="192.0.2.12"))
        result = self.run_update()
        self.assertEqual(result["page_views"], 5)
        self.assertEqual(result["unique_visitors"], 3)
        self.assertEqual(result["since"], initial["since"])

    def test_compressed_twin_uses_per_event_max_not_file_total(self):
        self.write(event() * 3 + event(1))
        compressed = self.logs / "access.log.gz"
        compressed.write_bytes(gzip.compress((event() * 2 + event(1) * 2).encode()))
        self.assertEqual(self.run_update()["page_views"], 5)
        self.assertEqual(self.run_update()["page_views"], 5)

    def test_copytruncate_does_not_recount_identical_snapshot(self):
        self.write(event() * 3)
        self.run_update()
        self.write(self.log.read_text(), self.logs / "access.log.1")
        self.write(event(1))
        self.assertEqual(self.run_update()["page_views"], 4)

    def test_download_categories_and_request_filters(self):
        self.write(
            event() + event(1, target="/index.html") + event(2, target="/privacy.html")
            + event(3, target="/download/McGo.exe?ref=site", status=206) * 2
            + event(4, target="/download/McGo-CSharp-Preview-x64-Setup.exe", status=302)
            + event(5, target="/download/McGo-CSharp-Preview-x64.zip")
            + event(6, method="HEAD") + event(7, method="POST")
            + event(8, status=404) + event(9, status=500) + event(10, target="/assets/a.js")
        )
        data = self.run_update()
        self.assertEqual(data["schema"], 1)
        self.assertEqual(data["page_views"], 3)
        self.assertEqual(data["downloads"], {"total": 4, "stable": 2, "preview": 1, "legacy_preview": 1})

    def test_rejected_requests_with_incomplete_request_lines_are_valid_logs(self):
        rejected = '192.0.2.10 - - [07/Nov/2026:12:00:00 +0800] "-" 400 0 "-" "-"\n'
        self.write(event() + rejected + rejected.replace('"-" 400', '"GET /" 400')
                   + event(target="http://[invalid", status=400))
        data = self.run_update()
        self.assertEqual(data["page_views"], 1)
        self.assertEqual(data["unique_visitors"], 1)

    def test_missing_empty_and_incomplete_logs_preserve_last_output(self):
        self.write(event())
        self.run_update()
        self.log.unlink()
        self.assert_preserved(self.run_update, stats.StatsError)
        self.write("")
        self.assert_preserved(self.run_update, stats.StatsError)
        self.write(event(1).rstrip("\n"))
        self.assert_preserved(self.run_update, stats.StatsError)
        self.write(event(1))
        self.assertEqual(self.run_update()["page_views"], 2)

    def test_incomplete_tail_is_retried_after_completion(self):
        self.write(event() + event(1).rstrip("\n"))
        self.assertEqual(self.run_update()["page_views"], 1)
        self.write(event() + event(1))
        self.assertEqual(self.run_update()["page_views"], 2)

    def test_partial_batch_read_failure_rolls_back_and_recovers(self):
        self.write(event())
        self.run_update()
        self.write(event() + event(1))
        bad = self.logs / "access.log.1"
        self.write(event(2), bad)
        original = stats.read_log

        def fail_second(path, key):
            if path == str(bad):
                raise PermissionError("simulated unreadable rotated log")
            return original(path, key)

        with mock.patch.object(stats, "read_log", side_effect=fail_second):
            self.assert_preserved(self.run_update, PermissionError)
        self.assertEqual(self.run_update()["page_views"], 3)
        self.assertEqual(self.run_update()["page_views"], 3)

    def test_disappearing_log_rolls_back(self):
        self.write(event())
        self.run_update()
        with mock.patch.object(stats.glob, "glob", return_value=[str(self.log), str(self.logs / "missing")]):
            self.assert_preserved(self.run_update, FileNotFoundError)

    def test_corrupt_and_truncated_gzip_roll_back_and_recover(self):
        self.write(event())
        self.run_update()
        compressed = self.logs / "access.log.1.gz"
        compressed.write_bytes(b"not gzip")
        self.assert_preserved(self.run_update, OSError)
        payload = gzip.compress(event(1).encode())
        compressed.write_bytes(payload[:-5])
        self.assert_preserved(self.run_update, EOFError)
        compressed.write_bytes(payload)
        self.assertEqual(self.run_update()["page_views"], 2)

    def test_bad_encoding_and_malformed_records_do_not_publish(self):
        self.write(event())
        self.run_update()
        self.log.write_bytes(event(1).encode() + b"\xff\n")
        self.assert_preserved(self.run_update, UnicodeError)
        self.write(event(1) + "unsupported record\n")
        self.assert_preserved(self.run_update, stats.StatsError)

    def test_log_truncation_during_read_aborts(self):
        self.write(event())
        self.run_update()
        actual = self.log.stat()
        changed = mock.Mock(st_size=0, st_mtime_ns=actual.st_mtime_ns + 1)
        with mock.patch.object(stats.os, "fstat", side_effect=[actual, changed]):
            with self.assertRaises(stats.StatsError):
                stats.read_log(str(self.log), b"key")

    def test_rotation_during_scan_rolls_back_and_recovers(self):
        self.write(event())
        self.run_update()
        original = stats.read_log

        def rotate_after_read(path, key):
            result = original(path, key)
            self.log.rename(self.logs / "access.log.1")
            self.write(event(1))
            return result

        with mock.patch.object(stats, "read_log", side_effect=rotate_after_read):
            self.assert_preserved(self.run_update, stats.StatsError)
        self.assertEqual(self.run_update()["page_views"], 2)

    def test_failed_atomic_replace_recovers_from_committed_state(self):
        self.write(event())
        self.run_update()
        before = self.output.read_bytes()
        self.write(event() + event(1))
        with mock.patch.object(stats.os, "replace", side_effect=PermissionError("output busy")):
            with self.assertRaises(PermissionError):
                self.run_update()
        self.assertEqual(self.output.read_bytes(), before)
        self.assertEqual(list(self.output.parent.glob(".*.tmp")), [])
        self.write(event(2))
        self.assertEqual(self.run_update()["page_views"], 3)
        self.assertEqual(self.run_update()["page_views"], 3)

    def test_fsync_failure_keeps_last_output_and_cleans_temporary(self):
        self.write(event())
        self.run_update()
        before = self.output.read_bytes()
        with mock.patch.object(stats.os, "fsync", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                stats.atomic_write(self.output, {"page_views": 999})
        self.assertEqual(self.output.read_bytes(), before)
        self.assertEqual(list(self.output.parent.glob(".*.tmp")), [])

    def test_existing_public_history_is_anchored_only_once(self):
        baseline = {
            "schema": 1, "page_views": 112, "unique_visitors": 54,
            "downloads": {"total": 109, "stable": 95, "preview": 10, "legacy_preview": 4},
            "since": "2026-05-31T22:00:19+08:00", "generated_at": "2026-07-18T14:39:57+00:00",
        }
        self.output.parent.mkdir()
        self.output.write_text(json.dumps(baseline))
        self.write(event() * 2 + event(1, target="/download/McGo.exe"))
        data = self.run_update()
        for key in ("page_views", "unique_visitors", "downloads", "since"):
            self.assertEqual(data[key], baseline[key])
        self.assertTrue(data["history"]["unique_visitors_is_lower_bound"])
        self.assertEqual(self.run_update()["page_views"], 112)
        self.write(event(2) + event(3, target="/download/McGo.exe"))
        data = self.run_update()
        self.assertEqual(data["page_views"], 113)
        self.assertEqual(data["downloads"]["stable"], 96)
        self.assertEqual(data["downloads"]["total"], 110)

    def test_invalid_baseline_aborts_first_run_then_recovers(self):
        self.write(event())
        self.output.parent.mkdir()
        self.output.write_text('{"schema":1}')
        with self.assertRaises(stats.StatsError):
            self.run_update()
        self.assertEqual(self.output.read_text(), '{"schema":1}')
        self.output.unlink()
        self.assertEqual(self.run_update()["page_views"], 1)

    def test_no_logs_on_first_run_does_not_create_output(self):
        with self.assertRaises(stats.StatsError):
            self.run_update()
        self.assertFalse(self.output.exists())
        self.write(event())
        self.assertEqual(self.run_update()["page_views"], 1)

    def test_private_paths_and_no_raw_identifiers(self):
        self.write(event(target="/?private-token=secret-value"))
        self.run_update()
        for payload in (self.state.read_bytes(), self.output.read_bytes()):
            self.assertNotIn(b"192.0.2.10", payload)
            self.assertNotIn(b"secret-value", payload)
            self.assertNotIn(b"test-agent", payload)
        with self.assertRaises(stats.StatsError):
            stats.update_stats(self.pattern, self.output, self.output.parent / "state.db")
        with self.assertRaises(stats.StatsError):
            stats.private_state_path(self.output.parent / "private" / "state.db", self.output.parent / "data" / "stats.json")
        self.assertNotIn(self.output.parent, stats.default_state(self.output).parents)
        if os.name != "nt":
            self.assertEqual(self.state.stat().st_mode & 0o777, 0o600)

    def test_cli_compatible_default_state_and_nonzero_failure(self):
        self.write(event())
        default = self.root / "default-private" / "stats.sqlite3"
        args = ["--log-pattern", self.pattern, "--output", str(self.output)]
        with mock.patch.object(stats, "default_state", return_value=default):
            self.assertEqual(stats.main(args), 0)
        self.assertTrue(default.exists())
        args.extend(["--state", str(default)])
        before = self.output.read_bytes()
        self.log.unlink()
        with mock.patch("sys.stderr", new_callable=io.StringIO) as error:
            self.assertEqual(stats.main(args), 1)
            self.assertIn("No logs matched", error.getvalue())
        self.assertEqual(self.output.read_bytes(), before)

    def test_concurrent_processes_cannot_publish_older_snapshot_last(self):
        self.write(event())
        marker, release = self.root / "marker", self.root / "release"
        code = """
import pathlib, sys, time
import update_stats as stats
pattern, output, state, marker, release = sys.argv[1:]
original = stats.atomic_write
def delayed_write(path, data):
    pathlib.Path(marker).touch()
    deadline = time.monotonic() + 15
    while not pathlib.Path(release).exists():
        if time.monotonic() > deadline:
            raise RuntimeError('test release timeout')
        time.sleep(0.02)
    original(path, data)
stats.atomic_write = delayed_write
stats.update_stats(pattern, output, state)
"""
        cwd = Path(stats.__file__).parent
        first = subprocess.Popen(
            [sys.executable, "-B", "-c", code, self.pattern, str(self.output), str(self.state), str(marker), str(release)],
            cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        second = None
        try:
            deadline = time.monotonic() + 10
            while not marker.exists() and first.poll() is None and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(marker.exists(), "first writer did not reach publication")
            self.write(event() + event(1))
            second = subprocess.Popen(
                [sys.executable, "-B", str(Path(stats.__file__)), "--log-pattern", self.pattern,
                 "--output", str(self.output), "--state", str(self.state)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            )
            time.sleep(0.2)
            self.assertIsNone(second.poll(), "second writer bypassed the publication lock")
            release.touch()
            for process in (first, second):
                _, error = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, error.decode())
            self.assertEqual(json.loads(self.output.read_text())["page_views"], 2)
            self.assertEqual(self.run_update()["page_views"], 2)
        finally:
            release.touch()
            for process in (first, second):
                if process is not None:
                    if process.poll() is None:
                        process.kill()
                    process.communicate()


if __name__ == "__main__":
    unittest.main()
