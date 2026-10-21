// Ragdoll Sandbox - online mode over plain UDP (System.Net.Sockets, no packages). Host-authoritative:
//  * clients send input at ~30 Hz (movement / possession, aim ray, tool selection, buttons as press counters),
//  * the host simulates everything and sends snapshots at ~20 Hz (quantized segment poses, props, players,
//    vitals, server timestamps); clients render ~100 ms in the past with interpolation and brief extrapolation,
//  * a small reliable, ordered channel (own ack/resend) carries events: spawn/despawn, body ops (cuts, holes,
//    fractures, dents - stored in bind space so clients replay them exactly), explosions, settings (gore),
//    tool changes, chat, joins/leaves, resets and late-join sync.
// All packet buffers are preallocated; the per-frame paths do not allocate.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace RagdollSandbox
{
    public enum NetFxKind : byte { Gunshot, Shotgun, Impact, Blood }

    #region Serialization
    [StructLayout(LayoutKind.Explicit)]
    struct FloatBits { [FieldOffset(0)] public float f; [FieldOffset(0)] public uint u; }

    /// <summary>Little-endian writer/reader over a preallocated byte array.</summary>
    public class NetBuffer
    {
        public readonly byte[] Data;
        public int Pos, Length;
        public bool Overflow;
        public const float PosScale = 200f; // 5 mm, +-163 m

        public NetBuffer(int size) { Data = new byte[size]; }
        public void Reset() { Pos = 0; Length = 0; Overflow = false; }
        public void SetRead(int len) { Pos = 0; Length = len; Overflow = false; }
        public int Remaining { get { return Data.Length - Pos; } }
        public bool CanRead(int n) { return Pos + n <= Length; }

        public void U8(int v) { if (Pos >= Data.Length) { Overflow = true; return; } Data[Pos++] = (byte)v; }
        public void U16(int v) { U8(v & 0xFF); U8((v >> 8) & 0xFF); }
        public void I16(int v) { U16((ushort)(short)v); }
        public void U32(uint v) { U8((int)(v & 0xFF)); U8((int)((v >> 8) & 0xFF)); U8((int)((v >> 16) & 0xFF)); U8((int)((v >> 24) & 0xFF)); }
        public void I32(int v) { U32((uint)v); }
        public void F32(float f) { var b = new FloatBits { f = f }; U32(b.u); }
        public void Vec(Vector3 v) { F32(v.x); F32(v.y); F32(v.z); }
        public void QPos(Vector3 v) { I16(Q(v.x)); I16(Q(v.y)); I16(Q(v.z)); }
        static int Q(float f) { return Mathf.Clamp(Mathf.RoundToInt(f * PosScale), -32767, 32767); }
        public void Dir(Vector3 d) { I16(Mathf.RoundToInt(Mathf.Clamp(d.x, -1f, 1f) * 32767f)); I16(Mathf.RoundToInt(Mathf.Clamp(d.y, -1f, 1f) * 32767f)); I16(Mathf.RoundToInt(Mathf.Clamp(d.z, -1f, 1f) * 32767f)); }
        public void Quat(Quaternion q) { U32(PackQuat(q)); }
        public void Col(Color c) { U8((int)(Mathf.Clamp01(c.r) * 255f)); U8((int)(Mathf.Clamp01(c.g) * 255f)); U8((int)(Mathf.Clamp01(c.b) * 255f)); }
        public void Str(string s, int maxBytes)
        {
            if (s == null) s = "";
            int n = Math.Min(s.Length, maxBytes / 3);
            if (Pos + 1 + n * 3 > Data.Length) { Overflow = true; return; }
            int bytes = Encoding.UTF8.GetBytes(s, 0, n, Data, Pos + 1);
            Data[Pos] = (byte)Math.Min(255, bytes);
            Pos += 1 + Math.Min(255, bytes);
        }

        public int RU8() { if (Pos >= Length) { Overflow = true; return 0; } return Data[Pos++]; }
        public int RU16() { int a = RU8(); return a | (RU8() << 8); }
        public int RI16() { return (short)RU16(); }
        public uint RU32() { uint a = (uint)RU8(); a |= (uint)RU8() << 8; a |= (uint)RU8() << 16; a |= (uint)RU8() << 24; return a; }
        public int RI32() { return (int)RU32(); }
        public float RF32() { var b = new FloatBits { u = RU32() }; return b.f; }
        public Vector3 RVec() { float x = RF32(), y = RF32(); return new Vector3(x, y, RF32()); }
        public Vector3 RQPos() { float x = RI16() / PosScale, y = RI16() / PosScale; return new Vector3(x, y, RI16() / PosScale); }
        public Vector3 RDir() { float x = RI16() / 32767f, y = RI16() / 32767f; return new Vector3(x, y, RI16() / 32767f); }
        public Quaternion RQuat() { return UnpackQuat(RU32()); }
        public Color RCol() { float r = RU8() / 255f, g = RU8() / 255f; return new Color(r, g, RU8() / 255f); }
        public string RStr()
        {
            int n = RU8();
            if (!CanRead(n)) { Overflow = true; return ""; }
            string s = Encoding.UTF8.GetString(Data, Pos, n);
            Pos += n;
            return s;
        }

        /// <summary>Smallest-three quaternion in 32 bits (2-bit index + 3 x 10 bits).</summary>
        public static uint PackQuat(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f || !MathUtil.IsFinite(q)) q = Quaternion.identity;
            else { q.x /= mag; q.y /= mag; q.z /= mag; q.w /= mag; }
            float ax = Mathf.Abs(q.x), ay = Mathf.Abs(q.y), az = Mathf.Abs(q.z), aw = Mathf.Abs(q.w);
            int idx = 0; float m = ax;
            if (ay > m) { idx = 1; m = ay; }
            if (az > m) { idx = 2; m = az; }
            if (aw > m) { idx = 3; }
            float sgn = (idx == 0 ? q.x : idx == 1 ? q.y : idx == 2 ? q.z : q.w) < 0f ? -1f : 1f;
            float a, b, c;
            switch (idx)
            {
                case 0: a = q.y; b = q.z; c = q.w; break;
                case 1: a = q.x; b = q.z; c = q.w; break;
                case 2: a = q.x; b = q.y; c = q.w; break;
                default: a = q.x; b = q.y; c = q.z; break;
            }
            const float S = 0.70710678f;
            uint qa = (uint)Mathf.Clamp(Mathf.RoundToInt((a * sgn / S * 0.5f + 0.5f) * 1023f), 0, 1023);
            uint qb = (uint)Mathf.Clamp(Mathf.RoundToInt((b * sgn / S * 0.5f + 0.5f) * 1023f), 0, 1023);
            uint qc = (uint)Mathf.Clamp(Mathf.RoundToInt((c * sgn / S * 0.5f + 0.5f) * 1023f), 0, 1023);
            return ((uint)idx << 30) | (qa << 20) | (qb << 10) | qc;
        }

        public static Quaternion UnpackQuat(uint v)
        {
            const float S = 0.70710678f;
            int idx = (int)(v >> 30);
            float a = (((v >> 20) & 1023) / 1023f - 0.5f) * 2f * S;
            float b = (((v >> 10) & 1023) / 1023f - 0.5f) * 2f * S;
            float c = ((v & 1023) / 1023f - 0.5f) * 2f * S;
            float d = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (idx)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }

        public static bool SeqNewer(int a, int b) { return (short)(a - b) > 0; }
    }
    #endregion

    #region Interpolation
    /// <summary>A short history of timestamped poses for N transforms (one body or one prop).</summary>
    public class PoseTrack
    {
        public const int Slots = 8;
        public readonly int Capacity;
        readonly float[] time = new float[Slots];
        readonly int[] count = new int[Slots];
        readonly Vector3[] pos;
        readonly Quaternion[] rot;
        int newest = -1, filled;
        public float NewestTime { get { return newest >= 0 ? time[newest] : -1f; } }

        public PoseTrack(int capacity)
        {
            Capacity = capacity;
            pos = new Vector3[Slots * capacity];
            rot = new Quaternion[Slots * capacity];
        }

        /// <summary>Starts a sample; returns the slot to fill, or -1 if the sample is older than the newest.</summary>
        public int Begin(float t, int n)
        {
            if (newest >= 0 && t <= time[newest]) return -1;
            newest = (newest + 1) % Slots;
            time[newest] = t;
            count[newest] = Mathf.Min(n, Capacity);
            if (filled < Slots) filled++;
            return newest;
        }

        public void Set(int slot, int i, Vector3 p, Quaternion r)
        {
            if (slot < 0 || i >= Capacity) return;
            pos[slot * Capacity + i] = p;
            rot[slot * Capacity + i] = r;
        }

        /// <summary>Pose of transform i at render time t (interpolated, or extrapolated up to maxExtra seconds).</summary>
        public bool Sample(float t, int i, float maxExtra, out Vector3 p, out Quaternion r)
        {
            p = Vector3.zero; r = Quaternion.identity;
            if (newest < 0) return false;
            int n0 = newest;
            if (i >= count[n0]) return false;
            if (t >= time[n0] || filled < 2)
            {
                // extrapolate from the two newest samples
                int n1 = (n0 - 1 + Slots) % Slots;
                if (filled < 2 || i >= count[n1]) { p = pos[n0 * Capacity + i]; r = rot[n0 * Capacity + i]; return true; }
                float dt = time[n0] - time[n1];
                float e = Mathf.Min(t - time[n0], maxExtra);
                Vector3 a = pos[n1 * Capacity + i], b = pos[n0 * Capacity + i];
                p = dt > 1e-4f ? b + (b - a) * (e / dt) : b;
                r = rot[n0 * Capacity + i];
                return true;
            }
            for (int k = 0; k < filled - 1; k++)
            {
                int hi = (n0 - k + Slots) % Slots;
                int lo = (hi - 1 + Slots) % Slots;
                if (time[lo] <= t)
                {
                    if (i >= count[lo] || i >= count[hi]) { p = pos[hi * Capacity + i]; r = rot[hi * Capacity + i]; return true; }
                    float span = time[hi] - time[lo];
                    float u = span > 1e-5f ? Mathf.Clamp01((t - time[lo]) / span) : 1f;
                    p = Vector3.LerpUnclamped(pos[lo * Capacity + i], pos[hi * Capacity + i], u);
                    r = Quaternion.Slerp(rot[lo * Capacity + i], rot[hi * Capacity + i], u);
                    return true;
                }
            }
            int oldest = (n0 - (filled - 1) + Slots) % Slots;
            if (i >= count[oldest]) return false;
            p = pos[oldest * Capacity + i]; r = rot[oldest * Capacity + i];
            return true;
        }

        public void Clear() { newest = -1; filled = 0; }
    }
    #endregion

    #region Session
    public class NetPlayerInfo
    {
        public bool used;
        public string name = "";
        public int ping;
        public bool host;
    }

    /// <summary>The network session: offline (inactive), host or client.</summary>
    public class NetSession
    {
        public const int Magic = 0x5244, Protocol = 4, MaxPlayersHard = 4, Mtu = 1150;
        public const int ExplosiveKindBase = 100, CarKind = 6;
        enum Pkt : byte { Hello = 1, Welcome, Reject, Input, Snapshot, Reliable, Bye, Fx }
        enum Msg : byte
        {
            SpawnBody = 1, Despawn, BodyOp, Owner, Explosion, Settings, Chat, PlayerJoin, PlayerLeave, Reset, PropSpawn, PropDespawn,
            WorldProp, Kill, ToolChange,
            SpawnRequest = 40, ResetRequest, ToolAction, ChatSend
        }

        class RelMsg { public int id; public readonly byte[] data = new byte[320]; public int len; public float lastSent = -10f; }

        class Peer
        {
            public int id;
            public EndPoint ep;
            public string name = "";
            public float lastRecv;
            public readonly List<RelMsg> outQ = new List<RelMsg>(64);
            public int nextOutId = 1;
            public int inExpected = 1;
            public float rtt = 0.1f;
            // client state as seen by the host
            public int inputSeq = -1;
            public Vector3 playerPos;
            public float yaw, pitch;
            public bool possess, laser, driving;
            public int tool;
            public RagdollBody body;
            public float lastClientTime, lastClientTimeRecv;
            public int press0, press1, jumpCount;
            public bool held0, held1, shift, ctrl;
            public float scrollAcc;
            public Ray aim;
            public Vector3 moveWorld;
            public float camYaw;
            public bool run, crouch, reachL, reachR;
            public int lastPress0 = -1, lastPress1 = -1, lastJump = -1;
            public bool prevHeld0, prevHeld1;
            public readonly Dictionary<int, float> bodySent = new Dictionary<int, float>(16);
            public readonly Dictionary<int, float> propSent = new Dictionary<int, float>(512);
            public int snapCounter;
            public bool synced;
        }

        public bool Active { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsClient { get; private set; }
        public bool Connecting { get; private set; }
        public int LocalId { get; private set; }
        public string LocalIP { get; private set; }
        public string Status = "offline";
        public readonly NetPlayerInfo[] Infos = new NetPlayerInfo[MaxPlayersHard];

        Socket socket;
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint hostEp;
        readonly NetBuffer rx = new NetBuffer(2048);
        readonly NetBuffer tx = new NetBuffer(1400);
        readonly NetBuffer msg = new NetBuffer(400);
        readonly Peer[] peers = new Peer[MaxPlayersHard];   // host: remote players by id (1..3); client: peers[0] = host
        readonly Stack<RelMsg> pool = new Stack<RelMsg>(256);
        float snapTimer, inputTimer, relTimer, helloTimer, connectStart, settingsTimer;
        bool settingsDirty;
        int snapSeq, inputSeq;
        float serverOffset;
        bool haveOffset;
        float rttClient = 0.1f;
        int bytesOut, bytesIn, bytesOutLast, bytesInLast;
        float bwTimer;
        string pendingName;
        int fxBudget;
        float fxTimer;

        // client-side state
        readonly Dictionary<int, RagdollBody> bodies = new Dictionary<int, RagdollBody>(16);
        readonly Dictionary<int, PoseTrack> bodyTracks = new Dictionary<int, PoseTrack>(16);
        readonly Dictionary<int, PoseTrack> propTracks = new Dictionary<int, PoseTrack>(512);
        readonly Dictionary<int, Transform> propXf = new Dictionary<int, Transform>(512);
        RagdollBody myBody;
        int myBodyNetId = -1;
        int press0, press1, jumpCount;
        float scrollAcc;
        readonly List<int> tmpKeys = new List<int>(64);

        // host-side state
        int nextBodyNetId = 1, nextPropNetId = 1;
        class NetProp { public int id; public GameObject go; public Rigidbody rb; public int kind; public float lastMove; }
        readonly Dictionary<int, NetProp> netProps = new Dictionary<int, NetProp>(128);
        readonly List<int> deadProps = new List<int>(16);
        readonly List<bool> worldAlive = new List<bool>(512);
        readonly List<float> worldMove = new List<float>(512);

        // avatars and name tags (both sides)
        class Avatar
        {
            public GameObject go;
            public LineRenderer beam;
            public Vector3 pos, targetPos;
            public float yaw, targetYaw, pitch;
            public bool possess, laser, driving, visible;
            public Vector3 laserEnd;
            public int tool;
            public int bodyNetId = -1;
        }
        readonly Avatar[] avatars = new Avatar[MaxPlayersHard];
        GameObject tagCanvas;
        readonly Text[] tags = new Text[MaxPlayersHard];
        Transform root;

        public NetSession()
        {
            for (int i = 0; i < MaxPlayersHard; i++) Infos[i] = new NetPlayerInfo();
            for (int i = 0; i < 256; i++) pool.Push(new RelMsg());
            LocalIP = DetectLocalIP();
            SandboxEvents.Kill += OnKill;
        }

        #region Start / stop
        public bool StartHost(int port, string name)
        {
            Shutdown(null);
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                DisableConnReset(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (Exception e)
            {
                CloseSocket();
                Status = "could not open UDP port " + port + ": " + e.Message;
                SandboxEvents.RaiseToast(Status, new Color(1f, 0.5f, 0.4f));
                return false;
            }
            Active = true; IsHost = true; IsClient = false; Connecting = false;
            LocalId = 0;
            Infos[0].used = true; Infos[0].name = Clean(name); Infos[0].host = true; Infos[0].ping = 0;
            EnsureVisuals();
            worldAlive.Clear(); worldMove.Clear();
            var wp = Sandbox.World.Props;
            for (int i = 0; i < wp.Count; i++) { worldAlive.Add(wp[i].go != null && wp[i].go.activeInHierarchy); worldMove.Add(Time.time); }
            // number the bodies and props that already exist
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) { var r = Sandbox.Ragdolls[i]; if (r != null) r.NetId = nextBodyNetId++; }
            for (int i = 0; i < Sandbox.Vehicles.Cars.Count; i++) OnPropSpawned(Sandbox.Vehicles.Cars[i].go, CarKind);
            Status = "hosting on " + LocalIP + ":" + port;
            SandboxEvents.RaiseToast("Hosting on " + LocalIP + ":" + port + " (UDP)", new Color(0.6f, 1f, 0.7f));
            // the host's own ragdoll
            var b = Sandbox.Boot.SpawnRagdoll(Sandbox.World.RagdollSpawn + new Vector3(-2.5f, 0f, -1f), Sandbox.World.RagdollSpawnYaw);
            if (b != null) { AssignBody(0, b); Sandbox.Boot.Possess(b); }
            return true;
        }

        public bool Join(string address, int port, string name)
        {
            Shutdown(null);
            IPAddress ip;
            if (!IPAddress.TryParse(address.Trim(), out ip))
            {
                try
                {
                    var list = Dns.GetHostAddresses(address.Trim());
                    ip = null;
                    for (int i = 0; i < list.Length; i++) if (list[i].AddressFamily == AddressFamily.InterNetwork) { ip = list[i]; break; }
                }
                catch (Exception) { ip = null; }
            }
            if (ip == null) { Status = "unknown address " + address; SandboxEvents.RaiseToast(Status, new Color(1f, 0.5f, 0.4f)); return false; }
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                DisableConnReset(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                hostEp = new IPEndPoint(ip, port);
                socket.Connect(hostEp);
            }
            catch (Exception e)
            {
                CloseSocket();
                Status = "could not connect: " + e.Message;
                SandboxEvents.RaiseToast(Status, new Color(1f, 0.5f, 0.4f));
                return false;
            }
            Active = true; IsHost = false; IsClient = true; Connecting = true;
            pendingName = Clean(name);
            connectStart = Time.unscaledTime;
            helloTimer = 0f;
            peers[0] = new Peer { id = 0, ep = hostEp, lastRecv = Time.unscaledTime };
            Status = "connecting to " + ip + ":" + port + "...";
            return true;
        }

        static void DisableConnReset(Socket s)
        {
            // Windows reports ICMP "port unreachable" as a socket error on the next receive; turn that off
            try { s.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
            s.Blocking = false;
        }

        public void Shutdown(string reason)
        {
            if (!Active) { CloseSocket(); return; }
            bool wasClient = IsClient;
            // tell the other side (unreliable, a few times)
            for (int k = 0; k < 3; k++)
            {
                if (IsHost) { for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null) { Header(Pkt.Bye); tx.Str(reason ?? "", 60); SendTo(peers[i]); } }
                else if (peers[0] != null) { Header(Pkt.Bye); tx.Str(reason ?? "", 60); SendTo(peers[0]); }
            }
            CloseSocket();
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                if (peers[i] != null) { for (int k = 0; k < peers[i].outQ.Count; k++) pool.Push(peers[i].outQ[k]); peers[i] = null; }
                Infos[i].used = false;
                if (Sandbox.Tools != null) Sandbox.Tools.RemoveRemote(i);
                HideAvatar(i);
            }
            Active = false; IsHost = false; IsClient = false; Connecting = false;
            bodies.Clear(); bodyTracks.Clear(); propTracks.Clear(); propXf.Clear(); netProps.Clear();
            myBody = null; myBodyNetId = -1; haveOffset = false;
            Status = "offline";
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) { var r = Sandbox.Ragdolls[i]; if (r != null) r.OwnerPlayer = -1; }
            if (wasClient && Sandbox.Boot != null && Sandbox.Boot.Built && reason != "closed")
            {
                // back to an offline world of our own
                Sandbox.Boot.RebuildWorld(Sandbox.Seed, true);
            }
            if (!string.IsNullOrEmpty(reason) && reason != "closed") SandboxEvents.RaiseToast("Online session ended: " + reason, new Color(1f, 0.8f, 0.5f));
        }

        void CloseSocket()
        {
            if (socket != null) { try { socket.Close(); } catch (Exception) { } }
            socket = null;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Player";
            s = s.Trim();
            if (s.Length > 16) s = s.Substring(0, 16);
            return s.Length == 0 ? "Player" : s;
        }

        static string DetectLocalIP()
        {
            try
            {
                // a connected UDP socket picks the outgoing interface without sending anything
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(IPAddress.Parse("10.255.255.255"), 9));
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep != null && !IPAddress.IsLoopback(ep.Address) && !ep.Address.Equals(IPAddress.Any)) return ep.Address.ToString();
                }
            }
            catch (Exception) { }
            try
            {
                var list = Dns.GetHostAddresses(Dns.GetHostName());
                for (int i = 0; i < list.Length; i++) if (list[i].AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(list[i])) return list[i].ToString();
            }
            catch (Exception) { }
            return "127.0.0.1";
        }
        #endregion

        #region Packet helpers
        void Header(Pkt t)
        {
            tx.Reset();
            tx.U16(Magic);
            tx.U8((int)t);
        }

        void HeaderAck(Pkt t, Peer p)
        {
            Header(t);
            tx.U16((p.inExpected - 1) & 0xFFFF);
        }

        void SendTo(Peer p)
        {
            if (socket == null || p == null || tx.Overflow) return;
            try
            {
                if (IsClient) socket.Send(tx.Data, 0, tx.Pos, SocketFlags.None);
                else socket.SendTo(tx.Data, 0, tx.Pos, SocketFlags.None, p.ep);
                bytesOut += tx.Pos + 28;
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        RelMsg BeginMsg(Msg type)
        {
            msg.Reset();
            msg.U8((int)type);
            return null;
        }

        /// <summary>Queues the message currently in 'msg' on one peer's reliable channel.</summary>
        void QueueMsg(Peer p)
        {
            if (p == null || msg.Overflow) return;
            var m = pool.Count > 0 ? pool.Pop() : new RelMsg();
            m.id = p.nextOutId;
            p.nextOutId = (p.nextOutId + 1) & 0xFFFF;
            if (p.nextOutId == 0) p.nextOutId = 1;
            Buffer.BlockCopy(msg.Data, 0, m.data, 0, msg.Pos);
            m.len = msg.Pos;
            m.lastSent = -10f;
            p.outQ.Add(m);
        }

        void QueueAll(int exceptId = -1)
        {
            if (IsClient) { QueueMsg(peers[0]); return; }
            for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null && i != exceptId && peers[i].synced) QueueMsg(peers[i]);
        }

        void FlushReliable(Peer p, float now)
        {
            if (p == null || p.outQ.Count == 0) return;
            float resend = Mathf.Clamp(p.rtt * 1.5f + 0.03f, 0.08f, 0.5f);
            HeaderAck(Pkt.Reliable, p);
            int countPos = tx.Pos;
            tx.U8(0);
            int n = 0;
            for (int i = 0; i < p.outQ.Count && n < 60; i++)
            {
                var m = p.outQ[i];
                if (now - m.lastSent < resend) continue;
                if (tx.Pos + m.len + 4 > Mtu) break;
                tx.U16(m.id);
                tx.U8(m.len);
                Buffer.BlockCopy(m.data, 0, tx.Data, tx.Pos, m.len);
                tx.Pos += m.len;
                m.lastSent = now;
                n++;
            }
            if (n == 0) return;
            tx.Data[countPos] = (byte)n;
            SendTo(p);
        }

        void ProcessAck(Peer p, int ack)
        {
            for (int i = p.outQ.Count - 1; i >= 0; i--)
            {
                var m = p.outQ[i];
                if (!NetBuffer.SeqNewer(m.id, ack)) { p.outQ.RemoveAt(i); pool.Push(m); }
            }
        }
        #endregion

        #region Tick
        public void Tick(float dt)
        {
            if (!Active) return;
            float now = Time.unscaledTime;
            Receive(now);
            if (!Active) return;
            if (IsHost) HostTick(now, dt);
            else ClientTick(now, dt);
            bwTimer += dt;
            if (bwTimer >= 1f) { bwTimer = 0f; bytesOutLast = bytesOut; bytesInLast = bytesIn; bytesOut = 0; bytesIn = 0; }
            UpdateAvatars(dt);
        }

        public void FixedTick(float dt) { }

        public void LateTick()
        {
            if (!Active) return;
            if (IsClient && !Connecting) ApplyInterpolation();
            UpdateNameTags();
        }

        void Receive(float now)
        {
            if (socket == null) return;
            int guard = 0;
            while (guard++ < 256)
            {
                int len;
                EndPoint from = anyEp;
                try
                {
                    if (socket.Available <= 0) break;
                    if (IsClient) len = socket.Receive(rx.Data, 0, rx.Data.Length, SocketFlags.None);
                    else len = socket.ReceiveFrom(rx.Data, 0, rx.Data.Length, SocketFlags.None, ref from);
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { return; }
                bytesIn += len + 28;
                if (len < 3) continue;
                rx.SetRead(len);
                if (rx.RU16() != Magic) continue;
                var type = (Pkt)rx.RU8();
                try
                {
                    if (IsHost) HostReceive(type, from, now);
                    else ClientReceive(type, now);
                }
                catch (Exception e) { Debug.LogWarning("[RagdollSandbox] Bad packet (" + type + "): " + e.Message); }
                if (!Active) return;
            }
        }
        #endregion

        #region Host
        Peer FindPeer(EndPoint ep)
        {
            for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null && peers[i].ep.Equals(ep)) return peers[i];
            return null;
        }

        void HostReceive(Pkt type, EndPoint from, float now)
        {
            var p = FindPeer(from);
            if (type == Pkt.Hello)
            {
                int proto = rx.RU16();
                string name = Clean(rx.RStr());
                if (proto != Protocol) { SendReject(from, "version mismatch (host v" + Protocol + ")"); return; }
                if (p == null)
                {
                    int id = -1;
                    int max = Mathf.Clamp(Sandbox.Boot.network.maxPlayers, 2, MaxPlayersHard);
                    for (int i = 1; i < max; i++) if (peers[i] == null) { id = i; break; }
                    if (id < 0) { SendReject(from, "server full"); return; }
                    p = new Peer { id = id, ep = from, name = UniqueName(name), lastRecv = now };
                    peers[id] = p;
                    Infos[id].used = true; Infos[id].name = p.name; Infos[id].host = false;
                    OnPeerJoined(p);
                }
                p.lastRecv = now;
                SendWelcome(p);
                return;
            }
            if (p == null) return;
            p.lastRecv = now;
            switch (type)
            {
                case Pkt.Input: ReadInput(p); break;
                case Pkt.Reliable:
                    ProcessAck(p, rx.RU16());
                    ReadReliable(p);
                    break;
                case Pkt.Bye: DropPeer(p, "left"); break;
            }
        }

        string UniqueName(string n)
        {
            string s = n;
            for (int k = 2; k < 10; k++)
            {
                bool used = false;
                for (int i = 0; i < MaxPlayersHard; i++) if (Infos[i].used && Infos[i].name == s) used = true;
                if (!used) return s;
                s = n + k;
            }
            return s;
        }

        void SendReject(EndPoint ep, string reason)
        {
            Header(Pkt.Reject);
            tx.Str(reason, 90);
            try { socket.SendTo(tx.Data, 0, tx.Pos, SocketFlags.None, ep); } catch (Exception) { }
        }

        void SendWelcome(Peer p)
        {
            var b = Sandbox.Boot;
            var cs = b.character;
            Header(Pkt.Welcome);
            tx.U8(p.id);
            tx.I32(Sandbox.Seed);
            tx.F32(Time.time);
            tx.U8((int)cs.look);
            tx.F32(cs.height); tx.F32(cs.mass);
            tx.Col(cs.skinColor); tx.Col(cs.shirtColor); tx.Col(cs.pantsColor); tx.Col(cs.shoesColor); tx.Col(cs.hairColor); tx.Col(cs.eyeColor);
            tx.U8((cs.shirt ? 1 : 0) | (cs.pants ? 2 : 0) | (cs.shoes ? 4 : 0) | (cs.hair ? 8 : 0));
            tx.U8(b.network.maxPlayers);
            SendTo(p);
        }

        void OnPeerJoined(Peer p)
        {
            // late-join sync: settings, players, world prop states, bodies with their op history, spawned props
            WriteSettings(); QueueMsg(p);
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                if (!Infos[i].used || i == p.id) continue;
                BeginMsg(Msg.PlayerJoin); msg.U8(i); msg.Str(Infos[i].name, 48); QueueMsg(p);
            }
            for (int i = 0; i < worldAlive.Count; i++)
                if (!worldAlive[i]) { BeginMsg(Msg.WorldProp); msg.U16(i); msg.U8(0); QueueMsg(p); }
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null || r.Destroyed) continue;
                WriteSpawnBody(r); QueueMsg(p);
                for (int k = 0; k < r.OpLog.Count; k++) { WriteOp(r.NetId, r.OpLog[k]); QueueMsg(p); }
            }
            foreach (var kv in netProps)
            {
                var np = kv.Value;
                if (np.go == null) continue;
                WritePropSpawn(np); QueueMsg(p);
            }
            p.synced = true;
            // tell everybody else, then give the new player a ragdoll
            BeginMsg(Msg.PlayerJoin); msg.U8(p.id); msg.Str(p.name, 48); QueueAll(p.id);
            var body = Sandbox.Boot.SpawnRagdoll(Sandbox.World.RagdollSpawn + new Vector3((p.id - 1.5f) * 2.2f, 0f, -1.5f), Sandbox.World.RagdollSpawnYaw);
            if (body != null) AssignBody(p.id, body);
            SandboxEvents.RaiseToast(p.name + " joined", new Color(0.7f, 0.9f, 1f));
            if (Sandbox.Hud != null) Sandbox.Hud.AddChat(null, p.name + " joined", new Color(0.7f, 0.9f, 1f));
        }

        void DropPeer(Peer p, string reason)
        {
            if (p == null) return;
            int id = p.id;
            for (int k = 0; k < p.outQ.Count; k++) pool.Push(p.outQ[k]);
            peers[id] = null;
            Infos[id].used = false;
            if (Sandbox.Tools != null) Sandbox.Tools.RemoveRemote(id);
            HideAvatar(id);
            BeginMsg(Msg.PlayerLeave); msg.U8(id); msg.Str(reason, 40); QueueAll();
            // the leaving player's ragdoll goes back to being an NPC
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r != null && r.OwnerPlayer == id) { r.OwnerPlayer = -1; r.SetPossessed(false); BeginMsg(Msg.Owner); msg.U16(r.NetId); msg.U8(255); QueueAll(); }
            }
            SandboxEvents.RaiseToast(p.name + " " + reason, new Color(1f, 0.8f, 0.6f));
            if (Sandbox.Hud != null) Sandbox.Hud.AddChat(null, p.name + " " + reason, new Color(1f, 0.8f, 0.6f));
        }

        void ReadInput(Peer p)
        {
            ProcessAck(p, rx.RU16());
            int seq = rx.RU16();
            if (p.inputSeq >= 0 && !NetBuffer.SeqNewer(seq, p.inputSeq)) return;
            p.inputSeq = seq;
            p.lastClientTime = rx.RF32();
            p.lastClientTimeRecv = Time.unscaledTime;
            Infos[p.id].ping = rx.RU16();
            int flags = rx.RU8();
            p.possess = (flags & 1) != 0; p.run = (flags & 2) != 0; p.crouch = (flags & 4) != 0;
            p.reachL = (flags & 8) != 0; p.reachR = (flags & 16) != 0; p.driving = (flags & 32) != 0;
            int tb = rx.RU8();
            p.held0 = (tb & 1) != 0; p.held1 = (tb & 2) != 0; p.shift = (tb & 4) != 0; p.ctrl = (tb & 8) != 0;
            p.laser = (tb & 16) != 0;
            p.press0 = rx.RU8(); p.press1 = rx.RU8(); p.jumpCount = rx.RU8();
            p.scrollAcc += rx.RI16() / 100f;
            int tool = rx.RU8(), expl = rx.RU8(), prop = rx.RU8(), grabMode = rx.RU8();
            p.playerPos = rx.RQPos();
            p.yaw = rx.RU16() * (360f / 65535f);
            p.pitch = rx.RI16() / 100f;
            Vector3 o = rx.RQPos();
            Vector3 d = rx.RDir();
            p.aim = new Ray(o, MathUtil.SafeNormal(d, Vector3.forward));
            p.camYaw = rx.RU16() * (360f / 65535f);
            p.moveWorld = new Vector3(rx.RI16() / 1000f, 0f, rx.RI16() / 1000f);
            if (rx.Overflow) return;
            if (tool != p.tool)
            {
                p.tool = tool;
                BeginMsg(Msg.ToolChange); msg.U8(p.id); msg.U8(tool); QueueAll(p.id);
            }
            var tu = Sandbox.Tools.RemoteUser(p.id);
            if (tu != null) tu.SetSelection((ToolId)Mathf.Clamp(tool, 0, ToolSystem.ToolCount - 1), expl, prop, grabMode & 1);
        }

        /// <summary>Feeds remote players' latest input to their tool users and possessed ragdolls (every frame).</summary>
        void ApplyRemoteInputs()
        {
            for (int i = 1; i < MaxPlayersHard; i++)
            {
                var p = peers[i];
                if (p == null || p.inputSeq < 0) continue;
                var tu = Sandbox.Tools.RemoteUser(i);
                var inp = new ToolInput();
                inp.aim = p.aim;
                inp.right = MathUtil.SafeNormal(Vector3.Cross(Vector3.up, p.aim.direction), Vector3.right);
                inp.up = Vector3.Cross(p.aim.direction, inp.right);
                inp.blocked = p.possess || p.driving;
                inp.held0 = p.held0; inp.held1 = p.held1;
                inp.down0 = p.lastPress0 >= 0 && p.press0 != p.lastPress0;
                inp.down1 = p.lastPress1 >= 0 && p.press1 != p.lastPress1;
                inp.up0 = p.prevHeld0 && !p.held0;
                inp.up1 = p.prevHeld1 && !p.held1;
                inp.shift = p.shift; inp.ctrl = p.ctrl;
                inp.scroll = p.scrollAcc;
                p.scrollAcc = 0f;
                p.lastPress0 = p.press0; p.lastPress1 = p.press1;
                p.prevHeld0 = p.held0; p.prevHeld1 = p.held1;
                if (tu != null) tu.Input = inp;
                // possession
                var b = p.body;
                if (b != null && (b.Destroyed || b.OwnerPlayer != p.id)) { b = null; p.body = null; }
                if (b != null)
                {
                    if (p.possess && !b.Possessed) b.SetPossessed(true);
                    else if (!p.possess && b.Possessed) b.SetPossessed(false);
                    if (p.possess)
                    {
                        var pi = b.Brain.Input;
                        pi.move = Vector3.ClampMagnitude(p.moveWorld, 1f);
                        pi.camYaw = p.camYaw;
                        pi.run = p.run; pi.crouch = p.crouch;
                        if (p.lastJump >= 0 && p.jumpCount != p.lastJump) pi.jump = true;
                        pi.reachL = p.reachL; pi.reachR = p.reachR;
                        pi.aimDir = p.aim.direction;
                        RaycastHit hit;
                        if (Sandbox.Tools.RaycastFirst(p.aim, 40f, out hit))
                        {
                            var bp = Registry.PartOf(hit.collider);
                            pi.aimPoint = bp != null && bp.body == b ? p.aim.origin + p.aim.direction * 6f : hit.point;
                        }
                        else pi.aimPoint = p.aim.origin + p.aim.direction * 20f;
                        b.Brain.Input = pi;
                    }
                }
                p.lastJump = p.jumpCount;
            }
        }

        void ReadReliable(Peer p)
        {
            int n = rx.RU8();
            for (int k = 0; k < n; k++)
            {
                int id = rx.RU16();
                int len = rx.RU8();
                if (!rx.CanRead(len)) return;
                int end = rx.Pos + len;
                if (id == p.inExpected)
                {
                    p.inExpected = (p.inExpected + 1) & 0xFFFF;
                    if (p.inExpected == 0) p.inExpected = 1;
                    int save = rx.Length;
                    rx.Length = end;
                    if (IsHost) HostMessage(p); else ClientMessage();
                    rx.Length = save;
                }
                rx.Pos = end;
                if (!Active) return;
            }
        }

        void HostMessage(Peer p)
        {
            var t = (Msg)rx.RU8();
            switch (t)
            {
                case Msg.SpawnRequest:
                    {
                        Vector3 pos = rx.RVec(); float yaw = rx.RF32();
                        Sandbox.Boot.SpawnRagdoll(pos, yaw);
                        break;
                    }
                case Msg.ResetRequest: Sandbox.Boot.ResetSandbox(rx.RU8() != 0); break;
                case Msg.ToolAction:
                    {
                        int tool = rx.RU8(), sub = rx.RU8(); bool sec = rx.RU8() != 0;
                        Vector3 o = rx.RVec(), d = rx.RVec();
                        Sandbox.Tools.RemoteAction(p.id, (ToolId)tool, sub, sec, new Ray(o, d));
                        break;
                    }
                case Msg.ChatSend:
                    {
                        string text = rx.RStr();
                        BroadcastChat(p.id, text);
                        break;
                    }
            }
        }

        void HostTick(float now, float dt)
        {
            var ns = Sandbox.Boot.network;
            // timeouts
            for (int i = 1; i < MaxPlayersHard; i++)
            {
                var p = peers[i];
                if (p != null && now - p.lastRecv > ns.timeout) DropPeer(p, "timed out");
            }
            ApplyRemoteInputs();
            // destroyed spawned props and world props
            deadProps.Clear();
            foreach (var kv in netProps) if (kv.Value.go == null) deadProps.Add(kv.Key);
            for (int i = 0; i < deadProps.Count; i++)
            {
                netProps.Remove(deadProps[i]);
                BeginMsg(Msg.PropDespawn); msg.U16(deadProps[i]); QueueAll();
            }
            var wp = Sandbox.World.Props;
            while (worldAlive.Count < wp.Count) { worldAlive.Add(true); worldMove.Add(Time.time); }
            for (int i = 0; i < wp.Count; i++)
            {
                bool alive = wp[i].go != null && wp[i].go.activeInHierarchy;
                if (alive != worldAlive[i])
                {
                    worldAlive[i] = alive;
                    worldMove[i] = Time.time;
                    BeginMsg(Msg.WorldProp); msg.U16(i); msg.U8(alive ? 1 : 0); QueueAll();
                }
                var rb = wp[i].rb;
                if (alive && rb != null && !rb.isKinematic && !rb.IsSleeping()) worldMove[i] = Time.time;
            }
            foreach (var kv in netProps) { var np = kv.Value; if (np.rb != null && !np.rb.IsSleeping()) np.lastMove = Time.time; }
            if (settingsDirty)
            {
                settingsTimer -= dt;
                if (settingsTimer <= 0f) { settingsDirty = false; WriteSettings(); QueueAll(); }
            }
            fxTimer -= dt;
            if (fxTimer <= 0f) { fxTimer = 0.1f; fxBudget = 6; }
            snapTimer -= dt;
            if (snapTimer <= 0f)
            {
                snapTimer += 1f / Mathf.Max(5, ns.snapshotRate);
                if (snapTimer < 0f) snapTimer = 0f;
                snapSeq = (snapSeq + 1) & 0xFFFF;
                for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null) SendSnapshot(peers[i]);
            }
            relTimer -= dt;
            if (relTimer <= 0f)
            {
                relTimer = 0.05f;
                for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null) FlushReliable(peers[i], now);
            }
            // the host's own avatar data for the snapshot
            Infos[0].ping = 0;
        }

        static bool BodyAsleep(RagdollBody r)
        {
            for (int i = 0; i < r.AllPieces.Count; i++) { var p = r.AllPieces[i]; if (p != null && p.rb != null && !p.rb.IsSleeping()) return false; }
            return true;
        }

        void SendSnapshot(Peer p)
        {
            var ns = Sandbox.Boot.network;
            float now = Time.time;
            p.snapCounter++;
            int part = 0;
            BeginSnapshot(p, part);
            // players: the host and every other client
            int countPos = tx.Pos; tx.U8(0); int count = 0;
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                if (!Infos[i].used || i == p.id) continue;
                WritePlayer(i);
                count++;
            }
            tx.Data[countPos] = (byte)count;
            // bodies
            countPos = tx.Pos; tx.U8(0); count = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null || r.Destroyed || r.Pelvis == null) continue;
                float last;
                bool sent = p.bodySent.TryGetValue(r.NetId, out last);
                bool asleep = BodyAsleep(r);
                float dist = (r.CenterOfMass - p.playerPos).magnitude;
                if (sent)
                {
                    if (asleep && now - last < 1f) continue;
                    if (dist > ns.farDistance && (p.snapCounter % 3) != 0 && now - last < 1f) continue;
                }
                int need = 9 + r.AllPieces.Count * 10;
                if (tx.Pos + need > Mtu)
                {
                    tx.Data[countPos] = (byte)count;
                    tx.U8(0); // no props in this packet
                    SendTo(p);
                    part++;
                    BeginSnapshot(p, part);
                    tx.U8(0); // no players
                    countPos = tx.Pos; tx.U8(0); count = 0;
                }
                WriteBody(r);
                p.bodySent[r.NetId] = now;
                count++;
            }
            tx.Data[countPos] = (byte)count;
            // props (world props by index, spawned props by 0x8000 | netId)
            countPos = tx.Pos; tx.U8(0); count = 0;
            int budgetNew = 40;
            var wp = Sandbox.World.Props;
            for (int i = 0; i < wp.Count && i < worldAlive.Count; i++)
            {
                if (!worldAlive[i] || wp[i].rb == null) continue;
                float last;
                bool sent = p.propSent.TryGetValue(i, out last);
                if (sent && last > worldMove[i] + 0.3f) continue;
                if (!sent) { if (budgetNew <= 0) continue; budgetNew--; }
                if (count >= 250 || tx.Pos + 14 > Mtu) { tx.Data[countPos] = (byte)count; SendTo(p); part++; BeginSnapshot(p, part); tx.U8(0); tx.U8(0); countPos = tx.Pos; tx.U8(0); count = 0; }
                Transform t = wp[i].go.transform;
                tx.U16(i); tx.QPos(t.position); tx.Quat(t.rotation);
                p.propSent[i] = now;
                count++;
            }
            foreach (var kv in netProps)
            {
                var np = kv.Value;
                if (np.go == null) continue;
                float last;
                int key = 0x8000 | np.id;
                bool sent = p.propSent.TryGetValue(key, out last);
                if (sent && last > np.lastMove + 0.3f) continue;
                if (count >= 250 || tx.Pos + 14 > Mtu) { tx.Data[countPos] = (byte)count; SendTo(p); part++; BeginSnapshot(p, part); tx.U8(0); tx.U8(0); countPos = tx.Pos; tx.U8(0); count = 0; }
                Transform t = np.go.transform;
                tx.U16(key); tx.QPos(t.position); tx.Quat(t.rotation);
                p.propSent[key] = now;
                count++;
            }
            tx.Data[countPos] = (byte)count;
            SendTo(p);
        }

        void BeginSnapshot(Peer p, int part)
        {
            HeaderAck(Pkt.Snapshot, p);
            tx.U16(snapSeq);
            tx.U8(part);
            tx.F32(Time.time);
            tx.F32(p.lastClientTime + (Time.unscaledTime - p.lastClientTimeRecv));
        }

        void WritePlayer(int id)
        {
            tx.U8(id);
            Vector3 pos; float yaw, pitch; bool possess, laser, driving; Vector3 laserEnd; int tool;
            if (id == 0)
            {
                var pl = Sandbox.Player;
                pos = pl != null ? pl.transform.position : Vector3.zero;
                yaw = pl != null ? pl.Yaw : 0f; pitch = pl != null ? pl.Pitch : 0f;
                possess = Sandbox.Possessed != null;
                driving = Sandbox.Vehicles.Driving != null;
                if (driving) pos = Sandbox.Vehicles.Driving.go.transform.position;
                var lu = Sandbox.Tools.Local;
                laser = lu.LaserFiring; laserEnd = lu.LaserEnd; tool = (int)lu.Current;
            }
            else
            {
                var p = peers[id];
                pos = p.playerPos; yaw = p.yaw; pitch = p.pitch; possess = p.possess; driving = p.driving;
                var tu = Sandbox.Tools.Remote[id];
                laser = tu != null && tu.LaserFiring; laserEnd = tu != null ? tu.LaserEnd : Vector3.zero; tool = p.tool;
            }
            tx.U8((possess ? 1 : 0) | (laser ? 2 : 0) | (driving ? 4 : 0));
            tx.QPos(pos);
            tx.U16(Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 65535f));
            tx.I16(Mathf.RoundToInt(pitch * 100f));
            tx.U8(tool);
            if (laser) tx.QPos(laserEnd);
        }

        void WriteBody(RagdollBody r)
        {
            tx.U16(r.NetId);
            var v = r.Vitals;
            tx.U8((int)r.Brain.State);
            tx.U8(Mathf.RoundToInt(Mathf.Clamp01(v.BloodFraction) * 255f));
            tx.U8(Mathf.RoundToInt(Mathf.Clamp01(v.Consciousness) * 255f));
            tx.U8(Mathf.RoundToInt(Mathf.Clamp01(v.Pain) * 255f));
            tx.U8((v.Dead ? 1 : 0) | (v.Unconscious ? 2 : 0) | (r.Possessed ? 4 : 0));
            int n = Mathf.Min(r.AllPieces.Count, 64);
            tx.U8(n);
            for (int i = 0; i < n; i++)
            {
                var p = r.AllPieces[i];
                Transform t = p != null ? p.transform : r.transform;
                tx.QPos(t.position);
                tx.Quat(t.rotation);
            }
        }
        #endregion

        #region Host -> client events
        void WriteSettings()
        {
            var b = Sandbox.Boot;
            BeginMsg(Msg.Settings);
            msg.U8((int)b.dismember.gore);
            msg.U8(TimeControl.SlowMotion ? 1 : 0);
            msg.U8((int)b.physics.gravity);
            msg.F32(b.blood.bloodAmount);
            msg.U8(b.general.reactions ? 1 : 0);
            msg.F32(b.physics.slowMotionScale);
        }

        void WriteSpawnBody(RagdollBody r)
        {
            BeginMsg(Msg.SpawnBody);
            msg.U16(r.NetId);
            msg.U8(r.Index);
            msg.U8(r.OwnerPlayer < 0 ? 255 : r.OwnerPlayer);
            msg.Vec(r.transform.position);
            msg.F32(r.transform.eulerAngles.y);
        }

        void WriteOp(int netId, BodyOp op)
        {
            BeginMsg(Msg.BodyOp);
            msg.U16(netId);
            msg.U8((int)op.type);
            msg.U8(op.seg);
            msg.U8(op.seg2 + 1);
            msg.Vec(op.a); msg.Vec(op.b);
            msg.F32(op.f0); msg.F32(op.f1); msg.F32(op.f2);
            msg.U8(op.flags);
        }

        void WritePropSpawn(NetProp np)
        {
            BeginMsg(Msg.PropSpawn);
            msg.U16(np.id);
            msg.U8(np.kind);
            msg.Vec(np.go.transform.position);
            msg.Quat(np.go.transform.rotation);
        }

        public void OnRagdollSpawned(RagdollBody body)
        {
            if (body == null) return;
            if (!Active || !IsHost) return;
            body.NetId = nextBodyNetId++;
            WriteSpawnBody(body);
            QueueAll();
        }

        public void OnRagdollRemoved(RagdollBody body)
        {
            if (!Active || body == null) return;
            if (IsHost)
            {
                BeginMsg(Msg.Despawn); msg.U16(body.NetId); QueueAll();
                for (int i = 1; i < MaxPlayersHard; i++) if (peers[i] != null) { peers[i].bodySent.Remove(body.NetId); if (peers[i].body == body) peers[i].body = null; }
            }
            else
            {
                bodies.Remove(body.NetId); bodyTracks.Remove(body.NetId);
                if (body == myBody) { myBody = null; myBodyNetId = -1; }
            }
        }

        public void OnBodyOp(RagdollBody body, BodyOp op)
        {
            if (!Active || !IsHost || body == null) return;
            WriteOp(body.NetId, op);
            QueueAll();
        }

        /// <summary>Gives a ragdoll to a player (host: 0 = itself).</summary>
        public void AssignBody(int player, RagdollBody body)
        {
            if (body == null) return;
            body.OwnerPlayer = player;
            if (!Active || !IsHost) return;
            if (player > 0 && peers[player] != null) peers[player].body = body;
            BeginMsg(Msg.Owner); msg.U16(body.NetId); msg.U8(player); QueueAll();
        }

        public void RespawnPlayerBodies(List<int> owners)
        {
            if (!Active || !IsHost || owners == null) return;
            for (int k = 0; k < owners.Count; k++)
            {
                int id = owners[k];
                if (id < 0 || id >= MaxPlayersHard || !Infos[id].used) continue;
                var b = Sandbox.Boot.SpawnRagdoll(Sandbox.World.RagdollSpawn + new Vector3((id - 1.5f) * 2.2f, 0f, -1.5f), Sandbox.World.RagdollSpawnYaw);
                if (b == null) continue;
                AssignBody(id, b);
                if (id == 0) Sandbox.Boot.Possess(b);
            }
        }

        public void OnReset(bool full)
        {
            if (!Active || !IsHost) return;
            BeginMsg(Msg.Reset); msg.U8(full ? 1 : 0); QueueAll();
            if (full)
            {
                for (int i = 0; i < worldMove.Count; i++) worldMove[i] = Time.time;
            }
        }

        public void OnSettingsChanged()
        {
            if (!Active || !IsHost) return;
            if (!settingsDirty) settingsTimer = 0.25f;
            settingsDirty = true;
        }

        public void OnExplosion(Vector3 pos, float radius, float vis)
        {
            if (!Active || !IsHost) return;
            BeginMsg(Msg.Explosion); msg.Vec(pos); msg.F32(radius); msg.F32(vis); QueueAll();
        }

        public void OnPropSpawned(GameObject go, int kind)
        {
            if (!Active || !IsHost || go == null) return;
            var np = new NetProp { id = nextPropNetId++, go = go, rb = go.GetComponent<Rigidbody>(), kind = kind, lastMove = Time.time };
            if (nextPropNetId >= 0x7FFF) nextPropNetId = 1;
            netProps[np.id] = np;
            WritePropSpawn(np);
            QueueAll();
        }

        void OnKill(string who, string what, Color c)
        {
            if (!Active || !IsHost) return;
            BeginMsg(Msg.Kill); msg.Str(who, 60); msg.Str(what, 90); msg.Col(c); QueueAll();
        }

        void BroadcastChat(int from, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 120) text = text.Substring(0, 120);
            BeginMsg(Msg.Chat); msg.U8(from); msg.Str(text, 240); QueueAll();
            if (Sandbox.Hud != null) Sandbox.Hud.AddChat(Infos[from].name, text, PlayerColor(from));
        }

        /// <summary>Unreliable, rate-limited visual events (tracers, impacts, blood) so clients see the shots.</summary>
        public void BroadcastFx(NetFxKind kind, Vector3 a, Vector3 b)
        {
            if (!Active || !IsHost || fxBudget <= 0) return;
            fxBudget--;
            for (int i = 1; i < MaxPlayersHard; i++)
            {
                var p = peers[i];
                if (p == null || !p.synced) continue;
                if (kind == NetFxKind.Blood && (a - p.playerPos).sqrMagnitude > 40f * 40f) continue;
                Header(Pkt.Fx);
                tx.U8((int)kind);
                tx.QPos(a);
                tx.QPos(b);
                SendTo(p);
            }
        }
        #endregion

        #region Client
        void ClientReceive(Pkt type, float now)
        {
            var hp = peers[0];
            if (hp == null) return;
            hp.lastRecv = now;
            switch (type)
            {
                case Pkt.Welcome: if (Connecting) OnWelcome(); break;
                case Pkt.Reject:
                    {
                        string why = rx.RStr();
                        Status = "rejected: " + why;
                        Shutdown("rejected by host: " + why);
                        if (Sandbox.Hud != null) Sandbox.Hud.ShowJoinPanel(true);
                        break;
                    }
                case Pkt.Snapshot: if (!Connecting) ReadSnapshot(hp); break;
                case Pkt.Reliable:
                    ProcessAck(hp, rx.RU16());
                    if (!Connecting) ReadReliable(hp);
                    break;
                case Pkt.Fx: if (!Connecting) ReadFx(); break;
                case Pkt.Bye:
                    {
                        string why = rx.RStr();
                        Shutdown(string.IsNullOrEmpty(why) ? "host left" : "host: " + why);
                        if (Sandbox.Boot != null) Sandbox.Boot.ReturnToMenu();
                        break;
                    }
            }
        }

        void OnWelcome()
        {
            var b = Sandbox.Boot;
            LocalId = rx.RU8();
            int seed = rx.RI32();
            float hostTime = rx.RF32();
            var cs = b.character;
            cs.look = (BodyLook)rx.RU8();
            cs.height = rx.RF32(); cs.mass = rx.RF32();
            cs.skinColor = rx.RCol(); cs.shirtColor = rx.RCol(); cs.pantsColor = rx.RCol(); cs.shoesColor = rx.RCol(); cs.hairColor = rx.RCol(); cs.eyeColor = rx.RCol();
            int f = rx.RU8();
            cs.shirt = (f & 1) != 0; cs.pants = (f & 2) != 0; cs.shoes = (f & 4) != 0; cs.hair = (f & 8) != 0;
            rx.RU8();
            if (rx.Overflow) return;
            Connecting = false;
            serverOffset = hostTime - Time.unscaledTime;
            haveOffset = true;
            Infos[LocalId].used = true; Infos[LocalId].name = pendingName; Infos[LocalId].host = false;
            Infos[0].used = true; Infos[0].host = true; if (string.IsNullOrEmpty(Infos[0].name)) Infos[0].name = "Host";
            EnsureVisuals();
            // rebuild the world with the host's seed; every body becomes a puppet driven by snapshots
            BodyTemplate.ClearCache();
            b.RebuildWorld(seed, false);
            PrepareClientWorld();
            Status = "connected as " + pendingName;
            SandboxEvents.RaiseToast("Connected. V: your ragdoll / free camera, Enter: chat, Tab (hold): players", new Color(0.6f, 1f, 0.7f));
            b.StartPlaying();
        }

        void PrepareClientWorld()
        {
            var wp = Sandbox.World.Props;
            propXf.Clear(); propTracks.Clear();
            for (int i = 0; i < wp.Count; i++)
            {
                var w = wp[i];
                if (w.go == null) continue;
                MakeShell(w.go);
                propXf[i] = w.go.transform;
            }
        }

        static void MakeShell(GameObject go)
        {
            var rbs = go.GetComponentsInChildren<Rigidbody>();
            for (int k = 0; k < rbs.Length; k++) { rbs[k].isKinematic = true; rbs[k].interpolation = RigidbodyInterpolation.None; }
            var eps = go.GetComponentsInChildren<ExplosiveProp>();
            for (int k = 0; k < eps.Length; k++) eps[k].Remote = true;
        }

        void ClientTick(float now, float dt)
        {
            var ns = Sandbox.Boot.network;
            var hp = peers[0];
            if (Connecting)
            {
                helloTimer -= dt;
                if (helloTimer <= 0f)
                {
                    helloTimer = 0.5f;
                    Header(Pkt.Hello);
                    tx.U16(Protocol);
                    tx.Str(pendingName, 48);
                    SendTo(hp);
                }
                if (now - connectStart > 8f)
                {
                    Shutdown("no answer from the host (check the IP, the port and the host's firewall)");
                    if (Sandbox.Hud != null) Sandbox.Hud.ShowJoinPanel(true);
                }
                return;
            }
            if (now - hp.lastRecv > ns.timeout)
            {
                Shutdown("connection to the host timed out");
                if (Sandbox.Boot != null) Sandbox.Boot.ReturnToMenu();
                return;
            }
            // accumulate edges between input packets
            var lu = Sandbox.Tools.Local;
            if (lu.Input.down0) press0 = (press0 + 1) & 0xFF;
            if (lu.Input.down1) press1 = (press1 + 1) & 0xFF;
            scrollAcc += lu.Input.scroll;
            if (myBody != null && Sandbox.Possessed == myBody && myBody.Brain.Input.jump) { jumpCount = (jumpCount + 1) & 0xFF; var pi = myBody.Brain.Input; pi.jump = false; myBody.Brain.Input = pi; }
            if (myBody == null && myBodyNetId >= 0) { RagdollBody mb; if (bodies.TryGetValue(myBodyNetId, out mb)) SetMyBody(mb); }
            inputTimer -= dt;
            if (inputTimer <= 0f)
            {
                inputTimer += 1f / Mathf.Max(10, ns.inputRate);
                if (inputTimer < 0f) inputTimer = 0f;
                SendInput(hp);
            }
            relTimer -= dt;
            if (relTimer <= 0f) { relTimer = 0.05f; FlushReliable(hp, now); }
        }

        void SendInput(Peer hp)
        {
            inputSeq = (inputSeq + 1) & 0xFFFF;
            HeaderAck(Pkt.Input, hp);
            tx.U16(inputSeq);
            tx.F32(Time.unscaledTime);
            tx.U16(Mathf.Clamp(Mathf.RoundToInt(rttClient * 1000f), 0, 65535));
            bool possess = myBody != null && Sandbox.Possessed == myBody;
            var pin = possess ? myBody.Brain.Input : new PossessInput();
            var lu = Sandbox.Tools.Local;
            var inp = lu.Input;
            tx.U8((possess ? 1 : 0) | (pin.run ? 2 : 0) | (pin.crouch ? 4 : 0) | (pin.reachL ? 8 : 0) | (pin.reachR ? 16 : 0));
            tx.U8((inp.held0 ? 1 : 0) | (inp.held1 ? 2 : 0) | (inp.shift ? 4 : 0) | (inp.ctrl ? 8 : 0) | (lu.LaserFiring ? 16 : 0));
            tx.U8(press0); tx.U8(press1); tx.U8(jumpCount);
            tx.I16(Mathf.Clamp(Mathf.RoundToInt(scrollAcc * 100f), -30000, 30000));
            scrollAcc = 0f;
            tx.U8((int)lu.Current); tx.U8(lu.ExplosiveIndex); tx.U8(lu.PropIndex); tx.U8(lu.GrabMode);
            var pl = Sandbox.Player;
            tx.QPos(pl != null ? pl.transform.position : Vector3.zero);
            tx.U16(Mathf.RoundToInt(Mathf.Repeat(pl != null ? pl.Yaw : 0f, 360f) / 360f * 65535f));
            tx.I16(Mathf.RoundToInt((pl != null ? pl.Pitch : 0f) * 100f));
            tx.QPos(inp.aim.origin);
            tx.Dir(inp.aim.direction);
            tx.U16(Mathf.RoundToInt(Mathf.Repeat(pin.camYaw, 360f) / 360f * 65535f));
            tx.I16(Mathf.RoundToInt(Mathf.Clamp(pin.move.x, -1f, 1f) * 1000f));
            tx.I16(Mathf.RoundToInt(Mathf.Clamp(pin.move.z, -1f, 1f) * 1000f));
            SendTo(hp);
        }

        void ReadSnapshot(Peer hp)
        {
            ProcessAck(hp, rx.RU16());
            rx.RU16(); // snapshot sequence
            rx.RU8();  // part
            float st = rx.RF32();
            float echo = rx.RF32();
            float rtt = Time.unscaledTime - echo;
            if (rtt > 0f && rtt < 5f) rttClient = Mathf.Lerp(rttClient, rtt, 0.1f);
            hp.rtt = rttClient;
            float off = st - Time.unscaledTime;
            if (!haveOffset) { serverOffset = off; haveOffset = true; }
            else serverOffset = off > serverOffset ? Mathf.Lerp(serverOffset, off, 0.2f) : Mathf.Lerp(serverOffset, off, 0.02f);
            // players
            int n = rx.RU8();
            for (int k = 0; k < n; k++)
            {
                int id = rx.RU8();
                int flags = rx.RU8();
                Vector3 pos = rx.RQPos();
                float yaw = rx.RU16() * (360f / 65535f);
                float pitch = rx.RI16() / 100f;
                int tool = rx.RU8();
                Vector3 le = (flags & 2) != 0 ? rx.RQPos() : Vector3.zero;
                if (id >= MaxPlayersHard) continue;
                var a = GetAvatar(id);
                a.targetPos = pos; a.targetYaw = yaw; a.pitch = pitch; a.tool = tool;
                a.possess = (flags & 1) != 0; a.laser = (flags & 2) != 0; a.driving = (flags & 4) != 0; a.laserEnd = le;
                if (!a.visible) { a.pos = pos; a.yaw = yaw; a.visible = true; }
            }
            // bodies
            n = rx.RU8();
            for (int k = 0; k < n; k++)
            {
                int netId = rx.RU16();
                int state = rx.RU8();
                float blood = rx.RU8() / 255f, consc = rx.RU8() / 255f, pain = rx.RU8() / 255f;
                int vf = rx.RU8();
                int segs = rx.RU8();
                RagdollBody b;
                bodies.TryGetValue(netId, out b);
                PoseTrack track = null;
                if (b != null && !bodyTracks.TryGetValue(netId, out track)) { track = new PoseTrack(64); bodyTracks[netId] = track; }
                int slot = track != null ? track.Begin(st, segs) : -1;
                for (int s = 0; s < segs; s++)
                {
                    Vector3 p = rx.RQPos();
                    Quaternion q = rx.RQuat();
                    if (slot >= 0) track.Set(slot, s, p, q);
                }
                if (b != null && !b.Destroyed && b.Vitals != null)
                {
                    var v = b.Vitals;
                    v.BloodVolume = blood * v.MaxBlood;
                    v.Consciousness = consc;
                    v.Pain = pain;
                    v.Dead = (vf & 1) != 0;
                    v.Unconscious = (vf & 2) != 0;
                    if (state >= 0 && state <= (int)MotorState.Dead) b.Brain.State = (MotorState)state;
                }
            }
            // props
            n = rx.RU8();
            for (int k = 0; k < n; k++)
            {
                int key = rx.RU16();
                Vector3 p = rx.RQPos();
                Quaternion q = rx.RQuat();
                if (!propXf.ContainsKey(key)) continue;
                PoseTrack track;
                if (!propTracks.TryGetValue(key, out track)) { track = new PoseTrack(1); propTracks[key] = track; }
                int slot = track.Begin(st, 1);
                track.Set(slot, 0, p, q);
            }
        }

        void ApplyInterpolation()
        {
            var ns = Sandbox.Boot.network;
            float renderTime = Time.unscaledTime + serverOffset - ns.interpolationDelay;
            foreach (var kv in bodyTracks)
            {
                RagdollBody b;
                if (!bodies.TryGetValue(kv.Key, out b) || b == null || b.Destroyed) continue;
                var track = kv.Value;
                int n = b.AllPieces.Count;
                for (int i = 0; i < n; i++)
                {
                    var part = b.AllPieces[i];
                    if (part == null) continue;
                    Vector3 p; Quaternion q;
                    if (track.Sample(renderTime, i, ns.maxExtrapolation, out p, out q)) part.transform.SetPositionAndRotation(p, q);
                }
                b.ComputeCenterOfMass();
            }
            foreach (var kv in propTracks)
            {
                Transform t;
                if (!propXf.TryGetValue(kv.Key, out t) || t == null) continue;
                Vector3 p; Quaternion q;
                if (kv.Value.Sample(renderTime, 0, ns.maxExtrapolation, out p, out q)) t.SetPositionAndRotation(p, q);
            }
        }

        void ClientMessage()
        {
            var b = Sandbox.Boot;
            var t = (Msg)rx.RU8();
            switch (t)
            {
                case Msg.SpawnBody:
                    {
                        int netId = rx.RU16(); int index = rx.RU8(); int owner = rx.RU8();
                        Vector3 pos = rx.RVec(); float yaw = rx.RF32();
                        RagdollBody old;
                        if (bodies.TryGetValue(netId, out old) && old != null) b.RemoveRagdoll(old);
                        var body = b.SpawnPuppet(netId, index, pos, yaw);
                        if (body == null) break;
                        body.OwnerPlayer = owner == 255 ? -1 : owner;
                        bodies[netId] = body;
                        if (body.OwnerPlayer == LocalId) { myBodyNetId = netId; SetMyBody(body); }
                        break;
                    }
                case Msg.Despawn:
                    {
                        int netId = rx.RU16();
                        RagdollBody body;
                        if (bodies.TryGetValue(netId, out body))
                        {
                            bodies.Remove(netId); bodyTracks.Remove(netId);
                            if (body == myBody) { myBody = null; myBodyNetId = -1; if (Sandbox.Possessed == body) { Sandbox.Possessed = null; Sandbox.Cam.SetMode(CameraMode.FirstPerson); } }
                            if (body != null) b.RemoveRagdoll(body);
                        }
                        break;
                    }
                case Msg.BodyOp:
                    {
                        int netId = rx.RU16();
                        var op = new BodyOp();
                        op.type = (BodyOpType)rx.RU8();
                        op.seg = rx.RU8();
                        op.seg2 = rx.RU8() - 1;
                        op.a = rx.RVec(); op.b = rx.RVec();
                        op.f0 = rx.RF32(); op.f1 = rx.RF32(); op.f2 = rx.RF32();
                        op.flags = rx.RU8();
                        RagdollBody body;
                        if (!rx.Overflow && bodies.TryGetValue(netId, out body) && body != null && body.Skin != null) body.Skin.ApplyRemoteOp(op);
                        break;
                    }
                case Msg.Owner:
                    {
                        int netId = rx.RU16(); int owner = rx.RU8();
                        RagdollBody body;
                        if (!bodies.TryGetValue(netId, out body) || body == null) break;
                        body.OwnerPlayer = owner == 255 ? -1 : owner;
                        if (body.OwnerPlayer == LocalId) { myBodyNetId = netId; SetMyBody(body); }
                        else if (body == myBody) { myBody = null; myBodyNetId = -1; }
                        break;
                    }
                case Msg.Explosion:
                    {
                        Vector3 pos = rx.RVec(); float radius = rx.RF32(); float vis = rx.RF32();
                        Sandbox.Explosions.Explode(pos, radius, 0f, 0f, vis, 0, null);
                        break;
                    }
                case Msg.Settings:
                    {
                        b.dismember.gore = (GoreLevel)rx.RU8();
                        TimeControl.SlowMotion = rx.RU8() != 0;
                        b.physics.gravity = (GravityMode)rx.RU8();
                        b.blood.bloodAmount = rx.RF32();
                        b.general.reactions = rx.RU8() != 0;
                        b.physics.slowMotionScale = rx.RF32();
                        break;
                    }
                case Msg.Chat:
                    {
                        int from = rx.RU8(); string text = rx.RStr();
                        if (Sandbox.Hud != null && from < MaxPlayersHard) Sandbox.Hud.AddChat(Infos[from].name, text, PlayerColor(from));
                        break;
                    }
                case Msg.PlayerJoin:
                    {
                        int id = rx.RU8(); string name = rx.RStr();
                        if (id >= MaxPlayersHard) break;
                        bool known = Infos[id].used;
                        Infos[id].used = true; Infos[id].name = name; Infos[id].host = id == 0;
                        if (!known && id != 0 && Sandbox.Hud != null) Sandbox.Hud.AddChat(null, name + " joined", new Color(0.7f, 0.9f, 1f));
                        break;
                    }
                case Msg.PlayerLeave:
                    {
                        int id = rx.RU8(); string why = rx.RStr();
                        if (id >= MaxPlayersHard) break;
                        if (Sandbox.Hud != null) Sandbox.Hud.AddChat(null, Infos[id].name + " " + why, new Color(1f, 0.8f, 0.6f));
                        Infos[id].used = false;
                        HideAvatar(id);
                        break;
                    }
                case Msg.Reset:
                    {
                        bool full = rx.RU8() != 0;
                        if (full)
                        {
                            Sandbox.Decals.Clear();
                            Sandbox.Blood.Clear();
                            Sandbox.World.ResetProps();
                            PrepareClientWorld();
                        }
                        SandboxEvents.RaiseToast(full ? "Host reset the scene" : "Host reset the ragdolls", Color.white);
                        break;
                    }
                case Msg.PropSpawn:
                    {
                        int id = rx.RU16(); int kind = rx.RU8(); Vector3 pos = rx.RVec(); Quaternion rot = rx.RQuat();
                        SpawnShell(id, kind, pos, rot);
                        break;
                    }
                case Msg.PropDespawn:
                    {
                        int id = rx.RU16();
                        int key = 0x8000 | id;
                        Transform tr;
                        if (propXf.TryGetValue(key, out tr) && tr != null)
                        {
                            var car = Sandbox.Vehicles.Find(id);
                            if (car != null) { Sandbox.Vehicles.Cars.Remove(car); car.Destroy(); }
                            else UnityEngine.Object.Destroy(tr.gameObject);
                        }
                        propXf.Remove(key); propTracks.Remove(key);
                        break;
                    }
                case Msg.WorldProp:
                    {
                        int index = rx.RU16(); bool alive = rx.RU8() != 0;
                        var wp = Sandbox.World.Props;
                        if (index < wp.Count && wp[index].go != null) wp[index].go.SetActive(alive);
                        break;
                    }
                case Msg.Kill:
                    {
                        string who = rx.RStr(); string what = rx.RStr(); Color c = rx.RCol();
                        SandboxEvents.NetDelivery = true;
                        SandboxEvents.RaiseKill(who, what, c);
                        SandboxEvents.NetDelivery = false;
                        break;
                    }
                case Msg.ToolChange:
                    {
                        int id = rx.RU8(); int tool = rx.RU8();
                        if (id < MaxPlayersHard) GetAvatar(id).tool = tool;
                        break;
                    }
            }
        }

        void SpawnShell(int id, int kind, Vector3 pos, Quaternion rot)
        {
            GameObject go = null;
            if (kind == CarKind)
            {
                var car = Sandbox.Vehicles.SpawnRemote(id, pos, rot);
                if (car != null) go = car.go;
            }
            else if (kind >= ExplosiveKindBase)
            {
                var ep = Sandbox.Explosions.Spawn((ExplosiveKind)(kind - ExplosiveKindBase), pos, rot, Vector3.zero);
                if (ep != null) go = ep.gameObject;
            }
            else go = Sandbox.Tools.Local.MakeProp(kind, pos, rot);
            if (go == null) return;
            MakeShell(go);
            int key = 0x8000 | id;
            propXf[key] = go.transform;
            Sandbox.Tools.TrackSpawned(go);
        }

        void SetMyBody(RagdollBody body)
        {
            if (body == null || myBody == body) return;
            myBody = body;
            // start possessing our own ragdoll
            Sandbox.Possessed = body;
            body.SetPossessed(true);
            Sandbox.Cam.SetMode(CameraMode.Possess);
        }

        void ReadFx()
        {
            var kind = (NetFxKind)rx.RU8();
            Vector3 a = rx.RQPos(), b = rx.RQPos();
            if (rx.Overflow || Sandbox.Fx == null) return;
            switch (kind)
            {
                case NetFxKind.Gunshot:
                case NetFxKind.Shotgun:
                    Sandbox.Fx.Tracer(a, b, new Color(1f, 0.85f, 0.55f, 0.8f), 0.012f, 0.05f);
                    Sandbox.Fx.MuzzleFlash(a, (b - a).normalized, kind == NetFxKind.Shotgun ? 1.6f : 1f);
                    Sandbox.Audio.Play(kind == NetFxKind.Shotgun ? Sfx.Shotgun : Sfx.Gunshot, a, 0.9f, 1f, 3f);
                    break;
                case NetFxKind.Impact: Sandbox.Fx.Impact(a, (b - a).sqrMagnitude > 0f ? b : Vector3.up, false, false); break;
                case NetFxKind.Blood: Sandbox.Fx.EmitBloodSpray(a, MathUtil.SafeNormal(b, Vector3.up), b.magnitude, 30f, 8, 0.02f, Sandbox.Boot.blood.arterial, Vector3.zero); break;
            }
        }

        public void RequestSpawnRagdoll(Vector3 pos, float yaw)
        {
            if (!IsClient || Connecting) return;
            BeginMsg(Msg.SpawnRequest); msg.Vec(pos); msg.F32(yaw); QueueMsg(peers[0]);
        }

        public void RequestReset(bool full)
        {
            if (!IsClient || Connecting) return;
            BeginMsg(Msg.ResetRequest); msg.U8(full ? 1 : 0); QueueMsg(peers[0]);
        }

        public void SendToolAction(ToolId tool, int sub, bool secondary, Ray ray)
        {
            if (!IsClient || Connecting) return;
            BeginMsg(Msg.ToolAction); msg.U8((int)tool); msg.U8(sub); msg.U8(secondary ? 1 : 0); msg.Vec(ray.origin); msg.Vec(ray.direction); QueueMsg(peers[0]);
        }

        public void SendChat(string text)
        {
            if (!Active || string.IsNullOrEmpty(text)) return;
            if (IsHost) { BroadcastChat(0, text); return; }
            if (Connecting) return;
            if (text.Length > 120) text = text.Substring(0, 120);
            BeginMsg(Msg.ChatSend); msg.Str(text, 240); QueueMsg(peers[0]);
        }

        /// <summary>V on a client: toggle between the own ragdoll and the free first-person camera (tools).</summary>
        public void TogglePossessRemote()
        {
            if (!IsClient) return;
            if (Sandbox.Possessed != null)
            {
                var b = Sandbox.Possessed;
                b.SetPossessed(false);
                Sandbox.Possessed = null;
                Sandbox.Cam.SetMode(CameraMode.FirstPerson);
                if (Sandbox.Player != null) Sandbox.Player.Teleport(b.CenterOfMass - Sandbox.MainCamera.transform.forward * 2f + Vector3.up * 0.2f);
                SandboxEvents.RaiseToast("Free camera (tools). V to return to your ragdoll", Color.white);
            }
            else if (myBody != null && !myBody.Destroyed)
            {
                var b = myBody;
                myBody = null;
                SetMyBody(b);
            }
            else SandboxEvents.RaiseToast("You have no ragdoll right now", new Color(1f, 0.7f, 0.6f));
        }
        #endregion

        #region Avatars and name tags
        public static Color PlayerColor(int id)
        {
            switch (id)
            {
                case 0: return new Color(1f, 0.82f, 0.35f);
                case 1: return new Color(0.45f, 0.8f, 1f);
                case 2: return new Color(0.6f, 1f, 0.55f);
                default: return new Color(1f, 0.55f, 0.85f);
            }
        }

        void EnsureVisuals()
        {
            if (tagCanvas != null) return;
            root = Sandbox.Root;
            tagCanvas = new GameObject("Name Tags");
            tagCanvas.transform.SetParent(root, false);
            var canvas = tagCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;
            var scaler = tagCanvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            Font font = Compat.BuiltinFont();
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                var go = new GameObject("Tag" + i);
                go.transform.SetParent(tagCanvas.transform, false);
                var t = go.AddComponent<Text>();
                t.font = font; t.fontSize = 22; t.alignment = TextAnchor.LowerCenter; t.raycastTarget = false;
                t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
                t.rectTransform.sizeDelta = new Vector2(300f, 40f);
                var o = go.AddComponent<Outline>(); o.effectColor = new Color(0f, 0f, 0f, 0.8f);
                go.SetActive(false);
                tags[i] = t;
            }
        }

        Avatar GetAvatar(int id)
        {
            var a = avatars[id];
            if (a != null) return a;
            a = new Avatar();
            avatars[id] = a;
            a.go = new GameObject("Player Avatar " + id);
            a.go.transform.SetParent(Sandbox.Root, false);
            var mat = MaterialFactory.Lit("Avatar" + id, PlayerColor(id), null, 0.45f, 0f);
            var body = new GameObject("Body");
            body.transform.SetParent(a.go.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Capsule(0.3f, 1.7f, 12);
            body.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var visor = new GameObject("Visor");
            visor.transform.SetParent(a.go.transform, false);
            visor.transform.localPosition = new Vector3(0f, 1.55f, 0.24f);
            visor.AddComponent<MeshFilter>().sharedMesh = MeshFactory.BoxCached(new Vector3(0.36f, 0.1f, 0.1f), 0f);
            visor.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.DarkMetal;
            var bgo = new GameObject("Beam");
            bgo.transform.SetParent(a.go.transform, false);
            a.beam = bgo.AddComponent<LineRenderer>();
            a.beam.sharedMaterial = MaterialFactory.LineAdd;
            a.beam.positionCount = 2;
            a.beam.startWidth = 0.016f; a.beam.endWidth = 0.009f;
            a.beam.startColor = new Color(1f, 0.25f, 0.15f, 1f); a.beam.endColor = new Color(1f, 0.6f, 0.4f, 1f);
            a.beam.useWorldSpace = true;
            a.beam.enabled = false;
            a.go.SetActive(false);
            return a;
        }

        void HideAvatar(int id)
        {
            var a = avatars[id];
            if (a != null) { a.visible = false; if (a.go != null) a.go.SetActive(false); }
            if (tags[id] != null) tags[id].gameObject.SetActive(false);
        }

        void UpdateAvatars(float dt)
        {
            // host: remote players' positions come from their input packets
            if (IsHost)
            {
                for (int i = 1; i < MaxPlayersHard; i++)
                {
                    var p = peers[i];
                    if (p == null || p.inputSeq < 0) continue;
                    var a = GetAvatar(i);
                    a.targetPos = p.playerPos; a.targetYaw = p.yaw; a.pitch = p.pitch; a.possess = p.possess; a.driving = p.driving; a.tool = p.tool;
                    var tu = Sandbox.Tools.Remote[i];
                    a.laser = tu != null && tu.LaserFiring; a.laserEnd = tu != null ? tu.LaserEnd : Vector3.zero;
                    if (!a.visible) { a.pos = a.targetPos; a.yaw = a.targetYaw; a.visible = true; }
                }
            }
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                var a = avatars[i];
                if (a == null || !a.visible || !Infos[i].used || i == LocalId) { if (a != null && a.go != null && a.go.activeSelf) a.go.SetActive(false); continue; }
                bool show = !a.possess && !a.driving;
                if (a.go.activeSelf != show) a.go.SetActive(show);
                a.pos = MathUtil.Damp(a.pos, a.targetPos, 14f, dt);
                a.yaw = Mathf.LerpAngle(a.yaw, a.targetYaw, Mathf.Min(1f, dt * 14f));
                a.go.transform.SetPositionAndRotation(a.pos, Quaternion.Euler(0f, a.yaw, 0f));
                if (a.laser && show)
                {
                    a.beam.enabled = true;
                    Vector3 eye = a.pos + Vector3.up * 1.6f + Quaternion.Euler(a.pitch, a.yaw, 0f) * new Vector3(0.2f, -0.15f, 0.5f);
                    a.beam.SetPosition(0, eye);
                    a.beam.SetPosition(1, a.laserEnd);
                }
                else if (a.beam.enabled) a.beam.enabled = false;
            }
        }

        RagdollBody OwnedBody(int id)
        {
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) { var r = Sandbox.Ragdolls[i]; if (r != null && r.OwnerPlayer == id && !r.Destroyed) return r; }
            return null;
        }

        void UpdateNameTags()
        {
            if (tagCanvas == null) return;
            var cam = Sandbox.MainCamera;
            for (int i = 0; i < MaxPlayersHard; i++)
            {
                var t = tags[i];
                if (t == null) continue;
                bool show = Infos[i].used && i != LocalId && cam != null;
                Vector3 world = Vector3.zero;
                if (show)
                {
                    var a = avatars[i];
                    var body = OwnedBody(i);
                    bool possess = IsHost && i == 0 ? Sandbox.Possessed != null : a != null && a.possess;
                    if (possess && body != null) world = (body.Head != null && body.Head.attached ? body.Head.transform.position : body.CenterOfMass) + Vector3.up * 0.35f;
                    else if (a != null && a.visible) world = a.pos + Vector3.up * 2.1f;
                    else if (IsHost && i == 0 && Sandbox.Player != null) world = Sandbox.Player.transform.position + Vector3.up * 2.1f;
                    else show = false;
                }
                Vector3 sp = show ? cam.WorldToScreenPoint(world) : Vector3.zero;
                if (show && (sp.z < 0.3f || sp.z > 80f)) show = false;
                if (t.gameObject.activeSelf != show) t.gameObject.SetActive(show);
                if (!show) continue;
                if (t.text != Infos[i].name) { t.text = Infos[i].name; t.color = PlayerColor(i); }
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)tagCanvas.transform, sp, null, out local);
                t.rectTransform.anchoredPosition = local;
                float k = Mathf.Clamp(14f / Mathf.Max(1f, sp.z), 0.6f, 1.2f);
                t.rectTransform.localScale = new Vector3(k, k, 1f);
            }
        }
        #endregion

        public string DebugLine()
        {
            int peersN = 0;
            for (int i = 0; i < MaxPlayersHard; i++) if (Infos[i].used) peersN++;
            return (IsHost ? "HOST " : "CLIENT ") + "players " + peersN + "  out " + (bytesOutLast / 1024f).ToString("0.0") + " KB/s  in " + (bytesInLast / 1024f).ToString("0.0") +
                   " KB/s" + (IsClient ? "  rtt " + Mathf.RoundToInt(rttClient * 1000f) + " ms" : "");
        }

        public int PlayerCount { get { int n = 0; for (int i = 0; i < MaxPlayersHard; i++) if (Infos[i].used) n++; return n; } }
    }
    #endregion
}
