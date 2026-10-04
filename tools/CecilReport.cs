// CecilReport - describe what Mono.Cecil sees inside an assembly, so we can tell
// how a Cecil round-trip changed the metadata.
//
// Usage: CecilReport <assembly.dll>

using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: CecilReport <assembly.dll>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var m = asm.MainModule;

        Console.WriteLine($"file                 : {args[0]}");
        Console.WriteLine($"assembly             : {asm.Name.Name} {asm.Name.Version}");
        Console.WriteLine($"module.Types.Count   : {m.Types.Count}   (top-level + <Module>)");
        Console.WriteLine($"GetTypes().Count     : {m.GetTypes().Count()}   (all incl. nested)");
        Console.WriteLine($"ExportedTypes.Count  : {m.ExportedTypes.Count}");
        Console.WriteLine($"TypeReferences.Count : {m.GetTypeReferences().Count()}");

        // collisions as Cecil sees them, on the full name
        var byFull = new Dictionary<string, List<TypeDefinition>>();
        foreach (var t in m.GetTypes())
        {
            string k = t.FullName;
            if (!byFull.TryGetValue(k, out var l)) byFull[k] = l = new List<TypeDefinition>();
            l.Add(t);
        }
        var dupFull = byFull.Where(kv => kv.Value.Count > 1).ToList();
        Console.WriteLine();
        Console.WriteLine($"collisions on FullName      : {dupFull.Count}");
        foreach (var kv in dupFull.Take(10))
            Console.WriteLine($"   {kv.Key} x{kv.Value.Count}  nested={kv.Value.Select(v=>v.IsNested).Distinct().Count()} distinct");

        // collisions as the CLR would see them: namespace + name + generic arity
        var byClr = new Dictionary<(string, string, int), List<TypeDefinition>>();
        foreach (var t in m.Types)          // top-level only
        {
            var k = (t.Namespace ?? "", t.Name, t.GenericParameters.Count);
            if (!byClr.TryGetValue(k, out var l)) byClr[k] = l = new List<TypeDefinition>();
            l.Add(t);
        }
        var dupClr = byClr.Where(kv => kv.Value.Count > 1).ToList();
        Console.WriteLine();
        Console.WriteLine($"CLR-identity collisions (top-level, ns+name+arity): {dupClr.Count}");
        foreach (var kv in dupClr.Take(10))
            Console.WriteLine($"   {kv.Key.Item1}|{kv.Key.Item2}`{kv.Key.Item3} x{kv.Value.Count}");

        // visibility histogram for top-level types
        var vis = m.Types.GroupBy(t => (int)(t.Attributes & (TypeAttributes)0x7))
                         .OrderByDescending(g => g.Count())
                         .Select(g => $"{VisName(g.Key)}={g.Count()}");
        Console.WriteLine();
        Console.WriteLine("top-level visibility: " + string.Join(", ", vis));

        int nestedStillPublic = m.Types.Count(t =>
            ((int)(t.Attributes & (TypeAttributes)0x7)) is 2 or 3 or 4 or 5 or 6 or 7);
        Console.WriteLine($"top-level types with Nested* visibility: {nestedStillPublic}");
        return 0;
    }

    private static string VisName(int v) => v switch
    {
        0 => "NotPublic", 1 => "Public", 2 => "NestedPublic", 3 => "NestedPrivate",
        4 => "NestedFamily", 5 => "NestedAssembly", 6 => "NestedFamANDAssem",
        7 => "NestedFamORAssem", _ => "?"
    };
}
