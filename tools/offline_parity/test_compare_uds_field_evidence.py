"""Adversarial standard-library checks for independent UDS field evidence."""
from __future__ import annotations

import csv
from dataclasses import dataclass, replace
from pathlib import Path
import tempfile
import unittest

from compare_uds_field_evidence import HEADER, compare, grouped, main, validated


@dataclass(frozen=True)
class Row:
    transaction: int = 1
    request_frame: int = 0
    response_first_frame: int = 1
    channel: int = 0
    request_id: int = 2016
    response_id: int = 2024
    extended: int = 0
    catalog_sha256: str = "A" * 64
    ecu_evidence: str = "Confirmed"
    ecu: str = "ECM"
    service_id: int = 34
    did: int = 62469
    field_path: str = "DID 0xF405/Temperature"
    bit_offset: int = 24
    bit_length: int = 8
    raw: str = "0x28"
    physical: str = "-20"
    unit: str = "degC"
    numeric_verified: int = 1
    source_spans: str = "1:4:3:1"


def write(path: Path, rows):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, lineterminator="\n")
        writer.writerow(HEADER)
        for row in rows:
            writer.writerow([getattr(row, column) for column in HEADER])


class UdsFieldEvidenceParityTests(unittest.TestCase):
    def check(self, left, right, **kwargs):
        with tempfile.TemporaryDirectory() as folder:
            a, b = Path(folder) / "rbg.csv", Path(folder) / "oracle.csv"
            write(a, left)
            write(b, right)
            return compare(grouped(a), grouped(b), **kwargs)

    def test_identical_source_fields_are_positive_nonempty_evidence(self):
        rows = [Row(), Row(transaction=2, request_frame=5,
                          response_first_frame=6, source_spans="6:4:3:1")]
        result = self.check(rows, rows)
        self.assertEqual("PASS", result["status"])
        self.assertEqual(2, result["matched_source_fields"])
        self.assertEqual("FAIL", self.check([], [])["status"])

    def test_exact_coded_raw_mismatch_fails_when_physical_matches(self):
        result = self.check([Row()], [replace(Row(), raw="0x29")])
        self.assertEqual("FAIL", result["status"])
        self.assertEqual(1, result["mismatches_by_field"]["raw"])

    def test_physical_scale_requires_explicit_absolute_tolerance(self):
        a = [Row()]
        b = [replace(Row(), physical="-20.00005")]
        self.assertEqual("FAIL", self.check(a, b)["status"])
        self.assertEqual("PASS", self.check(
            a, b, abs_tolerance=0.0001)["status"])

    def test_exact_can_source_index_or_byte_offset_mismatch_fails(self):
        a = [Row()]
        b = [replace(Row(), source_spans="2:4:3:1")]
        self.assertEqual(1, self.check(a, b)["mismatches_by_field"]["source_spans"])
        b = [replace(Row(), source_spans="1:5:3:1")]
        self.assertEqual(1, self.check(a, b)["mismatches_by_field"]["source_spans"])

    def test_missing_and_extra_fields_do_not_disappear_on_merge(self):
        additional = Row(transaction=2, request_frame=5, response_first_frame=6,
                         source_spans="6:4:3:1")
        self.assertEqual(1, self.check(
            [Row(), additional], [Row()])["mismatches_by_field"]["extra_actual"])
        self.assertEqual(1, self.check(
            [Row()], [Row(), additional])["mismatches_by_field"]["missing_actual"])

    def test_wrong_ecu_definition_did_bit_location_or_catalog_fails(self):
        r = Row()
        for modified in (
            replace(r, ecu="BCM"),
            replace(r, ecu_evidence="Unknown"),
            replace(r, catalog_sha256="B" * 64),
            replace(r, did=0xF406),
            replace(r, field_path="DID 0xF405/Other"),
            replace(r, bit_offset=25, bit_length=7)
        ):
            with self.subTest(modified=modified):
                result = self.check([r], [modified])
                self.assertEqual("FAIL", result["status"])

    def test_invalid_source_gaps_overflows_and_wrong_header_fail_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.csv"
            for damaged in (
                replace(Row(), source_spans="1:4:4:1"),
                replace(Row(), source_spans="1:64:3:1"),
                replace(Row(), source_spans="1:4:3:0"),
                replace(Row(), source_spans="garbage")
            ):
                write(path, [damaged])
                with self.assertRaises(ValueError):
                    list(validated(path))
            path.write_text("not,the,canonical,header\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                list(validated(path))

    def test_duplicate_field_or_reversed_transaction_is_invalid_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.csv"
            write(path, [Row(), Row()])
            with self.assertRaises(ValueError):
                list(validated(path))
            write(path, [replace(Row(), transaction=2), Row()])
            with self.assertRaises(ValueError):
                list(validated(path))

    def test_nonfinite_values_and_bad_catalog_fingerprint_are_invalid(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.csv"
            for damaged in (
                replace(Row(), physical="nan"),
                replace(Row(), physical="inf"),
                replace(Row(), catalog_sha256="not-a-fingerprint")
            ):
                write(path, [damaged])
                with self.assertRaises(ValueError):
                    list(validated(path))

    def test_raw_only_string_is_compared_without_invented_numeric_value(self):
        text = replace(Row(), numeric_verified=0, raw="41 42 43",
                       physical="ABC", bit_length=24, source_spans="1:4:3:3")
        self.assertEqual("PASS", self.check([text], [text])["status"])
        different = replace(text, physical="ABD")
        self.assertEqual("FAIL", self.check([text], [different])["status"])

    def test_different_original_capture_hashes_cannot_pass_identical_fields(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            a, b = [root / name for name in ("actual.csv", "reference.csv")]
            write(a, [Row()])
            write(b, [Row()])
            with self.assertRaises(SystemExit) as failure:
                main(["--actual", str(a), "--reference", str(b),
                      "--reference-source", "independent fixture",
                      "--actual-trace-sha256", "A" * 64,
                      "--reference-trace-sha256", "B" * 64])
            self.assertEqual(2, failure.exception.code)

    def test_cli_rejects_missing_or_invalid_original_trace_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            a, b = [root / name for name in ("actual.csv", "reference.csv")]
            write(a, [Row()])
            write(b, [Row()])
            with self.assertRaises(SystemExit) as missing:
                main(["--actual", str(a), "--reference", str(b),
                      "--reference-source", "fixture"])
            self.assertEqual(2, missing.exception.code)
            with self.assertRaises(SystemExit) as malformed:
                main(["--actual", str(a), "--reference", str(b),
                      "--reference-source", "fixture",
                      "--actual-trace-sha256", "not-sha",
                      "--reference-trace-sha256", "not-sha"])
            self.assertEqual(2, malformed.exception.code)

    def test_same_original_capture_digest_accepts_case_insensitive_sha256(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            a, b = [root / name for name in ("actual.csv", "reference.csv")]
            write(a, [Row()])
            write(b, [Row()])
            result = main(["--actual", str(a), "--reference", str(b),
                           "--reference-source", "independent fixture",
                           "--actual-trace-sha256", "a" * 64,
                           "--reference-trace-sha256", "A" * 64])
            self.assertEqual(0, result)

    def test_cli_privacy_safe_report_never_logs_raw_did_or_ecu_names(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            a, b, report = [root / name for name in
                            ("actual.csv", "reference.csv", "report.json")]
            write(a, [Row()])
            write(b, [replace(Row(), raw="0x29")])
            status = main(["--actual", str(a), "--reference", str(b),
                           "--reference-source", "independent synthetic fixture",
                           "--actual-trace-sha256", "A" * 64,
                           "--reference-trace-sha256", "A" * 64,
                           "--report", str(report)])
            self.assertEqual(1, status)
            contents = report.read_text(encoding="utf-8")
            self.assertIn("response_first_frame_zero_based", contents)
            for confidential in ("0x28", "0x29", "ECM", "Temperature"):
                self.assertNotIn(confidential, contents)


if __name__ == "__main__":
    unittest.main()
