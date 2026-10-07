using System;

namespace BITKit.Multiplayer
{
    public enum SendTo { Host, All }
    public enum RpcDelivery { Reliable, Unreliable }

    /// <summary>Editor source-generation output for a public remote interface.</summary>
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

    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class WovenAssemblyAttribute : Attribute { }
}
