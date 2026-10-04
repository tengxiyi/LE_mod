using System;
using System.Linq;
using Mono.Cecil;

public static class ShowConv
{
    public static int Main(string[] a)
    {
        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        string want = a[1];

        foreach (var t in asm.MainModule.GetTypes())
        {
            if (t.FullName != want && t.Name != want) continue;
            Console.WriteLine("type " + t.FullName);
            foreach (var m in t.Methods.Where(m => m.Name.StartsWith("op_") || m.IsConstructor))
            {
                var ps = string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.FullName));
                Console.WriteLine("   " + m.Attributes + "  " + m.ReturnType.FullName + " " + m.Name + "(" + ps + ")");
            }
        }
        return 0;
    }
}
