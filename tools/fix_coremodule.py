"""
FixCoreModule -- resolve duplicate TypeDef names in a generated Il2CppInterop
assembly by appending name strings into existing in-section slack.

Diagnosis (measured, see tools/LoadProbe)
-----------------------------------------
Since Steam updated Last Epoch to Unity 6000.4.8f1 (IL2CPP metadata v39),
Cpp2IL 2022.1.0-pre-release.21 emits no NestedClass table (verified: 0 rows).
Compiler-generated NESTED types therefore collapse into same-named TOP-LEVEL
types and the CLR rejects the assembly:

    BadImageFormatException: Duplicate type with name '<>O'

Measured scope: of 213 assemblies in MelonLoader\\Il2CppAssemblies exactly ONE
fails to load -- UnityEngine.CoreModule.dll -- holding 201 colliding TypeDef rows
in 68 groups. None of those rows is referenced by any TypeRef, ExportedType or
NestedClass row, and all 68 groups collide in the global namespace ('').
Only genuinely-damaged output needs repair.

Why the heap must grow
----------------------
The CLR keys top-level types on (namespace, name, arity), so each colliding row
needs its own name. Renaming frees nothing: the #Strings heap interns identical
strings, so `<>c` is ONE slot shared by all ten colliding rows. The heap is also
completely full (its only slack is the null terminators) and is immediately
followed by #US, so it cannot be extended in place.

Approach: use the slack the file already has
--------------------------------------------
The file is 0x4D6E00 bytes while the metadata blob ends at 0x4D69DA, and .text
declares VSize 0x4D682A. So there is a small but sufficient window of unused bytes
at the end of the file. This tool:

  1. finds the metadata blob and the #Strings stream inside it,
  2. refuses to run unless the free window is large enough,
  3. appends one short name per renamed row at the end of the metadata blob
     (i.e. immediately after the last stream, in the unused tail),
  4. bumps #Strings Size to cover the original heap plus the appended bytes,
  5. repoints TypeDef.Name for the renamed rows to the appended offsets.

`#Strings` then spans a byte range that physically contains the #US / #GUID /
#Blob streams after its logical end. That is harmless: streams are located by
their own headers and strings are reached by index, and every index used is
either pre-existing (< the original size) or points into the appended tail.

Everything else -- section table, RVAs, data directories, CLI header -- is left
exactly as it was. The only fields touched are the #Strings Size dword and the
TypeDef.Name column.

Usage
-----
    python fix_coremodule.py <assembly.dll> [--dry-run] [--in-place]

Writes "<name>.fixed.dll" unless --in-place; keeps a .bak. Verify the result with
tools/LoadProbe.
"""

import os
import shutil
import struct
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from diagnose_typedef_dupes import (  # noqa: E402
    analyse, read_pe_metadata, parse_streams, TABLES, T_TYPEDEF, u32,
)

NAME_I, NS_I = 1, 2
FILE_ALIGN = 0x200
SECT_ALIGN = 0x1000


def _stream_size_field(b, md, want):
    """File offset of the Size dword for stream `want` in the metadata stream table.

    Each entry is Offset(4) Size(4) Name(variable ASCII, null terminated, padded
    to the next 4-byte boundary).
    """
    _, count_off = _find_stream_table(b, md)
    nstreams = struct.unpack_from('<H', b, count_off)[0]
    p = count_off + 2
    for _ in range(nstreams):
        size_field = p + 4
        p += 8
        end = b.index(b'\0', p)
        name = b[p:end].decode('ascii', 'replace')
        if name == want:
            return size_field
        p = (end + 1 + 3) & ~3
    raise ValueError('stream %r not found in metadata stream table' % want)


def _find_stream_table(b, md):
    ver_len = u32(b, md + 12)
    p = (md + 16 + ver_len + 3) & ~3
    n = struct.unpack_from('<H', b, p + 2)[0]
    if 1 <= n <= 8:
        return 'spec', p + 2
    raise ValueError('could not locate metadata stream table at md=0x%X' % md)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    src = argv[1]
    dry = '--dry-run' in argv
    in_place = '--in-place' in argv

    r = analyse(src)
    md, _ = read_pe_metadata(src)
    orig = open(src, 'rb').read()
    b = bytearray(orig)
    streams = parse_streams(orig, md)
    s_off, s_size = streams['#Strings']          # offset is relative to md
    s_off += md                                   # now a file offset
    strings_size_field = _stream_size_field(orig, md, '#Strings')
    rows, heaps = r['rows'][T_TYPEDEF], r['heaps']

    # ---- geometry ---------------------------------------------------------
    pe = u32(b, 0x3C)
    nsec = struct.unpack_from('<H', b, pe + 6)[0]      # u16 -- not u32
    optsz = struct.unpack_from('<H', b, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', b, opt)[0]
    sec = opt + optsz
    dd = opt + (96 if magic == 0x10B else 112)
    clr_rva = u32(b, dd + 14 * 8)

    def rva2off(rva):
        for i in range(nsec):
            s = sec + i * 40
            vsize = u32(b, s + 8); va = u32(b, s + 12)
            rawsz = u32(b, s + 16); rawptr = u32(b, s + 20)
            if va <= rva < va + max(vsize, rawsz):
                return rawptr + (rva - va), rawptr + rawsz
        raise ValueError('RVA 0x%X not mapped' % rva)

    clr_off, _ = rva2off(clr_rva)
    meta_rva = u32(b, clr_off + 8)
    meta_size = u32(b, clr_off + 12)
    meta_off, section_raw_end = rva2off(meta_rva)
    if meta_off != md:
        raise ValueError('metadata offset mismatch: 0x%X vs 0x%X' % (meta_off, md))

    blob_end = md + meta_size
    window_start = blob_end
    window_end = min(len(b), section_raw_end)
    window = window_end - window_start

    # ---- plan -------------------------------------------------------------
    by = defaultdict(list)
    for i, row in enumerate(rows, start=1):
        by[(heaps.string(row[NS_I]), heaps.string(row[NAME_I]))].append(i)
    coll = {k: v for k, v in by.items() if len(v) > 1}

    assignments = {}
    for key, idxs in sorted(coll.items()):
        for n, rid in enumerate(idxs[1:], start=1):
            assignments[rid] = 'M%d_%d' % (rid, n)

    # ---- zero-growth alternative -----------------------------------------
    # These rows carry NestedPublic/NestedPrivate visibility, which is only legal
    # for a type that really is nested. None of them are (NestedClass is empty).
    # Top-level types must use Public/NotPublic. Clearing the "nested" visibility
    # bits costs one byte per row, needs no heap growth and no PE relocation.
    if '--visibility' in argv:
        VIS_MASK = 0x7
        vis_plan = []
        for idxs in coll.values():
            for rid in idxs:
                flags = rows[rid - 1][0]
                if (flags & VIS_MASK) in (2, 3, 4, 5, 6, 7):   # any Nested*
                    vis_plan.append((rid, flags))
        print('visibility-only mode: %d rows have nested visibility' % len(vis_plan))

        if dry:
            for rid, fl in vis_plan[:10]:
                print('   row %-6d flags=0x%X vis=%d -> Public' % (rid, fl, fl & VIS_MASK))
            print('\n[dry-run] nothing written')
            return 0

        td_off = r['table_offsets'][T_TYPEDEF]
        row_width = sum(r['layouts'][T_TYPEDEF])
        for rid, fl in vis_plan:
            col = md + td_off + (rid - 1) * row_width      # Flags is column 0
            new_flags = (fl & ~VIS_MASK) | 0x1            # -> Public
            struct.pack_into('<I', b, col, new_flags)

        out_path = src if in_place else os.path.splitext(src)[0] + '.fixed.dll'
        if not src.endswith('.fixed.dll') and not os.path.exists(src + '.bak'):
            shutil.copy2(src, src + '.bak')
            print('backup: %s.bak' % src)
        with open(out_path, 'wb') as fh:
            fh.write(bytes(b))
        print('written: %s (%d bytes, size unchanged: %s)'
              % (out_path, len(b), len(b) == len(orig)))
        return 0

    new_blob = bytearray()
    idx_of = {}
    for rid in sorted(assignments):
        idx_of[rid] = s_size + len(new_blob)
        new_blob += assignments[rid].encode('utf-8') + b'\0'

    # ---- placement --------------------------------------------------------
    # The new names must live inside .text's *mapped* range: the CLR reads the
    # metadata through the section's virtual address space, so bytes appended past
    # the section's mapped size are unreadable and yield "Bad IL format".
    #
    # Rather than bolt a new section on, insert the bytes into .text's raw data
    # and shift the following section(s) down:
    #
    #   insert_at   = text_rawptr + text_rawsize      (end of .text raw data)
    #   pad to FILE_ALIGN so every later section stays aligned
    #   .text.RawSize += inserted
    #   .text.VirtualSize = mapped end, rounded up to SECT_ALIGN
    #   every later section: PointerToRawData += inserted
    #
    # The insertion point is FILE_ALIGN aligned and inside .text's VA range, so
    # the string index band starting at s_size maps contiguously and no index
    # recomputation is needed.
    text_sec = None
    for i in range(nsec):
        s = sec + i * 40
        if b[s:s + 8].rstrip(b'\0') == b'.text':
            text_sec = s
    if text_sec is None:
        raise RuntimeError('.text section not found')
    text_va = u32(b, text_sec + 12)
    text_vsize = u32(b, text_sec + 8)
    text_rawptr = u32(b, text_sec + 20)
    text_rawsize = u32(b, text_sec + 16)
    text_raw_end = text_rawptr + text_rawsize

    insert_at = text_raw_end
    pad = (-len(new_blob)) % FILE_ALIGN
    inserted = len(new_blob) + pad
    new_vsize = max(text_vsize, (insert_at - text_rawptr) + inserted)
    mapped_end = (text_va + new_vsize + SECT_ALIGN - 1) & ~(SECT_ALIGN - 1)

    # refuse if this would collide with the next section's VA
    next_va = None
    for i in range(nsec):
        s = sec + i * 40
        va = u32(b, s + 12)
        if va > text_va and (next_va is None or va < next_va):
            next_va = va
    ok_vs = next_va is None or text_va + new_vsize <= next_va
    print('\nplacement:')
    print('  insert at (end of .text raw) : 0x%X' % insert_at)
    print('  string bytes / padding       : %d / %d' % (len(new_blob), pad))
    print('  .text RawSize 0x%X -> 0x%X' % (text_rawsize, text_rawsize + inserted))
    print('  .text VSize   0x%X -> 0x%X  (next section VA: %s)'
          % (text_vsize, new_vsize, ('0x%X' % next_va) if next_va else '-'))
    print('  no VA collision with next section: %s' % ok_vs)

    if not ok_vs:
        print('FAILED: .text would collide with the next section; no file written.')
        return 1

    print('TypeDef rows          : %d' % len(rows))
    print('collision groups      : %d' % len(coll))
    print('rows to rename        : %d' % len(assignments))
    print('metadata blob ends    : 0x%X' % blob_end)

    for rid in sorted(assignments)[:8]:
        print('   row %-6d %-34s -> %s'
              % (rid, heaps.string(rows[rid - 1][NAME_I])[:34], assignments[rid]))
    if len(assignments) > 8:
        print('   ... and %d more' % (len(assignments) - 8))

    if dry:
        print('\n[dry-run] nothing written')
        return 0

    # ---- insert the strings and shift later sections ---------------------
    b[insert_at:insert_at] = new_blob + b'\0' * pad
    for i in range(nsec):
        s = sec + i * 40
        rp = u32(b, s + 20)
        if rp >= insert_at and rp != 0:
            struct.pack_into('<I', b, s + 20, rp + inserted)
    struct.pack_into('<I', b, text_sec + 8, new_vsize)                 # VSize
    struct.pack_into('<I', b, text_sec + 16, text_rawsize + inserted)  # RawSize
    struct.pack_into('<I', b, opt + 56,
                     max(u32(b, opt + 56), mapped_end))                # SizeOfImage
    print('  inserted %d bytes; file now %d bytes, SizeOfImage 0x%X'
          % (inserted, len(b), u32(b, opt + 56)))

    # ---- grow the logical #Strings heap ----------------------------------
    # Stream offsets are relative to the metadata root the parser walked (`md`).
    # The CLR resolves strings through the same root, so extending the Size dword
    # there is what makes the appended names visible.
    struct.pack_into('<I', b, strings_size_field, s_size + len(new_blob))

    # ---- repoint TypeDef.Name (geometry from the proven parser) ----------
    # table_offsets are relative to the metadata root, and `rows` was parsed with
    # that same root, so the column address is root + table_off + row*width + col.
    td_off = r['table_offsets'][T_TYPEDEF]
    row_width = sum(r['layouts'][T_TYPEDEF])
    name_col = r['layouts'][T_TYPEDEF][0]
    str_w = r['str_w']
    for rid, idx in idx_of.items():
        col = md + td_off + (rid - 1) * row_width + name_col
        if str_w == 4:
            struct.pack_into('<I', b, col, idx)
        else:
            struct.pack_into('<H', b, col, idx)

    # Heap-index width flags live in the #~ stream header. If any string index we
    # just wrote exceeds 0xFFFF the 4-byte flag must be set, or the CLR would read
    # the Name column as 2-byte indices and see garbage.
    tds_abs = md + streams['#~'][0]
    if str_w == 4 and not (b[tds_abs + 6] & 0x01):
        b[tds_abs + 6] |= 0x01
        print('set #Strings 4-byte index flag in #~ header')

    out_path = src if in_place else os.path.splitext(src)[0] + '.fixed.dll'
    if not src.endswith('.fixed.dll') and not os.path.exists(src + '.bak'):
        shutil.copy2(src, src + '.bak')
        print('backup: %s.bak' % src)
    with open(out_path, 'wb') as fh:
        fh.write(bytes(b))
    print('written: %s (%d bytes, size unchanged: %s)'
          % (out_path, len(b), len(b) == len(orig)))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
