using System;
using System.Linq;
using Mono.Cecil;
public static class ShowMembers {
  public static int Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    foreach (var t in asm.MainModule.GetTypes()) {
      if (t.FullName != a[1]) continue;
      Console.WriteLine("type " + t.FullName);
      foreach (var m in t.Methods.Where(m => m.Name.Contains(a[2])))
        Console.WriteLine("   " + m.ReturnType.FullName + " " + m.Name + "(" +
          string.Join(", ", m.Parameters.Select(pp => pp.ParameterType.FullName)) + ")");
    }
    return 0;
  }
}
