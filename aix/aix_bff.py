#!/usr/bin/env python3
"""
Decoder/extractor for AIX 3.1 installp/BFF "backup by name" tape files.
Dmitry Brant, 2026, with help from Claude Opus 4.8.

Container format (little-endian):
  Volume header: 72 bytes, magic 0xEA6B at off+2.
  Per file record:
    off+0 : 2 bytes (b0, 0x0b) then magic 0xEA6B (control) or 0xEA6C (product) at off+2
    off+12: st_mode (u32)      off+16: uid   off+20: gid
    off+24: original (uncompressed) size
    off+28/32/36: atime/mtime/ctime
    off+48: rdev/flag           off+56: stored (on-tape) size
    off+64: file name, NUL-terminated, padded to a multiple of 8 bytes
    then  : 40-byte attribute/ACL block starting 02 00 00 00 02 00 00 00 10 00 00 00
    then  : `stored size` bytes of data
    then  : pad to 8-byte boundary
  If stored_size == orig_size the data is verbatim, else it is Huffman-compressed.

Compression: canonical Huffman over the 256-byte alphabet.
  byte0 = N (max code length); next N bytes = leaf count per code length 1..N;
  then sum(counts) symbol bytes in canonical (leaves-at-high-codes) order.
  The tree is completed to Kraft==1 by adding leaf(s) whose symbol is a byte
  value absent from the explicit table (stored implicitly to save space).
"""
import struct, os, sys

ATTR_SIG = b'\x02\x00\x00\x00\x02\x00\x00\x00\x10\x00\x00\x00'

def u32(d, o): return struct.unpack_from('<I', d, o)[0]
def align(x, n): return (x + n - 1)//n*n

def huff_decompress(comp, target):
    """Canonical Huffman: byte0=N (max code len), N leaf-counts, then the symbols.
    The tree is completed to Kraft==1 with completing leaf(s) whose symbol byte(s)
    are stored explicitly right after the sum(counts) primary symbols; the packed
    MSB-first bitstream follows. Leaves occupy the high codes at each level."""
    N = comp[0]
    counts = [0] + list(comp[1:1+N])
    nsym = sum(counts)

    # Kraft deficit -> completing leaves (one per set bit of the deficit)
    weight = sum(counts[l] << (N - l) for l in range(1, N+1))
    deficit = (1 << N) - weight
    add_levels = [L for L in range(1, N+1) if deficit & (1 << (N - L))]

    # primary symbols, then one explicit symbol per completing leaf (level order)
    syms = list(comp[1+N : 1+N+nsym])
    extra = list(comp[1+N+nsym : 1+N+nsym+len(add_levels)])
    bitstart = (1 + N + nsym + len(add_levels)) * 8

    counts2 = counts[:]
    addmap = {}
    for L, sym in zip(add_levels, extra):
        counts2[L] += 1
        addmap[L] = sym
    s2 = []; idx = 0
    for L in range(1, N+1):
        s2.extend(syms[idx:idx+counts[L]]); idx += counts[L]
        if L in addmap:
            s2.append(addmap[L])
    return bytes(_decode(comp, counts2, s2, bitstart, target))

def _decode(comp, counts, syms, bitpos, target):
    N = len(counts) - 1
    base = [0]*(N+2); acc = 0
    for l in range(1, N+1):
        base[l] = acc; acc += counts[l]
    internal = [0]*(N+2); internal[0] = 1
    for l in range(1, N+1):
        internal[l] = 2*internal[l-1] - counts[l]
        if internal[l] < 0:
            return bytearray()
    out = bytearray(); tot = len(comp)*8
    # allow a few extra bits past the end (encoder byte-padding) treated as 0
    while len(out) < target:
        if bitpos >= tot + N and len(out) < target:
            break
        c = 0; l = 1
        while l <= N:
            bit = ((comp[bitpos >> 3] >> (7-(bitpos & 7))) & 1) if (bitpos >> 3) < len(comp) else 0
            c = c*2 + bit; bitpos += 1
            if c >= internal[l]:
                si = base[l] + (c - internal[l])
                if si >= len(syms):
                    return out
                out.append(syms[si]); break
            l += 1
        else:
            return out
    return out

def parse(data):
    off = 72
    recs = []
    while off + 64 <= len(data):
        if not (data[off+3] == 0xea and data[off+2] in (0x6b, 0x6c)):
            break
        mode = u32(data, off+12)
        orig = u32(data, off+24)
        stored = u32(data, off+56)
        ns = off + 64
        try: ne = data.index(b'\x00', ns)
        except ValueError: break
        name = data[ns:ne].decode('latin1')
        w = data.find(ATTR_SIG, ne, ne+96)
        if w < 0: break
        acl_len = u32(data, w+24)
        dstart = w + 24 + acl_len
        ftype = (mode >> 12) & 0xF
        truncated = False
        if ftype == 0x8:            # regular file
            # supply a few extra bytes: the final Huffman codes can spill into the
            # record's 8-byte alignment padding, which `stored` does not count.
            # Decoding stops at the known output length, so extra input is harmless.
            payload = data[dstart : align(dstart+stored, 8) + 8]
            if dstart + stored > len(data):
                truncated = True
            dend = dstart + stored
        else:
            payload = b''
            dend = dstart
        recs.append(dict(off=off, mag=data[off+2], mode=mode, ftype=ftype,
                         orig=orig, stored=stored, name=name, payload=payload,
                         truncated=truncated))
        if truncated:
            break
        off = align(dend, 8)
    return recs, off

def extract(path, outdir):
    data = open(path, 'rb').read()
    recs, end = parse(data)
    for r in recs:
        if r['ftype'] != 0x8:
            continue
        if r['stored'] != r['orig']:
            raw = huff_decompress(r['payload'], r['orig'])
        else:
            raw = r['payload'][:r['stored']]      # verbatim; trim alignment slack
        if r.get('truncated'):
            status = f'TRUNCATED({len(raw)}/{r["orig"]})'
        else:
            status = 'OK' if len(raw) == r['orig'] else f'SHORT({len(raw)}/{r["orig"]})'
        comp = 'raw' if r['stored'] == r['orig'] else 'huff'
        rel = r['name'].lstrip('./')
        dest = os.path.join(outdir, rel)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, 'wb') as f:
            f.write(raw)
        print(f"  {r['name']:<34} {oct(r['mode'])[2:]:>6} {r['orig']:>8}  [{comp}] {status}")
    return recs, end

if __name__ == '__main__':
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    out = sys.argv[2] if len(sys.argv) > 2 else 'extracted'
    for fn in sorted([f for f in os.listdir(src) if f.endswith('.bin')],
                     key=lambda x: int(x[:-4]) if x[:-4].isdigit() else 0):
        p = os.path.join(src, fn)
        d = open(p, 'rb').read()
        print(f"\n=== {fn} ({len(d)} bytes) ===")
        if not any(d[:64]):
            print("  [empty / tape filemark]"); continue
        if d[2:4] != b'\x6b\xea':
            print(f"  [not a backup archive; first bytes {d[:8].hex()}]")
            if d[:2] == b'1 ':
                print("  [ASCII installp control table / TOC]")
            continue
        extract(p, os.path.join(out, fn[:-4]))
