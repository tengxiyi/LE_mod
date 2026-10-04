// FindMissingRefs - find types/methods in an assembly that reference type names
// which no longer exist in the interop assemblies.
//
// Motivation: after a game update some Il2Cpp types vanish (here the Prophecy /
// Constellation UI was rebuilt, so Il2CppLE.Factions.ConstellationStar is gone).
// A mod compiled against the old API still loads, but any method that touches a
// removed type throws TypeLoadException when it runs. For a method called from
// OnGUI that means once per frame, which floods the log.
//
// This scans statically so the workaround can target exact method names instead
// of guessing.
//
// Usage:
//   FindMissingRefs <mod.dll> <interopDir> [--root <namespacePrefix>]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: FindMissingRefs <mod.dll> <interopDir> [--root <ns>]");
            return 2;
        }

        string modPath = args[0];
        string interopDir = args[1];
        string root = null;
        int r = Array.IndexOf(args, "--root");
        if (r >= 0 && r + 1 < args.Length) root = args[r + 1];

        // ---- build the set of type names that DO exist in the interop layer ----
        var known = new HashSet<string>(StringComparer.Ordinal);
        var interopAsms = new List<AssemblyDefinition>();
        foreach (var f in Directory.GetFiles(interopDir, "*.dll"))
        {
            try
            {
                var a = AssemblyDefinition.ReadAssembly(f);
                interopAsms.Add(a);
                foreach (var t in a.MainModule.GetTypes())
                    known.Add(a.Name.Name + "|" + t.FullName.Replace('/', '+'));
            }
            catch { /* skip unreadable */ }
        }
        Console.WriteLine($"interop assemblies : {interopAsms.Count}");
        Console.WriteLine($"known interop types: {known.Count}");

        // ---- scan the mod ------------------------------------------------------
        var mod = AssemblyDefinition.ReadAssembly(modPath);
        Console.WriteLine($"mod                : {mod.Name.Name}");

        // Map each TypeReference in the mod to "assembly|fullname" and see if it
        // resolves. TypeReferences carry a Scope that names the target assembly.
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);   // key -> display
        foreach (var tr in mod.MainModule.GetTypeReferences())
        {
            string scope = tr.Scope?.Name ?? "";
            if (scope.Length == 0) continue;
            // only judge references into assemblies we actually have
            if (!known.Any(k => k.StartsWith(scope + "|", StringComparison.Ordinal))) continue;
            string key = scope + "|" + tr.FullName.Replace('/', '+');
            if (!known.Contains(key))
                missing[key] = scope + " :: " + tr.FullName.Replace('/', '+');
        }

        Console.WriteLine();
        Console.WriteLine($"MISSING interop types referenced by the mod: {missing.Count}");
        foreach (var m in missing.Values.OrderBy(s => s)) Console.WriteLine("   " + m);

        // ---- attribute them to methods ----------------------------------------
        var guilty = new List<(string Type, string Method)>();
        foreach (var t in mod.MainModule.GetTypes())
        {
            bool typeSelfMissing = false;
            try { typeSelfMissing = t.BaseType != null && IsMissing(t.BaseType, missing); }
            catch { }

            foreach (var m in t.Methods)
            {
                bool hit = false;
                try
                {
                    if (m.ReturnType != null && IsMissing(m.ReturnType, missing)) hit = true;
                    foreach (var p in m.Parameters) if (IsMissing(p.ParameterType, missing)) hit = true;
                    if (!hit && m.HasBody)
                    {
                        foreach (var v in m.Body.Variables) if (IsMissing(v.VariableType, missing)) { hit = true; break; }
                    }
                }
                catch { }
                if (hit || typeSelfMissing) guilty.Add((t.FullName, m.Name));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"methods touching a missing type: {guilty.Count}");
        if (root != null)
            guilty = guilty.Where(g => g.Type.StartsWith(root, StringComparison.Ordinal)).ToList();

        foreach (var g in guilty.OrderBy(g => g.Type).ThenBy(g => g.Method).Take(60))
            Console.WriteLine($"   {g.Type}::{g.Method}");
        if (guilty.Count > 60) Console.WriteLine($"   ... and {guilty.Count - 60} more");

        // ---- the ones Unity calls every frame ---------------------------------
        var perFrame = guilty.Where(g =>
            g.Method is "OnGUI" or "OnGui" or "Update" or "LateUpdate" or "FixedUpdate" or "OnRenderObject")
            .ToList();
        Console.WriteLine();
        Console.WriteLine("PER-FRAME offenders (these flood the log):");
        foreach (var g in perFrame) Console.WriteLine($"   {g.Type}::{g.Method}");
        if (perFrame.Count == 0) Console.WriteLine("   (none)");
        return 0;
    }

    private static bool IsMissing(TypeReference tr, Dictionary<string, string> missing)
    {
        if (tr == null) return false;
        while (tr is TypeSpecification spec) tr = spec.ElementType;
        if (tr is GenericInstanceType git)
        {
            if (IsMissing(git.ElementType, missing)) return true;
            return git.GenericArguments.Any(a => IsMissing(a, missing));
        }
        string scope = tr.Scope?.Name ?? "";
        if (scope.Length == 0) return false;
        return missing.ContainsKey(scope + "|" + tr.FullName.Replace('/', '+'));
    }
}
