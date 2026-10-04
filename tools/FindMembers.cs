// FindMembers - list all types in an assembly that declare a member (field/property/method)
// whose name contains the given substring. For locating which game type owns an interesting
// field found by raw-string search.
//
// Usage: FindMembers <assembly.dll> <substring> [<substring> ...]

using System;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: FindMembers <assembly.dll> <substr> [...]"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);

        for (int i = 1; i < args.Length; i++)
        {
            string needle = args[i];
            Console.WriteLine();
            Console.WriteLine($"### '{needle}'");

            foreach (var t in asm.MainModule.GetTypes())
            {
                var names = new System.Collections.Generic.List<string>();
                names.AddRange(t.Fields.Select(f => f.Name));
                names.AddRange(t.Properties.Select(p => p.Name));
                names.AddRange(t.Methods.Select(m => m.Name));

                var typeHit = t.FullName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                var hits = names.Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                                .Distinct().OrderBy(n => n).ToList();
                if (typeHit)
                {
                    Console.WriteLine($"   [type] {t.FullName}");
                    foreach (var f in t.Fields) Console.WriteLine($"        field    {f.FieldType.Name} {f.Name}");
                    foreach (var p in t.Properties) Console.WriteLine($"        property {p.PropertyType.Name} {p.Name}");
                    foreach (var m in t.Methods) Console.WriteLine($"        method   {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.Name))})");
                }
                else if (hits.Count > 0)
                    Console.WriteLine($"   {t.FullName}  ->  {string.Join(" | ", hits)}");
            }
        }
        return 0;
    }
}
