"""Strict, bounded-memory DBC signal parity checker (standard library only).

Inputs must refer to the SAME original capture and exact DBC. The reference
must be independently decoded (e.g. make_independent_dbc_reference.py), not
a second RBG dump. No source names, payloads, raw codes or paths are logged.
"""
from __future__ import annotations

import argparse
from collections import Counter
import csv
from dataclasses import dataclass
from itertools import groupby
import json
import math
from pathlib import Path
import re
import sys
from typing import Iterator

from make_independent_dbc_reference import HEADER


@dataclass(frozen=True)
class SignalRow:
    frame: int
    timestamp_ns: int
    channel: int
    can_id: int
    extended: int
    dbc_sha256: str
    dbc_name: str
    message: str
    signal: str
    start_bit: int
    bit_length: int
    byte_order: str
    value_type: str
    raw_bits_hex: str
    raw_coded: str
    raw_exact_in_double: int
    physical: float
    unit: str
    named_value: str

    @property
    def identity(self) -> tuple:
        return (self.channel, self.can_id, self.extended, self.dbc_sha256,
                self.dbc_name, self.message, self.signal)


def load_rows(path: Path) -> Iterator[SignalRow]:
    with path.open("r", encoding="utf-8-sig", newline="") as source:
        reader = csv.DictReader(source)
        if reader.fieldnames is None or tuple(reader.fieldnames) != HEADER:
            raise ValueError("Incorrect canonical DBC signal header")
        last_frame = -1
        seen = set()
        for line, row in enumerate(reader, start=2):
            if None in row or any(row.get(field) is None for field in HEADER):
                raise ValueError(f"Malformed field count in canonical row {line}")
            try:
                frame = int(row["frame_index"])
                timestamp = int(row["timestamp_ns"])
                channel = int(row["channel"])
                can_id = int(row["can_id"])
                extended = int(row["extended"])
                start_bit = int(row["start_bit"])
                bit_length = int(row["bit_length"])
                exact = int(row["raw_exact_in_double"])
                physical = float(row["physical"])
            except (ValueError, TypeError) as exc:
                raise ValueError(f"Invalid canonical numeric field in CSV row {line}") from exc
            if (frame < last_frame or frame < 0 or channel < 0 or extended not in (0, 1)
                or can_id < 0 or can_id > (0x1FFFFFFF if extended else 0x7FF)
                or start_bit < 0 or bit_length < 1 or bit_length > 64
                or exact not in (0, 1) or not math.isfinite(physical)):
                raise ValueError(f"Invalid signal evidence at row {line}")
            if (row["byte_order"] not in ("Intel", "Motorola") or
                row["value_type"] not in ("Integer", "Float32", "Float64")):
                raise ValueError(f"Unsupported encoding label at row {line}")
            sha = row["dbc_sha256"]
            if not re.fullmatch("[0-9A-Fa-f]{64}", sha):
                raise ValueError(f"Invalid DBC fingerprint at row {line}")
            raw_hex = row["raw_bits_hex"]
            digits = (bit_length + 3) // 4
            if not re.fullmatch(r"0x[0-9A-Fa-f]{" + str(digits) + "}", raw_hex) or int(raw_hex[2:], 16) >= (1 << bit_length):
                raise ValueError(f"Invalid exact raw bit pattern at row {line}")
            record = SignalRow(frame, timestamp, channel, can_id, extended,
                               sha.upper(), row["dbc_name"], row["message"],
                               row["signal"], start_bit, bit_length, row["byte_order"],
                               row["value_type"], "0x" + raw_hex[2:].upper(),
                               row["raw_coded"], exact, physical,
                               row["unit"], row["named_value"])
            if frame != last_frame:
                seen.clear()
            if record.identity in seen:
                raise ValueError(f"Duplicate source signal identity at row {line}")
            seen.add(record.identity)
            last_frame = frame
            yield record


def grouped(path: Path) -> Iterator[tuple[int, dict[tuple, SignalRow]]]:
    for frame, entries in groupby(load_rows(path), key=lambda entry: entry.frame):
        rows = [*entries]
        # Rows on a given frame MUST describe one physical recorded CAN object.
        first = rows[0]
        if any((row.timestamp_ns, row.channel, row.can_id, row.extended) !=
               (first.timestamp_ns, first.channel, first.can_id, first.extended)
               for row in rows[1:]):
            raise ValueError(f"Contradictory source frame metadata at index {frame}")
        yield frame, {row.identity: row for row in rows}


def compare(actual: Iterator[tuple[int, dict]], reference: Iterator[tuple[int, dict]], *,
            time_tolerance_ns: int = 0, physical_abs_tolerance: float = 0,
            physical_rel_tolerance: float = 0, reference_channel_offset: int = 0,
            max_examples: int = 25) -> dict:
    if (time_tolerance_ns < 0 or physical_abs_tolerance < 0 or physical_rel_tolerance < 0
        or not math.isfinite(physical_abs_tolerance) or not math.isfinite(physical_rel_tolerance)):
        raise ValueError("Tolerances must be finite and nonnegative")
    counts: Counter = Counter()
    examples: list[dict] = []
    matching = 0
    actual_count = 0
    reference_count = 0
    source_a = source_r = None

    def failure(frame: int, field: str):
        counts[field] += 1
        if len(examples) < max_examples:
            examples.append({"frame_index_zero_based": frame, "field": field})

    def next_or_none(it):
        return next(it, None)

    a = next_or_none(actual)
    r = next_or_none(reference)
    while a is not None or r is not None:
        if r is None or (a is not None and a[0] < r[0]):
            actual_count += len(a[1])
            for _ in a[1]:
                failure(a[0], "extra_actual")
            a = next_or_none(actual)
            continue
        if a is None or r[0] < a[0]:
            reference_count += len(r[1])
            for _ in r[1]:
                failure(r[0], "missing_actual")
            r = next_or_none(reference)
            continue
        frame = a[0]
        actual_count += len(a[1])
        reference_count += len(r[1])
        if source_a is None:
            source_a = next(iter(a[1].values())).timestamp_ns
            source_r = next(iter(r[1].values())).timestamp_ns
        # Physical bus offset is an explicit operator input, never guessed.
        normalized_reference = {
            (key[0] - reference_channel_offset, *key[1:]): row
            for key, row in r[1].items()
        }
        if len(normalized_reference) != len(r[1]):
            raise ValueError("Channel offset creates ambiguous source identity")
        for key, observed in a[1].items():
            expected = normalized_reference.get(key)
            if expected is None:
                failure(frame, "extra_actual")
                continue
            matching += 1
            if abs((observed.timestamp_ns - source_a) -
                   (expected.timestamp_ns - source_r)) > time_tolerance_ns:
                failure(frame, "timestamp_ns")
            for field in ("start_bit", "bit_length", "byte_order", "value_type",
                          "raw_bits_hex", "raw_coded", "raw_exact_in_double",
                          "unit", "named_value"):
                if getattr(observed, field) != getattr(expected, field):
                    failure(frame, field)
            if not math.isclose(observed.physical, expected.physical,
                                abs_tol=physical_abs_tolerance,
                                rel_tol=physical_rel_tolerance):
                failure(frame, "physical")
        for key in normalized_reference.keys() - a[1].keys():
            failure(frame, "missing_actual")
        a = next_or_none(actual)
        r = next_or_none(reference)

    return {
        "status": "PASS" if matching > 0 and not counts else "FAIL",
        "matched_source_signals": matching, "actual_rows": actual_count,
        "reference_rows": reference_count,
        "mismatch_count": sum(counts.values()), "mismatches_by_field": dict(counts),
        "examples": examples,
        "time_basis": "relative_to_first_common_source_signal",
        "timestamp_tolerance_ns": time_tolerance_ns,
        "physical_absolute_tolerance": physical_abs_tolerance,
        "physical_relative_tolerance": physical_rel_tolerance,
        "reference_channel_offset": reference_channel_offset,
        "claim": "Raw DBC signal parity only; NOT UDS/Vector certification"
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--actual", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--reference-source", required=True)
    parser.add_argument("--time-tolerance-ns", type=int, default=0)
    parser.add_argument("--abs-tolerance", type=float, default=0)
    parser.add_argument("--rel-tolerance", type=float, default=0)
    parser.add_argument("--reference-channel-offset", type=int, default=0)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    if not args.reference_source.strip() or args.actual.resolve() == args.reference.resolve():
        parser.error("Use a distinct independently decoded reference and name its source")
    if args.report is not None and args.report.resolve() in (args.actual.resolve(), args.reference.resolve()):
        parser.error("Audit report must not overwrite either evidence source")
    try:
        result = compare(grouped(args.actual), grouped(args.reference),
                         time_tolerance_ns=args.time_tolerance_ns,
                         physical_abs_tolerance=args.abs_tolerance,
                         physical_rel_tolerance=args.rel_tolerance,
                         reference_channel_offset=args.reference_channel_offset)
        result["independent_reference_declared"] = args.reference_source
        summary = json.dumps(result, indent=2, sort_keys=True)
        if args.report is not None:
            args.report.write_text(summary + "\n", encoding="utf-8")
        print(summary)
        return 0 if result["status"] == "PASS" else 1
    except (ValueError, OSError, csv.Error) as exc:
        print(f"Input/verification error: {type(exc).__name__}: {exc}",
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
