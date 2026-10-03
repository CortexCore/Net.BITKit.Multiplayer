using System;
using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer.Probes;
using UnityEditor;
using UnityEngine;

namespace BITKit.Multiplayer.EditorProbe
{
    internal static class ProbeDrawing
    {
        internal static void Draw(float x, float y, uint sequence)
        {
            var grid = GUILayoutUtility.GetRect(320, 220, GUILayout.ExpandWidth(true));
            GUI.Box(grid, GUIContent.none);
            for (int i = 1; i < 8; i++)
            {
                var px = grid.x + i * grid.width / 8;
                var py = grid.y + i * grid.height / 8;
                EditorGUI.DrawRect(new Rect(px, grid.y, 1, grid.height), new Color(.23f, .27f, .32f));
                EditorGUI.DrawRect(new Rect(grid.x, py, grid.width, 1), new Color(.23f, .27f, .32f));
            }
            Handles.color = new Color(.12f, .9f, .7f);
            Handles.DrawSolidDisc(new Vector3(grid.center.x + x * grid.width * .35f,
                grid.center.y - y * grid.height * .35f), Vector3.forward, 7);
            GUI.Label(new Rect(grid.x + 8, grid.y + 5, grid.width - 16, 20), "UDP pose seq " + sequence);
        }
        internal static void Stats(ProbeNode node)
        {
            if (node == null) return;
            var stats = node.Datagrams;
            EditorGUILayout.LabelField("Peer", node.Peer);
            EditorGUILayout.LabelField("Scope", node.Scope);
            EditorGUILayout.LabelField("Ready", node.Ready.ToString());
            EditorGUILayout.LabelField("Time (SyncVar)", node.ClockLabel, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Authenticated last Add sender", node.Sender);
            EditorGUILayout.LabelField("UDP", "sent " + (stats?.SentDatagrams ?? 0) + " / received " +
                (stats?.ReceivedDatagrams ?? 0));
            if (node.Error.Length != 0) EditorGUILayout.HelpBox(node.Error, MessageType.Error);
        }
    }

    public sealed class ProbeHostWindow : EditorWindow
    {
        private ProbeNode node;
        private int port = 17891;
        private double lastTick;
        private double lastClockTick;
        private uint sequence;
        [MenuItem("Tools/BITKit/Multiplayer/Host")]
        public static void Open() => GetWindow<ProbeHostWindow>("BITKit Host Probe").Show();
        internal static ProbeHostWindow Instance => GetWindow<ProbeHostWindow>("BITKit Host Probe");
        internal ProbeNode Node => node;
        internal void Begin(int requestedPort)
        {
            StopNode();
            port = requestedPort;
            node = new ProbeNode();
            lastClockTick = EditorApplication.timeSinceStartup;
            _ = node.StartHostAsync(port);
        }
        internal void StopNode() { node?.Dispose(); node = null; }
        private void OnEnable() => EditorApplication.update += Tick;
        private void OnDisable() { EditorApplication.update -= Tick; StopNode(); }
        private void Tick()
        {
            if (node?.Ready == true && EditorApplication.timeSinceStartup - lastClockTick >= 1)
            { lastClockTick = EditorApplication.timeSinceStartup; node.PublishClock(); }
            if (node?.Ready == true && node.UdpEnabled && EditorApplication.timeSinceStartup - lastTick >= .05)
            {
                lastTick = EditorApplication.timeSinceStartup;
                sequence++;
                node.SendPose((float)Math.Sin(lastTick * .8), (float)Math.Cos(lastTick * .8), sequence);
            }
            Repaint(); // Editor main thread only; callbacks never call Repaint.
        }
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Loopback-only authoritative Host (Edit Mode)", EditorStyles.boldLabel);
            port = EditorGUILayout.IntField("TCP + UDP port", port);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Start") && !EditorApplication.isPlaying && port is > 0 and <= 65535) Begin(port);
                if (GUILayout.Button("Stop")) StopNode();
            }
            if (node == null) return;
            EditorGUILayout.SelectableLabel("Client ticket: " + node.Token, GUILayout.Height(19));
            ProbeDrawing.Stats(node);
            EditorGUILayout.LabelField("Host counter (authority)", node.Count.ToString());
            bool enabled = EditorGUILayout.Toggle("UDP enabled", node.UdpEnabled);
            if (node.Ready) node.UdpEnabled = enabled;
            node.GetPose(out var x, out var y); ProbeDrawing.Draw(x, y, node.Sequence);
        }
    }

    public sealed class ProbeClientWindow : EditorWindow
    {
        private ProbeNode node;
        private string address = "127.0.0.1", ticket = "", scope = "";
        private int port = 17891;
        [MenuItem("Tools/BITKit/Multiplayer/Client")]
        public static void Open() => GetWindow<ProbeClientWindow>("BITKit Client Probe").Show();
        internal static ProbeClientWindow Instance => GetWindow<ProbeClientWindow>("BITKit Client Probe");
        internal ProbeNode Node => node;
        internal void UseHost(ProbeHostWindow host)
        {
            if (host?.Node == null) return;
            address = "127.0.0.1"; port = host.Node.Port;
            ticket = host.Node.Token; scope = host.Node.Scope;
        }
        internal void Begin()
        {
            StopNode();
            node = new ProbeNode();
            _ = node.StartClientAsync(address, port, ticket, scope);
        }
        internal void StopNode() { node?.Dispose(); node = null; }
        private void OnEnable() => EditorApplication.update += Tick;
        private void OnDisable() { EditorApplication.update -= Tick; StopNode(); }
        private void Tick() => Repaint();
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Independent Client / real loopback wire (Edit Mode)", EditorStyles.boldLabel);
            address = EditorGUILayout.TextField("Loopback IP", address);
            port = EditorGUILayout.IntField("TCP + UDP port", port);
            ticket = EditorGUILayout.TextField("Host ticket", ticket);
            scope = EditorGUILayout.TextField("Expected scope", scope);
            if (GUILayout.Button("Copy open Host connection details")) UseHost(ProbeHostWindow.Instance);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Start") && !EditorApplication.isPlaying && port is > 0 and <= 65535) Begin();
                if (GUILayout.Button("Stop")) StopNode();
            }
            if (node == null) return;
            ProbeDrawing.Stats(node);
            EditorGUILayout.LabelField("Last Read() result", node.RemoteValue.ToString());
            if (GUILayout.Button("Add(1) on Host")) node.Add();
            if (GUILayout.Button("Read() from Host")) _ = node.ReadAsync();
            bool enabled = EditorGUILayout.Toggle("UDP enabled", node.UdpEnabled);
            if (node.Ready) node.UdpEnabled = enabled;
            node.GetPose(out var x, out var y); ProbeDrawing.Draw(x, y, node.Sequence);
        }
    }

    // MCP/Editor-console entry point. No Play Mode, no synchronous waiting.
    [InitializeOnLoad]
    public static class EditorProbeDiagnostics
    {
        private static ProbeHostWindow host;
        private static ProbeClientWindow client;
        private static int stage, initialCount, readBefore;
        private static uint frozen;
        private static double started, stepAt;
        private static string result = "Idle", error = "";
        private static bool freezeEvidence, resumeEvidence;
        static EditorProbeDiagnostics()
        {
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.playModeStateChanged += state =>
            { if (state == PlayModeStateChange.ExitingEditMode) Stop(); };
        }
        private static int FreePort()
        {
            for (int i = 0; i < 16; i++)
            {
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    int candidate = ((IPEndPoint)udp.LocalEndPoint).Port;
                    var tcp = new TcpListener(IPAddress.Loopback, candidate);
                    try { tcp.Start(); return candidate; }
                    catch (SocketException) { /* Only port probes retry, never a failed session. */ }
                    finally { tcp.Stop(); }
                }
            }
            throw new InvalidOperationException("No loopback TCP/UDP port available");
        }
        public static void StartSmoke()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Edit Mode only");
            Stop();
            result = "Starting Host"; error = ""; stage = 1;
            readBefore = 0;
            freezeEvidence = resumeEvidence = false;
            started = stepAt = EditorApplication.timeSinceStartup;
            try
            {
                host = ProbeHostWindow.Instance; host.Show();
                client = ProbeClientWindow.Instance; client.Show();
                host.Begin(FreePort());
            }
            catch (Exception ex) { Fail(ex.GetType().Name); }
        }
        private static void Fail(string message)
        { error = message; result = "Failed"; stage = 0; host?.StopNode(); client?.StopNode(); }
        private static void Poll()
        {
            if (stage == 0) return;
            var now = EditorApplication.timeSinceStartup;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Stop(); return; }
            if (now - started > 14) { Fail("Smoke timeout"); return; }
            var h = host?.Node; var c = client?.Node;
            if (h?.Error.Length > 0 || c?.Error.Length > 0) { Fail("Host=" + h?.Error + "; Client=" + c?.Error); return; }
            try
            {
                if (stage == 1 && h?.Ready == true)
                { client.UseHost(host); client.Begin(); result = "Connecting Client"; stage = 2; }
                else if (stage == 2 && c?.Ready == true)
                {
                    if (typeof(RpcProbeService).GetMethod("Add")?.IsDefined(typeof(WovenTypedRpcAttribute), false) != true)
                    { Fail("RPC probe assembly was not woven by Unity ILPP"); return; }
                    initialCount = h.Count;
                    c.Add();
                    _ = c.ReadAsync();
                    result = "Checking RPC + UDP"; stage = 3;
                }
                else if (stage == 3 && h.Count > initialCount && c.Reads > 0 &&
                    c.RemoteValue != h.Count && readBefore == 0)
                { readBefore = 1; _ = c.ReadAsync(); } // One-way Add and Task Read can complete in either order.
                else if (stage == 3 && h.Count > initialCount && h.Sender == c.Peer &&
                    c.Reads > 0 && c.RemoteValue == h.Count && c.Sequence > 3 &&
                    h.Datagrams?.SentDatagrams > 0 && c.Datagrams?.ReceivedDatagrams > 0)
                {
                    h.UdpEnabled = false; stepAt = now; result = "Draining UDP"; stage = 4;
                }
                else if (stage == 4 && now - stepAt > .3)
                { frozen = c.Sequence; readBefore = c.Reads; _ = c.ReadAsync(); stepAt = now; stage = 5; result = "Checking UDP freeze + reliable Read"; }
                else if (stage == 5 && now - stepAt > .5)
                {
                    if (c.Sequence != frozen || c.Reads <= readBefore || c.RemoteValue != h.Count)
                    { Fail("UDP freeze or reliable Read failed"); return; }
                    freezeEvidence = true; h.UdpEnabled = true; stage = 6; result = "Checking UDP resume";
                }
                else if (stage == 6 && c.Sequence > frozen)
                { resumeEvidence = true; result = "Passed (windows remain running)"; stage = 0; }
            }
            catch (Exception ex) { Fail(ex.GetType().Name); }
        }
        public static string GetStatus()
        {
            var h = host?.Node; var c = client?.Node;
            return JsonUtility.ToJson(new SmokeStatus
            {
                Result = result, RpcWoven = typeof(RpcProbeService).GetMethod("Add")?.IsDefined(typeof(WovenTypedRpcAttribute), false) == true,
                HostReady = h?.Ready == true, ClientReady = c?.Ready == true,
                HostCount = h?.Count ?? 0, ClientCounter = c?.Count ?? 0, RemoteValue = c?.RemoteValue ?? -1,
                HostPeer = h?.Peer ?? "", ClientPeer = c?.Peer ?? "", Sender = h?.Sender ?? "",
                HostUdpSent = h?.Datagrams?.SentDatagrams ?? 0, ClientUdpReceived = c?.Datagrams?.ReceivedDatagrams ?? 0,
                LastSequence = c?.Sequence ?? 0, Freeze = freezeEvidence, Resume = resumeEvidence,
                HostClock = h?.ClockSeconds ?? 0, ClientClock = c?.ClockSeconds ?? 0, ClientClockUpdates = c?.ClockUpdates ?? 0,
                TimeSynchronized = c?.Runtime?.NetworkTime.IsSynchronized == true,
                HostNetworkTime = h?.Runtime?.NetworkTime.time ?? 0, ClientNetworkTime = c?.Runtime?.NetworkTime.time ?? 0,
                Error = error + " " + (h?.Error ?? "") + " " + (c?.Error ?? ""), IsPlaying = EditorApplication.isPlaying
            });
        }
        // Editor-only integration seam: callers can bind their own real components to
        // the independently connected pair. No pose/state is shared through this API.
        public static bool TryGetRuntimes(out RpcRuntime hostRuntime, out RpcRuntime clientRuntime)
        {
            hostRuntime = host?.Node?.Runtime;
            clientRuntime = client?.Node?.Runtime;
            return stage == 0 && host?.Node?.Ready == true && client?.Node?.Ready == true &&
                client.Node.Sequence > 0 && error.Length == 0;
        }
        public static void SetHostUdpEnabled(bool enabled)
        { if (host?.Node != null) host.Node.UdpEnabled = enabled; }
        public static void Stop()
        {
            stage = 0;
            // Include windows opened manually, not only those started by StartSmoke.
            foreach (var window in Resources.FindObjectsOfTypeAll<ProbeHostWindow>()) window.StopNode();
            foreach (var window in Resources.FindObjectsOfTypeAll<ProbeClientWindow>()) window.StopNode();
            host = null; client = null; result = "Stopped";
        }
        [Serializable] private sealed class SmokeStatus
        {
            public string Result, Error, HostPeer, ClientPeer, Sender;
            public bool RpcWoven, HostReady, ClientReady, Freeze, Resume, IsPlaying;
            public int HostCount, ClientCounter, RemoteValue;
            public long HostUdpSent, ClientUdpReceived;
            public long LastSequence;
            public int HostClock, ClientClock, ClientClockUpdates;
            public bool TimeSynchronized;
            public double HostNetworkTime, ClientNetworkTime;
        }
    }
}
