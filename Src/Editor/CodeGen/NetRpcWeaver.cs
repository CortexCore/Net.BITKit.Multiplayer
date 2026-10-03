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
        /// <summary>Design-v1 MessagePack backend. Legacy B6 weaving remains available to existing integrations.</summary>
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
        public static IReadOnlyList<string> WeaveNetRpcModule(ModuleDefinition module)
        {
            var errors = new List<string>();
            var methods = AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => !m.IsAbstract && Has(m, RpcAttributeName)).ToArray();
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
                    method.DeclaringType.Methods.Any(m => m.Name.StartsWith("__netrpc_body_")) || Has(method, WovenRpcName))
                    errors.Add(method.FullName + ": NetRpc supports non-generic instance Host/All RPCs returning void/Task/ValueTask/UniTask; All and Unreliable require void; ref/out/async void/repeated weaving are invalid.");
            }
            if (errors.Count != 0 || methods.Length == 0) return errors;
            var core = module.AssemblyResolver.Resolve(module.AssemblyReferences.FirstOrDefault(a => a.Name == CoreName)
                ?? new AssemblyNameReference(CoreName, new Version(1, 0, 0, 0))).MainModule;
            TypeDefinition Type(string name) => core.GetType("BITKit.Multiplayer.NetRpc." + name);
            MethodReference Method(string type, string name, int generics = 0) => module.ImportReference(Type(type).Methods.Single(m => m.Name == name && m.GenericParameters.Count == generics));
            var invocationType = module.ImportReference(Type("NetRpcInvocation"));
            var begin = Method("NetRpcDispatch", "Begin"); var write = Method("NetRpcInvocation", "Write", 1);
            var dispose = Method("NetRpcInvocation", "Dispose"); var read = Method("NetMessageReader", "Read", 1); var complete = Method("NetMessageReader", "Complete");
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
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, unchecked((int)id)); il.Emit(OpCodes.Ldc_I4, (int)route); il.Emit(OpCodes.Ldc_I4, (int)delivery);
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
            var contracts = module.AssemblyResolver.Resolve(module.AssemblyReferences.First(a => a.Name == ContractsName)).MainModule;
            var markerType = contracts.GetType(WovenAssemblyName);
            module.Assembly.CustomAttributes.Add(new CustomAttribute(module.ImportReference(markerType.Methods.Single(m => m.IsConstructor && !m.HasParameters))));
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
