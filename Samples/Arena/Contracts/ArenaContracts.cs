using System.Runtime.Serialization;
using System.Diagnostics;
using MemoryPack;

namespace BITKit.Multiplayer.Samples.Arena;

public static class ArenaProtocol
{
    public const string LobbyToken = "bitkit-arena-lobby-v2";
    public const string GameToken = "bitkit-arena-game-v2";
    public const int DefaultLobbyPort = 17890;
    public const int DefaultGamePort = 17891;
    public const int DefaultRelayPort = 17892;
    public const string RelayToken = "bitkit-relay-v1";
    public const float HalfExtent = 12f;
}

[DataContract]
public sealed class AuthSession
{
    [DataMember] public bool Success { get; set; }
    [DataMember] public string Error { get; set; } = "";
    [DataMember] public string PlayerId { get; set; } = "";
    [DataMember] public string DisplayName { get; set; } = "";
    [DataMember] public string SessionToken { get; set; } = "";
}

public enum ArenaConnectionMode { Direct, Relay }

[DataContract]
public sealed class RoomInfo
{
    [DataMember] public string RoomId { get; set; } = "";
    [DataMember] public string Name { get; set; } = "";
    [DataMember] public string Scope { get; set; } = "";
    [DataMember] public string HostPeerId { get; set; } = "";
    [DataMember] public string Address { get; set; } = "127.0.0.1";
    [DataMember] public int Port { get; set; }
    [DataMember] public long ExpiresUnixMilliseconds { get; set; }
    [DataMember] public ArenaConnectionMode ConnectionMode { get; set; } = ArenaConnectionMode.Direct;
    [DataMember] public bool UseTls { get; set; }
}

[DataContract]
public sealed class RoomRegistration
{
    [DataMember] public bool Success { get; set; }
    [DataMember] public string Error { get; set; } = "";
    [DataMember] public RoomInfo Room { get; set; } = new();
    [DataMember] public string HostToken { get; set; } = "";
}

[DataContract]
public sealed class JoinTicket
{
    [DataMember] public bool Success { get; set; }
    [DataMember] public string Error { get; set; } = "";
    [DataMember] public string Ticket { get; set; } = "";
    [DataMember] public RoomInfo Room { get; set; } = new();
}

[DataContract]
public sealed class AdmissionIdentity
{
    [DataMember] public bool Success { get; set; }
    [DataMember] public string Error { get; set; } = "";
    [DataMember] public string PlayerId { get; set; } = "";
    [DataMember] public string DisplayName { get; set; } = "";
    [DataMember] public string PeerId { get; set; } = "";
}

// No account implementation is required by the client: LobbyClient implements this remote interface.
public interface IArenaLobbyApi : IAsyncDisposable
{
    Task<AuthSession> GuestAsync(string displayName);
    Task<AuthSession> RegisterAsync(string username, string password);
    Task<AuthSession> LoginAsync(string username, string password);
    Task<RoomRegistration> CreateRoomAsync(string sessionToken, string name, string address, int port);
    Task<RoomInfo[]> ListRoomsAsync(string sessionToken);
    Task<JoinTicket> JoinRoomAsync(string sessionToken, string roomId);
    Task<AdmissionIdentity> RedeemTicketAsync(string roomId, string hostToken, string ticket);
    Task<bool> HeartbeatRoomAsync(string roomId, string hostToken);
    Task<bool> CloseRoomAsync(string roomId, string hostToken);
}

public interface IArenaRelayLobbyApi : IArenaLobbyApi
{
    Task<RoomRegistration> ReserveRelayRoomAsync(string sessionToken, string name, string relayAddress, int relayPort, bool useTls);
    Task<RoomInfo?> ValidateRoomHostAsync(string roomId, string hostToken);
    Task<bool> PublishRelayRoomAsync(string roomId, string hostToken);
}

[DataContract]
[MemoryPackable]
public sealed partial class PlayerPose
{
    [DataMember, MemoryPackOrder(0)] public int MotionId { get; set; }
    [DataMember, MemoryPackOrder(1)] public string PlayerId { get; set; } = "";
    [DataMember, MemoryPackOrder(2)] public string PeerId { get; set; } = "";
    [DataMember, MemoryPackOrder(3)] public string Name { get; set; } = "";
    [DataMember, MemoryPackOrder(4)] public float X { get; set; }
    [DataMember, MemoryPackOrder(5)] public float Z { get; set; }
    [DataMember, MemoryPackOrder(6)] public float AimX { get; set; } = 1;
    [DataMember, MemoryPackOrder(7)] public float AimZ { get; set; }
    [DataMember, MemoryPackOrder(8)] public int Health { get; set; } = 100;
    [DataMember, MemoryPackOrder(9)] public int Deaths { get; set; }
}

[DataContract]
[MemoryPackable]
public sealed partial class BulletPose
{
    [DataMember, MemoryPackOrder(0)] public long Id { get; set; }
    [DataMember, MemoryPackOrder(1)] public string OwnerId { get; set; } = "";
    [DataMember, MemoryPackOrder(2)] public float X { get; set; }
    [DataMember, MemoryPackOrder(3)] public float Z { get; set; }
    [DataMember, MemoryPackOrder(4)] public float Vx { get; set; }
    [DataMember, MemoryPackOrder(5)] public float Vz { get; set; }
    [DataMember, MemoryPackOrder(6)] public long BornTick { get; set; }
}

[DataContract]
[MemoryPackable]
public sealed partial class RoomSnapshot
{
    [DataMember, MemoryPackOrder(0)] public string Scope { get; set; } = "";
    [DataMember, MemoryPackOrder(1)] public long Tick { get; set; }
    [DataMember, MemoryPackOrder(2)] public PlayerPose[] Players { get; set; } = Array.Empty<PlayerPose>();
    [DataMember, MemoryPackOrder(3)] public BulletPose[] Bullets { get; set; } = Array.Empty<BulletPose>();
    [DataMember, MemoryPackOrder(4)] public int TotalShots { get; set; }
    [DataMember, MemoryPackOrder(5)] public int TotalHits { get; set; }
}

// Read-only snapshot copies keep Raylib and socket callbacks on different threads safely.
public interface IArenaSession : IAsyncDisposable
{
    bool IsHost { get; }
    bool IsReady { get; }
    string LocalPlayerId { get; }
    string LocalPeerId { get; }
    string RoomId { get; }
    string RoomName { get; }
    string LastError { get; }
    ArenaConnectionMode ConnectionMode { get; }
    string ConnectedEndpoint { get; }
    string TransportName { get; }
    RoomSnapshot GetView();
    void SubmitMove(float x, float z, uint sequence);
    void Fire(float directionX, float directionZ, uint sequence);
    ArenaDatagramReport GetDatagramReport();
    void SetUdpEnabled(bool enabled);
    Task RebindUdpAsync(CancellationToken cancellationToken = default);
    void SetAllocationPhases(ArenaAllocationReport? report);
}

public sealed class ArenaHostOptions
{
    public string RoomName { get; set; } = "Arena V2";
    public string AdvertisedAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = ArenaProtocol.DefaultGamePort;
    public bool LocalPlayer { get; set; } = true;
    // Library callers retain Direct compatibility; App auto-selects Relay for player-host.
    public ArenaConnectionMode ConnectionMode { get; set; } = ArenaConnectionMode.Direct;
    public string RelayAddress { get; set; } = "127.0.0.1";
    public int RelayPort { get; set; } = ArenaProtocol.DefaultRelayPort;
    public bool RelayUseTls { get; set; }
}

public sealed class ArenaClientOptions
{
    public string RoomId { get; set; } = "";
}

public sealed class ArenaReport
{
    public string Role { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public bool WasReady { get; set; }
    public bool IsReady { get; set; }
    public string Error { get; set; } = "";
    public RoomSnapshot Final { get; set; } = new();
    public int MaximumPlayers { get; set; }
    public int MinimumOwnHealth { get; set; } = 100;
    public long ViewsObserved { get; set; }
    // Local sample diagnostics only. Final remains the unmodified authoritative state.
    public ArenaPresentationReport Presentation { get; set; } = new();
    public ArenaConnectionMode ConnectionMode { get; set; }
    public string ConnectedEndpoint { get; set; } = "";
    public string TransportName { get; set; } = "";
    public ArenaDatagramReport Datagrams { get; set; } = new();
    public ArenaGcReport Gc { get; set; } = new();
    public ArenaAllocationReport? Allocations { get; set; }
}

// Opt-in, fixed-size leaf counters. Each interval is synchronous and must end on its
// starting thread. Bytes are allocations on THAT thread only; elapsed Stopwatch time
// includes blocking/waits and is not CPU time. The process-wide Gc report also includes
// socket callbacks, timer continuations and other threads not covered by these leaves.
public enum ArenaAllocationPhase
{
    ViewClone, Interpolation, ReportMetadata, ReportWrite, InputSubmit,
    Datagrams, GcSample, AppOther, AuthorityStep, MotionSend, ReliableLifecycle
}

public sealed class ArenaAllocationPhaseTotals
{
    private long count, bytes, maxBytes, ticks, maxTicks;
    public long Count => Interlocked.Read(ref count);
    public long Bytes => Interlocked.Read(ref bytes);
    public long MaxBytes => Interlocked.Read(ref maxBytes);
    public long ElapsedTicks => Interlocked.Read(ref ticks);
    public long MaxElapsedTicks => Interlocked.Read(ref maxTicks);
    internal void Add(long allocated, long elapsed)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(ref bytes, allocated);
        Interlocked.Add(ref ticks, elapsed);
        Max(ref maxBytes, allocated);
        Max(ref maxTicks, elapsed);
    }
    private static void Max(ref long target, long value)
    {
        long prior;
        while ((prior = Interlocked.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, prior) != prior) { }
    }
}

public sealed class ArenaAllocationReport
{
    private readonly ArenaAllocationPhaseTotals[] totals = Enumerable.Range(0, 11).Select(_ => new ArenaAllocationPhaseTotals()).ToArray();
    private int sampling;
    public int WarmupSeconds => 3;
    public long StopwatchFrequency => Stopwatch.Frequency;
    public string Semantics => "Disjoint synchronous leaves: ViewClone/Interpolation/ReportMetadata/ReportWrite/InputSubmit/Datagrams/GcSample/AppOther run on each App Update caller thread; AuthorityStep/MotionSend/ReliableLifecycle run on each host tick continuation thread. No interval crosses an await. GC bytes are local to the interval's thread; elapsed Stopwatch ticks include waiting, not CPU. ReportWrite excludes its own current serialization until the next report. Asynchronous/receive threads are not covered and remain in process Gc.";
    public bool Sampling => Volatile.Read(ref sampling) != 0;
    public ArenaAllocationPhaseTotals ViewClone => totals[0];
    public ArenaAllocationPhaseTotals Interpolation => totals[1];
    public ArenaAllocationPhaseTotals ReportMetadata => totals[2];
    public ArenaAllocationPhaseTotals ReportWrite => totals[3];
    public ArenaAllocationPhaseTotals InputSubmit => totals[4];
    public ArenaAllocationPhaseTotals Datagrams => totals[5];
    public ArenaAllocationPhaseTotals GcSample => totals[6];
    public ArenaAllocationPhaseTotals AppOther => totals[7];
    public ArenaAllocationPhaseTotals AuthorityStep => totals[8];
    public ArenaAllocationPhaseTotals MotionSend => totals[9];
    public ArenaAllocationPhaseTotals ReliableLifecycle => totals[10];
    public void EnableAfterWarmup() => Volatile.Write(ref sampling, 1);
    public ArenaAllocationInterval Start(ArenaAllocationPhase phase) => Sampling
        ? new ArenaAllocationInterval(totals[(int)phase]) : default;
}

public readonly struct ArenaAllocationInterval
{
    private readonly ArenaAllocationPhaseTotals? totals;
    private readonly long bytes, ticks;
    private readonly int thread;
    internal ArenaAllocationInterval(ArenaAllocationPhaseTotals totals)
    {
        this.totals = totals;
        thread = Environment.CurrentManagedThreadId;
        bytes = GC.GetAllocatedBytesForCurrentThread();
        ticks = Stopwatch.GetTimestamp();
    }
    public void End()
    {
        if (totals == null) return;
        if (Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("Allocation phase crossed a thread boundary");
        long elapsed = Stopwatch.GetTimestamp() - ticks;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        totals.Add(Math.Max(0, allocated), Math.Max(0, elapsed));
    }
}

// Whole-process measurements include presentation copies, reliable traffic, transport,
// diagnostics and the GC sampler itself. They are not motion-RPC-only measurements.
// A bounded initial / highest-managed-heap / final series avoids an unbounded trace.
public sealed class ArenaGcReport
{
    public int WarmupSeconds { get; set; } = 3;
    public int SampleCount { get; set; }
    public long WindowMilliseconds { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public double AllocatedBytesPerSecond { get; set; }
    public double PeakSampleAllocatedBytesPerSecond { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public ArenaGcSample? Initial { get; set; }
    public ArenaGcSample? PeakManagedHeap { get; set; }
    public ArenaGcSample? Final { get; set; }
}

public sealed class ArenaGcSample
{
    public long ElapsedMilliseconds { get; set; }
    public long AllocatedBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long ManagedHeapBytes { get; set; }
    // GetGCMemoryInfo describes the most recent collection; these may be zero
    // before the first GC and are not a synchronous live-heap census.
    public long HeapCommittedBytes { get; set; }
    public long FragmentedBytes { get; set; }
    public long WorkingSetBytes { get; set; }
}

[DataContract]
public struct PlayerMotionUpdate
{
    [DataMember] public int Id;
    [DataMember] public float X, Z, AimX, AimZ;
}

[DataContract]
public struct BulletMotionUpdate
{
    [DataMember] public long Id;
    [DataMember] public float X, Z;
}

public sealed class ArenaDatagramReport
{
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public int PayloadLimit { get; set; }
    public long SentDatagrams { get; set; }
    public long ReceivedDatagrams { get; set; }
    public long RejectedDatagrams { get; set; }
    public long DroppedDatagrams { get; set; }
    public int LargestDatagramBytes { get; set; }
    public long PositionTick { get; set; }
    public long PlayerBatchesReceived { get; set; }
    public long BulletBatchesReceived { get; set; }
    public long ReliableProbesCompleted { get; set; }
    public long HealthUpdatesReceived { get; set; }
    public int PoseBufferAllocations { get; set; }
    public long PositionSendAllocatedBytes { get; set; }
}

public sealed class ArenaPresentationReport
{
    public bool Enabled { get; set; }
    public int BufferMilliseconds { get; set; } = 100;
    public int SimulatedDelayMilliseconds { get; set; }
    public int SimulatedJitterMilliseconds { get; set; }
    public double RenderTick { get; set; }
    public int BufferedSnapshots { get; set; }
    public int PendingSnapshots { get; set; }
    public long InterpolatedFrames { get; set; }
    public long ResetCount { get; set; }
    public RoomSnapshot Frame { get; set; } = new();
}
