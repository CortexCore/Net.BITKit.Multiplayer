using MemoryPack;

namespace Fixture;

[MemoryPackable]
public sealed partial class NestedDetail
{
    [MemoryPackOrder(0)] public int Code { get; set; }
    [MemoryPackOrder(1)] public string Label { get; set; } = "";
}

[MemoryPackable]
public sealed partial class NestedSnapshot
{
    [MemoryPackOrder(0)] public string Scope { get; set; } = "";
    [MemoryPackOrder(1)] public long Tick { get; set; }
    [MemoryPackOrder(2)] public NestedDetail[] Rows { get; set; } = System.Array.Empty<NestedDetail>();
    [MemoryPackOrder(3)] public int Total { get; set; }
}

[MemoryPackable]
public sealed partial class BinaryState
{
    [MemoryPackOrder(0)] public string Scope { get; set; } = "";
    [MemoryPackOrder(1)] public long Tick { get; set; }
    [MemoryPackOrder(2)] public NestedDetail Detail { get; set; } = new();
}

// Deliberately declares a property before a field, but gives the field the first
// explicit wire index. A preflight based on metadata tokens would misparse this.
[MemoryPackable]
public sealed partial class MixedOrderDto
{
    [MemoryPackOrder(1)] public string Label { get; set; } = "";
    [MemoryPackOrder(0)] public int Code;
    [MemoryPackOrder(2)] public NestedDetail[] Rows { get; set; } = System.Array.Empty<NestedDetail>();
}

[MemoryPackable]
public partial struct AnnotatedScalar
{
    [MemoryPackOrder(0)] public int Value;
}

[MemoryPackable]
public sealed partial class UnorderedDto
{
    public int Value { get; set; }
}

[MemoryPackable]
public sealed partial class IgnoredDto
{
    [MemoryPackOrder(0)] public int Value { get; set; }
    [MemoryPackIgnore] public int Hidden { get; set; }
}

[MemoryPackable]
public sealed partial class PrivateIncludedDto
{
    [MemoryPackOrder(0)] public int Value { get; set; }
    [MemoryPackInclude, MemoryPackOrder(1)] private int hidden = 1;
}

[MemoryPackable(SerializeLayout.Explicit)]
public sealed partial class CustomLayoutDto
{
    [MemoryPackOrder(0)] public int Value { get; set; }
}

public interface IBinaryService
{
    System.Threading.Tasks.Task<NestedSnapshot> Snapshot(NestedSnapshot snapshot);
    System.Threading.Tasks.Task<AnnotatedScalar> EchoScalar(AnnotatedScalar value);
    void Publish(NestedSnapshot snapshot);
}
