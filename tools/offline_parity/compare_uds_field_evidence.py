"""Strict offline parity gate for independently decoded ISO-TP / UDS DID fields.

Compare TraceVerify --uds-field-dump with a canonical, independently created
reference (for example, Vector export normalised to this exact schema).
No Python external packages are required; the source interpretation remains
local and the JSON report contains aggregate counts and frame indices only.
"""
from __future__ import annotations

import argparse
import csv
from collections import Counter
from itertools import groupby
import json
import math
from pathlib import Path
import re
import sys

HEADER = (
    "transaction", "request_frame", "response_first_frame", "channel",
    "request_id", "response_id", "extended", "catalog_sha256",
    "ecu_evidence", "ecu", "service_id", "did", "field_path",
    "bit_offset", "bit_length", "raw", "physical", "unit",
    "numeric_verified", "source_spans"
)
INTS = (
    "transaction", "request_frame", "response_first_frame", "channel",
    "request_id", "response_id", "extended", "service_id", "did",
    "bit_offset", "bit_length", "numeric_verified"
)
SOURCE_SPAN = re.compile(r"(\d+):(\d+):(\d+):(\d+)")


def validated(path: Path):
    """Yield (transaction number, stable key, fields) without reordering source."""
    previous_transaction = 0
    seen_in_transaction = set()
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if tuple(reader.fieldnames or ()) != HEADER:
            raise ValueError("Unexpected canonical UDS evidence CSV header")
        for line_number, row in enumerate(reader, start=2):
            if None in row or any(row.get(name) is None for name in HEADER):
                raise ValueError(f"Wrong CSV field count at line {line_number}")
            try:
                data = {field: int(row[field]) for field in INTS}
            except (ValueError, TypeError) as exc:
                raise ValueError(f"Invalid numeric field at line {line_number}") from exc
            tx = data["transaction"]
            if (tx <= 0 or tx < previous_transaction or data["request_frame"] < -1
                or data["response_first_frame"] < 0 or data["channel"] < 0
                or data["extended"] not in (0, 1)
                or data["service_id"] != 0x22 or data["did"] < 0 or data["did"] > 65535
                or data["bit_offset"] < 0 or data["bit_length"] <= 0
                or data["bit_length"] > 16 * 1024 * 1024 * 8
                or data["numeric_verified"] not in (0, 1)
                or row["ecu_evidence"] not in ("Confirmed", "Inferred", "Unknown")):
                raise ValueError(f"Invalid UDS source metadata at line {line_number}")
            max_id = 0x1FFFFFFF if data["extended"] else 0x7FF
            if not (0 <= data["request_id"] <= max_id and
                    0 <= data["response_id"] <= max_id):
                raise ValueError(f"Invalid CAN arbitration identity at line {line_number}")
            if not re.fullmatch(r"[a-fA-F0-9]{64}", row["catalog_sha256"]):
                raise ValueError(f"Invalid catalog fingerprint at line {line_number}")
            spans = row["source_spans"].split(";")
            if not spans or not all(SOURCE_SPAN.fullmatch(span) for span in spans):
                raise ValueError(f"Missing or malformed CAN source spans at line {line_number}")
            next_byte = data["bit_offset"] // 8
            end_byte = (data["bit_offset"] + data["bit_length"] + 7) // 8
            previous_frame = -1
            for span in spans:
                frame, can_offset, pdu_offset, length = map(int, span.split(":"))
                if (frame < data["response_first_frame"] or frame <= previous_frame
                    or can_offset + length > 64 or length <= 0
                    or pdu_offset != next_byte):
                    raise ValueError(f"Invalid or gapped source byte range at line {line_number}")
                previous_frame = frame
                next_byte += length
            if next_byte != end_byte:
                raise ValueError(f"Source spans do not cover requested field at line {line_number}")
            if data["numeric_verified"]:
                try:
                    physical = float(row["physical"])
                except ValueError as exc:
                    raise ValueError(f"Invalid numeric physical value at line {line_number}") from exc
                if not math.isfinite(physical):
                    raise ValueError(f"Nonfinite physical value at line {line_number}")
            if tx != previous_transaction:
                seen_in_transaction.clear()
            previous_transaction = tx
            row["catalog_sha256"] = row["catalog_sha256"].upper()
            # The source frame index is part of the field identity. This is a
            # comparison of actual recorded evidence, not fuzzy name matching.
            key = tuple(row[name] for name in (
                "transaction", "response_first_frame", "channel", "request_id",
                "response_id", "extended", "catalog_sha256", "ecu_evidence",
                "ecu", "service_id", "did", "field_path", "bit_offset", "bit_length"
            ))
            if key in seen_in_transaction:
                raise ValueError(f"Duplicate source field identity at line {line_number}")
            seen_in_transaction.add(key)
            yield tx, key, row


def grouped(path: Path):
    for transaction, items in groupby(validated(path), key=lambda entry: entry[0]):
        yield transaction, {key: row for _, key, row in items}


def compare(actual, reference, *, abs_tolerance=0.0, rel_tolerance=0.0,
            max_examples=25):
    if (not math.isfinite(abs_tolerance) or not math.isfinite(rel_tolerance)
        or abs_tolerance < 0 or rel_tolerance < 0):
        raise ValueError("Tolerances must be finite and nonnegative")
    a = next(actual, None)
    r = next(reference, None)
    counts = Counter()
    examples = []
    matched = 0
    actual_rows = 0
    reference_rows = 0

    def fail(frame, field):
        counts[field] += 1
        if len(examples) < max_examples:
            examples.append({"response_first_frame_zero_based": frame, "field": field})

    while a is not None or r is not None:
        if r is None or (a is not None and a[0] < r[0]):
            actual_rows += len(a[1])
            for row in a[1].values():
                fail(int(row["response_first_frame"]), "extra_actual")
            a = next(actual, None)
            continue
        if a is None or r[0] < a[0]:
            reference_rows += len(r[1])
            for row in r[1].values():
                fail(int(row["response_first_frame"]), "missing_actual")
            r = next(reference, None)
            continue
        actual_rows += len(a[1])
        reference_rows += len(r[1])
        for key, row in a[1].items():
            expected = r[1].get(key)
            frame = int(row["response_first_frame"])
            if expected is None:
                fail(frame, "extra_actual")
                continue
            matched += 1
            for field in ("request_frame", "raw", "unit", "numeric_verified",
                          "source_spans"):
                if row[field] != expected[field]:
                    fail(frame, field)
            if row["numeric_verified"] == expected["numeric_verified"] == "1":
                if not math.isclose(
                    float(row["physical"]), float(expected["physical"]),
                    abs_tol=abs_tolerance, rel_tol=rel_tolerance
                ):
                    fail(frame, "physical")
            elif row["physical"] != expected["physical"]:
                fail(frame, "physical")
        for key in r[1].keys() - a[1].keys():
            fail(int(r[1][key]["response_first_frame"]), "missing_actual")
        a = next(actual, None)
        r = next(reference, None)

    return {
        "status": "PASS" if matched > 0 and not counts else "FAIL",
        "matched_source_fields": matched, "actual_rows": actual_rows,
        "reference_rows": reference_rows, "mismatch_count": sum(counts.values()),
        "mismatches_by_field": dict(counts), "examples": examples,
        "physical_absolute_tolerance": abs_tolerance,
        "physical_relative_tolerance": rel_tolerance,
        "claim": "Independent UDS DID field parity gate only; NOT a Vector certification"
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--actual", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--reference-source", required=True)
    parser.add_argument("--actual-trace-sha256", required=True,
                        help="Canonical CAN-frame SHA-256 reported by RBG TraceVerify")
    parser.add_argument("--reference-trace-sha256", required=True,
                        help="Independently computed SHA-256 of the same canonical CAN frames")
    parser.add_argument("--abs-tolerance", type=float, default=0.0)
    parser.add_argument("--rel-tolerance", type=float, default=0.0)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    if (not args.reference_source.strip() or
        args.actual.resolve() == args.reference.resolve()):
        parser.error("A distinct independent reference and its named source are mandatory")
    if args.report and args.report.resolve() in (
        args.actual.resolve(), args.reference.resolve()
    ):
        parser.error("A report cannot overwrite an evidence input")
    # Source frame IDs and byte ranges can match accidentally between
    # different captures. A true raw DID parity gate must also prove the
    # *entire capture identity* before comparing source-backed fields.
    hashes = (args.actual_trace_sha256, args.reference_trace_sha256)
    if any(not re.fullmatch(r"[a-fA-F0-9]{64}", item) for item in hashes):
        parser.error("Both canonical trace identities must be 64-digit SHA-256 hashes")
    if hashes[0].lower() != hashes[1].lower():
        parser.error("Independent reference is not from the same canonical CAN trace")
    try:
        result = compare(grouped(args.actual), grouped(args.reference),
                         abs_tolerance=args.abs_tolerance,
                         rel_tolerance=args.rel_tolerance)
        result["independent_reference_declared"] = args.reference_source
        result["identical_canonical_trace_sha256"] = True
        summary = json.dumps(result, indent=2, sort_keys=True)
        if args.report:
            args.report.write_text(summary + "\n", encoding="utf-8")
        print(summary)
        return 0 if result["status"] == "PASS" else 1
    except (ValueError, OSError, csv.Error) as exc:
        print(f"Input/verification error: {type(exc).__name__}: {exc}",
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
