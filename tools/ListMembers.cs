// ListMembers - dump the public members of a type, to check an API surface
// before compiling against it.
//
// Usage: ListMembers <assembly.dll> <TypeFullName>

using System;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ListMembers <assembly.dll> <type>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var t = asm.MainModule.GetTypes().FirstOrDefault(x => x.FullName == args[1])
             ?? asm.MainModule.GetTypes().FirstOrDefault(x => x.Name == args[1]);
        if (t == null) { Console.Error.WriteLine("type not found: " + args[1]); return 1; }

        Console.WriteLine($"=== {t.FullName} ===");
        Console.WriteLine("-- static methods --");
        foreach (var m in t.Methods.Where(m => m.IsPublic && m.IsStatic))
            Console.WriteLine($"   {Sig(m)}");
        Console.WriteLine("-- instance methods --");
        foreach (var m in t.Methods.Where(m => m.IsPublic && !m.IsStatic && !m.IsGetter && !m.IsSetter))
            Console.WriteLine($"   {Sig(m)}");
        Console.WriteLine("-- properties --");
        foreach (var p in t.Properties.Where(p => (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false)))
            Console.WriteLine($"   {p.PropertyType.Name} {p.Name}  {{ {(p.GetMethod?.IsPublic == true ? "get; " : "")}{(p.SetMethod?.IsPublic == true ? "set; " : "")}}}");
        return 0;
    }

    private static string Sig(MethodDefinition m)
    {
        string ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
        string gp = m.HasGenericParameters ? "<" + string.Join(",", m.GenericParameters.Select(g => g.Name)) + ">" : "";
        return $"{m.ReturnType.Name} {m.Name}{gp}({ps})";
    }
}
