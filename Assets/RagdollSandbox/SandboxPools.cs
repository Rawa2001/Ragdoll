// Ragdoll Sandbox - pools: global particle systems (emitted on demand, never instantiated per effect),
// pooled flash lights, tracer/lightning line renderers, and the decal system (batched static decals in a
// few chunked meshes with a global cap, plus pooled quads that stick to moving bodies).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region FxSystem
    public class FxSystem
    {
        public ParticleSystem BloodDrops, BloodMist, Fire, Smoke, Sparks, Dust, Debris, Shockwave, Muzzle, Zaps, Splinters, Steam;
        Transform root;
        Rng rng = new Rng(4242);

        struct FlashLight { public Light light; public float t, dur, intensity; }
        readonly FlashLight[] flashes = new FlashLight[8];
        int nextFlash;

        struct TracerSlot { public LineRenderer lr; public float t, dur; public Color color; public float width; }
        readonly TracerSlot[] tracers = new TracerSlot[16];
        int nextTracer;

        public float Budget { get { return Sandbox.Boot != null && Sandbox.Boot.Quality != null ? Sandbox.Boot.Quality.ParticleScale : 1f; } }

        public void Build(Transform parent, int maxParticles)
        {
            root = new GameObject("FX").transform;
            root.SetParent(parent, false);
            int max = Mathf.Max(500, maxParticles);

            BloodDrops = MakeSystem("BloodDrops", MaterialFactory.Blood, max, true, 1f, ParticleSystemRenderMode.Stretch);
            var br = BloodDrops.GetComponent<ParticleSystemRenderer>();
            br.velocityScale = 0.035f;
            br.lengthScale = 1f;
            var col = BloodDrops.collision;
            col.dampen = 1f; col.bounce = 0f; col.lifetimeLoss = 1f; col.sendCollisionMessages = true; col.radiusScale = 0.4f;
            col.quality = ParticleSystemCollisionQuality.High;
            BloodDrops.gameObject.AddComponent<BloodCollisionHandler>();

            BloodMist = MakeSystem("BloodMist", MaterialFactory.Mist, max / 3, false, 0.15f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(BloodMist, 0.6f, 2.2f);
            SetFade(BloodMist, new Color(1, 1, 1, 0.55f));
            SetDrag(BloodMist, 2.5f);

            Fire = MakeSystem("Fireball", MaterialFactory.Fire, 600, false, -0.08f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(Fire, 0.7f, 1.8f);
            var fg = new Gradient();
            fg.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.8f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.15f), 0.25f),
                new GradientColorKey(new Color(0.85f, 0.2f, 0.05f), 0.6f), new GradientColorKey(new Color(0.2f, 0.08f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.4f), new GradientAlphaKey(0f, 1f) });
            var fcol = Fire.colorOverLifetime; fcol.enabled = true; fcol.color = new ParticleSystem.MinMaxGradient(fg);
            SetDrag(Fire, 4f);
            SetSpin(Fire, 60f);

            Smoke = MakeSystem("Smoke", MaterialFactory.Smoke, 500, false, -0.035f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(Smoke, 0.6f, 3.2f);
            var sg = new Gradient();
            sg.SetKeys(new[] { new GradientColorKey(new Color(0.3f, 0.28f, 0.26f), 0f), new GradientColorKey(new Color(0.55f, 0.53f, 0.5f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.75f, 0.08f), new GradientAlphaKey(0.4f, 0.6f), new GradientAlphaKey(0f, 1f) });
            var scol = Smoke.colorOverLifetime; scol.enabled = true; scol.color = new ParticleSystem.MinMaxGradient(sg);
            SetDrag(Smoke, 1.4f);
            SetSpin(Smoke, 25f);

            Sparks = MakeSystem("Sparks", MaterialFactory.Spark, 800, true, 1f, ParticleSystemRenderMode.Stretch);
            var sr = Sparks.GetComponent<ParticleSystemRenderer>(); sr.velocityScale = 0.05f; sr.lengthScale = 1.5f;
            var scl = Sparks.collision; scl.bounce = 0.35f; scl.dampen = 0.4f; scl.lifetimeLoss = 0.3f; scl.quality = ParticleSystemCollisionQuality.Medium;
            SetFade(Sparks, new Color(1f, 0.8f, 0.45f, 1f));

            Dust = MakeSystem("Dust", MaterialFactory.Dust, 400, false, 0.02f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(Dust, 0.5f, 2.6f);
            SetFade(Dust, new Color(0.62f, 0.56f, 0.47f, 0.7f));
            SetDrag(Dust, 2.2f);

            Debris = MakeSystem("Debris", MaterialFactory.Debris, 300, true, 1f, ParticleSystemRenderMode.Mesh);
            var dr = Debris.GetComponent<ParticleSystemRenderer>();
            dr.mesh = MeshFactory.Rock(0.5f, 77, 0.35f);
            dr.shadowCastingMode = ShadowCastingMode.On;
            var dm = Debris.main; dm.startRotation3D = true;
            var dcl = Debris.collision; dcl.bounce = 0.25f; dcl.dampen = 0.35f; dcl.lifetimeLoss = 0f; dcl.quality = ParticleSystemCollisionQuality.Medium;
            SetSpin(Debris, 200f);

            Splinters = MakeSystem("Splinters", MaterialFactory.Shard, 200, true, 1f, ParticleSystemRenderMode.Mesh);
            var spr = Splinters.GetComponent<ParticleSystemRenderer>();
            spr.mesh = MeshFactory.Box(new Vector3(0.25f, 0.06f, 1f), 0f);
            var spm = Splinters.main; spm.startRotation3D = true;
            var spc = Splinters.collision; spc.bounce = 0.3f; spc.dampen = 0.3f; spc.lifetimeLoss = 0f; spc.quality = ParticleSystemCollisionQuality.Medium;
            SetSpin(Splinters, 300f);

            Shockwave = MakeSystem("Shockwave", MaterialFactory.Ring, 20, false, 0f, ParticleSystemRenderMode.HorizontalBillboard);
            SetSizeOverLife(Shockwave, 0.1f, 1f);
            SetFade(Shockwave, new Color(1f, 0.9f, 0.75f, 0.85f));

            Muzzle = MakeSystem("Muzzle", MaterialFactory.Fire, 60, false, 0f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(Muzzle, 1f, 0.3f);
            SetFade(Muzzle, new Color(1f, 0.85f, 0.5f, 1f));

            Zaps = MakeSystem("Zaps", MaterialFactory.Spark, 300, false, 0.2f, ParticleSystemRenderMode.Stretch);
            var zr = Zaps.GetComponent<ParticleSystemRenderer>(); zr.velocityScale = 0.03f;
            SetFade(Zaps, new Color(0.6f, 0.85f, 1f, 1f));

            Steam = MakeSystem("Steam", MaterialFactory.Smoke, 200, false, -0.1f, ParticleSystemRenderMode.Billboard);
            SetSizeOverLife(Steam, 0.5f, 2f);
            SetFade(Steam, new Color(0.9f, 0.9f, 0.92f, 0.4f));
            SetDrag(Steam, 1.5f);

            for (int i = 0; i < flashes.Length; i++)
            {
                var go = new GameObject("Flash" + i);
                go.transform.SetParent(root, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.enabled = false;
                l.renderMode = LightRenderMode.ForcePixel;
                flashes[i].light = l;
            }
            for (int i = 0; i < tracers.Length; i++)
            {
                var go = new GameObject("Tracer" + i);
                go.transform.SetParent(root, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = MaterialFactory.LineAdd;
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                lr.shadowCastingMode = ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.numCapVertices = 2;
                lr.enabled = false;
                tracers[i].lr = lr;
            }
        }

        ParticleSystem MakeSystem(string name, Material mat, int max, bool collision, float gravity, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 1f;
            main.startSpeed = 0f;
            main.startSize = 0.1f;
            main.gravityModifier = gravity;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.enabled = false;
            if (collision)
            {
                var c = ps.collision;
                c.enabled = true;
                c.type = ParticleSystemCollisionType.World;
                c.mode = ParticleSystemCollisionMode.Collision3D;
                c.collidesWith = ~0;
                c.enableDynamicColliders = true;
                c.maxCollisionShapes = 128;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = mode;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingFudge = 0f;
            ps.Play();
            return ps;
        }

        static void SetSizeOverLife(ParticleSystem ps, float start, float end)
        {
            var s = ps.sizeOverLifetime;
            s.enabled = true;
            float mx = Mathf.Max(start, end);
            var curve = new AnimationCurve(new Keyframe(0f, start / mx), new Keyframe(1f, end / mx));
            s.size = new ParticleSystem.MinMaxCurve(mx, curve);
        }

        static void SetFade(ParticleSystem ps, Color c)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(c.a, 0f), new GradientAlphaKey(c.a * 0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        static void SetDrag(ParticleSystem ps, float drag)
        {
            var l = ps.limitVelocityOverLifetime;
            l.enabled = true;
            l.limit = new ParticleSystem.MinMaxCurve(1000f);
            l.drag = new ParticleSystem.MinMaxCurve(drag);
            l.multiplyDragByParticleSize = false;
            l.multiplyDragByParticleVelocity = false;
        }

        static void SetSpin(ParticleSystem ps, float degPerSec)
        {
            var r = ps.rotationOverLifetime;
            r.enabled = true;
            r.z = new ParticleSystem.MinMaxCurve(-degPerSec * Mathf.Deg2Rad, degPerSec * Mathf.Deg2Rad);
            if (ps.main.startRotation3D)
            {
                r.separateAxes = true;
                r.x = new ParticleSystem.MinMaxCurve(-degPerSec * Mathf.Deg2Rad, degPerSec * Mathf.Deg2Rad);
                r.y = new ParticleSystem.MinMaxCurve(-degPerSec * Mathf.Deg2Rad, degPerSec * Mathf.Deg2Rad);
            }
        }

        public static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color32 color)
        {
            if (ps == null) return;
            var ep = new ParticleSystem.EmitParams();
            ep.position = pos;
            ep.velocity = vel;
            ep.startSize = size;
            ep.startLifetime = life;
            ep.startColor = color;
            ep.applyShapeToPosition = false;
            ps.Emit(ep, 1);
        }

        static void Emit3D(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color32 color, Vector3 rot)
        {
            var ep = new ParticleSystem.EmitParams();
            ep.position = pos;
            ep.velocity = vel;
            ep.startSize = size;
            ep.startLifetime = life;
            ep.startColor = color;
            ep.rotation3D = rot;
            ep.applyShapeToPosition = false;
            ps.Emit(ep, 1);
        }

        int Count(float n) { return Mathf.Max(1, Mathf.RoundToInt(n * Budget)); }

        public void EmitBloodDrop(Vector3 pos, Vector3 vel, float size, Color color)
        {
            Emit(BloodDrops, pos, vel, size, rng.Range(1.5f, 3f), color);
        }

        public void EmitBloodSpray(Vector3 pos, Vector3 dir, float speed, float spreadDeg, int count, float size, Color color, Vector3 inherit)
        {
            int n = Count(count * Mathf.Max(0.2f, Sandbox.BloodAmount));
            if (Sandbox.BloodAmount <= 0.001f) return;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = Quaternion.AngleAxis(rng.Range(0f, 360f), dir) * Quaternion.AngleAxis(rng.Range(0f, spreadDeg), Perp(dir)) * dir;
                Emit(BloodDrops, pos, inherit + d * speed * rng.Range(0.5f, 1.1f), size * rng.Range(0.5f, 1.3f), rng.Range(1.5f, 3f), color);
            }
        }

        public void EmitMist(Vector3 pos, Vector3 vel, int count, float size, Color color)
        {
            if (Sandbox.BloodAmount <= 0.001f) return;
            int n = Count(count);
            for (int i = 0; i < n; i++)
                Emit(BloodMist, pos + rng.OnUnitSphere() * size * 0.3f, vel + rng.OnUnitSphere() * 0.6f, size * rng.Range(0.6f, 1.4f), rng.Range(0.6f, 1.4f), color);
        }

        static Vector3 Perp(Vector3 d)
        {
            Vector3 p = Vector3.Cross(d, Vector3.up);
            if (p.sqrMagnitude < 1e-4f) p = Vector3.Cross(d, Vector3.right);
            return p.normalized;
        }

        public void Explosion(Vector3 pos, float radius, float scale)
        {
            int fire = Count(28 * scale);
            for (int i = 0; i < fire; i++)
            {
                Vector3 d = rng.OnUnitSphere(); d.y = Mathf.Abs(d.y) * 0.8f + 0.1f;
                Emit(Fire, pos + d * radius * 0.08f, d * rng.Range(2f, 7f) * Mathf.Sqrt(scale), radius * rng.Range(0.18f, 0.32f), rng.Range(0.35f, 0.8f), new Color32(255, 255, 255, 255));
            }
            int smoke = Count(18 * scale);
            for (int i = 0; i < smoke; i++)
            {
                Vector3 d = rng.OnUnitSphere(); d.y = Mathf.Abs(d.y);
                float g = rng.Range(0.6f, 1f);
                Emit(Smoke, pos + d * radius * 0.15f + Vector3.up * rng.Range(0f, radius * 0.3f), d * rng.Range(0.5f, 3f) + Vector3.up * rng.Range(1f, 3.5f),
                    radius * rng.Range(0.25f, 0.45f), rng.Range(3.5f, 7.5f), new Color(g, g, g, 1f));
            }
            int sparks = Count(40 * scale);
            for (int i = 0; i < sparks; i++)
            {
                Vector3 d = rng.OnUnitSphere(); d.y = Mathf.Abs(d.y) * 0.9f + 0.05f;
                Emit(Sparks, pos, d * rng.Range(6f, 22f), rng.Range(0.03f, 0.07f), rng.Range(0.4f, 1.4f), new Color32(255, 220, 140, 255));
            }
            int debris = Count(14 * scale);
            for (int i = 0; i < debris; i++)
            {
                Vector3 d = rng.OnUnitSphere(); d.y = Mathf.Abs(d.y) + 0.3f;
                float g = rng.Range(0.6f, 1f);
                Emit3D(Debris, pos + Vector3.up * 0.1f, d.normalized * rng.Range(4f, 13f), rng.Range(0.05f, 0.16f), rng.Range(3f, 5f),
                    new Color(g, g * 0.95f, g * 0.9f, 1f), new Vector3(rng.Range(0, 360), rng.Range(0, 360), rng.Range(0, 360)));
            }
            int dust = Count(16 * scale);
            for (int i = 0; i < dust; i++)
            {
                Vector2 c = rng.InsideUnitCircle().normalized;
                Vector3 d = new Vector3(c.x, 0.05f, c.y);
                Emit(Dust, pos + d * radius * 0.2f, d * rng.Range(4f, 9f), radius * rng.Range(0.2f, 0.35f), rng.Range(1.5f, 3f), new Color(1, 1, 1, 1));
            }
            Emit(Shockwave, pos + Vector3.up * 0.15f, Vector3.zero, radius * 2.4f, 0.45f, new Color32(255, 240, 220, 220));
            Flash(pos + Vector3.up * 0.5f, new Color(1f, 0.75f, 0.4f), 9f * Mathf.Sqrt(scale), radius * 3.5f, 0.28f);
        }

        public void Impact(Vector3 pos, Vector3 normal, bool metal, bool wood)
        {
            int n = Count(metal ? 10 : 6);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = (normal + rng.OnUnitSphere() * 0.7f).normalized;
                if (metal) Emit(Sparks, pos, d * rng.Range(3f, 9f), rng.Range(0.02f, 0.04f), rng.Range(0.2f, 0.6f), new Color32(255, 210, 120, 255));
                else if (wood) Emit3D(Splinters, pos, d * rng.Range(2f, 5f), rng.Range(0.03f, 0.08f), rng.Range(1.5f, 3f), new Color32(255, 255, 255, 255),
                    new Vector3(rng.Range(0, 360), rng.Range(0, 360), rng.Range(0, 360)));
            }
            Emit(Dust, pos + normal * 0.05f, normal * 0.6f, 0.25f, 0.8f, new Color(1, 1, 1, 0.8f));
        }

        public void MuzzleFlash(Vector3 pos, Vector3 dir, float scale)
        {
            for (int i = 0; i < 3; i++) Emit(Muzzle, pos + dir * (0.03f * i), dir * (2f + i), 0.12f * scale * (1f - i * 0.2f), 0.06f, new Color32(255, 230, 170, 255));
            Flash(pos, new Color(1f, 0.8f, 0.5f), 3.5f * scale, 6f, 0.06f);
        }

        public void ElectricSparks(Vector3 pos, Vector3 normal, int count)
        {
            int n = Count(count);
            for (int i = 0; i < n; i++)
                Emit(Zaps, pos, (normal + rng.OnUnitSphere()).normalized * rng.Range(2f, 6f), rng.Range(0.02f, 0.05f), rng.Range(0.1f, 0.35f), new Color32(170, 220, 255, 255));
        }

        public void SteamPuff(Vector3 pos, Vector3 vel)
        {
            Emit(Steam, pos, vel + rng.OnUnitSphere() * 0.3f, rng.Range(0.3f, 0.6f), rng.Range(1f, 2f), new Color(1, 1, 1, 1));
        }

        public void Flash(Vector3 pos, Color c, float intensity, float range, float duration)
        {
            var f = flashes[nextFlash];
            nextFlash = (nextFlash + 1) % flashes.Length;
            f.light.transform.position = pos;
            f.light.color = c;
            f.light.range = range;
            f.light.intensity = intensity;
            f.light.enabled = true;
            f.t = 0f; f.dur = Mathf.Max(0.02f, duration); f.intensity = intensity;
            flashes[(nextFlash + flashes.Length - 1) % flashes.Length] = f;
        }

        public void Tracer(Vector3 a, Vector3 b, Color c, float width, float duration)
        {
            var t = tracers[nextTracer];
            nextTracer = (nextTracer + 1) % tracers.Length;
            t.lr.positionCount = 2;
            t.lr.SetPosition(0, a);
            t.lr.SetPosition(1, b);
            t.lr.startWidth = width; t.lr.endWidth = width * 0.6f;
            t.lr.startColor = c; t.lr.endColor = c;
            t.lr.enabled = true;
            t.t = 0f; t.dur = duration; t.color = c; t.width = width;
            tracers[(nextTracer + tracers.Length - 1) % tracers.Length] = t;
        }

        /// <summary>Jagged lightning bolt between two points (shock tool, electric floor).</summary>
        public void Lightning(Vector3 a, Vector3 b, Color c, float width, float duration)
        {
            var t = tracers[nextTracer];
            nextTracer = (nextTracer + 1) % tracers.Length;
            int n = 10;
            t.lr.positionCount = n;
            Vector3 d = b - a;
            Vector3 p1 = Perp(d.normalized), p2 = Vector3.Cross(d.normalized, p1);
            float jag = d.magnitude * 0.08f;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)(n - 1);
                Vector3 off = (i == 0 || i == n - 1) ? Vector3.zero : p1 * rng.Range(-jag, jag) + p2 * rng.Range(-jag, jag);
                t.lr.SetPosition(i, a + d * u + off);
            }
            t.lr.startWidth = width; t.lr.endWidth = width;
            t.lr.startColor = c; t.lr.endColor = c;
            t.lr.enabled = true;
            t.t = 0f; t.dur = duration; t.color = c; t.width = width;
            tracers[(nextTracer + tracers.Length - 1) % tracers.Length] = t;
        }

        public void Tick(float dt)
        {
            float udt = Time.unscaledDeltaTime;
            for (int i = 0; i < flashes.Length; i++)
            {
                if (!flashes[i].light.enabled) continue;
                flashes[i].t += udt;
                float k = 1f - flashes[i].t / flashes[i].dur;
                if (k <= 0f) flashes[i].light.enabled = false;
                else flashes[i].light.intensity = flashes[i].intensity * k * k;
            }
            for (int i = 0; i < tracers.Length; i++)
            {
                if (!tracers[i].lr.enabled) continue;
                tracers[i].t += udt;
                float k = 1f - tracers[i].t / Mathf.Max(0.01f, tracers[i].dur);
                if (k <= 0f) { tracers[i].lr.enabled = false; continue; }
                Color c = tracers[i].color; c.a *= k;
                tracers[i].lr.startColor = c; tracers[i].lr.endColor = c;
            }
        }

        public void Clear()
        {
            ParticleSystem[] all = { BloodDrops, BloodMist, Fire, Smoke, Sparks, Dust, Debris, Shockwave, Muzzle, Zaps, Splinters, Steam };
            for (int i = 0; i < all.Length; i++) if (all[i] != null) all[i].Clear();
        }
    }
    #endregion

    #region DecalSystem
    public enum DecalKind { Blood, Scorch, BulletHole, Footprint, Handprint, Smear, Crack }

    /// <summary>Decals on static geometry are batched into chunked meshes (one draw call per chunk) with a global cap
    /// that recycles the oldest; decals on moving bodies use a small pool of parented quads.</summary>
    public class DecalSystem
    {
        const int ChunkSize = 256;

        class Chunk
        {
            public Mesh mesh;
            public Vector3[] v;
            public Vector2[] uv;
            public Color32[] c;
            public bool geomDirty, colorDirty;
        }

        struct Rec
        {
            public bool alive;
            public float birth;
            public DecalKind kind;
            public Color baseColor;
            public float baseAlpha;
        }

        Chunk[] chunks;
        Rec[] recs;
        int capacity, allocated, next;
        float ageTimer;
        Transform root;

        // Dynamic (parented) decals
        class DynDecal { public GameObject go; public Transform t; public Mesh mesh; public Color32[] colors; public float birth; public Color baseColor; public bool alive; }
        DynDecal[] dyn;
        int nextDyn;
        static readonly Vector3[] quadTmp = new Vector3[4];
        static readonly Vector2[] uvTmp = new Vector2[4];

        public int Count
        {
            get { int n = 0; for (int i = 0; i < capacity; i++) if (recs[i].alive) n++; return n; }
        }

        public void Build(Transform parent, int max)
        {
            root = new GameObject("Decals").transform;
            root.SetParent(parent, false);
            allocated = Mathf.Max(256, Mathf.Max(max, 2000));
            allocated = ((allocated + ChunkSize - 1) / ChunkSize) * ChunkSize;
            capacity = Mathf.Clamp(max, 64, allocated);
            recs = new Rec[allocated];
            int nChunks = allocated / ChunkSize;
            chunks = new Chunk[nChunks];
            for (int ci = 0; ci < nChunks; ci++)
            {
                var ch = new Chunk();
                ch.v = new Vector3[ChunkSize * 4];
                ch.uv = new Vector2[ChunkSize * 4];
                ch.c = new Color32[ChunkSize * 4];
                var tris = new int[ChunkSize * 6];
                for (int q = 0; q < ChunkSize; q++)
                {
                    int vi = q * 4, ti = q * 6;
                    tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                    tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
                }
                ch.mesh = new Mesh();
                ch.mesh.name = "DecalChunk" + ci;
                ch.mesh.MarkDynamic();
                ch.mesh.vertices = ch.v;
                ch.mesh.uv = ch.uv;
                ch.mesh.colors32 = ch.c;
                ch.mesh.triangles = tris;
                ch.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4000f);
                var go = new GameObject("DecalChunk" + ci);
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = ch.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MaterialFactory.Decal;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                chunks[ci] = ch;
            }
            dyn = new DynDecal[160];
            for (int i = 0; i < dyn.Length; i++) dyn[i] = MakeDyn(i);
        }

        public void SetCapacity(int n)
        {
            if (recs == null) return;
            int newCap = Mathf.Clamp(n, 64, allocated);
            if (newCap < capacity)
            {
                for (int i = newCap; i < capacity; i++) Kill(i);
                if (next >= newCap) next = 0;
            }
            capacity = newCap;
        }

        void Kill(int i)
        {
            if (!recs[i].alive) return;
            recs[i].alive = false;
            var ch = chunks[i / ChunkSize];
            int vi = (i % ChunkSize) * 4;
            for (int k = 0; k < 4; k++) { ch.v[vi + k] = Vector3.zero; ch.c[vi + k] = new Color32(0, 0, 0, 0); }
            ch.geomDirty = true; ch.colorDirty = true;
        }

        public void Clear()
        {
            for (int i = 0; i < allocated; i++) Kill(i);
            for (int i = 0; i < dyn.Length; i++)
            {
                if (dyn[i].go == null) { dyn[i] = MakeDyn(i); continue; }
                dyn[i].alive = false; dyn[i].go.SetActive(false); dyn[i].t.SetParent(root, false);
            }
            next = 0;
        }

        /// <summary>Adds a decal. If 'attach' is a moving body, the decal sticks to it.</summary>
        public void Add(Vector3 pos, Vector3 normal, float size, float angleDeg, int cell, Color color, DecalKind kind,
            float stretch = 1f, Transform attach = null)
        {
            if (recs == null || size <= 0.001f || !MathUtil.IsFinite(pos)) return;
            normal = MathUtil.SafeNormal(normal, Vector3.up);
            Vector3 t = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.95f ? Vector3.up : Vector3.forward).normalized;
            t = Quaternion.AngleAxis(angleDeg, normal) * t;
            Vector3 bt = Vector3.Cross(normal, t);
            Rect uv = TextureFactory.AtlasRect(cell);
            float hw = size * 0.5f * stretch, hh = size * 0.5f;
            if (attach != null)
            {
                AddDynamic(pos, normal, t, bt, hw, hh, uv, color, attach);
                return;
            }
            int i = next;
            next = (next + 1) % capacity;
            var ch = chunks[i / ChunkSize];
            int vi = (i % ChunkSize) * 4;
            float offset = 0.004f + (i % 7) * 0.0009f;
            Vector3 c = pos + normal * offset;
            ch.v[vi] = c - t * hw - bt * hh;
            ch.v[vi + 1] = c + t * hw - bt * hh;
            ch.v[vi + 2] = c + t * hw + bt * hh;
            ch.v[vi + 3] = c - t * hw + bt * hh;
            ch.uv[vi] = new Vector2(uv.xMin, uv.yMin);
            ch.uv[vi + 1] = new Vector2(uv.xMax, uv.yMin);
            ch.uv[vi + 2] = new Vector2(uv.xMax, uv.yMax);
            ch.uv[vi + 3] = new Vector2(uv.xMin, uv.yMax);
            Color32 c32 = color;
            for (int k = 0; k < 4; k++) ch.c[vi + k] = c32;
            ch.geomDirty = true; ch.colorDirty = true;
            recs[i].alive = true;
            recs[i].birth = Time.time;
            recs[i].kind = kind;
            recs[i].baseColor = color;
            recs[i].baseAlpha = color.a;
        }

        DynDecal MakeDyn(int i)
        {
            var d = new DynDecal();
            d.go = new GameObject("DynDecal" + i);
            d.t = d.go.transform;
            d.t.SetParent(root, false);
            d.mesh = new Mesh();
            d.mesh.name = "DynDecal";
            d.mesh.MarkDynamic();
            d.mesh.vertices = new Vector3[4];
            d.mesh.uv = new Vector2[4];
            d.colors = new Color32[4];
            d.mesh.colors32 = d.colors;
            d.mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            d.go.AddComponent<MeshFilter>().sharedMesh = d.mesh;
            var mr = d.go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MaterialFactory.Decal;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            d.go.SetActive(false);
            return d;
        }

        void AddDynamic(Vector3 pos, Vector3 normal, Vector3 t, Vector3 bt, float hw, float hh, Rect uv, Color color, Transform attach)
        {
            if (dyn[nextDyn].go == null) dyn[nextDyn] = MakeDyn(nextDyn);
            var d = dyn[nextDyn];
            nextDyn = (nextDyn + 1) % dyn.Length;
            d.t.SetParent(attach, false);
            d.t.position = pos + normal * 0.005f;
            d.t.rotation = Quaternion.LookRotation(t, normal);
            // local quad: x = bitangent, z = tangent (LookRotation forward = t, up = normal)
            Vector3 s = d.t.lossyScale;
            float sx = Mathf.Abs(s.x) > 1e-4f ? 1f / s.x : 1f, sz = Mathf.Abs(s.z) > 1e-4f ? 1f / s.z : 1f;
            quadTmp[0] = new Vector3(-hh * sx, 0f, -hw * sz);
            quadTmp[1] = new Vector3(-hh * sx, 0f, hw * sz);
            quadTmp[2] = new Vector3(hh * sx, 0f, hw * sz);
            quadTmp[3] = new Vector3(hh * sx, 0f, -hw * sz);
            uvTmp[0] = new Vector2(uv.xMin, uv.yMin); uvTmp[1] = new Vector2(uv.xMax, uv.yMin);
            uvTmp[2] = new Vector2(uv.xMax, uv.yMax); uvTmp[3] = new Vector2(uv.xMin, uv.yMax);
            d.mesh.vertices = quadTmp;
            d.mesh.uv = uvTmp;
            Color32 c32 = color;
            for (int k = 0; k < 4; k++) d.colors[k] = c32;
            d.mesh.colors32 = d.colors;
            d.mesh.RecalculateBounds();
            d.baseColor = color;
            d.birth = Time.time;
            d.alive = true;
            d.go.SetActive(true);
        }

        /// <summary>Releases parented decals when their body is destroyed.</summary>
        public void DetachFrom(Transform t)
        {
            for (int i = 0; i < dyn.Length; i++)
            {
                if (!dyn[i].alive || dyn[i].go == null) continue;
                if (dyn[i].t.parent == t || dyn[i].t.IsChildOf(t))
                {
                    dyn[i].alive = false;
                    dyn[i].go.SetActive(false);
                    dyn[i].t.SetParent(root, false);
                }
            }
        }

        public void Tick(float dt)
        {
            if (recs == null) return;
            ageTimer -= dt;
            if (ageTimer > 0f) return;
            ageTimer = 0.5f;
            var bs = Sandbox.Boot.blood;
            float now = Time.time;
            for (int i = 0; i < capacity; i++)
            {
                if (!recs[i].alive) continue;
                DecalKind k = recs[i].kind;
                if (k != DecalKind.Blood && k != DecalKind.Footprint && k != DecalKind.Handprint && k != DecalKind.Smear) continue;
                float age = now - recs[i].birth;
                if (age > bs.decalDryTime * 1.3f) continue;
                float u = Mathf.Clamp01(age / bs.decalDryTime);
                Color c = Color.Lerp(recs[i].baseColor, bs.dried, u * u * 0.85f);
                c.a = recs[i].baseAlpha * Mathf.Lerp(1f, 0.85f, u);
                Color32 c32 = c;
                var ch = chunks[i / ChunkSize];
                int vi = (i % ChunkSize) * 4;
                for (int q = 0; q < 4; q++) ch.c[vi + q] = c32;
                ch.colorDirty = true;
            }
            for (int i = 0; i < dyn.Length; i++)
            {
                var d = dyn[i];
                if (!d.alive) continue;
                if (d.go == null) { dyn[i] = MakeDyn(i); continue; }
                if (d.t.parent == null || !d.t.parent.gameObject.activeInHierarchy) { d.alive = false; d.go.SetActive(false); d.t.SetParent(root, false); continue; }
                float age = now - d.birth;
                if (age > bs.decalDryTime * 1.3f) continue;
                Color c = Color.Lerp(d.baseColor, bs.dried, Mathf.Clamp01(age / bs.decalDryTime) * 0.85f);
                Color32 c32 = c;
                for (int k = 0; k < 4; k++) d.colors[k] = c32;
                d.mesh.colors32 = d.colors;
            }
        }

        public void LateTick()
        {
            if (chunks == null) return;
            for (int i = 0; i < chunks.Length; i++)
            {
                var ch = chunks[i];
                if (ch.geomDirty)
                {
                    ch.mesh.vertices = ch.v;
                    ch.mesh.uv = ch.uv;
                    ch.geomDirty = false;
                    ch.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4000f);
                }
                if (ch.colorDirty)
                {
                    ch.mesh.colors32 = ch.c;
                    ch.colorDirty = false;
                }
            }
        }
    }
    #endregion
}
