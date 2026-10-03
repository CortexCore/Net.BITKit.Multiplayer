# TouchSocket UDP receive allocation: dependency evidence (read-only)

This note **locates** a GC allocation source; it does not implement a replacement receiver, patch a binary, or change third-party source. The combined workload measurements and limitations belong in [performance-gc-attribution.md](performance-gc-attribution.md). In the newer Release captures, `UdpSessionBase.RunReceive` accounts for 76.49% of Alice's sampled allocation estimate in Direct (`20260927-120937-f9f48d`) and 73.60% in Relay (`20260927-121125-5fea65`). These are **EventPipe allocation-sampling estimates**, not exact allocated bytes or packet counts.

## Binary and target actually examined

- `Samples/Arena/App/Arena.App.csproj` targets `net10.0`. `Artifacts/bin/Arena.App/Release/net10.0/Arena.App.deps.json` selects **`TouchSocket/4.3.9`, runtime `lib/net10.0/TouchSocket.dll`**. Do not label this Arena executable's loaded asset `net8.0` merely because the transport project also builds a `net8.0` target.
- `Artifacts/bin/Arena.App/Release/net10.0/TouchSocket.dll` matches `%USERPROFILE%/.nuget/packages/touchsocket/4.3.9/lib/net10.0/TouchSocket.dll` byte-for-byte by SHA-256: `7E7E98EB795850A079052911A6DDFC84FD6652E5DCB7A221BEB2178B9D9A1751`. The binary has assembly/file version `4.3.9.0`. The separate cached `lib/net8.0/TouchSocket.dll` has SHA-256 `FE80F5E3B85DFFF10A86B920EBD89A371D1A423B9128854AC93F23CC552CFCA2` and the **same relevant `RunReceive` allocation**; so does the cached `netstandard2.1` assembly. The run directory does not include a process-loaded-module hash: the SHA match establishes the examined Release output, not a separately recorded in-process hash.
- The package nuspec links repository commit `a13ce02bf133eaa7a23ed269ad96290989c668f4`; the [upstream `src/TouchSocket/Components/Udp/UdpSessionBase.cs` on `master`](https://github.com/RRQM/TouchSocket/blob/master/src/TouchSocket/Components/Udp/UdpSessionBase.cs) provides useful source context, **not a pinned, verified source revision for this exact NuGet binary**. The decompiled cached/deployed DLL and IL below are the version-specific evidence.

## Exact receive-path evidence

Decompilation of the **deployed net10 DLL**, reduced to the relevant lines of `TouchSocket.Sockets.UdpSessionBase.RunReceive()`:

```csharp
using UdpSocketReceiver receiver = new UdpSocketReceiver();
while (true)
{
    Memory<byte> memory = new Memory<byte>(new byte[65536]);
    // The state check follows the allocation.
    if (m_serverState != ServerState.Running) break;
    UdpOperationResult result = await receiver.ReceiveAsync(
        m_monitor.Socket, m_monitor.IPHost.EndPoint, memory).ConfigureDefaultAwait();
    if (result.BytesTransferred > 0)
        await HandleReceivingData(memory.Slice(0, result.BytesTransferred),
            result.RemoteEndPoint).ConfigureDefaultAwait();
    // Other status/error branches omitted.
}
```

This is a **new 65,536-byte managed `byte[]` on each receive-loop iteration**, even when the application datagram is tiny. In the DLL's `<RunReceive>d__40.MoveNext` IL, `IL_0023: ldc.i4 65536`, `IL_0028: newarr System.Byte`, `IL_002d: newobj Memory<byte>(byte[])`, then the receiver call at `IL_0077`; `IL_010d` calls `HandleReceivingData` with the transferred-length slice. After awaited processing, `IL_0201: br IL_0022` returns to the allocation. `UdpSocketReceiver.ReceiveAsync` calls `Socket.ReceiveFromAsync(SocketAsyncEventArgs)` with that buffer. The 65,536-byte payload array itself is below the usual ~85 KB large-object threshold; do not infer a LOH issue from its size alone.

The receive chain is `UdpSessionBase.RunReceive` → `UdpSocketReceiver.ReceiveAsync` → `HandleReceivingData` → `PrivateHandleReceivedData` → `OnUdpReceived` → registered UDP plugin → `UdpLane.OnPacket`. `HandleReceivingData` awaits downstream handling before the next loop iteration. Our `UdpLane` pools **downstream outgoing body/wire rentals**, and Relay forwarding pools its copied borrowed payload; neither can undo TouchSocket's earlier `new byte[65536]`. Our receive callback must consume/copy borrowed `ReadOnlyMemory<byte>` before returning; it cannot retain that memory across the next receive.

## Reproduce the inspection

The inspection used `ilspycmd` 11.1 installed **locally**, not globally, under `C:/Users/Iris/AppData/Local/Temp/opencode/touchsocket-ilspy/`. Equivalent commands (replace paths for another machine):

```powershell
dotnet tool install ilspycmd --tool-path 'C:\Users\Iris\AppData\Local\Temp\opencode\touchsocket-ilspy'
& 'C:\Users\Iris\AppData\Local\Temp\opencode\touchsocket-ilspy\ilspycmd.exe' -m 'M:TouchSocket.Sockets.UdpSessionBase.RunReceive' 'Artifacts\bin\Arena.App\Release\net10.0\TouchSocket.dll'
& 'C:\Users\Iris\AppData\Local\Temp\opencode\touchsocket-ilspy\ilspycmd.exe' -m 'M:TouchSocket.Sockets.UdpSessionBase.BeginReceive(TouchSocket.Sockets.IPHost)' 'Artifacts\bin\Arena.App\Release\net10.0\TouchSocket.dll'
Get-FileHash -Algorithm SHA256 'Artifacts\bin\Arena.App\Release\net10.0\TouchSocket.dll'
```

For precise offsets, inspect the same assembly's IL for the compiler-generated `UdpSessionBase/<RunReceive>d__40.MoveNext`, rather than the public `RunReceive` async-state-machine factory method. The analogous NuGet input is `%USERPROFILE%/.nuget/packages/touchsocket/4.3.9/lib/net10.0/TouchSocket.dll`.

## Configuration and safe follow-up

`BeginReceive(IPHost)` reads `UdpOverlappedCount` (documented default **1**) and starts that many `RunReceive` workers; it does **not** read a configurable receive-datagram length. Increasing the worker count increases potential allocation concurrency. Changing the OS socket `ReceiveBufferSize`, `TransportOption.MaxBufferSize`, a UDP data adapter, or an `IUdpReceivingPlugin` does **not** replace the hard-coded array: adapters/plugins run after the allocation. `RunReceive` and `BeginReceive` are private; there is no existing TouchSocket 4.3.9 UDP buffer-size/reuse configuration or subclass override for this loop.

The lowest-risk *proposed* dependency-side A/B is to rent a **full 65,536-byte buffer once per receive worker**, reuse it only after `await HandleReceivingData` has finished, and return it in `finally` on worker shutdown. Keep the full capacity first: that isolates allocation reduction without changing oversized-datagram/truncation semantics or substituting a homemade socket. Validate callback borrowing and disposal under both Windows and Linux, including multiple overlapped workers; compare equivalent steady-state Release phases using `GC.GetTotalAllocatedBytes`, EventPipe `RunReceive`/`System.Byte[]` attribution, UDP counters, and real Direct/Relay bind, traffic, and rebind tests.

**Do not simply shrink to 1200 bytes.** `RunReceive` dispatches when `BytesTransferred > 0` without first rejecting `SocketError.MessageSize`. A too-small socket receive buffer can truncate a larger UDP datagram; a 1200-byte prefix may look like a complete accepted frame. A separate small-buffer experiment would require at least a **1201-byte sentinel**, explicit truncation/error rejection, and Windows/Linux tests with 1200-, 1201-, and near-maximum-size datagrams so oversized packets never enter authentication or gameplay. No such dependency change or A/B has been made here.
