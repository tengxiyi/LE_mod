// DumpEnum.cs - print a .NET enum's members and their numeric values.
//
// Used to resolve UnityEngine.KeyCode constants exactly, from the game's own assemblies, instead
// of inferring them.
//
// Usage: DumpEnum <assembly.dll> <EnumFullName|substring>

using System;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: DumpEnum <assembly.dll> <enum>"); return 2; }

        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var candidates = asm.MainModule.GetTypes()
            .Where(t => t.IsEnum && t.FullName.Contains(args[1], StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($"no enum matching '{args[1]}' in {args[0]}");
            return 1;
        }

        foreach (var e in candidates)
        {
            // Mono.Cecil has no GetEnumUnderlyingType(); the backing field is named `value__`.
            var backing = e.Fields.FirstOrDefault(f => f.Name == "value__");
            Console.WriteLine($"=== {e.FullName}  (underlying {backing?.FieldType.Name ?? "?"}) ===");
            foreach (var f in e.Fields.Where(f => f.HasConstant))
                Console.WriteLine($"   {Convert.ToInt64(f.Constant),6}  {f.Name}");
            Console.WriteLine();
        }
        return 0;
    }
}
