using System;

namespace BITKit.Multiplayer
{
    public enum SendTo { Host, All, Target }
    public enum RpcDelivery { Reliable, Unreliable }

    /// <summary>Opt an assembly into the MessagePack NetRpc backend; legacy assemblies keep their existing backend.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class NetRpcBackendAttribute : Attribute { }

    /// <summary>Editor source-generation output for a public remote interface, in a writable Unity asset/package path.</summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class NetRpcRemoteContractAttribute : Attribute
    {
        public NetRpcRemoteContractAttribute(string outputAssetPath) => OutputAssetPath = outputAssetPath;
        public string OutputAssetPath { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RpcAttribute : Attribute
    {
        public RpcAttribute(SendTo to) => To = to;
        public RpcAttribute(SendTo to, RpcDelivery delivery) { To = to; Delivery = delivery; }
        public SendTo To { get; }
        public RpcDelivery Delivery { get; set; } = RpcDelivery.Reliable;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SyncVarAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class HookAttribute : Attribute
    {
        public HookAttribute(string method) => Method = method;
        public string Method { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HostOnlyAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientOnlyAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class WovenRpcAttribute : Attribute
    {
        public WovenRpcAttribute(string id) => Id = id;
        public string Id { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class WovenTypedRpcAttribute : Attribute
    {
        public WovenTypedRpcAttribute(ulong method, ulong fingerprint)
        {
            Method = method;
            Fingerprint = fingerprint;
        }

        public ulong Method { get; }
        public ulong Fingerprint { get; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class WovenSyncVarAttribute : Attribute
    {
        public WovenSyncVarAttribute(ulong fingerprint) => Fingerprint = fingerprint;
        public ulong Fingerprint { get; }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class WovenAssemblyAttribute : Attribute { }
}
