using Cysharp.Threading.Tasks;
using System.Collections.Concurrent;
using BITKit.Multiplayer.NetRpc;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Samples.NetRpcGodot;

public sealed class NetworkImpairment
{
    public volatile int LatencyMs;
    public volatile int LossPercent;
    public volatile bool Reorder;
    public volatile bool DropNextEventDelta;
    public long Received, DroppedDatagrams, DelayedDatagrams, ReorderedDatagrams, DroppedDeltas;
}

/// <summary>Test-harness infrastructure only: bounded owned copies for delayed borrowed ingress.</summary>
public sealed class ImpairedTransport : NetTransport, ITransportLifetime, IDisposable
{
    private readonly NetTransport _inner;
    private readonly ITransportLifetime _lifetimeSource;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Random _random = new(91827);
    private readonly object _gate = new();
    private int _inflight;
    private readonly ConcurrentQueue<Exception> _errors;
    public NetworkImpairment Settings { get; }
    private Action<ReadOnlyMemory<byte>>? _received;
    private Action? _closedHandlers;
    private bool _closed, _subscribed;
    public event Action<ReadOnlyMemory<byte>>? OnReceived
    {
        add
        {
            lock (_gate)
            {
                if (_closed) return;
                _received += value;
                // Install downstream first: the inner may synchronously replay owned early frames.
                if (!_subscribed) { _subscribed = true; _inner.OnReceived += Receive; }
            }
        }
        remove { lock (_gate) _received -= value; }
    }
    public event Action? Closed
    {
        add { bool notify; lock (_gate) { notify = _closed; if (!notify) _closedHandlers += value; } if (notify) value?.Invoke(); }
        remove { lock (_gate) _closedHandlers -= value; }
    }
    public ImpairedTransport(NetTransport inner, ITransportLifetime lifetimeSource, NetworkImpairment settings, ConcurrentQueue<Exception> errors)
    { _inner = inner; _lifetimeSource = lifetimeSource; Settings = settings; _errors = errors; lifetimeSource.Closed += Lost; }
    private void Lost()
    {
        Action? handlers;
        lock (_gate) { if (_closed) return; _closed = true; handlers = _closedHandlers; _closedHandlers = null; _received = null; }
        _lifetime.Cancel();
        Exception? failure = null;
        if (handlers != null) foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
    }
    private void Deliver(ReadOnlyMemory<byte> data)
    {
        Action<ReadOnlyMemory<byte>>? receiver;
        lock (_gate) { if (_closed) return; receiver = _received; }
        receiver?.Invoke(data);
    }
    public async UniTask Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Settings.LatencyMs > 0) await Task.Delay(Math.Clamp(Settings.LatencyMs, 0, 500), cancellationToken).ConfigureAwait(false);
        await _inner.Send(data, cancellationToken);
    }
    public UniTask SendFast(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => _inner.SendFast(data, cancellationToken);
    private void Receive(ReadOnlyMemory<byte> data)
    {
        Interlocked.Increment(ref Settings.Received);
        var model = NetRpcCodec.Decode(data);
        if (model.Kind == NetRpcMessageKind.SyncOperation && model.MethodId == RpcContextService.PropertyId(typeof(IArena), nameof(IArena.Events)) && Settings.DropNextEventDelta)
        { Settings.DropNextEventDelta = false; Interlocked.Increment(ref Settings.DroppedDeltas); return; }
        bool datagram = model.Kind == NetRpcMessageKind.Component || model.Kind == NetRpcMessageKind.FastCall;
        int delay = 0;
        lock (_gate)
        {
            if (datagram && _random.Next(100) < Math.Clamp(Settings.LossPercent, 0, 100)) { Interlocked.Increment(ref Settings.DroppedDatagrams); return; }
            if (datagram)
            {
                delay = Math.Clamp(Settings.LatencyMs, 0, 500);
                if (Settings.Reorder && _random.Next(2) == 0) { delay += 140; Interlocked.Increment(ref Settings.ReorderedDatagrams); }
            }
        }
        if (delay == 0) { Deliver(data); return; }
        if (Interlocked.Increment(ref _inflight) > 128) { Interlocked.Decrement(ref _inflight); Interlocked.Increment(ref Settings.DroppedDatagrams); return; }
        Interlocked.Increment(ref Settings.DelayedDatagrams);
        DeliverLater(data.ToArray(), delay).Forget();
    }
    private async UniTaskVoid DeliverLater(byte[] owned, int delay)
    {
        try { await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false); if (!_lifetime.IsCancellationRequested) Deliver(owned); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _errors.Enqueue(error); }
        finally { Interlocked.Decrement(ref _inflight); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_subscribed) { _inner.OnReceived -= Receive; _subscribed = false; } }
        _lifetimeSource.Closed -= Lost;
        Lost();
    }
}
