using MemoryPack;
namespace Fixture;
[MemoryPackable]
public partial class SyncItem
{
    [MemoryPackOrder(0)] public int Count { get; set; }
    [MemoryPackOrder(1)] public string Name { get; set; } = "";
}
