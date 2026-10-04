using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

public static class FindMethodVis
{
    public static int Main(string[] args)
    {
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        string typeName = args[1];
        string methodName = args[2];

        foreach (var t in asm.MainModule.GetTypes())
        {
            if (t.Name != typeName) continue;
            Console.WriteLine($"type {t.FullName}  (isPublic={t.IsPublic}, isNotPublic={t.IsNotPublic})");
            foreach (var m in t.Methods.Where(m => m.Name.Contains(methodName)))
            {
                var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name));
                Console.WriteLine($"   {m.Attributes,-40} {m.ReturnType.Name} {m.Name}({ps})");
            }
        }

        // Also report whether the interop exposes a non-generic FindObjectsOfType at all.
        Console.WriteLine();
        Console.WriteLine("-- all *FindObjects* in the assembly --");
        foreach (var t in asm.MainModule.GetTypes())
            foreach (var m in t.Methods.Where(m => m.Name.Contains("FindObjects")))
                Console.WriteLine($"   [{t.Name}] {m.Attributes} {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
        return 0;
    }
}
