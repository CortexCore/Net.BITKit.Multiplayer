using BITKit.Multiplayer;

namespace Fixture;

public interface IHotProbe
{
    void Tick(int value);
    void Poses(ArraySegment<AnnotatedScalar> values);
    Task<int> Echo(int value);
}
