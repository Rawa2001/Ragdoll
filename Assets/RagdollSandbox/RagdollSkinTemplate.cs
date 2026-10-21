// Ragdoll Sandbox - the continuous body mesh template, generated once per Look:
//  * A signed-distance body made of smooth-unioned round cones, ellipsoids and rounded boxes (muscle shapes),
//    polygonised with surface nets on a 1 cm grid into ONE welded, watertight mesh (no seams, no joint spheres).
//  * Skin weights: nearest-bone membership plus smooth planar blends across every joint (2-4 influences).
//  * UVs: one cylindrical chart per bone, shelf-packed into a single atlas. A per-texel position map (bind space)
//    lets every paint stamp be evaluated in 3D, so damage, clothes and the face cross chart borders seamlessly.
//  * Base layers (skin, clothes, face, hair) are painted from the position map; Clay gets a kneaded/fingerprint
//    normal map, Realistic a fine pore map.
// Everything here is plain math (no Unity engine calls except Mathf/Vector3) until textures are created, so the
// mesh operations that reuse these structures can run on worker threads.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    public enum BodyLook { Clay, Realistic }

    #region SDF
    public struct SdfPrim
    {
        public byte kind;      // 0 round cone, 1 ellipsoid, 2 rounded box
        public Vector3 a, b;   // cone end points / centre (a)
        public float ra, rb;   // cone radii / box rounding (ra)
        public Vector3 r;      // ellipsoid radii / box half extents
        public float k;        // smooth-union blend radius
        public Vector3 mn, mx; // bounds (expanded)
    }

    public static class BodySdf
    {
        public static List<SdfPrim> Build(BodyLook look)
        {
            var list = new List<SdfPrim>(64);
            bool clay = look == BodyLook.Clay;
            float limb = clay ? 1.08f : 1f, torso = clay ? 1.05f : 1f, headS = clay ? 1.14f : 1f, extremity = clay ? 1.12f : 1f;
            float kk = clay ? 1.5f : 1f;
            // torso
            Ell(list, new Vector3(0f, 0.925f, -0.005f), new Vector3(0.155f, 0.105f, 0.105f) * torso, 0.03f * kk);
            for (int s = -1; s <= 1; s += 2) Ell(list, new Vector3(s * 0.068f, 0.885f, -0.055f), new Vector3(0.083f, 0.085f, 0.07f) * torso, 0.03f * kk);
            Ell(list, new Vector3(0f, 1.08f, 0f), new Vector3(0.135f, 0.12f, 0.095f) * torso, 0.06f * kk);
            Ell(list, new Vector3(0f, 1.07f, 0.035f), new Vector3(0.11f, 0.09f, 0.07f) * torso, 0.04f * kk);
            Ell(list, new Vector3(0f, 1.265f, -0.005f), new Vector3(0.152f, 0.165f, 0.1f) * torso, 0.06f * kk);
            for (int s = -1; s <= 1; s += 2) Ell(list, new Vector3(s * 0.065f, 1.315f, 0.05f), new Vector3(0.075f, 0.055f, 0.045f) * torso, 0.03f * kk);
            Cone(list, new Vector3(-0.13f, 1.41f, -0.03f), 0.055f * torso, new Vector3(0.13f, 1.41f, -0.03f), 0.055f * torso, 0.05f * kk);
            for (int s = -1; s <= 1; s += 2) Ell(list, new Vector3(s * 0.19f, 1.405f, -0.005f), new Vector3(0.07f, 0.065f, 0.062f) * limb, 0.035f * kk);
            // neck and head
            Cone(list, new Vector3(0f, 1.42f, -0.015f), 0.056f * torso, new Vector3(0f, 1.585f, 0f), 0.048f * torso, 0.03f * kk);
            Ell(list, new Vector3(0f, 1.665f, 0f), new Vector3(0.078f, 0.098f, 0.095f) * headS, 0.03f * kk);
            Ell(list, new Vector3(0f, 1.6f, 0.03f), new Vector3(0.062f, 0.06f, 0.07f) * headS, 0.03f * kk);
            if (clay) Ell(list, new Vector3(0f, 1.643f, 0.1f * headS), Vector3.one * 0.017f, 0.012f);
            else Ell(list, new Vector3(0f, 1.645f, 0.095f), new Vector3(0.012f, 0.02f, 0.016f), 0.008f);
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s;
                // arms
                Cone(list, new Vector3(x * 0.18f, 1.415f, -0.005f), 0.052f * limb, new Vector3(x * 0.455f, 1.42f, 0f), 0.038f * limb, 0.03f * kk);
                Ell(list, new Vector3(x * 0.32f, 1.405f, 0.012f), new Vector3(0.085f, 0.04f, 0.04f) * limb, 0.02f * kk);
                Ell(list, new Vector3(x * 0.31f, 1.425f, -0.018f), new Vector3(0.09f, 0.038f, 0.035f) * limb, 0.02f * kk);
                Cone(list, new Vector3(x * 0.465f, 1.42f, 0f), 0.038f * limb, new Vector3(x * 0.705f, 1.42f, 0.003f), 0.026f * limb, 0.025f * kk);
                Ell(list, new Vector3(x * 0.53f, 1.425f, 0f), new Vector3(0.08f, 0.038f, 0.04f) * limb, 0.02f * kk);
                // hands (mitten with thumb)
                Box(list, new Vector3(x * 0.775f, 1.418f, 0.003f), new Vector3(0.045f, 0.014f, 0.04f) * extremity, 0.012f * extremity, 0.018f * kk);
                Box(list, new Vector3(x * 0.865f, 1.418f, 0.003f), new Vector3(0.045f, 0.011f, 0.037f) * extremity, 0.01f * extremity, 0.012f * kk);
                Cone(list, new Vector3(x * 0.745f, 1.413f, 0.03f), 0.013f * extremity, new Vector3(x * 0.79f, 1.405f, 0.068f), 0.011f * extremity, 0.012f * kk);
                // legs
                Cone(list, new Vector3(x * 0.09f, 0.92f, 0f), 0.088f * limb, new Vector3(x * 0.09f, 0.49f, 0.005f), 0.052f * limb, 0.04f * kk);
                Ell(list, new Vector3(x * 0.095f, 0.69f, 0.03f), new Vector3(0.068f, 0.16f, 0.055f) * limb, 0.03f * kk);
                Ell(list, new Vector3(x * 0.09f, 0.72f, -0.03f), new Vector3(0.065f, 0.15f, 0.05f) * limb, 0.03f * kk);
                Ell(list, new Vector3(x * 0.09f, 0.48f, 0.015f), new Vector3(0.052f, 0.055f, 0.05f) * limb, 0.02f * kk);
                Cone(list, new Vector3(x * 0.09f, 0.48f, 0f), 0.05f * limb, new Vector3(x * 0.09f, 0.09f, -0.005f), 0.032f * limb, 0.02f * kk);
                Ell(list, new Vector3(x * 0.09f, 0.36f, -0.032f), new Vector3(0.05f, 0.1f, 0.048f) * limb, 0.025f * kk);
                Ell(list, new Vector3(x * 0.09f, 0.075f, -0.005f), Vector3.one * 0.036f * limb, 0.015f * kk);
                // feet
                Cone(list, new Vector3(x * 0.09f, 0.04f, -0.035f), 0.036f * extremity, new Vector3(x * 0.09f, 0.032f, 0.15f), 0.032f * extremity, 0.02f * kk);
                Ell(list, new Vector3(x * 0.09f, 0.036f, -0.04f), Vector3.one * 0.035f * extremity, 0.015f * kk);
            }
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                Vector3 mn, mx;
                if (p.kind == 0)
                {
                    float r = Mathf.Max(p.ra, p.rb);
                    mn = Vector3.Min(p.a, p.b) - Vector3.one * r; mx = Vector3.Max(p.a, p.b) + Vector3.one * r;
                }
                else if (p.kind == 1) { mn = p.a - p.r; mx = p.a + p.r; }
                else { mn = p.a - p.r - Vector3.one * p.ra; mx = p.a + p.r + Vector3.one * p.ra; }
                float pad = p.k + 0.03f;
                p.mn = mn - Vector3.one * pad; p.mx = mx + Vector3.one * pad;
                list[i] = p;
            }
            return list;
        }

        static void Ell(List<SdfPrim> l, Vector3 c, Vector3 r, float k) { l.Add(new SdfPrim { kind = 1, a = c, r = r, k = k }); }
        static void Cone(List<SdfPrim> l, Vector3 a, float ra, Vector3 b, float rb, float k) { l.Add(new SdfPrim { kind = 0, a = a, b = b, ra = ra, rb = rb, k = k }); }
        static void Box(List<SdfPrim> l, Vector3 c, Vector3 half, float round, float k) { l.Add(new SdfPrim { kind = 2, a = c, r = half, ra = round, k = k }); }

        public static float Prim(ref SdfPrim p, Vector3 q)
        {
            switch (p.kind)
            {
                case 0: return RoundCone(q, p.a, p.b, p.ra, p.rb);
                case 1: return Ellipsoid(q - p.a, p.r);
                default: return RoundBox(q - p.a, p.r, p.ra);
            }
        }

        public static float RoundCone(Vector3 p, Vector3 a, Vector3 b, float r1, float r2)
        {
            Vector3 ba = b - a;
            float l2 = Vector3.Dot(ba, ba);
            float rr = r1 - r2;
            float a2 = l2 - rr * rr;
            float il2 = 1f / l2;
            Vector3 pa = p - a;
            float y = Vector3.Dot(pa, ba);
            float z = y - l2;
            Vector3 xv = pa * l2 - ba * y;
            float x2 = Vector3.Dot(xv, xv);
            float y2 = y * y * l2;
            float z2 = z * z * l2;
            float k = Mathf.Sign(rr) * rr * rr * x2;
            if (Mathf.Sign(z) * a2 * z2 > k) return Mathf.Sqrt(x2 + z2) * il2 - r2;
            if (Mathf.Sign(y) * a2 * y2 < k) return Mathf.Sqrt(x2 + y2) * il2 - r1;
            return (Mathf.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
        }

        public static float Ellipsoid(Vector3 p, Vector3 r)
        {
            Vector3 pr = new Vector3(p.x / r.x, p.y / r.y, p.z / r.z);
            Vector3 prr = new Vector3(p.x / (r.x * r.x), p.y / (r.y * r.y), p.z / (r.z * r.z));
            float k0 = pr.magnitude;
            float k1 = prr.magnitude;
            if (k1 < 1e-8f) return -Mathf.Min(r.x, Mathf.Min(r.y, r.z));
            return k0 * (k0 - 1f) / k1;
        }

        public static float RoundBox(Vector3 p, Vector3 b, float r)
        {
            Vector3 q = new Vector3(Mathf.Abs(p.x) - b.x + r, Mathf.Abs(p.y) - b.y + r, Mathf.Abs(p.z) - b.z + r);
            Vector3 qm = new Vector3(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f), Mathf.Max(q.z, 0f));
            return qm.magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f) - r;
        }

        public static float SMin(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * 0.25f;
        }

        /// <summary>Full evaluation (with bounds culling).</summary>
        public static float Eval(List<SdfPrim> prims, Vector3 p)
        {
            float d = 1f;
            for (int i = 0; i < prims.Count; i++)
            {
                var pr = prims[i];
                if (p.x < pr.mn.x || p.y < pr.mn.y || p.z < pr.mn.z || p.x > pr.mx.x || p.y > pr.mx.y || p.z > pr.mx.z) continue;
                d = SMin(d, Prim(ref pr, p), pr.k);
            }
            return Mathf.Max(d, 0.0015f - p.y);
        }

        public static Vector3 Gradient(List<SdfPrim> prims, Vector3 p, float e)
        {
            float dx = Eval(prims, p + new Vector3(e, 0f, 0f)) - Eval(prims, p - new Vector3(e, 0f, 0f));
            float dy = Eval(prims, p + new Vector3(0f, e, 0f)) - Eval(prims, p - new Vector3(0f, e, 0f));
            float dz = Eval(prims, p + new Vector3(0f, 0f, e)) - Eval(prims, p - new Vector3(0f, 0f, e));
            return new Vector3(dx, dy, dz) / (2f * e);
        }
    }
    #endregion

    #region Template
    public class SkinChart
    {
        public int bone;
        public Vector3 origin, axis, r0, r1;
        public float tmin = float.MaxValue, tmax = float.MinValue;
        public float radius;
        public int x, y, w, h;      // texel rect (with padding)
        public int pad = 3;
        public float U(Vector3 p)
        {
            Vector3 q = p - origin;
            float t = Vector3.Dot(q, axis);
            Vector3 rad = q - axis * t;
            float a = Mathf.Atan2(Vector3.Dot(rad, r1), Vector3.Dot(rad, r0));
            return (a + Mathf.PI) / (2f * Mathf.PI);
        }
        public float T(Vector3 p) { return Vector3.Dot(p - origin, axis); }
        public float V(Vector3 p) { return (T(p) - tmin) / Mathf.Max(1e-4f, tmax - tmin); }
        public float InnerW { get { return (w - pad * 2) / 1.08f; } }
        public float InnerH { get { return h - pad * 2; } }
        /// <summary>Texel coordinates for normalised (u, v).</summary>
        public Vector2 Texel(float u, float v) { return new Vector2(x + pad + u * InnerW, y + pad + v * InnerH); }
    }

    /// <summary>Per-Look body mesh template (reference scale 1.75 m, T-pose, facing +Z). Shared by every ragdoll.</summary>
    public class BodyTemplate
    {
        public BodyLook look;
        public List<SdfPrim> prims;
        // render vertices
        public Vector3[] pos, nrm;
        public Vector4[] tan;
        public Vector2[] uv;
        public BoneWeight[] bw;
        public int[] weld;
        public int[] tris;
        public int weldCount;
        public int[] primary;              // per render vertex: primary bone
        // atlas
        public int atlasSize;
        public SkinChart[] charts = new SkinChart[PartInfo.Count];
        public Vector3[] texPos;           // bind position per texel
        public sbyte[] texChart;           // chart per texel (-1 none)
        public Texture2D normalMap, capTexture, holeTexture;
        public float buildMs;

        static readonly Dictionary<int, BodyTemplate> cache = new Dictionary<int, BodyTemplate>();

        public static BodyTemplate Get(BodyLook look, int atlasSize)
        {
            int key = (int)look * 10000 + atlasSize;
            BodyTemplate t;
            if (cache.TryGetValue(key, out t) && t != null) return t;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            t = new BodyTemplate();
            t.look = look;
            t.atlasSize = atlasSize;
            t.BuildMesh(0.0105f);
            t.BuildAtlas();
            t.BuildTextures();
            t.buildMs = (float)sw.Elapsed.TotalMilliseconds;
            cache[key] = t;
            Debug.Log("[RagdollSandbox] Body template (" + look + ") built: " + t.pos.Length + " vertices, " + (t.tris.Length / 3) + " triangles, " + t.buildMs.ToString("0") + " ms");
            return t;
        }

        public static void ClearCache()
        {
            foreach (var kv in cache)
            {
                var t = kv.Value;
                if (t == null) continue;
                if (t.normalMap != null) UnityEngine.Object.Destroy(t.normalMap);
                if (t.capTexture != null) UnityEngine.Object.Destroy(t.capTexture);
                if (t.holeTexture != null) UnityEngine.Object.Destroy(t.holeTexture);
            }
            cache.Clear();
            BodyPaintCache.Clear();
        }

        #region Skin segments
        public static Vector3 SkinSegA(int b)
        {
            switch ((PartId)b)
            {
                case PartId.Pelvis: return new Vector3(0f, 0.84f, 0f);
                case PartId.Abdomen: return new Vector3(0f, 1.03f, 0f);
                case PartId.Chest: return new Vector3(0f, 1.2f, 0f);
                case PartId.Neck: return new Vector3(0f, 1.45f, 0f);
                case PartId.Head: return new Vector3(0f, 1.58f, 0.01f);
                case PartId.FootL: return new Vector3(-0.09f, 0.05f, -0.04f);
                case PartId.FootR: return new Vector3(0.09f, 0.05f, -0.04f);
            }
            return PartInfo.Origin[b];
        }

        public static Vector3 SkinSegB(int b)
        {
            switch ((PartId)b)
            {
                case PartId.Pelvis: return new Vector3(0f, 1.02f, 0f);
                case PartId.Abdomen: return new Vector3(0f, 1.2f, 0f);
                case PartId.Chest: return new Vector3(0f, 1.45f, 0f);
                case PartId.Neck: return new Vector3(0f, 1.58f, 0f);
                case PartId.Head: return new Vector3(0f, 1.72f, 0.01f);
                case PartId.FootL: return new Vector3(-0.09f, 0.03f, 0.17f);
                case PartId.FootR: return new Vector3(0.09f, 0.03f, 0.17f);
            }
            return PartInfo.Tip[b];
        }

        struct JointBlend { public Vector3 j, d; public float w, r; }

        static JointBlend BlendFor(int child)
        {
            PartId c = (PartId)child;
            float side = PartInfo.SideSign(c);
            var b = new JointBlend();
            b.j = PartInfo.Origin[child];
            b.d = PartInfo.BoneAxis(c);
            switch (c)
            {
                case PartId.Abdomen: b.w = 0.05f; b.r = 1f; break;
                case PartId.Chest: b.w = 0.06f; b.r = 1f; break;
                case PartId.Neck: b.j = new Vector3(0f, 1.46f, 0f); b.w = 0.035f; b.r = 0.12f; break;
                case PartId.Head: b.j = new Vector3(0f, 1.575f, 0f); b.w = 0.025f; b.r = 0.15f; break;
                case PartId.UpperArmL: case PartId.UpperArmR: b.w = 0.045f; b.r = 0.085f; break;
                case PartId.ForearmL: case PartId.ForearmR: b.w = 0.03f; b.r = 0.07f; break;
                case PartId.HandL: case PartId.HandR: b.w = 0.018f; b.r = 0.06f; break;
                case PartId.ThighL: case PartId.ThighR: b.j = new Vector3(side * 0.09f, 0.9f, 0f); b.w = 0.05f; b.r = 0.12f; break;
                case PartId.ShinL: case PartId.ShinR: b.w = 0.035f; b.r = 0.09f; break;
                case PartId.FootL: case PartId.FootR: b.d = Vector3.down; b.w = 0.02f; b.r = 0.07f; break;
                default: b.w = 0.03f; b.r = 0.1f; break;
            }
            return b;
        }

        public static int PrimaryBone(Vector3 p)
        {
            int best = 0;
            float bd = float.MaxValue;
            for (int b = 0; b < PartInfo.Count; b++)
            {
                float d = MathUtil.DistancePointSegment(p, SkinSegA(b), SkinSegB(b)) - PartInfo.Radius[b];
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        /// <summary>Skin weights: primary bone + smooth planar blends across the joints around it.</summary>
        public static BoneWeight Weights(Vector3 p, int primaryBone)
        {
            var w = new float[PartInfo.Count];
            w[primaryBone] = 1f;
            int b = primaryBone;
            PartId pid = PartInfo.Parent[b];
            if (pid != PartId.Count)
            {
                var jb = BlendFor(b);
                Vector3 q = p - jb.j;
                float s = Vector3.Dot(q, jb.d);
                float rad = (q - jb.d * s).magnitude;
                float g = 1f - Smooth(jb.r, jb.r * 1.5f, rad);
                float f = Smooth(-jb.w, jb.w, s);
                float move = (1f - f) * g;
                w[(int)pid] += move;
                w[b] -= move;
            }
            var ch = PartInfo.Children((PartId)b);
            for (int i = 0; i < ch.Count; i++)
            {
                int c = (int)ch[i];
                var jb = BlendFor(c);
                Vector3 q = p - jb.j;
                float s = Vector3.Dot(q, jb.d);
                float rad = (q - jb.d * s).magnitude;
                float g = 1f - Smooth(jb.r, jb.r * 1.5f, rad);
                float f = Smooth(-jb.w, jb.w, s) * g;
                float move = w[b] * f;
                w[c] += move;
                w[b] -= move;
            }
            // top 4
            var res = new BoneWeight();
            int[] idx = { 0, 0, 0, 0 };
            float[] val = { 0f, 0f, 0f, 0f };
            for (int i = 0; i < PartInfo.Count; i++)
            {
                float v = w[i];
                if (v <= 1e-4f) continue;
                for (int k = 0; k < 4; k++)
                {
                    if (v > val[k])
                    {
                        for (int m = 3; m > k; m--) { val[m] = val[m - 1]; idx[m] = idx[m - 1]; }
                        val[k] = v; idx[k] = i;
                        break;
                    }
                }
            }
            float sum = val[0] + val[1] + val[2] + val[3];
            if (sum <= 0f) { val[0] = 1f; idx[0] = primaryBone; sum = 1f; }
            res.boneIndex0 = idx[0]; res.weight0 = val[0] / sum;
            res.boneIndex1 = idx[1]; res.weight1 = val[1] / sum;
            res.boneIndex2 = idx[2]; res.weight2 = val[2] / sum;
            res.boneIndex3 = idx[3]; res.weight3 = val[3] / sum;
            return res;
        }
        #endregion

        #region Mesh (surface nets)
        void BuildMesh(float h)
        {
            prims = BodySdf.Build(look);
            Vector3 bmin = new Vector3(-0.97f, -0.02f, -0.19f), bmax = new Vector3(0.97f, 1.84f, 0.22f);
            int nx = Mathf.CeilToInt((bmax.x - bmin.x) / h) | 1;
            int ny = Mathf.CeilToInt((bmax.y - bmin.y) / h) + 1;
            int nz = Mathf.CeilToInt((bmax.z - bmin.z) / h) + 1;
            // symmetric grid in x (centre column at x = 0) so the mesh is perfectly mirrored
            Vector3 org = new Vector3(-(nx - 1) * 0.5f * h, bmin.y, bmin.z);
            int nxy = nx * ny;
            var grid = new float[nxy * nz];
            for (int i = 0; i < grid.Length; i++) grid[i] = 1f;
            for (int pi = 0; pi < prims.Count; pi++)
            {
                var pr = prims[pi];
                int x0 = Mathf.Max(0, Mathf.FloorToInt((pr.mn.x - org.x) / h)), x1 = Mathf.Min(nx - 1, Mathf.CeilToInt((pr.mx.x - org.x) / h));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((pr.mn.y - org.y) / h)), y1 = Mathf.Min(ny - 1, Mathf.CeilToInt((pr.mx.y - org.y) / h));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((pr.mn.z - org.z) / h)), z1 = Mathf.Min(nz - 1, Mathf.CeilToInt((pr.mx.z - org.z) / h));
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        int row = y * nx + z * nxy;
                        for (int x = x0; x <= x1; x++)
                        {
                            Vector3 p = new Vector3(org.x + x * h, org.y + y * h, org.z + z * h);
                            float d = BodySdf.Prim(ref pr, p);
                            int gi = row + x;
                            grid[gi] = BodySdf.SMin(grid[gi], d, pr.k);
                        }
                    }
            }
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                {
                    float py = org.y + y * h;
                    float clip = 0.0015f - py;
                    int row = y * nx + z * nxy;
                    for (int x = 0; x < nx; x++) if (grid[row + x] < clip) grid[row + x] = clip;
                }
            int cx = nx - 1, cy = ny - 1, cz = nz - 1;
            var cellVert = new int[cx * cy * cz];
            for (int i = 0; i < cellVert.Length; i++) cellVert[i] = -1;
            var verts = new List<Vector3>(24000);
            int[] co = { 0, 1, nx, nx + 1, nxy, nxy + 1, nxy + nx, nxy + nx + 1 };
            int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            var cv = new float[8];
            for (int z = 0; z < cz; z++)
                for (int y = 0; y < cy; y++)
                    for (int x = 0; x < cx; x++)
                    {
                        int g0 = x + y * nx + z * nxy;
                        int mask = 0;
                        for (int c = 0; c < 8; c++) { cv[c] = grid[g0 + co[c]]; if (cv[c] < 0f) mask |= 1 << c; }
                        if (mask == 0 || mask == 255) continue;
                        Vector3 sum = Vector3.zero;
                        int n = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int a = edges[e, 0], b = edges[e, 1];
                            bool ia = (mask & (1 << a)) != 0, ib = (mask & (1 << b)) != 0;
                            if (ia == ib) continue;
                            float t = cv[a] / (cv[a] - cv[b]);
                            Vector3 pa = new Vector3(a & 1, (a >> 1) & 1, (a >> 2) & 1), pb = new Vector3(b & 1, (b >> 1) & 1, (b >> 2) & 1);
                            sum += Vector3.Lerp(pa, pb, t);
                            n++;
                        }
                        Vector3 local = sum / Mathf.Max(1, n);
                        cellVert[x + y * cx + z * cx * cy] = verts.Count;
                        verts.Add(org + new Vector3((x + local.x) * h, (y + local.y) * h, (z + local.z) * h));
                    }
            var tri = new List<int>(verts.Count * 6);
            for (int z = 1; z < nz - 1; z++)
                for (int y = 1; y < ny - 1; y++)
                    for (int x = 1; x < nx - 1; x++)
                    {
                        int g = x + y * nx + z * nxy;
                        bool inside = grid[g] < 0f;
                        // x edge: u = y, v = z
                        if (x < nx - 1) Face(grid, inside, grid[g + 1] < 0f, cellVert, cx, cy, x, y, z, 0, verts, tri);
                        if (y < ny - 1) Face(grid, inside, grid[g + nx] < 0f, cellVert, cx, cy, x, y, z, 1, verts, tri);
                        if (z < nz - 1) Face(grid, inside, grid[g + nxy] < 0f, cellVert, cx, cy, x, y, z, 2, verts, tri);
                    }
            grid = null;
            cellVert = null;
            // project onto the analytic surface, one tangential smoothing pass, project again
            var vp = verts.ToArray();
            Project(vp, h);
            Smooth(vp, tri, 0.35f);
            Project(vp, h);
            // compact unused vertices (surface nets can leave isolated cells)
            var used = new int[vp.Length];
            for (int i = 0; i < used.Length; i++) used[i] = -1;
            int count = 0;
            for (int i = 0; i < tri.Count; i++) if (used[tri[i]] < 0) used[tri[i]] = count++;
            var wpos = new Vector3[count];
            for (int i = 0; i < vp.Length; i++) if (used[i] >= 0) wpos[used[i]] = vp[i];
            for (int i = 0; i < tri.Count; i++) tri[i] = used[tri[i]];
            weldCount = count;
            weldPosTmp = wpos;
            weldTris = tri.ToArray();
        }

        Vector3[] weldPosTmp;
        int[] weldTris;

        static void Face(float[] grid, bool inside0, bool inside1, int[] cellVert, int cx, int cy, int x, int y, int z, int axis, List<Vector3> verts, List<int> tri)
        {
            if (inside0 == inside1) return;
            // the four cells around the edge, ordered around the axis (u x v = axis)
            int ux = 0, uy = 0, uz = 0, vx = 0, vy = 0, vz = 0;
            if (axis == 0) { uy = 1; vz = 1; }
            else if (axis == 1) { uz = 1; vx = 1; }
            else { ux = 1; vy = 1; }
            int c0 = Cell(cellVert, cx, cy, x - ux - vx, y - uy - vy, z - uz - vz);
            int c1 = Cell(cellVert, cx, cy, x - vx, y - vy, z - vz);
            int c2 = Cell(cellVert, cx, cy, x, y, z);
            int c3 = Cell(cellVert, cx, cy, x - ux, y - uy, z - uz);
            if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0) return;
            if (!inside0) { int t = c1; c1 = c3; c3 = t; }
            // split along the shorter diagonal
            float d02 = (verts[c0] - verts[c2]).sqrMagnitude, d13 = (verts[c1] - verts[c3]).sqrMagnitude;
            if (d02 <= d13) { tri.Add(c0); tri.Add(c1); tri.Add(c2); tri.Add(c0); tri.Add(c2); tri.Add(c3); }
            else { tri.Add(c1); tri.Add(c2); tri.Add(c3); tri.Add(c1); tri.Add(c3); tri.Add(c0); }
        }

        static int Cell(int[] cellVert, int cx, int cy, int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= cx || y >= cy) return -1;
            int i = x + y * cx + z * cx * cy;
            if (i >= cellVert.Length) return -1;
            return cellVert[i];
        }

        void Project(Vector3[] vp, float h)
        {
            float e = h * 0.25f;
            for (int i = 0; i < vp.Length; i++)
            {
                Vector3 p = vp[i];
                for (int it = 0; it < 2; it++)
                {
                    float d = BodySdf.Eval(prims, p);
                    Vector3 g = BodySdf.Gradient(prims, p, e);
                    float g2 = g.sqrMagnitude;
                    if (g2 < 1e-8f) break;
                    Vector3 step = g * (d / g2);
                    if (step.magnitude > h) step = step.normalized * h;
                    p -= step;
                }
                vp[i] = p;
            }
        }

        static void Smooth(Vector3[] vp, List<int> tri, float k)
        {
            var sum = new Vector3[vp.Length];
            var cnt = new int[vp.Length];
            for (int i = 0; i < tri.Count; i += 3)
            {
                int a = tri[i], b = tri[i + 1], c = tri[i + 2];
                sum[a] += vp[b] + vp[c]; cnt[a] += 2;
                sum[b] += vp[a] + vp[c]; cnt[b] += 2;
                sum[c] += vp[a] + vp[b]; cnt[c] += 2;
            }
            for (int i = 0; i < vp.Length; i++) if (cnt[i] > 0) vp[i] = Vector3.Lerp(vp[i], sum[i] / cnt[i], k);
        }
        #endregion

        #region Atlas
        void BuildAtlas()
        {
            var wpos = weldPosTmp;
            int nw = wpos.Length;
            var wprim = new int[nw];
            var wnrm = new Vector3[nw];
            var wbw = new BoneWeight[nw];
            for (int i = 0; i < nw; i++)
            {
                wprim[i] = PrimaryBone(wpos[i]);
                wbw[i] = Weights(wpos[i], wprim[i]);
                wnrm[i] = MathUtil.SafeNormal(BodySdf.Gradient(prims, wpos[i], 0.002f), Vector3.up);
            }
            // charts
            for (int b = 0; b < PartInfo.Count; b++)
            {
                var c = new SkinChart();
                c.bone = b;
                PartId id = (PartId)b;
                c.origin = SkinSegA(b);
                c.axis = PartInfo.IsFoot(id) ? Vector3.forward : PartInfo.BoneAxis(id);
                if (PartInfo.IsFoot(id)) c.origin = new Vector3(PartInfo.SideSign(id) * 0.09f, 0.035f, -0.08f);
                c.r0 = PartInfo.IsArm(id) || PartInfo.IsFoot(id) ? Vector3.up : Vector3.forward;
                c.r1 = Vector3.Cross(c.axis, c.r0).normalized;
                charts[b] = c;
            }
            int ntri = weldTris.Length / 3;
            var triChart = new int[ntri];
            var radSum = new float[PartInfo.Count];
            var radCnt = new int[PartInfo.Count];
            for (int t = 0; t < ntri; t++)
            {
                int a = wprim[weldTris[t * 3]], b = wprim[weldTris[t * 3 + 1]], c = wprim[weldTris[t * 3 + 2]];
                int chart = a == b || a == c ? a : b == c ? b : a;
                triChart[t] = chart;
                var ch = charts[chart];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = wpos[weldTris[t * 3 + k]];
                    float tt = ch.T(p);
                    if (tt < ch.tmin) ch.tmin = tt;
                    if (tt > ch.tmax) ch.tmax = tt;
                    Vector3 q = p - ch.origin;
                    radSum[chart] += (q - ch.axis * Vector3.Dot(q, ch.axis)).magnitude;
                    radCnt[chart]++;
                }
            }
            for (int b = 0; b < PartInfo.Count; b++)
            {
                var ch = charts[b];
                if (ch.tmax < ch.tmin) { ch.tmin = 0f; ch.tmax = 0.01f; }
                ch.radius = radCnt[b] > 0 ? Mathf.Max(0.01f, radSum[b] / radCnt[b]) : 0.03f;
            }
            Pack();
            // render vertices: one per (weld, chart, wrap)
            var key = new Dictionary<long, int>(nw * 2);
            var lp = new List<Vector3>(nw * 2); var ln = new List<Vector3>(nw * 2); var luv = new List<Vector2>(nw * 2);
            var lbw = new List<BoneWeight>(nw * 2); var lw = new List<int>(nw * 2); var lprim = new List<int>(nw * 2);
            var outTris = new int[weldTris.Length];
            float S = atlasSize;
            for (int t = 0; t < ntri; t++)
            {
                var ch = charts[triChart[t]];
                float u0 = ch.U(wpos[weldTris[t * 3]]), u1 = ch.U(wpos[weldTris[t * 3 + 1]]), u2 = ch.U(wpos[weldTris[t * 3 + 2]]);
                float umax = Mathf.Max(u0, Mathf.Max(u1, u2)), umin = Mathf.Min(u0, Mathf.Min(u1, u2));
                bool wrap = umax - umin > 0.5f;
                for (int k = 0; k < 3; k++)
                {
                    int w = weldTris[t * 3 + k];
                    float u = k == 0 ? u0 : k == 1 ? u1 : u2;
                    int wf = wrap && u < 0.5f ? 1 : 0;
                    long kk = ((long)w << 8) | ((long)triChart[t] << 1) | (long)wf;
                    int idx;
                    if (!key.TryGetValue(kk, out idx))
                    {
                        idx = lp.Count;
                        key[kk] = idx;
                        lp.Add(wpos[w]); ln.Add(wnrm[w]); lbw.Add(wbw[w]); lw.Add(w); lprim.Add(wprim[w]);
                        Vector2 tx = ch.Texel(u + wf, ch.V(wpos[w]));
                        luv.Add(new Vector2(tx.x / S, tx.y / S));
                    }
                    outTris[t * 3 + k] = idx;
                }
            }
            pos = lp.ToArray(); nrm = ln.ToArray(); uv = luv.ToArray(); bw = lbw.ToArray(); weld = lw.ToArray(); primary = lprim.ToArray();
            tris = outTris;
            tan = ComputeTangents(pos, nrm, uv, tris);
            Rasterize(triChart);
            weldPosTmp = null;
            weldTris = null;
        }

        /// <summary>Shelf packing of the charts with the largest uniform texel density that fits.</summary>
        void Pack()
        {
            int S = atlasSize;
            float lo = 50f, hi = 3000f;
            for (int iter = 0; iter < 22; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                if (TryPack(mid, S, false)) lo = mid; else hi = mid;
            }
            TryPack(lo, S, true);
        }

        bool TryPack(float density, int S, bool commit)
        {
            int n = charts.Length;
            var order = new int[n];
            var ws = new int[n]; var hs = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                var c = charts[i];
                ws[i] = Mathf.CeilToInt(2f * Mathf.PI * c.radius * density * 1.08f) + c.pad * 2;
                hs[i] = Mathf.CeilToInt((c.tmax - c.tmin) * density) + c.pad * 2;
                if (ws[i] > S) return false;
            }
            Array.Sort(order, (a, b) => hs[b].CompareTo(hs[a]));
            int x = 0, y = 0, rowH = 0;
            var px = new int[n]; var py = new int[n];
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (x + ws[i] > S) { x = 0; y += rowH; rowH = 0; }
                if (y + hs[i] > S) return false;
                px[i] = x; py[i] = y;
                x += ws[i];
                rowH = Mathf.Max(rowH, hs[i]);
            }
            if (commit)
                for (int i = 0; i < n; i++) { charts[i].x = px[i]; charts[i].y = py[i]; charts[i].w = ws[i]; charts[i].h = hs[i]; }
            return true;
        }

        void Rasterize(int[] triChart)
        {
            int S = atlasSize;
            texPos = new Vector3[S * S];
            texChart = new sbyte[S * S];
            for (int i = 0; i < texChart.Length; i++) texChart[i] = -1;
            int ntri = tris.Length / 3;
            for (int t = 0; t < ntri; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                Vector2 ta = uv[a] * S, tb = uv[b] * S, tc = uv[c] * S;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ta.x, Mathf.Min(tb.x, tc.x)) - 1f)), x1 = Mathf.Min(S - 1, Mathf.CeilToInt(Mathf.Max(ta.x, Mathf.Max(tb.x, tc.x)) + 1f));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ta.y, Mathf.Min(tb.y, tc.y)) - 1f)), y1 = Mathf.Min(S - 1, Mathf.CeilToInt(Mathf.Max(ta.y, Mathf.Max(tb.y, tc.y)) + 1f));
                float den = (tb.y - tc.y) * (ta.x - tc.x) + (tc.x - tb.x) * (ta.y - tc.y);
                if (Mathf.Abs(den) < 1e-9f) continue;
                sbyte ch = (sbyte)triChart[t];
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float l0 = ((tb.y - tc.y) * (px - tc.x) + (tc.x - tb.x) * (py - tc.y)) / den;
                        float l1 = ((tc.y - ta.y) * (px - tc.x) + (ta.x - tc.x) * (py - tc.y)) / den;
                        float l2 = 1f - l0 - l1;
                        const float eps = -0.08f;
                        if (l0 < eps || l1 < eps || l2 < eps) continue;
                        int ti = y * S + x;
                        texPos[ti] = pos[a] * l0 + pos[b] * l1 + pos[c] * l2;
                        texChart[ti] = ch;
                    }
            }
            // dilate so bilinear filtering never samples unpainted gutter texels
            for (int pass = 0; pass < 3; pass++)
            {
                var src = (sbyte[])texChart.Clone();
                for (int y = 1; y < S - 1; y++)
                    for (int x = 1; x < S - 1; x++)
                    {
                        int i = y * S + x;
                        if (src[i] >= 0) continue;
                        int n = i - 1;
                        if (src[n] < 0) n = i + 1;
                        if (src[n] < 0) n = i - S;
                        if (src[n] < 0) n = i + S;
                        if (src[n] < 0) continue;
                        texChart[i] = src[n];
                        texPos[i] = texPos[n];
                    }
            }
        }

        public static Vector4[] ComputeTangents(Vector3[] p, Vector3[] n, Vector2[] t, int[] tri)
        {
            var tan1 = new Vector3[p.Length];
            var tan2 = new Vector3[p.Length];
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                int a = tri[i], b = tri[i + 1], c = tri[i + 2];
                Vector3 e1 = p[b] - p[a], e2 = p[c] - p[a];
                Vector2 d1 = t[b] - t[a], d2 = t[c] - t[a];
                float r = d1.x * d2.y - d2.x * d1.y;
                if (Mathf.Abs(r) < 1e-12f) continue;
                r = 1f / r;
                Vector3 sdir = (e1 * d2.y - e2 * d1.y) * r;
                Vector3 tdir = (e2 * d1.x - e1 * d2.x) * r;
                tan1[a] += sdir; tan1[b] += sdir; tan1[c] += sdir;
                tan2[a] += tdir; tan2[b] += tdir; tan2[c] += tdir;
            }
            var res = new Vector4[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                Vector3 nn = n[i];
                Vector3 tt = tan1[i] - nn * Vector3.Dot(nn, tan1[i]);
                tt = MathUtil.SafeNormal(tt, Vector3.Cross(nn, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(nn, Vector3.up).normalized : Vector3.right);
                float w = Vector3.Dot(Vector3.Cross(nn, tt), tan2[i]) < 0f ? -1f : 1f;
                res[i] = new Vector4(tt.x, tt.y, tt.z, w);
            }
            return res;
        }
        #endregion

        #region Textures
        void BuildTextures()
        {
            int S = atlasSize;
            bool clay = look == BodyLook.Clay;
            // height field -> tangent-space normal map
            var height = new float[S * S];
            var prints = FingerprintCenters();
            for (int i = 0; i < height.Length; i++)
            {
                if (texChart[i] < 0) continue;
                Vector3 p = texPos[i];
                float h;
                if (clay)
                {
                    h = Noise3.Fbm(p * 34f, 3) * 0.55f + Noise3.Value(p * 160f) * 0.12f;
                    for (int k = 0; k < prints.Length; k++)
                    {
                        Vector3 d = p - prints[k];
                        float dist = d.magnitude;
                        if (dist > 0.016f) continue;
                        float fade = 1f - dist / 0.016f;
                        float ridge = Mathf.Sin(dist * 2f * Mathf.PI / 0.0011f + Noise3.Value(p * 300f) * 2f);
                        h += ridge * 0.22f * fade * fade;
                    }
                }
                else h = Noise3.Value(p * 900f) * 0.25f + Noise3.Fbm(p * 60f, 2) * 0.2f;
                height[i] = h;
            }
            var npx = new Color32[S * S];
            float strength = clay ? 2.2f : 1.1f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    sbyte c = texChart[i];
                    float h0 = height[i];
                    float hx1 = x + 1 < S && texChart[i + 1] == c ? height[i + 1] : h0;
                    float hx0 = x > 0 && texChart[i - 1] == c ? height[i - 1] : h0;
                    float hy1 = y + 1 < S && texChart[i + S] == c ? height[i + S] : h0;
                    float hy0 = y > 0 && texChart[i - S] == c ? height[i - S] : h0;
                    Vector3 n = new Vector3(-(hx1 - hx0) * strength, -(hy1 - hy0) * strength, 1f).normalized;
                    npx[i] = new Color32((byte)(n.x * 127.5f + 127.5f), (byte)(n.y * 127.5f + 127.5f), (byte)(n.z * 127.5f + 127.5f), 255);
                }
            normalMap = new Texture2D(S, S, TextureFormat.RGBA32, true, true);
            normalMap.name = "Body normal (" + look + ")";
            normalMap.wrapMode = TextureWrapMode.Clamp;
            normalMap.filterMode = FilterMode.Trilinear;
            normalMap.anisoLevel = 4;
            normalMap.SetPixels32(npx);
            normalMap.Apply(true, true);
            capTexture = BuildCapTexture(clay);
            holeTexture = BuildHoleTexture(clay);
        }

        Vector3[] FingerprintCenters()
        {
            var rng = new Rng(4711 + (int)look);
            var list = new List<Vector3>(48);
            for (int i = 0; i < 60 && list.Count < 40; i++)
            {
                int v = rng.Range(0, pos.Length);
                list.Add(pos[v]);
            }
            return list.ToArray();
        }

        static Texture2D BuildCapTexture(bool clay)
        {
            const int S = 256;
            var px = new Color32[S * S];
            Color clayIn = new Color(0.86f, 0.56f, 0.46f), clayBand = new Color(0.72f, 0.42f, 0.34f), clayCore = new Color(0.93f, 0.7f, 0.58f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                    Color c;
                    float n = Noise3.Value(new Vector3(u * 40f, v * 40f, 0.5f)) * 0.5f + Noise3.Value(new Vector3(u * 140f, v * 140f, 2.5f)) * 0.25f;
                    if (u < 0.5f)
                    {
                        // flesh / clay interior
                        if (clay) c = clayIn * (0.9f + n * 0.2f);
                        else
                        {
                            float fib = Mathf.Abs(Mathf.Sin((u * 0.7f + v * 0.3f) * 180f + n * 6f));
                            c = Color.Lerp(new Color(0.55f, 0.06f, 0.07f), new Color(0.75f, 0.16f, 0.15f), fib * 0.6f + n * 0.4f);
                        }
                    }
                    else if (u < 0.75f)
                    {
                        // bone disc (centre 0.625, 0.5): cortical white ring + yellow marrow
                        float dx = (u - 0.625f) / 0.125f, dy = (v - 0.5f) / 0.5f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        if (clay) c = clayCore * (0.95f + n * 0.1f);
                        else c = r < 0.55f ? Color.Lerp(new Color(0.85f, 0.65f, 0.35f), new Color(0.75f, 0.25f, 0.2f), n) : new Color(0.93f, 0.9f, 0.82f) * (0.95f + n * 0.08f);
                    }
                    else
                    {
                        // band: skin/dermis (outer) -> fat -> muscle (inner)
                        float t = (u - 0.75f) / 0.25f;
                        if (clay) c = Color.Lerp(clayBand, clayIn, t) * (0.95f + n * 0.1f);
                        else c = t < 0.25f ? new Color(0.82f, 0.55f, 0.48f) : t < 0.7f ? new Color(0.95f, 0.85f, 0.55f) * (0.95f + n * 0.1f) : new Color(0.62f, 0.1f, 0.1f);
                    }
                    c.a = 1f;
                    px[y * S + x] = c;
                }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.name = "Cut cap";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D BuildHoleTexture(bool clay)
        {
            const int S = 64;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float v = (y + 0.5f) / S;
                    float n = Noise3.Value(new Vector3(x * 0.4f, y * 0.4f, 1.3f));
                    Color c = clay ? Color.Lerp(new Color(0.7f, 0.42f, 0.34f), new Color(0.28f, 0.14f, 0.11f), v) * (0.9f + n * 0.2f)
                                   : Color.Lerp(new Color(0.5f, 0.04f, 0.05f), new Color(0.08f, 0.0f, 0.01f), v) * (0.85f + n * 0.3f);
                    c.a = 1f;
                    px[y * S + x] = c;
                }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.name = "Hole";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }
        #endregion
    }

    /// <summary>Hash-based 3D value noise (deterministic, thread safe).</summary>
    public static class Noise3
    {
        static float Hash(int x, int y, int z)
        {
            uint h = MathUtil.Hash((uint)x * 73856093U ^ (uint)y * 19349663U ^ (uint)z * 83492791U);
            return (h & 0xFFFFFF) / 16777215f;
        }

        public static float Value(Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            float fx = p.x - x, fy = p.y - y, fz = p.z - z;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
            float a = Mathf.Lerp(Hash(x, y, z), Hash(x + 1, y, z), fx), b = Mathf.Lerp(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), fx);
            float c = Mathf.Lerp(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), fx), d = Mathf.Lerp(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(a, b, fy), Mathf.Lerp(c, d, fy), fz) * 2f - 1f;
        }

        public static float Fbm(Vector3 p, int octaves)
        {
            float s = 0f, amp = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++) { s += Value(p) * amp; norm += amp; p *= 2.03f; amp *= 0.5f; }
            return s / Mathf.Max(1e-4f, norm);
        }
    }
    #endregion

    #region Base paint
    /// <summary>Skin, clothes, face and hair painted from the template's position map, cached per appearance.</summary>
    public class BodyPaint
    {
        public Color32[] skin, cloth;
        public byte[] mask;       // 0 skin, 1 shirt, 2 pants, 3 shoes, 4 hair
    }

    public static class BodyPaintCache
    {
        static readonly Dictionary<string, BodyPaint> cache = new Dictionary<string, BodyPaint>();

        public static void Clear() { cache.Clear(); }

        public static BodyPaint Get(BodyTemplate t, CharacterSettings cs)
        {
            string key = t.look + "|" + t.atlasSize + "|" + cs.skinColor + cs.shirt + cs.shirtColor + cs.pants + cs.pantsColor + cs.shoes + cs.shoesColor + cs.hair + cs.hairColor + cs.skinVariation;
            BodyPaint p;
            if (cache.TryGetValue(key, out p)) return p;
            p = Paint(t, cs);
            if (cache.Count > 8) cache.Clear();
            cache[key] = p;
            return p;
        }

        static BodyPaint Paint(BodyTemplate t, CharacterSettings cs)
        {
            int S = t.atlasSize;
            var bp = new BodyPaint { skin = new Color32[S * S], cloth = new Color32[S * S], mask = new byte[S * S] };
            bool clay = t.look == BodyLook.Clay;
            Color skinC = cs.skinColor;
            for (int i = 0; i < S * S; i++)
            {
                int chart = t.texChart[i];
                if (chart < 0) { bp.skin[i] = skinC; bp.cloth[i] = skinC; continue; }
                Vector3 p = t.texPos[i];
                PartId bone = (PartId)chart;
                float n = Noise3.Fbm(p * 22f, 3);
                Color c = skinC * (1f + n * cs.skinVariation * 2.2f);
                if (!clay)
                {
                    // subtle realistic variation: redder knees/elbows/face, darker palms creases
                    float blush = 0f;
                    if (bone == PartId.Head && p.z > 0.04f) blush = Mathf.Clamp01(1f - ((p.y - 1.63f) * (p.y - 1.63f) + (Mathf.Abs(p.x) - 0.045f) * (Mathf.Abs(p.x) - 0.045f)) / 0.0006f) * 0.08f;
                    c = Color.Lerp(c, new Color(0.85f, 0.45f, 0.42f), blush);
                }
                c = PaintFace(c, p, bone, clay, cs);
                c.a = 1f;
                bp.skin[i] = c;
                byte m = 0;
                Color cl = c;
                if (cs.hair && IsHair(p, bone))
                {
                    m = 4;
                    float strand = clay ? Noise3.Fbm(p * 90f, 2) : Noise3.Value(new Vector3(p.x * 900f, p.y * 140f, p.z * 900f));
                    cl = cs.hairColor * (0.85f + strand * 0.25f);
                }
                else if (cs.shoes && IsShoe(p, bone))
                {
                    m = 3;
                    cl = cs.shoesColor * (0.92f + Noise3.Value(p * 300f) * 0.06f);
                    if (p.y < 0.012f) cl = clay ? cs.shoesColor * 0.6f : new Color(0.92f, 0.92f, 0.9f);
                    if (!clay && Mathf.Abs(p.y - 0.045f) < 0.004f) cl = new Color(0.95f, 0.95f, 0.95f);
                }
                else if (cs.pants && IsPants(p, bone))
                {
                    m = 2;
                    float weave = clay ? Noise3.Fbm(p * 60f, 2) * 0.06f : Noise3.Value(new Vector3(p.x * 1400f, p.y * 1400f, p.z * 1400f)) * 0.07f;
                    cl = cs.pantsColor * (0.94f + weave);
                    if (Mathf.Abs(p.y - 0.975f) < 0.012f) cl *= 0.7f; // belt line
                    if (PartInfo.IsLeg(bone) && p.y < 0.13f) cl *= 0.82f; // hem
                    if (!clay && bone == PartId.Pelvis && Mathf.Abs(p.x) < 0.004f && p.z > 0.05f) cl *= 0.8f; // fly seam
                }
                else if (cs.shirt && IsShirt(p, bone))
                {
                    m = 1;
                    float weave = clay ? Noise3.Fbm(p * 50f, 2) * 0.06f : Noise3.Value(p * 1200f) * 0.05f;
                    cl = cs.shirtColor * (0.95f + weave);
                    if (PartInfo.IsArm(bone) && Mathf.Abs(Mathf.Abs(p.x) - 0.34f) < 0.012f) cl *= 0.78f;   // sleeve hem
                    if (bone == PartId.Pelvis || (bone == PartId.Abdomen && p.y < 1.0f)) cl *= 0.85f;
                }
                cl.a = 1f;
                bp.cloth[i] = cl;
                bp.mask[i] = m;
            }
            return bp;
        }

        static bool IsShirt(Vector3 p, PartId b)
        {
            if (b == PartId.Abdomen) return true;
            if (b == PartId.Chest) return !(p.y > 1.43f - (p.z > 0.02f ? 0.035f : 0f) && Mathf.Abs(p.x) < 0.07f);
            if (b == PartId.Pelvis) return p.y > 0.985f;
            if (b == PartId.UpperArmL || b == PartId.UpperArmR) return Mathf.Abs(p.x) < 0.34f;
            if (b == PartId.Neck) return false;
            return false;
        }

        static bool IsPants(Vector3 p, PartId b)
        {
            if (b == PartId.Pelvis) return p.y <= 0.985f;
            if (b == PartId.Abdomen) return p.y < 0.985f;
            if (b == PartId.ThighL || b == PartId.ThighR) return true;
            if (b == PartId.ShinL || b == PartId.ShinR) return p.y > 0.115f;
            return false;
        }

        static bool IsShoe(Vector3 p, PartId b)
        {
            if (PartInfo.IsFoot(b)) return true;
            if (b == PartId.ShinL || b == PartId.ShinR) return p.y <= 0.115f;
            return false;
        }

        static bool IsHair(Vector3 p, PartId b)
        {
            if (b != PartId.Head && b != PartId.Neck) return false;
            float front = p.z;
            if (b == PartId.Neck) return p.y > 1.56f && front < -0.03f;
            if (p.y > 1.71f - Mathf.Max(0f, -front) * 0.4f && front < 0.075f) return true;
            if (p.y > 1.6f && front < -0.035f) return true;
            if (p.y > 1.64f && Mathf.Abs(p.x) > 0.07f && front < 0.02f) return true;
            return false;
        }

        /// <summary>Face: eyes, brows, nose shading and mouth. Clay: big glossy dot eyes and a simple smile.</summary>
        static Color PaintFace(Color c, Vector3 p, PartId bone, bool clay, CharacterSettings cs)
        {
            if (bone != PartId.Head || p.z < 0.03f) return c;
            float ax = Mathf.Abs(p.x);
            if (clay)
            {
                float ex = (ax - 0.032f) / 0.0115f, ey = (p.y - 1.666f) / 0.0125f;
                float e2 = ex * ex + ey * ey;
                if (e2 < 1f)
                {
                    float hx = (p.x - Mathf.Sign(p.x) * 0.032f - 0.004f) / 0.0035f, hy = (p.y - 1.67f) / 0.0035f;
                    return hx * hx + hy * hy < 1f ? new Color(0.98f, 0.98f, 0.98f) : new Color(0.06f, 0.05f, 0.05f);
                }
                // smile
                float mx = p.x / 0.026f;
                if (Mathf.Abs(mx) < 1f)
                {
                    float curve = 1.611f + 0.009f * mx * mx;
                    if (Mathf.Abs(p.y - curve) < 0.0024f) return new Color(0.35f, 0.12f, 0.1f);
                }
                // cheeks
                float cx = (ax - 0.05f) / 0.017f, cy = (p.y - 1.632f) / 0.012f;
                float cc = cx * cx + cy * cy;
                if (cc < 1f) return Color.Lerp(c, new Color(0.95f, 0.5f, 0.45f), (1f - cc) * 0.45f);
                return c;
            }
            // realistic
            float sx = (ax - 0.031f) / 0.0135f, sy = (p.y - 1.664f) / 0.0072f;
            float s2 = sx * sx + sy * sy;
            if (s2 < 1f)
            {
                float ix = (ax - 0.031f) / 0.0062f, iy = (p.y - 1.664f) / 0.0062f;
                float i2 = ix * ix + iy * iy;
                if (i2 < 0.2f) return new Color(0.04f, 0.03f, 0.03f);
                if (i2 < 1f) return Color.Lerp(cs.eyeColor, cs.eyeColor * 0.6f, i2);
                return new Color(0.94f, 0.93f, 0.9f);
            }
            if (s2 < 1.6f && p.y > 1.664f) return c * 0.72f; // upper lid line
            // brows
            float bx = (ax - 0.032f) / 0.019f;
            if (Mathf.Abs(bx) < 1f)
            {
                float by = 1.684f + 0.003f * (1f - bx * bx);
                if (Mathf.Abs(p.y - by) < 0.0028f * (1f - Mathf.Abs(bx) * 0.5f)) return cs.hairColor * 0.9f;
            }
            // lips
            float lx = p.x / 0.022f, ly = (p.y - 1.612f) / 0.0055f;
            float l2 = lx * lx + ly * ly;
            if (l2 < 1f) return Mathf.Abs(p.y - 1.612f) < 0.0007f ? new Color(0.42f, 0.18f, 0.17f) : Color.Lerp(c, new Color(0.72f, 0.36f, 0.34f), 0.65f);
            // nose shadow
            if (ax < 0.014f && p.y > 1.628f && p.y < 1.635f) return c * 0.86f;
            return c;
        }
    }
    #endregion
}
