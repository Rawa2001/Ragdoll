// Ragdoll Sandbox - explosions: radial force with falloff, line-of-sight shielding, clamped impulses, per-part blast
// damage, shrapnel raycasts, chain reactions, visuals (flash, fireball, smoke, shockwave, debris, sparks, dust,
// scorch decal), camera shake + FOV kick and a procedural boom. Explosive types: grenade, TNT, C4, barrel,
// landmine and the mega blast.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    #region ExplosionSystem
    public enum ExplosiveKind { Grenade, TNT, C4, Barrel, Landmine, Mega }

    public class ExplosionSystem
    {
        struct Pending { public ExplosiveProp prop; public float at; }

        readonly List<ExplosiveProp> live = new List<ExplosiveProp>(64);
        readonly List<Pending> pending = new List<Pending>(32);
        readonly Collider[] overlap = new Collider[512];
        readonly HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();
        readonly HashSet<RagdollBody> nearBodies = new HashSet<RagdollBody>();
        readonly RaycastHit[] losHits = new RaycastHit[16];
        Transform root;
        Rng rng = new Rng(777);
        public static Mesh GrenadeMesh, TntMesh, C4Mesh, BarrelMesh, MineMesh, MegaMesh;
        public static Material TntMat, C4Mat, MineMat, MegaMat, GrenadeMat;

        public void Build(Transform parent)
        {
            root = new GameObject("Explosives").transform;
            root.SetParent(parent, false);
            BuildMeshes();
        }

        static void BuildMeshes()
        {
            var b = new MeshBuilder();
            MeshFactory.AddEllipsoid(b, Vector3.zero, new Vector3(0.045f, 0.055f, 0.045f), Quaternion.identity, 8, 12);
            MeshFactory.AddLathe(b, new[] { new Vector2(0.018f, 0.045f), new Vector2(0.018f, 0.07f), new Vector2(0.01f, 0.075f) }, 10, Vector3.zero, Quaternion.identity, 1f, 1f, false, true, 0);
            MeshFactory.AddBox(b, new Vector3(0.02f, 0.06f, 0.035f), new Vector3(0.01f, 0.05f, 0.012f), Quaternion.Euler(0f, 0f, -15f), 0f);
            GrenadeMesh = b.ToMesh("Grenade");
            b = new MeshBuilder();
            for (int i = 0; i < 3; i++)
            {
                Vector3 off = new Vector3((i - 1) * 0.045f, 0f, (i == 1 ? 0.02f : -0.01f));
                MeshFactory.AddLathe(b, new[] { new Vector2(0.022f, -0.1f), new Vector2(0.022f, 0.1f) }, 12, off, Quaternion.identity, 1f, 4f, true, true, 0);
            }
            MeshFactory.AddBox(b, Vector3.zero, new Vector3(0.15f, 0.03f, 0.07f), Quaternion.identity, 0f);
            TntMesh = b.ToMesh("TNT");
            b = new MeshBuilder();
            MeshFactory.AddBox(b, Vector3.zero, new Vector3(0.14f, 0.045f, 0.09f), Quaternion.identity, 0f);
            MeshFactory.AddBox(b, new Vector3(0f, 0.027f, 0f), new Vector3(0.06f, 0.012f, 0.04f), Quaternion.identity, 0f);
            C4Mesh = b.ToMesh("C4");
            BarrelMesh = MeshFactory.Lathe(new[] { new Vector2(0.28f, -0.44f), new Vector2(0.3f, -0.4f), new Vector2(0.3f, -0.15f), new Vector2(0.315f, -0.13f),
                new Vector2(0.3f, -0.11f), new Vector2(0.3f, 0.11f), new Vector2(0.315f, 0.13f), new Vector2(0.3f, 0.15f), new Vector2(0.3f, 0.4f), new Vector2(0.28f, 0.44f) }, 20, 1f, 1.1f);
            MineMesh = MeshFactory.Lathe(new[] { new Vector2(0.15f, 0f), new Vector2(0.16f, 0.02f), new Vector2(0.14f, 0.045f), new Vector2(0.05f, 0.055f), new Vector2(0.03f, 0.07f) }, 18);
            MegaMesh = MeshFactory.Sphere(0.35f, 14, 20);
            GrenadeMat = MaterialFactory.Lit("Grenade", new Color(0.28f, 0.34f, 0.2f), TextureFactory.Metal, 0.35f, 0.4f);
            TntMat = MaterialFactory.Lit("TNT", new Color(0.8f, 0.12f, 0.08f), TextureFactory.PaintOrange, 0.3f, 0f);
            C4Mat = MaterialFactory.Lit("C4", new Color(0.82f, 0.78f, 0.62f), TextureFactory.Sand, 0.15f, 0f);
            MineMat = MaterialFactory.Lit("Landmine", new Color(0.3f, 0.33f, 0.25f), TextureFactory.Metal, 0.4f, 0.5f);
            MegaMat = MaterialFactory.Lit("Mega", new Color(0.9f, 0.85f, 0.3f), TextureFactory.Metal, 0.8f, 0.6f);
            MaterialFactory.SetEmission(MegaMat, new Color(1.5f, 0.9f, 0.2f));
        }

        public void Register(ExplosiveProp p) { if (p != null && !live.Contains(p)) live.Add(p); }
        public void Unregister(ExplosiveProp p) { live.Remove(p); }

        public ExplosiveProp Spawn(ExplosiveKind kind, Vector3 pos, Quaternion rot, Vector3 velocity, Transform parent = null)
        {
            var go = new GameObject(kind.ToString());
            go.transform.SetParent(parent != null ? parent : root, false);
            go.transform.SetPositionAndRotation(pos, rot);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Collider col;
            switch (kind)
            {
                case ExplosiveKind.Grenade:
                    mf.sharedMesh = GrenadeMesh; mr.sharedMaterial = GrenadeMat; rb.mass = 0.45f;
                    var gs = go.AddComponent<SphereCollider>(); gs.radius = 0.05f; col = gs; col.sharedMaterial = PhysMats.Rubber; break;
                case ExplosiveKind.TNT:
                    mf.sharedMesh = TntMesh; mr.sharedMaterial = TntMat; rb.mass = 1.2f;
                    var tb = go.AddComponent<BoxCollider>(); tb.size = new Vector3(0.16f, 0.2f, 0.08f); col = tb; col.sharedMaterial = PhysMats.Wood; break;
                case ExplosiveKind.C4:
                    mf.sharedMesh = C4Mesh; mr.sharedMaterial = C4Mat; rb.mass = 0.6f;
                    var cb = go.AddComponent<BoxCollider>(); cb.size = new Vector3(0.14f, 0.05f, 0.09f); col = cb; col.sharedMaterial = PhysMats.Rubber; break;
                case ExplosiveKind.Barrel:
                    mf.sharedMesh = BarrelMesh; mr.sharedMaterial = MaterialFactory.BarrelRed; rb.mass = 45f;
                    var bc = go.AddComponent<CapsuleCollider>(); bc.radius = 0.3f; bc.height = 0.9f; col = bc; col.sharedMaterial = PhysMats.Metal;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; break;
                case ExplosiveKind.Landmine:
                    mf.sharedMesh = MineMesh; mr.sharedMaterial = MineMat; rb.mass = 3f; rb.isKinematic = true;
                    var mc = go.AddComponent<CapsuleCollider>(); mc.direction = 1; mc.radius = 0.16f; mc.height = 0.32f; mc.center = new Vector3(0f, 0.03f, 0f);
                    col = mc;
                    var trig = go.AddComponent<SphereCollider>(); trig.isTrigger = true; trig.radius = 0.32f; trig.center = new Vector3(0f, 0.1f, 0f);
                    break;
                default:
                    mf.sharedMesh = MegaMesh; mr.sharedMaterial = MegaMat; rb.mass = 30f;
                    var ms = go.AddComponent<SphereCollider>(); ms.radius = 0.35f; col = ms; col.sharedMaterial = PhysMats.Metal; break;
            }
            Compat.SetVelocity(rb, velocity);
            if (velocity.sqrMagnitude > 0.1f) rb.angularVelocity = rng.OnUnitSphere() * 6f;
            var ep = go.AddComponent<ExplosiveProp>();
            ep.Init(kind, rb, col);
            Registry.RegisterDamageable(col, ep);
            Register(ep);
            if (parent == null && Sandbox.IsHost && Sandbox.Net != null) Sandbox.Net.OnPropSpawned(go, NetSession.ExplosiveKindBase + (int)kind);
            return ep;
        }

        public void Schedule(ExplosiveProp p, float delay)
        {
            if (p == null || p.Detonated || p.Scheduled) return;
            p.Scheduled = true;
            pending.Add(new Pending { prop = p, at = Time.time + delay });
        }

        public void DetonateAllC4()
        {
            int n = 0;
            for (int i = 0; i < live.Count; i++)
            {
                var p = live[i];
                if (p != null && p.Kind == ExplosiveKind.C4 && !p.Detonated) { Schedule(p, 0.05f + n * 0.06f); n++; }
            }
            if (n > 0)
            {
                if (Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Beep, 0.6f, 1.4f);
                SandboxEvents.RaiseToast("Detonating " + n + " C4", new Color(1f, 0.8f, 0.4f));
            }
        }

        public int CountC4()
        {
            int n = 0;
            for (int i = 0; i < live.Count; i++) if (live[i] != null && live[i].Kind == ExplosiveKind.C4 && !live[i].Detonated) n++;
            return n;
        }

        public void Tick(float dt)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (p.prop == null) { pending.RemoveAt(i); continue; }
                if (Time.time >= p.at)
                {
                    pending.RemoveAt(i);
                    p.prop.Detonate();
                }
            }
            for (int i = live.Count - 1; i >= 0; i--) if (live[i] == null) live.RemoveAt(i);
        }

        public void Clear()
        {
            for (int i = 0; i < live.Count; i++) if (live[i] != null && !live[i].IsWorldProp) UnityEngine.Object.Destroy(live[i].gameObject);
            pending.Clear();
        }

        /// <summary>Radial explosion. force = impulse (N*s) at the centre, damage = blast damage at the centre.</summary>
        public void Explode(Vector3 pos, float radius, float force, float damage, float visualScale, int shrapnel, ExplosiveProp source)
        {
            var es = Sandbox.Boot.explosions;
            if (!MathUtil.IsFinite(pos)) return;
            // Visuals and sound
            Sandbox.Fx.Explosion(pos, radius, visualScale);
            RaycastHit gh;
            if (Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out gh, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody grb = gh.collider.attachedRigidbody;
                Sandbox.Decals.Add(gh.point, gh.normal, radius * 0.85f, rng.Range(0f, 360f), rng.Value < 0.5f ? (int)SplatCell.Scorch0 : (int)SplatCell.Scorch1,
                    new Color(1f, 1f, 1f, 0.9f), DecalKind.Scorch, 1f, grb != null && !grb.isKinematic ? gh.collider.transform : null);
            }
            if (Sandbox.Audio != null)
            {
                bool big = radius > 5f;
                Sandbox.Audio.Play(big ? Sfx.Explosion : Sfx.ExplosionSmall, pos, 1f, rng.Range(0.85f, 1.05f) * (radius > 12f ? 0.7f : 1f), radius * 2f);
            }
            if (Sandbox.Cam != null)
            {
                float d = (Sandbox.MainCamera.transform.position - pos).magnitude;
                float k = Mathf.Clamp01(1f - d / (radius * 6f)) * es.cameraShake * Mathf.Sqrt(visualScale);
                Sandbox.Cam.AddShake(k);
                Sandbox.Cam.AddFovKick(es.fovKick * k);
            }
            if (Sandbox.Player != null) Sandbox.Player.ExplosionPush(pos, radius, force);
            // online: the host simulates, clients only see and hear it (bodies and props come in snapshots)
            if (Sandbox.IsHost && Sandbox.Net != null) Sandbox.Net.OnExplosion(pos, radius, visualScale);
            if (Sandbox.IsClient) return;

            // Physics
            pushed.Clear();
            nearBodies.Clear();
            int n = Physics.OverlapSphereNonAlloc(pos, radius, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider c = overlap[i];
                if (c == null) continue;
                Rigidbody rb = c.attachedRigidbody;
                Vector3 closest = ClosestPoint(c, pos);
                float dist = (closest - pos).magnitude;
                float falloff = Mathf.Clamp01(1f - dist / radius);
                falloff = Mathf.Pow(falloff, 1.4f);
                if (falloff <= 0.001f) continue;
                float shield = Occlusion(pos, closest, c);
                Vector3 dir = MathUtil.SafeNormal(closest - pos, Vector3.up);
                dir = (dir + Vector3.up * 0.35f).normalized;
                BodyPart part = Registry.PartOf(c);
                if (rb != null && !rb.isKinematic && pushed.Add(rb))
                {
                    float impulse = force * falloff * shield;
                    float dv = Mathf.Min(es.maxDeltaV, impulse / Mathf.Max(0.05f, rb.mass));
                    if (part != null) dv = Mathf.Min(dv, es.maxDeltaV * 0.8f);
                    rb.AddForceAtPosition(dir * dv * rb.mass, Vector3.Lerp(rb.worldCenterOfMass, closest, 0.5f), ForceMode.Impulse);
                }
                IDamageable dmg = Registry.DamageableOf(c);
                if (dmg != null && !(source != null && dmg == (IDamageable)source))
                {
                    var info = DamageInfo.Make(DamageType.Blast, damage * falloff * shield, closest, dir);
                    info.impulse = force * falloff * shield;
                    info.radius = radius;
                    dmg.ApplyDamage(info);
                }
                if (part != null && part.body != null) nearBodies.Add(part.body);
            }
            for (int r = 0; r < Sandbox.Ragdolls.Count; r++)
            {
                var b = Sandbox.Ragdolls[r];
                if (b == null) continue;
                float d = (b.CenterOfMass - pos).magnitude;
                if (d < radius * 2.5f) b.Brain.OnExplosionNearby(pos, radius);
            }
            // Shrapnel
            for (int i = 0; i < shrapnel; i++)
            {
                Vector3 d = rng.OnUnitSphere();
                if (d.y < -0.2f) d.y = -d.y * 0.3f;
                RaycastHit hit;
                if (!Physics.Raycast(pos + d * 0.1f, d, out hit, radius * 1.5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                float k = Mathf.Clamp01(1f - hit.distance / (radius * 1.5f));
                IDamageable dmg = Registry.DamageableOf(hit.collider);
                if (dmg != null && !(source != null && dmg == (IDamageable)source))
                {
                    var info = DamageInfo.Make(DamageType.Pierce, 10f * k + 4f, hit.point, d);
                    info.normal = hit.normal;
                    info.depth = 0.04f;
                    dmg.ApplyDamage(info);
                }
                if (hit.rigidbody != null && !hit.rigidbody.isKinematic) hit.rigidbody.AddForceAtPosition(d * 4f * k, hit.point, ForceMode.Impulse);
                else if (rng.Value < 0.4f) Sandbox.Decals.Add(hit.point, hit.normal, 0.06f, rng.Range(0f, 360f), (int)SplatCell.Hole0 + rng.Range(0, 2), new Color(1, 1, 1, 0.8f), DecalKind.BulletHole);
            }
            // Chain reactions
            for (int i = 0; i < live.Count; i++)
            {
                var p = live[i];
                if (p == null || p == source || p.Detonated || p.Scheduled) continue;
                float d = (p.transform.position - pos).magnitude;
                if (d < radius * (p.Kind == ExplosiveKind.Barrel ? 0.9f : 0.75f)) Schedule(p, es.chainDelay * (0.5f + d / radius) + rng.Range(0f, 0.08f));
            }
            SandboxEvents.RaiseExplosion(pos, radius);
        }

        static Vector3 ClosestPoint(Collider c, Vector3 p)
        {
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider) return c.ClosestPoint(p);
            var mc = c as MeshCollider;
            if (mc != null && mc.convex) return c.ClosestPoint(p);
            return c.ClosestPointOnBounds(p);
        }

        /// <summary>1 = clear line of sight, 0.25 = shielded by static geometry.</summary>
        float Occlusion(Vector3 from, Vector3 to, Collider target)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.2f) return 1f;
            int n = Physics.RaycastNonAlloc(from, d / len, losHits, len - 0.05f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = losHits[i].collider;
                if (c == target) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                return 0.25f;
            }
            return 1f;
        }
    }
    #endregion

    #region ExplosiveProp
    /// <summary>An explosive object (thrown or placed). Detonates on fuse, impact damage, pressure or remote trigger.</summary>
    public class ExplosiveProp : MonoBehaviour, IDamageable
    {
        public ExplosiveKind Kind;
        public bool Detonated, Scheduled, IsWorldProp;
        /// <summary>Network client copy: inert, moved by snapshots, removed by the host.</summary>
        public bool Remote;
        public float Fuse = -1f;
        public float Health = 40f;
        Rigidbody rb;
        Collider col;
        bool stuck;
        FixedJoint stickJoint;
        float beepTimer;
        int fuseLoop = -1;
        float armTime;
        bool mineTriggered;
        Light blink;

        public void Init(ExplosiveKind kind, Rigidbody body, Collider c)
        {
            Kind = kind;
            rb = body;
            col = c;
            armTime = Time.time + (kind == ExplosiveKind.Landmine ? 1.2f : 0.2f);
            var es = Sandbox.Boot.explosions;
            switch (kind)
            {
                case ExplosiveKind.Grenade: Fuse = es.grenadeFuse; Health = 1000f; break;
                case ExplosiveKind.TNT: Fuse = -1f; Health = 30f; break;
                case ExplosiveKind.C4: Fuse = -1f; Health = 2000f; break;
                case ExplosiveKind.Barrel: Fuse = -1f; Health = 35f; break;
                case ExplosiveKind.Landmine: Fuse = -1f; Health = 50f; break;
                case ExplosiveKind.Mega: Fuse = 3.5f; Health = 5000f; break;
            }
            if (kind == ExplosiveKind.C4 || kind == ExplosiveKind.Mega || kind == ExplosiveKind.Landmine)
            {
                var lgo = new GameObject("Blink");
                lgo.transform.SetParent(transform, false);
                lgo.transform.localPosition = new Vector3(0f, kind == ExplosiveKind.Mega ? 0.4f : 0.06f, 0f);
                blink = lgo.AddComponent<Light>();
                blink.type = LightType.Point;
                blink.range = kind == ExplosiveKind.Mega ? 4f : 1.2f;
                blink.intensity = 0f;
                blink.color = kind == ExplosiveKind.C4 ? new Color(1f, 0.2f, 0.15f) : new Color(1f, 0.7f, 0.2f);
                blink.shadows = LightShadows.None;
            }
        }

        public void LightFuse(float seconds)
        {
            if (Detonated || Fuse >= 0f) return;
            Fuse = seconds;
        }

        void Update()
        {
            if (Detonated) return;
            if (Remote)
            {
                if (blink != null) blink.intensity = (Mathf.Repeat(Time.time * 1.5f, 1f) < 0.2f) ? 1.5f : 0f;
                return;
            }
            if (Fuse >= 0f)
            {
                Fuse -= Time.deltaTime;
                if (Kind == ExplosiveKind.TNT || Kind == ExplosiveKind.Grenade)
                {
                    if (fuseLoop < 0 && Sandbox.Audio != null && Kind == ExplosiveKind.TNT) fuseLoop = Sandbox.Audio.StartLoop(Sfx.Fuse, transform, 0.35f, 1f, 100);
                    if (Kind == ExplosiveKind.TNT && Sandbox.Fx != null && Time.frameCount % 3 == 0)
                        FxSystem.Emit(Sandbox.Fx.Sparks, transform.position + transform.up * 0.11f, UnityEngine.Random.onUnitSphere * 1.5f + Vector3.up, 0.03f, 0.3f, new Color32(255, 200, 120, 255));
                }
                if (Kind == ExplosiveKind.Mega)
                {
                    beepTimer -= Time.deltaTime;
                    if (beepTimer <= 0f) { beepTimer = Mathf.Max(0.12f, Fuse * 0.25f); Beep(1.2f); }
                }
                if (Fuse <= 0f) Detonate();
            }
            if (blink != null)
            {
                float rate = Kind == ExplosiveKind.Mega ? 6f : 1.5f;
                blink.intensity = (Mathf.Repeat(Time.time * rate, 1f) < 0.2f) ? (Kind == ExplosiveKind.Mega ? 6f : 1.5f) : 0f;
            }
        }

        void Beep(float pitch)
        {
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Beep, transform.position, 0.6f, pitch, 2f);
        }

        void OnCollisionEnter(Collision c)
        {
            if (Detonated || Remote) return;
            float imp = c.impulse.magnitude;
            if (Kind == ExplosiveKind.C4 && !stuck && Time.time > armTime - 0.15f) StickTo(c);
            if (Kind == ExplosiveKind.Grenade && imp > 0.3f && Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Clang, transform.position, Mathf.Clamp01(imp * 0.3f), 1.8f);
            if (Kind == ExplosiveKind.Barrel && imp > 90f) ApplyDamage(DamageInfo.Make(DamageType.Blunt, (imp - 90f) * 0.25f, transform.position, Vector3.down));
            if (Kind == ExplosiveKind.Barrel && imp > 15f && Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Clang, transform.position, Mathf.Clamp01(imp / 150f), 0.55f);
            if (Kind == ExplosiveKind.TNT && imp > 140f) LightFuse(0.05f);
        }

        void OnTriggerEnter(Collider other)
        {
            if (Kind != ExplosiveKind.Landmine || Detonated || Remote || mineTriggered || Time.time < armTime) return;
            Rigidbody orb = other.attachedRigidbody;
            bool heavy = (orb != null && !orb.isKinematic && orb.mass > 4f) || Registry.PartOf(other) != null || other is CharacterController;
            if (!heavy) return;
            mineTriggered = true;
            Beep(2f);
            Sandbox.Explosions.Schedule(this, 0.3f);
        }

        void StickTo(Collision c)
        {
            stuck = true;
            if (c.rigidbody != null)
            {
                stickJoint = gameObject.AddComponent<FixedJoint>();
                stickJoint.connectedBody = c.rigidbody;
                stickJoint.breakForce = 4000f;
                rb.mass = 0.2f;
                BodyPart part = Registry.PartOf(c.collider);
                if (part != null)
                {
                    Physics.IgnoreCollision(col, c.collider, true);
                    SandboxEvents.RaiseToast("C4 stuck to " + PartInfo.Short[(int)part.id], new Color(1f, 0.85f, 0.5f));
                }
            }
            else
            {
                rb.isKinematic = true;
            }
            if (c.contactCount > 0)
            {
                var cp = c.GetContact(0);
                transform.rotation = Quaternion.FromToRotation(transform.up, cp.normal) * transform.rotation;
            }
            Beep(1.6f);
        }

        public void ApplyDamage(DamageInfo info)
        {
            if (Detonated || Remote) return;
            if (Kind == ExplosiveKind.Grenade || Kind == ExplosiveKind.Mega) return;
            if (Kind == ExplosiveKind.C4) { if (info.type == DamageType.Blast && info.amount > 25f) Sandbox.Explosions.Schedule(this, 0.05f); return; }
            Health -= info.amount;
            if (info.type == DamageType.Pierce && Kind == ExplosiveKind.Barrel && Sandbox.Fx != null) Sandbox.Fx.Impact(info.point, info.normal, true, false);
            if (Health <= 0f) Sandbox.Explosions.Schedule(this, Kind == ExplosiveKind.Barrel ? 0.12f : 0.05f);
            else if (Kind == ExplosiveKind.TNT && info.type != DamageType.Blunt) LightFuse(0.6f);
        }

        public void Detonate()
        {
            if (Detonated) return;
            Detonated = true;
            var es = Sandbox.Boot.explosions;
            Vector3 p = rb != null ? rb.worldCenterOfMass : transform.position;
            float r, f, d, vis;
            switch (Kind)
            {
                case ExplosiveKind.Grenade: r = es.grenadeRadius; f = es.grenadeForce; d = es.grenadeDamage; vis = 0.9f; break;
                case ExplosiveKind.TNT: r = es.tntRadius; f = es.tntForce; d = es.tntDamage; vis = 1.3f; break;
                case ExplosiveKind.C4: r = es.c4Radius; f = es.c4Force; d = es.c4Damage; vis = 1.2f; break;
                case ExplosiveKind.Barrel: r = es.barrelRadius; f = es.barrelForce; d = es.barrelDamage; vis = 1.4f; break;
                case ExplosiveKind.Landmine: r = es.mineRadius; f = es.mineForce; d = es.mineDamage; vis = 1f; p += Vector3.up * 0.1f; break;
                default: r = es.megaRadius; f = es.megaForce; d = es.megaDamage; vis = 3.5f; break;
            }
            if (fuseLoop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(fuseLoop);
            fuseLoop = -1;
            if (col != null) Registry.Unregister(col);
            gameObject.SetActive(false);
            Sandbox.Explosions.Explode(p, r, f, d, vis, Kind == ExplosiveKind.Mega ? es.shrapnel * 3 : es.shrapnel, this);
            if (IsWorldProp && Sandbox.World != null) Sandbox.World.OnWorldExplosiveGone(this);
            Destroy(gameObject, 0.05f);
        }

        void OnDestroy()
        {
            if (fuseLoop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(fuseLoop);
            if (col != null) Registry.Unregister(col);
            if (Sandbox.Explosions != null) Sandbox.Explosions.Unregister(this);
        }
    }
    #endregion
}
