"""Independent BLF interoperability check against python-can (not Vector software).

Generates BLF files with python-can's writer, decodes them with RBG CAN Insight
(tools/RbgCanInsight.TraceVerify --dump) and compares every frame; then re-exports the
application's decode with the application's BLF writer and reads that file back with
python-can. Everything stays local; payloads are synthetic.

    python -m venv venv && venv\\Scripts\\pip install python-can
    venv\\Scripts\\python tools\\blf-interop\\blf_interop_check.py <TraceVerify.dll> [--frames 3000]
"""
import argparse
import csv
import os
import random
import subprocess
import sys
import tempfile

import can


def synthetic_messages(count: int, seed: int = 7):
    rng = random.Random(seed)
    messages = []
    t = 0.5
    for i in range(count):
        t += rng.uniform(0.0001, 0.02)
        kind = i % 10
        channel = rng.choice([0, 1, 2])
        if kind == 0:   # extended id
            msg = can.Message(arbitration_id=rng.randrange(0x800, 0x1FFFFFFF), is_extended_id=True,
                              data=bytes(rng.randrange(256) for _ in range(rng.randint(0, 8))))
        elif kind == 1:  # remote
            msg = can.Message(arbitration_id=rng.randrange(0x7FF), is_extended_id=False,
                              is_remote_frame=True, dlc=rng.randint(0, 8))
        elif kind == 2:  # CAN FD, BRS
            msg = can.Message(arbitration_id=rng.randrange(0x7FF), is_fd=True, bitrate_switch=True,
                              data=bytes(rng.randrange(256) for _ in range(rng.choice([12, 16, 20, 24, 32, 48, 64]))))
        elif kind == 3:  # CAN FD, no BRS, extended
            msg = can.Message(arbitration_id=rng.randrange(0x800, 0x1FFFFFFF), is_extended_id=True, is_fd=True,
                              data=bytes(rng.randrange(256) for _ in range(rng.choice([9, 15, 63, 64]))))
        elif kind == 4 and i % 40 == 4:  # error frame (sparse)
            msg = can.Message(is_error_frame=True, arbitration_id=0, data=b"")
        else:
            msg = can.Message(arbitration_id=rng.randrange(0x7FF), is_extended_id=False,
                              data=bytes(rng.randrange(256) for _ in range(rng.randint(0, 8))))
        msg.timestamp = t
        msg.channel = channel
        messages.append(msg)
    return messages


def write_blf(path, messages, compression_level):
    writer = can.BLFWriter(path, channel=1, compression_level=compression_level)
    for msg in messages:
        writer.on_message_received(msg)
    writer.stop()


def read_blf(path):
    return list(can.BLFReader(path))


def key(msg, base_time):
    return {
        "ts_us": round((msg.timestamp - base_time) * 1e6),
        "channel": msg.channel,
        "id": 0 if msg.is_error_frame else msg.arbitration_id,
        "ext": int(bool(msg.is_extended_id)) if not msg.is_error_frame else 0,
        "fd": int(bool(msg.is_fd)),
        "brs": int(bool(msg.bitrate_switch)) if msg.is_fd else 0,
        "remote": int(bool(msg.is_remote_frame)),
        "error": int(bool(msg.is_error_frame)),
        "dlc": int(msg.dlc),  # Requested length for RTR; not observed payload bytes.
        "data": bytes(msg.data).hex().upper() if not msg.is_remote_frame else "",
    }


def run_dump(dll, blf, dump_csv, export_blf=None):
    cmd = ["dotnet", dll, blf, f"--dump={dump_csv}"]
    if export_blf:
        cmd.append(f"--export-blf={export_blf}")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise SystemExit(f"TraceVerify failed ({proc.returncode}): {proc.stderr.strip() or proc.stdout.strip()}")


def compare(reference, rows, label, channel_offset, tolerance_us=11):
    problems = []
    if len(reference) != len(rows):
        problems.append(f"frame count: reference {len(reference)} vs application {len(rows)}")
    for i, (ref_msg, row) in enumerate(zip(reference, rows)):
        # Application timestamps are absolute nanoseconds; compare relative to the first frame.
        ref = key(ref_msg, reference[0].timestamp)
        got = {
            "ts_us": round((int(row["timestamp_ns"]) - int(rows[0]["timestamp_ns"])) / 1000),
            "channel": int(row["channel"]) - channel_offset,
            "id": int(row["id"]) if not int(row["error"]) else 0,
            "ext": int(row["extended"]) if not int(row["error"]) else 0,
            "fd": int(row["fd"]),
            "brs": int(row["brs"]),
            "remote": int(row["remote"]),
            "error": int(row["error"]),
            "dlc": int(row["dlc"]),
            "data": row["data"].upper() if not int(row["remote"]) else "",
        }
        for field in ("channel", "id", "ext", "fd", "brs", "remote", "error", "dlc"):
            if ref[field] != got[field]:
                problems.append(f"frame {i}: {field} {ref[field]} != {got[field]}")
        if not int(row["error"]) and ref["data"] != got["data"]:
            problems.append(f"frame {i}: data {ref['data']} != {got['data']}")
        if abs(ref["ts_us"] - got["ts_us"]) > tolerance_us:
            problems.append(f"frame {i}: timestamp {ref['ts_us']}us != {got['ts_us']}us")
        if len(problems) > 20:
            break
    print(f"[{'PASS' if not problems else 'FAIL'}] {label}: {len(reference)} reference frames")
    for problem in problems[:20]:
        print("   ", problem)
    return not problems


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dll", help="path to RbgCanInsight.TraceVerify.dll")
    parser.add_argument("--frames", type=int, default=3000)
    args = parser.parse_args()
    if args.frames < 12:
        parser.error("--frames must be at least 12 to exercise positive and negative controls")

    messages = synthetic_messages(args.frames)
    ok = True
    print("coverage:",
          f"extended={sum(bool(m.is_extended_id) and not m.is_error_frame for m in messages)}",
          f"remote={sum(bool(m.is_remote_frame) for m in messages)}",
          f"fd={sum(bool(m.is_fd) for m in messages)}",
          f"fd+brs={sum(bool(m.is_fd and m.bitrate_switch) for m in messages)}",
          f"error={sum(bool(m.is_error_frame) for m in messages)}",
          f"channels={sorted({m.channel for m in messages})}",
          f"max_payload={max(len(m.data) for m in messages)}")
    with tempfile.TemporaryDirectory() as work:
        for level, label in ((6, "zlib level 6"), (0, "no compression")):
            source = os.path.join(work, f"py_{level}.blf")
            write_blf(source, messages, level)
            reference = read_blf(source)  # python-can's own decode is the reference

            dump_csv = os.path.join(work, f"dump_{level}.csv")
            exported = os.path.join(work, f"app_{level}.blf")
            run_dump(args.dll, source, dump_csv, exported)
            with open(dump_csv, newline="") as handle:
                rows = list(csv.DictReader(handle))
            # python-can reports channel-1; the application reports the raw zero-based channel.
            ok &= compare(reference, rows, f"python-can writer ({label}) -> application import",
                          channel_offset=0)
            # Negative control: the comparison must notice a deliberately corrupted row.
            tampered = [dict(r) for r in rows]
            tampered[10]["data"] = "FFFF" if tampered[10]["data"] != "FFFF" else "0000"
            tampered[11]["id"] = str(int(tampered[11]["id"]) ^ 1)
            import contextlib, io
            with contextlib.redirect_stdout(io.StringIO()):
                detects = not compare(reference, tampered, "negative control", channel_offset=0)
            print(f"[{'PASS' if detects else 'FAIL'}] negative control detects tampered data/id ({label})")
            ok &= detects

            # Application writer -> python-can reader, compared with python-can's reference decode.
            again = read_blf(exported)
            dump2 = os.path.join(work, f"dump_{level}_re.csv")
            run_dump(args.dll, exported, dump2)
            with open(dump2, newline="") as handle:
                rows2 = list(csv.DictReader(handle))
            ok &= compare(reference, rows2, f"application export ({label}) -> application re-import",
                          channel_offset=0)
            same = len(again) == len(reference)
            for a, b in zip(again, reference):
                if (a.arbitration_id, a.is_extended_id, a.is_fd, a.is_remote_frame, a.is_error_frame,
                        bytes(a.data), a.channel) != (b.arbitration_id, b.is_extended_id, b.is_fd,
                                                      b.is_remote_frame, b.is_error_frame, bytes(b.data), b.channel):
                    same = False
                    break
            print(f"[{'PASS' if same else 'FAIL'}] application BLF export ({label}) read back by python-can: "
                  f"{len(again)} frames, identical content/order to python-can's own decode")
            ok &= same
    print("RESULT:", "ALL PASS" if ok else "FAILURES")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
