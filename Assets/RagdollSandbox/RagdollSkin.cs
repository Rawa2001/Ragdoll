// Ragdoll Sandbox - BodySkin: the single continuous skinned body of one ragdoll.
//  * One SkinnedMeshRenderer per connected piece (one piece until something is cut off). The hidden physics
//    segments are its bones; every segment derived from a bone shares that bone's bind pose.
//  * Mesh operations (slices, holes, dents, squash, stretch, fracture re-weighting) run as jobs on a worker thread,
//    one at a time per body, and are applied on the main thread when finished (never blocking a frame).
//  * BodyCanvas: one paint atlas per body (skin + clothes + face + damage). Stamps are evaluated in 3D through the
//    template's position map; uploads are dirty-rectangle GPU copies, throttled and budgeted per frame.
using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region Mesh data
    /// <summary>CPU copy of a body piece mesh in bind space (all lists are owned by one job at a time).</summary>
    public class MeshData
    {
        public List<Vector3> pos, nrm, rest;
        public List<Vector4> tan;
        public List<Vector2> uv;
        public List<BoneWeight> bw;
        public List<int> weld;
        public readonly List<int>[] sub = new List<int>[3];   // 0 skin, 1 cut caps, 2 hole interiors
        public int nextWeld;
        public bool[] bones = new bool[0];

        public MeshData(int cap)
        {
            pos = new List<Vector3>(cap); nrm = new List<Vector3>(cap); rest = new List<Vector3>(cap); tan = new List<Vector4>(cap);
            uv = new List<Vector2>(cap); bw = new List<BoneWeight>(cap); weld = new List<int>(cap);
            for (int i = 0; i < 3; i++) sub[i] = new List<int>(cap * 2);
        }

        public int Count { get { return pos.Count; } }

        public int Add(Vector3 p, Vector3 n, Vector4 t, Vector2 u, BoneWeight w, int weldId, Vector3 restP)
        {
            pos.Add(p); nrm.Add(n); tan.Add(t); uv.Add(u); bw.Add(w); weld.Add(weldId); rest.Add(restP);
            return pos.Count - 1;
        }

        public int CopyFrom(MeshData src, int i)
        {
            return Add(src.pos[i], src.nrm[i], src.tan[i], src.uv[i], src.bw[i], src.weld[i], src.rest[i]);
        }

        public bool HasBone(int b) { return b >= 0 && b < bones.Length && bones[b]; }

        public void SetBone(int b, bool on)
        {
            if (b < 0) return;
            if (b >= bones.Length) Array.Resize(ref bones, b + 8);
            bones[b] = on;
        }

        public int TriangleCount { get { return (sub[0].Count + sub[1].Count + sub[2].Count) / 3; } }

        public static int Dominant(BoneWeight w)
        {
            int b = w.boneIndex0; float v = w.weight0;
            if (w.weight1 > v) { v = w.weight1; b = w.boneIndex1; }
            if (w.weight2 > v) { v = w.weight2; b = w.boneIndex2; }
            if (w.weight3 > v) { b = w.boneIndex3; }
            return b;
        }
    }

    public class MeshPiece
    {
        public GameObject go;
        public SkinnedMeshRenderer smr;
        public Mesh mesh;
        public MeshData data;
        public BodyPart root;
    }

    /// <summary>A mesh operation: Run() on a worker thread (pure data), Apply() on the main thread.</summary>
    public abstract class MeshJob
    {
        public BodySkin skin;
        public MeshPiece piece;
        public volatile bool done;
        public Exception error;
        /// <summary>Main thread, right before the job is dispatched (reads scene state the job needs).</summary>
        public virtual void Prepare() { }
        public abstract void Run();
        public abstract void Apply();

        public void Execute()
        {
            try { Run(); }
            catch (Exception e) { error = e; }
            done = true;
        }
    }
    #endregion

    #region BodySkin
    public class BodySkin
    {
        readonly RagdollBody body;
        public BodyTemplate Template;
        public BodyCanvas Canvas;
        public readonly List<MeshPiece> Pieces = new List<MeshPiece>(4);
        public Material SkinMat, CapMat, HoleMat;
        Material[] mats;
        Transform[] bones = new Transform[0];
        Matrix4x4[] bindposes = new Matrix4x4[0];
        readonly Queue<MeshJob> queue = new Queue<MeshJob>(8);
        MeshJob running;
        float highlight = -1f;
        bool xray;
        bool disposed;
        int paintsThisFrame, paintFrame;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        public BodySkin(RagdollBody b) { body = b; }

        public int JobsPending { get { return queue.Count + (running != null ? 1 : 0); } }
        public bool Busy { get { return running != null || queue.Count > 0; } }
        public Transform[] Bones { get { return bones; } }
        public Matrix4x4[] Bindposes { get { return bindposes; } }

        public void Build()
        {
            var cs = body.Look;
            int atlas = Mathf.ClosestPowerOfTwo(Mathf.Clamp(cs.bodyTextureSize, 256, 2048));
            Template = BodyTemplate.Get(body.Style, atlas);
            Canvas = new BodyCanvas(Template, BodyPaintCache.Get(Template, cs), body.Style, body.Index);
            bool clay = body.Style == BodyLook.Clay;
            SkinMat = MaterialFactory.LitEmissive("Body skin", Color.white, Canvas.Texture, clay ? cs.claySmoothness : cs.skinSmoothness);
            MaterialFactory.SetNormalMap(SkinMat, Template.normalMap, clay ? 1f : 0.45f);
            CapMat = MaterialFactory.Lit("Cut surface", Color.white, Template.capTexture, clay ? 0.35f : 0.6f, 0f);
            HoleMat = MaterialFactory.Lit("Wound interior", Color.white, Template.holeTexture, clay ? 0.25f : 0.7f, 0f);
            mats = new[] { SkinMat, CapMat, HoleMat };
            RebuildBones();
            // main piece: the whole template scaled to this body
            var t = Template;
            int n = t.pos.Length;
            var d = new MeshData(n + 512);
            float s = body.Scale;
            for (int i = 0; i < n; i++) d.Add(t.pos[i] * s, t.nrm[i], t.tan[i], t.uv[i], t.bw[i], t.weld[i], t.pos[i] * s);
            d.sub[0].AddRange(t.tris);
            d.nextWeld = t.weldCount;
            for (int b = 0; b < body.AllPieces.Count; b++) d.SetBone(b, true);
            var piece = CreatePiece(d, body.Pelvis);
            Upload(piece, true);
        }

        MeshPiece CreatePiece(MeshData d, BodyPart root)
        {
            var p = new MeshPiece();
            p.data = d;
            p.root = root;
            p.go = new GameObject(Pieces.Count == 0 ? "Body" : "Body piece");
            p.go.transform.SetParent(body.transform, false);
            p.go.layer = Layers.Ragdoll;
            p.smr = p.go.AddComponent<SkinnedMeshRenderer>();
            p.mesh = new Mesh();
            p.mesh.name = "Body mesh";
            p.mesh.MarkDynamic();
            p.smr.sharedMesh = p.mesh;
            p.smr.sharedMaterials = mats;
            p.smr.bones = bones;
            p.smr.rootBone = root != null ? root.transform : body.transform;
            p.smr.quality = SkinQuality.Bone4;
            p.smr.updateWhenOffscreen = false;
            p.smr.skinnedMotionVectors = false;
            p.smr.shadowCastingMode = ShadowCastingMode.On;
            Pieces.Add(p);
            return p;
        }

        void RebuildBones()
        {
            int n = body.AllPieces.Count;
            bones = new Transform[n];
            bindposes = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                var seg = body.AllPieces[i];
                bones[i] = seg != null ? seg.transform : body.transform;
                Vector3 o = seg != null ? PartInfo.Origin[(int)seg.id] * body.Scale : Vector3.zero;
                bindposes[i] = Matrix4x4.Translate(-o);
            }
        }

        /// <summary>A new segment exists (fracture/cut): every piece mesh gets the longer bone and bind pose arrays.</summary>
        public void OnSegmentAdded(BodyPart seg)
        {
            RebuildBones();
            for (int i = 0; i < Pieces.Count; i++)
            {
                var p = Pieces[i];
                if (p.mesh == null) continue;
                p.mesh.bindposes = bindposes;
                p.smr.bones = bones;
            }
        }

        public MeshPiece PieceOf(BodyPart seg)
        {
            if (seg == null) return null;
            for (int i = 0; i < Pieces.Count; i++) if (Pieces[i].data != null && Pieces[i].data.HasBone(seg.segIndex)) return Pieces[i];
            return null;
        }

        /// <summary>Uploads a piece's CPU data to its Mesh (event time only).</summary>
        public void Upload(MeshPiece p, bool full)
        {
            var d = p.data;
            var m = p.mesh;
            if (full)
            {
                m.Clear();
                m.indexFormat = d.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                m.SetVertices(d.pos);
                m.SetNormals(d.nrm);
                m.SetTangents(d.tan);
                m.SetUVs(0, d.uv);
                m.boneWeights = d.bw.ToArray();
                m.bindposes = bindposes;
                m.subMeshCount = 3;
                for (int k = 0; k < 3; k++) m.SetTriangles(d.sub[k], k, false);
            }
            else
            {
                m.SetVertices(d.pos);
                m.SetNormals(d.nrm);
            }
            UpdateBounds(p);
        }

        void UpdateBounds(MeshPiece p)
        {
            var d = p.data;
            if (d.Count == 0) return;
            Vector3 o = p.root != null ? PartInfo.Origin[(int)p.root.id] * body.Scale : Vector3.zero;
            Vector3 mn = d.pos[0], mx = d.pos[0];
            for (int i = 1; i < d.Count; i++) { mn = Vector3.Min(mn, d.pos[i]); mx = Vector3.Max(mx, d.pos[i]); }
            var b = new Bounds((mn + mx) * 0.5f - o, mx - mn);
            b.Expand(0.6f * body.Scale);
            p.smr.localBounds = b;
            p.mesh.bounds = b;
        }

        public void Enqueue(MeshJob j)
        {
            if (disposed || j == null) return;
            j.skin = this;
            queue.Enqueue(j);
            if (running == null) StartNext();
        }

        void StartNext()
        {
            if (queue.Count == 0) { running = null; return; }
            running = queue.Dequeue();
            try { running.Prepare(); }
            catch (Exception e) { Debug.LogWarning("[RagdollSandbox] Mesh job prepare failed: " + e.Message); running.error = e; running.done = true; return; }
            if (Sandbox.Boot != null && Sandbox.Boot.graphics.threadedMeshJobs && SystemInfo.processorCount > 1 && Application.platform != RuntimePlatform.WebGLPlayer)
                ThreadPool.QueueUserWorkItem(RunJob, running);
            else running.Execute();
        }

        static void RunJob(object o) { ((MeshJob)o).Execute(); }

        public void Tick(float dt)
        {
            if (running != null && running.done)
            {
                var j = running;
                running = null;
                if (j.error != null) Debug.LogWarning("[RagdollSandbox] Mesh job failed: " + j.error.Message);
                else if (!disposed)
                {
                    try { j.Apply(); }
                    catch (Exception e) { Debug.LogWarning("[RagdollSandbox] Mesh job apply failed: " + e); }
                }
                StartNext();
            }
            if (Canvas != null) Canvas.Tick(Time.time);
        }

        public void LateTick()
        {
            if (Canvas == null || !Canvas.Dirty) return;
            float dist = 5f;
            if (Sandbox.MainCamera != null && body.Pelvis != null) dist = (Sandbox.MainCamera.transform.position - body.Pelvis.rb.position).magnitude;
            if (Canvas.ReadyToUpload(Time.unscaledTime, dist) && CanvasUploader.TryConsume()) Canvas.Upload(Time.unscaledTime);
        }

        public void SetXRay(bool on)
        {
            xray = on;
            if (SkinMat == null) return;
            if (on) MaterialFactory.MakeTransparent(SkinMat, 0.22f);
            else MaterialFactory.MakeOpaque(SkinMat);
            for (int i = 0; i < Pieces.Count; i++) if (Pieces[i].smr != null) Pieces[i].smr.shadowCastingMode = on ? ShadowCastingMode.Off : ShadowCastingMode.On;
        }

        public void SetHighlight(float amount)
        {
            if (Mathf.Abs(amount - highlight) < 0.01f || SkinMat == null) return;
            highlight = amount;
            SkinMat.SetColor(EmissionId, new Color(0.25f, 0.18f, 0.08f) * amount);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int i = 0; i < Pieces.Count; i++)
            {
                if (Pieces[i].mesh != null) UnityEngine.Object.Destroy(Pieces[i].mesh);
                if (Pieces[i].go != null) UnityEngine.Object.Destroy(Pieces[i].go);
            }
            Pieces.Clear();
            if (SkinMat != null) UnityEngine.Object.Destroy(SkinMat);
            if (CapMat != null) UnityEngine.Object.Destroy(CapMat);
            if (HoleMat != null) UnityEngine.Object.Destroy(HoleMat);
            if (Canvas != null) Canvas.Dispose();
        }

        #region Painting
        /// <summary>Paints a stamp at a world point on a segment (converted to the template's reference space).</summary>
        public void Paint(BodyPart part, Vector3 world, StampKind kind, float radius, float intensity, Color color, float depth, Vector3 alongWorld)
        {
            if (Canvas == null || part == null) return;
            if (Sandbox.Gore == GoreLevel.Off && (kind == StampKind.Blood || kind == StampKind.Meat || kind == StampKind.Cut || kind == StampKind.Hole)) return;
            if (paintFrame != Time.frameCount) { paintFrame = Time.frameCount; paintsThisFrame = 0; }
            if (kind == StampKind.Blood && ++paintsThisFrame > 6) return;
            float s = body.Scale;
            Vector3 refP = part.ToBind(world) / s;
            Vector3 refAlong = alongWorld.sqrMagnitude > 1e-8f ? part.DirToBind(alongWorld).normalized : Vector3.zero;
            Canvas.AddStamp(kind, refP, radius / s, intensity, color, depth, refAlong, Time.time);
        }

        public void PaintBind(StampKind kind, Vector3 bindP, float radius, float intensity, Color color, float depth)
        {
            if (Canvas == null) return;
            float s = body.Scale;
            Canvas.AddStamp(kind, bindP / s, radius / s, intensity, color, depth, Vector3.zero, Time.time);
        }

        public void ClearPaint()
        {
            if (Canvas != null) Canvas.ClearAll();
        }
        #endregion

        #region Mesh operations (main-thread entry points)
        public void Dent(BodyPart part, Vector3 world, Vector3 inwardWorld, float radius, float depth)
        {
            var piece = PieceOf(part);
            if (piece == null || depth <= 0f) return;
            var op = new BodyOp { type = BodyOpType.Dent, seg = part.segIndex, seg2 = -1, a = part.ToBind(world), b = part.DirToBind(inwardWorld).normalized, f0 = radius, f1 = depth };
            Record(op);
            ApplyDeformOp(op, piece);
        }

        public void Squash(BodyPart part, Vector3 world, Vector3 axisWorld, float radius, float amount)
        {
            var piece = PieceOf(part);
            if (piece == null || amount <= 0f) return;
            var op = new BodyOp { type = BodyOpType.Squash, seg = part.segIndex, seg2 = -1, a = part.ToBind(world), b = part.DirToBind(axisWorld).normalized, f0 = radius, f1 = amount };
            Record(op);
            ApplyDeformOp(op, piece);
        }

        public void Stretch(BodyPart part, Vector3 world, Vector3 dirWorld, float radius, float amount)
        {
            var piece = PieceOf(part);
            if (piece == null || amount <= 0f) return;
            var op = new BodyOp { type = BodyOpType.Stretch, seg = part.segIndex, seg2 = -1, a = part.ToBind(world), b = part.DirToBind(dirWorld).normalized, f0 = radius, f1 = amount };
            Record(op);
            ApplyDeformOp(op, piece);
        }

        void ApplyDeformOp(BodyOp op, MeshPiece piece)
        {
            float maxDisp = Sandbox.Boot.damage.maxDentDepth * body.Scale * 1.6f;
            Enqueue(new DeformJob { piece = piece, op = op, maxDisp = maxDisp });
        }

        /// <summary>A real hole: triangles inside the radius are removed and replaced by a tunnel with a raised rim.</summary>
        public void Hole(BodyPart part, Vector3 world, Vector3 inwardWorld, float radius, float depth, bool crater)
        {
            var piece = PieceOf(part);
            if (piece == null) return;
            var op = new BodyOp { type = BodyOpType.Hole, seg = part.segIndex, seg2 = -1, a = part.ToBind(world), b = part.DirToBind(inwardWorld).normalized, f0 = radius, f1 = depth, flags = crater ? 1 : 0 };
            Record(op);
            ApplyHoleOp(op, piece);
        }

        void ApplyHoleOp(BodyOp op, MeshPiece piece)
        {
            var seg = SegAt(op.seg);
            if (seg == null) return;
            // depth limited by the distance to the bone axis so the tunnel never pokes out the other side
            Vector3 o = PartInfo.Origin[(int)seg.id] * body.Scale;
            Vector3 a = seg.segAxis;
            Vector3 q = op.a - o;
            float axisDist = (q - a * Vector3.Dot(q, a)).magnitude;
            float depth = Mathf.Min(op.f1, Mathf.Max(0.006f * body.Scale, axisDist * 0.7f));
            Enqueue(new HoleJob { piece = piece, center = op.a, inward = op.b, radius = op.f0, depth = depth, crater = (op.flags & 1) != 0, bone = op.seg, clay = body.Style == BodyLook.Clay });
        }

        public BodyPart SegAt(int i) { return i >= 0 && i < body.AllPieces.Count ? body.AllPieces[i] : null; }

        void Record(BodyOp op)
        {
            body.OpLog.Add(op);
            if (Sandbox.Net != null) Sandbox.Net.OnBodyOp(body, op);
        }

        /// <summary>Re-applies an op received from the network (or replayed for a late joiner).</summary>
        public void ApplyRemoteOp(BodyOp op)
        {
            body.OpLog.Add(op);
            switch (op.type)
            {
                case BodyOpType.Dent: case BodyOpType.Squash: case BodyOpType.Stretch:
                    { var p = PieceOf(SegAt(op.seg)); if (p != null) ApplyDeformOp(op, p); break; }
                case BodyOpType.Hole:
                    { var p = PieceOf(SegAt(op.seg)); if (p != null) ApplyHoleOp(op, p); break; }
                case BodyOpType.Slice: QueueSlice(op, false); break;
                case BodyOpType.Fracture: QueueFracture(op, false); break;
                case BodyOpType.Sever: QueueSever(op, false); break;
            }
        }

        /// <summary>Slice the body anywhere along a segment with a plane (world). The segment's rigid body is split at the
        /// plane and the mesh is cut, capped and separated into pieces.</summary>
        public bool Slice(BodyPart seg, Vector3 worldPoint, Vector3 worldNormal)
        {
            if (seg == null || seg.rb == null) return false;
            Vector3 nb = seg.DirToBind(worldNormal).normalized;
            Vector3 pb = seg.ToBind(worldPoint);
            // where the plane crosses the segment axis
            Vector3 o = PartInfo.Origin[(int)seg.id] * body.Scale;
            Vector3 a = seg.segAxis;
            float denom = Vector3.Dot(nb, a);
            if (Mathf.Abs(denom) < 0.3f) return false;   // nearly parallel to the bone: a gash, not a cut
            float split = Vector3.Dot(pb - o, nb) / denom;
            float len = seg.segMax - seg.segMin;
            float minS = seg.segMin + len * 0.12f, maxS = seg.segMax - len * 0.12f;
            if (maxS <= minS) return false;
            split = Mathf.Clamp(split, minS, maxS);
            // orient the normal toward the distal end and move the plane onto the clamped split point
            if (denom < 0f) nb = -nb;
            Vector3 onAxis = o + a * split;
            var op = new BodyOp { type = BodyOpType.Slice, seg = seg.segIndex, seg2 = -1, a = onAxis, b = nb, f0 = split };
            return QueueSlice(op, true);
        }

        bool QueueSlice(BodyOp op, bool record)
        {
            var seg = SegAt(op.seg);
            var piece = PieceOf(seg);
            if (seg == null || piece == null) return false;
            if (record) Record(op);
            Enqueue(new SliceJob { piece = piece, op = op, sever = false, clay = body.Style == BodyLook.Clay });
            return true;
        }

        /// <summary>A joint broke (or was cut at the joint): separate the mesh at the joint plane (bind space).</summary>
        public void SeverAt(BodyPart child, BodyPart parent, Vector3 bindPoint, Vector3 bindNormal)
        {
            if (child == null) return;
            var op = new BodyOp { type = BodyOpType.Sever, seg = child.segIndex, seg2 = parent != null ? parent.segIndex : -1, a = bindPoint, b = bindNormal.normalized };
            QueueSever(op, true);
        }

        void QueueSever(BodyOp op, bool record)
        {
            var child = SegAt(op.seg);
            var parent = SegAt(op.seg2);
            var piece = PieceOf(child);
            if (piece == null) piece = PieceOf(parent);
            if (piece == null) return;
            if (record) Record(op);
            Enqueue(new SliceJob { piece = piece, op = op, sever = true, clay = body.Style == BodyLook.Clay });
        }

        /// <summary>A bone fractures at an axis coordinate: the mesh beyond the fracture is re-weighted to a new segment
        /// joined by a loose joint, so the limb visibly kinks there.</summary>
        public void Fracture(BodyPart seg, float split)
        {
            if (seg == null) return;
            var op = new BodyOp { type = BodyOpType.Fracture, seg = seg.segIndex, seg2 = -1, f0 = split };
            QueueFracture(op, true);
        }

        void QueueFracture(BodyOp op, bool record)
        {
            var seg = SegAt(op.seg);
            var piece = PieceOf(seg);
            if (seg == null || piece == null) return;
            if (record) Record(op);
            Enqueue(new ReweightJob { piece = piece, op = op });
        }

        /// <summary>Called by jobs: creates the separated piece's renderer.</summary>
        public MeshPiece AddPiece(MeshData d, BodyPart root)
        {
            var p = CreatePiece(d, root);
            Upload(p, true);
            return p;
        }

        public void RemovePiece(MeshPiece p)
        {
            if (p == null) return;
            Pieces.Remove(p);
            if (p.mesh != null) UnityEngine.Object.Destroy(p.mesh);
            if (p.go != null) UnityEngine.Object.Destroy(p.go);
        }

        public RagdollBody Body { get { return body; } }
        #endregion
    }
    #endregion

    #region Canvas
    public enum StampKind { Bruise, Cut, Hole, Blood, Burn, Meat, Shock, Crack, Tear }

    /// <summary>Global per-frame budget for body texture uploads.</summary>
    public static class CanvasUploader
    {
        static int frame = -1, used;
        public static int PerFrame = 2;
        public static int UploadsLastSecond;
        static int counter;
        static float windowStart;

        public static bool TryConsume()
        {
            if (frame != Time.frameCount) { frame = Time.frameCount; used = 0; }
            if (used >= PerFrame) return false;
            used++;
            counter++;
            if (Time.unscaledTime - windowStart > 1f) { UploadsLastSecond = counter; counter = 0; windowStart = Time.unscaledTime; }
            return true;
        }
    }

    /// <summary>One paint atlas per body. Layers: skin + cloth (shared base, chosen per texel by this body's cloth mask,
    /// so torn clothes reveal skin) and up to 96 live damage stamps evaluated in 3D via the position map.</summary>
    public class BodyCanvas
    {
        struct Stamp
        {
            public StampKind kind;
            public Vector3 c, along;
            public float r, intensity, birth, depth;
            public int seed;
            public Color color;
            public RectInt r0, r1, r2, r3, r4, r5;
            public int rects;
        }

        readonly BodyTemplate t;
        readonly BodyPaint basePaint;
        readonly int S;
        readonly Color32[] px;       // what is uploaded: bake + live (ageing) stamps
        readonly Color32[] bake;     // base layers + permanent damage (cuts, holes, burns, torn cloth)
        readonly byte[] mask;
        readonly List<Stamp> stamps = new List<Stamp>(96);
        readonly bool clay;
        RenderTexture rt;
        Texture2D tex;            // fallback path (no CopyTexture)
        readonly bool useRT;
        RectInt dirty;
        bool hasDirty;
        float lastUpload = -10f;
        float nextAge;
        int seedCounter;
        bool disposed;
        static readonly Dictionary<int, Texture2D> staging = new Dictionary<int, Texture2D>();

        public Texture Texture { get { return useRT ? (Texture)rt : tex; } }
        public bool Dirty { get { return hasDirty; } }
        public int StampCount { get { return stamps.Count; } }

        public BodyCanvas(BodyTemplate template, BodyPaint paint, BodyLook look, int seed)
        {
            t = template;
            basePaint = paint;
            S = template.atlasSize;
            clay = look == BodyLook.Clay;
            seedCounter = seed * 1000 + 17;
            px = new Color32[S * S];
            bake = new Color32[S * S];
            mask = (byte[])paint.mask.Clone();
            for (int i = 0; i < px.Length; i++) { bake[i] = mask[i] != 0 ? paint.cloth[i] : paint.skin[i]; px[i] = bake[i]; }
            useRT = (SystemInfo.copyTextureSupport & CopyTextureSupport.TextureToRT) != 0 && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32);
            if (useRT)
            {
                rt = new RenderTexture(S, S, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                rt.name = "Body paint";
                rt.useMipMap = true;
                rt.autoGenerateMips = false;
                rt.wrapMode = TextureWrapMode.Clamp;
                rt.filterMode = FilterMode.Trilinear;
                rt.anisoLevel = 4;
                if (!rt.Create()) { useRT = false; rt.Release(); UnityEngine.Object.Destroy(rt); rt = null; }
                else
                {
                    var st = Staging(S);
                    if (st == null || st.graphicsFormat != rt.graphicsFormat) { useRT = false; rt.Release(); UnityEngine.Object.Destroy(rt); rt = null; }
                }
            }
            if (!useRT)
            {
                tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
                tex.name = "Body paint";
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
            }
            dirty = new RectInt(0, 0, S, S);
            hasDirty = true;
            Upload(0f);
        }

        static Texture2D Staging(int size)
        {
            int k = Mathf.NextPowerOfTwo(Mathf.Max(32, size));
            Texture2D st;
            if (staging.TryGetValue(k, out st) && st != null) return st;
            st = new Texture2D(k, k, TextureFormat.RGBA32, false);
            st.name = "Paint staging " + k;
            st.hideFlags = HideFlags.DontSave;
            staging[k] = st;
            return st;
        }

        public static void ReleaseStaging()
        {
            foreach (var kv in staging) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            staging.Clear();
        }

        public void Dispose()
        {
            disposed = true;
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }

        public bool ReadyToUpload(float now, float distance)
        {
            if (!hasDirty) return false;
            float interval = distance > 25f ? 0.6f : distance > 12f ? 0.25f : 0.12f;
            if (!useRT) interval = Mathf.Max(interval, 0.35f);
            return now - lastUpload >= interval;
        }

        public void Upload(float now)
        {
            if (!hasDirty || disposed) return;
            hasDirty = false;
            lastUpload = now;
            var r = dirty;
            if (useRT)
            {
                // copy the dirty rectangle in tiles that fit the staging textures
                int tile = Mathf.Min(S, 512);
                for (int ty = r.yMin; ty < r.yMax; ty += tile)
                    for (int tx = r.xMin; tx < r.xMax; tx += tile)
                    {
                        int w = Mathf.Min(tile, r.xMax - tx), h = Mathf.Min(tile, r.yMax - ty);
                        int size = Mathf.NextPowerOfTwo(Mathf.Max(w, h));
                        var st = Staging(size);
                        int ss = st.width;
                        var data = st.GetPixelData<Color32>(0);
                        for (int y = 0; y < h; y++)
                        {
                            int srcRow = (ty + y) * S + tx, dstRow = y * ss;
                            for (int x = 0; x < w; x++) data[dstRow + x] = px[srcRow + x];
                        }
                        st.Apply(false, false);
                        Graphics.CopyTexture(st, 0, 0, 0, 0, w, h, rt, 0, 0, tx, ty);
                    }
                rt.GenerateMips();
            }
            else
            {
                tex.SetPixels32(px);
                tex.Apply(true, false);
            }
        }

        void MarkDirty(RectInt r)
        {
            r.xMin = Mathf.Clamp(r.xMin, 0, S); r.yMin = Mathf.Clamp(r.yMin, 0, S);
            r.xMax = Mathf.Clamp(r.xMax, 0, S); r.yMax = Mathf.Clamp(r.yMax, 0, S);
            if (r.width <= 0 || r.height <= 0) return;
            if (!hasDirty) { dirty = r; hasDirty = true; return; }
            int x0 = Mathf.Min(dirty.xMin, r.xMin), y0 = Mathf.Min(dirty.yMin, r.yMin);
            int x1 = Mathf.Max(dirty.xMax, r.xMax), y1 = Mathf.Max(dirty.yMax, r.yMax);
            dirty = new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        public void ClearAll()
        {
            stamps.Clear();
            Array.Copy(basePaint.mask, mask, mask.Length);
            for (int i = 0; i < px.Length; i++) { bake[i] = mask[i] != 0 ? basePaint.cloth[i] : basePaint.skin[i]; px[i] = bake[i]; }
            MarkDirty(new RectInt(0, 0, S, S));
        }

        /// <summary>Adds a stamp in reference bind space and paints it immediately into its texel rectangles.</summary>
        public void AddStamp(StampKind kind, Vector3 c, float radius, float intensity, Color color, float depth, Vector3 along, float now)
        {
            if (!MathUtil.IsFinite(c) || radius <= 0f) return;
            var s = new Stamp { kind = kind, c = c, along = along, r = radius, intensity = Mathf.Clamp01(intensity), birth = now, depth = depth, seed = seedCounter++, color = color };
            float reach = kind == StampKind.Cut ? radius * 1.2f + 0.01f : kind == StampKind.Tear ? radius * 1.9f : radius * 1.7f;
            ComputeRects(ref s, reach);
            if (s.rects == 0) return;
            bool permanent = kind == StampKind.Tear || kind == StampKind.Cut || kind == StampKind.Hole || kind == StampKind.Meat || kind == StampKind.Burn || kind == StampKind.Crack || kind == StampKind.Shock;
            if (permanent)
            {
                // baked into the permanent layer, then the live stamps are re-applied on top
                for (int i = 0; i < s.rects; i++) { var rr = RectAt(ref s, i); DrawRect(ref s, rr, now, bake); Recompose(rr, now); }
                return;
            }
            for (int i = 0; i < s.rects; i++) { var rr = RectAt(ref s, i); DrawRect(ref s, rr, now, px); MarkDirty(rr); }
            stamps.Add(s);
            if (stamps.Count > 90)
            {
                // the oldest live stamp is frozen into the bake layer
                var old = stamps[0];
                stamps.RemoveAt(0);
                for (int i = 0; i < old.rects; i++) DrawRect(ref old, RectAt(ref old, i), now, bake);
            }
        }

        static RectInt RectAt(ref Stamp s, int i)
        {
            switch (i) { case 0: return s.r0; case 1: return s.r1; case 2: return s.r2; case 3: return s.r3; case 4: return s.r4; default: return s.r5; }
        }

        static void SetRect(ref Stamp s, int i, RectInt r)
        {
            switch (i) { case 0: s.r0 = r; break; case 1: s.r1 = r; break; case 2: s.r2 = r; break; case 3: s.r3 = r; break; case 4: s.r4 = r; break; default: s.r5 = r; break; }
        }

        void ComputeRects(ref Stamp s, float reach)
        {
            s.rects = 0;
            for (int b = 0; b < PartInfo.Count && s.rects < 6; b++)
            {
                var ch = t.charts[b];
                float d = MathUtil.DistancePointSegment(s.c, BodyTemplate.SkinSegA(b), BodyTemplate.SkinSegB(b)) - PartInfo.Radius[b] * 1.6f;
                if (d > reach + 0.02f) continue;
                Vector3 q = s.c - ch.origin;
                float tt = Vector3.Dot(q, ch.axis);
                float rad = Mathf.Max(0.012f, (q - ch.axis * tt).magnitude);
                float u = ch.U(s.c), v = ch.V(s.c);
                float ru = reach / (2f * Mathf.PI * rad) * 1.25f + 0.02f, rv = reach / Mathf.Max(0.01f, ch.tmax - ch.tmin) * 1.25f;
                if (ru > 0.5f) ru = 0.55f;
                AddChartRect(ref s, ch, u - ru, u + ru, v - rv, v + rv);
                if (u - ru < 0f) AddChartRect(ref s, ch, u - ru + 1f, u + ru + 1f, v - rv, v + rv);
                if (u + ru > 1f) AddChartRect(ref s, ch, u - ru - 1f, u + ru - 1f, v - rv, v + rv);
            }
        }

        void AddChartRect(ref Stamp s, SkinChart ch, float u0, float u1, float v0, float v1)
        {
            if (s.rects >= 6) return;
            Vector2 a = ch.Texel(Mathf.Max(u0, -0.02f), Mathf.Max(v0, -0.05f)), b = ch.Texel(Mathf.Min(u1, 1.08f), Mathf.Min(v1, 1.05f));
            int x0 = Mathf.Max(ch.x, Mathf.FloorToInt(a.x) - 2), y0 = Mathf.Max(ch.y, Mathf.FloorToInt(a.y) - 2);
            int x1 = Mathf.Min(ch.x + ch.w, Mathf.CeilToInt(b.x) + 2), y1 = Mathf.Min(ch.y + ch.h, Mathf.CeilToInt(b.y) + 2);
            if (x1 <= x0 || y1 <= y0) return;
            SetRect(ref s, s.rects, new RectInt(x0, y0, x1 - x0, y1 - y0));
            s.rects++;
        }

        public void Tick(float now)
        {
            if (now < nextAge) return;
            nextAge = now + 3f;
            // bruise ageing and blood drying: recompose the rectangles of the young stamps
            for (int i = 0; i < stamps.Count; i++)
            {
                var s = stamps[i];
                float age = now - s.birth;
                bool ageing = (s.kind == StampKind.Bruise && age < 240f) || (s.kind == StampKind.Blood && age < 120f);
                if (!ageing) continue;
                for (int k = 0; k < s.rects; k++) Recompose(RectAt(ref s, k), now);
            }
        }

        /// <summary>Rebuilds a rectangle from the bake layer and every live stamp touching it.</summary>
        void Recompose(RectInt r, float now)
        {
            r.xMin = Mathf.Clamp(r.xMin, 0, S); r.yMin = Mathf.Clamp(r.yMin, 0, S);
            r.xMax = Mathf.Clamp(r.xMax, 0, S); r.yMax = Mathf.Clamp(r.yMax, 0, S);
            for (int y = r.yMin; y < r.yMax; y++)
            {
                int row = y * S;
                for (int x = r.xMin; x < r.xMax; x++) px[row + x] = bake[row + x];
            }
            for (int k = 0; k < stamps.Count; k++)
            {
                var s = stamps[k];
                for (int j = 0; j < s.rects; j++)
                {
                    var sr = RectAt(ref s, j);
                    int x0 = Mathf.Max(sr.xMin, r.xMin), x1 = Mathf.Min(sr.xMax, r.xMax), y0 = Mathf.Max(sr.yMin, r.yMin), y1 = Mathf.Min(sr.yMax, r.yMax);
                    if (x1 <= x0 || y1 <= y0) continue;
                    DrawRect(ref s, new RectInt(x0, y0, x1 - x0, y1 - y0), now, px);
                }
            }
            MarkDirty(r);
        }

        void DrawRect(ref Stamp s, RectInt r, float now, Color32[] dst)
        {
            float age = now - s.birth;
            Color bruise = BruiseColor(age, s.intensity, clay);
            Color blood = BloodColor(s.color, age);
            float invR = 1f / Mathf.Max(1e-4f, s.r);
            for (int y = r.yMin; y < r.yMax; y++)
            {
                int row = y * S;
                for (int x = r.xMin; x < r.xMax; x++)
                {
                    int i = row + x;
                    if (t.texChart[i] < 0) continue;
                    Vector3 p = t.texPos[i];
                    Vector3 dv = p - s.c;
                    float d2 = dv.sqrMagnitude * invR * invR;
                    if (d2 > 4.5f && s.kind != StampKind.Cut) continue;
                    float n = MathUtil.Hash01(x, y, s.seed);
                    Color src; float a;
                    switch (s.kind)
                    {
                        case StampKind.Bruise:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.25f;
                                a = Mathf.Clamp01(1.2f - d) * bruise.a;
                                src = bruise;
                                break;
                            }
                        case StampKind.Cut:
                            {
                                Vector3 along = s.along.sqrMagnitude > 0.5f ? s.along : Vector3.right;
                                float l = Vector3.Dot(dv, along);
                                if (Mathf.Abs(l) > s.r) { a = 0f; src = Color.clear; break; }
                                float across = (dv - along * l).magnitude;
                                float taper = Mathf.Sqrt(Mathf.Max(0f, 1f - (l * invR) * (l * invR)));
                                float width = (0.0012f + 0.0035f * Mathf.Clamp01(s.depth)) * taper * (0.8f + n * 0.4f) + 0.0004f;
                                if (across < width * 0.45f) { src = clay ? new Color(0.35f, 0.16f, 0.12f) : (across < width * 0.18f && s.depth > 0.6f ? new Color(0.92f, 0.88f, 0.8f) : new Color(0.3f, 0.02f, 0.03f)); a = 1f; }
                                else if (across < width) { src = clay ? new Color(0.62f, 0.35f, 0.28f) : new Color(0.62f, 0.08f, 0.08f); a = 1f; }
                                else if (across < width * 2.4f) { src = clay ? new Color(0.5f, 0.3f, 0.25f) : new Color(0.55f, 0.12f, 0.12f); a = Mathf.Clamp01((width * 2.4f - across) / width) * 0.5f; }
                                else { a = 0f; src = Color.clear; }
                                break;
                            }
                        case StampKind.Hole:
                            {
                                float d = Mathf.Sqrt(d2);
                                if (d < 1.15f) { src = clay ? new Color(0.45f, 0.24f, 0.18f) : new Color(0.32f, 0.02f, 0.03f); a = 1f; }
                                else if (d < 1.9f) { src = clay ? new Color(0.55f, 0.32f, 0.26f) : new Color(0.45f, 0.08f, 0.1f); a = Mathf.Clamp01((1.9f - d) / 0.75f) * 0.75f; }
                                else { a = 0f; src = Color.clear; }
                                break;
                            }
                        case StampKind.Meat:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.18f;
                                if (clay) { src = d < 0.9f ? new Color(0.8f, 0.5f, 0.42f) : new Color(0.6f, 0.35f, 0.3f); a = d < 1.3f ? 1f : 0f; break; }
                                if (d < 0.35f && s.depth > 0.5f) { src = d < 0.18f ? new Color(0.5f, 0.1f, 0.08f) : new Color(0.93f, 0.9f, 0.82f); a = 1f; }
                                else if (d < 0.85f) { src = Color.Lerp(new Color(0.42f, 0.03f, 0.04f), new Color(0.7f, 0.15f, 0.14f), n); a = 1f; }
                                else if (d < 1.05f) { src = new Color(0.9f, 0.82f, 0.5f); a = 0.8f; }
                                else if (d < 1.5f) { src = blood; a = Mathf.Clamp01((1.5f - d) * 2f) * 0.8f; }
                                else { a = 0f; src = Color.clear; }
                                break;
                            }
                        case StampKind.Burn:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.3f;
                                a = Mathf.Clamp01(1.1f - d) * s.intensity;
                                src = d < 0.5f ? new Color(0.12f, 0.08f, 0.06f) : new Color(0.45f, 0.22f, 0.15f);
                                break;
                            }
                        case StampKind.Shock:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.5f;
                                a = Mathf.Clamp01(1f - d) * 0.6f * s.intensity;
                                src = new Color(0.25f, 0.2f, 0.25f);
                                break;
                            }
                        case StampKind.Crack:
                            {
                                // thin zig-zag bone crack line under the skin
                                Vector3 along = s.along.sqrMagnitude > 0.5f ? s.along : Vector3.up;
                                Vector3 side = Vector3.Cross(along, Vector3.Cross(dv, along));
                                float l = Vector3.Dot(dv, along) * invR;
                                float zig = Mathf.Abs(Mathf.Repeat(l * 3f, 2f) - 1f) * 0.3f - 0.15f;
                                float w = Mathf.Abs(Vector3.Dot(dv, side.sqrMagnitude > 1e-10f ? side.normalized : Vector3.zero) * invR - zig);
                                a = Mathf.Abs(l) < 1f && w < 0.06f ? 0.55f * (1f - Mathf.Abs(l)) : 0f;
                                src = clay ? new Color(0.45f, 0.3f, 0.25f) : new Color(0.45f, 0.25f, 0.4f);
                                break;
                            }
                        case StampKind.Tear:
                            {
                                float d = Mathf.Sqrt(d2) * 0.55f;
                                float jag = n * 0.5f;
                                if (mask[i] != 0 && mask[i] != 4 && dst == bake)
                                {
                                    if (d < 1f - jag * 0.6f) { mask[i] = 0; bake[i] = basePaint.skin[i]; }
                                }
                                a = 0f; src = Color.clear;
                                if (mask[i] != 0 && d < 1.15f) { src = new Color(0.1f, 0.08f, 0.07f); a = 0.35f; }
                                break;
                            }
                        default:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.45f;
                                a = Mathf.Clamp01((1f - d) * 3f) * s.intensity * blood.a;
                                src = blood;
                                break;
                            }
                    }
                    if (a <= 0.003f) continue;
                    Color32 dc = dst[i];
                    dst[i] = new Color32(
                        (byte)Mathf.Clamp(dc.r + (src.r * 255f - dc.r) * a, 0f, 255f),
                        (byte)Mathf.Clamp(dc.g + (src.g * 255f - dc.g) * a, 0f, 255f),
                        (byte)Mathf.Clamp(dc.b + (src.b * 255f - dc.b) * a, 0f, 255f), 255);
                }
            }
        }

        /// <summary>Bruise colour over time: red-purple -> blue/purple -> yellow-green -> fades. Clay: a darker smudge.</summary>
        static Color BruiseColor(float age, float intensity, bool clay)
        {
            Color c;
            if (clay) c = new Color(0.45f, 0.28f, 0.25f);
            else if (age < 12f) c = Color.Lerp(new Color(0.62f, 0.25f, 0.3f), new Color(0.38f, 0.2f, 0.42f), age / 12f);
            else if (age < 60f) c = Color.Lerp(new Color(0.38f, 0.2f, 0.42f), new Color(0.3f, 0.22f, 0.45f), (age - 12f) / 48f);
            else if (age < 140f) c = Color.Lerp(new Color(0.3f, 0.22f, 0.45f), new Color(0.62f, 0.6f, 0.3f), (age - 60f) / 80f);
            else c = new Color(0.62f, 0.6f, 0.3f);
            float fade = age < 140f ? 1f : Mathf.Clamp01(1f - (age - 140f) / 80f);
            c.a = Mathf.Clamp01(intensity) * (clay ? 0.45f : 0.75f) * fade * Mathf.Clamp01(age / 1.5f + 0.35f);
            return c;
        }

        static Color BloodColor(Color fresh, float age)
        {
            var bs = Sandbox.Boot != null ? Sandbox.Boot.blood : null;
            float dry = bs != null ? bs.decalDryTime : 90f;
            Color dried = bs != null ? bs.dried : new Color(0.22f, 0.07f, 0.04f);
            Color c = Color.Lerp(fresh, dried, Mathf.Clamp01(age / dry) * 0.8f);
            c.a = 0.95f;
            return c;
        }
    }
    #endregion
}
