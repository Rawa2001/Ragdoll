// Ragdoll Sandbox - anatomy library: procedural skeleton, arteries and organ meshes (reference scale, part-local),
// shared by all ragdolls and shown in the X-ray view or when organs spill out. The skin itself is in RagdollSkin.cs.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
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
}
