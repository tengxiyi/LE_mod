// FixCoreModuleCecil - resolve duplicate top-level TypeDef names in an
// Il2CppInterop-generated assembly, using Mono.Cecil for all metadata rewriting.
//
// Why this exists
// ---------------
// Since Unity 6000.4 (IL2CPP metadata v39), Cpp2IL 2022.1.0-pre-release.21 emits
// no NestedClass table. Compiler-generated NESTED types therefore collapse into
// same-named TOP-LEVEL types, and the CLR rejects the assembly:
//
//     BadImageFormatException: Duplicate type with name '<>O'
//
// Measured with tools/LoadProbe: of 213 assemblies in MelonLoader\Il2CppAssemblies
// exactly ONE fails -- UnityEngine.CoreModule.dll -- with 201 colliding TypeDef
// rows in 68 groups. None is referenced by any TypeRef, ExportedType or
// NestedClass row, and all collide in the global namespace.
//
// The CLR keys top-level types purely on (namespace, name, arity) -- verified by
// experiment: fixing only the visibility flags does NOT satisfy it, the names
// must be unique. So we rename the colliding duplicates and, because they are no
// longer nested, clear their Nested* visibility.
//
// Doing this by hand means growing the #Strings heap, which has zero free space
// and is immediately followed by #US. Mono.Cecil rebuilds the heaps and remaps
// every index for us, so this program is only the policy layer.
//
// Usage:
//     FixCoreModuleCecil <assembly.dll> [-o <out.dll>] [--dry-run] [--in-place]
//
// Default output: <name>.fixed.dll next to the input. Read-only unless it writes
// the output file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private const int VisibilityMask = 0x7;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: FixCoreModuleCecil <assembly.dll> [-o <out.dll>] [--dry-run] [--in-place]");
            return 2;
        }

        string input = args[0];
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("no such file: " + input);
            return 2;
        }

        bool dryRun = args.Contains("--dry-run");
        bool inPlace = args.Contains("--in-place");

        string output = input;
        int oIdx = Array.IndexOf(args, "-o");
        if (oIdx >= 0 && oIdx + 1 < args.Length) output = args[oIdx + 1];
        else if (!inPlace) output = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + ".fixed.dll");

        var asm = AssemblyDefinition.ReadAssembly(input);
        var module = asm.MainModule;

        Console.WriteLine($"input                : {input}");
        Console.WriteLine($"assembly             : {asm.Name.Name} {asm.Name.Version}");
        Console.WriteLine($"TypeDef rows         : {module.Types.Count}");

        // ---- find collisions on the CLR's identity key: (namespace, name) ----
        var groups = new Dictionary<(string, string), List<TypeDefinition>>();
        foreach (var t in module.Types)
        {
            var key = (t.Namespace ?? string.Empty, t.Name);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<TypeDefinition>();
            list.Add(t);
        }

        var colliding = groups.Where(g => g.Value.Count > 1)
                              .OrderByDescending(g => g.Value.Count)
                              .ToList();

        Console.WriteLine($"collision groups     : {colliding.Count}");
        Console.WriteLine($"rows in groups       : {colliding.Sum(g => g.Value.Count)}");

        // sanity: nothing outside the global namespace should collide here
        var nonGlobal = colliding.Where(g => g.Key.Item1.Length != 0).ToList();
        if (nonGlobal.Count > 0)
            Console.WriteLine($"  note: {nonGlobal.Count} colliding group(s) are namespaced");

        // ---- plan renames: keep the first of each group -----------------------
        var plans = new List<(TypeDefinition Type, string Old, string New)>();
        var taken = new HashSet<string>(module.Types.Select(t => t.Namespace + "." + t.Name));

        foreach (var g in colliding)
        {
            for (int i = 1; i < g.Value.Count; i++)
            {
                var t = g.Value[i];
                string newName;
                int n = i;
                do
                {
                    newName = $"M{t.MetadataToken.RID}_{n}";
                    n++;
                }
                while (!taken.Add(t.Namespace + "." + newName));
                plans.Add((t, t.Name, newName));
            }
        }

        Console.WriteLine($"rows to rename       : {plans.Count}");
        foreach (var p in plans.Take(8))
            Console.WriteLine($"   rid {p.Type.MetadataToken.RID,-6} {p.Old,-34} -> {p.New}");
        if (plans.Count > 8) Console.WriteLine($"   ... and {plans.Count - 8} more");

        if (dryRun)
        {
            Console.WriteLine("\n[dry-run] nothing written");
            return 0;
        }

        // ---- apply -----------------------------------------------------------
        int visFixed = 0;
        foreach (var p in plans)
        {
            var t = p.Type;
            t.Name = p.New;

            // A nested type's visibility is illegal on a top-level type, and these
            // rows are all NestedPublic/NestedPrivate. Make them ordinary.
            var vis = (int)(t.Attributes & (TypeAttributes)VisibilityMask);
            if (vis != 0 && vis != 1)          // 0 = NotPublic, 1 = Public
            {
                t.Attributes = (t.Attributes & ~(TypeAttributes)VisibilityMask)
                               | TypeAttributes.Public;
                visFixed++;
            }
        }
        Console.WriteLine($"visibility normalised: {visFixed}");

        if (!inPlace && File.Exists(output))
        {
            string bak = output + ".bak";
            if (!File.Exists(bak)) File.Copy(output, bak);
        }

        asm.Write(output);
        Console.WriteLine($"written              : {output} ({new FileInfo(output).Length} bytes)");
        Console.WriteLine();
        Console.WriteLine("next: verify with tools/LoadProbe --only fixed");
        return 0;
    }
}
