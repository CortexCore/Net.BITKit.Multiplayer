using BITKit.Multiplayer;
using System.Threading.Tasks;

namespace Fixture;

public interface ICounter
{
    Task<int> Add(int n);
    Task<int> Read(RpcTarget target, int n);
    void Announce(string message);
    Task BroadcastAsync();
    Task Fail();
    Task Slow();
    Task<int> Delayed(int n);
}

public interface ILeft { Task<int> Ping(int value); }
public interface IRight { Task<int> Ping(int value); }
public interface IBase { Task<int> Base(int value); }
public interface IChild : IBase { }
public interface IExplicit { Task<int> Special(int value); }
public interface IGeneric<T> { Task<T> Echo(T value); }
