// VerifyFixed - load an assembly with the CLR and confirm it is not just
// loadable but usable: types must still resolve by name.

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: VerifyFixed <dir> <assembly.dll>"); return 2; }
        string dir = args[0];
        string file = args[1];

        var ctx = new AssemblyLoadContext("verify", false);
        // The interop assemblies reference each other (and Il2Cppmscorlib), so
        // resolve from the same directory. Hook both the ALC and the default
        // context: the runtime asks the default one for some dependencies.
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolve = (c, n) =>
        {
            var p = System.IO.Path.Combine(dir, n.Name + ".dll");
            if (!System.IO.File.Exists(p)) return null;
            try { return c.LoadFromAssemblyPath(p); } catch { return null; }
        };
        ctx.Resolving += (c, n) => resolve(c, n);
        AssemblyLoadContext.Default.Resolving += (c, n) => resolve(c, n);

        Assembly a;
        string full = System.IO.Path.IsPathRooted(file)
            ? file
            : System.IO.Path.Combine(dir, file);
        try { a = ctx.LoadFromAssemblyPath(System.IO.Path.GetFullPath(full)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }

        Console.WriteLine($"LOADED   : {a.FullName}");
        Type[] types;
        try { types = a.GetTypes(); }
        catch (ReflectionTypeLoadException rtle)
        {
            Console.WriteLine($"GetTypes() partially failed; got {rtle.Types.Count(t => t != null)}");
            foreach (var e in rtle.LoaderExceptions.Where(e => e != null).Take(3))
                Console.WriteLine("   loader ex: " + e!.Message);
            types = rtle.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        Console.WriteLine($"types    : {types.Length}");
        Console.WriteLine($"  public : {types.Count(t => t.IsPublic || t.IsNestedPublic)}");
        int nested = 0;
        foreach (var t in types) { try { if (t.IsNested) nested++; } catch { /* missing dep */ } }
        Console.WriteLine($"  nested : {nested}");
        Console.WriteLine($"  enums  : {types.Count(t => t.IsEnum)}");

        // the names MelonLoader / mods actually ask for
        string[] wanted = { "UnityEngine.GameObject", "UnityEngine.Transform", "UnityEngine.Vector3",
                            "UnityEngine.Object", "UnityEngine.Component", "UnityEngine.MonoBehaviour",
                            "UnityEngine.Application", "UnityEngine.Debug" };
        Console.WriteLine();
        Console.WriteLine("name resolution:");
        int resolved = 0;
        foreach (var w in wanted)
        {
            var t = types.FirstOrDefault(x => x.FullName == w);
            Console.WriteLine($"   {(t != null ? "OK  " : "MISS")} {w}");
            if (t != null) resolved++;
        }
        Console.WriteLine($"   resolved {resolved}/{wanted.Length}");

        // any duplicate type names surviving in the runtime's own view?
        var dups = types.GroupBy(t => t.FullName).Where(g => g.Count() > 1).ToList();
        Console.WriteLine();
        Console.WriteLine($"runtime-visible duplicate FullNames: {dups.Count}");
        foreach (var d in dups.Take(5)) Console.WriteLine("   " + d.Key + " x" + d.Count());

        return 0;
    }
}
