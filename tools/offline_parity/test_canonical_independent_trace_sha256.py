"""Adversarial tests for the independent canonical whole-capture hash."""
import csv
import hashlib
from pathlib import Path
import struct
import tempfile
import unittest

from canonical_independent_trace_sha256 import HEADER, canonical_digest, main


def frame(**changes):
    item = {
        "index": 0, "timestamp_ns": 100, "channel": 0, "id": 2024,
        "extended": 0, "fd": 0, "brs": 0, "remote": 0,
        "error": 0, "dlc": 4, "data": "04 62 F4 05"
    }
    item.update(changes)
    return item


def write(path, rows):
    with path.open("w", encoding="utf-8", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=HEADER)
        writer.writeheader()
        writer.writerows(rows)


class CanonicalFrameDigestTests(unittest.TestCase):
    def test_exact_binary_header_and_payload_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            csv_path = Path(folder) / "independent.csv"
            row = frame()
            write(csv_path, [row])
            expected = hashlib.sha256(
                struct.pack("<IIqBB", 0, 0x7E8, 100, 0, 4) +
                bytes.fromhex("04 62 F4 05")
            ).hexdigest().upper()
            actual, count = canonical_digest(csv_path)
            self.assertEqual(expected, actual)
            self.assertEqual(1, count)

    def test_remote_dlc_is_hash_metadata_and_not_invented_payload(self):
        with tempfile.TemporaryDirectory() as folder:
            csv_path = Path(folder) / "remote.csv"
            write(csv_path, [frame(remote=1, dlc=8, data="")])
            expected = hashlib.sha256(
                struct.pack("<IIqBB", 0, 0x7E8, 100, 2, 8)
            ).hexdigest().upper()
            self.assertEqual((expected, 1), canonical_digest(csv_path))

    def test_extended_can_fd_brs_bits_and_64_byte_payload(self):
        with tempfile.TemporaryDirectory() as folder:
            csv_path = Path(folder) / "fd.csv"
            payload = bytes(range(64))
            write(csv_path, [frame(id=0x18DAF110, extended=1, fd=1, brs=1,
                                   dlc=64, data=payload.hex())])
            flags = 1 | 8 | 16
            expected = hashlib.sha256(
                struct.pack("<IIqBB", 0, 0x18DAF110, 100, flags, 64) +
                payload
            ).hexdigest().upper()
            self.assertEqual(expected, canonical_digest(csv_path)[0])

    def test_wrong_order_dlc_identifier_and_incomplete_row_fail(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "source.csv"
            for broken in (
                [frame(index=1)],
                [frame(), frame(index=0)],
                [frame(dlc=5)],
                [frame(id=0x800)],
                [frame(fd=0, brs=1)],
                [frame(fd=1, dlc=10, data="00" * 10)],
                [frame(remote=1, data="11223344")],
            ):
                with self.subTest(case=broken):
                    write(path, broken)
                    with self.assertRaises(ValueError):
                        canonical_digest(path)

    def test_two_different_frame_streams_never_share_reference_hash(self):
        with tempfile.TemporaryDirectory() as folder:
            first, second = (Path(folder) / name for name in
                             ("a.csv", "b.csv"))
            write(first, [frame()])
            write(second, [frame(timestamp_ns=101)])
            self.assertNotEqual(canonical_digest(first)[0],
                                canonical_digest(second)[0])

    def test_cli_report_is_local_aggregate_and_input_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, report = root / "source.csv", root / "digest.json"
            write(source, [frame()])
            before = source.read_bytes()
            self.assertEqual(0, main([
                "--input", str(source), "--report", str(report),
                "--expected-count", "1"
            ]))
            self.assertEqual(before, source.read_bytes())
            result = report.read_text(encoding="utf-8")
            self.assertIn("CanonicalSha256", result)
            self.assertNotIn("0462F405", result)
            self.assertEqual(1, main([
                "--input", str(source), "--expected-count", "2"
            ]))


if __name__ == "__main__":
    unittest.main()
