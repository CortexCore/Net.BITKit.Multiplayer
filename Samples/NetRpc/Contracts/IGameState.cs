using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace NetRpc.Sample.Contracts
{
    public interface IGameState
    {
        UniTask<int> Plus(int a, int b);
        UniTask Damage(int damage);
        int Health { get; }
        IList<int> Items { get; }
        IDictionary<int, int> Counts { get; }
    }
}
