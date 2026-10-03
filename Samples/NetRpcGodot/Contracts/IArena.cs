using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.Samples.NetRpcGodot
{
    public struct Position
    {
        public float X { get; set; }
        public float Y { get; set; }
        public Position(float x, float y) { X = x; Y = y; }
    }
    public interface IArena
    {
        UniTask<int> Join(string name, int requestedSlot);
        void Input(int player, float x, float y);
        UniTask<bool> Pickup(int player, int pickup);
        UniTask<bool> Respawn(int player);
        UniTask<int> Ping(int nonce);
        int Tick { get; }
        IList<int> ConnectedPlayers { get; }
        IList<int> AvailablePickups { get; }
        IList<string> Events { get; }
        IDictionary<int, int> Scores { get; }
        IDictionary<int, int> Inventory { get; }
    }
    public static class ArenaRules
    {
        public const float Width = 800, Height = 500, Speed = 160, AttackRange = 180, PickupRange = 65;
        public const ulong Scope = 0x474F444F544C4142UL;
        public static Position Spawn(int slot) => new Position(slot == 1 ? 200 : 340, 250);
        public static Position PickupPosition(int id) => id == 1 ? new Position(260, 250) : id == 2 ? new Position(480, 180) : new Position(560, 350);
    }
}
