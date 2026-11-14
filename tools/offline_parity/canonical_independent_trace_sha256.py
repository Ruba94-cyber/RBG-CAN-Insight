"""Independent canonical CAN-frame digest (stdlib only).

Use with a separately decoded CANalyzer/Vector/python-can frame CSV,
NOT a TraceVerify --dump copy. It proves source capture identity in
combination with --actual-trace-sha256 / --reference-trace-sha256 of the
separate UDS field comparator. No frames or private payloads are printed.

Expected columns (exact order), zero-based original source frame order:
index,timestamp_ns,channel,id,extended,fd,brs,remote,error,dlc,data

The values in id/flags/dlc are decimal, and data is a hex string.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import struct
import sys

HEADER = ("index", "timestamp_ns", "channel", "id", "extended", "fd",
          "brs", "remote", "error", "dlc", "data")
FD_LENGTHS = frozenset((0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64))
SIGNED_64_MIN = -(1 << 63)
SIGNED_64_MAX = (1 << 63) - 1


def canonical_digest(input_path: Path) -> tuple[str, int]:
    digest = hashlib.sha256()
    count = 0
    with input_path.open("r", encoding="utf-8-sig", newline="") as source:
        reader = csv.DictReader(source)
        if tuple(reader.fieldnames or ()) != HEADER:
            raise ValueError("Expected strict canonical frame CSV columns")
        for line, row in enumerate(reader, start=2):
            if None in row or any(row.get(name) is None for name in HEADER):
                raise ValueError(f"Wrong source frame column count on line {line}")
            try:
                index = int(row["index"])
                timestamp = int(row["timestamp_ns"])
                channel = int(row["channel"])
                arb_id = int(row["id"])
                extended = int(row["extended"])
                fd = int(row["fd"])
                brs = int(row["brs"])
                remote = int(row["remote"])
                error = int(row["error"])
                dlc = int(row["dlc"])
                data = bytes.fromhex(row["data"])
            except (ValueError, TypeError) as exc:
                raise ValueError(f"Invalid canonical frame at line {line}") from exc
            if (index != count or channel < 0 or channel > 0x7FFFFFFF or
                timestamp < SIGNED_64_MIN or timestamp > SIGNED_64_MAX or
                any(flag not in (0, 1) for flag in (extended, fd, brs, remote, error)) or
                arb_id < 0 or arb_id > (0x1FFFFFFF if extended else 0x7FF) or
                dlc < 0 or dlc > 64 or (brs and not fd) or
                (remote and (fd or data or dlc > 8)) or
                (not remote and len(data) != dlc) or
                (fd and dlc not in FD_LENGTHS) or
                (not fd and not remote and dlc > 8)):
                raise ValueError(f"Invalid CAN identity, payload or DLC at line {line}")
            flags = extended | (remote << 1) | (error << 2) | (fd << 3) | (brs << 4)
            digest.update(struct.pack("<IIqBB", channel, arb_id, timestamp,
                                      flags, dlc))
            digest.update(data)
            count += 1
    return digest.hexdigest().upper(), count


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True,
                        help="Independent, separately decoded canonical CAN frame CSV")
    parser.add_argument("--report", type=Path, help="Optional aggregate digest JSON")
    parser.add_argument("--expected-count", type=int)
    args = parser.parse_args(argv)
    if args.report and args.report.resolve() == args.input.resolve():
        parser.error("Report must never overwrite the independent frame evidence CSV")
    try:
        sha, count = canonical_digest(args.input)
        result = {"CanonicalSha256": sha, "FrameCount": count,
                  "IndependentSourceRequired": True,
                  "description": "Normalized CAN-frame digest, not a UDS parity certificate"}
        if args.report:
            args.report.write_text(json.dumps(result, indent=2) + "\n",
                                   encoding="utf-8")
        print(json.dumps(result, indent=2))
        if count == 0 or (args.expected_count is not None and
                          args.expected_count != count):
            return 1
        return 0
    except (OSError, ValueError, csv.Error) as exc:
        # No raw payloads or local paths in the error output.
        print(f"Invalid independent frame export: {type(exc).__name__}",
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
