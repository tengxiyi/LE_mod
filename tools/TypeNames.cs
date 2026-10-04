using System;
using System.Linq;
using Mono.Cecil;
public static class TypeNames {
  public static int Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    Console.WriteLine("assembly: " + asm.Name.FullName);
    foreach (var t in asm.MainModule.GetTypes().Where(t => t.Name == a[1] && !t.IsNested))
      Console.WriteLine($"  fullname='{t.FullName}'  ns='{t.Namespace}'  name='{t.Name}'");
    return 0;
  }
}
