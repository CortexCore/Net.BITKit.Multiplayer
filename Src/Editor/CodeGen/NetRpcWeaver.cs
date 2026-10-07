using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BITKit.Multiplayer.CodeGen
{
    public static partial class Weaver
    {
        /// <summary>Compile ordinary Rpc methods into the single MessagePack NetRpc backend.</summary>
        public static IReadOnlyList<string> WeaveNetRpc(string input, string output)
        {
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!); resolver.AddSearchDirectory(AppContext.BaseDirectory);
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            var errors = WeaveNetRpcModule(module);
            if (errors.Count == 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); module.Write(output); }
            return errors;
        }
        public static IReadOnlyList<string> WeaveNetRpcModule(ModuleDefinition module,
            Func<TypeDefinition, bool>? includeType = null, bool markAssembly = true)
        {
            var errors = new List<string>();
            if (module.Assembly.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == WovenAssemblyName))
                return new[] { "Assembly is already woven." };
            var methods = AllTypes(module.Types).Where(t => includeType == null || includeType(t))
                .SelectMany(t => t.Methods).Where(m => !m.IsAbstract && Has(m, RpcAttributeName)).ToArray();
            foreach (var method in methods)
            {
                var attribute = method.CustomAttributes.First(a => a.AttributeType.FullName == RpcAttributeName);
                var route = (SendTo)(int)attribute.ConstructorArguments[0].Value;
                var delivery = Delivery(attribute);
                bool supportedReturn = method.ReturnType.FullName == "System.Void" || AsyncKind(method.ReturnType) != 0;
                if (!method.HasBody || method.IsStatic || method.HasGenericParameters || GenericEnclosingType(method.DeclaringType) ||
                    method.Parameters.Any(p => p.ParameterType is ByReferenceType || p.ParameterType is PointerType) || HasAsyncVoid(method) ||
                    !supportedReturn || route != SendTo.Host && route != SendTo.All || route == SendTo.All && method.ReturnType.FullName != "System.Void" ||
                    delivery == RpcDelivery.Unreliable && method.ReturnType.FullName != "System.Void" || !Enum.IsDefined(typeof(RpcDelivery), delivery) ||
                    method.DeclaringType.Methods.Any(m => m.Name.StartsWith("__netrpc_body_")))
                    errors.Add(method.FullName + ": NetRpc supports non-generic instance Host/All RPCs returning void/Task/ValueTask/UniTask; All and Unreliable require void; ref/out/async void/repeated weaving are invalid.");
            }
            var rpcTypes = methods.Select(method => method.DeclaringType).Distinct().ToArray();
            var contexts = new Dictionary<TypeDefinition, (MethodDefinition Constructor, ParameterDefinition Parameter)[]>();
            foreach (var type in rpcTypes)
            {
                var contextConstructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic)
                    .SelectMany(constructor => constructor.Parameters.Where(parameter => IsRpcContext(parameter.ParameterType))
                        .Select(parameter => (Constructor: constructor, Parameter: parameter))).ToArray();
                if (contextConstructors.Length == 0)
                    errors.Add(type.FullName + ": an RPC service must declare a source constructor parameter IRpcContext<TContract>; NetRpc does not generate constructors.");
                else
                {
                    var contracts = contextConstructors.Select(item => ((GenericInstanceType)item.Parameter.ParameterType).GenericArguments[0])
                        .GroupBy(contract => contract.FullName).ToArray();
                    if (contracts.Length != 1)
                        errors.Add(type.FullName + ": all IRpcContext<TContract> constructor parameters must use the same contract.");
                    else if (!IsAssignableTo(type, contracts[0].First()))
                        errors.Add(type.FullName + ": IRpcContext<TContract> must use the RPC implementation type or one of its implemented contracts.");
                    else
                        contexts[type] = contextConstructors;
                }

                var businessDispose = type.Methods.FirstOrDefault(method => !method.IsStatic && !method.IsAbstract && method.HasBody &&
                    method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Void" &&
                    (method.Name == "Dispose" || method.Name.EndsWith(".Dispose", StringComparison.Ordinal)));
                if (!Implements(type, "System.IDisposable") || businessDispose == null)
                    errors.Add(type.FullName + ": an RPC service must implement System.IDisposable and declare its Dispose method in source; NetRpc does not add lifecycle interfaces or methods.");
            }
            if (errors.Count != 0 || methods.Length == 0) return errors;
            var core = module.AssemblyResolver.Resolve(module.AssemblyReferences.FirstOrDefault(a => a.Name == CoreName)
                ?? new AssemblyNameReference(CoreName, new Version(1, 0, 0, 0))).MainModule;
            TypeDefinition Type(string name) => core.GetType("BITKit.Multiplayer.NetRpc." + name);
            MethodReference Method(string type, string name, int generics = 0, int parameters = -1) => module.ImportReference(Type(type).Methods.Single(m =>
                m.Name == name && m.GenericParameters.Count == generics && (parameters < 0 || m.Parameters.Count == parameters)));
            var invocationType = module.ImportReference(Type("NetRpcInvocation"));
            var begin = Method("NetRpcDispatch", "Begin", parameters: 5); var write = Method("NetRpcInvocation", "Write", 1);
            var dispose = Method("NetRpcInvocation", "Dispose"); var read = Method("NetMessageReader", "Read", 1); var complete = Method("NetMessageReader", "Complete");
            var register = Method("IRpcContext", "Register", parameters: 1);
            var contextFields = rpcTypes.ToDictionary(type => type, type =>
            {
                var field = new FieldDefinition("__netrpc_context", FieldAttributes.Private, module.ImportReference(Type("IRpcContext")));
                type.Fields.Add(field);
                foreach (var context in contexts[type])
                    InjectContextRegistration(context.Constructor, context.Parameter, field, register);
                return field;
            });
            foreach (var method in methods)
            {
                uint id = BITKit.Multiplayer.NetRpc.RpcContextService.StableId(method.DeclaringType.FullName.Replace('/', '+') + "/" + method.Name + "/" + string.Join(",", method.Parameters.Select(p => TypeId(p.ParameterType))) + "/" + TypeId(method.ReturnType));
                var attribute = method.CustomAttributes.First(a => a.AttributeType.FullName == RpcAttributeName);
                var route = (SendTo)(int)attribute.ConstructorArguments[0].Value; var delivery = Delivery(attribute);
                var raw = MoveBody(method, "__netrpc_body_" + id);
                EmitNetReceiver(method, raw, id, module, Type("NetMessageReader"), read, complete, Type("NetRpcResults"));
                var body = method.Body; body.InitLocals = true;
                var writer = new VariableDefinition(invocationType); body.Variables.Add(writer);
                VariableDefinition? result = null;
                if (method.ReturnType.FullName != "System.Void") { result = new VariableDefinition(method.ReturnType); body.Variables.Add(result); }
                var il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, contextFields[method.DeclaringType]);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)id)); il.Emit(OpCodes.Ldc_I4, (int)route); il.Emit(OpCodes.Ldc_I4, (int)delivery);
                il.Emit(OpCodes.Call, begin); il.Emit(OpCodes.Stloc, writer);
                var start = il.Create(OpCodes.Nop); il.Append(start);
                var end = il.Create(OpCodes.Nop);
                if (route == SendTo.Host)
                {
                    il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "ShouldExecuteLocal"));
                    var remote = il.Create(OpCodes.Nop); il.Emit(OpCodes.Brfalse, remote);
                    il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "EnterLocal"));
                    il.Emit(OpCodes.Ldarg_0); foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
                    il.Emit(OpCodes.Call, raw); if (result != null) il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "ExitLocal"));
                    il.Emit(OpCodes.Leave, end); il.Append(remote);
                }
                foreach (var p in method.Parameters)
                { il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Ldarg, p); var w = new GenericInstanceMethod(write); w.GenericArguments.Add(p.ParameterType); il.Emit(OpCodes.Callvirt, w); }
                if (route == SendTo.All)
                {
                    // Freeze outgoing arguments before the local body can mutate reference arguments.
                    il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "EnterLocal"));
                    il.Emit(OpCodes.Ldarg_0); foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
                    il.Emit(OpCodes.Call, raw);
                    il.Emit(OpCodes.Ldloc, writer); il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "ExitLocal"));
                }
                il.Emit(OpCodes.Ldloc, writer);
                if (result == null) il.Emit(OpCodes.Callvirt, Method("NetRpcInvocation", "FinishVoid"));
                else
                {
                    var kind = AsyncKind(method.ReturnType); var value = method.ReturnType as GenericInstanceType;
                    var finish = Method("NetRpcInvocation", kind == 3 ? "FinishUniTask" : kind == 2 ? "FinishValueTask" : "FinishTask", value == null ? 0 : 1);
                    if (value != null) { var generic = new GenericInstanceMethod(finish); generic.GenericArguments.Add(value.GenericArguments[0]); finish = generic; }
                    il.Emit(OpCodes.Callvirt, finish);
                    il.Emit(OpCodes.Stloc, result);
                }
                il.Emit(OpCodes.Leave, end);
                var finallyStart = il.Create(OpCodes.Ldloc, writer); il.Append(finallyStart); il.Emit(OpCodes.Callvirt, dispose); il.Emit(OpCodes.Endfinally);
                il.Append(end); if (result != null) il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
                body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = start, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = end });
            }
            if (markAssembly) MarkWovenAssembly(module);
            NormalizeImportedCorelib(module); return errors;
        }
        private static RpcDelivery Delivery(CustomAttribute attribute) => attribute.Properties.Where(p => p.Name == "Delivery")
            .Select(p => (RpcDelivery)(int)p.Argument.Value).DefaultIfEmpty(attribute.ConstructorArguments.Count > 1 ? (RpcDelivery)(int)attribute.ConstructorArguments[1].Value : RpcDelivery.Reliable).First();
        private static int AsyncKind(TypeReference type)
        {
            string name = type is GenericInstanceType g ? g.ElementType.FullName : type.FullName;
            return name == "System.Threading.Tasks.Task" || name == "System.Threading.Tasks.Task`1" ? 1 :
                name == "System.Threading.Tasks.ValueTask" || name == "System.Threading.Tasks.ValueTask`1" ? 2 :
                name == "Cysharp.Threading.Tasks.UniTask" || name == "Cysharp.Threading.Tasks.UniTask`1" ? 3 : 0;
        }

        private static bool IsRpcContext(TypeReference type) => type is GenericInstanceType generic &&
            generic.ElementType.FullName == "BITKit.Multiplayer.NetRpc.IRpcContext`1";

        private static bool Implements(TypeDefinition type, string contract) =>
            IsAssignableTo(type, contract, new HashSet<string>());

        private static bool IsAssignableTo(TypeDefinition type, TypeReference contract) =>
            IsAssignableTo(type, contract.FullName, new HashSet<string>());

        private static bool IsAssignableTo(TypeDefinition? type, string contract, HashSet<string> visited)
        {
            if (type == null || !visited.Add(type.FullName)) return false;
            if (type.FullName == contract) return true;
            if (type.Interfaces.Any(item => item.InterfaceType.FullName == contract ||
                IsAssignableTo(Resolve(item.InterfaceType), contract, visited))) return true;
            return IsAssignableTo(Resolve(type.BaseType), contract, visited);
        }

        private static TypeDefinition? Resolve(TypeReference? type)
        {
            if (type == null) return null;
            try { return type.Resolve(); }
            catch { return null; }
        }

        private static void InjectContextRegistration(MethodDefinition constructor, ParameterDefinition context,
            FieldReference field, MethodReference register)
        {
            foreach (var instruction in constructor.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray())
            {
                var il = constructor.Body.GetILProcessor();
                var skip = il.Create(OpCodes.Nop);
                il.InsertBefore(instruction, il.Create(OpCodes.Ldarg, context));
                il.InsertBefore(instruction, il.Create(OpCodes.Brfalse, skip));
                il.InsertBefore(instruction, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(instruction, il.Create(OpCodes.Ldarg, context));
                il.InsertBefore(instruction, il.Create(OpCodes.Stfld, field));
                il.InsertBefore(instruction, il.Create(OpCodes.Ldarg, context));
                il.InsertBefore(instruction, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(instruction, il.Create(OpCodes.Callvirt, register));
                il.InsertBefore(instruction, il.Create(OpCodes.Pop));
                il.InsertBefore(instruction, skip);
            }
        }

        private static void EmitNetReceiver(MethodDefinition method, MethodDefinition raw, uint id, ModuleDefinition module,
            TypeDefinition reader, MethodReference read, MethodReference complete, TypeDefinition results)
        {
            var value = method.ReturnType as GenericInstanceType;
            var kind = AsyncKind(method.ReturnType);
            var resultMethod = results.Methods.Single(m => m.Name == (kind == 3 ? "CompleteUniTask" : kind == 2 ? "CompleteValueTask" : "CompleteTask") && m.GenericParameters.Count == (value == null ? 0 : 1));
            var receive = new MethodDefinition("__netrpc_recv_" + id, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.ImportReference(resultMethod.ReturnType));
            receive.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.TypeSystem.Object));
            receive.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, module.ImportReference(reader)));
            method.DeclaringType.Methods.Add(receive); receive.Body.InitLocals = true;
            var il = receive.Body.GetILProcessor();
            foreach (var p in method.Parameters)
            {
                var local = new VariableDefinition(p.ParameterType); receive.Body.Variables.Add(local);
                il.Emit(OpCodes.Ldarg_1); var r = new GenericInstanceMethod(read); r.GenericArguments.Add(p.ParameterType); il.Emit(OpCodes.Callvirt, r); il.Emit(OpCodes.Stloc, local);
            }
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, complete);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, method.DeclaringType);
            foreach (var local in receive.Body.Variables) il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Call, raw);
            if (method.ReturnType.FullName == "System.Void") il.Emit(OpCodes.Ldsfld, module.ImportReference(results.Fields.Single(f => f.Name == "Void")));
            else
            {
                MethodReference finish = module.ImportReference(resultMethod);
                if (value != null) { var generic = new GenericInstanceMethod(finish); generic.GenericArguments.Add(value.GenericArguments[0]); finish = generic; }
                il.Emit(OpCodes.Call, finish);
            }
            il.Emit(OpCodes.Ret);
        }
    }
}
