using System.Collections.Concurrent;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>Godot boundary only: network callbacks never touch a Node off the engine thread.</summary>
public sealed class GodotThread : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<(Action Run, Action Stop)> _pending = new();
    private readonly object _gate = new();
    private bool _stopped;
    public int Calls { get; private set; }
    public int MarshalledCalls { get; private set; }
    public bool IsMainThread => Environment.CurrentManagedThreadId == _thread;

    public void AssertMainThread()
    {
        if (!IsMainThread) throw new InvalidOperationException("A Godot Node was accessed off the engine thread.");
        Calls++;
    }

    public UniTask<T> InvokeAsync<T>(Func<T> action, CancellationToken token = default)
    {
        if (IsMainThread)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate) ObjectDisposedException.ThrowIf(_stopped, this);
            AssertMainThread();
            return UniTask.FromResult(action());
        }
        var completion = new UniTaskCompletionSource<T>();
        lock (_gate)
        {
            if (_stopped) return UniTask.FromException<T>(new ObjectDisposedException(nameof(GodotThread)));
            if (_pending.Count >= 1024) return UniTask.FromException<T>(new InvalidOperationException("Godot dispatcher queue exceeded 1024 operations."));
            _pending.Enqueue((() =>
            {
                try { token.ThrowIfCancellationRequested(); AssertMainThread(); MarshalledCalls++; completion.TrySetResult(action()); }
                catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                catch (Exception error) { completion.TrySetException(error); }
            }, () => completion.TrySetException(new ObjectDisposedException(nameof(GodotThread)))));
        }
        return completion.Task;
    }

    public void Invoke(Action action)
    {
        if (IsMainThread) { AssertMainThread(); action(); return; }
        // Adapter's synchronous interface is the explicit engine boundary. The engine keeps pumping.
        InvokeAsync(() => { action(); return true; }).AsTask().GetAwaiter().GetResult();
    }

    public void Pump()
    {
        AssertMainThread();
        for (int i = 0; i < 256 && _pending.TryDequeue(out var work); i++) work.Run();
    }

    public void Dispose()
    {
        lock (_gate) { if (_stopped) return; _stopped = true; }
        while (_pending.TryDequeue(out var work)) work.Stop();
    }
}
