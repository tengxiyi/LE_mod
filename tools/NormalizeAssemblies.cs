// NormalizeAssemblies - Mono.Cecil read/write roundtrip for every interop assembly in a folder.
//
// Why: Cpp2IL 2022.1.0-pre-release.21 emits metadata that the CLR intermittently rejects when
// types are resolved late (Fatal error 0x80131506 inside Assembly.GetType). A Cecil roundtrip
// rebuilds all metadata heaps and produces assemblies the CLR accepts — the same treatment that
// fixed UnityEngine.CoreModule.dll (stable in game for a full day).
//
// Usage: NormalizeAssemblies <inputDir> <outputDir>
// Writes every <inputDir>\*.dll to <outputDir> under the same name. Non-recursive.

using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: NormalizeAssemblies <inputDir> <outputDir>"); return 2; }

        Directory.CreateDirectory(args[1]);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(args[0]);
        var readParams = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };

        var dlls = Directory.GetFiles(args[0], "*.dll").OrderBy(f => f).ToList();
        Console.WriteLine($"input : {args[0]}  ({dlls.Count} dlls)");
        Console.WriteLine($"output: {args[1]}");

        int ok = 0, failed = 0;
        foreach (var dll in dlls)
        {
            string outPath = Path.Combine(args[1], Path.GetFileName(dll));
            try
            {
                var asm = AssemblyDefinition.ReadAssembly(dll, readParams);
                asm.Write(outPath);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL {Path.GetFileName(dll)}: {ex.GetType().Name}: {ex.Message}");
                // copy the original through unchanged so the output folder stays complete
                File.Copy(dll, outPath, overwrite: true);
            }
        }

        Console.WriteLine($"normalized: {ok}, failed(copied as-is): {failed}");
        return failed == 0 ? 0 : 1;
    }
}
