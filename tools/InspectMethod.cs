// InspectMethod - dump a method's IL operands and local signatures, to find
// exactly which type reference makes it impossible to JIT.
//
// Usage: InspectMethod <assembly.dll> <TypeFullName|substring> <methodName>

using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: InspectMethod <assembly.dll> <type> <method>");
            return 2;
        }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        string typeNeedle = args[1];
        string methodName = args[2];

        var types = asm.MainModule.GetTypes()
            .Where(t => t.FullName.Contains(typeNeedle, StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"types matching '{typeNeedle}': {types.Count}");

        foreach (var t in types)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {t.FullName} ===");
            Console.WriteLine($"  base            : {t.BaseType?.FullName}");
            Console.WriteLine($"  interfaces      : {string.Join(", ", t.Interfaces.Select(i => i.InterfaceType.FullName))}");
            Console.WriteLine($"  fields ({t.Fields.Count})     :");
            foreach (var f in t.Fields.Take(20))
                Console.WriteLine($"      {f.FieldType.FullName} {f.Name}");

            foreach (var m in t.Methods.Where(m => m.Name == methodName))
            {
                Console.WriteLine();
                Console.WriteLine($"  --- {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName))}) -> {m.ReturnType.FullName}");
                if (!m.HasBody) { Console.WriteLine("      (no body)"); continue; }

                Console.WriteLine($"      locals:");
                foreach (var v in m.Body.Variables)
                    Console.WriteLine($"        V_{v.Index}: {v.VariableType.FullName}");

                Console.WriteLine($"      IL ({m.Body.Instructions.Count} instrs):");
                foreach (var ins in m.Body.Instructions.Take(80))
                {
                    string op = ins.Operand switch
                    {
                        null => "",
                        TypeReference tr => "  <" + tr.FullName + ">  scope=" + (tr.Scope?.Name ?? "?"),
                        MethodReference mr => "  " + mr.DeclaringType.FullName + "::" + mr.Name,
                        FieldReference fr => "  " + fr.DeclaringType.FullName + "::" + fr.Name,
                        _ => "  " + ins.Operand
                    };
                    Console.WriteLine($"        {ins.Offset:X4}: {ins.OpCode.Name}{op}");
                }
                if (m.Body.Instructions.Count > 80)
                    Console.WriteLine($"        ... and {m.Body.Instructions.Count - 80} more");
            }
        }
        return 0;
    }
}
