// DumpIl.cs - print a method's full IL with resolved branch targets and decoded operands.
//
// Written to answer a specific question honestly: "which key does what?". Guessing from a
// filtered view of the IL produced a wrong answer once already, so this dumps everything.
//
// Usage: DumpIl <assembly.dll> <TypeName|substring> <methodName>

using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: DumpIl <assembly.dll> <type> <method>"); return 2; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);

        var types = asm.MainModule.GetTypes()
            .Where(t => t.FullName.Contains(args[1], StringComparison.Ordinal)).ToList();

        foreach (var t in types)
        {
            var methods = args[2] == "-"
                ? t.Methods.ToList()
                : t.Methods.Where(m => m.Name == args[2] || m.Name.Contains(args[2])).ToList();
            if (methods.Count == 0) continue;

            // Map instruction offsets to labels so branch targets are readable.
            foreach (var m in methods)
            {
                if (!m.HasBody) continue;
                var body = m.Body;
                string ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine($"=== {t.FullName}::{m.Name}({ps}) -> {m.ReturnType.Name}  [{body.Instructions.Count} instrs] ===");

                foreach (var ins in body.Instructions)
                {
                    string operand;
                    switch (ins.Operand)
                    {
                        case null:
                            operand = "";
                            break;
                        case Instruction target:
                            operand = $"-> IL_{target.Offset:X4}";
                            break;
                        case Instruction[] targets:
                            operand = "-> " + string.Join(", ", targets.Select(x => $"IL_{x.Offset:X4}"));
                            break;
                        case string s:
                            operand = $"\"{s}\"";
                            break;
                        case MethodReference mr:
                            operand = $"{mr.DeclaringType.Name}::{mr.Name}";
                            break;
                        case FieldReference fr:
                            operand = $"{fr.DeclaringType.Name}::{fr.Name}";
                            break;
                        case TypeReference tr:
                            operand = tr.Name;
                            break;
                        case VariableDefinition v:
                            operand = $"V_{v.Index}";
                            break;
                        case ParameterDefinition p:
                            operand = p.Name;
                            break;
                        default:
                            operand = ins.Operand.ToString();
                            break;
                    }
                    Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode.Name,-12} {operand}");
                }
                Console.WriteLine();
            }
        }
        return 0;
    }
}
