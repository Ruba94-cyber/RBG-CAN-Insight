"""Independent DBC/BLF oracle (hand-derived from the DBC and Vector BLF layouts).

No python-can / cantools needed: only struct + zlib from the Python standard library.
It deliberately shares NO code or algorithm with the C# product:
  * signals are extracted through a *linear bit index* (Intel: bit n of the little-endian
    integer; Motorola: big-endian integer where DBC bit (byte*8+b) maps to linear
    byte*8+(7-b)), not through the DBC "sawtooth" walk the product uses;
  * the BLF files are produced by this script's own byte writer.

Usage:  python make_vectors.py <output-dir>
Writes oracle.dbc, oracle_main.blf, oracle_neg_*.blf and expected.json.
"""
import json
import os
import random
import struct
import sys
import zlib

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(OUT, exist_ok=True)


# ---------------------------------------------------------------- DBC oracle
def extract(data, start, length, order):
    """Raw unsigned integer; None when the signal does not fit the payload."""
    nbits = len(data) * 8
    if order == "intel":
        if start + length > nbits:
            return None
        return (int.from_bytes(data, "little") >> start) & ((1 << length) - 1)
    byte, bit = divmod(start, 8)
    lin = byte * 8 + (7 - bit)  # linear big-endian index of the MSB
    if lin + length > nbits:
        return None
    return (int.from_bytes(data, "big") >> (nbits - lin - length)) & ((1 << length) - 1)


def to_signed(v, length):
    return v - (1 << length) if v >> (length - 1) else v


class Sig:
    def __init__(s, name, start, length, order="intel", signed=False, factor=1.0, offset=0.0,
                 unit="", vtype="int", mux=None, vals=None, mul=None):
        s.name, s.start, s.length, s.order, s.signed = name, start, length, order, signed
        s.factor, s.offset, s.unit, s.vtype, s.mux = factor, offset, unit, vtype, mux
        s.vals, s.mul = vals or {}, mul or {}

    def dbc_line(s):
        muxtxt = "" if s.mux is None else " " + s.mux
        sign = "-" if s.signed else "+"
        return (f' SG_ {s.name}{muxtxt} : {s.start}|{s.length}@{1 if s.order == "intel" else 0}{sign} '
                f'({s.factor!r},{s.offset!r}) [0|0] "{s.unit}" ECU2')


class Msg:
    def __init__(m, ident, ext, name, length, sigs):
        m.ident, m.ext, m.name, m.length, m.sigs = ident, ext, name, length, sigs

    @property
    def dbc_id(m):
        return m.ident | (0x80000000 if m.ext else 0)


def raw_and_value(sig, data):
    r = extract(data, sig.start, sig.length, sig.order)
    if r is None:
        return None
    if sig.vtype == "f32":
        v = struct.unpack("<f", r.to_bytes(4, "little"))[0]
        return r, v, v * sig.factor + sig.offset
    if sig.vtype == "f64":
        v = struct.unpack("<d", r.to_bytes(8, "little"))[0]
        return r, v, v * sig.factor + sig.offset
    rv = to_signed(r, sig.length) if sig.signed else r
    return rv, rv, rv * sig.factor + sig.offset


def mux_value(sig, data):
    r = extract(data, sig.start, sig.length, sig.order)
    return None if r is None else (to_signed(r, sig.length) if sig.signed else r)


def active(msg, sig, data, depth=0):
    """Extended multiplexing: all SG_MUL_VAL_ controllers must be active and in range.
    Simple multiplexing: m<N> binds to the (only) 'M' signal."""
    if depth > 8:
        return False
    by_name = {x.name: x for x in msg.sigs}
    if sig.mul:
        for ctrl, ranges in sig.mul.items():
            c = by_name[ctrl]
            if not active(msg, c, data, depth + 1):
                return False
            v = mux_value(c, data)
            if v is None or not any(lo <= v <= hi for lo, hi in ranges):
                return False
        return True
    if sig.mux and sig.mux != "M":
        n = int(sig.mux[1:].rstrip("M"))
        roots = [x for x in msg.sigs if x.mux == "M"]
        assert len(roots) == 1
        return mux_value(roots[0], data) == n
    return True


def decode(msg, data):
    out = {}
    for s in msg.sigs:
        rv = raw_and_value(s, data)
        if rv is None or not active(msg, s, data):
            continue
        out[s.name] = rv
    return out


# ---- self-check of the oracle against constants worked out by hand on paper
assert extract(bytes([0x12, 0x34, 0, 0, 0, 0, 0, 0]), 7, 16, "moto") == 0x1234
assert extract(bytes([0, 0, 0xA5, 0x3C, 0, 0, 0, 0]), 19, 12, "moto") == 0x53C
assert extract(bytes([0, 0, 0, 0x34, 0x12, 0, 0, 0]), 24, 16, "intel") == 0x1234
assert to_signed(extract(bytes([0, 0xF5, 0x8C, 0, 0, 0, 0, 0]), 8, 12, "intel"), 12) == -779
assert -779 * 0.5 - 10 == -399.5
assert struct.unpack("<f", bytes([0xDB, 0x0F, 0x49, 0x40]))[0] == 3.1415927410125732
assert extract(bytes([0xFF] * 8), 0, 64, "intel") == 2**64 - 1

# ---------------------------------------------------------------- messages
msgs = []
msgs.append(Msg(0x100, False, "IntelMsg", 8, [
    Sig("U8", 0, 8, unit="-"),
    Sig("S12", 8, 12, signed=True, factor=0.5, offset=-10.0, unit="degC"),
    Sig("U16", 24, 16, factor=0.01, unit="V"),
    Sig("Flag", 40, 1, vals={0: "Off", 1: "On"}),
    Sig("S5", 41, 5, signed=True, vals={-1: "Minus one", -16: "Min", 0: "Zero"}),
    Sig("U10", 46, 10, factor=2.0, offset=1.0),
    Sig("S8", 56, 8, signed=True),
]))
msgs.append(Msg(0x101, False, "MotorolaMsg", 8, [
    Sig("M16", 7, 16, "moto"),
    Sig("M12", 19, 12, "moto"),
    Sig("M4", 23, 4, "moto"),
    Sig("MS4", 35, 4, "moto", signed=True, factor=3.0, offset=-1.0),
    Sig("M4b", 39, 4, "moto"),
    Sig("M8", 47, 8, "moto"),
    Sig("MS10", 55, 10, "moto", signed=True, factor=0.125),
]))
msgs.append(Msg(0x102, False, "FloatMsg", 8, [
    Sig("F32le", 0, 32, vtype="f32", factor=2.0, offset=0.5),
    Sig("F32be", 39, 32, "moto", vtype="f32"),
]))
msgs.append(Msg(0x103, False, "Float64Msg", 8, [Sig("F64", 0, 64, vtype="f64", offset=-1.0)]))
msgs.append(Msg(0x104, False, "Wide64Msg", 8, [Sig("U64", 0, 64)]))
msgs.append(Msg(0x105, False, "WideS64Msg", 8, [Sig("S64", 0, 64, signed=True)]))
msgs.append(Msg(0x106, False, "MotoWideMsg", 8, [Sig("MU64", 7, 64, "moto")]))
msgs.append(Msg(0x18FEF100, True, "ExtJ1939Msg", 8, [
    Sig("EngSpeed", 24, 16, factor=0.125, unit="rpm"),
    Sig("Torque", 8, 8, offset=-125.0, unit="%"),
]))
msgs.append(Msg(0x100, True, "ExtSameNumber", 8, [Sig("ExtU8", 0, 8, factor=10.0)]))
msgs.append(Msg(0x300, False, "FdMsg", 64, [
    Sig("FdFirst", 0, 8),
    Sig("FdI16", 200, 16, factor=0.5),
    Sig("FdS32", 400, 32, signed=True, factor=0.001, offset=100.0),
    Sig("FdMoto", 503, 16, "moto", signed=True),
    Sig("FdLast", 504, 8),
]))
msgs.append(Msg(0x301, True, "FdExtMsg", 24, [
    Sig("FdeU32", 128, 32), Sig("FdeMoto", 167, 24, "moto", factor=0.5),
]))
msgs.append(Msg(0x400, False, "SimpleMux", 8, [
    Sig("Sel", 0, 8, mux="M"),
    Sig("A0", 8, 16, mux="m0"),
    Sig("A1", 8, 16, mux="m1", factor=0.1),
    Sig("B1", 24, 8, signed=True, mux="m1"),
    Sig("C", 32, 8),
]))
msgs.append(Msg(0x401, False, "ExtMux", 8, [
    Sig("SelA", 0, 4, mux="M"),
    Sig("SelB", 4, 4, mux="m1M", mul={"SelA": [(1, 1)]}),
    Sig("Y", 8, 8, mux="m5", mul={"SelB": [(5, 5), (7, 8)]}),
    Sig("Z", 16, 8, mux="m0", mul={"SelA": [(0, 0)]}),
    Sig("W", 24, 8, mux="m2", mul={"SelA": [(2, 3)]}),
    Sig("Always", 32, 8),
]))
msgs.append(Msg(0x402, False, "SignedMux", 8, [
    Sig("SSel", 0, 4, signed=True, mux="M"),
    Sig("SNeg", 8, 8, mux="m15"),   # raw nibble 0xF is -1 when signed; m15 must NOT match
    Sig("SPos", 16, 8, mux="m7"),
]))

# Generated broad coverage: single-signal messages with random placement.
rnd = random.Random(20261111)
gen_msgs = []
for i in range(60):
    ln = rnd.choice([8, 8, 8, 12, 24, 64])
    bits = rnd.choice([1, 2, 3, 5, 7, 8, 9, 11, 12, 13, 16, 17, 24, 31, 32, 33, 48, 63, 64])
    bits = min(bits, ln * 8)
    lin = rnd.randrange(0, ln * 8 - bits + 1)
    order = rnd.choice(["intel", "moto"])
    start = lin if order == "intel" else (lin // 8) * 8 + (7 - lin % 8)
    signed = rnd.random() < 0.5
    factor = rnd.choice([1.0, 0.5, 0.01, 2.0, 0.125, -1.0, 3.0])
    offset = rnd.choice([0.0, -40.0, 100.0, 0.5])
    ext = rnd.random() < 0.3
    ident = rnd.randrange(0x800, 0x1FFFFFFF) if ext else 0x500 + i
    gen_msgs.append(Msg(ident, ext, f"Gen{i:02d}", ln,
                        [Sig(f"G{i:02d}", start, bits, order, signed, factor, offset)]))
msgs.extend(gen_msgs)


# ---------------------------------------------------------------- DBC text
def dbc_text():
    lines = ['VERSION "oracle-1"', "", "NS_ :", "", "BS_:", "", "BU_: ECU1 ECU2", ""]
    for m in msgs:
        lines.append(f"BO_ {m.dbc_id} {m.name}: {m.length} ECU1")
        for s in m.sigs:
            lines.append(s.dbc_line())
        lines.append("")
    for m in msgs:
        for s in m.sigs:
            if s.vtype in ("f32", "f64"):
                lines.append(f"SIG_VALTYPE_ {m.dbc_id} {s.name} : {1 if s.vtype == 'f32' else 2};")
    for m in msgs:
        for s in m.sigs:
            for ctrl, ranges in s.mul.items():
                lines.append(f"SG_MUL_VAL_ {m.dbc_id} {s.name} {ctrl} " +
                             ", ".join(f"{a}-{b}" for a, b in ranges) + ";")
    for m in msgs:
        for s in m.sigs:
            if s.vals:
                lines.append(f"VAL_ {m.dbc_id} {s.name} " +
                             " ".join(f'{k} "{v}"' for k, v in s.vals.items()) + " ;")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- BLF writer
FD_LEN2DLC = {0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 6: 6, 7: 7, 8: 8, 12: 9, 16: 10, 20: 11,
              24: 12, 32: 13, 48: 14, 64: 15}


def lobj(typ, body, v2=False, tsflag=2, ts_raw=0):
    if v2:
        hdr = struct.pack("<IBBHQQ", tsflag, 0, 0, 0, ts_raw, ts_raw)
        hsize, hver = 40, 2
    else:
        hdr = struct.pack("<IHHQ", tsflag, 0, 0, ts_raw)
        hsize, hver = 32, 1
    payload = hdr + body
    return b"LOBJ" + struct.pack("<HHII", hsize, hver, 16 + len(payload), typ) + payload


def raw_obj(typ, body):  # base header only (containers, junk objects)
    return b"LOBJ" + struct.pack("<HHII", 16, 1, 16 + len(body), typ) + body


def frame_obj(f):
    rawid = f["id"] | (0x80000000 if f["ext"] else 0)
    ts = f["ts"]
    unit10 = f.get("tsflag", 2) == 1
    if unit10:
        assert ts % 10000 == 0
    kw = dict(v2=f.get("v2", False), tsflag=f.get("tsflag", 2), ts_raw=ts // 10000 if unit10 else ts)
    data = f["data"]
    k = f["kind"]
    if k in ("can1", "can2"):
        flags = (0x80 if f.get("remote") else 0) | (1 if f.get("tx") else 0)
        dlc = f.get("dlc", len(data))
        body = struct.pack("<HBBI", f["ch"], flags, dlc, rawid) + data.ljust(8, b"\0")
        if k == "can2":
            body += struct.pack("<IBBH", 111111, 0, 0, 0)
        return lobj(1 if k == "can1" else 86, body, **kw)
    if k == "fd100":
        flags = (0x80 if f.get("remote") else 0) | (1 if f.get("tx") else 0)
        fdf = (1 if f.get("fd") else 0) | (2 if f.get("brs") else 0) | (4 if f.get("esi") else 0)
        body = struct.pack("<HBBIIBBBB", f["ch"], flags, FD_LEN2DLC[len(data)], rawid, 5000, 20, fdf,
                           len(data), 0)
        body += struct.pack("<I", 0) + data.ljust(64, b"\0")
        return lobj(100, body, **kw)
    if k == "fd101":
        fl = ((0x10 if f.get("remote") else 0) | (0x1000 if f.get("fd") else 0) |
              (0x2000 if f.get("brs") else 0) | (0x4000 if f.get("esi") else 0))
        body = struct.pack("<BBBBIIIIIIIHBBI", f["ch"], FD_LEN2DLC[len(data)], len(data), 0, rawid, 5000,
                           fl, 0x1111, 0x2222, 0, 0, 20, 1 if f.get("tx") else 0, 0, 0xDEADBEEF) + data
        return lobj(101, body, **kw)
    if k == "err73":
        body = struct.pack("<HHIBBBBIIHH", f["ch"], 32, 0, 1, 2, len(data), 0, 0, rawid, 0, 0)
        body += data.ljust(8, b"\0")
        return lobj(73, body, **kw)
    if k == "err2":
        return lobj(2, struct.pack("<HHI", f["ch"], 8, 0), **kw)
    raise ValueError(k)


def pad_mod(obj):         # python-can style: objectSize % 4
    return obj + b"\0" * (len(obj) % 4)


def pad_compl(obj):       # Vector style: pad up to a multiple of 4
    return obj + b"\0" * ((4 - len(obj) % 4) % 4)


def container(chunk, compress):
    if compress:
        payload, method = zlib.compress(chunk), 2
    else:
        payload, method = chunk, 0
    return raw_obj(10, struct.pack("<H6xI4x", method, len(chunk)) + payload)


def file_header(total, count, uncompressed):
    h = bytearray(144)
    h[0:4] = b"LOGG"
    struct.pack_into("<I", h, 4, 144)
    h[8:16] = bytes([1, 2, 3, 4, 5, 6, 7, 8])
    struct.pack_into("<QQII", h, 16, total, uncompressed, count, 0)
    return bytes(h)


def assemble(top_objects, uncompressed=0, count=0):
    body = b"".join(top_objects)
    return file_header(144 + len(body), count, uncompressed) + body


# ---------------------------------------------------------------- frame plan
frames = []   # in on-disk order = expected import order
ts_clock = [1_000_000]


def add(kind, ch, ident, ext, data, **kw):
    ts_clock[0] += rnd.randrange(900, 1100) * 1000
    f = dict(kind=kind, ch=ch, id=ident, ext=ext, data=bytes(data), ts=kw.pop("ts", ts_clock[0]), **kw)
    frames.append(f)
    return f


by_key = {(m.ident, m.ext): m for m in msgs}
M = {m.name: m for m in msgs}


def add_msg(m, data, kind="can1", ch=1, **kw):
    return add(kind, ch, m.ident, m.ext, data, **kw)


# Intel
add_msg(M["IntelMsg"], bytes([0xAB, 0xF5, 0x8C, 0x34, 0x12, 0x00, 0x00, 0x00]), "can1", 1)
add_msg(M["IntelMsg"], bytes([0xFF] * 8), "can2", 2, tx=True, v2=True)
add_msg(M["IntelMsg"], bytes(8), "can1", 1, tsflag=1)
add_msg(M["IntelMsg"], bytes([0x01, 0x02, 0x03, 0x04]), "can1", 3)            # short payload: later signals absent
add_msg(M["IntelMsg"], bytes([0x80, 0x00, 0x08, 0, 0, 0x3F, 0xF0, 0x80]), "can1", 1)
# Motorola
add_msg(M["MotorolaMsg"], bytes([0x12, 0x34, 0xA5, 0x3C, 0x9F, 0xC3, 0x7E, 0x81]), "can1", 1)
add_msg(M["MotorolaMsg"], bytes([0xFF] * 8), "can2", 1)
add_msg(M["MotorolaMsg"], bytes([0x80, 0x01, 0x10, 0x00, 0x08, 0x00, 0x00, 0x00]), "can1", 2)
# floats
add_msg(M["FloatMsg"], bytes([0xDB, 0x0F, 0x49, 0x40]) + bytes([0x40, 0x49, 0x0F, 0xDB]), "can1", 1)
add_msg(M["FloatMsg"], struct.pack("<f", -0.0) + struct.pack(">f", 1.0e-30), "can1", 1)
add_msg(M["FloatMsg"], struct.pack("<f", 3.0e38) + struct.pack(">f", -123456.789), "fd100", 1, fd=True)
add_msg(M["Float64Msg"], struct.pack("<d", 2.718281828459045), "can1", 1)
add_msg(M["Float64Msg"], struct.pack("<d", -1.0e-300), "can1", 2)
# 64-bit exactness
add_msg(M["Wide64Msg"], bytes([0xFF] * 8), "can1", 1)
add_msg(M["Wide64Msg"], struct.pack("<Q", 9007199254740993), "can1", 1)   # 2^53+1: not a double
add_msg(M["WideS64Msg"], struct.pack("<q", -9223372036854775808), "can1", 1)
add_msg(M["WideS64Msg"], struct.pack("<q", 9007199254740993), "can1", 1)
add_msg(M["MotoWideMsg"], bytes.fromhex("0123456789ABCDEF"), "can1", 1)
# 29-bit, and the same numeric id in both formats
add_msg(M["ExtJ1939Msg"], bytes([0, 0x8D, 0, 0x10, 0x27, 0, 0, 0]), "can1", 1)
add_msg(M["ExtSameNumber"], bytes([7]), "can2", 2, tx=True)
add("can1", 2, 0x100, False, bytes([9]))              # standard 0x100 = IntelMsg, but only 1 byte
add("can1", 1, 0x555, False, bytes([1, 2, 3]))        # no DBC message
# remote and error frames (must yield no values even for a defined id)
add("can1", 1, 0x100, False, b"", remote=True, dlc=8)
add("can2", 1, 0x18FEF100, True, b"", remote=True, dlc=4, tx=True)
add("fd101", 1, 0x100, False, b"", remote=True)
add("err73", 1, 0x100, False, bytes([1, 2, 3, 4, 5, 6, 7, 8]))
add("err2", 2, 0, False, b"")
# CAN FD
fd = bytearray(64)
fd[0] = 0xEE
fd[25], fd[26] = 0x34, 0x12                       # FdI16 @200 = 0x1234
fd[50:54] = struct.pack("<i", -123456)
fd[62], fd[63] = 0x80, 0x01                       # FdMoto @503 big-endian signed 16 = 0x8001
add_msg(M["FdMsg"], bytes(fd), "fd100", 1, fd=True, brs=True)
fd2 = bytearray(fd)
fd2[63] = 0x99
add_msg(M["FdMsg"], bytes(fd2), "fd101", 3, fd=True, brs=False, esi=True, tx=True)
add_msg(M["FdMsg"], bytes(fd2[:48]), "fd100", 1, fd=True)       # shorter FD frame: FdMoto/FdLast absent
add_msg(M["FdExtMsg"], bytes(range(1, 25)), "fd101", 2, fd=True, brs=True, v2=True)
add_msg(M["FdExtMsg"], bytes(range(101, 125)), "fd100", 2, fd=True, tsflag=1)
# multiplexing
sm = M["SimpleMux"]
for sel in (0, 1, 2):
    add_msg(sm, bytes([sel, 0x34, 0x12, 0x80, 0x55, 0, 0, 0]))
em = M["ExtMux"]
for first in (0x51, 0x71, 0x81, 0x61, 0x50, 0x52, 0x53, 0x54):
    add_msg(em, bytes([first, 0x11, 0x22, 0x33, 0x44, 0, 0, 0]))
sg = M["SignedMux"]
add_msg(sg, bytes([0x0F, 0xAA, 0xBB, 0, 0, 0, 0, 0]))       # signed nibble = -1: neither m15 nor m7
add_msg(sg, bytes([0x07, 0xAA, 0xBB, 0, 0, 0, 0, 0]))       # m7 active
# generated coverage
for m in gen_msgs:
    for _ in range(4):
        n = m.length
        data = bytes(rnd.randrange(256) for _ in range(n))
        kind = rnd.choice(["can1", "can2"]) if n <= 8 else rnd.choice(["fd100", "fd101"])
        assert n <= 8 or n in FD_LEN2DLC
        add_msg(m, data, kind, rnd.choice([1, 2, 3]), fd=n > 8, brs=rnd.random() < 0.5,
                tx=rnd.random() < 0.3, tsflag=rnd.choice([1, 2]), v2=rnd.random() < 0.2)

for f in frames:   # 10 us unit frames need a ts that is a multiple of 10 us
    if f.get("tsflag") == 1:
        f["ts"] = f["ts"] // 10000 * 10000

# top-level (uncompressed) frames written after all containers
tail_frames = [
    dict(kind="can1", ch=3, id=0x100, ext=False, data=bytes([0x11, 0, 0, 0, 0, 0, 0, 0]),
         tsflag=1, ts=900_000_000),
    dict(kind="fd101", ch=1, id=0x300, ext=False, data=bytes(fd), fd=True, v2=True, ts=901_000_000),
]

stream = b""
for i, f in enumerate(frames):
    o = frame_obj(f)
    if i % 17 == 5:
        stream += pad_mod(lobj(11, b"\x01" * 24))            # LIN_MESSAGE: skipped
    if i % 23 == 7:
        stream += pad_compl(raw_obj(999, b"\xAA" * 13))      # unknown type: skipped
    stream += pad_mod(o) if i % 2 else pad_compl(o)
cuts = [len(stream) // 3 + 5, 2 * len(stream) // 3 + 3]       # splits land inside objects
chunks = [stream[:cuts[0]], stream[cuts[0]:cuts[1]], stream[cuts[1]:]]
tops = [pad_compl(container(chunks[0], True)), pad_compl(container(chunks[1], False)),
        pad_compl(container(chunks[2], True)), pad_compl(raw_obj(999, b"\x55" * 7))]
for f in tail_frames:
    tops.append(pad_compl(frame_obj(f)))
frames_all = frames + tail_frames
main = assemble(tops, uncompressed=len(stream), count=len(frames_all))
good_container_file = assemble([pad_compl(container(pad_compl(frame_obj(frames[0])), True))])

# ---------------------------------------------------------------- negatives
neg = {}
neg["truncated_container"] = main[: 144 + 16 + 20 + 60]
neg["truncated_object_header"] = assemble([pad_compl(frame_obj(frames_all[0]))]) + b"LOBJ\x20\x00"
neg["zero_length"] = b""
neg["header_only_garbage"] = b"LOGG"
neg["bad_magic"] = b"NOPE" + main[4:]
neg["huge_object_size"] = assemble([b"LOBJ" + struct.pack("<HHII", 32, 1, 0xFFFFFFF0, 1) + b"\0" * 16])
neg["huge_container_declared"] = assemble(
    [raw_obj(10, struct.pack("<H6xI4x", 0, 0x7FFFFFF0) + b"\0" * 32)])
neg["huge_zlib_declared"] = assemble(
    [raw_obj(10, struct.pack("<H6xI4x", 2, 0x7FFFFFF0) + zlib.compress(b"\0" * 64))])
neg["huge_header_size"] = main[:4] + struct.pack("<I", 0x7FFFFFFF) + main[8:]
cont = bytearray(good_container_file)
cont[-6] ^= 0xFF                                                # corrupt inside the zlib stream/adler32
neg["bad_zlib_checksum"] = bytes(cont)
bad_sig = bytearray(assemble([pad_compl(frame_obj(frames_all[k])) for k in (0, 1, 2)]))
bad_sig[144 + 1] = ord("X")                                     # first LOBJ -> LXBJ
neg["bad_object_signature"] = bytes(bad_sig)
neg["unknown_only"] = assemble([pad_compl(raw_obj(999, b"\1" * 9)), pad_compl(lobj(11, b"\2" * 24)),
                                pad_compl(frame_obj(frames_all[0]))])
neg["unsupported_compression"] = assemble([raw_obj(10, struct.pack("<H6xI4x", 7, 4) + b"abcd")])


# ---------------------------------------------------------------- expected json
def frame_json(i, f):
    is_err = f["kind"] in ("err73", "err2")
    return {
        "index": i, "channel": f["ch"] - 1, "id": f["id"] if f["kind"] != "err2" else 0,
        "extended": f["ext"], "remote": bool(f.get("remote")), "error": is_err,
        "tx": bool(f.get("tx")) and not is_err, "fd": bool(f.get("fd")) and f["kind"] in ("fd100", "fd101"), "brs": bool(f.get("brs")) and bool(f.get("fd")) and f["kind"] in ("fd100", "fd101"),
        "ts_ns": f["ts"],
        "data": "" if (f.get("remote") or f["kind"] == "err2") else f["data"].hex(),
    }


signals = []
for i, f in enumerate(frames_all):
    if f["kind"] in ("err73", "err2") or f.get("remote"):
        continue
    m = by_key.get((f["id"], f["ext"]))
    if not m:
        continue
    for name, (raw, rawv, phys) in decode(m, f["data"]).items():
        sig = next(s for s in m.sigs if s.name == name)
        text = sig.vals.get(raw) if (sig.vals and sig.vtype == "int") else None
        signals.append({"frame": i, "message": m.name, "signal": name, "raw": str(raw),
                        "raw_value": float(rawv), "physical": phys, "text": text, "unit": sig.unit})

expected = {
    "oracle": "hand-derived: tools/dbc-blf-oracle/make_vectors.py (Python %s stdlib struct+zlib, "
              "independent linear-bit-index algorithm)" % sys.version.split()[0],
    "frames": [frame_json(i, f) for i, f in enumerate(frames_all)],
    "signals": signals,
    "unknown_only_frame": frame_json(0, frames_all[0]),
    "bad_signature_survivors": [frame_json(k, frames_all[k]) for k in (1, 2)],
}

with open(os.path.join(OUT, "oracle.dbc"), "w", newline="\n") as fh:
    fh.write(dbc_text())
with open(os.path.join(OUT, "oracle_main.blf"), "wb") as fh:
    fh.write(main)
for name, blob in neg.items():
    with open(os.path.join(OUT, f"oracle_neg_{name}.blf"), "wb") as fh:
        fh.write(blob)
with open(os.path.join(OUT, "expected.json"), "w") as fh:
    json.dump(expected, fh, indent=1)
print(len(frames_all), "frames,", len(signals), "signals,", len(main), "bytes main")
