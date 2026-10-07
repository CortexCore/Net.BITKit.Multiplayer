using System.Buffers.Binary;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;

namespace NetRpcTransportBaseline;

internal static class SelfTests
{
    public static void Run()
    {
        int passed = 0;
        Check("valid payload, independent warmup, out-of-order sequence", () =>
        {
            var receiver = new Receiver(2, 32, 2, 3);
            byte[] payload = Payload.Create(2, 32);
            receiver.Receive(payload);
            Payload.SetSequence(payload, 0, 1); receiver.Receive(payload);
            Payload.SetSequence(payload, 1, 2); receiver.Receive(payload);
            Payload.SetSequence(payload, 1, 0); receiver.Receive(payload);
            Payload.SetSequence(payload, 1, 1); receiver.Receive(payload);
            var snapshot = receiver.Snapshot();
            Require(receiver.CleanWarmup && snapshot.WarmupUnique == 2 && snapshot.Unique == 3 && snapshot.Callbacks == 3 && snapshot.PayloadBytes == 96);
        });
        Check("duplicate is counted, never counted as another successful receive", () =>
        {
            var receiver = new Receiver(0, 32, 1, 2);
            byte[] payload = Payload.Create(0, 32); Payload.SetSequence(payload, 1, 0);
            receiver.Receive(payload); receiver.Receive(payload);
            Require(receiver.Snapshot() is { Unique: 1, Callbacks: 2, Duplicates: 1 });
        });
        Check("bad length, wrong edge, corruption, and out-of-range sequence are rejected", () =>
        {
            var receiver = new Receiver(0, 32, 1, 2);
            byte[] payload = Payload.Create(0, 32); Payload.SetSequence(payload, 1, 0);
            receiver.Receive(payload.AsMemory(0, 31));
            payload[1] = 1; receiver.Receive(payload); payload[1] = 0;
            payload[^1] ^= 1; receiver.Receive(payload); payload[^1] ^= 1;
            Payload.SetSequence(payload, 1, 2); receiver.Receive(payload);
            Require(receiver.Snapshot() is { Unique: 0, Callbacks: 4, Invalid: 4, PayloadBytes: 127 });
        });
        Check("missing receives remain missing", () =>
        {
            var receiver = new Receiver(1, 32, 1, 3);
            byte[] payload = Payload.Create(1, 32); Payload.SetSequence(payload, 1, 2); receiver.Receive(payload);
            Require(receiver.MeasuredUnique == 1 && receiver.Snapshot().Unique != 3);
        });
        Check("send is awaited before payload reuse and completed count", () =>
        {
            var first = new PendingTransport();
            ITransport[] transports = [first, new PendingTransport(false), new PendingTransport(false), new PendingTransport(false)];
            byte[][] payloads = Enumerable.Range(0, 4).Select(edge => Payload.Create(edge, 32)).ToArray();
            var completed = new long[4];
            var pending = Program.RunLoop(transports, payloads, completed, 2, "tcp", 1, null, default).AsTask();
            Require(!pending.IsCompleted && completed.Sum() == 0 && BinaryPrimitives.ReadInt32LittleEndian(first.Borrowed.Span.Slice(4, 4)) == 0);
            first.Complete(); pending.GetAwaiter().GetResult();
            Require(completed.All(c => c == 2));
        });
        Check("failed sends propagate and do not increment completion counters", () =>
        {
            ITransport[] transports = [new PendingTransport(false, true), new PendingTransport(false), new PendingTransport(false), new PendingTransport(false)];
            byte[][] payloads = Enumerable.Range(0, 4).Select(edge => Payload.Create(edge, 32)).ToArray();
            var completed = new long[4];
            try
            {
                Program.RunLoop(transports, payloads, completed, 1, "udp", 1, null, default).GetAwaiter().GetResult();
                throw new Exception("Expected send failure.");
            }
            catch (IOException) { Require(completed.Sum() == 0); }
        });
        Check("worker control performs no sends", () =>
        {
            ITransport[] transports = Enumerable.Range(0, 4).Select(_ => (ITransport)new PendingTransport(false, true)).ToArray();
            byte[][] payloads = Enumerable.Range(0, 4).Select(edge => Payload.Create(edge, 32)).ToArray();
            var completed = new long[4];
            Program.RunLoop(transports, payloads, completed, 4, "control", 1, null, default).GetAwaiter().GetResult();
            Require(completed.Sum() == 0 && payloads.All(p => BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4)) == 3));
        });
        Check("configuration rejects unknown, duplicate, invalid and unpaired options", () =>
        {
            string[][] cases = [["--bad", "1"], ["--iterations", "0"], ["--iterations", "1", "--iterations", "2"], ["--rates", "0"], ["--sizes", "7"], ["--channels", "fake"], ["--channels", "tcp,tcp"], ["--output"]];
            foreach (var args in cases)
            {
                try { Options.Parse(args); throw new Exception("Expected configuration rejection."); }
                catch (ArgumentException) { }
            }
        });
        Console.WriteLine($"PASS {passed}/{passed} transport diagnostic guard tests");
        void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Guard assertion failed."); }

    private sealed class PendingTransport(bool pending = true, bool fail = false) : ITransport
    {
        private readonly UniTaskCompletionSource _completion = new();
        private bool _first = true;
        public ReadOnlyMemory<byte> Borrowed;
        public event Action<ReadOnlyMemory<byte>>? OnReceived { add { } remove { } }
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            if (fail) throw new IOException("Expected diagnostic fake send failure.");
            Borrowed = payload;
            if (pending && _first) { _first = false; return _completion.Task; }
            return UniTask.CompletedTask;
        }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken token = default) => Send(payload, token);
        public void Complete() => _completion.TrySetResult();
    }
}
