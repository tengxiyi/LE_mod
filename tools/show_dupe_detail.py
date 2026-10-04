"""
Show the exact TypeDef rows behind a name collision, including generic arity
and flags, and report whether each colliding row is nested or top-level.

Usage: python show_dupe_detail.py <assembly.dll> <TypeName>
Read-only.
"""

import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from diagnose_typedef_dupes import analyse, T_TYPEDEF, T_NESTEDCLASS, T_FIELD, T_METHODDEF  # noqa: E402

NAME_I, NS_I, FLAGS_I, FIELD_I, METHOD_I = 1, 2, 0, 4, 5

# TypeAttributes visibility mask
VIS = {0: 'NotPublic', 1: 'Public', 2: 'NestedPublic', 3: 'NestedPrivate',
       4: 'NestedFamily', 5: 'NestedAssembly', 6: 'NestedFamANDAssem',
       7: 'NestedFamORAssem'}


def detail(path, want):
    r = analyse(path)
    td, heaps = r['rows'][T_TYPEDEF], r['heaps']
    nc = r['rows'].get(T_NESTEDCLASS, [])
    nested = {row[0] for row in nc}

    hits = []
    for i, row in enumerate(td, start=1):
        nm = heaps.string(row[NAME_I])
        ns = heaps.string(row[NS_I])
        if nm == want or nm.startswith(want + '`'):
            hits.append((i, nm, ns, row[FLAGS_I]))

    print('=' * 78)
    print('FILE : %s' % os.path.basename(path))
    print("NAME : %r  -> %d TypeDef row(s)" % (want, len(hits)))
    for (i, nm, ns, flags) in hits:
        vis = VIS.get(flags & 0x7, '?')
        arity = nm.split('`')[1] if '`' in nm else '0'
        print('  row %-5d name=%-28s ns=%-14s arity=%s vis=%-16s nested=%s'
              % (i, nm, ns or '<global>', arity, vis, i in nested))
    return len(hits)


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    for path in argv[1:]:
        try:
            detail(path, argv[1] if False else sys.argv[2])
        except Exception as exc:  # noqa: BLE001
            print('FAILED on %s: %s: %s' % (path, type(exc).__name__, exc))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
