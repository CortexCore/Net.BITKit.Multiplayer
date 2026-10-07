using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BITKit.Multiplayer.CodeGen
{
    public static partial class Weaver
    {
        private const string CoreName = "Net.BITKit.Multiplayer";
        private const string ContractsName = "Net.BITKit.Multiplayer.Contracts";
        private const string RpcAttributeName = "BITKit.Multiplayer.RpcAttribute";
        private const string WovenAssemblyName = "BITKit.Multiplayer.WovenAssemblyAttribute";

        private static void MarkWovenAssembly(ModuleDefinition module)
        {
            var reference = module.AssemblyReferences.First(a => a.Name == ContractsName);
            var contracts = module.AssemblyResolver.Resolve(reference).MainModule;
            var marker = contracts.GetType(WovenAssemblyName);
            module.Assembly.CustomAttributes.Add(new CustomAttribute(module.ImportReference(marker.Methods.Single(
                method => method.IsConstructor && !method.HasParameters))));
        }

        private static bool Has(MethodDefinition method, string name) =>
            method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == name);
        private static bool HasAsyncVoid(MethodDefinition method) => method.ReturnType.FullName == "System.Void" &&
            Has(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
            types.SelectMany(type => new[] { type }.Concat(AllTypes(type.NestedTypes)));
        private static bool GenericEnclosingType(TypeDefinition type) => type.HasGenericParameters ||
            type.DeclaringType != null && GenericEnclosingType(type.DeclaringType);
        private static string TypeId(TypeReference type) => type is GenericInstanceType generic
            ? generic.ElementType.FullName.Replace('/', '+') + "<" + string.Join(",", generic.GenericArguments.Select(TypeId)) + ">"
            : type.FullName.Replace('/', '+');

        private static MethodDefinition MoveBody(MethodDefinition method, string name)
        {
            var raw = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig, method.ReturnType);
            foreach (var parameter in method.Parameters)
                raw.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            raw.Body = method.Body;
            foreach (var instruction in raw.Body.Instructions)
                if (instruction.Operand is ParameterDefinition parameter)
                    instruction.Operand = raw.Parameters[parameter.Index];
            method.DeclaringType.Methods.Add(raw);
            method.Body = new MethodBody(method);
            return raw;
        }

        private static void NormalizeImportedCorelib(ModuleDefinition module)
        {
            var target = module.TypeSystem.Object.Scope;
            if (target is not AssemblyNameReference assembly || assembly.Name == "System.Private.CoreLib") return;
            foreach (var reference in module.GetTypeReferences())
                if (reference.Scope is AssemblyNameReference imported && imported.Name == "System.Private.CoreLib")
                    reference.Scope = target;
            for (int i = module.AssemblyReferences.Count - 1; i >= 0; i--)
                if (module.AssemblyReferences[i].Name == "System.Private.CoreLib") module.AssemblyReferences.RemoveAt(i);
        }

        /// <summary>Unity Mono requires a debug row for generated methods and the final MethodDef.</summary>
        public static void EnsureSafePortableSymbols(ModuleDefinition module)
        {
            var document = new Document("bitkit-generated");
            foreach (var type in AllTypes(module.Types))
                foreach (var method in type.Methods)
                    if (method.HasBody && method.Body.Instructions.Count != 0 && !method.DebugInformation.HasSequencePoints)
                        AddHiddenPoint(method, document);
            const string name = "__BITKitPortableSymbols";
            var tail = module.Types.FirstOrDefault(type => type.Name == name);
            if (tail != null) module.Types.Remove(tail);
            else
            {
                tail = new TypeDefinition("", name, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
                    module.TypeSystem.Object);
                var method = new MethodDefinition("Hidden", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
                method.Body.GetILProcessor().Emit(OpCodes.Ret);
                tail.Methods.Add(method);
                AddHiddenPoint(method, document);
            }
            module.Types.Add(tail);
        }

        private static void AddHiddenPoint(MethodDefinition method, Document document) =>
            method.DebugInformation.SequencePoints.Add(new SequencePoint(method.Body.Instructions[0], document)
            { StartLine = 0xFEEFEE, EndLine = 0xFEEFEE, StartColumn = 0, EndColumn = 0 });
    }
}
