"""Independent offline DBC reference from original BLF, python-can and cantools.

Requires locally installed python-can + cantools; neither is bundled with
RBG CAN Insight. Produces source-linked numerical evidence, NOT a Vector
certification. Keep original files and exports on the authorised PC.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import os
from pathlib import Path
import struct
import sys
import tempfile

HEADER = (
    "frame_index", "timestamp_ns", "channel", "can_id", "extended",
    "dbc_sha256", "dbc_name", "message", "signal", "start_bit", "bit_length",
    "byte_order", "value_type", "raw_bits_hex", "raw_coded",
    "raw_exact_in_double", "physical", "unit", "named_value"
)


def safe_label(value: object) -> str:
    text = str(value) if value is not None else ""
    if text.lstrip()[:1] in ("=", "+", "-", "@", "\t", "\r"):
        return "'" + text
    return text


def exact_raw(signal, decoded):
    if signal.is_float:
        if signal.length not in (32, 64):
            raise ValueError("Unsupported floating-point DBC signal length")
        packed = struct.pack("<f" if signal.length == 32 else "<d", float(decoded))
        raw_bits = int.from_bytes(packed, byteorder="little", signed=False)
        return raw_bits, "IEEE-754 bit pattern", 0, ""
    if not isinstance(decoded, int):
        raise ValueError("Independent decoder returned a noninteger raw code")
    raw_bits = decoded & ((1 << signal.length) - 1)
    meaning = signal.choices.get(decoded) if signal.choices else None
    if meaning is not None:
        meaning = getattr(meaning, "name", meaning)
    return raw_bits, str(decoded), int(abs(decoded) <= (1 << 53) - 1), safe_label(meaning)


def generate(source_blf: Path, source_dbc: Path, writer) -> int:
    try:
        import can
        import cantools
    except ImportError as exc:
        raise ValueError("Install python-can and cantools locally for independent references") from exc

    db = cantools.database.load_file(str(source_dbc), strict=True)
    identities = {}
    for message in db.messages:
        key = (message.frame_id, bool(message.is_extended_frame))
        if key in identities:
            raise ValueError("Ambiguous DBC numeric ID and frame-format identity")
        identities[key] = message
    sha = hashlib.sha256()
    with source_dbc.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            sha.update(block)
    dbc_hash = sha.hexdigest().upper()

    csv_writer = csv.writer(writer, lineterminator="\n")
    csv_writer.writerow(HEADER)
    total = 0
    first_timestamp = None
    with can.BLFReader(str(source_blf)) as reader:
        for frame_index, frame in enumerate(reader):
            if first_timestamp is None:
                first_timestamp = frame.timestamp
            if frame.is_error_frame or frame.is_remote_frame:
                continue
            message = identities.get((frame.arbitration_id, bool(frame.is_extended_id)))
            if message is None:
                continue
            if not isinstance(frame.channel, int) or frame.channel < 0:
                raise ValueError(f"Unknown physical CAN channel on frame {frame_index}")
            payload = bytes(frame.data)
            # Independent decoder must not invent values from truncated signals.
            # A malformed message fails the gate rather than being hidden.
            try:
                coded = message.decode(payload, decode_choices=False,
                                       scaling=False, allow_truncated=False)
                physical = message.decode(payload, decode_choices=False,
                                         scaling=True, allow_truncated=False)
            except Exception as exc:
                raise ValueError(f"Independent DBC decoding failed at frame {frame_index}") from exc
            timestamp_ns = round((frame.timestamp - first_timestamp) * 1_000_000_000)
            by_name = {signal.name: signal for signal in message.signals}
            if len(by_name) != len(message.signals):
                raise ValueError("Duplicate DBC signal names cannot be resolved safely")
            for name in sorted(coded):
                if name not in by_name or name not in physical:
                    raise ValueError("Independent physical/raw decoder disagreement")
                signal = by_name[name]
                if signal.length < 1 or signal.length > 64:
                    raise ValueError("Unsupported source bit length")
                raw_bits, raw_coded, exact, meaning = exact_raw(signal, coded[name])
                physical_value = float(physical[name])
                if not __import__("math").isfinite(physical_value):
                    raise ValueError("Nonfinite physical value must not be treated as observation")
                csv_writer.writerow((
                    frame_index, timestamp_ns, frame.channel, frame.arbitration_id,
                    int(bool(frame.is_extended_id)), dbc_hash, safe_label(source_dbc.name),
                    safe_label(message.name), safe_label(name), signal.start,
                    signal.length, "Intel" if signal.byte_order == "little_endian" else "Motorola",
                    ("Float32" if signal.length == 32 else "Float64")
                    if signal.is_float else "Integer",
                    f"0x{raw_bits:0{(signal.length + 3) // 4}X}",
                    safe_label(raw_coded), exact, format(physical_value, ".17g"),
                    safe_label(signal.unit), meaning
                ))
                total += 1
    return total


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blf", required=True, type=Path, help="Original independent BLF")
    parser.add_argument("--dbc", required=True, type=Path, help="Exact DBC used by RBG")
    parser.add_argument("--output", required=True, type=Path, help="Local canonical reference CSV")
    args = parser.parse_args(argv)
    if args.output.resolve() in (args.blf.resolve(), args.dbc.resolve()):
        parser.error("Never overwrite the original BLF or DBC")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="", delete=False,
            dir=args.output.parent, prefix=".dbc-oracle-", suffix=".tmp"
        ) as stream:
            temporary = Path(stream.name)
            count = generate(args.blf, args.dbc, stream)
        if count == 0:
            raise ValueError("No independently decoded signals: reference not valid")
        os.replace(temporary, args.output)
        print(f"Reference created: {count} source-linked signal observations "
              "(python-can + cantools; NOT Vector-certified).")
        return 0
    except (ValueError, OSError) as exc:
        print(f"Reference gate failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    raise SystemExit(main())
