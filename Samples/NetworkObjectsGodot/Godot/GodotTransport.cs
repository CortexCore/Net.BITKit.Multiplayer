using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>Own borrowed ingress memory until the engine thread applies it, including component Changed.</summary>
public sealed class GodotTransport : NetTransport, ITransportLifetime, IDisposable
{
    private readonly NetTransport _inner;
    private readonly ITransportLifetime _lifetime;
    private readonly GodotThread _thread;
    private readonly Action<Exception> _report;
    private readonly object _gate = new();
    private Action<ReadOnlyMemory<byte>>? _received;
    private Action? _closed;
    private bool _subscribed, _stopped;
    private int _inflightBytes;
    public GodotTransport(NetTransport inner, ITransportLifetime lifetime, GodotThread thread, Action<Exception> report)
    { _inner = inner; _lifetime = lifetime; _thread = thread; _report = report; _lifetime.Closed += Lost; }

    public event Action<ReadOnlyMemory<byte>>? OnReceived
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                _received += value;
                if (!_subscribed) { _subscribed = true; _inner.OnReceived += Receive; }
            }
        }
        remove { lock (_gate) _received -= value; }
    }
    public event Action? Closed { add { lock (_gate) _closed += value; } remove { lock (_gate) _closed -= value; } }
    public UniTask Send(ReadOnlyMemory<byte> data, CancellationToken token = default) => _inner.Send(data, token);
    public UniTask SendFast(ReadOnlyMemory<byte> data, CancellationToken token = default) => _inner.SendFast(data, token);

    private void Receive(ReadOnlyMemory<byte> borrowed)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_inflightBytes + borrowed.Length > 8 * 1024 * 1024)
            { _report(new InvalidOperationException("Godot inbound queue exceeded 8 MiB.")); return; }
            _inflightBytes += borrowed.Length;
        }
        Deliver(borrowed.ToArray()).Forget(_report);
    }
    private async UniTask Deliver(byte[] owned)
    {
        try
        {
            await _thread.InvokeAsync(() =>
            {
                Action<ReadOnlyMemory<byte>>? receiver;
                lock (_gate) receiver = _stopped ? null : _received;
                receiver?.Invoke(owned); return true;
            });
        }
        finally { lock (_gate) _inflightBytes -= owned.Length; }
    }
    private void Lost() => _thread.InvokeAsync(() =>
    {
        Action? handlers;
        lock (_gate) { handlers = _stopped ? null : _closed; _closed = null; }
        handlers?.Invoke(); return true;
    }).Forget(_report);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return; _stopped = true; _received = null; _closed = null;
            if (_subscribed) _inner.OnReceived -= Receive;
        }
        _lifetime.Closed -= Lost;
    }
}
