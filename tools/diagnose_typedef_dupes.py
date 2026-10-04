"""
Diagnose duplicate TypeDef names in a generated Il2CppInterop assembly.

Pure-stdlib ECMA-335 reader: locates the CLI metadata, parses the #~ table
stream header, computes column widths from the heap sizes, and reports every
TypeDef whose name collides with another TypeDef in the same assembly.

Usage:
    python diagnose_typedef_dupes.py <assembly.dll> [<assembly.dll> ...]

Only reads files. Makes no modifications.
"""

import sys
import struct
from collections import defaultdict

# --- Table indices (ECMA-335 II.22) -----------------------------------------
T_MODULE, T_TYPEREF, T_TYPEDEF, T_FIELDPTR, T_FIELD, T_METHODPTR, T_METHODDEF = range(7)
T_PARAM, T_INTERFACEIMPL, T_MEMBERREF, T_CONSTANT, T_CUSTOMATTRIBUTE = range(7, 12)
T_FIELDMARSHAL, T_DECLSECURITY, T_CLASSLAYOUT, T_FIELDLAYOUT = range(12, 16)
T_STANDALONESIG, T_EVENTMAP, T_EVENTPTR, T_EVENT = range(16, 20)
T_PROPERTYMAP, T_PROPERTYPTR, T_PROPERTY, T_METHODSEMANTICS = range(20, 24)
T_METHODIMPL, T_MODULEREF, T_TYPESPEC, T_IMPLMAP = range(24, 28)
T_FIELDRVA, T_ENCLOG, T_ENCMAP, T_ASSEMBLY = range(28, 32)
T_ASSEMBLYPROCESSOR, T_ASSEMBLYOS, T_ASSEMBLYREF, T_ASSEMBLYREFFILE = range(32, 36)
T_EXPORTEDTYPE, T_MANIFESTRESOURCE, T_NESTEDCLASS = range(36, 39)
T_GENERICPARAM, T_METHODSPEC, T_GENERICPARAMCONSTRAINT = range(39, 42)

# Column layout per table: list of tokens. Each token is one of
#   'const:<n>'   fixed-width integer
#   <heapspec>    an index into a heap: 'string', 'guid', 'blob'
#   '<table>:<coded>'  a coded index into one or more tables
#   '<table>'     a simple index into the named table
#
# Only the tables needed to walk TypeDef are fully specified; the rest use the
# 'other' catch-all below, which is fine because we decode row-by-row using the
# real layout table.
TABLES = {
    T_MODULE: [('const:2',), ('string',), ('guid',), ('guid',), ('guid',)],
    T_TYPEREF: [('ResolutionScope',), ('string',), ('string',)],
    T_TYPEDEF: [('const:4',), ('string',), ('string',), ('TypeDefOrRef',), (T_FIELD,), (T_METHODDEF,)],
    T_FIELDPTR: [(T_FIELD,)],
    T_FIELD: [('const:2',), ('string',), ('blob',)],
    T_METHODPTR: [(T_METHODDEF,)],
    T_METHODDEF: [('const:4',), ('const:2',), ('const:2',), ('string',), ('blob',), (T_PARAM,)],
    T_PARAM: [('const:2',), ('const:2',), ('string',)],
    T_INTERFACEIMPL: [(T_TYPEDEF,), ('TypeDefOrRef',)],
    T_MEMBERREF: [('MemberRefParent',), ('string',), ('blob',)],
    T_CONSTANT: [('const:1',), ('const:1',), ('HasConstant',), ('blob',)],
    T_CUSTOMATTRIBUTE: [('HasCustomAttribute',), ('CustomAttributeType',), ('blob',)],
    T_FIELDMARSHAL: [('HasFieldMarshal',), ('blob',)],
    T_DECLSECURITY: [('const:2',), ('HasDeclSecurity',), ('blob',)],
    T_CLASSLAYOUT: [('const:2',), ('const:4',), (T_TYPEDEF,)],
    T_FIELDLAYOUT: [('const:4',), (T_FIELD,)],
    T_STANDALONESIG: [('blob',)],
    T_EVENTMAP: [(T_TYPEDEF,), (T_EVENT,)],
    T_EVENTPTR: [(T_EVENT,)],
    T_EVENT: [('const:2',), ('string',), ('TypeDefOrRef',)],
    T_PROPERTYMAP: [(T_TYPEDEF,), (T_PROPERTY,)],
    T_PROPERTYPTR: [(T_PROPERTY,)],
    T_PROPERTY: [('const:2',), ('string',), ('blob',)],
    T_METHODSEMANTICS: [('const:2',), (T_METHODDEF,), ('HasSemantics',)],
    T_METHODIMPL: [(T_TYPEDEF,), ('MethodDefOrRef',), ('MethodDefOrRef',)],
    T_MODULEREF: [('string',)],
    T_TYPESPEC: [('blob',)],
    T_IMPLMAP: [('const:2',), ('MemberForwarded',), ('string',), (T_MODULEREF,)],
    T_FIELDRVA: [('const:4',), (T_FIELD,)],
    T_ENCLOG: [('const:4',), ('const:4',)],
    T_ENCMAP: [('const:4',), ('const:4',)],
    T_ASSEMBLY: [('const:4',), ('const:2',), ('const:2',), ('const:2',), ('const:2',), ('const:4',), ('blob',), ('string',), ('string',)],
    T_ASSEMBLYPROCESSOR: [('const:4',)],
    T_ASSEMBLYOS: [('const:4',)],
    T_ASSEMBLYREF: [('const:2',), ('const:2',), ('const:2',), ('const:2',), ('const:4',), ('blob',), ('string',), ('string',), ('blob',)],
    T_ASSEMBLYREFFILE: [('const:4',), ('string',), ('blob',)],
    T_EXPORTEDTYPE: [('const:4',), ('const:4',), ('string',), ('string',), ('Implementation',)],
    T_MANIFESTRESOURCE: [('const:4',), ('const:4',), ('string',), ('Implementation',)],
    T_NESTEDCLASS: [(T_TYPEDEF,), (T_TYPEDEF,)],
    T_GENERICPARAM: [('const:2',), ('const:2',), ('TypeOwner',), ('string',)],
    T_METHODSPEC: [('MethodDefOrRef',), ('blob',)],
    T_GENERICPARAMCONSTRAINT: [(T_TYPEDEF,), ('TypeDefOrRef',)],
}

# Coded-index definitions: (tables, tag_bits)
CODED = {
    'TypeDefOrRef': ([T_TYPEDEF, T_TYPEREF, T_TYPESPEC], 2),
    'HasConstant': ([T_FIELD, T_PARAM, T_PROPERTY], 2),
    'HasCustomAttribute': ([T_METHODDEF, T_FIELD, T_TYPEREF, T_TYPEDEF, T_PARAM,
                            T_INTERFACEIMPL, T_MEMBERREF, T_MODULE, T_DECLSECURITY,
                            T_PROPERTY, T_EVENT, T_STANDALONESIG, T_MODULEREF,
                            T_MANIFESTRESOURCE, T_GENERICPARAM, T_GENERICPARAMCONSTRAINT,
                            T_METHODSPEC, T_ASSEMBLY], 5),
    'HasFieldMarshal': ([T_FIELD, T_PARAM], 1),
    'HasDeclSecurity': ([T_TYPEDEF, T_METHODDEF, T_ASSEMBLY], 2),
    'MemberRefParent': ([T_TYPEDEF, T_TYPEREF, T_MODULEREF, T_METHODDEF, T_TYPESPEC], 3),
    'HasSemantics': ([T_EVENT, T_PROPERTY], 1),
    'MethodDefOrRef': ([T_METHODDEF, T_MEMBERREF], 1),
    'MemberForwarded': ([T_FIELD, T_METHODDEF], 1),
    'Implementation': ([T_ASSEMBLYREFFILE, T_EXPORTEDTYPE], 1),
    'CustomAttributeType': ([0, 0, T_METHODDEF, T_MEMBERREF, 0], 3),
    'ResolutionScope': ([T_MODULE, T_MODULEREF, T_ASSEMBLYREF, T_TYPEREF], 2),
    'TypeOwner': ([T_TYPEDEF, T_TYPEREF, T_METHODDEF], 2),
}


def u16(b, o):
    return struct.unpack_from('<H', b, o)[0]


def u32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def read_pe_metadata(path):
    """Locate and return (metadata_root_offset, file_bytes)."""
    with open(path, 'rb') as fh:
        b = fh.read()
    if b[:2] != b'MZ':
        raise ValueError('not a PE file (no MZ)')
    pe = u32(b, 0x3C)
    if b[pe:pe + 4] != b'PE\0\0':
        raise ValueError('bad PE signature')
    nsec = u16(b, pe + 6)
    optsz = u16(b, pe + 20)
    opt = pe + 24
    magic = u16(b, opt)
    # Data directory 14 = CLR runtime header
    dd = opt + (96 if magic == 0x10B else 112)
    clr_rva = u32(b, dd + 14 * 8)
    clr_size = u32(b, dd + 14 * 8 + 4)
    if clr_rva == 0:
        raise ValueError('no CLR header (not a managed assembly)')
    # Section table -> RVA to file offset
    sec = opt + optsz
    def rva2off(rva):
        for i in range(nsec):
            s = sec + i * 40
            vsize = u32(b, s + 8)
            vaddr = u32(b, s + 12)
            rawsz = u32(b, s + 16)
            rawptr = u32(b, s + 20)
            if vaddr <= rva < vaddr + max(vsize, rawsz):
                return rawptr + (rva - vaddr)
        raise ValueError('RVA 0x%X not mapped' % rva)
    clr = rva2off(clr_rva)
    meta_rva = u32(b, clr + 8)
    return rva2off(meta_rva), b


def parse_streams(b, md):
    """Return {name: (offset_relative_to_md, size)} for each metadata stream.

    ECMA-335 II.24.2.1 root layout (verified byte-for-byte against the generated
    assemblies):

        md + 0    Signature 'BSJB'
        md + 4    MajorVersion(1)  MinorVersion(1)
        md + 6    Reserved (4 bytes)
        md + 10   VersionLength (4)
        md + 14   Version string, padded to the next 4-byte boundary
        then      Flags(2) Streams(2)
        then      per stream:  Offset(4) Size(4) Name(variable ASCII,
                  null terminated, padded to the next 4-byte boundary)

    The name is a null-terminated, 4-byte aligned string - not a fixed 4-byte
    field. Reading it as fixed-width consumes bytes that belong to the next
    entry's Offset, which silently desynchronises every following stream.
    """
    if b[md:md + 4] != b'BSJB':
        raise ValueError('metadata signature BSJB not found')
    ver_len = u32(b, md + 12)
    p = (md + 16 + ver_len + 3) & ~3
    nstreams = u16(b, p + 2)
    if nstreams == 0 or nstreams > 8:
        raise ValueError('implausible stream count %d at md=0x%X' % (nstreams, md))
    p += 4
    streams = {}
    for _ in range(nstreams):
        off = u32(b, p)
        size = u32(b, p + 4)
        p += 8
        end = b.index(b'\0', p)
        name = b[p:end].decode('ascii', 'replace')
        streams[name] = (off, size)
        p = (end + 1 + 3) & ~3
    return streams


def find_stream_table(b, md):
    """Return (label, offset_of_stream_count) for the metadata root at `md`."""
    ver_len = u32(b, md + 12)
    p = (md + 16 + ver_len + 3) & ~3
    n = u16(b, p + 2)
    if 1 <= n <= 8:
        return 'spec', p + 2
    raise ValueError('could not locate metadata stream table at md=0x%X' % md)


class Heaps:
    """String/blob readers over stream offsets that are relative to `md`."""

    def __init__(self, b, streams, md=0):
        self.b = b
        self.md = md
        self.streams = streams
        self.strings = streams.get('#Strings')
        self.blob = streams.get('#Blob')
        self.guid = streams.get('#GUID')

    def string(self, idx):
        base, size = self.strings
        if idx >= size:
            return '<string-index-out-of-range>'
        start = self.md + base + idx
        end = self.b.index(b'\0', start)
        return self.b[start:end].decode('utf-8', 'replace')


def parse_tables(b, streams, md=0):
    """Return (rows, rowcounts, heap_sizes) where rows[t] is a list of tuples."""
    tds, tdsize = streams['#~']
    tds += md                       # stream offsets are relative to the root
    heap_sizes = b[tds + 6]
    valid = struct.unpack_from('<Q', b, tds + 8)[0]
    sorted_mask = struct.unpack_from('<Q', b, tds + 16)[0]
    p = tds + 24
    rowcounts = {}
    for t in range(64):
        if valid & (1 << t):
            rowcounts[t] = u32(b, p)
            p += 4
    # Heap index widths
    str_w = 4 if (heap_sizes & 0x01) else 2
    guid_w = 4 if (heap_sizes & 0x02) else 2
    blob_w = 4 if (heap_sizes & 0x04) else 2

    def simple_w(t):
        return 4 if rowcounts.get(t, 0) >= (1 << 16) else 2

    def coded_w(name):
        tables, bits = CODED[name]
        maxrows = max([rowcounts.get(t, 0) for t in tables if t] or [0])
        return 4 if maxrows >= (1 << (16 - bits)) else 2

    def col_w(spec):
        # Tolerate an accidental 1-tuple such as (T_FIELD,) used as a spec.
        if isinstance(spec, tuple):
            spec = spec[0]
        if isinstance(spec, int):
            return simple_w(spec)
        if spec.startswith('const:'):
            return int(spec.split(':')[1])
        if spec == 'string':
            return str_w
        if spec == 'guid':
            return guid_w
        if spec == 'blob':
            return blob_w
        if spec in CODED:
            return coded_w(spec)
        raise ValueError('unknown column spec %r' % (spec,))

    layouts = {}
    for t in range(64):
        if valid & (1 << t) and t in TABLES:
            layouts[t] = [col_w(spec) for spec in TABLES[t]]

    rows = {}
    heaps = Heaps(b, streams, md)
    skipped = []
    table_offsets = {}
    table_data_start = p
    for t in sorted(rowcounts):
        if t not in layouts:
            # No layout for this table, so every table after it would be
            # misaligned. Stop walking and report which table blocked us.
            skipped = [k for k in sorted(rowcounts) if k >= t]
            break
        widths = layouts[t]
        roww = sum(widths)
        n = rowcounts[t]
        table_offsets[t] = p
        out = []
        base = p
        for i in range(n):
            off = base + i * roww
            vals = []
            o = off
            for w in widths:
                vals.append(u32(b, o) if w == 4 else u16(b, o))
                o += w
            out.append(tuple(vals))
        rows[t] = out
        p += n * roww
    if skipped:
        print('  [warn] no column layout for table(s) %s; walked %d table(s) '
              '(TypeDef readable: %s)'
              % (skipped, len(rows), T_TYPEDEF in rows))
    return rows, rowcounts, (str_w, guid_w, blob_w), heaps, layouts, table_data_start, table_offsets


def analyse(path):
    md, b = read_pe_metadata(path)
    streams = parse_streams(b, md)
    (rows, rowcounts, widths, heaps,
     layouts, table_data_start, table_offsets) = parse_tables(b, streams, md)
    str_w = widths[0]
    # TypeDef row = (Flags, Name, Namespace, Extends, FieldList, MethodList)
    name_i = 1
    ns_i = 2
    by_name = defaultdict(list)
    for idx, row in enumerate(rows[T_TYPEDEF], start=1):
        nm = heaps.string(row[name_i])
        ns = heaps.string(row[ns_i])
        by_name[(ns, nm)].append(idx)
    dupes = {k: v for k, v in by_name.items() if len(v) > 1}
    return {
        'path': path,
        'rows': rows,
        'heaps': heaps,
        'typedef_count': len(rows[T_TYPEDEF]),
        'typeref_count': len(rows.get(T_TYPEREF, [])),
        'dupes': dupes,
        'layouts': layouts,
        'table_data_start': table_data_start,
        'table_offsets': table_offsets,
        'str_w': str_w,
    }


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    rc = 0
    for path in argv[1:]:
        print('=' * 78)
        print('FILE:', path)
        try:
            r = analyse(path)
        except Exception as exc:  # noqa: BLE001 - CLI diagnostics
            print('  FAILED to parse:', type(exc).__name__, exc)
            rc = 1
            continue
        print('  TypeDef rows : %d' % r['typedef_count'])
        print('  TypeRef rows : %d' % r['typeref_count'])
        print('  Distinct type names : %d' % (r['typedef_count'] - sum(len(v) - 1 for v in r['dupes'].values())))
        print('  DUPLICATE names     : %d' % len(r['dupes']))
        if r['dupes']:
            for (ns, nm), idxs in sorted(r['dupes'].items())[:40]:
                print('    %-40s ns=%-22s rows=%s' % (nm, ns or '<global>', idxs))
            if len(r['dupes']) > 40:
                print('    ... and %d more' % (len(r['dupes']) - 40))
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv))
