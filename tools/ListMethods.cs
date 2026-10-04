// ListMethods - list every method of a type with its signature, so we can find
// the entry points worth porting before dumping any IL.
//
// Usage: ListMethods <assembly.dll> <TypeFullName|substring>

using System;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ListMethods <assembly.dll> <type>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var types = asm.MainModule.GetTypes()
            .Where(t => t.FullName.Contains(args[1], StringComparison.Ordinal))
            .OrderBy(t => t.FullName)
            .ToList();

        foreach (var t in types)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {t.FullName}  (kind={t.MetadataType}, base={t.BaseType?.Name}) ===");
            foreach (var f in t.Fields)
                Console.WriteLine($"   field  {f.FieldType.Name,-28} {f.Name}");
            foreach (var p in t.Properties)
                Console.WriteLine($"   prop   {p.PropertyType.Name,-28} {p.Name}");
            foreach (var m in t.Methods.OrderBy(m => m.Name))
            {
                if (m.IsGetter || m.IsSetter) continue;
                string ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
                string st = m.IsStatic ? "static " : "";
                Console.WriteLine($"   method {st}{m.ReturnType.Name,-16} {m.Name}({ps})   IL={(m.HasBody ? m.Body.Instructions.Count : 0)}");
            }
        }
        return 0;
    }
}
