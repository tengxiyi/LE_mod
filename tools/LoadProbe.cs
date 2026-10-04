// LoadProbe: empirically test which generated Il2CppInterop assemblies the
// .NET runtime can actually load. Read-only.
//
// Usage: LoadProbe <directory> [--only <substring>]
//
// For every *.dll in <directory>, attempt AssemblyLoadContext.LoadFromAssemblyPath
// and record the outcome. Duplicate-type / invalid-metadata failures surface as
// BadImageFormatException, which is exactly the class of bug we are chasing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

sealed class ProbeContext : AssemblyLoadContext
{
    private readonly string _dir;
    public ProbeContext(string dir) : base("probe", isCollectible: false) { _dir = dir; }

    protected override Assembly? Load(AssemblyName name)
    {
        // Resolve dependencies from the same directory when possible; otherwise
        // return null so the default context can try. Returning null is fine:
        // metadata validation of the assembly under test happens on load.
        var candidate = Path.Combine(_dir, name.Name + ".dll");
        if (File.Exists(candidate))
        {
            try { return LoadFromAssemblyPath(candidate); }
            catch { return null; }
        }
        return null;
    }
}

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: LoadProbe <directory> [--only <substring>]");
            return 2;
        }

        string dir = args[0];
        string? only = null;
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] == "--only") only = args[i + 1];

        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine("no such directory: " + dir);
            return 2;
        }

        var files = Directory.GetFiles(dir, "*.dll")
                             .Where(f => only == null || Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .ToArray();

        Console.WriteLine($"probing {files.Length} assembly(ies) in {dir}");

        int ok = 0;
        var failures = new List<(string Name, string Ex, string Msg)>();

        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var ctx = new ProbeContext(dir);
            try
            {
                ctx.LoadFromAssemblyPath(f);
                ok++;
            }
            catch (Exception ex)
            {
                // unwrap to the innermost interesting exception
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                failures.Add((name, inner.GetType().Name, inner.Message));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"LOADED OK : {ok}/{files.Length}");
        Console.WriteLine($"FAILED    : {failures.Count}/{files.Length}");
        Console.WriteLine();

        if (failures.Count > 0)
        {
            Console.WriteLine("--- failures ---");
            foreach (var g in failures.GroupBy(x => x.Msg).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"[{g.Count()}x] {g.Key}");
                foreach (var item in g.Take(30)) Console.WriteLine($"        {item.Name}");
                if (g.Count() > 30) Console.WriteLine($"        ... and {g.Count() - 30} more");
            }
        }

        // machine-readable summary for scripting
        var outFile = Path.Combine(Path.GetTempPath(), "loadprobe_result.txt");
        File.WriteAllLines(outFile, failures.Select(x => x.Name + "\t" + x.Ex + "\t" + x.Msg));
        Console.WriteLine();
        Console.WriteLine("failure list written to: " + outFile);

        return failures.Count == 0 ? 0 : 1;
    }
}
