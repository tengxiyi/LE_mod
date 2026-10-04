// TypeSearch - list types in an assembly whose full name matches a substring.
// Used to find what a renamed/removed game type became after a game update.
//
// Usage: TypeSearch <assembly.dll> <substring> [<substring> ...]

using System;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: TypeSearch <assembly.dll> <substr> [...]"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var all = asm.MainModule.GetTypes().ToList();
        Console.WriteLine($"assembly: {asm.Name.Name}   types: {all.Count}");

        for (int i = 1; i < args.Length; i++)
        {
            string needle = args[i];
            var hits = all.Where(t => t.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                          .Select(t => t.FullName)
                          .OrderBy(s => s)
                          .ToList();
            Console.WriteLine();
            Console.WriteLine($"### '{needle}' -> {hits.Count} match(es)");
            foreach (var h in hits.Take(40)) Console.WriteLine("   " + h);
            if (hits.Count > 40) Console.WriteLine($"   ... and {hits.Count - 40} more");
        }
        return 0;
    }
}
