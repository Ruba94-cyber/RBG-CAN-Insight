"""Positive and adversarial tests for strict offline frame parity."""
import csv
from dataclasses import replace
import json
from pathlib import Path
import tempfile
import unittest

from compare_frames import CSV_FIELDS, Frame, compare, main, read_csv


def sample(index, time_ns=0):
    return Frame(index, time_ns, 2, 0x12A, 0, 0, 0, 0, 0, 2, bytes.fromhex("12AB"))


def write_csv(path, frames):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=CSV_FIELDS)
        writer.writeheader()
        for frame in frames:
            row = vars(frame).copy()
            row["data"] = frame.data.hex().upper()
            writer.writerow(row)


class ParityTests(unittest.TestCase):
    def result(self, actual, reference, **options):
        return compare(iter(actual), iter(reference),
                       tolerance_ns=options.get("tolerance_ns", 0),
                       relative=options.get("relative", True),
                       reference_channel_offset=options.get("channel_offset", 0))

    def test_identical_frames_pass(self):
        source = [sample(0, 300), sample(1, 800)]
        result = self.result(source, source)
        self.assertEqual("PASS", result["status"])
        self.assertEqual(2, result["compared_frames"])

    def test_relative_clock_and_explicit_channel_offset(self):
        actual = [sample(0, 500), sample(1, 1100)]
        reference = [replace(sample(0, 70000), channel=3),
                     replace(sample(1, 70600), channel=3)]
        self.assertEqual("PASS", self.result(actual, reference, channel_offset=1)["status"])
        self.assertEqual("FAIL", self.result(actual, reference)["status"])
        self.assertEqual("FAIL", self.result(actual, reference, channel_offset=1, relative=False)["status"])

    def test_timestamp_tolerance_boundary(self):
        actual = [sample(0, 200), sample(1, 500)]
        reference = [sample(0, 100), sample(1, 403)]
        self.assertEqual("FAIL", self.result(actual, reference, tolerance_ns=2)["status"])
        self.assertEqual("PASS", self.result(actual, reference, tolerance_ns=3)["status"])

    def test_all_identity_fields_checked(self):
        base = sample(0)
        variations = {
            "channel": replace(base, channel=3), "id": replace(base, id=0x12B),
            "extended": replace(base, extended=1), "fd": replace(base, fd=1),
            "brs": replace(base, fd=1, brs=1), "remote": replace(base, remote=1),
            "error": replace(base, error=1), "dlc": replace(base, dlc=1),
            "data": replace(base, data=b"\x12\xAC")}
        for field, altered in variations.items():
            with self.subTest(field=field):
                result = self.result([altered], [base])
                self.assertEqual("FAIL", result["status"])
                self.assertGreater(result["mismatch_fields"][field], 0)

    def test_missing_reordered_and_empty_frames_fail(self):
        self.assertEqual("FAIL", self.result([], [sample(0)])["status"])
        self.assertEqual("FAIL", self.result([], [])["status"])
        self.assertEqual("FAIL", self.result([sample(0), sample(1)], [sample(0)])["status"])
        actual = [sample(0, 200), sample(1, 100)]
        reference = [sample(0, 100), sample(1, 200)]
        self.assertEqual("FAIL", self.result(actual, reference)["status"])

    def test_csv_round_trip_and_broken_original_index(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "frames.csv"
            write_csv(path, [sample(0, 100), sample(1, 200)])
            self.assertEqual([sample(0, 100), sample(1, 200)], list(read_csv(path)))
            write_csv(path, [sample(0), sample(2)])
            with self.assertRaisesRegex(ValueError, "Nonconsecutive"):
                list(read_csv(path))

    def test_bad_flags_payload_and_identifier_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.csv"
            for frame in [replace(sample(0), brs=1), replace(sample(0), id=0xFFF),
                          replace(sample(0), data=b"\x01")]:
                write_csv(path, [frame])
                with self.assertRaises(ValueError):
                    list(read_csv(path))

    def test_cli_rejects_unsafe_report_collision_and_absolute_blf_time(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            actual, reference = root / "actual.csv", root / "reference.csv"
            write_csv(actual, [sample(0)])
            write_csv(reference, [sample(0)])
            with self.assertRaises(SystemExit) as overwritten:
                main(["--actual", str(actual), "--reference-csv", str(reference),
                      "--reference-source", "Independent", "--report", str(actual)])
            self.assertEqual(2, overwritten.exception.code)
            with self.assertRaises(SystemExit) as timing:
                main(["--actual", str(actual), "--reference-blf", str(root / "source.blf"),
                      "--reference-source", "python-can", "--absolute-timestamps"])
            self.assertEqual(2, timing.exception.code)

    def test_cli_safe_report_and_negative_control(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            actual, reference, report = (root / "actual.csv", root / "reference.csv", root / "gate.json")
            write_csv(actual, [sample(0, 100), sample(1, 200)])
            write_csv(reference, [sample(0, 100), sample(1, 200)])
            args = ["--actual", str(actual), "--reference-csv", str(reference),
                    "--reference-source", "Independent test fixture", "--report", str(report)]
            self.assertEqual(0, main(args))
            self.assertEqual("PASS", json.loads(report.read_text())["status"])
            write_csv(reference, [sample(0, 100), replace(sample(1, 200), data=b"\xFF\x00")])
            self.assertEqual(1, main(args))
            self.assertEqual(1, json.loads(report.read_text())["mismatch_fields"]["data"])
            self.assertNotIn("FF00", report.read_text())


if __name__ == "__main__":
    unittest.main()
