# AIX 3.1 installp / BFF "backup by name" tape format

Dmitry Brant, 2026, with help from Claude Opus 4.8.

Reverse-engineered from a set of IBM AIX **3.1** (`03.01.0005`, built early 1991)
RS/6000 BOS product-installation tape records. Each tape "file" (a `*.bin` dump
here) is one self-contained archive in AIX's **backup-by-name** format — the
same format `restore`, `installp`, and `.bff` package files use. All multi-byte
integers are **little-endian**.

The companion decoder/extractor is [`aix_bff.py`](aix_bff.py):

```
python3 aix_bff.py <dir-with-*.bin> <output-dir>
```

It prints one line per file (`name  mode  size  [raw|huff]  status`) and writes
the reconstructed directory tree under `<output-dir>/<tapefile>/…`.

---

## 1. Tape-file level

Each `N.bin` is one physical tape record and is one of:

| Kind | How to detect | Notes |
|------|---------------|-------|
| Empty / filemark | first 64 bytes all `0x00` | tape leader / gap |
| ASCII installp TOC | begins `31 20` (`"1 "`) | product table of contents, plain text |
| Backup archive | bytes `[2:4] == 6B EA` (magic `0xEA6B`) | everything below |

An archive = **one 72-byte volume header** followed by a sequence of **file
records**. Parsing stops at the first position that no longer carries a record
magic (or at end-of-data / truncation).

### Volume header (72 bytes)

| Offset | Size | Meaning |
|-------:|-----:|---------|
| 0 | 2 | record kind (`09 00` for the volume header) |
| 2 | 2 | **magic `0xEA6B`** (`6B EA`) |
| 8 | 4 | backup date (Unix `time_t`) |
| 12 | 4 | dump date (Unix `time_t`) |
| 16 | 4 | `0x7FFFFFFF` |
| 20 | 20 | label string `"by name"` (NUL-padded) |
| 40 | 20 | second label / `"build"` |

The first file record begins at offset **72**.

---

## 2. File record

```
+----------------+------------------+----------------+----------------+---------+
| 64-byte header | name (NUL-term.) | 40-byte attr   | data           | pad     |
|                | pad to /8        | (ACL) block    | (stored bytes) | to /8   |
+----------------+------------------+----------------+----------------+---------+
```

### 2a. 64-byte header

| Offset | Size | Meaning |
|-------:|-----:|---------|
| 0 | 1 | record type byte (varies: `09`,`0A`,`0B`,`0C`,…) |
| 1 | 1 | `0x0B` for file records |
| 2 | 2 | **magic**: `0xEA6B` = control file, `0xEA6C` = product file (structurally identical) |
| 4 | 4 | inode-ish / checksum |
| 8 | 4 | (secondary id) |
| 12 | 4 | **`st_mode`** (e.g. `0100644`, `0100755`) |
| 16 | 4 | uid |
| 20 | 4 | gid |
| 24 | 4 | **original (uncompressed) size** |
| 28 | 4 | atime (`time_t`) |
| 32 | 4 | mtime (`time_t`) |
| 36 | 4 | ctime (`time_t`) |
| 48 | 4 | rdev / flag word |
| 56 | 4 | **stored (on-tape) size** |

The file's type comes from `(st_mode >> 12) & 0xF` (`0x8` = regular file, the
only type that carries data in these records).

### 2b. Name

NUL-terminated path (e.g. `./etc/methods/cfgent`), then **NUL-padded up to the
next multiple of 8 bytes**.

### 2c. 40-byte attribute / ACL block

Fixed 40 bytes beginning with the signature
`02 00 00 00 02 00 00 00 10 00 00 00`. It carries a minimal AIX base ACL
(`acl_len = 16` at attr+24, followed by the mode word). `aix_bff.py` locates the
data start robustly by searching for this signature after the name, then adds
`24 + acl_len`.

### 2d. Data and padding

`stored`-size bytes of file data follow, then the record is padded with NULs to
the next **8-byte** boundary. The next record begins there.

**Verbatim vs. compressed:**

* `stored == original`  → data is stored **verbatim**.
* `stored <  original`  → data is **Huffman-compressed** (§3).

---

## 3. Compression — canonical Huffman over bytes

Each compressed file is a single canonical-Huffman stream.

### Stream layout

```
[ N ][ counts[1..N] ][ symbols ][ completing symbol(s) ][ MSB-first bitstream ]
  1        N            sum(counts)   (one per Kraft-deficit leaf)
```

1. **`N`** (1 byte) — maximum code length.
2. **`counts[1..N]`** (`N` bytes) — number of leaves (symbols) at each code
   length `1..N`.
3. **symbols** — `sum(counts)` byte values, listed in canonical code order.
4. **completing symbol(s)** — the tree so far is *incomplete*; complete it to a
   full tree (Kraft sum = 1). For each missing codeword slot (one per set bit of
   the deficit `2^N - Σ counts[l]·2^(N−l)`), there is **one more symbol byte
   stored explicitly here**. In every sample the deficit was `2`, i.e. exactly
   **one** completing leaf one level above the deepest.
5. **bitstream** — packed **MSB-first**.

### Decoding

Canonical assignment with **leaves at the high codes** of each level
(internal nodes take the low codes):

```
internal[0] = 1
for l in 1..N:  internal[l] = 2*internal[l-1] - counts[l]   # counts include completing leaves
```

Walk bits, `code = code*2 + bit`; at level `l`, if `code >= internal[l]` it is a
leaf → symbol index `base[l] + (code - internal[l])`, where `base[l]` is the
running symbol offset. Emit bytes until `original` bytes are produced.

### Gotchas (each cost real debugging time)

* **The completing symbol is explicit, not inferred.** Getting this wrong shifts
  the whole output by a variable number of bytes (right content, wrong start).
* **Leaves occupy the high codes** at each level, not the low ones.
* The final codeword can **spill past `stored`** into the record's 8-byte
  alignment padding — read a few bytes beyond `stored` when decoding (harmless,
  because decoding halts at the known `original` length).

---

## 4. Validation

Decoded output was cross-checked against ground truth:

* Executables begin with XCOFF magic `0x01DF`; their embedded `f_timdat`
  timestamp equals the record's mtime. `file(1)` reports
  *"RISC System/6000 COFF executable … 2nd section name .text"*.
* Libraries decode to valid AIX `<aiaff>` archives (`file` → `archive`).
* Text / C source decode cleanly.

Of 703 file records, **700 decode byte-exact**. The three exceptions are tape
damage, not format issues:

| File | Symptom | Cause |
|------|---------|-------|
| `1.bin:./etc/drivers/hscsidd` | record cut off at end of dump | truncated tape record |
| `3.bin:./usr/lpp/bos/inst_updt/arp` | record cut off at end of dump | truncated tape record |
| `6.bin:./usr/lib/inst_updt/libdbx.a/shr.o` | ~99.99% then garbage tail | bit-level read error in that one stream |

Because records are located by the `stored`-size field, a corrupt or truncated
stream never derails the records that follow it.
