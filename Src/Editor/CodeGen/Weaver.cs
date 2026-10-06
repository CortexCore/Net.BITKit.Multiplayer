using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BITKit.Multiplayer;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BITKit.Multiplayer.CodeGen
{
public static partial class Weaver
{
    private const string CoreName = "Net.BITKit.Multiplayer";
    private const string ContractsName = "Net.BITKit.Multiplayer.Contracts";
    private const string CoreNamespace = "BITKit.Multiplayer.";
    private const string RpcAttributeName = CoreNamespace + "RpcAttribute";
    private const string RpcTargetName = CoreNamespace + "RpcTarget";
    private const string SyncVarName = CoreNamespace + "SyncVarAttribute";
    private const string HookName = CoreNamespace + "HookAttribute";
    private const string HostOnlyName = CoreNamespace + "HostOnlyAttribute";
    private const string ClientOnlyName = CoreNamespace + "ClientOnlyAttribute";
    private const string WovenRpcName = CoreNamespace + "WovenRpcAttribute";
    private const string WovenTypedName = CoreNamespace + "WovenTypedRpcAttribute";
    private const string WovenAssemblyName = CoreNamespace + "WovenAssemblyAttribute";
    private const string NetRpcBackendName = CoreNamespace + "NetRpcBackendAttribute";

    public static bool IsNetRpcType(TypeDefinition type) =>
        type.CustomAttributes.Any(a => a.AttributeType.FullName == NetRpcBackendName) ||
        type.DeclaringType != null && IsNetRpcType(type.DeclaringType);

    public static IReadOnlyList<string> WeaveMixedModule(ModuleDefinition module)
    {
        var errors = new List<string>(WeaveModule(module, type => !IsNetRpcType(type), markAssembly: false));
        if (errors.Count != 0) return errors;
        errors.AddRange(WeaveNetRpcModule(module, IsNetRpcType, markAssembly: false));
        if (errors.Count == 0) MarkWovenAssembly(module);
        return errors;
    }

    private static void MarkWovenAssembly(ModuleDefinition module)
    {
        var reference = module.AssemblyReferences.First(a => a.Name == ContractsName);
        var contracts = module.AssemblyResolver.Resolve(reference).MainModule;
        var marker = contracts.GetType(WovenAssemblyName);
        module.Assembly.CustomAttributes.Add(new CustomAttribute(module.ImportReference(marker.Methods.Single(
            method => method.IsConstructor && !method.HasParameters))));
    }

    private sealed class CoreReferences
    {
        public readonly MethodReference Set;
        public readonly MethodReference Guard;
        public readonly MethodReference Begin;
        public readonly MethodReference WovenRpcCtor;
        public readonly MethodReference WovenTypedCtor;
        public readonly MethodReference WovenSyncCtor;
        public readonly MethodReference WovenAssemblyCtor;
        public readonly TypeReference TypedWriter;
        public readonly TypeReference TypedReader;
        public readonly TypeReference RpcTarget;
        public readonly TypeReference ReceiverReturn;
        public readonly FieldReference VoidResult;
        public readonly MethodReference Read;
        public readonly MethodReference ReadTarget;
        public readonly MethodReference ReadComplete;
        public readonly MethodReference CompleteTask;
        public readonly MethodReference CompleteTaskValue;
        public readonly MethodReference Write;
        public readonly MethodReference PrepareVoid;
        public readonly MethodReference ShouldExecuteLocal;
        public readonly MethodReference EnterLocal;
        public readonly MethodReference ExitLocal;
        public readonly MethodReference FinishVoid;
        public readonly MethodReference FinishTask;
        public readonly MethodReference FinishTaskValue;
        public readonly MethodReference Dispose;

        public CoreReferences(ModuleDefinition module)
        {
            var coreReference = module.AssemblyReferences.FirstOrDefault(a => a.Name == CoreName)
                ?? throw new InvalidOperationException("Target must reference " + CoreName);
            var core = module.AssemblyResolver.Resolve(coreReference).MainModule;
            var contractsReference = module.AssemblyReferences.FirstOrDefault(a => a.Name == ContractsName)
                ?? throw new InvalidOperationException("Target must reference " + ContractsName);
            var contracts = module.AssemblyResolver.Resolve(contractsReference).MainModule;
            TypeDefinition Type(string name) => core.GetType(CoreNamespace + name)
                ?? throw new InvalidOperationException("Core API type missing: " + name);
            TypeDefinition ContractType(string name) => contracts.GetType(CoreNamespace + name)
                ?? throw new InvalidOperationException("Contracts API type missing: " + name);
            MethodReference Method(TypeDefinition type, string name, int genericCount = 0) => module.ImportReference(type.Methods.Single(m =>
                m.Name == name && m.GenericParameters.Count == genericCount));
            MethodReference Constructor(TypeDefinition type, params string[] arguments) => module.ImportReference(type.Methods.Single(m =>
                m.IsConstructor && !m.IsStatic && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(arguments)));

            var bridge = Type("DispatchBridge");
            Set = Method(bridge, "Set"); Guard = Method(bridge, "Guard"); Begin = Method(bridge, "BeginTyped");
             WovenRpcCtor = Constructor(ContractType("WovenRpcAttribute"), "System.String");
             WovenTypedCtor = Constructor(ContractType("WovenTypedRpcAttribute"), "System.UInt64", "System.UInt64");
             WovenSyncCtor = Constructor(ContractType("WovenSyncVarAttribute"), "System.UInt64");
             WovenAssemblyCtor = Constructor(ContractType("WovenAssemblyAttribute"));
            var reader = Type("TypedRpcReader"); var writer = Type("TypedRpcWriter");
            TypedReader = module.ImportReference(reader); TypedWriter = module.ImportReference(writer);
            RpcTarget = module.ImportReference(Type("RpcTarget"));
            ReceiverReturn = module.ImportReference(Type("TypedRpcReceiver").Methods.Single(m => m.Name == "Invoke").ReturnType);
            Read = Method(reader, "Read", 1); ReadTarget = Method(reader, "Target"); ReadComplete = Method(reader, "Complete");
            var results = Type("TypedRpcResults");
            VoidResult = module.ImportReference(results.Fields.Single(f => f.Name == "Void"));
            CompleteTask = Method(results, "CompleteTask"); CompleteTaskValue = Method(results, "CompleteTask", 1);
            Write = Method(writer, "Write", 1);
            PrepareVoid = Method(writer, "PrepareVoid"); ShouldExecuteLocal = Method(writer, "ShouldExecuteLocal");
            EnterLocal = Method(writer, "EnterLocal"); ExitLocal = Method(writer, "ExitLocal");
            FinishVoid = Method(writer, "FinishVoid"); FinishTask = Method(writer, "FinishTask");
            FinishTaskValue = Method(writer, "FinishTask", 1); Dispose = Method(writer, "Dispose");
        }
    }
    public static IReadOnlyList<string> Weave(string input, string output)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false, InMemory = true, AssemblyResolver = resolver });
        var errors = WeaveModule(module);
        if (errors.Count == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            module.Write(output);
        }
        return errors;
    }

    /// <summary>Transform a target module in memory; Unity ILPP and CLI use exactly this generator.</summary>
    public static IReadOnlyList<string> WeaveModule(ModuleDefinition module,
        Func<TypeDefinition, bool>? includeType = null, bool markAssembly = true)
    {
        var errors = new List<string>();
        if (module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == WovenAssemblyName))
            return new[] { $"{module.Assembly.Name.Name}: already woven" };
        var types = AllTypes(module.Types).Where(t => includeType == null || includeType(t)).ToArray();
        // Delay resolution until a decorated declaration exists; a Core-referencing
        // assembly with no RPCs is left byte-for-byte untouched by Unity ILPP.
        var hasDeclarations = types.Any(t =>
            t.Methods.Any(m => !m.IsAbstract && m.CustomAttributes.Any(a =>
                a.AttributeType.FullName is RpcAttributeName or HostOnlyName or ClientOnlyName)) ||
            t.Properties.Any(p => p.GetMethod is { IsAbstract: false } &&
                p.CustomAttributes.Any(a => a.AttributeType.FullName == SyncVarName || a.AttributeType.FullName == HookName)));
        if (!hasDeclarations) return errors;
        var references = new CoreReferences(module);
        var rpcList = new List<(MethodDefinition Method, SendTo To, string Id)>();
        var numericIds = new Dictionary<ulong, string>();
        var stateList = new List<PropertyDefinition>();
        var hooks = new Dictionary<PropertyDefinition, MethodDefinition>();
        foreach (var type in types)
        {
            if (type.Name.StartsWith("<")) continue;
            var ids = new HashSet<string>();
            var bodyNames = new HashSet<string>();
            foreach (var method in type.Methods.ToArray())
            {
                var rpc = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == RpcAttributeName);
                var host = Has(method, HostOnlyName);
                var client = Has(method, ClientOnlyName);
                if (rpc == null && !host && !client) continue;
                if (rpc != null && (host || client) || host && client) { errors.Add($"{method.FullName}: conflicting role attributes"); continue; }
                if (method.Name.StartsWith("__bitkit_") || Has(method, WovenRpcName)) { errors.Add($"{method.FullName}: already woven"); continue; }
                if (!method.HasBody || method.IsStatic || method.HasGenericParameters || GenericEnclosingType(type) || method.Parameters.Any(p => p.ParameterType is ByReferenceType || p.ParameterType is GenericParameter) || HasAsyncVoid(method))
                { errors.Add($"{method.FullName}: static/generic/ref/out/async void or missing body not supported"); continue; }
                if (rpc == null) continue;
                var to = (SendTo)(int)rpc.ConstructorArguments[0].Value;
                if (!Enum.IsDefined(typeof(SendTo), to)) { errors.Add($"{method.FullName}: unsupported RPC route {to}"); continue; }
                var delivery = rpc.ConstructorArguments.Count > 1 ? (RpcDelivery)(int)rpc.ConstructorArguments[1].Value : RpcDelivery.Reliable;
                foreach (var named in rpc.Properties)
                    if (named.Name == "Delivery") delivery = (RpcDelivery)(int)named.Argument.Value;
                if (!Enum.IsDefined(typeof(RpcDelivery), delivery) || delivery == RpcDelivery.Unreliable && method.ReturnType.FullName != "System.Void")
                { errors.Add($"{method.FullName}: unsupported Delivery {delivery}; Unreliable requires void"); continue; }
                var task = method.ReturnType.FullName == "System.Threading.Tasks.Task";
                var taskValue = method.ReturnType is GenericInstanceType g && g.ElementType.FullName == "System.Threading.Tasks.Task`1";
                if (method.ReturnType.FullName != "System.Void" && !task && !taskValue ||
                    to == SendTo.All && method.ReturnType.FullName != "System.Void" ||
                    to == SendTo.Target && method.Parameters.Count(p => p.ParameterType.FullName == RpcTargetName) != 1 ||
                    to != SendTo.Target && method.Parameters.Any(p => p.ParameterType.FullName == RpcTargetName) ||
                      method.Parameters.Any(p => delivery == RpcDelivery.Unreliable ? !SupportedUnreliable(p.ParameterType) : !Supported(p.ParameterType, arrays: true)) ||
                      taskValue && !Supported(((GenericInstanceType)method.ReturnType).GenericArguments[0], arrays: true))
                { errors.Add($"{method.FullName}: unsupported RPC return/parameter/routing or MemoryPack schema; All requires void, Target requires one RpcTarget; reliable generated DTO members require contiguous [MemoryPackOrder], reference DTOs require [MemoryPackable] partial"); continue; }
                if (method.Parameters.Count > 32)
                { errors.Add($"{method.FullName}: RPC supports at most 32 parameters"); continue; }
                var id = Identity(method);
                if (!ids.Add(id)) { errors.Add($"{method.FullName}: duplicate contract {id}"); continue; }
                var numeric = StableHash64(id);
                if (numericIds.TryGetValue(numeric, out var prior) && prior != id)
                { errors.Add($"{method.FullName}: numeric RPC ID collision with {prior}"); continue; }
                numericIds[numeric] = id;
                var bodyName = "__bitkit_body_" + method.Name + "_" + StableHash(id);
                if (!bodyNames.Add(bodyName) || type.Methods.Any(m => m.Name == bodyName))
                { errors.Add($"{method.FullName}: RPC body hash/name collision {bodyName}; change the signature or name"); continue; }
                rpcList.Add((method, to, id));
            }
            foreach (var property in type.Properties.Where(p => p.CustomAttributes.Any(a => a.AttributeType.FullName == HookName) &&
                !p.CustomAttributes.Any(a => a.AttributeType.FullName == SyncVarName)))
                errors.Add($"{property.FullName}: Hook requires SyncVar");
            foreach (var property in type.Properties.Where(p => p.CustomAttributes.Any(a => a.AttributeType.FullName == SyncVarName)))
            {
                if (property.Name.Length > 256) { errors.Add($"{property.FullName}: SyncVar member name exceeds 256 characters"); continue; }
                var setter = property.SetMethod;
                int kind = CollectionKind(property.PropertyType);
                if (kind != 0)
                {
                    var args = ((GenericInstanceType)property.PropertyType).GenericArguments;
                    if (setter != null || property.GetMethod == null || !property.GetMethod.HasBody || property.GetMethod.IsStatic || !AutoGetter(property) ||
                        property.HasParameters || GenericEnclosingType(type) || args.Any(a => !Supported(a, arrays: false)) ||
                        (kind == 2 || kind == 3) && !StableKey(args[0]))
                    { errors.Add($"{property.FullName}: SyncVar collection requires initialized getter-only auto-property, supported element schemas and primitive/enum/string keys"); continue; }
                }
                else if (setter == null || !setter.HasBody || setter.IsStatic || property.HasParameters || GenericEnclosingType(type) || !Supported(property.PropertyType, arrays: false) || property.PropertyType is GenericInstanceType || property.PropertyType is ArrayType || !AutoSetter(setter, property.Name))
                { errors.Add($"{property.FullName}: SyncVar requires supported scalar auto-property with setter"); continue; }
                if (Ancestors(type).Any(baseType => baseType.Properties.Any(p => p.Name == property.Name && p.CustomAttributes.Any(a => a.AttributeType.FullName == SyncVarName))))
                { errors.Add($"{property.FullName}: inherited SyncVar name collision"); continue; }
                var hookAttribute = property.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == HookName);
                if (hookAttribute != null)
                {
                    string name = (string)hookAttribute.ConstructorArguments[0].Value;
                    string changeType = kind == 0 ? "" : CoreNamespace + (kind == 1 ? "SyncListChange`1" : kind == 2 ? "SyncDictionaryChange`2" : "SyncHashSetChange`1") +
                        "<" + string.Join(",", ((GenericInstanceType)property.PropertyType).GenericArguments.Select(a => a.FullName)) + ">&";
                    var matches = type.Methods.Where(m => m.Name == name && !m.IsStatic && !m.HasGenericParameters && m.HasBody &&
                        m.ReturnType.FullName == "System.Void" && !HasAsyncVoid(m) && !Has(m, RpcAttributeName) && !Has(m, HostOnlyName) && !Has(m, ClientOnlyName) &&
                        (kind == 0 ? m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.FullName == property.PropertyType.FullName) :
                            m.Parameters.Count == 1 && m.Parameters[0].IsIn && !m.Parameters[0].IsOut && m.Parameters[0].ParameterType.FullName == changeType)).ToArray();
                    if (matches.Length != 1 || type.Methods.Any(m => m.Name == "__bitkit_hook_" + property.Name))
                    { errors.Add($"{property.FullName}: Hook must name one local void instance method: (oldValue, newValue) or (in typedCollectionChange)"); continue; }
                    hooks.Add(property, matches[0]);
                }
                stateList.Add(property);
            }
        }
        if (errors.Count > 0) return errors;
        foreach (var (method, to, id) in rpcList)
        {
            var marker = new CustomAttribute(references.WovenRpcCtor);
            marker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, id));
            method.CustomAttributes.Add(marker);
            var raw = MoveBody(method, "__bitkit_body_" + method.Name + "_" + StableHash(id));
            var delivery = Delivery(method.CustomAttributes.First(a => a.AttributeType.FullName == RpcAttributeName));
            {
                var numeric = StableHash64(id);
                var schemaText = "typed/v4|" + to + "|" + delivery + "|" + SchemaIdentity(method.ReturnType, new HashSet<string>()) + "|" +
                    string.Join("|", method.Parameters.Select(p => SchemaIdentity(p.ParameterType, new HashSet<string>())));
                var fingerprint = StableHash64(schemaText);
                var typedMarker = new CustomAttribute(references.WovenTypedCtor);
                typedMarker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.UInt64, numeric));
                typedMarker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.UInt64, fingerprint));
                method.CustomAttributes.Add(typedMarker);
                EmitTypedReceiver(method, raw, id, module, references);
                EmitTypedWrapper(method, raw, to, delivery, numeric, fingerprint, references.Begin, module, references);
                continue;
            }
        }
        foreach (var property in stateList)
        {
            int kind = CollectionKind(property.PropertyType);
            var marker = new CustomAttribute(references.WovenSyncCtor);
            marker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.UInt64,
                StableHash64("state/v1|" + property.DeclaringType.FullName + "/" + property.Name + "|" + kind + "|default-equality|" + SchemaIdentity(property.PropertyType, new HashSet<string>()))));
            property.CustomAttributes.Add(marker);
            if (hooks.TryGetValue(property, out var hook)) EmitStateHook(property, hook, kind, module);
            if (kind != 0) continue;
            var setter = property.SetMethod!;
            MoveBody(setter, "__bitkit_set_" + property.Name);
            var il = setter.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, property.Name); il.Emit(OpCodes.Ldarg_1);
            if (property.PropertyType.IsValueType) il.Emit(OpCodes.Box, property.PropertyType);
            il.Emit(OpCodes.Call, references.Set); il.Emit(OpCodes.Ret);
        }
        foreach (var type in types)
            foreach (var method in type.Methods.Where(m => m.HasBody && (Has(m, HostOnlyName) || Has(m, ClientOnlyName))))
            {
                var il = method.Body.GetILProcessor(); var first = method.Body.Instructions.First();
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(Has(method, HostOnlyName) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, references.Guard));
            }
        if (markAssembly && (rpcList.Count + stateList.Count > 0 ||
            types.Any(t => t.Methods.Any(m => Has(m, HostOnlyName) || Has(m, ClientOnlyName)))))
            module.Assembly.CustomAttributes.Add(new CustomAttribute(references.WovenAssemblyCtor));
        NormalizeImportedCorelib(module);
        return errors;
    }
    private static void NormalizeImportedCorelib(ModuleDefinition module)
    {
        // A standalone net10 CLI must not introduce its System.Private.CoreLib into
        // a Unity/mscorlib or netstandard target when importing BCL signatures.
        var target = module.TypeSystem.Object.Scope;
        if (target is not AssemblyNameReference targetAssembly || targetAssembly.Name == "System.Private.CoreLib") return;
        foreach (var reference in module.GetTypeReferences())
            if (reference.Scope is AssemblyNameReference imported && imported.Name == "System.Private.CoreLib")
                reference.Scope = target;
        for (var i = module.AssemblyReferences.Count - 1; i >= 0; i--)
            if (module.AssemblyReferences[i].Name == "System.Private.CoreLib") module.AssemblyReferences.RemoveAt(i);
    }
    /// <summary>Unity Mono requires a MethodDebugInformation row even for generated/hidden methods.</summary>
    public static void EnsureSafePortableSymbols(ModuleDefinition module)
    {
        var document = new Document("bitkit-generated");
        foreach (var type in AllTypes(module.Types))
            foreach (var method in type.Methods)
                if (method.HasBody && method.Body.Instructions.Count != 0 && !method.DebugInformation.HasSequencePoints)
                    AddHiddenPoint(method, document);
        // A final body guarantees Cecil pads the debug table through the last MethodDef,
        // including preceding abstract/interface methods. An empty PDB (zero rows) makes
        // Unity Mono abort while ExceptionDispatchInfo captures a stack trace.
        const string name = "__BITKitPortableSymbols";
        var tail = module.Types.FirstOrDefault(t => t.Name == name);
        if (tail != null) module.Types.Remove(tail);
        else
        {
            tail = new TypeDefinition("", name, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
            var method = new MethodDefinition("Hidden", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.GetILProcessor().Emit(OpCodes.Ret); tail.Methods.Add(method);
            AddHiddenPoint(method, document);
        }
        module.Types.Add(tail);
    }
    private static void AddHiddenPoint(MethodDefinition method, Document document)
    {
        method.DebugInformation.SequencePoints.Add(new SequencePoint(method.Body.Instructions[0], document)
        { StartLine = 0xFEEFEE, EndLine = 0xFEEFEE, StartColumn = 0, EndColumn = 0 });
    }
    private static bool Has(MethodDefinition m, string fullName) => m.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);
    private static bool HasAsyncVoid(MethodDefinition m) => m.ReturnType.FullName == "System.Void" && m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
    private static bool Supported(TypeReference t, HashSet<string>? visiting = null, bool arrays = false)
    {
        if (t is ArrayType array) return arrays && array.Rank == 1 && Supported(array.ElementType, visiting, arrays: true);
        if (t.FullName == RpcTargetName || t.IsPrimitive || t.FullName == "System.String" || t.Resolve()?.IsEnum == true) return true;
        if (t is GenericInstanceType generic)
            return (generic.ElementType.FullName == "System.Nullable`1" || arrays && generic.ElementType.FullName == "System.ArraySegment`1") &&
                Supported(generic.GenericArguments[0], visiting, arrays);
        if (t.FullName.StartsWith("System.")) return false;
        var definition = t.Resolve();
        if (definition == null) return false;
        if (definition.IsValueType && definition.IsExplicitLayout) return false;
        // MemoryPack always writes unmanaged structs as raw fixed-size memory, even if
        // annotated. They have no object header/member ordering to preflight.
        if (definition.IsValueType && SupportedUnreliable(t))
            return !definition.CustomAttributes.Any(a => a.AttributeType.FullName == "MemoryPack.MemoryPackableAttribute" &&
                    (a.ConstructorArguments.Count != 1 || Convert.ToInt32(a.ConstructorArguments[0].Value) != 0 || a.Properties.Count != 0 || a.Fields.Count != 0) ||
                    a.AttributeType.FullName.Contains("MemoryPackUnion")) &&
                !definition.Fields.Any(f => f.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName))) &&
                !definition.Properties.Any(p => p.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName))) &&
                !definition.Methods.Any(m => m.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName)));
        bool generated = definition.CustomAttributes.Any(a => a.AttributeType.FullName == "MemoryPack.MemoryPackableAttribute" &&
            a.ConstructorArguments.Count == 1 && Convert.ToInt32(a.ConstructorArguments[0].Value) == 0 && a.Properties.Count == 0 && a.Fields.Count == 0);
        if (!generated || definition.CustomAttributes.Any(a => a.AttributeType.FullName.Contains("MemoryPackUnion") || ForbiddenReliableMemberAttribute(a.AttributeType.FullName)) ||
            definition.Methods.Any(m => m.IsConstructor && !m.IsStatic && (m.Parameters.Count != 0 || m.CustomAttributes.Any(a => a.AttributeType.FullName == "MemoryPack.MemoryPackConstructorAttribute"))) ||
            definition.Methods.Any(m => m.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName))) ||
            definition.IsAbstract || definition.IsInterface || !definition.IsValueType && definition.BaseType?.FullName != "System.Object" ||
            definition.Fields.Any(f => f.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName))) ||
            definition.Properties.Any(p => p.CustomAttributes.Any(a => ForbiddenReliableMemberAttribute(a.AttributeType.FullName)))) return false;
        visiting ??= new HashSet<string>();
        if (!visiting.Add(t.FullName)) return false;
        var members = definition.Fields.Where(f => f.IsPublic && !f.IsStatic).Cast<IMemberDefinition>()
            .Concat(definition.Properties.Where(p => p.GetMethod?.IsPublic == true && !p.HasParameters)).ToArray();
        var orders = members.Select(m => m.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "MemoryPack.MemoryPackOrderAttribute"))
            .Select(a => a == null ? -1 : (int)a.ConstructorArguments[0].Value).OrderBy(x => x).ToArray();
        bool result = members.Length is > 0 and <= 254 && orders.SequenceEqual(Enumerable.Range(0, members.Length)) &&
            members.All(m => m is FieldDefinition f ? Supported(f.FieldType, visiting, arrays) :
                m is PropertyDefinition p && p.SetMethod != null && Supported(p.PropertyType, visiting, arrays));
        visiting.Remove(t.FullName);
        return result;
    }
    private static bool ForbiddenReliableMemberAttribute(string name) =>
        name is "MemoryPack.MemoryPackIncludeAttribute" or "MemoryPack.MemoryPackIgnoreAttribute" or "MemoryPack.MemoryPackAllowSerializeAttribute" ||
        name.Contains("Formatter") || name.Contains("MemoryPackOn");
    private static bool SupportedUnreliable(TypeReference t, HashSet<string>? visiting = null, bool nested = false)
    {
        if (t.FullName == RpcTargetName || t.IsPrimitive || t.Resolve()?.IsEnum == true) return true;
        // Only a top-level string can be preflighted without walking arbitrary generated DTOs.
        if (t.FullName == "System.String") return !nested;
        if (t is ArrayType array) return !nested && array.Rank == 1 && SupportedUnreliable(array.ElementType, visiting, true);
        if (t is GenericInstanceType generic)
        {
            if (generic.ElementType.FullName == "System.Nullable`1") return SupportedUnreliable(generic.GenericArguments[0], visiting, true);
            return !nested && generic.ElementType.FullName == "System.ArraySegment`1" && SupportedUnreliable(generic.GenericArguments[0], visiting, true);
        }
        var definition = t.Resolve();
        if (definition == null || t.FullName.StartsWith("System.") || nested && !definition.IsValueType) return false;
        if (definition.IsValueType && definition.IsExplicitLayout) return false;
        visiting ??= new HashSet<string>();
        if (!visiting.Add(t.FullName)) return false;
        var fields = definition.Fields.Where(f => !f.IsStatic).ToArray();
        // Unmanaged structs need no generator. Reference-containing DTOs require an explicit
        // generated MemoryPack formatter; nested collections are refused to bound hostile decode.
        bool unmanaged = definition.IsValueType && fields.All(f => SupportedUnreliable(f.FieldType, visiting, true) &&
            f.FieldType.FullName != "System.String" && !(f.FieldType is ArrayType));
        bool annotated = definition.CustomAttributes.Any(a => a.AttributeType.FullName == "MemoryPack.MemoryPackableAttribute");
        bool valid = unmanaged || annotated && !nested && !definition.IsValueType &&
            (fields.Length > 0 || definition.Properties.Count > 0) &&
            fields.All(f => SupportedUnreliable(f.FieldType, visiting, true)) &&
            definition.Properties.Where(p => p.GetMethod?.IsPublic == true)
                .All(p => p.SetMethod != null && SupportedUnreliable(p.PropertyType, visiting, true));
        if (valid && !unmanaged)
        {
            var members = definition.Fields.Where(f => f.IsPublic && !f.IsStatic).Cast<IMemberDefinition>()
                .Concat(definition.Properties.Where(p => p.GetMethod?.IsPublic == true && !p.HasParameters)).ToArray();
            var orders = members.Select(m => m.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "MemoryPack.MemoryPackOrderAttribute"))
                .Select(a => a == null ? -1 : (int)a.ConstructorArguments[0].Value).OrderBy(x => x).ToArray();
            valid = members.Length is > 0 and <= 254 && orders.SequenceEqual(Enumerable.Range(0, members.Length));
        }
        visiting.Remove(t.FullName);
        return valid;
    }
    private static bool AutoSetter(MethodDefinition method, string property)
    {
        var x = method.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return x.Length == 4 && x[0].OpCode == OpCodes.Ldarg_0 && x[1].OpCode == OpCodes.Ldarg_1 && x[2].OpCode == OpCodes.Stfld && x[2].Operand is FieldReference f && f.Name == "<" + property + ">k__BackingField" && x[3].OpCode == OpCodes.Ret;
    }
    private static int CollectionKind(TypeReference type) => type is GenericInstanceType g ? g.ElementType.FullName switch
    {
        CoreNamespace + "SyncList`1" => 1,
        CoreNamespace + "SyncDictionary`2" => 2,
        CoreNamespace + "SyncHashSet`1" => 3,
        _ => 0
    } : 0;
    private static bool StableKey(TypeReference type) => type.IsPrimitive || type.FullName == "System.String" || type.Resolve()?.IsEnum == true;
    private static bool AutoGetter(PropertyDefinition property)
    {
        var x = property.GetMethod!.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return x.Length == 3 && x[0].OpCode == OpCodes.Ldarg_0 && x[1].OpCode == OpCodes.Ldfld &&
            x[1].Operand is FieldReference f && f.Name == "<" + property.Name + ">k__BackingField" && x[2].OpCode == OpCodes.Ret;
    }
    private static void EmitStateHook(PropertyDefinition property, MethodDefinition hook, int kind, ModuleDefinition module)
    {
        var method = new MethodDefinition("__bitkit_hook_" + property.Name,
            MethodAttributes.Private | MethodAttributes.HideBySig | (kind == 0 ? MethodAttributes.Static : 0), module.TypeSystem.Void);
        property.DeclaringType.Methods.Add(method);
        if (kind == 0)
        {
            foreach (var name in new[] { "owner", "oldValue", "newValue" })
                method.Parameters.Add(new ParameterDefinition(name, ParameterAttributes.None, module.TypeSystem.Object));
            var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, property.DeclaringType);
            il.Emit(OpCodes.Ldarg_1); il.Emit(property.PropertyType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, property.PropertyType);
            il.Emit(OpCodes.Ldarg_2); il.Emit(property.PropertyType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, property.PropertyType);
            il.Emit(OpCodes.Call, hook); il.Emit(OpCodes.Ret);
        }
        else
        {
            method.Parameters.Add(new ParameterDefinition("change", hook.Parameters[0].Attributes, hook.Parameters[0].ParameterType));
            var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, hook); il.Emit(OpCodes.Ret);
        }
    }
    private static MethodDefinition MoveBody(MethodDefinition method, string name)
    {
        var raw = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig, method.ReturnType);
        foreach (var p in method.Parameters) raw.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        raw.Body = method.Body;
        foreach (var instruction in raw.Body.Instructions)
            if (instruction.Operand is ParameterDefinition p) instruction.Operand = raw.Parameters[p.Index];
        method.DeclaringType.Methods.Add(raw);
        method.Body = new MethodBody(method);
        return raw;
    }
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
    private static bool GenericEnclosingType(TypeDefinition type) => type.HasGenericParameters || type.DeclaringType != null && GenericEnclosingType(type.DeclaringType);
    private static IEnumerable<TypeDefinition> Ancestors(TypeDefinition type)
    {
        for (var baseType = type.BaseType?.Resolve(); baseType != null; baseType = baseType.BaseType?.Resolve()) yield return baseType;
    }
    private static string TypeId(TypeReference t) => t is GenericInstanceType g ? g.ElementType.FullName.Replace('/', '+') + "<" + string.Join(",", g.GenericArguments.Select(TypeId)) + ">" : t.FullName.Replace('/', '+');
    private static string Identity(MethodDefinition m)
    {
        var signature = m.Name + "(" + string.Join(",", m.Parameters.Select(p => TypeId(p.ParameterType))) + "):" + TypeId(m.ReturnType);
        return m.DeclaringType.FullName.Replace('/', '+') + "/" + signature;
    }
    private static string StableHash(string text)
    { unchecked { uint hash = 2166136261; foreach (var c in text) { hash ^= c; hash *= 16777619; } return hash.ToString("x8"); } }
    private static ulong StableHash64(string text)
    { unchecked { ulong hash = 14695981039346656037UL; foreach (var c in text) { hash ^= c; hash *= 1099511628211UL; } return hash; } }
    private static string SchemaIdentity(TypeReference type, HashSet<string> visiting)
    {
        if (type.FullName == RpcTargetName) return "RpcTarget";
        if (type is ArrayType array) return "array" + array.Rank + "<" + SchemaIdentity(array.ElementType, visiting) + ">";
        if (type is GenericInstanceType generic) return generic.ElementType.FullName + "<" + string.Join(",", generic.GenericArguments.Select(t => SchemaIdentity(t, visiting))) + ">";
        var definition = type.Resolve();
        if (definition != null && definition.IsEnum)
        {
            var underlying = definition.Fields.Single(f => f.Name == "value__").FieldType.FullName;
            var labels = definition.Fields.Where(f => f.IsLiteral)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.Name + "=" + Convert.ToString(f.Constant, System.Globalization.CultureInfo.InvariantCulture));
            return "enum:" + type.FullName + ":" + underlying + "{" + string.Join(",", labels) + "}";
        }
        if (definition == null || type.IsPrimitive || type.FullName.StartsWith("System."))
            return type.FullName;
        if (!visiting.Add(type.FullName)) throw new InvalidOperationException("Cyclic typed RPC schema: " + type.FullName);
        try
        {
            if (definition.IsValueType && SupportedUnreliable(type))
                return "raw:" + type.FullName + ":seq=" + definition.IsSequentialLayout +
                    ":pack=" + definition.PackingSize + ":size=" + definition.ClassSize + "{" +
                    string.Join(",", definition.Fields.Where(f => !f.IsStatic).Select(f => f.Name + ":" + SchemaIdentity(f.FieldType, visiting) +
                        ":offset=" + f.Offset)) + "}";
            var members = definition.Fields.Where(f => f.IsPublic && !f.IsStatic).Cast<IMemberDefinition>()
                .Concat(definition.Properties.Where(p => p.GetMethod?.IsPublic == true && !p.HasParameters))
                .OrderBy(m => (int)m.CustomAttributes.First(a => a.AttributeType.FullName == "MemoryPack.MemoryPackOrderAttribute").ConstructorArguments[0].Value);
            return "dto:" + type.FullName + "{" + string.Join(",", members.Select(m =>
                m.Name + ":" + SchemaIdentity(m is FieldDefinition f ? f.FieldType : ((PropertyDefinition)m).PropertyType, visiting))) + "}";
        }
        finally { visiting.Remove(type.FullName); }
    }
    private static void EmitTypedReceiver(MethodDefinition method, MethodDefinition raw, string id, ModuleDefinition module, CoreReferences core)
    {
        var receive = new MethodDefinition("__bitkit_recv_" + method.Name + "_" + StableHash(id),
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            core.ReceiverReturn);
        receive.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.TypeSystem.Object));
        receive.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None,
            new ByReferenceType(core.TypedReader)));
        method.DeclaringType.Methods.Add(receive);
        var il = receive.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, method.DeclaringType);
        foreach (var p in method.Parameters)
        {
            il.Emit(OpCodes.Ldarg_1);
            if (p.ParameterType.FullName == RpcTargetName) il.Emit(OpCodes.Call, core.ReadTarget);
            else
            {
                var read = new GenericInstanceMethod(core.Read); read.GenericArguments.Add(p.ParameterType);
                il.Emit(OpCodes.Call, read);
            }
        }
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, core.ReadComplete);
        il.Emit(OpCodes.Call, raw);
        if (method.ReturnType.FullName == "System.Void")
            il.Emit(OpCodes.Ldsfld, core.VoidResult);
        else if (method.ReturnType.FullName == "System.Threading.Tasks.Task")
            il.Emit(OpCodes.Call, core.CompleteTask);
        else
        {
            var finish = new GenericInstanceMethod(core.CompleteTaskValue);
            finish.GenericArguments.Add(((GenericInstanceType)method.ReturnType).GenericArguments[0]);
            il.Emit(OpCodes.Call, finish);
        }
        il.Emit(OpCodes.Ret);
    }
    private static void EmitTypedWrapper(MethodDefinition method, MethodDefinition raw, SendTo route, RpcDelivery delivery, ulong numeric, ulong fingerprint,
        MethodReference begin, ModuleDefinition module, CoreReferences core)
    {
        var body = method.Body; body.InitLocals = true;
        var writerLocal = new VariableDefinition(core.TypedWriter);
        var targetLocal = new VariableDefinition(core.RpcTarget);
        body.Variables.Add(writerLocal); body.Variables.Add(targetLocal);
        VariableDefinition? resultLocal = null;
        if (method.ReturnType.FullName != "System.Void") { resultLocal = new VariableDefinition(method.ReturnType); body.Variables.Add(resultLocal); }
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I8, unchecked((long)numeric));
        il.Emit(OpCodes.Ldc_I8, unchecked((long)fingerprint)); il.Emit(OpCodes.Ldc_I4, (int)route);
        il.Emit(OpCodes.Ldc_I4, (int)delivery);
        var target = method.Parameters.FirstOrDefault(p => p.ParameterType.FullName == RpcTargetName);
        if (target != null) il.Emit(OpCodes.Ldarg, target);
        else { il.Emit(OpCodes.Ldloca, targetLocal); il.Emit(OpCodes.Initobj, targetLocal.VariableType); il.Emit(OpCodes.Ldloc, targetLocal); }
        il.Emit(OpCodes.Call, begin); il.Emit(OpCodes.Stloc, writerLocal);
        var tryStart = il.Create(OpCodes.Nop); il.Append(tryStart);
        foreach (var p in method.Parameters)
        {
            if (p == target) continue;
            il.Emit(OpCodes.Ldloca, writerLocal); il.Emit(OpCodes.Ldarg, p);
            var write = new GenericInstanceMethod(core.Write); write.GenericArguments.Add(p.ParameterType);
            il.Emit(OpCodes.Call, write);
        }
        if (method.ReturnType.FullName == "System.Void")
        {
            il.Emit(OpCodes.Ldloca, writerLocal);
            il.Emit(OpCodes.Call, core.PrepareVoid);
            il.Emit(OpCodes.Ldloca, writerLocal);
            il.Emit(OpCodes.Call, core.ShouldExecuteLocal);
            var remote = il.Create(OpCodes.Nop); il.Emit(OpCodes.Brfalse, remote);
            il.Emit(OpCodes.Ldloca, writerLocal);
            il.Emit(OpCodes.Call, core.EnterLocal);
            il.Emit(OpCodes.Ldarg_0);
            foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Call, raw);
            il.Emit(OpCodes.Ldloca, writerLocal);
            il.Emit(OpCodes.Call, core.ExitLocal);
            il.Append(remote);
        }
        il.Emit(OpCodes.Ldloca, writerLocal);
        if (resultLocal == null) il.Emit(OpCodes.Call, core.FinishVoid);
        else if (method.ReturnType.FullName == "System.Threading.Tasks.Task")
            il.Emit(OpCodes.Call, core.FinishTask);
        else
        {
            var finish = new GenericInstanceMethod(core.FinishTaskValue);
            finish.GenericArguments.Add(((GenericInstanceType)method.ReturnType).GenericArguments[0]);
            il.Emit(OpCodes.Call, finish);
        }
        if (resultLocal != null) il.Emit(OpCodes.Stloc, resultLocal);
        var exit = il.Create(OpCodes.Nop); il.Emit(OpCodes.Leave, exit);
        var finallyStart = il.Create(OpCodes.Ldloca, writerLocal); il.Append(finallyStart);
        il.Emit(OpCodes.Call, core.Dispose);
        il.Emit(OpCodes.Endfinally); il.Append(exit);
        if (resultLocal != null) il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        { TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = exit });
    }
}
}
