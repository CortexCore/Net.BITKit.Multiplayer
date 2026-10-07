using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace BITKit.Multiplayer.CodeGen
{
    /// <summary>Editor-only in-memory Cecil transform; no dotnet process or runtime IL emit.</summary>
    public sealed class NetRpcILPostProcessor : ILPostProcessor
    {
        private const string Core = "Net.BITKit.Multiplayer";
        private const string Self = "Unity.BITKit.Multiplayer.CodeGen";
        private const string WovenMarker = "BITKit.Multiplayer.WovenAssemblyAttribute";
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            Core, Self, "Net.BITKit.Multiplayer.Transport"
        };

        public override ILPostProcessor GetInstance() => this;
        public override bool WillProcess(ICompiledAssembly assembly) =>
            !Excluded.Contains(assembly.Name) &&
            assembly.References.Any(path => Path.GetFileNameWithoutExtension(path) == Core);

        public override ILPostProcessResult Process(ICompiledAssembly compiled)
        {
            var diagnostics = new List<DiagnosticMessage>();
            if (!WillProcess(compiled)) return new ILPostProcessResult(null, diagnostics);
            try
            {
                var resolver = new DefaultAssemblyResolver();
                foreach (var directory in compiled.References.Select(Path.GetDirectoryName).Where(p => !string.IsNullOrEmpty(p)).Distinct())
                    resolver.AddSearchDirectory(directory);
                using var pe = new MemoryStream(compiled.InMemoryAssembly.PeData, writable: false);
                using var module = ModuleDefinition.ReadModule(pe, new ReaderParameters
                {
                    AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate,
                    InMemory = true, ReadSymbols = false
                });
                if (module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == WovenMarker))
                    return new ILPostProcessResult(null, diagnostics);

                foreach (var error in Weaver.WeaveNetRpcModule(module))
                    diagnostics.Add(new DiagnosticMessage { DiagnosticType = DiagnosticType.Error, MessageData = "BITKIT: " + error });
                if (diagnostics.Count != 0 || !module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == WovenMarker))
                    return new ILPostProcessResult(null, diagnostics);

                // Emit matching hidden debug entries for every MethodDef. An empty portable
                // PDB crashes Unity Mono's exception stack lookup (metadata.c row assertion).
                // Original source sequence-point remapping remains a separate concern.
                Weaver.EnsureSafePortableSymbols(module);
                using var output = new MemoryStream();
                using var symbols = new MemoryStream();
                module.Write(output, new WriterParameters { WriteSymbols = true,
                    SymbolWriterProvider = new PortablePdbWriterProvider(), SymbolStream = symbols });
                return new ILPostProcessResult(new InMemoryAssembly(output.ToArray(), symbols.ToArray()), diagnostics);
            }
            catch (Exception error)
            {
                diagnostics.Add(new DiagnosticMessage { DiagnosticType = DiagnosticType.Error,
                    MessageData = "BITKIT Unity IL postprocessor: " + error });
                return new ILPostProcessResult(null, diagnostics);
            }
        }
    }
}
