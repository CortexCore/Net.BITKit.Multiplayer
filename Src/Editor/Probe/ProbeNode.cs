using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.Probes;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.EditorProbe
{
    // One node per window. A provider holds only a stateless factory; the wire owns its
    // native UDP endpoint. Nothing here calls Unity APIs from socket/Task continuations.
    internal sealed class ProbeNode : IDisposable
    {
        internal const string HostIdentity = "editor-host";
        private static readonly TargetKey Key = new TargetKey("editor.probe");
        private readonly object gate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private ServiceProvider provider;
        private TouchSocketHostWire hostWire;
        private TouchSocketClientWire clientWire;
        private RpcRuntime runtime;
        private RpcProbeService service;
        private int disposed;
        private int remoteValue = -1;
        private int reads;
        private string error = "";
        internal bool Host { get; private set; }
        internal int Port { get; private set; }
        internal string Scope { get; private set; } = "";
        internal string Token { get; private set; } = "";
        internal string Peer { get; private set; } = "";
        internal bool Ready => Volatile.Read(ref disposed) == 0 && runtime?.IsReady == true &&
            (Host ? hostWire?.IsConnected == true : clientWire?.IsConnected == true);
        internal bool UdpEnabled
        {
            get => Host ? hostWire?.UnreliableEnabled == true : clientWire?.UnreliableEnabled == true;
            set { if (Host && hostWire != null) hostWire.UnreliableEnabled = value;
                else if (clientWire != null) clientWire.UnreliableEnabled = value; }
        }
        internal int Count => service?.Count ?? 0;
        internal int ClockSeconds => service?.DisplayClock ?? 12 * 3600;
        internal int ClockUpdates => service?.ClockUpdates ?? 0;
        private int formattedClock = -1;
        private string clockLabel = "12:00:00";
        internal string ClockLabel
        {
            get
            {
                int seconds = ClockSeconds % 86400;
                if (seconds != formattedClock) { formattedClock = seconds; clockLabel = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss"); }
                return clockLabel;
            }
        }
        internal void PublishClock()
        {
            if (!Ready || !Host || service == null) return;
            try { service.PublishClock(12 * 3600 + (int)(runtime.NetworkTime.time % 86400)); }
            catch (Exception ex) { SetError(ex); }
        }
        internal RpcRuntime Runtime => runtime;
        internal int RemoteValue => Volatile.Read(ref remoteValue);
        internal int Reads => Volatile.Read(ref reads);
        internal string Error { get { lock (gate) return error; } }
        internal DatagramStatistics Datagrams => Host ? hostWire?.GetDatagramStatistics() : clientWire?.GetDatagramStatistics();
        internal uint Sequence { get { if (service == null) return 0; service.GetPose(out _, out _, out var seq); return seq; } }
        internal void GetPose(out float x, out float y)
        { if (service == null) { x = y = 0; } else service.GetPose(out x, out y, out _); }
        internal string Sender => service?.LastSender ?? "";
        private ITransportFactory Factory()
        {
            var services = new ServiceCollection();
            services.AddUdpTransport();
            provider = services.BuildServiceProvider();
            return provider.GetRequiredService<ITransportFactory>();
        }
        private void SetError(Exception ex) { lock (gate) error = ex.GetType().Name; }

        internal async Task StartHostAsync(int port)
        {
            Host = true; Port = port; Scope = "editor-" + Guid.NewGuid().ToString("N");
            Token = Guid.NewGuid().ToString("N"); Peer = HostIdentity;
            try
            {
                hostWire = new TouchSocketHostWire(Factory());
                await hostWire.StartAsync(port, "bitkit-editor-probe-v1", IPAddress.Loopback,
                    ticket =>
                    {
                        if (ticket != Token || runtime == null || runtime.Members.Count > 1)
                            throw new InvalidOperationException("Probe ticket denied");
                        var admitted = new PeerId("editor-" + Guid.NewGuid().ToString("N"));
                        return Task.FromResult((admitted, admitted.Value + "|" + Scope));
                    },
                    peer => { runtime.HoldBroadcasts(peer); runtime.RegisterMember(new RoomMember(peer, peer.Value, ready: false)); },
                    peer => { if (runtime != null) runtime.RemoveMember(peer); });
                stop.Token.ThrowIfCancellationRequested();
                hostWire.PeerPrepared += peer =>
                {
                    var member = runtime.Members;
                    foreach (var item in member)
                        if (item.Peer.Equals(peer) && !item.Ready) { runtime.RegisterMember(new RoomMember(peer, peer.Value)); break; }
                };
                hostWire.PeerDirectoryReady += peer => runtime.ReleaseBroadcasts(peer);
                runtime = new RpcRuntime(NetworkRole.Host, Scope, new PeerId(HostIdentity), new PeerId(HostIdentity), hostWire);
                runtime.UnhandledDispatch += SetError;
                service = new RpcProbeService();
                runtime.Bind(Key, service, request => request.Sender.Ready);
                stop.Token.ThrowIfCancellationRequested();
            }
            catch (Exception ex) { SetError(ex); Dispose(); }
        }

        internal async Task StartClientAsync(string address, int port, string token, string expectedScope)
        {
            Port = port; Token = token;
            try
            {
                if (!IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip))
                    throw new ArgumentException("Editor probe is loopback only");
                clientWire = new TouchSocketClientWire(new PeerId(HostIdentity), Factory());
                await clientWire.ConnectAsync(address, port, "bitkit-editor-probe-v1");
                stop.Token.ThrowIfCancellationRequested();
                var reply = await clientWire.RequestAdmissionAsync(token, stop.Token);
                var fields = reply.Split('|');
                if (fields.Length != 2 || fields[0].Length == 0 || fields[1].Length == 0 ||
                    expectedScope.Length != 0 && fields[1] != expectedScope)
                    throw new InvalidOperationException("Probe admission/scope rejected");
                Peer = fields[0]; Scope = fields[1];
                runtime = new RpcRuntime(NetworkRole.Client, Scope, new PeerId(Peer), new PeerId(HostIdentity), clientWire);
                runtime.UnhandledDispatch += SetError;
                service = new RpcProbeService();
                runtime.Bind(Key, service);
                runtime.ConfirmReady();
                await clientWire.NotifyPreparedAsync();
                for (var i = 0; i < 320 && !Ready; i++)
                { stop.Token.ThrowIfCancellationRequested(); await Task.Delay(25, stop.Token); }
                if (!Ready) throw new TimeoutException("Probe directory did not become ready");
                for (var i = 0; i < 320 && !clientWire.IsUnreliableReady(new PeerId(HostIdentity)); i++)
                { stop.Token.ThrowIfCancellationRequested(); await Task.Delay(25, stop.Token); }
                if (!clientWire.IsUnreliableReady(new PeerId(HostIdentity)))
                    throw new TimeoutException("Probe UDP binding did not become ready");
                await clientWire.NotifyDirectoryReadyAsync();
                stop.Token.ThrowIfCancellationRequested();
            }
            catch (Exception ex) { SetError(ex); Dispose(); }
        }
        internal void Add() { if (Ready) try { service.Add(1); } catch (Exception ex) { SetError(ex); } }
        internal async Task ReadAsync()
        {
            if (!Ready) return;
            try { Volatile.Write(ref remoteValue, await service.Read()); Interlocked.Increment(ref reads); }
            catch (Exception ex) { SetError(ex); }
        }
        internal void SendPose(float x, float y, uint sequence)
        { if (Ready && Host && UdpEnabled) try { service.Pose(x, y, sequence); } catch (Exception ex) { SetError(ex); } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel();
            try { runtime?.Dispose(); } catch (Exception ex) { SetError(ex); }
            if (runtime == null) try { hostWire?.Dispose(); clientWire?.Dispose(); }
                catch (Exception ex) { SetError(ex); }
            provider?.Dispose(); stop.Dispose();
        }
    }
}
