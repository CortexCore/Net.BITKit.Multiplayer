using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BITKit.Multiplayer.CodeGen;
using BITKit.Multiplayer.NetRpc;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 && !(args.Length == 3 && args[0] == "--remote")) { Console.Error.WriteLine("Usage: CodeGen input.dll output.dll | --remote contracts.dll output.cs"); return 2; }
        try
        {
            if (args.Length == 3 && args[0] == "--remote")
            {
                var assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
                var contracts = assembly.GetExportedTypes().Where(t => t.IsInterface && !t.ContainsGenericParameters).ToArray();
                var sources = contracts.Select(RemoteInterfaceSourceGenerator.Generate).Select(s => s.Replace("#nullable enable", "").Replace("using System;", "").Replace("using System.Threading.Tasks;", "").Replace("using BITKit.Multiplayer.NetRpc;", "").Replace("using Cysharp.Threading.Tasks;", ""));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                File.WriteAllText(args[2], "#nullable enable\nusing System;\nusing System.Threading.Tasks;\nusing Cysharp.Threading.Tasks;\nusing BITKit.Multiplayer.NetRpc;\n" + string.Join("\n", sources));
                return 0;
            }
            var errors = Weaver.WeaveNetRpc(args[0], args[1]);
            foreach (var error in errors) Console.Error.WriteLine("BITKIT: " + error);
            return errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
    }
}
