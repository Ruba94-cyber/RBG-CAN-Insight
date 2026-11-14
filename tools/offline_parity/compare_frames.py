"""Strict, streaming offline CAN frame parity gate; no raw payloads in the report.

Reference input must come from an independently decoded capture (e.g. CANalyzer
export normalised into TraceVerify's canonical CSV schema, or python-can BLF).
A second dump made by RBG CAN Insight is NOT independent evidence.
"""
from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
from itertools import zip_longest
import json
from pathlib import Path
import sys
from typing import Iterator

FIELDS = ("channel", "id", "extended", "fd", "brs", "remote", "error", "dlc", "data")
CSV_FIELDS = ("index", "timestamp_ns", *FIELDS)


@dataclass(frozen=True)
class Frame:
    index: int
    timestamp_ns: int
    channel: int
    id: int
    extended: int
    fd: int
    brs: int
    remote: int
    error: int
    dlc: int
    data: bytes


def read_csv(path: Path) -> Iterator[Frame]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames is None or set(reader.fieldnames) != set(CSV_FIELDS):
            raise ValueError("CSV requires exactly: " + ",".join(CSV_FIELDS))
        for index, row in enumerate(reader):
            if None in row or any(row.get(name) is None for name in CSV_FIELDS):
                raise ValueError(f"Malformed CSV record at zero-based frame {index}")
            try:
                actual_index = int(row["index"])
                timestamp_ns = int(row["timestamp_ns"])
                channel = int(row["channel"])
                identifier = int(row["id"])
                flags = [int(row[key]) for key in ("extended", "fd", "brs", "remote", "error")]
                dlc = int(row["dlc"])
                data = bytes.fromhex(row["data"])
            except (TypeError, ValueError) as exc:
                raise ValueError(f"Invalid numeric/hex record at frame {index}") from exc
            if actual_index != index:
                raise ValueError(f"Nonconsecutive original frame index at record {index}")
            if channel < 0 or identifier < 0 or identifier > (0x1FFFFFFF if flags[0] else 0x7FF):
                raise ValueError(f"Invalid CAN channel or identifier at frame {index}")
            if any(flag not in (0, 1) for flag in flags) or (flags[2] and not flags[1]):
                raise ValueError(f"Invalid CAN flags at frame {index}")
            if dlc < 0 or dlc > 64 or len(data) > 64 or (not flags[3] and dlc != len(data)):
                raise ValueError(f"Invalid DLC/payload length at frame {index}")
            if flags[3] and data:
                raise ValueError(f"Remote frame contains unexpected payload at frame {index}")
            yield Frame(index, timestamp_ns, channel, identifier, *flags, dlc, data)


def read_blf_python_can(path: Path) -> Iterator[Frame]:
    try:
        import can  # optional external reference decoder
    except ImportError as exc:
        raise ValueError("python-can not installed; install it only for --reference-blf") from exc
    with can.BLFReader(str(path)) as reader:
        first_time = None
        for index, msg in enumerate(reader):
            if first_time is None:
                first_time = msg.timestamp
            # Convert relative time to avoid Unix epoch float precision loss.
            # The tolerance stays exact (zero) unless the operator opts in.
            time_ns = round((msg.timestamp - first_time) * 1_000_000_000)
            remote = int(bool(msg.is_remote_frame))
            error = int(bool(msg.is_error_frame))
            data = bytes(msg.data) if not remote else b""
            channel = msg.channel
            if not isinstance(channel, int) or channel < 0:
                raise ValueError(f"Independent BLF decoder did not establish a channel at frame {index}")
            yield Frame(index, time_ns, channel,
                        0 if error else int(msg.arbitration_id),
                        0 if error else int(bool(msg.is_extended_id)),
                        int(bool(msg.is_fd)), int(bool(msg.bitrate_switch)),
                        remote, error, int(msg.dlc), data)


def compare(actual: Iterator[Frame], reference: Iterator[Frame], *,
            tolerance_ns: int, relative: bool, reference_channel_offset: int,
            maximum_examples: int = 25) -> dict:
    counts = {field: 0 for field in ("timestamp_ns", *FIELDS, "missing_actual", "missing_reference")}
    examples: list[dict] = []
    compared = 0
    first_actual = first_reference = None
    for index, (a, r) in enumerate(zip_longest(actual, reference)):
        if a is None or r is None:
            name = "missing_actual" if a is None else "missing_reference"
            counts[name] += 1
            if len(examples) < maximum_examples:
                examples.append({"frame_index_zero_based": index, "field": name})
            continue
        compared += 1
        if first_actual is None:
            first_actual, first_reference = a.timestamp_ns, r.timestamp_ns
        at = a.timestamp_ns - first_actual if relative else a.timestamp_ns
        rt = r.timestamp_ns - first_reference if relative else r.timestamp_ns
        difference = at - rt
        if abs(difference) > tolerance_ns:
            counts["timestamp_ns"] += 1
            if len(examples) < maximum_examples:
                examples.append({"frame_index_zero_based": index, "field": "timestamp_ns",
                                 "absolute_difference_ns": abs(difference)})
        for field in FIELDS:
            observed = getattr(a, field)
            expected = getattr(r, field)
            if field == "channel":
                expected -= reference_channel_offset
            if observed != expected:
                counts[field] += 1
                if len(examples) < maximum_examples:
                    # Never copy source IDs, raw payloads or filenames into reports.
                    examples.append({"frame_index_zero_based": index, "field": field})
    mismatches = sum(counts.values())
    return {"status": "PASS" if compared > 0 and not mismatches else "FAIL",
            "compared_frames": compared, "mismatch_fields": counts,
            "mismatch_count": mismatches, "examples": examples,
            "timestamp_basis": "relative_to_first_frame" if relative else "absolute_nanoseconds",
            "timestamp_tolerance_ns": tolerance_ns,
            "reference_channel_offset": reference_channel_offset,
            "reference_is_independent": "MUST BE VERIFIED BY OPERATOR",
            "scope": "CAN frame order/identity/flags/DLC/payload/time; NOT DBC/UDS/Vector UI parity"}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--actual", type=Path, required=True, help="RBG TraceVerify --dump CSV")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--reference-csv", type=Path, help="Independently normalised canonical CSV")
    group.add_argument("--reference-blf", type=Path, help="Independent python-can BLF decode")
    parser.add_argument("--reference-source", required=True,
                        help="Human-declared independent tool/version, e.g. CANalyzer 18 export")
    parser.add_argument("--tolerance-ns", type=int, default=0,
                        help="Timestamp absolute tolerance; default 0 (exact)")
    parser.add_argument("--absolute-timestamps", action="store_true",
                        help="Compare absolute time; default is relative to each first frame")
    parser.add_argument("--reference-channel-offset", type=int, default=0,
                        help="Explicit offset SUBTRACTED from reference physical channels")
    parser.add_argument("--report", type=Path, help="Write safe aggregate JSON report")
    args = parser.parse_args(argv)
    if args.tolerance_ns < 0 or not args.reference_source.strip():
        parser.error("Use nonnegative tolerance and nonempty reference-source")
    if args.absolute_timestamps and args.reference_blf is not None:
        parser.error("Absolute timestamps require an independent CSV; python-can BLF input uses relative time")
    reference_path = args.reference_csv if args.reference_csv is not None else args.reference_blf
    if args.report is not None and args.report.resolve() in (args.actual.resolve(), reference_path.resolve()):
        parser.error("Report path must not overwrite either evidence input")
    try:
        baseline = (read_csv(args.reference_csv) if args.reference_csv is not None
                    else read_blf_python_can(args.reference_blf))
        result = compare(read_csv(args.actual), baseline,
                         tolerance_ns=args.tolerance_ns,
                         relative=not args.absolute_timestamps,
                         reference_channel_offset=args.reference_channel_offset)
    except (OSError, ValueError, csv.Error) as error:
        print(f"INPUT ERROR: {error}", file=sys.stderr)
        return 2
    result["reference_source_declared"] = args.reference_source
    report = json.dumps(result, indent=2, sort_keys=True)
    if args.report is not None:
        try:
            args.report.write_text(report + "\n", encoding="utf-8")
        except OSError as error:
            print(f"REPORT ERROR: {error}", file=sys.stderr)
            return 2
    print(report)
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
