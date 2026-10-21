// Ragdoll Sandbox - procedural content factories: textures and sprites, materials (URP with built-in fallbacks)
// and generic meshes (boxes with world-scale UVs, cylinders, lathes, tori, ring tubes, heightfields...).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region TextureFactory
    /// <summary>Float RGBA pixel canvas with wrap-around drawing, used to paint every procedural texture.</summary>
    public class PixelCanvas
    {
        public readonly int W, H;
        public readonly Color[] Px;
        public bool Wrap = true;

        public PixelCanvas(int w, int h, Color fill)
        {
            W = w; H = h;
            Px = new Color[w * h];
            for (int i = 0; i < Px.Length; i++) Px[i] = fill;
        }

        public int Index(int x, int y)
        {
            if (Wrap)
            {
                x %= W; if (x < 0) x += W;
                y %= H; if (y < 0) y += H;
            }
            else if (x < 0 || y < 0 || x >= W || y >= H) return -1;
            return y * W + x;
        }

        public void Blend(int x, int y, Color c, float a)
        {
            int i = Index(x, y);
            if (i < 0 || a <= 0f) return;
            if (a > 1f) a = 1f;
            Color d = Px[i];
            Px[i] = new Color(d.r + (c.r - d.r) * a, d.g + (c.g - d.g) * a, d.b + (c.b - d.b) * a, d.a + (c.a - d.a) * a);
        }

        /// <summary>Alpha-over that also accumulates alpha (for transparent stamps).</summary>
        public void Over(int x, int y, Color c, float a)
        {
            int i = Index(x, y);
            if (i < 0 || a <= 0f) return;
            if (a > 1f) a = 1f;
            Color d = Px[i];
            float outA = a + d.a * (1f - a);
            if (outA < 1e-5f) { Px[i] = new Color(0, 0, 0, 0); return; }
            Px[i] = new Color((c.r * a + d.r * d.a * (1f - a)) / outA, (c.g * a + d.g * d.a * (1f - a)) / outA,
                (c.b * a + d.b * d.a * (1f - a)) / outA, outA);
        }

        public void Circle(float cx, float cy, float r, Color c, float soft, bool over = false)
        {
            int x0 = Mathf.FloorToInt(cx - r - 1), x1 = Mathf.CeilToInt(cx + r + 1);
            int y0 = Mathf.FloorToInt(cy - r - 1), y1 = Mathf.CeilToInt(cy + r + 1);
            float s = Mathf.Max(0.5f, soft);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((r - d) / s + 0.5f) * c.a;
                    if (a <= 0f) continue;
                    if (over) Over(x, y, c, a); else Blend(x, y, c, a);
                }
        }

        public void Ellipse(float cx, float cy, float rx, float ry, float angle, Color c, float soft, bool over = false)
        {
            float rmax = Mathf.Max(rx, ry) + 2f;
            float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
            int x0 = Mathf.FloorToInt(cx - rmax), x1 = Mathf.CeilToInt(cx + rmax);
            int y0 = Mathf.FloorToInt(cy - rmax), y1 = Mathf.CeilToInt(cy + rmax);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float lx = (dx * ca + dy * sa) / Mathf.Max(0.01f, rx);
                    float ly = (-dx * sa + dy * ca) / Mathf.Max(0.01f, ry);
                    float d = Mathf.Sqrt(lx * lx + ly * ly);
                    float edge = soft / Mathf.Max(1f, Mathf.Min(rx, ry));
                    float a = Mathf.Clamp01((1f - d) / Mathf.Max(0.001f, edge) + 0.5f) * c.a;
                    if (a <= 0f) continue;
                    if (over) Over(x, y, c, a); else Blend(x, y, c, a);
                }
        }

        public void Line(float x0, float y0, float x1, float y1, float width, Color c, bool over = false)
        {
            float len = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, width * 0.35f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Circle(Mathf.Lerp(x0, x1, t), Mathf.Lerp(y0, y1, t), width * 0.5f, c, 0.8f, over);
            }
        }

        public void Rect(float x0, float y0, float x1, float y1, Color c, bool over = false)
        {
            for (int y = Mathf.FloorToInt(y0); y < Mathf.CeilToInt(y1); y++)
                for (int x = Mathf.FloorToInt(x0); x < Mathf.CeilToInt(x1); x++)
                {
                    if (over) Over(x, y, c, c.a); else Blend(x, y, c, c.a);
                }
        }

        public void Polygon(Vector2[] pts, Color c, bool over = false)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                minX = Mathf.Min(minX, pts[i].x); maxX = Mathf.Max(maxX, pts[i].x);
                minY = Mathf.Min(minY, pts[i].y); maxY = Mathf.Max(maxY, pts[i].y);
            }
            for (int y = Mathf.FloorToInt(minY); y <= Mathf.CeilToInt(maxY); y++)
                for (int x = Mathf.FloorToInt(minX); x <= Mathf.CeilToInt(maxX); x++)
                {
                    // 2x2 supersampling for soft edges
                    int inside = 0;
                    for (int s = 0; s < 4; s++)
                    {
                        float px = x + 0.25f + 0.5f * (s & 1), py = y + 0.25f + 0.5f * (s >> 1);
                        if (PointInPoly(pts, px, py)) inside++;
                    }
                    if (inside == 0) continue;
                    float a = inside / 4f * c.a;
                    if (over) Over(x, y, c, a); else Blend(x, y, c, a);
                }
        }

        static bool PointInPoly(Vector2[] p, float x, float y)
        {
            bool c = false;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                if (((p[i].y > y) != (p[j].y > y)) && (x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x)) c = !c;
            }
            return c;
        }

        public Texture2D ToTexture(string name, bool mips, TextureWrapMode wrap, FilterMode filter = FilterMode.Bilinear)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, mips);
            t.name = name;
            t.wrapMode = wrap;
            t.filterMode = filter;
            t.anisoLevel = mips ? 4 : 1;
            t.SetPixels(Px);
            t.Apply(mips, false);
            return t;
        }
    }

    /// <summary>Tileable value noise and fBm used for every procedural surface.</summary>
    public static class Noise
    {
        public static int Seed = 1337;

        static float Lattice(int x, int y, int period, int seed)
        {
            if (period > 0)
            {
                x %= period; if (x < 0) x += period;
                y %= period; if (y < 0) y += period;
            }
            return MathUtil.Hash01(x, y, seed + Seed);
        }

        /// <summary>Periodic value noise in [0,1]. u,v in [0,1) wrap with the given integer frequency.</summary>
        public static float Value(float u, float v, int freq, int seed)
        {
            float x = u * freq, y = v * freq;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = Lattice(ix, iy, freq, seed), b = Lattice(ix + 1, iy, freq, seed);
            float c = Lattice(ix, iy + 1, freq, seed), d = Lattice(ix + 1, iy + 1, freq, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        public static float Fbm(float u, float v, int baseFreq, int octaves, int seed, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int f = baseFreq;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(u, v, f, seed + o * 31) * amp;
                norm += amp;
                amp *= gain;
                f *= 2;
            }
            return sum / norm;
        }

        public static float Ridged(float u, float v, int baseFreq, int octaves, int seed)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int f = baseFreq;
            for (int o = 0; o < octaves; o++)
            {
                float n = 1f - Mathf.Abs(Value(u, v, f, seed + o * 57) * 2f - 1f);
                sum += n * n * amp;
                norm += amp;
                amp *= 0.5f;
                f *= 2;
            }
            return sum / norm;
        }

        /// <summary>Periodic cellular (Worley) noise: returns distance to nearest and second nearest feature (0..~1).</summary>
        public static Vector2 Cellular(float u, float v, int freq, int seed)
        {
            float x = u * freq, y = v * freq;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float d1 = 10f, d2 = 10f;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = ix + ox, cy = iy + oy;
                    float px = cx + Lattice(cx, cy, freq, seed);
                    float py = cy + Lattice(cx, cy, freq, seed + 101);
                    float dx = px - x, dy = py - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) d2 = d;
                }
            return new Vector2(d1, d2);
        }

        /// <summary>Non-periodic 2D Perlin fBm for terrain.</summary>
        public static float Perlin(float x, float y, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f, f = 1f;
            float ox = Seed * 0.137f % 1000f, oy = Seed * 0.311f % 1000f;
            for (int i = 0; i < octaves; i++)
            {
                sum += (Mathf.PerlinNoise(x * f + ox + i * 17.3f, y * f + oy + i * 9.7f) - 0.5f) * amp;
                norm += amp;
                amp *= 0.5f;
                f *= 2f;
            }
            return sum / norm;
        }
    }

    public enum SplatCell
    {
        Blob0 = 0, Blob11 = 11, Streak0 = 12, Streak5 = 17, Drip0 = 18, Drip3 = 21, FootL = 22, FootR = 23, Hand = 24,
        Scorch0 = 25, Scorch1 = 26, Hole0 = 27, Hole1 = 28, Smear0 = 29, Smear1 = 30, Crack = 31
    }

    public static class TextureFactory
    {
        public const int AtlasCols = 8, AtlasRows = 4, AtlasCell = 64;

        public static Texture2D Concrete, Tiles, Grass, Dirt, Wood, Crate, Metal, Tread, Brick, Rock, Rubber, Hazard, BarrelRed,
            BarrelSteel, Asphalt, Sand, PaintBlue, PaintOrange, PaintGreen, Flesh, Bone, CapLimb, CapTorso, White;
        public static Texture2D SplatAtlas, PuddleAtlas;
        public static Texture2D SoftParticle, Droplet, Spark, Fire, Smoke, Ring, Flare, Mist;
        public static Sprite RoundedRect, RoundedSmall, Circle, Vignette, WhiteSprite, Gradient;
        public static Sprite[] ToolIcons = new Sprite[0];

        public static void BuildAll(int seed, int worldSize)
        {
            Noise.Seed = seed;
            int big = Mathf.Clamp(worldSize, 128, 1024);
            int small = Mathf.Max(128, big / 2);
            White = Solid(4, Color.white);
            Concrete = MakeConcrete(big, seed);
            Tiles = MakeTiles(big, seed + 1);
            Grass = MakeGrass(big, seed + 2);
            Dirt = MakeDirt(small, seed + 3);
            Wood = MakeWood(small, seed + 4);
            Crate = MakeCrate(small, seed + 5);
            Metal = MakeMetal(small, seed + 6);
            Tread = MakeTread(small, seed + 7);
            Brick = MakeBrick(small, seed + 8);
            Rock = MakeRock(small, seed + 9);
            Rubber = MakeRubber(small, seed + 10);
            Hazard = MakeHazard(small);
            BarrelRed = MakeBarrel(small, new Color(0.72f, 0.1f, 0.07f), true, seed + 11);
            BarrelSteel = MakeBarrel(small, new Color(0.35f, 0.42f, 0.48f), false, seed + 12);
            Asphalt = MakeAsphalt(small, seed + 13);
            Sand = MakeSand(small, seed + 14);
            PaintBlue = MakePainted(small, new Color(0.16f, 0.36f, 0.62f), seed + 15);
            PaintOrange = MakePainted(small, new Color(0.9f, 0.45f, 0.12f), seed + 16);
            PaintGreen = MakePainted(small, new Color(0.25f, 0.5f, 0.32f), seed + 17);
            Flesh = MakeFlesh(128, seed + 18);
            Bone = MakeBone(64, seed + 19);
            CapLimb = MakeCap(128, false);
            CapTorso = MakeCap(128, true);
            SplatAtlas = MakeSplatAtlas(seed + 20);
            PuddleAtlas = MakePuddleAtlas(seed + 21);
            SoftParticle = MakeSoft(64, 2.2f);
            Mist = MakeSoft(64, 1.2f);
            Droplet = MakeDroplet(32);
            Spark = MakeSpark(64);
            Fire = MakeFire(64, seed + 22);
            Smoke = MakeSmoke(64, seed + 23);
            Ring = MakeRing(128);
            Flare = MakeSoft(64, 3.5f);
            BuildSprites();
        }

        public static Texture2D Solid(int size, Color c)
        {
            var pc = new PixelCanvas(size, size, c);
            return pc.ToTexture("Solid", false, TextureWrapMode.Repeat);
        }

        static Color Vary(Color c, float n, float amount)
        {
            float k = 1f + (n - 0.5f) * 2f * amount;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        static Texture2D MakeConcrete(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            Color baseC = new Color(0.58f, 0.57f, 0.55f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float large = Noise.Fbm(u, v, 3, 4, seed);
                    float fine = Noise.Value(u, v, s / 2, seed + 7);
                    float stain = Noise.Fbm(u, v, 5, 3, seed + 3);
                    Color c = Vary(baseC, large, 0.12f);
                    c = Vary(c, fine, 0.06f);
                    if (stain > 0.62f) c *= Mathf.Lerp(1f, 0.82f, (stain - 0.62f) * 4f);
                    float cr = Noise.Ridged(u, v, 4, 3, seed + 9);
                    if (cr > 0.86f) c *= Mathf.Lerp(1f, 0.7f, (cr - 0.86f) * 7f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            var r = new Rng(seed);
            for (int i = 0; i < s * 2; i++)
            {
                float g = r.Range(0.25f, 0.85f);
                pc.Circle(r.Range(0, s), r.Range(0, s), r.Range(0.4f, 1.3f), new Color(g, g, g * 0.97f, 0.35f), 0.8f);
            }
            return pc.ToTexture("Concrete", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeTiles(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            int tiles = 2;
            int tileSize = s / tiles;
            Color baseC = new Color(0.66f, 0.64f, 0.6f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    int tx = x / tileSize, ty = y / tileSize;
                    float tileVar = MathUtil.Hash01(tx, ty, seed);
                    Color c = Vary(baseC, tileVar, 0.08f);
                    c = Vary(c, Noise.Fbm(u, v, 4, 4, seed), 0.1f);
                    c = Vary(c, Noise.Value(u, v, s / 2, seed + 5), 0.05f);
                    int lx = x % tileSize, ly = y % tileSize;
                    int edge = Mathf.Min(Mathf.Min(lx, tileSize - 1 - lx), Mathf.Min(ly, tileSize - 1 - ly));
                    if (edge < 3) c = new Color(0.32f, 0.31f, 0.3f);
                    else if (edge < 6) c *= 0.92f;
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Tiles", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeGrass(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.green);
            Color a = new Color(0.27f, 0.45f, 0.18f), b = new Color(0.42f, 0.55f, 0.22f), dry = new Color(0.55f, 0.55f, 0.3f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float n = Noise.Fbm(u, v, 4, 5, seed);
                    float d = Noise.Fbm(u, v, 3, 3, seed + 4);
                    Color c = Color.Lerp(a, b, n);
                    if (d > 0.63f) c = Color.Lerp(c, dry, (d - 0.63f) * 2.5f);
                    c = Vary(c, Noise.Value(u, v, s / 2, seed + 2), 0.08f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            var r = new Rng(seed);
            int blades = s * s / 10;
            for (int i = 0; i < blades; i++)
            {
                float x = r.Range(0, s), y = r.Range(0, s);
                float len = r.Range(2f, 6f) * s / 256f;
                float ang = r.Range(-0.6f, 0.6f) + Mathf.PI * 0.5f;
                float g = r.Range(0.75f, 1.25f);
                Color c = new Color(0.3f * g, 0.5f * g, 0.2f * g, 0.5f);
                pc.Line(x, y, x + Mathf.Cos(ang) * len, y + Mathf.Sin(ang) * len, 1f, c);
            }
            return pc.ToTexture("Grass", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeDirt(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            Color a = new Color(0.36f, 0.27f, 0.18f), b = new Color(0.5f, 0.4f, 0.28f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Color.Lerp(a, b, Noise.Fbm(u, v, 4, 5, seed));
                    c = Vary(c, Noise.Value(u, v, s / 2, seed + 1), 0.1f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            var r = new Rng(seed);
            for (int i = 0; i < s; i++)
            {
                float x = r.Range(0, s), y = r.Range(0, s), rad = r.Range(1f, 3.5f) * s / 256f;
                float g = r.Range(0.35f, 0.65f);
                pc.Circle(x, y, rad, new Color(g, g * 0.9f, g * 0.8f, 0.8f), 1f);
                pc.Circle(x - rad * 0.3f, y + rad * 0.3f, rad * 0.45f, new Color(g + 0.15f, g + 0.12f, g + 0.1f, 0.5f), 1f);
            }
            return pc.ToTexture("Dirt", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeWood(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            int planks = 4;
            int pw = s / planks;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    int p = x / pw;
                    float pv = MathUtil.Hash01(p, 0, seed);
                    Color baseC = Color.Lerp(new Color(0.5f, 0.33f, 0.18f), new Color(0.68f, 0.5f, 0.3f), pv);
                    float warp = Noise.Fbm(u, v, 2, 3, seed + p) * 6f;
                    float grain = Mathf.Sin((u * planks * 14f + warp) * Mathf.PI * 2f) * 0.5f + 0.5f;
                    Color c = Vary(baseC, grain, 0.12f);
                    c = Vary(c, Noise.Value(u, v * 0.2f, s / 2, seed + 3), 0.05f);
                    int lx = x % pw;
                    if (lx < 2 || lx > pw - 3) c *= 0.55f;
                    int seam = (int)((v + pv) * 2f * s) % s;
                    if (seam < 2) c *= 0.6f;
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            var r = new Rng(seed);
            for (int k = 0; k < 6; k++)
            {
                float kx = r.Range(0, s), ky = r.Range(0, s);
                for (int ring = 5; ring > 0; ring--)
                    pc.Ellipse(kx, ky, ring * 1.6f * s / 256f, ring * 3.2f * s / 256f, 0f, new Color(0.3f, 0.18f, 0.08f, 0.25f), 1f);
            }
            return pc.ToTexture("Wood", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeCrate(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    int plank = y / (s / 5);
                    float pv = MathUtil.Hash01(plank, 3, seed);
                    Color baseC = Color.Lerp(new Color(0.55f, 0.38f, 0.2f), new Color(0.7f, 0.52f, 0.3f), pv);
                    float grain = Mathf.Sin((v * 60f + Noise.Fbm(u, v, 3, 3, seed) * 5f) * Mathf.PI) * 0.5f + 0.5f;
                    Color c = Vary(baseC, grain, 0.1f);
                    if (y % (s / 5) < 2) c *= 0.55f;
                    int border = s / 9;
                    if (x < border || x > s - border || y < border || y > s - border)
                    {
                        c = Vary(new Color(0.42f, 0.28f, 0.14f), Noise.Fbm(u, v, 6, 3, seed + 9), 0.12f);
                        if (x < 2 || y < 2 || x > s - 3 || y > s - 3) c *= 0.5f;
                    }
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            float b = s / 9f;
            pc.Line(b, b, s - b, s - b, s / 12f, new Color(0.42f, 0.28f, 0.14f, 1f));
            for (int i = 0; i < 4; i++)
            {
                float cx = i % 2 == 0 ? b * 0.5f : s - b * 0.5f, cy = i < 2 ? b * 0.5f : s - b * 0.5f;
                pc.Circle(cx, cy, s / 60f + 1f, new Color(0.25f, 0.25f, 0.27f, 1f), 1f);
            }
            return pc.ToTexture("Crate", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeMetal(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            Color baseC = new Color(0.55f, 0.57f, 0.6f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float brushed = Noise.Value(u * 0.05f, v, s / 2, seed);
                    Color c = Vary(baseC, brushed, 0.07f);
                    c = Vary(c, Noise.Fbm(u, v, 3, 4, seed + 2), 0.1f);
                    float rust = Noise.Fbm(u, v, 4, 4, seed + 5);
                    if (rust > 0.66f) c = Color.Lerp(c, new Color(0.45f, 0.25f, 0.12f), (rust - 0.66f) * 3f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            var r = new Rng(seed);
            for (int i = 0; i < 40; i++)
            {
                float x = r.Range(0, s), y = r.Range(0, s), a = r.Range(0f, Mathf.PI), l = r.Range(5f, 30f) * s / 256f;
                pc.Line(x, y, x + Mathf.Cos(a) * l, y + Mathf.Sin(a) * l, 0.8f, new Color(0.75f, 0.77f, 0.8f, 0.35f));
            }
            return pc.ToTexture("Metal", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeTread(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            Color baseC = new Color(0.5f, 0.52f, 0.55f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Vary(baseC, Noise.Fbm(u, v, 4, 4, seed), 0.1f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            int n = 8;
            float cell = s / (float)n;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float cx = (i + 0.5f) * cell, cy = (j + 0.5f) * cell;
                    float ang = ((i + j) % 2 == 0) ? 0.785f : -0.785f;
                    pc.Ellipse(cx + 1, cy - 1, cell * 0.36f, cell * 0.09f, ang, new Color(0.25f, 0.25f, 0.27f, 0.7f), 1.5f);
                    pc.Ellipse(cx, cy, cell * 0.35f, cell * 0.08f, ang, new Color(0.72f, 0.74f, 0.77f, 1f), 1.5f);
                }
            return pc.ToTexture("Tread", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeBrick(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            int rows = 8, cols = 4;
            int bh = s / rows, bw = s / cols;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    int row = y / bh;
                    int off = (row % 2) * bw / 2;
                    int col = ((x + off) % s) / bw;
                    float bv = MathUtil.Hash01(col, row, seed);
                    Color baseC = Color.Lerp(new Color(0.55f, 0.22f, 0.15f), new Color(0.72f, 0.38f, 0.26f), bv);
                    Color c = Vary(baseC, Noise.Fbm(u, v, 8, 3, seed + 2), 0.15f);
                    c = Vary(c, Noise.Value(u, v, s / 2, seed + 3), 0.08f);
                    int lx = (x + off) % bw, ly = y % bh;
                    int mortar = Math.Max(2, s / 128);
                    if (lx < mortar || ly < mortar) c = Vary(new Color(0.68f, 0.66f, 0.6f), Noise.Value(u, v, s / 4, seed + 4), 0.1f);
                    else if (lx < mortar + 2 || ly < mortar + 2) c *= 0.85f;
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Brick", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeRock(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Vector2 cell = Noise.Cellular(u, v, 6, seed);
                    float edges = Mathf.Clamp01((cell.y - cell.x) * 4f);
                    float n = Noise.Fbm(u, v, 4, 5, seed + 1);
                    Color c = Color.Lerp(new Color(0.38f, 0.37f, 0.36f), new Color(0.6f, 0.58f, 0.55f), n);
                    c *= Mathf.Lerp(0.6f, 1f, edges);
                    float lichen = Noise.Fbm(u, v, 5, 3, seed + 6);
                    if (lichen > 0.68f) c = Color.Lerp(c, new Color(0.45f, 0.5f, 0.3f), (lichen - 0.68f) * 2.5f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Rock", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeRubber(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float g = 0.12f + Noise.Fbm(u, v, 8, 3, seed) * 0.06f;
                    float tread = Mathf.Abs(Mathf.Sin((u * 16f + Mathf.Abs((v * 8f) % 1f - 0.5f) * 0.8f) * Mathf.PI));
                    if (tread < 0.25f) g *= 0.6f;
                    pc.Px[y * s + x] = new Color(g, g, g * 1.05f, 1f);
                }
            return pc.ToTexture("Rubber", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeHazard(int s)
        {
            var pc = new PixelCanvas(s, s, Color.yellow);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = ((x + y) / (float)s * 4f) % 1f;
                    Color c = d < 0.5f ? new Color(0.95f, 0.75f, 0.1f) : new Color(0.08f, 0.08f, 0.08f);
                    c = Vary(c, Noise.Value(x / (float)s, y / (float)s, 32, 77), 0.08f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Hazard", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeBarrel(int s, Color paint, bool explosive, int seed)
        {
            var pc = new PixelCanvas(s, s, paint);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Vary(paint, Noise.Fbm(u, v, 4, 4, seed), 0.12f);
                    float rib = Mathf.Abs(v - 0.33f) < 0.02f || Mathf.Abs(v - 0.66f) < 0.02f ? 0.65f : 1f;
                    c *= rib;
                    float wear = Noise.Fbm(u, v, 8, 3, seed + 3);
                    if (wear > 0.7f) c = Color.Lerp(c, new Color(0.4f, 0.4f, 0.42f), (wear - 0.7f) * 3f);
                    if (explosive && v > 0.42f && v < 0.58f)
                    {
                        float d = ((u * 8f + v * 4f) % 1f);
                        c = d < 0.5f ? new Color(0.95f, 0.78f, 0.1f) : new Color(0.1f, 0.1f, 0.1f);
                    }
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture(explosive ? "BarrelRed" : "BarrelSteel", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeAsphalt(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float n = Noise.Fbm(u, v, 4, 4, seed) * 0.08f + Noise.Value(u, v, s / 2, seed + 1) * 0.1f;
                    float g = 0.17f + n;
                    pc.Px[y * s + x] = new Color(g, g, g * 1.03f, 1f);
                }
            var r = new Rng(seed);
            for (int i = 0; i < s * 3; i++)
            {
                float g = r.Range(0.25f, 0.5f);
                pc.Circle(r.Range(0, s), r.Range(0, s), r.Range(0.4f, 1.1f), new Color(g, g, g, 0.6f), 0.7f);
            }
            return pc.ToTexture("Asphalt", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeSand(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.gray);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Color.Lerp(new Color(0.72f, 0.64f, 0.48f), new Color(0.82f, 0.74f, 0.58f), Noise.Fbm(u, v, 4, 4, seed));
                    c = Vary(c, Noise.Value(u, v, s / 2, seed + 1), 0.08f);
                    float ripple = Mathf.Sin((v * 20f + Noise.Fbm(u, v, 2, 2, seed + 4) * 4f) * Mathf.PI * 2f);
                    c *= 1f + ripple * 0.03f;
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Sand", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakePainted(int s, Color paint, int seed)
        {
            var pc = new PixelCanvas(s, s, paint);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Vary(paint, Noise.Fbm(u, v, 3, 4, seed), 0.1f);
                    float chip = Noise.Fbm(u, v, 6, 4, seed + 1);
                    if (chip > 0.7f) c = Color.Lerp(c, new Color(0.45f, 0.46f, 0.48f), Mathf.Clamp01((chip - 0.7f) * 6f));
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Painted", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeFlesh(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.red);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float n = Noise.Fbm(u, v, 4, 4, seed);
                    float fib = Mathf.Sin((v * 40f + n * 3f) * Mathf.PI) * 0.5f + 0.5f;
                    Color c = Color.Lerp(new Color(0.45f, 0.06f, 0.06f), new Color(0.72f, 0.2f, 0.2f), n * 0.7f + fib * 0.3f);
                    float vein = Noise.Ridged(u, v, 3, 3, seed + 3);
                    if (vein > 0.82f) c = Color.Lerp(c, new Color(0.3f, 0.05f, 0.12f), (vein - 0.82f) * 5f);
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Flesh", true, TextureWrapMode.Repeat);
        }

        static Texture2D MakeBone(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, Color.white);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    Color c = Color.Lerp(new Color(0.82f, 0.78f, 0.68f), new Color(0.95f, 0.93f, 0.86f), Noise.Fbm(u, v, 4, 3, seed));
                    c.a = 1f;
                    pc.Px[y * s + x] = c;
                }
            return pc.ToTexture("Bone", true, TextureWrapMode.Repeat);
        }

        /// <summary>Cross-section cap texture: skin ring, fat, muscle, bone and marrow (limb) or abdominal cavity (torso).</summary>
        static Texture2D MakeCap(int s, bool torso)
        {
            var pc = new PixelCanvas(s, s, new Color(0.5f, 0.06f, 0.06f));
            pc.Wrap = false;
            float c = s * 0.5f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f - c) / c, dy = (y + 0.5f - c) / c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = Noise.Fbm(x / (float)s, y / (float)s, 6, 3, 99);
                    Color col;
                    if (r > 0.92f) col = new Color(0.86f, 0.8f, 0.76f);
                    else if (r > 0.84f) col = new Color(0.93f, 0.82f, 0.45f);
                    else if (!torso)
                    {
                        float bx = dx, by = dy + 0.05f;
                        float br = Mathf.Sqrt(bx * bx + by * by);
                        if (br < 0.14f) col = new Color(0.45f, 0.08f, 0.08f);
                        else if (br < 0.26f) col = new Color(0.93f, 0.9f, 0.82f);
                        else col = Color.Lerp(new Color(0.55f, 0.08f, 0.08f), new Color(0.75f, 0.18f, 0.16f), n);
                    }
                    else
                    {
                        float sx = dx, sy = dy + 0.62f;
                        float sr = Mathf.Sqrt(sx * sx + sy * sy);
                        if (r > 0.72f) col = Color.Lerp(new Color(0.55f, 0.08f, 0.08f), new Color(0.72f, 0.17f, 0.15f), n);
                        else if (sr < 0.18f) col = sr < 0.09f ? new Color(0.5f, 0.12f, 0.1f) : new Color(0.92f, 0.88f, 0.8f);
                        else
                        {
                            float coil = Mathf.Sin(dx * 18f + Mathf.Sin(dy * 9f) * 2f) * Mathf.Sin(dy * 16f + Mathf.Cos(dx * 7f) * 2f);
                            col = coil > 0.1f ? new Color(0.85f, 0.55f, 0.52f) : new Color(0.35f, 0.04f, 0.05f);
                        }
                    }
                    if (r > 1f) col = new Color(0.4f, 0.04f, 0.04f);
                    col.a = 1f;
                    pc.Px[y * s + x] = col;
                }
            return pc.ToTexture(torso ? "CapTorso" : "CapLimb", true, TextureWrapMode.Clamp);
        }

        public static Rect AtlasRect(int cell)
        {
            int col = cell % AtlasCols, row = cell / AtlasCols;
            float w = 1f / AtlasCols, h = 1f / AtlasRows;
            float pad = 0.5f / (AtlasCols * AtlasCell);
            return new Rect(col * w + pad, row * h + pad, w - pad * 2f, h - pad * 2f);
        }

        static Texture2D MakeSplatAtlas(int seed)
        {
            int W = AtlasCols * AtlasCell, H = AtlasRows * AtlasCell;
            var pc = new PixelCanvas(W, H, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            var r = new Rng(seed);
            Color w = Color.white;
            float C = AtlasCell;
            for (int cell = 0; cell < AtlasCols * AtlasRows; cell++)
            {
                float ox = (cell % AtlasCols) * C, oy = (cell / AtlasCols) * C;
                float cx = ox + C * 0.5f, cy = oy + C * 0.5f;
                if (cell <= (int)SplatCell.Blob11)
                {
                    float main = C * r.Range(0.16f, 0.24f);
                    pc.Circle(cx, cy, main, w, 1.5f, true);
                    int lobes = r.Range(4, 9);
                    for (int i = 0; i < lobes; i++)
                    {
                        float a = r.Range(0f, Mathf.PI * 2f), d = main * r.Range(0.5f, 1.0f);
                        pc.Circle(cx + Mathf.Cos(a) * d, cy + Mathf.Sin(a) * d, main * r.Range(0.35f, 0.6f), w, 1.2f, true);
                    }
                    int drops = r.Range(6, 16);
                    for (int i = 0; i < drops; i++)
                    {
                        float a = r.Range(0f, Mathf.PI * 2f), d = main * r.Range(1.2f, 2.0f);
                        float dr = r.Range(0.6f, 2.4f);
                        float px = Mathf.Clamp(cx + Mathf.Cos(a) * d, ox + 3, ox + C - 3), py = Mathf.Clamp(cy + Mathf.Sin(a) * d, oy + 3, oy + C - 3);
                        pc.Circle(px, py, dr, w, 1f, true);
                    }
                }
                else if (cell <= (int)SplatCell.Streak5)
                {
                    float len = C * r.Range(0.3f, 0.42f);
                    pc.Ellipse(cx - len * 0.2f, cy, len * 0.5f, C * r.Range(0.07f, 0.11f), 0f, w, 1.5f, true);
                    for (int i = 0; i < 9; i++)
                    {
                        float t = i / 8f;
                        float px = cx + len * 0.2f + t * len * 0.65f;
                        float py = cy + r.Range(-3f, 3f) * t;
                        float pr = Mathf.Lerp(C * 0.05f, 0.7f, t);
                        pc.Circle(Mathf.Min(px, ox + C - 3), py, pr, w, 1f, true);
                    }
                }
                else if (cell <= (int)SplatCell.Drip3)
                {
                    pc.Ellipse(cx, oy + C * 0.8f, C * 0.22f, C * 0.1f, 0f, w, 1.5f, true);
                    int lines = r.Range(3, 6);
                    for (int i = 0; i < lines; i++)
                    {
                        float x = cx + r.Range(-C * 0.18f, C * 0.18f);
                        float y0 = oy + C * 0.8f, y1 = oy + C * r.Range(0.06f, 0.5f);
                        float wd = r.Range(1.5f, 3f);
                        pc.Line(x, y0, x, y1, wd, w, true);
                        pc.Circle(x, y1, wd * 0.9f, w, 1f, true);
                    }
                }
                else if (cell == (int)SplatCell.FootL || cell == (int)SplatCell.FootR)
                {
                    float m = cell == (int)SplatCell.FootL ? -1f : 1f;
                    pc.Ellipse(cx + m * 1.5f, cy + C * 0.1f, C * 0.12f, C * 0.2f, m * 0.1f, w, 1.5f, true);
                    pc.Ellipse(cx, cy - C * 0.22f, C * 0.09f, C * 0.11f, 0f, w, 1.5f, true);
                    for (int t = 0; t < 5; t++)
                        pc.Circle(cx + m * (-C * 0.1f + t * C * 0.05f), cy + C * 0.34f - Mathf.Abs(t - 1.5f) * 1.5f, C * (t == 0 ? 0.045f : 0.03f), w, 1f, true);
                }
                else if (cell == (int)SplatCell.Hand)
                {
                    pc.Ellipse(cx, cy - C * 0.08f, C * 0.17f, C * 0.18f, 0f, w, 1.5f, true);
                    for (int f = 0; f < 4; f++)
                    {
                        float fx = cx - C * 0.12f + f * C * 0.08f;
                        pc.Line(fx, cy + C * 0.08f, fx, cy + C * (0.3f + (f == 1 || f == 2 ? 0.06f : 0f)), C * 0.06f, w, true);
                    }
                    pc.Line(cx + C * 0.16f, cy - C * 0.05f, cx + C * 0.3f, cy + C * 0.1f, C * 0.065f, w, true);
                }
                else if (cell == (int)SplatCell.Scorch0 || cell == (int)SplatCell.Scorch1)
                {
                    for (int y = 0; y < AtlasCell; y++)
                        for (int x = 0; x < AtlasCell; x++)
                        {
                            float dx = (x + 0.5f - C * 0.5f) / (C * 0.5f), dy = (y + 0.5f - C * 0.5f) / (C * 0.5f);
                            float rr = Mathf.Sqrt(dx * dx + dy * dy);
                            float ang = Mathf.Atan2(dy, dx);
                            float rays = 0.75f + 0.25f * Mathf.Sin(ang * 9f + cell) * Mathf.Sin(ang * 4f);
                            float n = Noise.Value(x / C, y / C, 8, seed + cell);
                            float a = Mathf.Clamp01((rays - rr) * 2.5f) * (0.7f + n * 0.3f);
                            pc.Over((int)ox + x, (int)oy + y, new Color(0.04f, 0.035f, 0.03f), a);
                        }
                }
                else if (cell == (int)SplatCell.Hole0 || cell == (int)SplatCell.Hole1)
                {
                    pc.Circle(cx, cy, C * 0.16f, new Color(0.3f, 0.3f, 0.3f, 0.6f), 2f, true);
                    for (int i = 0; i < 7; i++)
                    {
                        float a = r.Range(0f, Mathf.PI * 2f), l = C * r.Range(0.15f, 0.32f);
                        pc.Line(cx, cy, cx + Mathf.Cos(a) * l, cy + Mathf.Sin(a) * l, 1f, new Color(0.1f, 0.1f, 0.1f, 0.6f), true);
                    }
                    pc.Circle(cx, cy, C * 0.07f, new Color(0.02f, 0.02f, 0.02f, 1f), 1f, true);
                }
                else if (cell == (int)SplatCell.Smear0 || cell == (int)SplatCell.Smear1)
                {
                    for (int y = 0; y < AtlasCell; y++)
                        for (int x = 0; x < AtlasCell; x++)
                        {
                            float u = x / C, v = y / C;
                            float band = Mathf.Clamp01(1f - Mathf.Abs(v - 0.5f) * 2.6f);
                            float streak = Noise.Value(u * 0.15f, v, 16, seed + cell * 7);
                            float fadeU = Mathf.Clamp01(u * 6f) * Mathf.Clamp01((1f - u) * 2f);
                            float a = band * fadeU * (0.35f + streak * 0.65f);
                            pc.Over((int)ox + x, (int)oy + y, w, a);
                        }
                }
                else
                {
                    for (int i = 0; i < 6; i++)
                    {
                        float a = r.Range(0f, Mathf.PI * 2f), l = C * r.Range(0.2f, 0.45f);
                        pc.Line(cx, cy, cx + Mathf.Cos(a) * l, cy + Mathf.Sin(a) * l, 1.4f, new Color(0.05f, 0.05f, 0.05f, 0.8f), true);
                    }
                }
            }
            return pc.ToTexture("SplatAtlas", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakePuddleAtlas(int seed)
        {
            int s = 256, half = 128;
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int q = 0; q < 4; q++)
            {
                int ox = (q % 2) * half, oy = (q / 2) * half;
                for (int y = 0; y < half; y++)
                    for (int x = 0; x < half; x++)
                    {
                        float dx = (x + 0.5f - half * 0.5f) / (half * 0.5f), dy = (y + 0.5f - half * 0.5f) / (half * 0.5f);
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float ang = Mathf.Atan2(dy, dx);
                        float edge = 0.72f + 0.12f * Mathf.Sin(ang * 3f + q * 1.7f) + 0.07f * Mathf.Sin(ang * 7f + q) +
                                     0.08f * (Noise.Value((ang + 3.2f) / 6.4f, 0.5f, 8, seed + q) - 0.5f);
                        float a = Mathf.Clamp01((edge - r) * 14f);
                        float depth = Mathf.Clamp01((edge - r) * 3f);
                        pc.Px[(oy + y) * s + ox + x] = new Color(1f, 1f, 1f, a * (0.75f + depth * 0.25f));
                    }
            }
            return pc.ToTexture("PuddleAtlas", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeSoft(int s, float falloff)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - r), falloff);
                    pc.Px[y * s + x] = new Color(1, 1, 1, a);
                }
            return pc.ToTexture("Soft", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeDroplet(int s)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            pc.Circle(s * 0.5f, s * 0.5f, s * 0.36f, Color.white, 1.5f, true);
            pc.Circle(s * 0.4f, s * 0.6f, s * 0.1f, new Color(1, 1, 1, 1), 1f, true);
            return pc.ToTexture("Droplet", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeSpark(int s)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Abs(dy) * 3.5f) * Mathf.Clamp01(1f - Mathf.Abs(dx));
                    pc.Px[y * s + x] = new Color(1, 1, 1, a * a);
                }
            return pc.ToTexture("Spark", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeFire(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = Noise.Fbm(x / (float)s, y / (float)s, 4, 4, seed);
                    float a = Mathf.Clamp01((1f - r) * 1.6f - (1f - n) * 0.6f);
                    pc.Px[y * s + x] = new Color(1, 1, 1, a);
                }
            return pc.ToTexture("Fire", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeSmoke(int s, int seed)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float n = Noise.Fbm(x / (float)s, y / (float)s, 3, 5, seed);
                    float a = Mathf.Clamp01((1f - r) * 1.3f) * (0.45f + n * 0.75f);
                    float g = 0.75f + n * 0.25f;
                    pc.Px[y * s + x] = new Color(g, g, g, Mathf.Clamp01(a));
                }
            return pc.ToTexture("Smoke", true, TextureWrapMode.Clamp);
        }

        static Texture2D MakeRing(int s)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0));
            pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) * 9f);
                    pc.Px[y * s + x] = new Color(1, 1, 1, a * a);
                }
            return pc.ToTexture("Ring", true, TextureWrapMode.Clamp);
        }

        static void BuildSprites()
        {
            WhiteSprite = MakeSprite(Solid(4, Color.white), Vector4.zero);
            RoundedRect = MakeSprite(MakeRounded(64, 16), new Vector4(20, 20, 20, 20));
            RoundedSmall = MakeSprite(MakeRounded(32, 6), new Vector4(8, 8, 8, 8));
            var circ = new PixelCanvas(64, 64, new Color(1, 1, 1, 0)); circ.Wrap = false;
            circ.Circle(32, 32, 30, Color.white, 1.5f, true);
            Circle = MakeSprite(circ.ToTexture("Circle", false, TextureWrapMode.Clamp), Vector4.zero);
            var vig = new PixelCanvas(256, 256, new Color(1, 1, 1, 0)); vig.Wrap = false;
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float dx = (x + 0.5f) / 128f - 1f, dy = (y + 0.5f) / 128f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.414f;
                    vig.Px[y * 256 + x] = new Color(1, 1, 1, Mathf.Clamp01(Mathf.Pow(r, 2.2f) * 1.6f));
                }
            Vignette = MakeSprite(vig.ToTexture("Vignette", false, TextureWrapMode.Clamp), Vector4.zero);
            var grad = new PixelCanvas(4, 64, Color.white); grad.Wrap = false;
            for (int y = 0; y < 64; y++) for (int x = 0; x < 4; x++) grad.Px[y * 4 + x] = new Color(1, 1, 1, 0.55f + 0.45f * y / 63f);
            Gradient = MakeSprite(grad.ToTexture("Gradient", false, TextureWrapMode.Clamp), Vector4.zero);
            ToolIcons = new Sprite[12];
            for (int i = 0; i < ToolIcons.Length; i++) ToolIcons[i] = MakeSprite(MakeToolIcon(i), Vector4.zero);
        }

        static Texture2D MakeRounded(int s, int radius)
        {
            var pc = new PixelCanvas(s, s, new Color(1, 1, 1, 0)); pc.Wrap = false;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, s - radius), cy = Mathf.Clamp(y + 0.5f, radius, s - radius);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    pc.Px[y * s + x] = new Color(1, 1, 1, Mathf.Clamp01(radius - d + 0.5f));
                }
            return pc.ToTexture("Rounded", false, TextureWrapMode.Clamp);
        }

        public static Sprite MakeSprite(Texture2D t, Vector4 border)
        {
            t.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }

        /// <summary>Draws a 64x64 white icon for each hotbar tool.</summary>
        static Texture2D MakeToolIcon(int i)
        {
            var pc = new PixelCanvas(64, 64, new Color(1, 1, 1, 0)); pc.Wrap = false;
            Color w = Color.white;
            switch (i)
            {
                case 0: // grab: open hand
                    pc.Ellipse(32, 24, 13, 12, 0, w, 1.2f, true);
                    for (int f = 0; f < 4; f++) pc.Line(22 + f * 6.5f, 30, 22 + f * 6.5f, 48 + (f == 1 || f == 2 ? 4 : 0), 5.5f, w, true);
                    pc.Line(43, 22, 52, 34, 5.5f, w, true);
                    break;
                case 1: // hammer
                    pc.Line(18, 10, 40, 40, 6, w, true);
                    pc.Polygon(new[] { new Vector2(30, 46), new Vector2(46, 30), new Vector2(56, 40), new Vector2(40, 56) }, w, true);
                    break;
                case 2: // blade
                    pc.Polygon(new[] { new Vector2(14, 12), new Vector2(22, 12), new Vector2(54, 50), new Vector2(50, 56) }, w, true);
                    pc.Line(10, 8, 20, 18, 8, w, true);
                    pc.Line(8, 20, 22, 6, 4, w, true);
                    break;
                case 3: // pistol
                    pc.Rect(10, 34, 52, 44, w, true);
                    pc.Polygon(new[] { new Vector2(14, 34), new Vector2(26, 34), new Vector2(22, 12), new Vector2(10, 12) }, w, true);
                    pc.Rect(24, 28, 30, 34, w, true);
                    break;
                case 4: // shotgun
                    pc.Rect(6, 34, 60, 40, w, true);
                    pc.Rect(18, 28, 46, 34, w, true);
                    pc.Polygon(new[] { new Vector2(6, 34), new Vector2(16, 34), new Vector2(10, 20), new Vector2(2, 22) }, w, true);
                    break;
                case 5: // explosive
                    pc.Circle(28, 26, 16, w, 1.2f, true);
                    pc.Rect(26, 40, 34, 46, w, true);
                    pc.Line(31, 46, 40, 54, 3, w, true);
                    pc.Circle(44, 56, 4, w, 1f, true);
                    break;
                case 6: // props: crate
                    pc.Rect(12, 12, 52, 52, w, true);
                    pc.Rect(17, 17, 47, 47, new Color(0, 0, 0, 1), false);
                    pc.Line(17, 17, 47, 47, 5, w, true);
                    pc.Line(17, 47, 47, 17, 5, w, true);
                    break;
                case 7: // shock
                    pc.Polygon(new[] { new Vector2(36, 58), new Vector2(16, 30), new Vector2(30, 30), new Vector2(24, 6), new Vector2(48, 36), new Vector2(34, 36) }, w, true);
                    break;
                case 8: // heal
                    pc.Rect(26, 10, 38, 54, w, true);
                    pc.Rect(10, 26, 54, 38, w, true);
                    break;
                case 9: // spawn ragdoll
                    pc.Circle(32, 50, 7, w, 1f, true);
                    pc.Line(32, 42, 32, 22, 7, w, true);
                    pc.Line(18, 36, 46, 36, 5, w, true);
                    pc.Line(32, 22, 22, 6, 6, w, true);
                    pc.Line(32, 22, 42, 6, 6, w, true);
                    break;
                case 10: // laser: emitter and beam
                    pc.Rect(6, 26, 26, 38, w, true);
                    pc.Rect(10, 16, 18, 26, w, true);
                    pc.Line(26, 32, 58, 32, 3, w, true);
                    pc.Circle(58, 32, 5, w, 1f, true);
                    break;
                default: // rope
                    for (int k = 0; k < 6; k++) pc.Ellipse(14 + k * 7, 14 + k * 7, 6, 4, 0.78f, w, 1f, true);
                    break;
            }
            for (int k = 0; k < pc.Px.Length; k++) if (pc.Px[k].r < 0.5f) pc.Px[k].a = 0f;
            return pc.ToTexture("Icon" + i, false, TextureWrapMode.Clamp);
        }
    }
    #endregion

    #region MaterialFactory
    public static class MaterialFactory
    {
        public static bool URP;
        static Shader lit, unlit, particleUnlit, particleLit, legacyAlpha, legacyAdd, spritesDefault;

        // Shared world materials
        public static Material Concrete, Tiles, Grass, Dirt, Wood, Crate, Metal, Tread, Brick, Rock, Rubber, Hazard, BarrelRed, BarrelSteel,
            Asphalt, Sand, PaintBlue, PaintOrange, PaintGreen, Bone, Flesh, Organ, Artery, CapLimb, CapTorso, Glow, Invisible, DarkMetal, Gold, Spikes,
            Trampoline, Electric, ElectricOff, Glass, Lamp;
        public static Material Decal, Puddle, Blood, Mist, Fire, Smoke, Spark, Dust, Ring, Line, LineAdd, Debris, Shard;

        public static void Init()
        {
            URP = GraphicsSettings.currentRenderPipeline != null;
            if (URP)
            {
                lit = Shader.Find("Universal Render Pipeline/Lit");
                unlit = Shader.Find("Universal Render Pipeline/Unlit");
                particleUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                particleLit = Shader.Find("Universal Render Pipeline/Particles/Simple Lit");
                if (lit == null) URP = false;
            }
            if (!URP)
            {
                lit = Shader.Find("Standard");
                unlit = Shader.Find("Unlit/Texture");
                particleUnlit = null;
                particleLit = null;
            }
            legacyAlpha = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            legacyAdd = Shader.Find("Legacy Shaders/Particles/Additive");
            spritesDefault = Shader.Find("Sprites/Default");
            if (lit == null) lit = spritesDefault;
            if (unlit == null) unlit = spritesDefault;
        }

        public static void BuildWorldMaterials()
        {
            Concrete = Lit("Concrete", Color.white, TextureFactory.Concrete, 0.18f, 0f);
            Tiles = Lit("Tiles", Color.white, TextureFactory.Tiles, 0.25f, 0f);
            Grass = Lit("Grass", Color.white, TextureFactory.Grass, 0.05f, 0f);
            Dirt = Lit("Dirt", Color.white, TextureFactory.Dirt, 0.05f, 0f);
            Wood = Lit("Wood", Color.white, TextureFactory.Wood, 0.22f, 0f);
            Crate = Lit("Crate", Color.white, TextureFactory.Crate, 0.18f, 0f);
            Metal = Lit("Metal", Color.white, TextureFactory.Metal, 0.55f, 0.8f);
            Tread = Lit("Tread", Color.white, TextureFactory.Tread, 0.5f, 0.85f);
            Brick = Lit("Brick", Color.white, TextureFactory.Brick, 0.12f, 0f);
            Rock = Lit("Rock", Color.white, TextureFactory.Rock, 0.15f, 0f);
            Rubber = Lit("Rubber", Color.white, TextureFactory.Rubber, 0.25f, 0f);
            Hazard = Lit("Hazard", Color.white, TextureFactory.Hazard, 0.35f, 0.1f);
            BarrelRed = Lit("BarrelRed", Color.white, TextureFactory.BarrelRed, 0.5f, 0.5f);
            BarrelSteel = Lit("BarrelSteel", Color.white, TextureFactory.BarrelSteel, 0.55f, 0.75f);
            Asphalt = Lit("Asphalt", Color.white, TextureFactory.Asphalt, 0.1f, 0f);
            Sand = Lit("Sand", Color.white, TextureFactory.Sand, 0.05f, 0f);
            PaintBlue = Lit("PaintBlue", Color.white, TextureFactory.PaintBlue, 0.4f, 0.3f);
            PaintOrange = Lit("PaintOrange", Color.white, TextureFactory.PaintOrange, 0.4f, 0.3f);
            PaintGreen = Lit("PaintGreen", Color.white, TextureFactory.PaintGreen, 0.4f, 0.3f);
            DarkMetal = Lit("DarkMetal", new Color(0.3f, 0.31f, 0.33f), TextureFactory.Metal, 0.6f, 0.9f);
            Gold = Lit("Brass", new Color(0.85f, 0.65f, 0.3f), TextureFactory.Metal, 0.7f, 1f);
            Spikes = Lit("Spikes", new Color(0.75f, 0.76f, 0.78f), TextureFactory.Metal, 0.8f, 1f);
            Trampoline = Lit("Trampoline", new Color(0.2f, 0.55f, 0.85f), TextureFactory.Rubber, 0.4f, 0f);
            Electric = Lit("ElectricOn", new Color(0.5f, 0.8f, 1f), TextureFactory.Tread, 0.7f, 0.6f);
            SetEmission(Electric, new Color(0.3f, 0.7f, 1.6f));
            ElectricOff = Lit("ElectricOff", new Color(0.35f, 0.38f, 0.42f), TextureFactory.Tread, 0.6f, 0.7f);
            Glass = LitTransparent("Glass", new Color(0.7f, 0.85f, 0.95f, 0.25f), null, 0.95f);
            Lamp = Unlit("Lamp", new Color(1f, 0.92f, 0.7f), null);
            Bone = Lit("Bone", Color.white, TextureFactory.Bone, 0.35f, 0f);
            Flesh = Lit("Flesh", Color.white, TextureFactory.Flesh, 0.75f, 0f);
            Organ = Lit("Organ", new Color(0.8f, 0.35f, 0.35f), TextureFactory.Flesh, 0.8f, 0f);
            Artery = Lit("Artery", new Color(0.8f, 0.08f, 0.08f), null, 0.7f, 0f);
            CapLimb = Lit("CapLimb", Color.white, TextureFactory.CapLimb, 0.7f, 0f);
            CapTorso = Lit("CapTorso", Color.white, TextureFactory.CapTorso, 0.7f, 0f);
            Glow = Unlit("Glow", new Color(1f, 0.6f, 0.2f), null);
            Invisible = null;

            Decal = ParticleMaterial("Decals", TextureFactory.SplatAtlas, false);
            Decal.renderQueue = 2950;
            SetDoubleSided(Decal);
            Puddle = LitTransparent("Puddle", new Color(0.35f, 0.01f, 0.02f, 0.95f), TextureFactory.PuddleAtlas, 0.92f);
            Puddle.renderQueue = 2960;
            Blood = ParticleMaterial("BloodDrops", TextureFactory.Droplet, false);
            Mist = ParticleMaterial("BloodMist", TextureFactory.Mist, false);
            Fire = ParticleMaterial("Fire", TextureFactory.Fire, true);
            Smoke = ParticleMaterial("Smoke", TextureFactory.Smoke, false);
            Spark = ParticleMaterial("Spark", TextureFactory.Spark, true);
            Dust = ParticleMaterial("Dust", TextureFactory.Smoke, false);
            Ring = ParticleMaterial("Shockwave", TextureFactory.Ring, true);
            Line = ParticleMaterial("Line", TextureFactory.SoftParticle, false);
            LineAdd = ParticleMaterial("LineAdd", TextureFactory.Spark, true);
            Debris = Lit("Debris", new Color(0.4f, 0.37f, 0.33f), TextureFactory.Rock, 0.1f, 0f);
            Shard = Lit("Shard", new Color(0.55f, 0.4f, 0.25f), TextureFactory.Wood, 0.2f, 0f);
        }

        public static Material Lit(string name, Color color, Texture tex, float smoothness, float metallic)
        {
            var m = new Material(lit);
            m.name = name;
            m.color = color;
            if (tex != null) m.mainTexture = tex;
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Glossiness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (URP) m.SetFloat("_EnvironmentReflections", 1f);
            m.enableInstancing = true;
            return m;
        }

        /// <summary>Lit material whose emission can be toggled later (hover highlight on body parts).</summary>
        public static Material LitEmissive(string name, Color color, Texture tex, float smoothness)
        {
            var m = Lit(name, color, tex, smoothness, 0f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.black);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }

        public static void SetEmission(Material m, Color c)
        {
            if (m == null) return;
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        /// <summary>Normal map for URP Lit (_BumpMap + _NORMALMAP) and the built-in Standard shader.</summary>
        public static void SetNormalMap(Material m, Texture tex, float scale)
        {
            if (m == null || tex == null) return;
            if (m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", tex);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", scale);
                m.EnableKeyword("_NORMALMAP");
            }
        }

        public static void SetDoubleSided(Material m)
        {
            if (m == null) return;
            m.SetFloat("_Cull", 0f);
            m.doubleSidedGI = true;
        }

        public static Material LitTransparent(string name, Color color, Texture tex, float smoothness)
        {
            var m = Lit(name, color, tex, smoothness, 0f);
            MakeTransparent(m, color.a);
            return m;
        }

        /// <summary>Switches a lit material to alpha-blended transparency (URP Lit or Standard).</summary>
        public static void MakeTransparent(Material m, float alpha)
        {
            if (m == null) return;
            Color c = m.color; c.a = alpha; m.color = c;
            if (URP)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.DisableKeyword("_ALPHAMODULATE_ON");
                m.SetShaderPassEnabled("ShadowCaster", false);
                m.SetShaderPassEnabled("DepthOnly", false);
            }
            else
            {
                m.SetFloat("_Mode", 3f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.One);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>Restores an opaque material previously switched with MakeTransparent.</summary>
        public static void MakeOpaque(Material m)
        {
            if (m == null) return;
            Color c = m.color; c.a = 1f; m.color = c;
            if (URP)
            {
                m.SetFloat("_Surface", 0f);
                m.SetOverrideTag("RenderType", "Opaque");
                m.SetFloat("_SrcBlend", (float)BlendMode.One);
                m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                m.SetFloat("_ZWrite", 1f);
                m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetShaderPassEnabled("ShadowCaster", true);
                m.SetShaderPassEnabled("DepthOnly", true);
            }
            else
            {
                m.SetFloat("_Mode", 0f);
                m.SetOverrideTag("RenderType", "");
                m.SetInt("_SrcBlend", (int)BlendMode.One);
                m.SetInt("_DstBlend", (int)BlendMode.Zero);
                m.SetInt("_ZWrite", 1);
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            m.renderQueue = -1;
        }

        public static Material Unlit(string name, Color color, Texture tex)
        {
            var m = new Material(unlit);
            m.name = name;
            m.color = color;
            if (tex != null) m.mainTexture = tex;
            return m;
        }

        /// <summary>Unlit vertex-colored transparent material for particles, decals and lines.</summary>
        public static Material ParticleMaterial(string name, Texture tex, bool additive)
        {
            Material m;
            if (URP && particleUnlit != null)
            {
                m = new Material(particleUnlit);
                m.name = name;
                m.mainTexture = tex;
                m.color = Color.white;
                m.SetFloat("_Surface", 1f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetFloat("_ZWrite", 0f);
                if (additive)
                {
                    m.SetFloat("_Blend", 2f);
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.One);
                    m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    m.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
                }
                else
                {
                    m.SetFloat("_Blend", 0f);
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                }
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                Shader s = additive ? legacyAdd : legacyAlpha;
                if (s == null) s = spritesDefault;
                m = new Material(s);
                m.name = name;
                m.mainTexture = tex;
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            return m;
        }

        public static Material Skybox()
        {
            Shader s = Shader.Find("Skybox/Procedural");
            if (s == null) return null;
            var m = new Material(s);
            m.name = "ProceduralSky";
            m.SetFloat("_SunSize", 0.045f);
            m.SetFloat("_SunSizeConvergence", 4f);
            m.SetFloat("_AtmosphereThickness", 0.95f);
            m.SetColor("_SkyTint", new Color(0.52f, 0.6f, 0.72f));
            m.SetColor("_GroundColor", new Color(0.42f, 0.38f, 0.33f));
            m.SetFloat("_Exposure", 1.25f);
            return m;
        }

        public static Material WithColor(Material src, Color c)
        {
            var m = new Material(src);
            m.color = c;
            return m;
        }
    }
    #endregion

    #region MeshFactory
    /// <summary>Accumulates geometry and produces a Mesh. Only used at build/event time.</summary>
    public class MeshBuilder
    {
        public readonly List<Vector3> V = new List<Vector3>(1024);
        public readonly List<Vector3> N = new List<Vector3>(1024);
        public readonly List<Vector2> UV = new List<Vector2>(1024);
        public readonly List<Color32> C = new List<Color32>();
        public readonly List<List<int>> Sub = new List<List<int>>();
        public bool HasNormals = true;

        public MeshBuilder(int submeshes = 1)
        {
            for (int i = 0; i < submeshes; i++) Sub.Add(new List<int>(2048));
        }

        public int Vert(Vector3 p, Vector3 n, Vector2 uv)
        {
            V.Add(p); N.Add(n); UV.Add(uv);
            return V.Count - 1;
        }

        public void Tri(int a, int b, int c, int sub = 0) { var l = Sub[sub]; l.Add(a); l.Add(b); l.Add(c); }

        public void Quad(int a, int b, int c, int d, int sub = 0) { Tri(a, b, c, sub); Tri(a, c, d, sub); }

        public Mesh ToMesh(string name, bool recalcNormals = false)
        {
            var m = new Mesh();
            m.name = name;
            if (V.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(V);
            m.SetUVs(0, UV);
            if (C.Count == V.Count) m.SetColors(C);
            m.subMeshCount = Sub.Count;
            for (int i = 0; i < Sub.Count; i++) m.SetTriangles(Sub[i], i, false);
            if (recalcNormals || !HasNormals) m.RecalculateNormals();
            else m.SetNormals(N);
            m.RecalculateBounds();
            if (V.Count > 3) m.RecalculateTangents();
            return m;
        }

        public void Clear()
        {
            V.Clear(); N.Clear(); UV.Clear(); C.Clear();
            for (int i = 0; i < Sub.Count; i++) Sub[i].Clear();
        }
    }

    public static class MeshFactory
    {
        static Mesh unitQuad, unitCube;

        /// <summary>Box with per-face UVs. uvScale = meters per texture repeat (0 = one repeat per face).</summary>
        public static Mesh Box(Vector3 size, float uvScale, Vector3 center = default(Vector3))
        {
            var b = new MeshBuilder();
            AddBox(b, center, size, Quaternion.identity, uvScale);
            return b.ToMesh("Box");
        }

        public static void AddBox(MeshBuilder b, Vector3 center, Vector3 size, Quaternion rot, float uvScale, int sub = 0)
        {
            Vector3 h = size * 0.5f;
            AddFace(b, center, rot, Vector3.right, Vector3.forward, Vector3.up, h.x, h.z, h.y, size.z, size.y, uvScale, sub);
            AddFace(b, center, rot, Vector3.left, Vector3.back, Vector3.up, h.x, h.z, h.y, size.z, size.y, uvScale, sub);
            AddFace(b, center, rot, Vector3.up, Vector3.right, Vector3.forward, h.y, h.x, h.z, size.x, size.z, uvScale, sub);
            AddFace(b, center, rot, Vector3.down, Vector3.right, Vector3.back, h.y, h.x, h.z, size.x, size.z, uvScale, sub);
            AddFace(b, center, rot, Vector3.forward, Vector3.left, Vector3.up, h.z, h.x, h.y, size.x, size.y, uvScale, sub);
            AddFace(b, center, rot, Vector3.back, Vector3.right, Vector3.up, h.z, h.x, h.y, size.x, size.y, uvScale, sub);
        }

        static void AddFace(MeshBuilder b, Vector3 c, Quaternion rot, Vector3 n, Vector3 u, Vector3 v, float dn, float du, float dv,
            float sizeU, float sizeV, float uvScale, int sub)
        {
            Vector3 o = c + rot * (n * dn);
            Vector3 U = rot * (u * du), Vv = rot * (v * dv);
            Vector3 N = rot * n;
            float su = uvScale > 0f ? sizeU / uvScale : 1f, sv = uvScale > 0f ? sizeV / uvScale : 1f;
            int i0 = b.Vert(o - U - Vv, N, new Vector2(0, 0));
            int i1 = b.Vert(o - U + Vv, N, new Vector2(0, sv));
            int i2 = b.Vert(o + U + Vv, N, new Vector2(su, sv));
            int i3 = b.Vert(o + U - Vv, N, new Vector2(su, 0));
            b.Quad(i0, i1, i2, i3, sub);
        }

        static readonly Dictionary<Vector4, Mesh> boxCache = new Dictionary<Vector4, Mesh>();

        /// <summary>Shared box mesh per (size, uvScale): props of the same size reuse one mesh.</summary>
        public static Mesh BoxCached(Vector3 size, float uvScale)
        {
            var key = new Vector4(Mathf.Round(size.x * 1000f), Mathf.Round(size.y * 1000f), Mathf.Round(size.z * 1000f), Mathf.Round(uvScale * 1000f));
            Mesh m;
            if (boxCache.TryGetValue(key, out m) && m != null) return m;
            m = Box(size, uvScale);
            boxCache[key] = m;
            return m;
        }

        public static Mesh UnitQuad()
        {
            if (unitQuad != null) return unitQuad;
            unitQuad = Quad(new Rect(0, 0, 1, 1));
            return unitQuad;
        }

        /// <summary>Flat quad on the XZ plane facing +Y, size 1, with the given UV rect.</summary>
        public static Mesh Quad(Rect uv)
        {
            var b = new MeshBuilder();
            int a = b.Vert(new Vector3(-0.5f, 0, -0.5f), Vector3.up, new Vector2(uv.xMin, uv.yMin));
            int c = b.Vert(new Vector3(-0.5f, 0, 0.5f), Vector3.up, new Vector2(uv.xMin, uv.yMax));
            int d = b.Vert(new Vector3(0.5f, 0, 0.5f), Vector3.up, new Vector2(uv.xMax, uv.yMax));
            int e = b.Vert(new Vector3(0.5f, 0, -0.5f), Vector3.up, new Vector2(uv.xMax, uv.yMin));
            b.Quad(a, c, d, e);
            return b.ToMesh("Quad");
        }

        public static Mesh UnitCube()
        {
            if (unitCube != null) return unitCube;
            unitCube = Box(Vector3.one, 0f);
            return unitCube;
        }

        /// <summary>Surface of revolution around +Y. profile: (radius, height) pairs from bottom to top.</summary>
        public static Mesh Lathe(Vector2[] profile, int segments, float uRepeat = 1f, float vScale = 1f, bool capBottom = true, bool capTop = true)
        {
            var b = new MeshBuilder();
            AddLathe(b, profile, segments, Vector3.zero, Quaternion.identity, uRepeat, vScale, capBottom, capTop, 0);
            return b.ToMesh("Lathe", true);
        }

        public static void AddLathe(MeshBuilder b, Vector2[] profile, int segments, Vector3 offset, Quaternion rot, float uRepeat, float vScale,
            bool capBottom, bool capTop, int sub)
        {
            int rings = profile.Length;
            float[] vAcc = new float[rings];
            for (int i = 1; i < rings; i++) vAcc[i] = vAcc[i - 1] + (profile[i] - profile[i - 1]).magnitude;
            int start = b.V.Count;
            for (int i = 0; i < rings; i++)
            {
                Vector2 tangent = (i == 0 ? profile[1] - profile[0] : i == rings - 1 ? profile[i] - profile[i - 1] : profile[i + 1] - profile[i - 1]).normalized;
                Vector2 n2 = new Vector2(tangent.y, -tangent.x);
                for (int s = 0; s <= segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    Vector3 p = new Vector3(ca * profile[i].x, profile[i].y, sa * profile[i].x);
                    Vector3 n = new Vector3(ca * n2.x, n2.y, sa * n2.x);
                    b.Vert(offset + rot * p, rot * n, new Vector2(s / (float)segments * uRepeat, vAcc[i] * vScale));
                }
            }
            int row = segments + 1;
            for (int i = 0; i < rings - 1; i++)
                for (int s = 0; s < segments; s++)
                {
                    int a = start + i * row + s, c = a + row;
                    b.Quad(a, c, c + 1, a + 1, sub);
                }
            if (capBottom && profile[0].x > 1e-4f) AddCap(b, profile[0], segments, offset, rot, false, sub);
            if (capTop && profile[rings - 1].x > 1e-4f) AddCap(b, profile[rings - 1], segments, offset, rot, true, sub);
        }

        static void AddCap(MeshBuilder b, Vector2 ring, int segments, Vector3 offset, Quaternion rot, bool top, int sub)
        {
            Vector3 n = rot * (top ? Vector3.up : Vector3.down);
            int c = b.Vert(offset + rot * new Vector3(0, ring.y, 0), n, new Vector2(0.5f, 0.5f));
            int first = b.V.Count;
            for (int s = 0; s <= segments; s++)
            {
                float a = s / (float)segments * Mathf.PI * 2f;
                b.Vert(offset + rot * new Vector3(Mathf.Cos(a) * ring.x, ring.y, Mathf.Sin(a) * ring.x), n,
                    new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f));
            }
            for (int s = 0; s < segments; s++)
            {
                if (top) b.Tri(c, first + s + 1, first + s, sub);
                else b.Tri(c, first + s, first + s + 1, sub);
            }
        }

        public static Mesh Cylinder(float radius, float height, int segments, float uvScale = 1f)
        {
            float circ = 2f * Mathf.PI * radius;
            var p = new[] { new Vector2(radius, -height * 0.5f), new Vector2(radius, height * 0.5f) };
            var b = new MeshBuilder();
            AddLathe(b, p, segments, Vector3.zero, Quaternion.identity, uvScale > 0 ? circ / uvScale : 1f, uvScale > 0 ? 1f / uvScale : 1f / height, true, true, 0);
            return b.ToMesh("Cylinder", false);
        }

        public static Mesh Sphere(float radius, int lat, int lon)
        {
            var p = new Vector2[lat + 1];
            for (int i = 0; i <= lat; i++)
            {
                float a = -Mathf.PI * 0.5f + Mathf.PI * i / lat;
                p[i] = new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius);
            }
            p[0].x = 0f; p[lat].x = 0f;
            var b = new MeshBuilder();
            AddLathe(b, p, lon, Vector3.zero, Quaternion.identity, 2f, 1f / (Mathf.PI * radius), false, false, 0);
            for (int i = 0; i < b.N.Count; i++) b.N[i] = b.V[i].normalized;
            return b.ToMesh("Sphere", false);
        }

        /// <summary>Ellipsoid via a sphere with non-uniform scale baked into vertices.</summary>
        public static void AddEllipsoid(MeshBuilder b, Vector3 center, Vector3 radii, Quaternion rot, int lat, int lon, int sub = 0)
        {
            int start = b.V.Count;
            for (int i = 0; i <= lat; i++)
            {
                float a = -Mathf.PI * 0.5f + Mathf.PI * i / lat;
                float cy = Mathf.Sin(a), cr = Mathf.Cos(a);
                for (int s = 0; s <= lon; s++)
                {
                    float o = s / (float)lon * Mathf.PI * 2f;
                    Vector3 unit = new Vector3(Mathf.Cos(o) * cr, cy, Mathf.Sin(o) * cr);
                    Vector3 p = Vector3.Scale(unit, radii);
                    Vector3 n = new Vector3(unit.x / Mathf.Max(1e-4f, radii.x), unit.y / Mathf.Max(1e-4f, radii.y), unit.z / Mathf.Max(1e-4f, radii.z)).normalized;
                    b.Vert(center + rot * p, rot * n, new Vector2(s / (float)lon, i / (float)lat));
                }
            }
            int row = lon + 1;
            for (int i = 0; i < lat; i++)
                for (int s = 0; s < lon; s++)
                {
                    int a = start + i * row + s, c = a + row;
                    b.Quad(a, c, c + 1, a + 1, sub);
                }
        }

        public static Mesh Ellipsoid(Vector3 radii, int lat = 10, int lon = 14)
        {
            var b = new MeshBuilder();
            AddEllipsoid(b, Vector3.zero, radii, Quaternion.identity, lat, lon);
            return b.ToMesh("Ellipsoid");
        }

        public static Mesh Capsule(float radius, float height, int segments = 16)
        {
            int hemi = 6;
            var list = new List<Vector2>();
            float half = Mathf.Max(0f, height * 0.5f - radius);
            for (int i = 0; i <= hemi; i++)
            {
                float a = -Mathf.PI * 0.5f + Mathf.PI * 0.5f * i / hemi;
                list.Add(new Vector2(Mathf.Cos(a) * radius, -half + Mathf.Sin(a) * radius));
            }
            for (int i = 0; i <= hemi; i++)
            {
                float a = Mathf.PI * 0.5f * i / hemi;
                list.Add(new Vector2(Mathf.Cos(a) * radius, half + Mathf.Sin(a) * radius));
            }
            var arr = list.ToArray();
            arr[0].x = 0f; arr[arr.Length - 1].x = 0f;
            return Lathe(arr, segments, 2f, 1f, false, false);
        }

        public static Mesh Cone(float radius, float height, int segments)
        {
            return Lathe(new[] { new Vector2(radius, 0f), new Vector2(radius * 0.02f, height) }, segments, 1f, 1f, true, false);
        }

        /// <summary>Torus around +Y (tires, rings).</summary>
        public static Mesh Torus(float majorR, float minorR, int majorSeg, int minorSeg, float flatten = 1f)
        {
            var b = new MeshBuilder();
            for (int i = 0; i <= majorSeg; i++)
            {
                float a = i / (float)majorSeg * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                for (int j = 0; j <= minorSeg; j++)
                {
                    float t = j / (float)minorSeg * Mathf.PI * 2f;
                    Vector3 n = dir * Mathf.Cos(t) + Vector3.up * Mathf.Sin(t);
                    Vector3 p = dir * majorR + new Vector3(n.x * minorR, n.y * minorR * flatten, n.z * minorR);
                    b.Vert(p, n, new Vector2(i / (float)majorSeg * 4f, j / (float)minorSeg));
                }
            }
            int row = minorSeg + 1;
            for (int i = 0; i < majorSeg; i++)
                for (int j = 0; j < minorSeg; j++)
                {
                    int a = i * row + j, c = a + row;
                    b.Quad(a, a + 1, c + 1, c);
                }
            return b.ToMesh("Torus", true);
        }

        /// <summary>Ramp: a wedge rising along +Z from 0 to height.</summary>
        public static Mesh Wedge(Vector3 size, float uvScale)
        {
            var b = new MeshBuilder();
            float w = size.x * 0.5f, h = size.y, l = size.z * 0.5f;
            Vector3 p0 = new Vector3(-w, 0, -l), p1 = new Vector3(w, 0, -l), p2 = new Vector3(w, 0, l), p3 = new Vector3(-w, 0, l);
            Vector3 p4 = new Vector3(-w, h, l), p5 = new Vector3(w, h, l);
            float s = uvScale > 0 ? 1f / uvScale : 1f;
            Vector3 slopeN = Vector3.Cross(p4 - p0, p1 - p0).normalized;
            float slopeLen = (p4 - p0).magnitude;
            int a = b.Vert(p0, slopeN, new Vector2(0, 0)), bb = b.Vert(p4, slopeN, new Vector2(0, slopeLen * s));
            int c = b.Vert(p5, slopeN, new Vector2(size.x * s, slopeLen * s)), d = b.Vert(p1, slopeN, new Vector2(size.x * s, 0));
            b.Quad(a, bb, c, d);
            a = b.Vert(p0, Vector3.down, new Vector2(0, 0)); bb = b.Vert(p1, Vector3.down, new Vector2(size.x * s, 0));
            c = b.Vert(p2, Vector3.down, new Vector2(size.x * s, size.z * s)); d = b.Vert(p3, Vector3.down, new Vector2(0, size.z * s));
            b.Quad(a, bb, c, d);
            a = b.Vert(p3, Vector3.forward, new Vector2(0, 0)); bb = b.Vert(p2, Vector3.forward, new Vector2(size.x * s, 0));
            c = b.Vert(p5, Vector3.forward, new Vector2(size.x * s, h * s)); d = b.Vert(p4, Vector3.forward, new Vector2(0, h * s));
            b.Quad(a, bb, c, d);
            a = b.Vert(p0, Vector3.left, new Vector2(0, 0)); bb = b.Vert(p3, Vector3.left, new Vector2(size.z * s, 0));
            c = b.Vert(p4, Vector3.left, new Vector2(size.z * s, h * s));
            b.Tri(a, bb, c);
            a = b.Vert(p1, Vector3.right, new Vector2(0, 0)); bb = b.Vert(p5, Vector3.right, new Vector2(size.z * s, h * s));
            c = b.Vert(p2, Vector3.right, new Vector2(size.z * s, 0));
            b.Tri(a, bb, c);
            return b.ToMesh("Wedge");
        }

        /// <summary>Half-pipe open toward +Y, running along Z.</summary>
        public static Mesh HalfPipe(float radius, float length, float thickness, int segments, float uvScale)
        {
            var b = new MeshBuilder();
            float s = uvScale > 0 ? 1f / uvScale : 1f;
            int start = b.V.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.PI + Mathf.PI * i / segments;
                Vector3 n = -new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                Vector3 p = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * radius + Vector3.up * radius;
                float u = radius * Mathf.PI * i / segments * s;
                b.Vert(p + Vector3.back * length * 0.5f, n, new Vector2(u, 0));
                b.Vert(p + Vector3.forward * length * 0.5f, n, new Vector2(u, length * s));
            }
            for (int i = 0; i < segments; i++)
            {
                int a = start + i * 2;
                b.Quad(a, a + 1, a + 3, a + 2);
            }
            // outer shell (simple boxes underneath so it looks solid)
            AddBox(b, new Vector3(0, thickness * 0.5f - thickness, 0), new Vector3(radius * 2f + thickness * 2f, thickness, length), Quaternion.identity, uvScale);
            AddBox(b, new Vector3(-radius - thickness * 0.5f, radius * 0.5f, 0), new Vector3(thickness, radius + thickness, length), Quaternion.identity, uvScale);
            AddBox(b, new Vector3(radius + thickness * 0.5f, radius * 0.5f, 0), new Vector3(thickness, radius + thickness, length), Quaternion.identity, uvScale);
            return b.ToMesh("HalfPipe");
        }

        /// <summary>Saw blade disc in the XY plane (normal +Z) with teeth.</summary>
        public static Mesh SawBlade(float radius, float thickness, int teeth)
        {
            var b = new MeshBuilder();
            int seg = teeth * 2;
            float ht = thickness * 0.5f;
            for (int side = 0; side < 2; side++)
            {
                float z = side == 0 ? ht : -ht;
                Vector3 n = side == 0 ? Vector3.forward : Vector3.back;
                int c = b.Vert(new Vector3(0, 0, z), n, new Vector2(0.5f, 0.5f));
                int first = b.V.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    float r = (i % 2 == 0) ? radius : radius * 0.86f;
                    float a2 = a + ((i % 2 == 0) ? 0.06f : 0f);
                    b.Vert(new Vector3(Mathf.Cos(a2) * r, Mathf.Sin(a2) * r, z), n, new Vector2(0.5f + Mathf.Cos(a2) * 0.5f, 0.5f + Mathf.Sin(a2) * 0.5f));
                }
                for (int i = 0; i < seg; i++)
                {
                    if (side == 0) b.Tri(c, first + i, first + i + 1);
                    else b.Tri(c, first + i + 1, first + i);
                }
            }
            return b.ToMesh("SawBlade", true);
        }

        /// <summary>Grid heightfield centered on origin.</summary>
        public static Mesh Heightfield(float sizeX, float sizeZ, int resX, int resZ, Func<float, float, float> height, float uvScale)
        {
            var b = new MeshBuilder();
            b.HasNormals = false;
            for (int z = 0; z <= resZ; z++)
                for (int x = 0; x <= resX; x++)
                {
                    float px = -sizeX * 0.5f + sizeX * x / resX, pz = -sizeZ * 0.5f + sizeZ * z / resZ;
                    float py = height(px, pz);
                    b.Vert(new Vector3(px, py, pz), Vector3.up, new Vector2(px / uvScale, pz / uvScale));
                }
            int row = resX + 1;
            for (int z = 0; z < resZ; z++)
                for (int x = 0; x < resX; x++)
                {
                    int a = z * row + x, c = a + row;
                    b.Quad(a, c, c + 1, a + 1);
                }
            return b.ToMesh("Heightfield", true);
        }

        /// <summary>Tube swept along a polyline with constant radius (used for arteries, intestines, ribs).</summary>
        public static void AddTube(MeshBuilder b, Vector3[] path, float radius, int segments, int sub = 0, float radiusEnd = -1f)
        {
            if (path.Length < 2) return;
            if (radiusEnd < 0f) radiusEnd = radius;
            int start = b.V.Count;
            Vector3 prevNormal = Vector3.zero;
            for (int i = 0; i < path.Length; i++)
            {
                Vector3 t = (i == 0 ? path[1] - path[0] : i == path.Length - 1 ? path[i] - path[i - 1] : path[i + 1] - path[i - 1]).normalized;
                Vector3 n;
                if (i == 0)
                {
                    n = Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                }
                else
                {
                    n = (prevNormal - t * Vector3.Dot(prevNormal, t)).normalized;
                    if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(t, Vector3.up).normalized;
                }
                prevNormal = n;
                Vector3 bn = Vector3.Cross(t, n);
                float r = Mathf.Lerp(radius, radiusEnd, i / (float)(path.Length - 1));
                for (int s = 0; s <= segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    Vector3 dir = n * Mathf.Cos(a) + bn * Mathf.Sin(a);
                    b.Vert(path[i] + dir * r, dir, new Vector2(s / (float)segments, i / (float)(path.Length - 1)));
                }
            }
            int row = segments + 1;
            for (int i = 0; i < path.Length - 1; i++)
                for (int s = 0; s < segments; s++)
                {
                    int a = start + i * row + s, c = a + row;
                    b.Quad(a, a + 1, c + 1, c, sub);
                }
        }

        /// <summary>Small irregular rock chunk (for debris and boulders).</summary>
        public static Mesh Rock(float radius, int seed, float roughness = 0.3f)
        {
            var b = new MeshBuilder();
            b.HasNormals = false;
            int lat = 8, lon = 12;
            var r = new Rng(seed);
            float[] bumps = new float[(lat + 1) * (lon + 1)];
            for (int i = 0; i < bumps.Length; i++) bumps[i] = 1f + r.Range(-roughness, roughness);
            for (int i = 0; i <= lat; i++)
            {
                float a = -Mathf.PI * 0.5f + Mathf.PI * i / lat;
                for (int s = 0; s <= lon; s++)
                {
                    float o = s / (float)lon * Mathf.PI * 2f;
                    int si = s == lon ? 0 : s;
                    float k = (i == 0 || i == lat) ? 1f : bumps[i * (lon + 1) + si];
                    Vector3 p = new Vector3(Mathf.Cos(o) * Mathf.Cos(a), Mathf.Sin(a) * 0.8f, Mathf.Sin(o) * Mathf.Cos(a)) * radius * k;
                    b.Vert(p, p.normalized, new Vector2(s / (float)lon * 2f, i / (float)lat));
                }
            }
            int row = lon + 1;
            for (int i = 0; i < lat; i++)
                for (int s = 0; s < lon; s++)
                {
                    int a = i * row + s, c = a + row;
                    b.Quad(a, c, c + 1, a + 1);
                }
            return b.ToMesh("Rock", true);
        }
    }
    #endregion
}
