using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.Samples.NetRpcGodot.Human
{
    // ① 两端共享的业务接口。Client 注入它，Host 提供实现。
    public interface IWorkshop
    {
        UniTask<int> Plus(int a, int b);
        UniTask<int> AddCoins(int amount);
        UniTask<bool> BuyApple();
        UniTask<int> BroadcastPulse();

        // 这些 getter 读取 Client 已同步到本地的数据，不是每次都请求 Host。
        int Coins { get; }
        IList<string> Messages { get; }
        IDictionary<string, int> Inventory { get; }
    }
}
