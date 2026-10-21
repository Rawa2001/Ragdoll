// Ragdoll Sandbox - mesh operations on the continuous body (bind space, worker-thread safe).
//  * SliceJob: real plane slicing of the skinned mesh. Vertices are classified by bone membership (and by the plane
//    for the bones being cut), crossing triangles are clipped exactly, both halves get welded caps (skin/fat band,
//    flesh or clay interior and a bone disc), and skin weights are remapped to each half's bones.
//  * HoleJob: removes the triangles inside the hole radius and builds a tunnel with a raised rim and a dark cavity,
//    keeping the mesh watertight.
//  * DeformJob: dents, squash/flatten and stretch, bounded by the rest pose.
//  * ReweightJob: fracture kink (vertices beyond the break follow the new distal segment).
// Every job does its maths in Run() (no Unity engine calls) and touches scene objects only in Apply().
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    public static class MeshOps
    {
        public static long Key(int a, int b) { return ((long)a << 32) | (uint)b; }

        public static BoneWeight Single(int bone)
        {
            return new BoneWeight { boneIndex0 = bone, weight0 = 1f };
        }

        public static BoneWeight Lerp(BoneWeight a, BoneWeight b, float t)
        {
            int[] idx = new int[8];
            float[] val = new float[8];
            int n = 0;
            Accum(idx, val, ref n, a.boneIndex0, a.weight0 * (1f - t)); Accum(idx, val, ref n, a.boneIndex1, a.weight1 * (1f - t));
            Accum(idx, val, ref n, a.boneIndex2, a.weight2 * (1f - t)); Accum(idx, val, ref n, a.boneIndex3, a.weight3 * (1f - t));
            Accum(idx, val, ref n, b.boneIndex0, b.weight0 * t); Accum(idx, val, ref n, b.boneIndex1, b.weight1 * t);
            Accum(idx, val, ref n, b.boneIndex2, b.weight2 * t); Accum(idx, val, ref n, b.boneIndex3, b.weight3 * t);
            return Top4(idx, val, n);
        }

        static void Accum(int[] idx, float[] val, ref int n, int bone, float w)
        {
            if (w <= 0f) return;
            for (int i = 0; i < n; i++) if (idx[i] == bone) { val[i] += w; return; }
            if (n < idx.Length) { idx[n] = bone; val[n] = w; n++; }
        }

        static BoneWeight Top4(int[] idx, float[] val, int n)
        {
            for (int i = 1; i < n; i++)
            {
                float v = val[i]; int b = idx[i]; int j = i - 1;
                while (j >= 0 && val[j] < v) { val[j + 1] = val[j]; idx[j + 1] = idx[j]; j--; }
                val[j + 1] = v; idx[j + 1] = b;
            }
            float sum = 0f;
            for (int i = 0; i < n && i < 4; i++) sum += val[i];
            if (sum <= 0f) return Single(n > 0 ? idx[0] : 0);
            var r = new BoneWeight();
            if (n > 0) { r.boneIndex0 = idx[0]; r.weight0 = val[0] / sum; }
            if (n > 1) { r.boneIndex1 = idx[1]; r.weight1 = val[1] / sum; }
            if (n > 2) { r.boneIndex2 = idx[2]; r.weight2 = val[2] / sum; }
            if (n > 3) { r.boneIndex3 = idx[3]; r.weight3 = val[3] / sum; }
            return r;
        }

        /// <summary>Moves every weight on a bone that 'allowed' rejects onto 'fallback'.</summary>
        public static BoneWeight Remap(BoneWeight w, bool[] allowed, int fallback)
        {
            int[] idx = new int[4];
            float[] val = new float[4];
            int n = 0;
            AccumAllowed(idx, val, ref n, w.boneIndex0, w.weight0, allowed, fallback);
            AccumAllowed(idx, val, ref n, w.boneIndex1, w.weight1, allowed, fallback);
            AccumAllowed(idx, val, ref n, w.boneIndex2, w.weight2, allowed, fallback);
            AccumAllowed(idx, val, ref n, w.boneIndex3, w.weight3, allowed, fallback);
            return Top4(idx, val, n);
        }

        static void AccumAllowed(int[] idx, float[] val, ref int n, int bone, float w, bool[] allowed, int fallback)
        {
            if (w <= 0f) return;
            bool ok = bone >= 0 && bone < allowed.Length && allowed[bone];
            Accum(idx, val, ref n, ok ? bone : fallback, w);
        }

        public static BoneWeight Move(BoneWeight w, int from, int to, float fraction)
        {
            int[] idx = new int[5];
            float[] val = new float[5];
            int n = 0;
            int[] bi = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
            float[] bv = { w.weight0, w.weight1, w.weight2, w.weight3 };
            for (int i = 0; i < 4; i++)
            {
                if (bv[i] <= 0f) continue;
                if (bi[i] == from) { Accum(idx, val, ref n, from, bv[i] * (1f - fraction)); Accum(idx, val, ref n, to, bv[i] * fraction); }
                else Accum(idx, val, ref n, bi[i], bv[i]);
            }
            return Top4(idx, val, n);
        }

        /// <summary>Area-weighted normals for the welds flagged in 'mask' (null = all), copied to every duplicate.</summary>
        public static void RecomputeNormals(MeshData m, bool[] mask)
        {
            int nw = m.nextWeld;
            var acc = new Vector3[nw];
            for (int k = 0; k < 3; k++)
            {
                var l = m.sub[k];
                for (int i = 0; i + 2 < l.Count; i += 3)
                {
                    int a = l[i], b = l[i + 1], c = l[i + 2];
                    int wa = m.weld[a], wb = m.weld[b], wc = m.weld[c];
                    if (mask != null && !mask[wa] && !mask[wb] && !mask[wc]) continue;
                    Vector3 fn = Vector3.Cross(m.pos[b] - m.pos[a], m.pos[c] - m.pos[a]);
                    if (k == 1) continue; // caps keep their flat normals
                    acc[wa] += fn; acc[wb] += fn; acc[wc] += fn;
                }
            }
            var capVert = new bool[m.Count];
            var caps = m.sub[1];
            for (int i = 0; i < caps.Count; i++) capVert[caps[i]] = true;
            for (int i = 0; i < m.Count; i++)
            {
                int w = m.weld[i];
                if (mask != null && !mask[w]) continue;
                if (acc[w].sqrMagnitude < 1e-14f || capVert[i]) continue;
                m.nrm[i] = acc[w].normalized;
            }
        }

        /// <summary>Ear clipping of a simple polygon given in 2D. Emits index triples into 'tris' (indices into pts).</summary>
        public static void EarClip(List<Vector2> pts, List<int> tris)
        {
            int n = pts.Count;
            if (n < 3) return;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            float area = 0f;
            for (int i = 0; i < n; i++) { Vector2 a = pts[i], b = pts[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
            float sign = area >= 0f ? 1f : -1f;
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * 4)
            {
                bool found = false;
                int m = idx.Count;
                for (int i = 0; i < m; i++)
                {
                    int ip = idx[(i + m - 1) % m], ic = idx[i], inx = idx[(i + 1) % m];
                    Vector2 a = pts[ip], b = pts[ic], c = pts[inx];
                    float cr = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (cr * sign <= 1e-12f) continue;
                    bool inside = false;
                    for (int j = 0; j < m; j++)
                    {
                        int q = idx[j];
                        if (q == ip || q == ic || q == inx) continue;
                        if (PointInTri(pts[q], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    tris.Add(ip); tris.Add(ic); tris.Add(inx);
                    idx.RemoveAt(i);
                    found = true;
                    break;
                }
                if (!found) break;
            }
            // remaining polygon (3 vertices, or a degenerate rest): fan
            for (int i = 1; i + 1 < idx.Count; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
        }

        static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        public static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) && (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y + 1e-12f) + poly[i].x)) inside = !inside;
            }
            return inside;
        }

        /// <summary>Boundary loops of a mesh (directed weld edges without a twin), as weld id lists.</summary>
        public static List<List<int>> BoundaryLoops(MeshData m, Func<int, bool> weldFilter)
        {
            var edges = new HashSet<long>();
            for (int k = 0; k < 3; k++)
            {
                var l = m.sub[k];
                for (int i = 0; i + 2 < l.Count; i += 3)
                {
                    int a = m.weld[l[i]], b = m.weld[l[i + 1]], c = m.weld[l[i + 2]];
                    edges.Add(Key(a, b)); edges.Add(Key(b, c)); edges.Add(Key(c, a));
                }
            }
            var next = new Dictionary<int, int>();
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xFFFFFFFF);
                if (edges.Contains(Key(b, a))) continue;
                if (weldFilter != null && (!weldFilter(a) || !weldFilter(b))) continue;
                if (!next.ContainsKey(a)) next[a] = b;
            }
            var loops = new List<List<int>>();
            var visited = new HashSet<int>();
            foreach (var kv in next)
            {
                int start = kv.Key;
                if (visited.Contains(start)) continue;
                var loop = new List<int>();
                int cur = start;
                bool closed = false;
                for (int guard = 0; guard < 8192; guard++)
                {
                    loop.Add(cur);
                    visited.Add(cur);
                    int nx;
                    if (!next.TryGetValue(cur, out nx)) break;
                    if (nx == start) { closed = true; break; }
                    if (visited.Contains(nx)) break;
                    cur = nx;
                }
                if (closed && loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }
    }

    #region SliceJob
    public class SliceJob : MeshJob
    {
        public BodyOp op;
        public bool sever;
        public bool clay;
        // prepared on the main thread
        bool[] dset, planeBones, allowedP, allowedD;
        int fallbackP, fallbackD, newIndex = -1;
        Vector3 n, center, boneO, boneA;
        float dist, radius2, boneRadius, scale;
        bool prepared;
        // results
        MeshData outP, outD;
        Vector3 capCenterP, capCenterD;
        bool hasCap;

        public override void Prepare()
        {
            if (startedPrepare) return;
            startedPrepare = true;
            var body = skin.Body;
            scale = body.Scale;
            int count = body.AllPieces.Count;
            if (sever)
            {
                var child = skin.SegAt(op.seg);
                var parent = skin.SegAt(op.seg2);
                if (child == null) return;
                // already a separate piece: nothing to cut
                if (parent != null && skin.PieceOf(child) != skin.PieceOf(parent)) return;
                dset = new bool[count + 1];
                MarkSubtree(child, dset);
                planeBones = new bool[count + 1];
                planeBones[child.segIndex] = true;
                if (parent != null) planeBones[parent.segIndex] = true;
                fallbackD = child.segIndex;
                fallbackP = parent != null ? parent.segIndex : body.Chest.segIndex;
                center = op.a;
                float r = PartInfo.Radius[(int)child.id] * scale * 2.4f;
                radius2 = r * r;
                boneO = op.a; boneA = op.b;
                boneRadius = PartInfo.BoneRadius[(int)child.id] * scale;
            }
            else
            {
                var s = skin.SegAt(op.seg);
                if (s == null) return;
                newIndex = count;
                dset = new bool[count + 1];
                dset[newIndex] = true;
                for (int i = 0; i < s.children.Count; i++)
                {
                    var c = s.children[i];
                    if (c == null || c.Joint == null) continue;
                    Vector3 anchorInS = s.transform.InverseTransformPoint(c.transform.TransformPoint(c.Joint.anchor));
                    if (Vector3.Dot(anchorInS, s.segAxis) > op.f0) MarkSubtree(c, dset);
                }
                planeBones = new bool[count + 1];
                planeBones[s.segIndex] = true;
                fallbackP = s.segIndex;
                fallbackD = newIndex;
                center = op.a;
                radius2 = float.MaxValue;
                boneO = PartInfo.Origin[(int)s.id] * scale;
                boneA = s.segAxis;
                boneRadius = PartInfo.BoneRadius[(int)s.id] * scale;
            }
            n = op.b.normalized;
            dist = Vector3.Dot(n, op.a);
            allowedD = dset;
            allowedP = new bool[dset.Length];
            for (int i = 0; i < dset.Length; i++) allowedP[i] = !dset[i];
            prepared = true;
        }

        static void MarkSubtree(BodyPart p, bool[] set)
        {
            if (p == null) return;
            if (p.segIndex < set.Length) set[p.segIndex] = true;
            for (int i = 0; i < p.children.Count; i++)
            {
                var c = p.children[i];
                if (c != null && c.Joint != null) MarkSubtree(c, set);
            }
        }

        public override void Run()
        {
            if (!prepared) return;
            var src = piece.data;
            int nv = src.Count;
            int maxW = src.nextWeld;
            var side = new sbyte[maxW];
            var sd = new float[maxW];
            var onPlane = new bool[maxW];
            var seen = new bool[maxW];
            for (int i = 0; i < nv; i++)
            {
                int w = src.weld[i];
                if (seen[w]) continue;
                seen[w] = true;
                int dom = MeshData.Dominant(src.bw[i]);
                Vector3 p = src.pos[i];
                float d = Vector3.Dot(n, p) - dist;
                sd[w] = d;
                bool pc = dom < planeBones.Length && planeBones[dom];
                if (pc && sever && (p - center).sqrMagnitude > radius2) pc = false;
                if (pc) { side[w] = (sbyte)(d >= 0f ? 1 : 0); onPlane[w] = true; }
                else side[w] = (sbyte)(dom < dset.Length && dset[dom] ? 1 : 0);
            }
            outP = new MeshData(nv);
            outD = new MeshData(nv / 2 + 64);
            var mapP = new int[nv];
            var mapD = new int[nv];
            for (int i = 0; i < nv; i++) { mapP[i] = -1; mapD[i] = -1; }
            var edgeWeld = new Dictionary<long, int>();
            var edgeP = new Dictionary<long, int>();
            var edgeD = new Dictionary<long, int>();
            int nextWeld = maxW;
            int[] poly = new int[4];
            for (int k = 0; k < 3; k++)
            {
                var l = src.sub[k];
                for (int t = 0; t + 2 < l.Count; t += 3)
                {
                    int a = l[t], b = l[t + 1], c = l[t + 2];
                    int wa = src.weld[a], wb = src.weld[b], wc = src.weld[c];
                    int sa = side[wa], sb = side[wb], sc = side[wc];
                    if (sa == sb && sb == sc) { AddTri(sa, k, a, b, c, mapP, mapD); continue; }
                    if (onPlane[wa] && onPlane[wb] && onPlane[wc])
                    {
                        int[] rv = { a, b, c };
                        for (int h = 0; h < 2; h++)
                        {
                            var H = h == 1 ? outD : outP;
                            var map = h == 1 ? mapD : mapP;
                            var edges = h == 1 ? edgeD : edgeP;
                            int cnt = 0;
                            for (int i = 0; i < 3; i++)
                            {
                                int cur = rv[i], nxt = rv[(i + 1) % 3];
                                bool curIn = side[src.weld[cur]] == h, nxtIn = side[src.weld[nxt]] == h;
                                if (curIn) poly[cnt++] = MapVert(H, map, src, cur);
                                if (curIn != nxtIn) poly[cnt++] = EdgeVert(H, edges, edgeWeld, ref nextWeld, src, sd, cur, nxt);
                            }
                            for (int j = 1; j + 1 < cnt; j++) { H.sub[k].Add(poly[0]); H.sub[k].Add(poly[j]); H.sub[k].Add(poly[j + 1]); }
                        }
                        continue;
                    }
                    int maj = sa + sb + sc >= 2 ? 1 : 0;
                    AddTri(maj, k, a, b, c, mapP, mapD);
                }
            }
            // skin weights: each half only keeps its own bones
            for (int i = 0; i < outP.Count; i++) outP.bw[i] = MeshOps.Remap(outP.bw[i], allowedP, fallbackP);
            for (int i = 0; i < outD.Count; i++) outD.bw[i] = MeshOps.Remap(outD.bw[i], allowedD, fallbackD);
            outP.nextWeld = nextWeld;
            capCenterP = BuildCaps(outP, n, fallbackP, ref nextWeld);
            outD.nextWeld = nextWeld;
            capCenterD = BuildCaps(outD, -n, fallbackD, ref nextWeld);
            outP.nextWeld = nextWeld;
            outD.nextWeld = nextWeld;
            for (int i = 0; i < src.bones.Length; i++)
            {
                bool inD = i < dset.Length && dset[i];
                if (src.bones[i]) { if (inD) outD.SetBone(i, true); else outP.SetBone(i, true); }
            }
            if (newIndex >= 0) outD.SetBone(newIndex, true);
            if (sever) outD.SetBone(fallbackD, true);
        }

        void AddTri(int h, int k, int a, int b, int c, int[] mapP, int[] mapD)
        {
            var H = h == 1 ? outD : outP;
            var map = h == 1 ? mapD : mapP;
            var src = piece.data;
            int ia = MapVert(H, map, src, a), ib = MapVert(H, map, src, b), ic = MapVert(H, map, src, c);
            H.sub[k].Add(ia); H.sub[k].Add(ib); H.sub[k].Add(ic);
        }

        static int MapVert(MeshData H, int[] map, MeshData src, int i)
        {
            if (map[i] >= 0) return map[i];
            map[i] = H.CopyFrom(src, i);
            return map[i];
        }

        static int EdgeVert(MeshData H, Dictionary<long, int> edges, Dictionary<long, int> edgeWeld, ref int nextWeld, MeshData src, float[] sd, int r1, int r2)
        {
            int lo = Mathf.Min(r1, r2), hi = Mathf.Max(r1, r2);
            long key = MeshOps.Key(lo, hi);
            int idx;
            if (edges.TryGetValue(key, out idx)) return idx;
            int wlo = src.weld[lo], whi = src.weld[hi];
            float dl = sd[wlo], dh = sd[whi];
            float t = Mathf.Abs(dl - dh) > 1e-9f ? Mathf.Clamp01(dl / (dl - dh)) : 0.5f;
            long wk = MeshOps.Key(Mathf.Min(wlo, whi), Mathf.Max(wlo, whi));
            int w;
            if (!edgeWeld.TryGetValue(wk, out w)) { w = nextWeld++; edgeWeld[wk] = w; }
            // consistent interpolation independent of which half asks first: parameterise from the lower weld id
            if (wlo > whi) { int tmp = lo; lo = hi; hi = tmp; t = 1f - t; }
            Vector3 p = Vector3.Lerp(src.pos[lo], src.pos[hi], t);
            Vector3 nn = Vector3.Lerp(src.nrm[lo], src.nrm[hi], t).normalized;
            Vector4 tg = Vector4.Lerp(src.tan[lo], src.tan[hi], t);
            Vector2 uv = Vector2.Lerp(src.uv[lo], src.uv[hi], t);
            Vector3 rest = Vector3.Lerp(src.rest[lo], src.rest[hi], t);
            BoneWeight bw = MeshOps.Lerp(src.bw[lo], src.bw[hi], t);
            idx = H.Add(p, nn, tg, uv, bw, w, rest);
            edges[key] = idx;
            return idx;
        }

        /// <summary>Caps every boundary loop of a half with a skin/fat band, a flesh (or clay) interior and a bone disc.</summary>
        Vector3 BuildCaps(MeshData H, Vector3 desiredN, int fallback, ref int nextWeld)
        {
            if (H.Count == 0) return Vector3.zero;
            int nw = Mathf.Max(nextWeld, H.nextWeld);
            var wpos = new Vector3[nw];
            var has = new bool[nw];
            for (int i = 0; i < H.Count; i++) { int w = H.weld[i]; if (!has[w]) { has[w] = true; wpos[w] = H.pos[i]; } }
            float tol = 0.04f * scale;
            Vector3 nn = n;
            float dd = dist;
            var loops = MeshOps.BoundaryLoops(H, w => w < nw && has[w] && Mathf.Abs(Vector3.Dot(nn, wpos[w]) - dd) < tol);
            Vector3 best = Vector3.zero;
            int bestCount = 0;
            BoneWeight bw = MeshOps.Single(fallback);
            Vector3 e1 = Vector3.Cross(desiredN, Mathf.Abs(desiredN.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(desiredN, e1);
            Vector4 tg = new Vector4(e1.x, e1.y, e1.z, 1f);
            var pts = new List<Vector2>();
            var inner = new List<Vector2>();
            var tris = new List<int>();
            foreach (var loopFwd in loops)
            {
                // the cap runs against the boundary edges: reverse the loop
                var loop = new List<int>(loopFwd);
                loop.Reverse();
                int m = loop.Count;
                Vector3 c = Vector3.zero;
                for (int i = 0; i < m; i++) c += wpos[loop[i]];
                c /= m;
                if (m > bestCount) { bestCount = m; best = c; }
                pts.Clear();
                for (int i = 0; i < m; i++) { Vector3 q = wpos[loop[i]] - c; pts.Add(new Vector2(Vector3.Dot(q, e1), Vector3.Dot(q, e2))); }
                float area = 0f;
                for (int i = 0; i < m; i++) { Vector2 a = pts[i], b = pts[(i + 1) % m]; area += a.x * b.y - b.x * a.y; }
                // area > 0 means counter-clockwise around desiredN, which gives outward-facing triangles
                bool flip = area < 0f;
                const float band = 0.1f;
                float rmax = 0.001f;
                inner.Clear();
                for (int i = 0; i < m; i++) { Vector2 q = pts[i] * (1f - band); inner.Add(q); rmax = Mathf.Max(rmax, q.magnitude); }
                int baseO = H.Count;
                for (int i = 0; i <= m; i++)
                {
                    int li = i % m;
                    Vector3 p = wpos[loop[li]];
                    H.Add(p, desiredN, tg, new Vector2(0.765f, i / (float)m), bw, loop[li], p);
                }
                int baseI = H.Count;
                var innerWeld = new int[m];
                for (int i = 0; i < m; i++) innerWeld[i] = nextWeld++;
                for (int i = 0; i <= m; i++)
                {
                    int li = i % m;
                    Vector3 p = c + e1 * inner[li].x + e2 * inner[li].y;
                    H.Add(p, desiredN, tg, new Vector2(0.985f, i / (float)m), bw, innerWeld[li], p);
                }
                var capList = H.sub[1];
                for (int i = 0; i < m; i++)
                {
                    int o0 = baseO + i, o1 = baseO + i + 1, i0 = baseI + i, i1 = baseI + i + 1;
                    Tri(capList, o0, o1, i1, flip);
                    Tri(capList, o0, i1, i0, flip);
                }
                // interior polygon (flesh / clay) with planar UVs
                int baseF = H.Count;
                for (int i = 0; i < m; i++)
                {
                    Vector3 p = c + e1 * inner[i].x + e2 * inner[i].y;
                    Vector2 uv = new Vector2(0.25f + inner[i].x / rmax * 0.22f, 0.5f + inner[i].y / rmax * 0.45f);
                    H.Add(p, desiredN, tg, uv, bw, innerWeld[i], p);
                }
                tris.Clear();
                MeshOps.EarClip(inner, tris);
                for (int i = 0; i + 2 < tris.Count; i += 3) Tri(capList, baseF + tris[i], baseF + tris[i + 1], baseF + tris[i + 2], flip);
                // bone disc where the bone axis crosses the cut
                if (!clay)
                {
                    float den = Vector3.Dot(n, boneA);
                    if (Mathf.Abs(den) > 0.2f)
                    {
                        float tt = (dist - Vector3.Dot(n, boneO)) / den;
                        Vector3 bc = boneO + boneA * tt;
                        Vector3 q = bc - c;
                        Vector2 b2 = new Vector2(Vector3.Dot(q, e1), Vector3.Dot(q, e2));
                        if (MeshOps.PointInPolygon(b2, inner) && boneRadius > 0f)
                        {
                            const int seg = 12;
                            Vector3 centre = c + e1 * b2.x + e2 * b2.y + desiredN * 0.0008f * scale;
                            int cIdx = H.Add(centre, desiredN, tg, new Vector2(0.625f, 0.5f), bw, nextWeld++, centre);
                            int ring = H.Count;
                            int ringWeld = nextWeld;
                            nextWeld += seg;
                            for (int i = 0; i < seg; i++)
                            {
                                float ang = i / (float)seg * Mathf.PI * 2f;
                                Vector3 p = centre + (e1 * Mathf.Cos(ang) + e2 * Mathf.Sin(ang)) * boneRadius;
                                H.Add(p, desiredN, tg, new Vector2(0.625f + Mathf.Cos(ang) * 0.115f, 0.5f + Mathf.Sin(ang) * 0.46f), bw, ringWeld + i, p);
                            }
                            for (int i = 0; i < seg; i++) Tri(capList, cIdx, ring + i, ring + (i + 1) % seg, false);
                        }
                    }
                }
            }
            hasCap |= loops.Count > 0;
            return best;
        }

        static void Tri(List<int> l, int a, int b, int c, bool flip)
        {
            if (flip) { l.Add(a); l.Add(c); l.Add(b); }
            else { l.Add(a); l.Add(b); l.Add(c); }
        }

        bool startedPrepare;

        public override void Apply()
        {
            if (!prepared) return;
            var body = skin.Body;
            BodyPart newSeg = null;
            if (!sever)
            {
                var s = skin.SegAt(op.seg);
                if (s == null) return;
                newSeg = body.SplitSegment(s, op.f0, false);
                if (newSeg == null) return;
                if (newSeg.segIndex != newIndex)
                {
                    // should not happen (all topology changes are serialised), but stay consistent if it does
                    var allowed = new bool[body.AllPieces.Count];
                    for (int i = 0; i < allowed.Length; i++) allowed[i] = i != newIndex;
                    for (int i = 0; i < outD.Count; i++) outD.bw[i] = MeshOps.Remap(outD.bw[i], allowed, newSeg.segIndex);
                    outD.SetBone(newSeg.segIndex, true);
                }
                op.seg2 = newSeg.segIndex;
            }
            if (outP.TriangleCount > 0)
            {
                piece.data = outP;
                skin.Upload(piece, true);
            }
            if (outD.TriangleCount > 0)
            {
                BodyPart root = sever ? skin.SegAt(fallbackD) : newSeg;
                skin.AddPiece(outD, root);
            }
            body.Dismember.OnMeshCut(this, sever, skin.SegAt(fallbackP), sever ? skin.SegAt(fallbackD) : newSeg, capCenterP, capCenterD, n);
        }

        public bool Sever { get { return sever; } }
    }
    #endregion

    #region HoleJob
    public class HoleJob : MeshJob
    {
        public Vector3 center, inward;
        public float radius, depth;
        public bool crater, clay;
        public int bone;
        bool changed;

        public override void Run()
        {
            var m = piece.data;
            var l0 = m.sub[0];
            int ntri = l0.Count / 3;
            var remove = new bool[ntri];
            Vector3 outN = -inward;
            float r2 = radius * radius;
            int removed = 0;
            int nearest = -1;
            float nd = float.MaxValue;
            for (int t = 0; t < ntri; t++)
            {
                Vector3 a = m.pos[l0[t * 3]], b = m.pos[l0[t * 3 + 1]], c = m.pos[l0[t * 3 + 2]];
                Vector3 g = (a + b + c) / 3f;
                float d2 = (g - center).sqrMagnitude;
                if (d2 > r2 * 9f) continue;
                Vector3 fn = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(fn, outN) <= 0f) continue;
                if (d2 < nd) { nd = d2; nearest = t; }
                if (d2 < r2) { remove[t] = true; removed++; }
            }
            if (removed == 0)
            {
                if (nearest < 0) return;
                remove[nearest] = true;
                removed = 1;
            }
            // boundary of the removed region
            var kept = new HashSet<long>();
            for (int k = 0; k < 3; k++)
            {
                var l = m.sub[k];
                for (int t = 0; t + 2 < l.Count; t += 3)
                {
                    if (k == 0 && remove[t / 3]) continue;
                    int a = m.weld[l[t]], b = m.weld[l[t + 1]], c = m.weld[l[t + 2]];
                    kept.Add(MeshOps.Key(a, b)); kept.Add(MeshOps.Key(b, c)); kept.Add(MeshOps.Key(c, a));
                }
            }
            var next = new Dictionary<int, int>();
            var weldVert = new Dictionary<int, int>();
            for (int t = 0; t < ntri; t++)
            {
                if (!remove[t]) continue;
                for (int e = 0; e < 3; e++)
                {
                    int ra = l0[t * 3 + e], rb = l0[t * 3 + (e + 1) % 3];
                    int a = m.weld[ra], b = m.weld[rb];
                    if (!weldVert.ContainsKey(a)) weldVert[a] = ra;
                    if (kept.Contains(MeshOps.Key(b, a)) && !next.ContainsKey(a)) next[a] = b;
                }
            }
            List<int> loop = null;
            var visited = new HashSet<int>();
            foreach (var kv in next)
            {
                if (visited.Contains(kv.Key)) continue;
                var cand = new List<int>();
                int cur = kv.Key;
                bool closed = false;
                for (int guard = 0; guard < 4096; guard++)
                {
                    cand.Add(cur); visited.Add(cur);
                    int nx;
                    if (!next.TryGetValue(cur, out nx)) break;
                    if (nx == kv.Key) { closed = true; break; }
                    if (visited.Contains(nx)) break;
                    cur = nx;
                }
                if (closed && cand.Count >= 3 && (loop == null || cand.Count > loop.Count)) loop = cand;
            }
            if (loop == null) return;
            // only the region bounded by this loop is removed (other loops would leave open holes)
            var newL0 = new List<int>(l0.Count);
            for (int t = 0; t < ntri; t++) if (!remove[t]) { newL0.Add(l0[t * 3]); newL0.Add(l0[t * 3 + 1]); newL0.Add(l0[t * 3 + 2]); }
            m.sub[0] = newL0;
            int nl = loop.Count;
            // raised rim
            float rim = radius * (crater ? 0.18f : 0.12f);
            var loopSet = new HashSet<int>(loop);
            for (int i = 0; i < m.Count; i++) if (loopSet.Contains(m.weld[i])) m.pos[i] = m.pos[i] + outN * rim;
            var loopPos = new Vector3[nl];
            var loopBw = new BoneWeight[nl];
            for (int i = 0; i < nl; i++)
            {
                int rv = weldVert.ContainsKey(loop[i]) ? weldVert[loop[i]] : 0;
                loopPos[i] = m.pos[rv];
                loopBw[i] = m.bw[rv];
            }
            // tunnel rings
            float[] depthK = { 0f, 0.45f, 0.85f };
            float[] shrink = { 0f, 0.25f, 0.55f };
            float dep = depth * (crater ? 1f : 1f);
            var ringStart = new int[3];
            var ringWeld = new int[3];
            ringWeld[0] = -1;
            for (int r = 1; r < 3; r++) { ringWeld[r] = m.nextWeld; m.nextWeld += nl; }
            Vector4 tg = new Vector4(1f, 0f, 0f, 1f);
            for (int r = 0; r < 3; r++)
            {
                ringStart[r] = m.Count;
                float dz = dep * depthK[r];
                Vector3 axisPt = center + inward * dz;
                for (int i = 0; i <= nl; i++)
                {
                    int li = i % nl;
                    Vector3 p = loopPos[li] + inward * dz;
                    p = Vector3.Lerp(p, axisPt, shrink[r]);
                    int w = r == 0 ? loop[li] : ringWeld[r] + li;
                    m.Add(p, outN, tg, new Vector2(i / (float)nl, depthK[r]), loopBw[li], w, p);
                }
            }
            Vector3 bottom = center + inward * dep;
            int bIdx = m.Add(bottom, outN, tg, new Vector2(0.5f, 1f), loopBw[0], m.nextWeld++, bottom);
            var l2 = m.sub[2];
            for (int r = 0; r < 2; r++)
            {
                int A = ringStart[r], B = ringStart[r + 1];
                for (int i = 0; i < nl; i++)
                {
                    l2.Add(A + i); l2.Add(A + i + 1); l2.Add(B + i + 1);
                    l2.Add(A + i); l2.Add(B + i + 1); l2.Add(B + i);
                }
            }
            int C = ringStart[2];
            for (int i = 0; i < nl; i++) { l2.Add(C + i); l2.Add(C + i + 1); l2.Add(bIdx); }
            // normals around the hole (skin + tunnel)
            var mask = new bool[m.nextWeld];
            float reach = radius * 3f + rim;
            float reach2 = reach * reach;
            for (int i = 0; i < m.Count; i++) if ((m.pos[i] - center).sqrMagnitude < reach2 + dep * dep) mask[m.weld[i]] = true;
            MeshOps.RecomputeNormals(m, mask);
            changed = true;
        }

        public override void Apply()
        {
            if (!changed) return;
            skin.Upload(piece, true);
        }
    }
    #endregion

    #region DeformJob
    public class DeformJob : MeshJob
    {
        public BodyOp op;
        public float maxDisp;
        bool changed;

        public override void Run()
        {
            var m = piece.data;
            Vector3 c = op.a, dir = op.b.normalized;
            float R = Mathf.Max(0.005f, op.f0), amount = op.f1;
            float R2 = R * R;
            var mask = new bool[m.nextWeld];
            bool any = false;
            for (int i = 0; i < m.Count; i++)
            {
                Vector3 p = m.pos[i];
                float d2 = (p - c).sqrMagnitude;
                if (d2 >= R2) continue;
                float x = Mathf.Sqrt(d2) / R;
                float f = (1f - x * x) * (1f - x * x);
                Vector3 delta;
                switch (op.type)
                {
                    case BodyOpType.Dent: delta = dir * amount * f; break;
                    case BodyOpType.Squash: delta = -dir * Vector3.Dot(p - c, dir) * Mathf.Clamp01(amount) * f; break;
                    default: delta = dir * amount * f; break;
                }
                Vector3 np = p + delta;
                Vector3 off = np - m.rest[i];
                if (off.magnitude > maxDisp) np = m.rest[i] + off.normalized * maxDisp;
                m.pos[i] = np;
                mask[m.weld[i]] = true;
                any = true;
            }
            if (!any) return;
            MeshOps.RecomputeNormals(m, mask);
            changed = true;
        }

        public override void Apply()
        {
            if (changed) skin.Upload(piece, false);
        }
    }
    #endregion

    #region ReweightJob
    /// <summary>Fracture: vertices beyond the break are re-weighted to the new distal segment (with a short blend band
    /// so the skin bends smoothly at the kink), then the physics bone is split with a loose joint.</summary>
    public class ReweightJob : MeshJob
    {
        public BodyOp op;
        int newIndex = -1, segIndex;
        Vector3 origin, axis;
        float split, band;
        bool prepared;

        public override void Prepare()
        {
            if (prepared) return;
            var body = skin.Body;
            var s = skin.SegAt(op.seg);
            if (s == null) return;
            newIndex = body.AllPieces.Count;
            segIndex = s.segIndex;
            origin = PartInfo.Origin[(int)s.id] * body.Scale;
            axis = s.segAxis;
            float len = s.segMax - s.segMin;
            split = Mathf.Clamp(op.f0, s.segMin + len * 0.15f, s.segMax - len * 0.15f);
            band = 0.018f * body.Scale;
            prepared = true;
        }

        public override void Run()
        {
            if (!prepared) return;
            var m = piece.data;
            for (int i = 0; i < m.Count; i++)
            {
                var w = m.bw[i];
                if (w.boneIndex0 != segIndex && w.boneIndex1 != segIndex && w.boneIndex2 != segIndex && w.boneIndex3 != segIndex) continue;
                float coord = Vector3.Dot(m.pos[i] - origin, axis);
                float f = Mathf.Clamp01((coord - (split - band)) / (2f * band));
                f = f * f * (3f - 2f * f);
                if (f <= 0f) continue;
                m.bw[i] = MeshOps.Move(w, segIndex, newIndex, f);
            }
            m.SetBone(newIndex, true);
        }

        public override void Apply()
        {
            if (!prepared) return;
            var body = skin.Body;
            var s = skin.SegAt(op.seg);
            if (s == null) return;
            var n = body.SplitSegment(s, split, true);
            if (n == null) return;
            op.seg2 = n.segIndex;
            skin.Upload(piece, true);
            body.Damage.OnFractureApplied(s, n, split);
        }
    }
    #endregion
}
