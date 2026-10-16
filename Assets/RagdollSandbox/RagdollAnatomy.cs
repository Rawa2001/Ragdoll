// Ragdoll Sandbox - anatomy visuals.
//  * TubeShape: ring-based (lathe/superellipse) body part profiles with muscle bulges, generating smooth organic
//    meshes that can be cut at any ring and deformed (dents, gashes, crush, fracture kinks).
//  * PartVisual: the per-part SkinnedMeshRenderer (blended into neighbouring bones at the joints so the body reads
//    as one smooth mannequin), its deformation and X-ray state.
//  * SkinCanvas: per-part runtime Texture2D (skin + clothes + bruises + cuts + blood) with batched uploads.
//  * BodyMeshLibrary: procedural skeleton, arteries and organ meshes placed inside the right parts.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region TubeShape
    public struct ProfileKey
    {
        public float t, a, b, of, os, n;
        public ProfileKey(float t, float a, float b, float of = 0f, float os = 0f, float n = 2f) { this.t = t; this.a = a; this.b = b; this.of = of; this.os = os; this.n = n; }
    }

    public struct Bump
    {
        public float t, tw, ang, aw, amp;
        public Bump(float t, float tw, float ang, float aw, float amp) { this.t = t; this.tw = tw; this.ang = ang; this.aw = aw; this.amp = amp; }
    }

    /// <summary>Parametric description of one body part's surface in reference (1.75 m) part-local units.</summary>
    public class TubeShape
    {
        public PartId id;
        public Vector3 start, end, side, front;
        public ProfileKey[] keys;
        public Bump[] bumps = new Bump[0];
        public float capStart = 0.02f, capEnd = 0.02f;
        public float jointT0, jointT1 = 1f;
        public float blendParent, blendChild;   // blend lengths (0 = none)
        public float soleY = float.NegativeInfinity;
        public int rings = 20, segments = 24;
        public bool hasThumb;
        public Vector3 thumbBase, thumbTip;

        public float Length { get { return (end - start).magnitude; } }
        public Vector3 Axis { get { return (end - start).normalized; } }
        public float TotalArc { get { return capStart + Length + capEnd; } }

        public void Eval(float t, out float a, out float b, out float of, out float os, out float n)
        {
            t = Mathf.Clamp01(t);
            int k = 0;
            while (k < keys.Length - 2 && keys[k + 1].t < t) k++;
            ProfileKey p1 = keys[k], p2 = keys[Mathf.Min(k + 1, keys.Length - 1)];
            ProfileKey p0 = keys[Mathf.Max(k - 1, 0)], p3 = keys[Mathf.Min(k + 2, keys.Length - 1)];
            float span = Mathf.Max(1e-5f, p2.t - p1.t);
            float u = Mathf.Clamp01((t - p1.t) / span);
            a = CatmullRom(p0.a, p1.a, p2.a, p3.a, u);
            b = CatmullRom(p0.b, p1.b, p2.b, p3.b, u);
            of = CatmullRom(p0.of, p1.of, p2.of, p3.of, u);
            os = CatmullRom(p0.os, p1.os, p2.os, p3.os, u);
            n = Mathf.Lerp(p1.n, p2.n, u);
            a = Mathf.Max(0.002f, a); b = Mathf.Max(0.002f, b);
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        public float BumpAt(float t, float angDeg)
        {
            float s = 0f;
            for (int i = 0; i < bumps.Length; i++)
            {
                var bp = bumps[i];
                float dt = (t - bp.t) / Mathf.Max(0.001f, bp.tw);
                if (dt > 3f || dt < -3f) continue;
                float da = Mathf.DeltaAngle(angDeg, bp.ang) / Mathf.Max(1f, bp.aw);
                if (da > 3f || da < -3f) continue;
                s += bp.amp * Mathf.Exp(-0.5f * (dt * dt + da * da));
            }
            return s;
        }

        public float Area(float t)
        {
            float a, b, of, os, n;
            Eval(t, out a, out b, out of, out os, out n);
            return Mathf.PI * a * b;
        }

        /// <summary>Volume fraction of [0,t] (used to split mass when a part is cut).</summary>
        public float VolumeFraction(float t)
        {
            float total = 0f, part = 0f;
            const int steps = 40;
            for (int i = 0; i < steps; i++)
            {
                float tt = (i + 0.5f) / steps;
                float a = Area(tt);
                total += a;
                if (tt < t) part += a;
            }
            return total > 0f ? part / total : 0.5f;
        }

        public float Circumference(float t)
        {
            float a, b, of, os, n;
            Eval(t, out a, out b, out of, out os, out n);
            float h = (a - b) * (a - b) / ((a + b) * (a + b));
            return Mathf.PI * (a + b) * (1f + 3f * h / (10f + Mathf.Sqrt(4f - 3f * h)));
        }

        public Vector3 Center(float t)
        {
            float a, b, of, os, n;
            Eval(t, out a, out b, out of, out os, out n);
            return Vector3.Lerp(start, end, t) + front * of + side * os;
        }

        public float TOf(Vector3 refLocal)
        {
            float len = Length;
            return len > 1e-5f ? Vector3.Dot(refLocal - start, Axis) / len : 0.5f;
        }

        public float VOf(float t) { return Mathf.Clamp01((capStart + Mathf.Clamp01(t) * Length) / TotalArc); }

        /// <summary>Maps a reference-space local point to texture UV.</summary>
        public Vector2 UVOf(Vector3 refLocal, out float t)
        {
            t = TOf(refLocal);
            Vector3 c = Center(Mathf.Clamp01(t));
            Vector3 r = refLocal - c;
            float ang = Mathf.Atan2(Vector3.Dot(r, front), Vector3.Dot(r, side));
            if (ang < 0f) ang += Mathf.PI * 2f;
            return new Vector2(ang / (Mathf.PI * 2f), VOf(t));
        }

        /// <summary>Surface radius (from the centre line) at parameter t and angle.</summary>
        public float RadiusAt(float t, float angRad)
        {
            float a, b, of, os, n;
            Eval(t, out a, out b, out of, out os, out n);
            float ca = Mathf.Cos(angRad), sa = Mathf.Sin(angRad);
            float x = a * Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 2f / n), y = b * Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 2f / n);
            return Mathf.Sqrt(x * x + y * y);
        }

        public float MeanRadius(float t)
        {
            float a, b, of, os, n;
            Eval(t, out a, out b, out of, out os, out n);
            return (a + b) * 0.5f;
        }

        public static TubeShape For(PartId id)
        {
            bool right = PartInfo.IsRight(id);
            PartId baseId = right ? PartInfo.Mirror(id) : id;
            var s = Create(baseId);
            s.id = id;
            if (right) s.MirrorX();
            return s;
        }

        void MirrorX()
        {
            start.x = -start.x; end.x = -end.x;
            thumbBase.x = -thumbBase.x; thumbTip.x = -thumbTip.x;
            if (Mathf.Abs(side.x) > 0.5f)
            {
                // Lateral axes: keep +X but mirror bump angles (medial <-> lateral) and side offsets.
                for (int i = 0; i < bumps.Length; i++) bumps[i].ang = 180f - bumps[i].ang;
                for (int i = 0; i < keys.Length; i++) keys[i].os = -keys[i].os;
            }
        }

        static Vector3 L(PartId id, float x, float y, float z) { return new Vector3(x, y, z) - PartInfo.Origin[(int)id]; }

        static TubeShape Create(PartId id)
        {
            var s = new TubeShape();
            s.id = id;
            switch (id)
            {
                case PartId.Pelvis:
                    s.start = L(id, 0f, 0.80f, 0f); s.end = L(id, 0f, 1.075f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0.00f, 0.085f, 0.066f, 0.006f, 0f, 2.2f), new ProfileKey(0.12f, 0.136f, 0.092f, 0.002f, 0f, 2.3f),
                        new ProfileKey(0.30f, 0.168f, 0.106f, -0.004f, 0f, 2.4f), new ProfileKey(0.45f, 0.176f, 0.110f, -0.006f, 0f, 2.5f),
                        new ProfileKey(0.62f, 0.170f, 0.108f, -0.006f, 0f, 2.5f), new ProfileKey(0.80f, 0.151f, 0.101f, -0.002f, 0f, 2.4f),
                        new ProfileKey(1.00f, 0.141f, 0.098f, 0f, 0f, 2.3f) };
                    s.bumps = new[] {
                        new Bump(0.40f, 0.16f, 298f, 32f, 0.022f), new Bump(0.40f, 0.16f, 242f, 32f, 0.022f), new Bump(0.38f, 0.2f, 270f, 6f, -0.008f),
                        new Bump(0.80f, 0.2f, 90f, 50f, 0.006f), new Bump(0.12f, 0.07f, 90f, 22f, 0.01f) };
                    s.capStart = 0.035f; s.capEnd = 0.02f;
                    s.jointT0 = 0.545f; s.jointT1 = 0.836f; s.blendChild = 0.05f;
                    s.rings = 16; s.segments = 28;
                    break;
                case PartId.Abdomen:
                    s.start = L(id, 0f, 0.995f, 0f); s.end = L(id, 0f, 1.245f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.148f, 0.099f, 0f, 0f, 2.3f), new ProfileKey(0.14f, 0.144f, 0.098f, 0f, 0f, 2.3f),
                        new ProfileKey(0.4f, 0.136f, 0.097f, 0.004f, 0f, 2.2f), new ProfileKey(0.65f, 0.139f, 0.099f, 0.003f, 0f, 2.2f),
                        new ProfileKey(0.82f, 0.145f, 0.103f, 0.002f, 0f, 2.3f), new ProfileKey(1f, 0.150f, 0.107f, 0.002f, 0f, 2.4f) };
                    s.bumps = new[] {
                        new Bump(0.55f, 0.3f, 90f, 30f, 0.005f), new Bump(0.25f, 0.2f, 0f, 30f, 0.004f), new Bump(0.25f, 0.2f, 180f, 30f, 0.004f),
                        new Bump(0.5f, 0.6f, 270f, 8f, -0.006f), new Bump(0.6f, 0.05f, 90f, 8f, -0.003f) };
                    s.capStart = 0.02f; s.capEnd = 0.02f;
                    s.jointT0 = 0.14f; s.jointT1 = 0.82f; s.blendParent = 0.05f; s.blendChild = 0.05f;
                    s.rings = 15; s.segments = 28;
                    break;
                case PartId.Chest:
                    s.start = L(id, 0f, 1.15f, 0f); s.end = L(id, 0f, 1.505f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.146f, 0.103f, 0f, 0f, 2.3f), new ProfileKey(0.14f, 0.151f, 0.107f, 0.002f, 0f, 2.3f),
                        new ProfileKey(0.33f, 0.160f, 0.116f, 0.005f, 0f, 2.4f), new ProfileKey(0.52f, 0.168f, 0.120f, 0.006f, 0f, 2.5f),
                        new ProfileKey(0.68f, 0.184f, 0.113f, 0f, 0f, 2.7f), new ProfileKey(0.79f, 0.170f, 0.097f, -0.012f, 0f, 2.8f),
                        new ProfileKey(0.89f, 0.115f, 0.075f, -0.016f, 0f, 2.5f), new ProfileKey(1f, 0.064f, 0.058f, -0.010f, 0f, 2.2f) };
                    s.bumps = new[] {
                        new Bump(0.55f, 0.12f, 58f, 30f, 0.016f), new Bump(0.55f, 0.12f, 122f, 30f, 0.016f),
                        new Bump(0.6f, 0.12f, 235f, 25f, 0.008f), new Bump(0.6f, 0.12f, 305f, 25f, 0.008f),
                        new Bump(0.45f, 0.2f, 200f, 25f, 0.008f), new Bump(0.45f, 0.2f, 340f, 25f, 0.008f),
                        new Bump(0.85f, 0.08f, 270f, 60f, 0.012f), new Bump(0.5f, 0.2f, 90f, 6f, -0.004f) };
                    s.capStart = 0.02f; s.capEnd = 0.015f;
                    s.jointT0 = 0.141f; s.jointT1 = 0.901f;
                    s.rings = 22; s.segments = 28;
                    break;
                case PartId.Neck:
                    s.start = L(id, 0f, 1.435f, 0f); s.end = L(id, 0f, 1.625f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.062f, 0.058f, -0.014f), new ProfileKey(0.25f, 0.057f, 0.054f, -0.006f),
                        new ProfileKey(0.6f, 0.053f, 0.052f, 0f), new ProfileKey(1f, 0.050f, 0.050f, 0.004f) };
                    s.bumps = new[] { new Bump(0.4f, 0.3f, 60f, 20f, 0.004f), new Bump(0.4f, 0.3f, 120f, 20f, 0.004f), new Bump(0.55f, 0.08f, 90f, 14f, 0.004f) };
                    s.capStart = 0.015f; s.capEnd = 0.015f;
                    s.jointT0 = 0.184f; s.jointT1 = 0.711f; s.blendParent = 0.04f; s.blendChild = 0.04f;
                    s.rings = 11; s.segments = 22;
                    break;
                case PartId.Head:
                    s.start = L(id, 0f, 1.525f, 0f); s.end = L(id, 0f, 1.722f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.034f, 0.034f, 0.058f, 0f, 2f), new ProfileKey(0.08f, 0.050f, 0.058f, 0.044f, 0f, 2.1f),
                        new ProfileKey(0.20f, 0.062f, 0.078f, 0.022f, 0f, 2.1f), new ProfileKey(0.36f, 0.068f, 0.090f, 0.012f, 0f, 2.2f),
                        new ProfileKey(0.52f, 0.074f, 0.098f, 0.006f, 0f, 2.2f), new ProfileKey(0.68f, 0.077f, 0.100f, 0f, 0f, 2.1f),
                        new ProfileKey(0.84f, 0.073f, 0.095f, -0.004f, 0f, 2f), new ProfileKey(1f, 0.060f, 0.077f, -0.006f, 0f, 2f) };
                    s.bumps = new[] {
                        new Bump(0.40f, 0.06f, 90f, 10f, 0.018f), new Bump(0.60f, 0.05f, 90f, 45f, 0.006f),
                        new Bump(0.53f, 0.04f, 66f, 10f, -0.006f), new Bump(0.53f, 0.04f, 114f, 10f, -0.006f),
                        new Bump(0.47f, 0.06f, 42f, 16f, 0.005f), new Bump(0.47f, 0.06f, 138f, 16f, 0.005f),
                        new Bump(0.50f, 0.07f, 0f, 12f, 0.013f), new Bump(0.50f, 0.07f, 180f, 12f, 0.013f),
                        new Bump(0.62f, 0.2f, 270f, 50f, 0.008f), new Bump(0.27f, 0.04f, 90f, 18f, 0.004f), new Bump(0.06f, 0.05f, 90f, 25f, 0.004f) };
                    s.capStart = 0.012f; s.capEnd = 0.034f;
                    s.jointT0 = 0.228f; s.jointT1 = 1f; s.blendParent = 0.03f;
                    s.rings = 16; s.segments = 30;
                    break;
                case PartId.UpperArmL:
                    s.start = L(id, -0.13f, 1.42f, 0f); s.end = L(id, -0.49f, 1.42f, 0f);
                    s.side = Vector3.up; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.060f, 0.062f), new ProfileKey(0.15f, 0.062f, 0.064f), new ProfileKey(0.30f, 0.055f, 0.058f),
                        new ProfileKey(0.55f, 0.048f, 0.052f, 0.002f), new ProfileKey(0.80f, 0.042f, 0.045f), new ProfileKey(0.93f, 0.040f, 0.043f),
                        new ProfileKey(1f, 0.038f, 0.040f) };
                    s.bumps = new[] {
                        new Bump(0.2f, 0.14f, 0f, 70f, 0.012f), new Bump(0.18f, 0.1f, 60f, 30f, 0.006f), new Bump(0.55f, 0.2f, 90f, 40f, 0.008f),
                        new Bump(0.45f, 0.25f, 270f, 45f, 0.008f), new Bump(0.93f, 0.05f, 270f, 25f, 0.006f) };
                    s.capStart = 0.03f; s.capEnd = 0.02f;
                    s.jointT0 = 0.153f; s.jointT1 = 0.931f; s.blendParent = 0.03f; s.blendChild = 0.04f;
                    s.rings = 22; s.segments = 22;
                    break;
                case PartId.ForearmL:
                    s.start = L(id, -0.44f, 1.42f, 0f); s.end = L(id, -0.735f, 1.42f, 0f);
                    s.side = Vector3.up; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.040f, 0.042f), new ProfileKey(0.085f, 0.041f, 0.044f), new ProfileKey(0.28f, 0.042f, 0.046f, 0.002f),
                        new ProfileKey(0.55f, 0.033f, 0.038f), new ProfileKey(0.85f, 0.021f, 0.030f), new ProfileKey(0.93f, 0.019f, 0.028f),
                        new ProfileKey(1f, 0.018f, 0.026f) };
                    s.bumps = new[] { new Bump(0.25f, 0.15f, 45f, 30f, 0.006f), new Bump(0.3f, 0.2f, 180f, 50f, 0.005f), new Bump(0.5f, 0.4f, 270f, 10f, 0.002f) };
                    s.capStart = 0.02f; s.capEnd = 0.012f;
                    s.jointT0 = 0.085f; s.jointT1 = 0.932f; s.blendParent = 0.04f; s.blendChild = 0.025f;
                    s.rings = 18; s.segments = 20;
                    break;
                case PartId.HandL:
                    s.start = L(id, -0.705f, 1.42f, 0f); s.end = L(id, -0.9f, 1.42f, 0f);
                    s.side = Vector3.up; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.018f, 0.026f, 0f, 0f, 2.6f), new ProfileKey(0.12f, 0.017f, 0.034f, 0f, 0f, 2.8f),
                        new ProfileKey(0.35f, 0.0165f, 0.042f, 0f, 0f, 3f), new ProfileKey(0.55f, 0.0145f, 0.041f, 0f, 0f, 3f),
                        new ProfileKey(0.78f, 0.0115f, 0.038f, 0f, 0f, 2.8f), new ProfileKey(1f, 0.0085f, 0.031f, 0f, 0f, 2.6f) };
                    s.bumps = new[] { new Bump(0.55f, 0.05f, 0f, 70f, 0.003f), new Bump(0.3f, 0.15f, 180f, 60f, 0.003f) };
                    s.capStart = 0.012f; s.capEnd = 0.012f;
                    s.jointT0 = 0.051f; s.jointT1 = 1f; s.blendParent = 0.02f;
                    s.rings = 12; s.segments = 20;
                    s.hasThumb = true;
                    s.thumbBase = L(id, -0.735f, 1.413f, 0.032f); s.thumbTip = L(id, -0.795f, 1.405f, 0.07f);
                    break;
                case PartId.ThighL:
                    s.start = L(id, -0.09f, 0.975f, 0f); s.end = L(id, -0.09f, 0.44f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.072f, 0.078f), new ProfileKey(0.14f, 0.088f, 0.091f, 0.004f), new ProfileKey(0.30f, 0.085f, 0.088f, 0.008f),
                        new ProfileKey(0.55f, 0.076f, 0.080f, 0.010f), new ProfileKey(0.80f, 0.062f, 0.064f, 0.008f), new ProfileKey(0.925f, 0.056f, 0.058f, 0.010f),
                        new ProfileKey(1f, 0.052f, 0.055f, 0.008f) };
                    s.bumps = new[] {
                        new Bump(0.45f, 0.25f, 90f, 45f, 0.008f), new Bump(0.8f, 0.08f, 40f, 25f, 0.008f), new Bump(0.5f, 0.25f, 150f, 35f, 0.006f),
                        new Bump(0.42f, 0.25f, 270f, 50f, 0.007f), new Bump(0.2f, 0.15f, 0f, 40f, 0.004f) };
                    s.capStart = 0.035f; s.capEnd = 0.02f;
                    s.jointT0 = 0.14f; s.jointT1 = 0.925f; s.blendParent = 0.05f; s.blendChild = 0.045f;
                    s.rings = 30; s.segments = 24;
                    break;
                case PartId.ShinL:
                    s.start = L(id, -0.09f, 0.53f, 0f); s.end = L(id, -0.09f, 0.045f, 0f);
                    s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.054f, 0.056f), new ProfileKey(0.103f, 0.056f, 0.057f, 0.006f), new ProfileKey(0.30f, 0.057f, 0.061f, -0.008f),
                        new ProfileKey(0.45f, 0.053f, 0.056f, -0.006f), new ProfileKey(0.70f, 0.040f, 0.042f, -0.002f), new ProfileKey(0.90f, 0.034f, 0.036f),
                        new ProfileKey(1f, 0.033f, 0.034f) };
                    s.bumps = new[] {
                        new Bump(0.3f, 0.14f, 300f, 30f, 0.012f), new Bump(0.27f, 0.13f, 240f, 28f, 0.010f), new Bump(0.1f, 0.04f, 90f, 30f, 0.008f),
                        new Bump(0.5f, 0.4f, 95f, 8f, 0.002f), new Bump(0.92f, 0.03f, 0f, 20f, 0.006f), new Bump(0.94f, 0.03f, 180f, 20f, 0.006f) };
                    s.capStart = 0.02f; s.capEnd = 0.012f;
                    s.jointT0 = 0.103f; s.jointT1 = 0.938f; s.blendParent = 0.045f; s.blendChild = 0.025f;
                    s.rings = 28; s.segments = 22;
                    break;
                case PartId.FootL:
                    s.start = L(id, -0.09f, 0.03f, -0.072f); s.end = L(id, -0.09f, 0.03f, 0.205f);
                    s.side = Vector3.right; s.front = Vector3.up;
                    s.keys = new[] {
                        new ProfileKey(0f, 0.028f, 0.026f, 0.006f), new ProfileKey(0.10f, 0.034f, 0.036f, 0.012f), new ProfileKey(0.26f, 0.036f, 0.048f, 0.024f),
                        new ProfileKey(0.45f, 0.042f, 0.040f, 0.014f, 0.004f), new ProfileKey(0.70f, 0.048f, 0.026f, 0f, 0.006f),
                        new ProfileKey(0.88f, 0.046f, 0.019f, -0.008f, 0.004f), new ProfileKey(1f, 0.040f, 0.014f, -0.012f, 0.002f) };
                    s.bumps = new[] { new Bump(0.9f, 0.06f, 70f, 25f, 0.003f) };
                    s.capStart = 0.015f; s.capEnd = 0.012f;
                    s.jointT0 = 0.26f; s.jointT1 = 1f; s.blendParent = 0.03f;
                    s.soleY = 0.003f - PartInfo.Origin[(int)id].y;
                    s.rings = 16; s.segments = 20;
                    break;
                default:
                    s.start = Vector3.zero; s.end = Vector3.up * 0.1f; s.side = Vector3.right; s.front = Vector3.forward;
                    s.keys = new[] { new ProfileKey(0f, 0.05f, 0.05f), new ProfileKey(1f, 0.05f, 0.05f) };
                    break;
            }
            return s;
        }
    }

    /// <summary>A recorded deformation, replayed when a part's mesh is regenerated after a cut.</summary>
    public struct DeformOp
    {
        public int kind;          // 0 dent, 1 gash, 2 flatten, 3 kink, 4 swell
        public Vector3 point;     // reference-space local
        public Vector3 dir;
        public Vector3 axis;
        public float radius, amount, length;
    }
    #endregion

    #region PartVisual
    /// <summary>Owns the skinned ring mesh of one piece, its deformation state, skin canvas and X-ray materials.</summary>
    public class PartVisual
    {
        public BodyPart part;
        public TubeShape shape;
        public SkinCanvas canvas;
        public float t0, t1;
        public bool cutStart, cutEnd;
        public SkinnedMeshRenderer smr;
        public Mesh mesh;
        public Material skinMat, capMat, xrayMat;
        public GameObject skinGO;
        public Transform parentBone, childBone;
        public PartId parentBoneId = PartId.Count, childBoneId = PartId.Count;
        public readonly List<DeformOp> ops = new List<DeformOp>();
        public bool sharedCanvas;

        Vector3[] baseVerts, verts, normals;
        float[] accum;
        BoneWeight[] weights;
        int ringCount, segs, gridVerts;
        Vector3[] ringCenter;
        float[] ringT, ringAxial;
        float scale;
        bool xray;
        float highlight;
        Material[] matsNormal = new Material[2], matsXray = new Material[2];

        public float Scale { get { return scale; } }

        public static PartVisual Create(BodyPart part, TubeShape shape, SkinCanvas canvas, Material skin, float t0, float t1, bool cutStart, bool cutEnd)
        {
            var v = new PartVisual();
            v.part = part; v.shape = shape; v.canvas = canvas; v.t0 = t0; v.t1 = t1; v.cutStart = cutStart; v.cutEnd = cutEnd;
            v.scale = part.body.Scale;
            v.skinMat = skin;
            v.capMat = PartInfo.IsTorso(part.id) ? MaterialFactory.CapTorso : MaterialFactory.CapLimb;
            v.skinGO = new GameObject("Skin");
            v.skinGO.transform.SetParent(part.transform, false);
            v.smr = v.skinGO.AddComponent<SkinnedMeshRenderer>();
            v.smr.shadowCastingMode = ShadowCastingMode.On;
            v.smr.receiveShadows = true;
            v.smr.updateWhenOffscreen = false;
            v.smr.skinnedMotionVectors = false;
            v.Generate();
            return v;
        }

        /// <summary>Visual for a piece cut from 'src': shares its canvas and material, replays its deformations.</summary>
        public static PartVisual CreateFrom(BodyPart piece, PartVisual src, float t0, float t1, bool cutStart, bool cutEnd)
        {
            var v = new PartVisual();
            v.part = piece; v.shape = src.shape; v.canvas = src.canvas; v.t0 = t0; v.t1 = t1; v.cutStart = cutStart; v.cutEnd = cutEnd;
            v.scale = piece.body.Scale;
            v.skinMat = src.skinMat;
            v.capMat = src.capMat;
            v.sharedCanvas = true;
            v.ops.AddRange(src.ops);
            v.skinGO = new GameObject("Skin");
            v.skinGO.transform.SetParent(piece.transform, false);
            v.smr = v.skinGO.AddComponent<SkinnedMeshRenderer>();
            v.smr.shadowCastingMode = ShadowCastingMode.On;
            v.smr.receiveShadows = true;
            v.smr.updateWhenOffscreen = false;
            v.smr.skinnedMotionVectors = false;
            v.Generate();
            return v;
        }

        public void SetBlend(Transform parentT, PartId parentId, Transform childT, PartId childId)
        {
            parentBone = parentT; parentBoneId = parentT != null ? parentId : PartId.Count;
            childBone = childT; childBoneId = childT != null ? childId : PartId.Count;
            ComputeWeights();
            ApplyBones();
        }

        public void ClearBlendTo(Transform t)
        {
            bool changed = false;
            if (t != null && parentBone == t) { parentBone = null; parentBoneId = PartId.Count; changed = true; }
            if (t != null && childBone == t) { childBone = null; childBoneId = PartId.Count; changed = true; }
            if (changed) { ComputeWeights(); ApplyBones(); }
        }

        public void ClearAllBlend()
        {
            parentBone = null; childBone = null; parentBoneId = PartId.Count; childBoneId = PartId.Count;
            ComputeWeights(); ApplyBones();
        }

        /// <summary>Builds the ring grid for [t0,t1], rounded ends unless cut, plus a flat cross-section cap on cut ends.</summary>
        public void Generate()
        {
            var s = shape;
            float len = s.Length;
            Vector3 axis = s.Axis;
            int capRings = 5;
            int mainRings = Mathf.Max(3, Mathf.RoundToInt(s.rings * Mathf.Max(0.15f, t1 - t0)) + 1);
            segs = Mathf.Max(8, s.segments);
            var rc = new List<float>(64);    // ring t
            var rk = new List<float>(64);    // ring scale factor
            var ro = new List<float>(64);    // axial offset beyond the ring t
            if (!cutStart)
                for (int k = capRings; k >= 1; k--)
                {
                    float phi = k / (float)capRings * Mathf.PI * 0.5f;
                    rc.Add(t0); rk.Add(Mathf.Cos(phi)); ro.Add(-s.capStart * Mathf.Sin(phi));
                }
            for (int i = 0; i < mainRings; i++) { rc.Add(Mathf.Lerp(t0, t1, i / (float)(mainRings - 1))); rk.Add(1f); ro.Add(0f); }
            if (!cutEnd)
                for (int k = 1; k <= capRings; k++)
                {
                    float phi = k / (float)capRings * Mathf.PI * 0.5f;
                    rc.Add(t1); rk.Add(Mathf.Cos(phi)); ro.Add(s.capEnd * Mathf.Sin(phi));
                }
            ringCount = rc.Count;
            int row = segs + 1;
            gridVerts = ringCount * row;
            int capVerts = (cutStart ? row + 1 : 0) + (cutEnd ? row + 1 : 0);
            int thumbLat = 6, thumbLon = 10;
            int thumbVerts = s.hasThumb ? (thumbLat + 1) * (thumbLon + 1) : 0;
            int total = gridVerts + capVerts + thumbVerts;
            baseVerts = new Vector3[total];
            var uvs = new Vector2[total];
            ringCenter = new Vector3[ringCount];
            ringT = new float[ringCount];
            ringAxial = new float[ringCount];
            float totalArc = s.TotalArc;
            for (int r = 0; r < ringCount; r++)
            {
                float t = rc[r], k = rk[r], off = ro[r];
                float a, b, of, os, n;
                s.Eval(t, out a, out b, out of, out os, out n);
                Vector3 c = s.start + axis * (t * len + off) + s.front * of + s.side * os;
                ringCenter[r] = c;
                ringT[r] = t;
                ringAxial[r] = t * len + off;
                float v = Mathf.Clamp01((s.capStart + t * len + off) / totalArc);
                for (int j = 0; j <= segs; j++)
                {
                    float ang = j / (float)segs * Mathf.PI * 2f;
                    float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                    float x = a * Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 2f / n);
                    float y = b * Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 2f / n);
                    float bump = s.BumpAt(t, ang * Mathf.Rad2Deg);
                    float rl = Mathf.Sqrt(x * x + y * y);
                    if (rl > 1e-6f) { x += x / rl * bump; y += y / rl * bump; }
                    Vector3 p = c + s.side * (x * k) + s.front * (y * k);
                    if (!float.IsNegativeInfinity(s.soleY) && p.y < s.soleY) p.y = s.soleY;
                    baseVerts[r * row + j] = p;
                    uvs[r * row + j] = new Vector2(j / (float)segs, v);
                }
            }
            int vi = gridVerts;
            int capStartBase = -1, capEndBase = -1;
            if (cutStart) { capStartBase = vi; vi = AddCapVerts(0, vi, uvs); }
            if (cutEnd) { capEndBase = vi; vi = AddCapVerts(ringCount - 1, vi, uvs); }
            int thumbBase = -1;
            if (s.hasThumb)
            {
                thumbBase = vi;
                Vector3 dir = (s.thumbTip - s.thumbBase).normalized;
                Vector3 centre = (s.thumbBase + s.thumbTip) * 0.5f;
                float half = (s.thumbTip - s.thumbBase).magnitude * 0.5f + 0.008f;
                Quaternion q = Quaternion.LookRotation(dir, Vector3.up);
                Vector3 rad = new Vector3(0.011f, 0.0105f, half);
                float tm = s.TOf(centre);
                for (int i = 0; i <= thumbLat; i++)
                {
                    float la = -Mathf.PI * 0.5f + Mathf.PI * i / thumbLat;
                    for (int j = 0; j <= thumbLon; j++)
                    {
                        float lo = j / (float)thumbLon * Mathf.PI * 2f;
                        Vector3 unit = new Vector3(Mathf.Cos(lo) * Mathf.Cos(la), Mathf.Sin(lo) * Mathf.Cos(la), Mathf.Sin(la));
                        baseVerts[vi] = centre + q * Vector3.Scale(unit, rad);
                        uvs[vi] = new Vector2(0.25f, s.VOf(tm));
                        vi++;
                    }
                }
            }
            for (int i = 0; i < baseVerts.Length; i++) baseVerts[i] *= scale;
            for (int r = 0; r < ringCount; r++) ringCenter[r] *= scale;

            // Triangles
            var tris0 = new List<int>(ringCount * segs * 6 + thumbVerts * 6);
            for (int r = 0; r < ringCount - 1; r++)
                for (int j = 0; j < segs; j++)
                {
                    int a0 = r * row + j, b0 = (r + 1) * row + j;
                    tris0.Add(a0); tris0.Add(b0); tris0.Add(b0 + 1);
                    tris0.Add(a0); tris0.Add(b0 + 1); tris0.Add(a0 + 1);
                }
            if (OrientationFlipped(row)) FlipTriangles(tris0);
            if (thumbBase >= 0)
            {
                int tr = thumbLon + 1;
                for (int i = 0; i < thumbLat; i++)
                    for (int j = 0; j < thumbLon; j++)
                    {
                        int a0 = thumbBase + i * tr + j, c0 = a0 + tr;
                        tris0.Add(a0); tris0.Add(c0 + 1); tris0.Add(c0);
                        tris0.Add(a0); tris0.Add(a0 + 1); tris0.Add(c0 + 1);
                    }
            }
            var tris1 = new List<int>(segs * 6);
            if (capStartBase >= 0) AddCapTris(tris1, capStartBase, row, false);
            if (capEndBase >= 0) AddCapTris(tris1, capEndBase, row, true);

            verts = (Vector3[])baseVerts.Clone();
            accum = new float[verts.Length];
            normals = new Vector3[verts.Length];
            if (mesh == null)
            {
                mesh = new Mesh();
                mesh.name = PartInfo.Names[(int)part.id] + " skin";
                mesh.MarkDynamic();
            }
            else mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(tris0, 0, false);
            mesh.SetTriangles(tris1, 1, false);
            weights = new BoneWeight[verts.Length];
            for (int i = 0; i < ops.Count; i++) ApplyOp(ops[i], false);
            RecomputeNormals();
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            ComputeWeights();
            matsNormal[0] = skinMat; matsNormal[1] = capMat;
            ApplyBones();
            ApplyMaterials();
        }

        int AddCapVerts(int ring, int vi, Vector2[] uvs)
        {
            int row = segs + 1;
            Vector3 c = ringCenter[ring];
            baseVerts[vi] = c;
            uvs[vi] = new Vector2(0.5f, 0.5f);
            vi++;
            float maxR = 1e-4f;
            for (int j = 0; j <= segs; j++) maxR = Mathf.Max(maxR, (baseVerts[ring * row + j] - c).magnitude);
            for (int j = 0; j <= segs; j++)
            {
                Vector3 p = baseVerts[ring * row + j];
                baseVerts[vi] = p;
                Vector3 d = p - c;
                uvs[vi] = new Vector2(0.5f + 0.48f * Vector3.Dot(d, shape.side) / maxR, 0.5f + 0.48f * Vector3.Dot(d, shape.front) / maxR);
                vi++;
            }
            return vi;
        }

        void AddCapTris(List<int> tris, int baseIdx, int row, bool atEnd)
        {
            // cap normal must face outward: +axis at the end, -axis at the start
            Vector3 axis = shape.Axis * (atEnd ? 1f : -1f);
            Vector3 c = baseVerts[baseIdx], p0 = baseVerts[baseIdx + 1], p1 = baseVerts[baseIdx + 2];
            bool flip = Vector3.Dot(Vector3.Cross(p0 - c, p1 - c), axis) < 0f;
            for (int j = 0; j < segs; j++)
            {
                int a = baseIdx + 1 + j, b = baseIdx + 2 + j;
                if (flip) { tris.Add(baseIdx); tris.Add(b); tris.Add(a); }
                else { tris.Add(baseIdx); tris.Add(a); tris.Add(b); }
            }
        }

        bool OrientationFlipped(int row)
        {
            int r = ringCount / 2;
            int a0 = r * row, b0 = (r + 1) * row;
            Vector3 n = Vector3.Cross(baseVerts[b0] - baseVerts[a0], baseVerts[b0 + 1] - baseVerts[a0]);
            Vector3 outward = baseVerts[a0] - ringCenter[r];
            return Vector3.Dot(n, outward) < 0f;
        }

        static void FlipTriangles(List<int> t)
        {
            for (int i = 0; i < t.Count; i += 3) { int x = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = x; }
        }

        void RecomputeNormals()
        {
            int row = segs + 1;
            Vector3 axis = shape.Axis;
            for (int r = 0; r < ringCount; r++)
            {
                int rp = Mathf.Max(0, r - 1), rn = Mathf.Min(ringCount - 1, r + 1);
                for (int j = 0; j < segs; j++)
                {
                    int jp = (j - 1 + segs) % segs, jn = (j + 1) % segs;
                    Vector3 du = verts[r * row + jn] - verts[r * row + jp];
                    Vector3 dv = verts[rn * row + j] - verts[rp * row + j];
                    Vector3 n = Vector3.Cross(dv, du);
                    Vector3 outward = verts[r * row + j] - ringCenter[r];
                    if (n.sqrMagnitude < 1e-12f)
                    {
                        n = r < ringCount / 2 ? -axis : axis;
                        if (outward.sqrMagnitude > 1e-10f) n = (n + outward.normalized).normalized;
                    }
                    else
                    {
                        n.Normalize();
                        if (Vector3.Dot(n, outward) < 0f) n = -n;
                    }
                    normals[r * row + j] = n;
                }
                normals[r * row + segs] = normals[r * row];
            }
            // Pole rings: point along the axis
            if (!cutStart) for (int j = 0; j <= segs; j++) normals[j] = -axis;
            if (!cutEnd) for (int j = 0; j <= segs; j++) normals[(ringCount - 1) * row + j] = axis;
            int vi = gridVerts;
            if (cutStart) { for (int k = 0; k <= segs + 1; k++) normals[vi + k] = -axis; vi += segs + 2; }
            if (cutEnd) { for (int k = 0; k <= segs + 1; k++) normals[vi + k] = axis; vi += segs + 2; }
            if (shape.hasThumb)
            {
                Vector3 centre = (shape.thumbBase + shape.thumbTip) * 0.5f * scale;
                for (int i = vi; i < verts.Length; i++) normals[i] = MathUtil.SafeNormal(verts[i] - centre, Vector3.up);
            }
        }

        void ComputeWeights()
        {
            if (weights == null || verts == null) return;
            float len = shape.Length;
            float j0 = shape.jointT0 * len, j1 = shape.jointT1 * len;
            bool bp = parentBone != null && shape.blendParent > 0f;
            bool bc = childBone != null && shape.blendChild > 0f;
            int row = segs + 1;
            for (int i = 0; i < verts.Length; i++)
            {
                float ax;
                if (i < gridVerts) ax = ringAxial[i / row];
                else ax = Vector3.Dot(baseVerts[i] / scale - shape.start, shape.Axis);
                float wp = 0f, wc = 0f;
                if (bp) wp = 1f - SmoothStep(-shape.blendParent, shape.blendParent, ax - j0);
                if (bc) wc = SmoothStep(-shape.blendChild, shape.blendChild, ax - j1);
                wp *= 0.85f; wc *= 0.85f;
                float ws = Mathf.Max(0f, 1f - wp - wc);
                float sum = ws + wp + wc;
                var w = new BoneWeight();
                w.boneIndex0 = 0; w.weight0 = ws / sum;
                w.boneIndex1 = bp ? 1 : 0; w.weight1 = bp ? wp / sum : 0f;
                w.boneIndex2 = bc ? 2 : 0; w.weight2 = bc ? wc / sum : 0f;
                weights[i] = w;
            }
        }

        static float SmoothStep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        void ApplyBones()
        {
            if (mesh == null || smr == null) return;
            var bones = new Transform[3];
            var binds = new Matrix4x4[3];
            bones[0] = part.transform;
            binds[0] = Matrix4x4.identity;
            bones[1] = parentBone != null ? parentBone : part.transform;
            binds[1] = parentBone != null ? Matrix4x4.Translate(-(PartInfo.Origin[(int)parentBoneId] - PartInfo.Origin[(int)part.id]) * scale) : Matrix4x4.identity;
            bones[2] = childBone != null ? childBone : part.transform;
            binds[2] = childBone != null ? Matrix4x4.Translate(-(PartInfo.Origin[(int)childBoneId] - PartInfo.Origin[(int)part.id]) * scale) : Matrix4x4.identity;
            mesh.boneWeights = weights;
            mesh.bindposes = binds;
            smr.bones = bones;
            smr.rootBone = part.transform;
            smr.sharedMesh = mesh;
            Bounds b = mesh.bounds;
            b.Expand(0.25f * scale);
            smr.localBounds = b;
        }

        void ApplyMaterials()
        {
            if (smr == null) return;
            if (xray)
            {
                matsXray[0] = xrayMat; matsXray[1] = xrayMat;
                smr.sharedMaterials = matsXray;
            }
            else
            {
                matsNormal[0] = skinMat; matsNormal[1] = capMat;
                smr.sharedMaterials = matsNormal;
            }
        }

        public void SetXRay(bool on)
        {
            xray = on;
            if (on && xrayMat == null)
            {
                xrayMat = new Material(skinMat);
                xrayMat.name = skinMat.name + " xray";
                MaterialFactory.MakeTransparent(xrayMat, 0.16f);
                xrayMat.color = new Color(0.75f, 0.85f, 1f, 0.16f);
                MaterialFactory.SetEmission(xrayMat, Color.black);
            }
            ApplyMaterials();
        }

        public void SetHighlight(float amount)
        {
            if (Mathf.Abs(amount - highlight) < 0.01f) return;
            highlight = amount;
            if (skinMat != null) skinMat.SetColor("_EmissionColor", new Color(0.35f, 0.3f, 0.12f) * amount);
        }

        public void LateTick()
        {
            if (canvas != null && !sharedCanvas) canvas.Upload();
        }

        public void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (xrayMat != null) UnityEngine.Object.Destroy(xrayMat);
            if (!sharedCanvas)
            {
                if (skinMat != null) UnityEngine.Object.Destroy(skinMat);
                if (canvas != null) canvas.Dispose();
            }
        }

        #region Deformation
        /// <summary>Converts a world point to reference-space part-local coordinates (for shape queries).</summary>
        public Vector3 ToRef(Vector3 world) { return part.transform.InverseTransformPoint(world) / scale; }
        public Vector3 DirToRef(Vector3 worldDir) { return part.transform.InverseTransformDirection(worldDir); }

        public void Dent(Vector3 worldPoint, Vector3 worldDir, float radius, float depth)
        {
            var op = new DeformOp { kind = 0, point = ToRef(worldPoint), dir = DirToRef(worldDir).normalized, radius = radius / scale, amount = depth / scale };
            ops.Add(op);
            ApplyOp(op, true);
        }

        public void Gash(Vector3 worldPoint, Vector3 worldAlong, Vector3 worldInward, float length, float depth)
        {
            var op = new DeformOp { kind = 1, point = ToRef(worldPoint), axis = DirToRef(worldAlong).normalized, dir = DirToRef(worldInward).normalized,
                length = length / scale, radius = 0.012f, amount = depth / scale };
            ops.Add(op);
            ApplyOp(op, true);
        }

        public void Flatten(Vector3 worldPoint, Vector3 worldAxis, float factor)
        {
            var op = new DeformOp { kind = 2, point = ToRef(worldPoint), axis = DirToRef(worldAxis).normalized, amount = Mathf.Clamp(factor, 0.35f, 1f), radius = 0.3f };
            ops.Add(op);
            ApplyOp(op, true);
        }

        public void Kink(float t, Vector3 worldAxis, float angleDeg)
        {
            var op = new DeformOp { kind = 3, point = shape.Center(t), axis = DirToRef(worldAxis).normalized, amount = angleDeg, length = t };
            ops.Add(op);
            ApplyOp(op, true);
        }

        public void Swell(Vector3 worldPoint, float radius, float amount)
        {
            var op = new DeformOp { kind = 4, point = ToRef(worldPoint), radius = radius / scale, amount = amount / scale };
            ops.Add(op);
            ApplyOp(op, true);
        }

        void ApplyOp(DeformOp op, bool upload)
        {
            if (verts == null) return;
            float maxDent = Sandbox.Boot != null ? Sandbox.Boot.damage.maxDentDepth : 0.03f;
            int row = segs + 1;
            Vector3 p = op.point * scale;
            switch (op.kind)
            {
                case 0:
                case 4:
                    {
                        float r = op.radius * scale, r2 = r * r;
                        for (int i = 0; i < gridVerts; i++)
                        {
                            Vector3 d = verts[i] - p;
                            float dist2 = d.sqrMagnitude;
                            if (dist2 > r2) continue;
                            float f = 1f - dist2 / r2; f *= f;
                            float amt = op.amount * scale * f;
                            if (op.kind == 0)
                            {
                                float room = maxDent - accum[i];
                                if (room <= 0f) continue;
                                amt = Mathf.Min(amt, room);
                                accum[i] += amt;
                                verts[i] += op.dir * amt;
                            }
                            else
                            {
                                Vector3 outward = (verts[i] - ringCenter[i / row]).normalized;
                                verts[i] += outward * amt;
                            }
                        }
                        break;
                    }
                case 1:
                    {
                        Vector3 a = p - op.axis * op.length * scale * 0.5f, b = p + op.axis * op.length * scale * 0.5f;
                        float w = op.radius * scale;
                        for (int i = 0; i < gridVerts; i++)
                        {
                            float dist = MathUtil.DistancePointSegment(verts[i], a, b);
                            if (dist > w) continue;
                            float f = 1f - dist / w;
                            float amt = Mathf.Min(op.amount * scale * f, maxDent * 1.4f - accum[i]);
                            if (amt <= 0f) continue;
                            accum[i] += amt;
                            Vector3 inward = -(verts[i] - ringCenter[i / row]).normalized;
                            verts[i] += inward * amt;
                        }
                        break;
                    }
                case 2:
                    {
                        Vector3 ax = op.axis;
                        for (int i = 0; i < gridVerts; i++)
                        {
                            Vector3 c = ringCenter[i / row];
                            Vector3 d = verts[i] - c;
                            float along = Vector3.Dot(d, ax);
                            verts[i] -= ax * along * (1f - op.amount);
                        }
                        break;
                    }
                case 3:
                    {
                        Quaternion q = Quaternion.AngleAxis(op.amount, op.axis);
                        for (int i = 0; i < gridVerts; i++)
                        {
                            float tt = ringT[i / row];
                            if (tt <= op.length) continue;
                            float k = Mathf.Clamp01((tt - op.length) / 0.12f);
                            Quaternion qq = Quaternion.Slerp(Quaternion.identity, q, k);
                            verts[i] = p + qq * (verts[i] - p);
                        }
                        break;
                    }
            }
            if (upload)
            {
                RecomputeNormals();
                mesh.vertices = verts;
                mesh.normals = normals;
            }
        }
        #endregion
    }
    #endregion

    #region SkinCanvas
    public enum StampKind { Bruise, Cut, Hole, Blood, Burn, Meat, Shock }

    /// <summary>Per-part paint texture: skin + clothes base, plus bruise/cut/blood stamps. Batched uploads with a dirty flag.</summary>
    public class SkinCanvas
    {
        struct Stamp
        {
            public StampKind kind;
            public float x, y, rx, ry, angle, intensity, birth, depth;
            public int seed;
            public Color color;
        }

        public Texture2D tex;
        public readonly int W, H;
        readonly Color32[] basePx, px;
        readonly byte[] cloth;
        readonly List<Stamp> stamps = new List<Stamp>(32);
        bool dirty;
        float nextAge;
        readonly TubeShape shape;
        readonly float scale;
        readonly Color skin;
        public bool HasAging;

        public SkinCanvas(TubeShape shape, CharacterSettings look, int size, float scale, int seed)
        {
            this.shape = shape;
            this.scale = scale;
            W = size; H = size;
            basePx = new Color32[W * H];
            px = new Color32[W * H];
            cloth = new byte[W * H];
            skin = look.skinColor;
            PaintBase(look, seed);
            Array.Copy(basePx, px, px.Length);
            tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.name = PartInfo.Names[(int)shape.id] + " paint";
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        void PaintBase(CharacterSettings look, int seed)
        {
            PartId id = shape.id;
            bool shortsHere = look.shorts && (id == PartId.Pelvis || id == PartId.ThighL || id == PartId.ThighR);
            bool shirtHere = look.shirt && (id == PartId.Chest || id == PartId.Abdomen || id == PartId.UpperArmL || id == PartId.UpperArmR);
            bool shoeHere = look.shoes && (id == PartId.FootL || id == PartId.FootR || id == PartId.ShinL || id == PartId.ShinR);
            float vary = look.skinVariation;
            for (int y = 0; y < H; y++)
            {
                float v = (y + 0.5f) / H;
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    float n = Noise.Value(u, v, 8, seed) * 0.7f + MathUtil.Hash01(x, y, seed) * 0.3f - 0.5f;
                    Color c = skin * (1f + n * vary * 2f);
                    c.a = 1f;
                    byte isCloth = 0;
                    if (shortsHere)
                    {
                        float hem = id == PartId.Pelvis ? 0.8f : 0.45f;
                        bool inside = v < hem;
                        if (inside)
                        {
                            float w = Noise.Value(u * 4f, v, 64, seed + 3) * 0.06f;
                            c = look.shortsColor * (0.94f + w);
                            if (Mathf.Abs(v - hem) < 0.02f) c *= 0.75f;
                            if (id == PartId.Pelvis && Mathf.Abs(u - 0.25f) < 0.004f) c *= 0.85f;
                            isCloth = 1;
                        }
                    }
                    if (shirtHere)
                    {
                        bool inside = id == PartId.Chest ? v < 0.86f : id == PartId.Abdomen ? true : v < 0.45f;
                        if (inside)
                        {
                            float w = Noise.Value(u * 3f, v * 3f, 64, seed + 5) * 0.05f;
                            c = look.shirtColor * (0.95f + w);
                            if ((id == PartId.UpperArmL || id == PartId.UpperArmR) && v > 0.43f) c *= 0.8f;
                            if (id == PartId.Chest && v > 0.84f) c *= 0.8f;
                            isCloth = 2;
                        }
                    }
                    if (shoeHere)
                    {
                        bool inside = (id == PartId.FootL || id == PartId.FootR) || v > 0.84f;
                        if (inside)
                        {
                            c = look.shoesColor * (0.95f + Noise.Value(u, v, 32, seed + 7) * 0.1f);
                            if ((id == PartId.FootL || id == PartId.FootR) && Mathf.Abs(u - 0.75f) < 0.2f && v > 0.2f) c = Color.Lerp(c, Color.white, 0.6f);
                            isCloth = 3;
                        }
                    }
                    c.a = 1f;
                    basePx[y * W + x] = c;
                    cloth[y * W + x] = isCloth;
                }
            }
        }

        public void Dispose()
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }

        /// <summary>Pixel radii for a world radius at parameter t.</summary>
        void Radii(float t, float worldRadius, out float rx, out float ry)
        {
            float circ = Mathf.Max(0.02f, shape.Circumference(t) * scale);
            float arc = Mathf.Max(0.02f, shape.TotalArc * scale);
            rx = Mathf.Max(1f, worldRadius / circ * W);
            ry = Mathf.Max(1f, worldRadius / arc * H);
        }

        public void AddStamp(StampKind kind, Vector2 uv, float t, float worldRadius, float angleRad, float intensity, Color color, float depth = 0f)
        {
            float rx, ry;
            Radii(t, worldRadius, out rx, out ry);
            var s = new Stamp
            {
                kind = kind, x = uv.x * W, y = uv.y * H, rx = rx, ry = ry, angle = angleRad, intensity = Mathf.Clamp01(intensity),
                birth = Time.time, seed = UnityEngine.Random.Range(0, 100000), color = color, depth = depth
            };
            if (kind == StampKind.Cut || kind == StampKind.Hole || kind == StampKind.Meat) TearCloth(s);
            stamps.Add(s);
            if (kind == StampKind.Bruise || kind == StampKind.Blood) HasAging = true;
            Draw(px, s, Time.time);
            dirty = true;
            if (stamps.Count > 90) BakeOldest();
        }

        void TearCloth(Stamp s)
        {
            float r = Mathf.Max(s.rx, s.ry) * 1.8f + 2f;
            int x0 = Mathf.FloorToInt(s.x - r), x1 = Mathf.CeilToInt(s.x + r);
            int y0 = Mathf.Max(0, Mathf.FloorToInt(s.y - r)), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(s.y + r));
            for (int y = y0; y <= y1; y++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int x = ((xx % W) + W) % W;
                    int i = y * W + x;
                    if (cloth[i] == 0) continue;
                    float dx = (xx - s.x) / Mathf.Max(1f, s.rx * 1.8f), dy = (y - s.y) / Mathf.Max(1f, s.ry * 1.8f);
                    float d = dx * dx + dy * dy;
                    float jag = MathUtil.Hash01(x, y, s.seed) * 0.5f;
                    if (d < 1f - jag * 0.6f)
                    {
                        Color c = skin * (0.97f + MathUtil.Hash01(x + 7, y, 11) * 0.06f); c.a = 1f;
                        basePx[i] = c;
                        cloth[i] = 0;
                        px[i] = c;
                    }
                    else if (d < 1.15f)
                    {
                        Color32 b = basePx[i];
                        basePx[i] = new Color32((byte)(b.r * 0.7f), (byte)(b.g * 0.7f), (byte)(b.b * 0.7f), 255);
                    }
                }
        }

        void BakeOldest()
        {
            float now = Time.time;
            var bs = Sandbox.Boot != null ? Sandbox.Boot.blood : null;
            float dry = bs != null ? bs.decalDryTime : 90f;
            for (int i = 0; i < stamps.Count; i++)
            {
                var s = stamps[i];
                bool finished = s.kind == StampKind.Cut || s.kind == StampKind.Hole || s.kind == StampKind.Burn || s.kind == StampKind.Meat ||
                                (s.kind == StampKind.Blood && now - s.birth > dry) || (s.kind == StampKind.Bruise && now - s.birth > 180f);
                if (!finished) continue;
                if (s.kind != StampKind.Bruise) Draw(basePx, s, now);
                stamps.RemoveAt(i);
                return;
            }
            stamps.RemoveAt(0);
        }

        public void ClearAll(CharacterSettings look, int seed)
        {
            stamps.Clear();
            PaintBase(look, seed);
            Array.Copy(basePx, px, px.Length);
            dirty = true;
        }

        public void ClearRegion(float v0, float v1)
        {
            for (int i = stamps.Count - 1; i >= 0; i--)
            {
                float v = stamps[i].y / H;
                if (v >= v0 && v <= v1) stamps.RemoveAt(i);
            }
            Recompose();
        }

        public void Tick(float now)
        {
            if (!HasAging || now < nextAge) return;
            nextAge = now + 2.5f;
            Recompose();
        }

        void Recompose()
        {
            Array.Copy(basePx, px, px.Length);
            float now = Time.time;
            bool aging = false;
            for (int i = 0; i < stamps.Count; i++)
            {
                Draw(px, stamps[i], now);
                if (stamps[i].kind == StampKind.Bruise && now - stamps[i].birth < 200f) aging = true;
                if (stamps[i].kind == StampKind.Blood && now - stamps[i].birth < 150f) aging = true;
            }
            HasAging = aging;
            dirty = true;
        }

        public void Upload()
        {
            if (tex == null) return;
            Tick(Time.time);
            if (!dirty) return;
            tex.SetPixels32(px);
            tex.Apply(false, false);
            dirty = false;
        }

        public void ForceAgeTick()
        {
            if (HasAging && Time.time >= nextAge) { Recompose(); }
        }

        void Draw(Color32[] dst, Stamp s, float now)
        {
            float age = now - s.birth;
            float r = Mathf.Max(s.rx, s.ry) * (s.kind == StampKind.Cut ? 1f : 1.6f) + 2f;
            int x0 = Mathf.FloorToInt(s.x - r), x1 = Mathf.CeilToInt(s.x + r);
            int y0 = Mathf.Max(0, Mathf.FloorToInt(s.y - r)), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(s.y + r));
            float ca = Mathf.Cos(s.angle), sa = Mathf.Sin(s.angle);
            Color bruiseColor = BruiseColor(age, s.intensity);
            Color bloodColor = BloodColor(s.color, age);
            for (int y = y0; y <= y1; y++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int x = ((xx % W) + W) % W;
                    int i = y * W + x;
                    float dx = (xx + 0.5f - s.x), dy = (y + 0.5f - s.y);
                    float lx = (dx * ca + dy * sa) / Mathf.Max(0.5f, s.rx);
                    float ly = (-dx * sa + dy * ca) / Mathf.Max(0.5f, s.ry);
                    float d2 = lx * lx + ly * ly;
                    float n = MathUtil.Hash01(x, y, s.seed);
                    Color src; float a;
                    switch (s.kind)
                    {
                        case StampKind.Bruise:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.25f;
                                a = Mathf.Clamp01(1.2f - d) * bruiseColor.a;
                                src = bruiseColor;
                                break;
                            }
                        case StampKind.Cut:
                            {
                                // long thin gash along the stamp angle (pixel space); rx/ry = half length
                                float halfLen = Mathf.Max(2f, (s.rx + s.ry) * 0.5f);
                                float along = Mathf.Abs(dx * ca + dy * sa) / halfLen;
                                float across = Mathf.Abs(-dx * sa + dy * ca);
                                if (along > 1f) { a = 0f; src = Color.clear; break; }
                                float width = (1.2f + 3.2f * Mathf.Clamp01(s.depth)) * Mathf.Sqrt(Mathf.Max(0f, 1f - along * along)) * (0.8f + n * 0.4f) + 0.4f;
                                if (across < width * 0.45f)
                                {
                                    src = across < width * 0.18f && s.depth > 0.6f ? new Color(0.92f, 0.88f, 0.8f) : new Color(0.3f, 0.02f, 0.03f);
                                    a = 1f;
                                }
                                else if (across < width) { src = new Color(0.62f, 0.08f, 0.08f); a = 1f; }
                                else if (across < width * 2.2f) { src = new Color(0.55f, 0.12f, 0.12f); a = Mathf.Clamp01((width * 2.2f - across) / width) * 0.5f; }
                                else { a = 0f; src = Color.clear; }
                                break;
                            }
                        case StampKind.Hole:
                            {
                                float d = Mathf.Sqrt(d2);
                                if (d < 0.45f) { src = new Color(0.12f, 0.0f, 0.0f); a = 1f; }
                                else if (d < 0.8f) { src = new Color(0.5f, 0.05f, 0.05f); a = 1f; }
                                else if (d < 1.6f) { src = new Color(0.45f, 0.1f, 0.12f); a = Mathf.Clamp01((1.6f - d) / 0.8f) * 0.6f; }
                                else { a = 0f; src = Color.clear; }
                                break;
                            }
                        case StampKind.Meat:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.18f;
                                if (d < 0.35f && s.depth > 0.5f) { src = d < 0.18f ? new Color(0.5f, 0.1f, 0.08f) : new Color(0.93f, 0.9f, 0.82f); a = 1f; }
                                else if (d < 0.85f) { src = Color.Lerp(new Color(0.42f, 0.03f, 0.04f), new Color(0.7f, 0.15f, 0.14f), n); a = 1f; }
                                else if (d < 1.05f) { src = new Color(0.9f, 0.82f, 0.5f); a = 0.8f; }
                                else if (d < 1.5f) { src = bloodColor; a = Mathf.Clamp01((1.5f - d) * 2f) * 0.8f; }
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
                        default:
                            {
                                float d = Mathf.Sqrt(d2) + (n - 0.5f) * 0.45f;
                                a = Mathf.Clamp01((1f - d) * 3f) * s.intensity * bloodColor.a;
                                src = bloodColor;
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

        /// <summary>Bruise colour over time: red-purple -> blue/purple -> yellow-green -> fades.</summary>
        static Color BruiseColor(float age, float intensity)
        {
            Color c;
            if (age < 12f) c = Color.Lerp(new Color(0.62f, 0.25f, 0.3f), new Color(0.38f, 0.2f, 0.42f), age / 12f);
            else if (age < 60f) c = Color.Lerp(new Color(0.38f, 0.2f, 0.42f), new Color(0.3f, 0.22f, 0.45f), (age - 12f) / 48f);
            else if (age < 140f) c = Color.Lerp(new Color(0.3f, 0.22f, 0.45f), new Color(0.62f, 0.6f, 0.3f), (age - 60f) / 80f);
            else c = new Color(0.62f, 0.6f, 0.3f);
            float fade = age < 140f ? 1f : Mathf.Clamp01(1f - (age - 140f) / 80f);
            c.a = Mathf.Clamp01(intensity) * 0.75f * fade * Mathf.Clamp01(age / 1.5f + 0.35f);
            return c;
        }

        static Color BloodColor(Color fresh, float age)
        {
            var bs = Sandbox.Boot != null ? Sandbox.Boot.blood : null;
            float dry = bs != null ? bs.decalDryTime : 90f;
            Color dried = bs != null ? bs.dried : new Color(0.22f, 0.07f, 0.04f);
            Color c = Color.Lerp(fresh, dried, Mathf.Clamp01(age / dry) * 0.85f);
            c.a = 0.95f;
            return c;
        }
    }
    #endregion

    #region BodyMeshLibrary
    public enum OrganType { Brain, Heart, LungL, LungR, Liver, Stomach, KidneyL, KidneyR, Intestine }

    public struct OrganDef
    {
        public OrganType type;
        public PartId host;
        public Vector3 center;     // T-pose world (reference)
        public Vector3 radii;
        public float rollDeg;
        public Color color;
        public float mass;

        public OrganDef(OrganType t, PartId h, Vector3 c, Vector3 r, float roll, Color col, float m)
        { type = t; host = h; center = c; radii = r; rollDeg = roll; color = col; mass = m; }
    }

    /// <summary>Procedural skeleton, arteries and organ meshes (reference scale, part-local), shared by all ragdolls.</summary>
    public static class BodyMeshLibrary
    {
        public static readonly Mesh[] Skeleton = new Mesh[PartInfo.Count];
        public static readonly Mesh[] Arteries = new Mesh[PartInfo.Count];
        /// <summary>Artery centre lines per part (reference part-local) used to decide if a wound is arterial.</summary>
        public static readonly List<Vector3>[] ArteryLines = new List<Vector3>[PartInfo.Count];
        public static readonly List<OrganDef> Organs = new List<OrganDef>();
        public static readonly Dictionary<OrganType, Mesh> OrganMeshes = new Dictionary<OrganType, Mesh>();
        public static Mesh IntestineSegment;
        public const int IntestineSegments = 10;
        public static readonly Vector3[] IntestinePath = new Vector3[IntestineSegments + 1];
        static bool built;

        static Vector3 W2L(PartId id, Vector3 world) { return world - PartInfo.Origin[(int)id]; }

        public static void Build()
        {
            if (built) return;
            built = true;
            for (int i = 0; i < PartInfo.Count; i++) ArteryLines[i] = new List<Vector3>();
            for (int i = 0; i < PartInfo.Count; i++)
            {
                PartId id = (PartId)i;
                Skeleton[i] = BuildSkeleton(id);
                Arteries[i] = BuildArteries(id);
            }
            BuildOrganDefs();
        }

        static Vector3 Mx(Vector3 v, float sx) { return new Vector3(v.x * sx, v.y, v.z); }

        static Mesh BuildSkeleton(PartId id)
        {
            var b = new MeshBuilder();
            float sx = PartInfo.SideSign(id);
            if (sx == 0f) sx = 1f;
            switch (id)
            {
                case PartId.Head:
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 1.672f, 0.012f)), new Vector3(0.072f, 0.085f, 0.092f), Quaternion.identity, 10, 14);
                    MeshFactory.AddTube(b, Pts(id, new Vector3(-0.055f, 1.585f, -0.005f), new Vector3(-0.05f, 1.56f, 0.04f), new Vector3(0f, 1.548f, 0.075f),
                        new Vector3(0.05f, 1.56f, 0.04f), new Vector3(0.055f, 1.585f, -0.005f)), 0.011f, 6);
                    break;
                case PartId.Neck:
                    for (int k = 0; k < 4; k++)
                        MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 1.485f + k * 0.022f, -0.012f)), new Vector3(0.018f, 0.008f, 0.016f), Quaternion.identity, 4, 8);
                    break;
                case PartId.Chest:
                    for (int k = 0; k < 9; k++)
                        MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 1.215f + k * 0.03f, -0.075f)), new Vector3(0.02f, 0.011f, 0.02f), Quaternion.identity, 4, 8);
                    for (int r = 0; r < 8; r++)
                    {
                        float y = 1.42f - r * 0.028f;
                        float w = 1f - Mathf.Abs(r - 3.5f) * 0.035f;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            MeshFactory.AddTube(b, Pts(id,
                                new Vector3(side * 0.02f, y, -0.075f), new Vector3(side * 0.09f * w, y - 0.005f, -0.07f), new Vector3(side * 0.135f * w, y - 0.02f, -0.02f),
                                new Vector3(side * 0.14f * w, y - 0.04f, 0.04f), new Vector3(side * 0.1f * w, y - 0.06f, 0.09f), new Vector3(side * 0.035f, y - 0.07f, 0.105f)), 0.0065f, 5);
                        }
                    }
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 1.33f, 0.11f)), new Vector3(0.018f, 0.085f, 0.008f), Quaternion.identity, 6, 8);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        MeshFactory.AddTube(b, Pts(id, new Vector3(side * 0.02f, 1.45f, 0.085f), new Vector3(side * 0.09f, 1.455f, 0.07f), new Vector3(side * 0.17f, 1.45f, 0.01f)), 0.007f, 5);
                        MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(side * 0.1f, 1.37f, -0.095f)), new Vector3(0.05f, 0.065f, 0.008f), Quaternion.Euler(0f, side * 20f, 0f), 6, 8);
                    }
                    break;
                case PartId.Abdomen:
                    for (int k = 0; k < 5; k++)
                        MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 1.045f + k * 0.035f, -0.065f)), new Vector3(0.024f, 0.013f, 0.022f), Quaternion.identity, 4, 8);
                    break;
                case PartId.Pelvis:
                    for (int side = -1; side <= 1; side += 2)
                        MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(side * 0.1f, 0.99f, -0.01f)), new Vector3(0.065f, 0.06f, 0.02f), Quaternion.Euler(0f, side * 20f, side * 25f), 6, 10);
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(0f, 0.965f, -0.075f)), new Vector3(0.04f, 0.06f, 0.02f), Quaternion.Euler(-15f, 0f, 0f), 6, 8);
                    MeshFactory.AddTube(b, Pts(id, new Vector3(-0.1f, 0.93f, 0.02f), new Vector3(-0.05f, 0.9f, 0.06f), new Vector3(0f, 0.895f, 0.068f),
                        new Vector3(0.05f, 0.9f, 0.06f), new Vector3(0.1f, 0.93f, 0.02f)), 0.012f, 6);
                    break;
                case PartId.UpperArmL: case PartId.UpperArmR:
                    AddLongBone(b, id, 0f, 1f, 0.011f, 0.022f, 0.018f, Vector3.zero);
                    break;
                case PartId.ForearmL: case PartId.ForearmR:
                    AddLongBone(b, id, 0f, 1f, 0.007f, 0.012f, 0.011f, new Vector3(0f, 0f, 0.011f));
                    AddLongBone(b, id, 0f, 1f, 0.006f, 0.011f, 0.009f, new Vector3(0f, 0f, -0.011f));
                    break;
                case PartId.HandL: case PartId.HandR:
                    for (int f = 0; f < 4; f++)
                    {
                        float z = -0.03f + f * 0.02f;
                        MeshFactory.AddTube(b, Pts(id, new Vector3(sx * 0.725f, 1.42f, z * 0.6f), new Vector3(sx * 0.8f, 1.42f, z)), 0.004f, 5);
                        MeshFactory.AddTube(b, Pts(id, new Vector3(sx * 0.805f, 1.42f, z), new Vector3(sx * (0.88f - Mathf.Abs(f - 1.5f) * 0.01f), 1.42f, z * 1.05f)), 0.0035f, 5);
                    }
                    MeshFactory.AddTube(b, Pts(id, new Vector3(sx * 0.725f, 1.415f, 0.02f), new Vector3(sx * 0.79f, 1.408f, 0.065f)), 0.0045f, 5);
                    break;
                case PartId.ThighL: case PartId.ThighR:
                    AddLongBone(b, id, 0f, 1f, 0.014f, 0.025f, 0.027f, Vector3.zero);
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(sx * 0.09f, 0.495f, 0.05f)), new Vector3(0.02f, 0.022f, 0.01f), Quaternion.identity, 5, 8);
                    break;
                case PartId.ShinL: case PartId.ShinR:
                    AddLongBone(b, id, 0f, 1f, 0.013f, 0.025f, 0.02f, new Vector3(0f, 0f, 0.006f));
                    AddLongBone(b, id, 0f, 1f, 0.006f, 0.01f, 0.01f, new Vector3(sx * 0.022f, 0f, -0.012f));
                    break;
                case PartId.FootL: case PartId.FootR:
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(sx * 0.09f, 0.035f, -0.035f)), new Vector3(0.022f, 0.022f, 0.035f), Quaternion.identity, 5, 8);
                    MeshFactory.AddEllipsoid(b, W2L(id, new Vector3(sx * 0.09f, 0.068f, 0f)), new Vector3(0.02f, 0.016f, 0.022f), Quaternion.identity, 5, 8);
                    for (int t = 0; t < 5; t++)
                    {
                        float x = sx * (0.09f + (t - 2f) * 0.015f);
                        MeshFactory.AddTube(b, Pts(id, new Vector3(sx * 0.09f, 0.045f, 0.02f), new Vector3(x, 0.025f, 0.15f)), 0.004f, 5);
                        MeshFactory.AddTube(b, Pts(id, new Vector3(x, 0.022f, 0.155f), new Vector3(x, 0.015f, 0.19f - Mathf.Abs(t - 1f) * 0.006f)), 0.0035f, 5);
                    }
                    break;
            }
            if (b.V.Count == 0) return null;
            return b.ToMesh(PartInfo.Names[(int)id] + " bones");
        }

        /// <summary>Long bone between t-range of the part's main axis (shaft + knobbed ends). Public: regenerated for cut pieces.</summary>
        public static void AddLongBone(MeshBuilder b, PartId id, float tA, float tB, float r, float knobA, float knobB, Vector3 offset)
        {
            Vector3 p0 = PartInfo.Origin[(int)id], p1 = PartInfo.Tip[(int)id];
            if (id == PartId.ShinL || id == PartId.ShinR) p1 = PartInfo.Origin[(int)id] + Vector3.down * 0.39f;
            Vector3 a = Vector3.Lerp(p0, p1, tA) - PartInfo.Origin[(int)id] + offset;
            Vector3 c = Vector3.Lerp(p0, p1, tB) - PartInfo.Origin[(int)id] + offset;
            Vector3 dir = (c - a).normalized;
            float shrink = 0.015f;
            MeshFactory.AddTube(b, new[] { a + dir * shrink, Vector3.Lerp(a, c, 0.5f), c - dir * shrink }, r, 6, 0, r * 0.9f);
            if (tA <= 0.001f) MeshFactory.AddEllipsoid(b, a + dir * shrink, Vector3.one * knobA, Quaternion.identity, 5, 8);
            if (tB >= 0.999f) MeshFactory.AddEllipsoid(b, c - dir * shrink, Vector3.one * knobB, Quaternion.identity, 5, 8);
        }

        public static Mesh LongBoneMesh(PartId id, float tA, float tB)
        {
            var b = new MeshBuilder();
            float sx = PartInfo.SideSign(id); if (sx == 0f) sx = 1f;
            switch (id)
            {
                case PartId.UpperArmL: case PartId.UpperArmR: AddLongBone(b, id, tA, tB, 0.011f, 0.022f, 0.018f, Vector3.zero); break;
                case PartId.ForearmL: case PartId.ForearmR:
                    AddLongBone(b, id, tA, tB, 0.007f, 0.012f, 0.011f, new Vector3(0f, 0f, 0.011f));
                    AddLongBone(b, id, tA, tB, 0.006f, 0.011f, 0.009f, new Vector3(0f, 0f, -0.011f)); break;
                case PartId.ThighL: case PartId.ThighR: AddLongBone(b, id, tA, tB, 0.014f, 0.025f, 0.027f, Vector3.zero); break;
                case PartId.ShinL: case PartId.ShinR:
                    AddLongBone(b, id, tA, tB, 0.013f, 0.025f, 0.02f, new Vector3(0f, 0f, 0.006f));
                    AddLongBone(b, id, tA, tB, 0.006f, 0.01f, 0.01f, new Vector3(sx * 0.022f, 0f, -0.012f)); break;
                default: return null;
            }
            return b.ToMesh(PartInfo.Names[(int)id] + " bone piece");
        }

        static Vector3[] Pts(PartId id, params Vector3[] world)
        {
            var o = new Vector3[world.Length];
            for (int i = 0; i < world.Length; i++) o[i] = W2L(id, world[i]);
            return o;
        }

        static Mesh BuildArteries(PartId id)
        {
            var b = new MeshBuilder();
            float sx = PartInfo.SideSign(id);
            var lines = ArteryLines[(int)id];
            switch (id)
            {
                case PartId.Chest:
                    AddArtery(b, lines, id, 0.012f, new Vector3(-0.02f, 1.3f, 0.03f), new Vector3(0f, 1.4f, 0.02f), new Vector3(0.01f, 1.42f, -0.02f), new Vector3(0f, 1.38f, -0.05f), new Vector3(0f, 1.2f, -0.05f));
                    for (int s = -1; s <= 1; s += 2)
                    {
                        AddArtery(b, lines, id, 0.005f, new Vector3(0f, 1.4f, 0.02f), new Vector3(s * 0.025f, 1.47f, 0.02f));
                        AddArtery(b, lines, id, 0.005f, new Vector3(0f, 1.4f, 0.02f), new Vector3(s * 0.1f, 1.43f, 0.01f), new Vector3(s * 0.18f, 1.42f, 0f));
                    }
                    break;
                case PartId.Neck:
                    for (int s = -1; s <= 1; s += 2) AddArtery(b, lines, id, 0.005f, new Vector3(s * 0.025f, 1.47f, 0.02f), new Vector3(s * 0.03f, 1.58f, 0.02f));
                    break;
                case PartId.Abdomen:
                    AddArtery(b, lines, id, 0.011f, new Vector3(0f, 1.2f, -0.045f), new Vector3(0f, 1.03f, -0.04f));
                    break;
                case PartId.Pelvis:
                    for (int s = -1; s <= 1; s += 2) AddArtery(b, lines, id, 0.008f, new Vector3(0f, 1.03f, -0.04f), new Vector3(s * 0.06f, 0.94f, 0f), new Vector3(s * 0.075f, 0.9f, 0.03f));
                    break;
                case PartId.ThighL: case PartId.ThighR:
                    AddArtery(b, lines, id, 0.006f, new Vector3(sx * 0.075f, 0.9f, 0.03f), new Vector3(sx * 0.07f, 0.7f, 0.035f), new Vector3(sx * 0.085f, 0.52f, -0.02f));
                    break;
                case PartId.ShinL: case PartId.ShinR:
                    AddArtery(b, lines, id, 0.004f, new Vector3(sx * 0.085f, 0.47f, -0.02f), new Vector3(sx * 0.09f, 0.2f, -0.01f), new Vector3(sx * 0.085f, 0.09f, 0.02f));
                    break;
                case PartId.UpperArmL: case PartId.UpperArmR:
                    AddArtery(b, lines, id, 0.004f, new Vector3(sx * 0.19f, 1.415f, 0f), new Vector3(sx * 0.46f, 1.405f, 0.015f));
                    break;
                case PartId.ForearmL: case PartId.ForearmR:
                    AddArtery(b, lines, id, 0.003f, new Vector3(sx * 0.47f, 1.41f, 0.015f), new Vector3(sx * 0.71f, 1.415f, 0.02f));
                    break;
            }
            if (b.V.Count == 0) return null;
            return b.ToMesh(PartInfo.Names[(int)id] + " arteries");
        }

        static void AddArtery(MeshBuilder b, List<Vector3> lines, PartId id, float r, params Vector3[] world)
        {
            var pts = Pts(id, world);
            MeshFactory.AddTube(b, pts, r, 6);
            for (int i = 0; i < pts.Length - 1; i++) { lines.Add(pts[i]); lines.Add(pts[i + 1]); }
        }

        static void BuildOrganDefs()
        {
            Organs.Clear();
            Organs.Add(new OrganDef(OrganType.Brain, PartId.Head, new Vector3(0f, 1.675f, 0.008f), new Vector3(0.062f, 0.052f, 0.078f), 0f, new Color(0.88f, 0.64f, 0.66f), 1.4f));
            Organs.Add(new OrganDef(OrganType.Heart, PartId.Chest, new Vector3(-0.022f, 1.315f, 0.035f), new Vector3(0.042f, 0.055f, 0.04f), -20f, new Color(0.55f, 0.06f, 0.08f), 0.3f));
            Organs.Add(new OrganDef(OrganType.LungL, PartId.Chest, new Vector3(-0.085f, 1.335f, -0.005f), new Vector3(0.06f, 0.105f, 0.074f), 5f, new Color(0.86f, 0.52f, 0.56f), 0.6f));
            Organs.Add(new OrganDef(OrganType.LungR, PartId.Chest, new Vector3(0.085f, 1.335f, -0.005f), new Vector3(0.064f, 0.108f, 0.076f), -5f, new Color(0.86f, 0.52f, 0.56f), 0.65f));
            Organs.Add(new OrganDef(OrganType.Liver, PartId.Abdomen, new Vector3(0.045f, 1.165f, 0.025f), new Vector3(0.095f, 0.045f, 0.07f), 10f, new Color(0.42f, 0.1f, 0.09f), 1.5f));
            Organs.Add(new OrganDef(OrganType.Stomach, PartId.Abdomen, new Vector3(-0.055f, 1.14f, 0.04f), new Vector3(0.055f, 0.045f, 0.042f), -15f, new Color(0.86f, 0.6f, 0.55f), 0.5f));
            Organs.Add(new OrganDef(OrganType.KidneyL, PartId.Abdomen, new Vector3(-0.06f, 1.1f, -0.055f), new Vector3(0.022f, 0.042f, 0.02f), 8f, new Color(0.45f, 0.08f, 0.08f), 0.15f));
            Organs.Add(new OrganDef(OrganType.KidneyR, PartId.Abdomen, new Vector3(0.06f, 1.1f, -0.055f), new Vector3(0.022f, 0.042f, 0.02f), -8f, new Color(0.45f, 0.08f, 0.08f), 0.15f));
            foreach (var d in Organs) OrganMeshes[d.type] = MeshFactory.Ellipsoid(d.radii, 10, 14);
            // Coiled intestines: a meandering path through the lower abdomen
            for (int i = 0; i <= IntestineSegments; i++)
            {
                float u = i / (float)IntestineSegments;
                float x = Mathf.Sin(u * Mathf.PI * 3.2f) * 0.075f;
                float y = 1.11f - u * 0.1f;
                float z = 0.03f + Mathf.Cos(u * Mathf.PI * 4.1f) * 0.025f;
                IntestinePath[i] = new Vector3(x, y, z);
            }
            var ib = new MeshBuilder();
            MeshFactory.AddTube(ib, new[] { new Vector3(0f, 0f, -0.035f), new Vector3(0.006f, 0f, 0f), new Vector3(0f, 0f, 0.035f) }, 0.016f, 8);
            MeshFactory.AddEllipsoid(ib, new Vector3(0f, 0f, -0.035f), Vector3.one * 0.016f, Quaternion.identity, 4, 8);
            MeshFactory.AddEllipsoid(ib, new Vector3(0f, 0f, 0.035f), Vector3.one * 0.016f, Quaternion.identity, 4, 8);
            IntestineSegment = ib.ToMesh("Intestine");
        }
    }
    #endregion

    #region BodyVisuals
    /// <summary>Builds every visual layer of a freshly created ragdoll.</summary>
    public static class BodyVisuals
    {
        public static void Build(RagdollBody body)
        {
            var look = body.Look;
            int big = Mathf.ClosestPowerOfTwo(Mathf.Clamp(look.paintTextureSize, 64, 512));
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var part = body.Parts[i];
                var shape = TubeShape.For(part.id);
                shape.segments = Mathf.Max(8, Mathf.RoundToInt(shape.segments * look.meshSegments / 24f));
                shape.rings = Mathf.Max(4, Mathf.RoundToInt(shape.rings * look.meshRings / 22f));
                bool small = part.id == PartId.HandL || part.id == PartId.HandR || part.id == PartId.FootL || part.id == PartId.FootR || part.id == PartId.Neck;
                int size = small ? Mathf.Max(64, big / 2) : big;
                var canvas = new SkinCanvas(shape, look, size, body.Scale, 100 + i * 13 + body.Index * 7);
                var mat = MaterialFactory.LitEmissive(PartInfo.Names[i] + " skin", Color.white, canvas.tex, 0.22f);
                part.visual = PartVisual.Create(part, shape, canvas, mat, 0f, 1f, false, false);
            }
            // Smooth joints: blend each mesh into the neighbouring bones.
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var part = body.Parts[i];
                var shape = part.visual.shape;
                Transform pb = null, cb = null;
                PartId pid = PartInfo.Parent[i], cid = PartId.Count;
                if (pid != PartId.Count && shape.blendParent > 0f) pb = body.Parts[(int)pid].transform;
                if (shape.blendChild > 0f)
                {
                    cid = BlendChildOf(part.id);
                    if (cid != PartId.Count) cb = body.Parts[(int)cid].transform;
                }
                part.visual.SetBlend(pb, pid, cb, cid);
            }
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var part = body.Parts[i];
                var inner = new GameObject("Inner");
                inner.transform.SetParent(part.transform, false);
                inner.transform.localScale = Vector3.one * body.Scale;
                AddInner(part, inner.transform, BodyMeshLibrary.Skeleton[i], MaterialFactory.Bone, "Bones");
                AddInner(part, inner.transform, BodyMeshLibrary.Arteries[i], MaterialFactory.Artery, "Arteries");
            }
        }

        public static PartId BlendChildOf(PartId id)
        {
            switch (id)
            {
                case PartId.Pelvis: return PartId.Abdomen;
                case PartId.Abdomen: return PartId.Chest;
                case PartId.Neck: return PartId.Head;
                case PartId.UpperArmL: return PartId.ForearmL;
                case PartId.ForearmL: return PartId.HandL;
                case PartId.UpperArmR: return PartId.ForearmR;
                case PartId.ForearmR: return PartId.HandR;
                case PartId.ThighL: return PartId.ShinL;
                case PartId.ShinL: return PartId.FootL;
                case PartId.ThighR: return PartId.ShinR;
                case PartId.ShinR: return PartId.FootR;
            }
            return PartId.Count;
        }

        public static MeshRenderer AddInner(BodyPart part, Transform parent, Mesh mesh, Material mat, string name)
        {
            if (mesh == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.enabled = false;
            part.innerRenderers.Add(mr);
            return mr;
        }
    }
    #endregion
}
