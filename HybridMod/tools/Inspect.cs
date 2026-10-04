// Throwaway reflection inspector used while authoring the HybridMod skeleton.
// Compile with Roslyn csc.exe against the installed .NET 6 shared framework.
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.IO;

internal static class Inspect
{
    private static int Main(string[] args)
    {
        string asmPath = args[0];
        string[] wanted = args.Length > 1
            ? args[1].Split(',')
            : new[] { "MelonLoader.MelonEnvironment", "MelonLoader.MelonLogger", "MelonLoader.MelonMod", "MelonLoader.MelonBase" };

        try
        {
            Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(asmPath);
            Console.WriteLine("MSBuild-time image: " + asm.FullName);
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            foreach (string want in wanted)
            {
                Type t = types.FirstOrDefault(x => x.FullName == want)
                         ?? types.FirstOrDefault(x => x.Name == want.Split('.').Last());
                if (t == null) { Console.WriteLine("== " + want + ": NOT FOUND =="); continue; }
                Console.WriteLine("== " + t.FullName + " (base " + (t.BaseType?.Name ?? "-") + ") static=" + t.IsAbstract + " ==");

                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).OrderBy(p => p.Name))
                    Console.WriteLine("   prop " + (p.GetMethod?.IsStatic == true ? "static " : "") + p.PropertyType.Name + " " + p.Name);

                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).OrderBy(f => f.Name))
                    Console.WriteLine("   field " + (f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);

                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                           .Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                    Console.WriteLine("   " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" +
                                      string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("INSPECT FAILED: " + ex);
            return 1;
        }
        return 0;
    }
}
