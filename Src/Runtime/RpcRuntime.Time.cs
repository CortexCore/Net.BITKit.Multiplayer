using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
public sealed partial class RpcRuntime
{
    public NetworkTime NetworkTime { get; private set; } = null!;
    private Timer? _clockTimer;
    private int _clockSending;
    private readonly Dictionary<PeerId, long> _clockReplies = new();
    private readonly HashSet<PeerId> _clockReplying = new();
    private void StartNetworkTime()
    {
        NetworkTime = new NetworkTime(!IsClient);
        if (IsClient) _clockTimer = new Timer(_ => TickNetworkTime(), null, 250, 2000);
    }
    private void TickNetworkTime()
    {
        if (!IsReady || Interlocked.CompareExchange(ref _clockSending, 1, 0) != 0) return;
        _ = SendTimeSample();
    }
    private async Task SendTimeSample()
    {
        try
        {
            long sequence = NetworkTime.BeginSample();
            if (sequence != 0) await Send(HostPeerId, new Packet { Kind = "timeRequest", Version = sequence }).ConfigureAwait(false);
        }
        catch (Exception ex) { if (IsConnected) ReportUnhandled(ex); }
        finally { Interlocked.Exchange(ref _clockSending, 0); }
    }
    private void ReceiveTime(PeerId peer, Packet packet)
    {
        if (IsClient && packet.Kind == "timeReply")
        {
            if (!IsReady || packet.Value == null || packet.Value.Length != 8) return;
            NetworkTime.AcceptSample(packet.Version, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(packet.Value)));
            return;
        }
        if (!IsHost || packet.Kind != "timeRequest" || packet.Version <= 0) return;
        lock (_gate)
        {
            if (_disposed != 0 || !_members.TryGetValue(peer, out var member) || !member.Ready || _clockReplying.Contains(peer) || _clockReplying.Count >= MaxMembers) return;
            long now = Stopwatch.GetTimestamp();
            if (_clockReplies.TryGetValue(peer, out var last) && now - last < Stopwatch.Frequency / 4) return;
            _clockReplies[peer] = now; _clockReplying.Add(peer);
        }
        _ = ReplyTime(peer, packet.Version);
    }
    private async Task ReplyTime(PeerId peer, long sequence)
    {
        try
        {
            var value = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(value, BitConverter.DoubleToInt64Bits(NetworkTime.Time));
            await Send(peer, new Packet { Kind = "timeReply", Version = sequence, Value = value }).ConfigureAwait(false);
        }
        catch (Exception ex) { if (IsConnected && !IsRetiredPeer(peer)) ReportUnhandled(ex); }
        finally { lock (_gate) _clockReplying.Remove(peer); }
    }
    private void StopNetworkTime()
    { _clockTimer?.Dispose(); _clockTimer = null; NetworkTime.Stop(); }
}
}
