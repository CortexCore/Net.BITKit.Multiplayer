using System;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.Probes
{
    // Separate runtime assembly: Unity's IL post-processor must weave these ordinary methods.
    public sealed class RpcProbeService
    {
        private int _count;
        private readonly object _poseGate = new object();
        private float _x, _y;
        private uint _sequence;
        private string _sender = "";
        private int _displayClock = 12 * 3600, _clockUpdates;

        [SyncVar, Hook(nameof(OnClockChanged))]
        public int ClockSeconds { get; private set; } = 12 * 3600;
        public int DisplayClock => Volatile.Read(ref _displayClock);
        public int ClockUpdates => Volatile.Read(ref _clockUpdates);
        public void PublishClock(int seconds) => ClockSeconds = seconds;
        private void OnClockChanged(int before, int after)
        { Volatile.Write(ref _displayClock, after); Interlocked.Increment(ref _clockUpdates); }

        public int Count => Volatile.Read(ref _count);
        public string LastSender { get { lock (_poseGate) return _sender; } }
        public void GetPose(out float x, out float y, out uint sequence)
        { lock (_poseGate) { x = _x; y = _y; sequence = _sequence; } }

        [Rpc(SendTo.Host)]
        public void Add(int amount)
        {
            if (!RpcCallContext.TryGetValue(out var context))
                throw new InvalidOperationException("Authenticated RPC sender required");
            lock (_poseGate) _sender = context.Sender.Value;
            Interlocked.Add(ref _count, amount);
        }

        [Rpc(SendTo.Host)]
        public Task<int> Read() => Task.FromResult(Count);

        [Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)]
        public void Pose(float x, float y, uint sequence)
        {
            lock (_poseGate)
            {
                if (sequence <= _sequence) return;
                _x = x; _y = y; _sequence = sequence;
            }
        }
    }
}
