using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MessagePack;

namespace NetRpcPerformance.Contracts;

[MessagePackObject]
public sealed class BoundedDto
{
    [Key(0)] public int Sequence { get; set; }
    [Key(1)] public byte[] Payload { get; set; } = System.Array.Empty<byte>();
}

public interface IWorkload
{
    UniTask<int> Scalar(int value);
    UniTask<int> Dto(BoundedDto value);
    UniTask<int> Bytes(byte[] value);
    UniTask<int> Fence();
    int ScalarState { get; }
    IList<int> Items { get; }
    IDictionary<int, int> Counts { get; }
}
