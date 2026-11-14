"""Adversarial, dependency-free tests for independent DBC signal parity."""
import csv
from dataclasses import replace
from pathlib import Path
import tempfile
import unittest

from compare_signal_evidence import SignalRow, compare, grouped, main, load_rows
from make_independent_dbc_reference import HEADER


SHA = "A" * 64
OTHER = "B" * 64


def row(frame=0, *, signal="Speed", timestamp=100, channel=0,
        raw="0x12", physical=18.0):
    return SignalRow(frame, timestamp, channel, 256, 0, SHA,
                     "source.dbc", "Engine", signal, 0, 8, "Intel",
                     "Integer", raw, str(int(raw, 16)), 1,
                     physical, "rpm", "")


def groups(*rows):
    result = []
    for frame in sorted({r.frame for r in rows}):
        selected = [r for r in rows if r.frame == frame]
        result.append((frame, {r.identity: r for r in selected}))
    return iter(result)


def write(path, rows):
    with path.open("w", encoding="utf-8", newline="") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(HEADER)
        for value in rows:
            writer.writerow((
                value.frame, value.timestamp_ns, value.channel,
                value.can_id, value.extended, value.dbc_sha256,
                value.dbc_name, value.message, value.signal,
                value.start_bit, value.bit_length, value.byte_order,
                value.value_type, value.raw_bits_hex, value.raw_coded,
                value.raw_exact_in_double, value.physical,
                value.unit, value.named_value
            ))


class IndependentSignalComparisonTests(unittest.TestCase):
    def result(self, actual, reference, **args):
        return compare(groups(*actual), groups(*reference), **args)

    def test_exact_source_and_values_pass(self):
        actual = [row(0), row(0, signal="Temperature", raw="0x20"),
                  row(5, timestamp=500)]
        result = self.result(actual, list(reversed(actual)))
        self.assertEqual("PASS", result["status"])
        self.assertEqual(3, result["matched_source_signals"])

    def test_independent_raw_bits_mismatch_fails_even_if_physical_is_same(self):
        outcome = self.result([row(0, raw="0x12", physical=18)],
                              [row(0, raw="0x13", physical=18)])
        self.assertEqual(1, outcome["mismatches_by_field"]["raw_bits_hex"])

    def test_physical_mismatch_and_explicit_tolerance(self):
        actual, reference = [row(0, physical=17.99)], [row(0, physical=18.0)]
        self.assertEqual("FAIL", self.result(actual, reference)["status"])
        self.assertEqual("PASS", self.result(actual, reference,
                         physical_abs_tolerance=0.02)["status"])

    def test_lost_signal_or_frame_never_disappears(self):
        self.assertEqual("FAIL", self.result([], [])["status"])
        output = self.result([row(0), row(4, timestamp=800)], [row(0)])
        self.assertEqual(1, output["mismatches_by_field"]["extra_actual"])
        output = self.result([row(0)], [row(0), row(4, timestamp=800)])
        self.assertEqual(1, output["mismatches_by_field"]["missing_actual"])

    def test_source_database_channel_and_can_format_are_identity(self):
        original = row(0)
        for changed in [replace(original, dbc_sha256=OTHER),
                        replace(original, channel=3),
                        replace(original, extended=1),
                        replace(original, can_id=257),
                        replace(original, signal="Pressure")]:
            with self.subTest(changed=changed):
                self.assertEqual("FAIL", self.result([original], [changed])["status"])
        shifted = replace(original, channel=1)
        self.assertEqual("PASS", self.result([original], [shifted],
                         reference_channel_offset=1)["status"])

    def test_signed_and_64bit_bits_are_exact(self):
        lhs = replace(row(), bit_length=64, raw_bits_hex="0xFFFFFFFFFFFFFFFF",
                      raw_coded="-1", raw_exact_in_double=1)
        rhs = replace(lhs, raw_bits_hex="0xFFFFFFFFFFFFFFFE")
        self.assertEqual("FAIL", self.result([lhs], [rhs])["status"])

    def test_timestamps_use_actual_recorded_frame_and_explicit_tolerance(self):
        lhs = [row(0, timestamp=100), row(1, timestamp=200)]
        rhs = [row(0, timestamp=9999), row(1, timestamp=10100)]
        self.assertEqual("FAIL", self.result(lhs, rhs)["status"])
        self.assertEqual("PASS", self.result(lhs, rhs,
                         time_tolerance_ns=1)["status"])

    def test_csv_rejects_duplicates_and_out_of_order(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.csv"
            write(path, [row(2), row(1)])
            with self.assertRaises(ValueError):
                list(load_rows(path))
            write(path, [row(0), row(0)])
            with self.assertRaises(ValueError):
                list(load_rows(path))

    def test_csv_rejects_wrong_raw_width_nonfinite_and_bad_header(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.csv"
            for invalid in [replace(row(), raw_bits_hex="0x999"),
                            replace(row(), raw_bits_hex="0xFF", bit_length=3),
                            replace(row(), physical=float("nan"))]:
                write(path, [invalid])
                with self.assertRaises(ValueError):
                    list(load_rows(path))
            path.write_text("not,a,signal,header\n")
            with self.assertRaises(ValueError):
                list(load_rows(path))

    def test_repeated_frame_disagrees_on_source_timestamp(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.csv"
            write(path, [row(0), row(0, signal="Temp", timestamp=500)])
            with self.assertRaises(ValueError):
                list(grouped(path))

    def test_cli_reports_only_aggregate_discrepancies(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            actual, reference, report = [base / x for x in ("actual.csv", "ref.csv", "report.json")]
            write(actual, [row()])
            write(reference, [replace(row(), raw_bits_hex="0x13")])
            result = main(["--actual", str(actual), "--reference", str(reference),
                           "--reference-source", "independent synthetic fixture",
                           "--report", str(report)])
            self.assertEqual(1, result)
            contents = report.read_text()
            self.assertIn("raw_bits_hex", contents)
            self.assertNotIn("0x12", contents)
            self.assertNotIn("0x13", contents)


if __name__ == "__main__":
    unittest.main()
