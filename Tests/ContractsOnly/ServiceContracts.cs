using System.Threading.Tasks;
using BITKit.Multiplayer;

namespace BITKit.Multiplayer.ContractsOnlyFixture
{
    public interface IInventoryServiceRpc
    {
        [Rpc(SendTo.Host)]
        Task<int> ReserveAsync(string itemId, int quantity);
    }
}
