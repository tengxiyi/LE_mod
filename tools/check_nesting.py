"""
Check whether same-named TypeDefs are properly NESTED (legal) or top-level (illegal).

The CLR allows several types to share a name as long as each has a distinct
enclosing type; the NestedClass table is what records that. If an assembly has
duplicate top-level names, LoadFromAssemblyPath throws BadImageFormatException
with "Duplicate type with name ...".

Reports, per assembly:
  - TypeDef count
  - NestedClass row count
  - number of distinct (namespace, name) keys that collide
  - for each collision: how many of the colliding TypeDefs are nested vs top-level
  - whether any collision is between two TOP-LEVEL types (the fatal case)

Usage: python check_nesting.py <a.dll> [<b.dll> ...]
Read-only.
"""

import sys
import os
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from diagnose_typedef_dupes import (  # noqa: E402
    analyse, T_TYPEDEF, T_NESTEDCLASS,
)

NAME_I, NS_I = 1, 2


def check(path):
    r = analyse(path)
    rows = r['rows']
    heaps = r['heaps']
    td = rows[T_TYPEDEF]
    nc = rows.get(T_NESTEDCLASS, [])

    nested = set()
    for row in nc:
        nested.add(row[0])  # NestedClass: (NestedClass, EnclosingClass)

    def key(i):
        row = td[i - 1]
        return (heaps.string(row[NS_I]), heaps.string(row[NAME_I]))

    by_key = defaultdict(list)
    for i in range(1, len(td) + 1):
        by_key[key(i)].append(i)

    collisions = {k: v for k, v in by_key.items() if len(v) > 1}
    fatal = {}
    for k, idxs in collisions.items():
        tops = [i for i in idxs if i not in nested]
        if len(tops) > 1:
            fatal[k] = tops

    print('=' * 78)
    print('FILE : %s' % path)
    print('  TypeDef rows      : %d' % len(td))
    print('  NestedClass rows  : %d' % len(nc))
    print('  Types marked nested: %d (%.1f%%)'
          % (len(nested), 100.0 * len(nested) / max(1, len(td))))
    print('  Colliding name keys: %d' % len(collisions))
    print('  FATAL (2+ top-level with same name): %d' % len(fatal))
    if fatal:
        print('  --- fatal collisions (first 25) ---')
        for (ns, nm), tops in sorted(fatal.items())[:25]:
            print('    %-46s ns=%-20s top-level rows=%s' % (nm, ns or '<global>', tops))
        if len(fatal) > 25:
            print('    ... and %d more' % (len(fatal) - 25))
    return len(fatal)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    for path in argv[1:]:
        try:
            check(path)
        except Exception as exc:  # noqa: BLE001 - CLI diagnostics
            print('=' * 78)
            print('FILE : %s' % path)
            print('  FAILED: %s: %s' % (type(exc).__name__, exc))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
