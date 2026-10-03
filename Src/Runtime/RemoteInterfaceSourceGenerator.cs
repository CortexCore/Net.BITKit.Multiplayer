using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    /// <summary>Emits native proxies and typed MessagePack receivers. Compile at build time or with the optional compiler adapter.</summary>
    public static class RemoteInterfaceSourceGenerator
    {
        public static string Generate<T>() => Generate(typeof(T));
        public static string ProxyName(Type contract) => "NetRemote_" + RpcContextService.ContractId(contract);
        public static string Generate(Type contract)
        {
            if (!contract.IsInterface || !contract.IsVisible || contract.ContainsGenericParameters)
                throw new NotSupportedException("Remote contract must be a public closed interface: " + contract);
            if (new[] { contract }.Concat(contract.GetInterfaces()).Any(t => t.GetEvents().Length != 0)) throw new NotSupportedException("Remote interface events require an explicit RPC method.");
            var source = new StringBuilder("#nullable enable\nusing System;\nusing System.Threading.Tasks;\nusing Cysharp.Threading.Tasks;\nusing BITKit.Multiplayer.NetRpc;\n");
            var name = ProxyName(contract);
            source.Append("public sealed class ").Append(name).Append(" : ").Append(TypeName(contract)).AppendLine(" {");
            source.AppendLine("private readonly RpcContext _context;");
            source.Append("public ").Append(name).AppendLine("(RpcContext context) { _context = context; }");
            var properties = new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(t => t.GetProperties()).GroupBy(p => p.Name).Select(g => g.First()).ToArray();
            foreach (var property in properties)
            {
                if (property.SetMethod != null || property.GetIndexParameters().Length != 0)
                    throw new NotSupportedException("Remote state requires getter-only properties: " + property);
                var type = property.PropertyType;
                bool collection = type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IList<>) || type.GetGenericTypeDefinition() == typeof(IDictionary<,>));
                if (!collection && (type == typeof(object) || type.IsInterface || type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
                    throw new NotSupportedException("Remote state collections must declare IList<T> or IDictionary<TKey,TValue>; scalar state needs a concrete value schema: " + property);
                var id = RpcContextService.PropertyId(contract, property.Name);
                source.Append("public ").Append(TypeName(type)).Append(" @").Append(property.Name).Append(" => _context.");
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>)) source.Append("GetList<").Append(TypeName(type.GetGenericArguments()[0]));
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)) source.Append("GetDictionary<").Append(string.Join(",", type.GetGenericArguments().Select(TypeName)));
                else source.Append("GetValue<").Append(TypeName(type));
                source.Append(">(").Append(id).AppendLine("u);");
            }
            foreach (var method in RpcContextService.ContractMethods(contract).GroupBy(m => m.ToString()).Select(g => g.First()))
                Emit(source, contract, method);
            source.AppendLine("}"); return source.ToString();
        }
        private static void Emit(StringBuilder source, Type contract, MethodInfo method)
        {
            var returnType = method.ReturnType;
            var attribute = method.GetCustomAttribute<RpcAttribute>();
            if (attribute != null && attribute.To != SendTo.Host) throw new NotSupportedException("Remote interfaces are Host-directed; use an ordinary woven RPC for broadcast: " + method);
            bool isVoid = returnType == typeof(void), task = returnType == typeof(Task) || returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>);
            bool valueTask = returnType == typeof(ValueTask) || returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
            bool uniTask = returnType.FullName?.StartsWith("Cysharp.Threading.Tasks.UniTask") == true;
            if (method.IsGenericMethod || method.GetParameters().Any(p => p.ParameterType.IsByRef) || !(isVoid || task || valueTask || uniTask))
                throw new NotSupportedException("RPC requires void/Task/ValueTask/UniTask and non-generic, non-ref parameters: " + method);
            if (attribute?.Delivery == RpcDelivery.Unreliable && !isVoid) throw new NotSupportedException("Unreliable RPC requires void: " + method);
            var result = returnType.IsGenericType ? returnType.GetGenericArguments()[0] : null;
            var id = RpcContextService.RpcMethodId(contract, method);
            var parameters = method.GetParameters();
            var declaration = string.Join(", ", parameters.Select(p => TypeName(p.ParameterType) + " @" + p.Name));
            source.Append("public ").Append(TypeName(returnType)).Append(" @").Append(method.Name).Append('(').Append(declaration).AppendLine(") {");
            source.AppendLine("using var bag = NetMessageBag.Pool();");
            foreach (var parameter in parameters) source.Append("bag.Write<").Append(TypeName(parameter.ParameterType)).Append(">(@").Append(parameter.Name).AppendLine(");");
            source.Append("var model = new NetRpcModel(NetRpcMessageKind.Call, _context.TargetId, ").Append(id).AppendLine("u, 0, bag.Count, bag.Memory);");
            if (isVoid) source.AppendLine(attribute?.Delivery == RpcDelivery.Unreliable ? "_context.NotifyFast(model);" : "_context.Notify(model);");
            else
            {
                source.Append("return _context.").Append(uniTask ? "Request" : valueTask ? "RequestValue" : "RequestTask");
                if (result != null) source.Append('<').Append(TypeName(result)).Append('>');
                source.AppendLine("(model);");
            }
            source.AppendLine("}");
            source.Append("public static UniTask<NetMessageBag?> __netrpc_recv_").Append(id).AppendLine("(object instance, NetMessageReader reader) {");
            foreach (var parameter in parameters) source.Append("var @").Append(parameter.Name).Append(" = reader.Read<").Append(TypeName(parameter.ParameterType)).AppendLine(">();");
            source.AppendLine("reader.Complete();");
            var invocation = "((" + TypeName(contract) + ")instance).@" + method.Name + "(" + string.Join(",", parameters.Select(p => "@" + p.Name)) + ")";
            if (isVoid) source.Append(invocation).AppendLine("; return NetRpcResults.Void;");
            else source.Append(uniTask ? "return NetRpcResults.CompleteUniTask(" : valueTask ? "return NetRpcResults.CompleteValueTask(" : "return NetRpcResults.CompleteTask(")
                .Append(invocation).AppendLine(");");
            source.AppendLine("}");
        }
        private static string TypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type.IsArray) { if (type.GetArrayRank() != 1) throw new NotSupportedException("Remote arrays must be one-dimensional."); return TypeName(type.GetElementType()!) + "[]"; }
            if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
            if (type.IsNested && type.DeclaringType!.IsGenericType) throw new NotSupportedException("Nested generic contracts require a non-generic declaring type.");
            var name = type.GetGenericTypeDefinition().FullName!.Split('`')[0].Replace('+', '.');
            return "global::" + name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }
    }
}
