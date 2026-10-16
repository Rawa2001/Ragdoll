// Ragdoll Sandbox - blood simulation: pooled droplet/spray/mist particles with world collision, splat decals
// (irregular blobs, directional streaks, wall drips), painting splatter into body textures, growing/merging/drying
// puddles, bloody footprints and handprints that fade over the next steps, and drag smears.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region BloodSystem
    public class BloodSystem
    {
        class Puddle
        {
            public GameObject go;
            public Transform t;
            public MeshRenderer mr;
            public Vector3 pos, normal;
            public float volume, radius, shownRadius, lastFeed, rot;
            public bool alive;
        }

        Puddle[] puddles;
        int nextPuddle;
        Mesh[] puddleMeshes;
        MaterialPropertyBlock mpb;
        Transform root;
        Rng rng = new Rng(9090);
        readonly RaycastHit[] hits = new RaycastHit[12];
        float printTimer, colorTimer;
        int decalsThisFrame;
        int frame;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int SmoothId = Shader.PropertyToID("_Smoothness");
        static readonly int GlossId = Shader.PropertyToID("_Glossiness");
        readonly Dictionary<BodyPart, Vector3> lastPrint = new Dictionary<BodyPart, Vector3>(32);

        public int PuddleCount { get { int n = 0; if (puddles != null) for (int i = 0; i < puddles.Length; i++) if (puddles[i].alive) n++; return n; } }

        public void Build(Transform parent)
        {
            root = new GameObject("Blood").transform;
            root.SetParent(parent, false);
            mpb = new MaterialPropertyBlock();
            puddleMeshes = new Mesh[4];
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2) * 0.5f, y = (i / 2) * 0.5f;
                puddleMeshes[i] = MeshFactory.Quad(new Rect(x + 0.002f, y + 0.002f, 0.496f, 0.496f));
            }
            int n = Mathf.Clamp(Sandbox.Boot.blood.maxPuddles, 4, 256);
            puddles = new Puddle[n];
            for (int i = 0; i < n; i++)
            {
                var p = new Puddle();
                p.go = new GameObject("Puddle" + i);
                p.t = p.go.transform;
                p.t.SetParent(root, false);
                p.go.AddComponent<MeshFilter>().sharedMesh = puddleMeshes[i % 4];
                p.mr = p.go.AddComponent<MeshRenderer>();
                p.mr.sharedMaterial = MaterialFactory.Puddle;
                p.mr.shadowCastingMode = ShadowCastingMode.Off;
                p.mr.receiveShadows = true;
                p.go.SetActive(false);
                puddles[i] = p;
            }
        }

        public void Clear()
        {
            if (puddles == null) return;
            for (int i = 0; i < puddles.Length; i++) { puddles[i].alive = false; puddles[i].go.SetActive(false); }
            lastPrint.Clear();
        }

        /// <summary>Blood flowing from a wound: forms or feeds a puddle on the ground beneath it.</summary>
        public void FlowFrom(Vector3 woundPos, float liters, RagdollBody body)
        {
            if (liters <= 0f || Sandbox.BloodAmount <= 0.001f) return;
            int n = Physics.RaycastNonAlloc(woundPos + Vector3.up * 0.02f, Vector3.down, hits, 0.7f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            int bi = -1;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                if (h.normal.y < 0.5f) continue;
                if (h.distance < best) { best = h.distance; bi = i; }
            }
            if (bi < 0) return;
            AddPuddle(hits[bi].point, hits[bi].normal, liters * Sandbox.BloodAmount);
        }

        public void AddPuddle(Vector3 pos, Vector3 normal, float liters)
        {
            var bs = Sandbox.Boot.blood;
            Puddle target = null;
            float bd = float.MaxValue;
            for (int i = 0; i < puddles.Length; i++)
            {
                var p = puddles[i];
                if (!p.alive) continue;
                float d = (p.pos - pos).magnitude;
                if (d < p.radius * 0.9f + 0.08f && d < bd && Mathf.Abs(p.pos.y - pos.y) < 0.15f) { bd = d; target = p; }
            }
            if (target == null)
            {
                target = puddles[nextPuddle];
                nextPuddle = (nextPuddle + 1) % puddles.Length;
                target.alive = true;
                target.pos = pos;
                target.normal = normal;
                target.volume = 0f;
                target.radius = 0f;
                target.shownRadius = 0.02f;
                target.rot = rng.Range(0f, 360f);
                target.go.SetActive(true);
            }
            else
            {
                // drift the centre toward the new inflow a little
                target.pos = Vector3.Lerp(target.pos, pos, liters / Mathf.Max(0.001f, target.volume + liters) * 0.5f);
            }
            target.volume += liters;
            target.lastFeed = Time.time;
            target.radius = Mathf.Min(bs.puddleMaxRadius, Mathf.Sqrt(target.volume * 0.001f / (Mathf.PI * 0.0022f)));
            MergeOverlaps(target);
        }

        void MergeOverlaps(Puddle p)
        {
            for (int i = 0; i < puddles.Length; i++)
            {
                var o = puddles[i];
                if (o == p || !o.alive) continue;
                float d = (o.pos - p.pos).magnitude;
                if (d < (o.radius + p.radius) * 0.6f && Mathf.Abs(o.pos.y - p.pos.y) < 0.15f)
                {
                    Puddle big = o.volume > p.volume ? o : p, small = big == o ? p : o;
                    big.volume += small.volume;
                    big.radius = Mathf.Min(Sandbox.Boot.blood.puddleMaxRadius, Mathf.Sqrt(big.volume * 0.001f / (Mathf.PI * 0.0022f)));
                    big.lastFeed = Mathf.Max(big.lastFeed, small.lastFeed);
                    small.alive = false;
                    small.go.SetActive(false);
                    if (small == p) return;
                }
            }
        }

        public void Tick(float dt)
        {
            frame++;
            decalsThisFrame = 0;
            if (puddles == null) return;
            var bs = Sandbox.Boot.blood;
            colorTimer -= dt;
            bool recolor = colorTimer <= 0f;
            if (recolor) colorTimer = 0.25f;
            float now = Time.time;
            for (int i = 0; i < puddles.Length; i++)
            {
                var p = puddles[i];
                if (!p.alive) continue;
                p.shownRadius = Mathf.MoveTowards(p.shownRadius, p.radius, dt * 0.25f);
                p.t.position = p.pos + p.normal * (0.006f + (i % 5) * 0.0008f);
                p.t.rotation = Quaternion.FromToRotation(Vector3.up, p.normal) * Quaternion.Euler(0f, p.rot, 0f);
                p.t.localScale = Vector3.one * Mathf.Max(0.02f, p.shownRadius * 2f);
                if (recolor)
                {
                    float dry = Mathf.Clamp01((now - p.lastFeed) / bs.puddleDryTime);
                    Color c = Color.Lerp(new Color(bs.venous.r * 0.9f, bs.venous.g, bs.venous.b, 0.96f), new Color(bs.dried.r, bs.dried.g, bs.dried.b, 0.85f), dry);
                    mpb.Clear();
                    mpb.SetColor(BaseColorId, c);
                    mpb.SetColor(ColorId, c);
                    mpb.SetFloat(SmoothId, Mathf.Lerp(0.93f, 0.25f, dry));
                    mpb.SetFloat(GlossId, Mathf.Lerp(0.93f, 0.25f, dry));
                    p.mr.SetPropertyBlock(mpb);
                }
            }
            printTimer -= dt;
            if (printTimer <= 0f) { printTimer = 0.2f; HandPrintsAndSmears(); }
        }

        /// <summary>Called by the step planner when a foot plants: picks up blood from puddles, leaves fading footprints.</summary>
        public void OnFootPlant(BodyPart foot)
        {
            if (foot == null || Sandbox.BloodAmount <= 0.001f) return;
            var bs = Sandbox.Boot.blood;
            Vector3 sole = foot.rb.position + foot.rb.rotation * (new Vector3(0f, -0.07f, 0.06f) * foot.body.Scale);
            for (int i = 0; i < puddles.Length; i++)
            {
                var p = puddles[i];
                if (!p.alive) continue;
                if ((p.pos - sole).magnitude < p.shownRadius + 0.05f && Time.time - p.lastFeed < bs.puddleDryTime * 0.8f) { foot.bloodiness = 1f; break; }
            }
            if (foot.bloodiness < 0.06f || bs.footprintSteps <= 0) return;
            RaycastHit hit;
            if (!foot.body.Balance.Ground(sole + Vector3.up * 0.15f, 0.4f, out hit)) return;
            Vector3 fwd = MathUtil.Flat(foot.rb.rotation * Vector3.forward);
            bool left = foot.id == PartId.FootL || foot.id == PartId.ShinL;
            Color c = bs.venous; c.a = Mathf.Clamp01(foot.bloodiness) * 0.85f;
            float ang = DecalAngle(hit.normal, fwd);
            Sandbox.Decals.Add(hit.point, hit.normal, 0.3f * foot.body.Scale, ang - 90f, left ? (int)SplatCell.FootL : (int)SplatCell.FootR, c, DecalKind.Footprint, 1f);
            foot.bloodiness *= 1f - 1f / Mathf.Max(1, bs.footprintSteps);
        }

        void HandPrintsAndSmears()
        {
            if (Sandbox.BloodAmount <= 0.001f) return;
            var bs = Sandbox.Boot.blood;
            for (int r = 0; r < Sandbox.Ragdolls.Count; r++)
            {
                var body = Sandbox.Ragdolls[r];
                if (body == null) continue;
                for (int i = 0; i < body.AllPieces.Count; i++)
                {
                    var p = body.AllPieces[i];
                    if (p == null || !p.Touching) continue;
                    bool hand = p.id == PartId.HandL || p.id == PartId.HandR;
                    bool bleeding = HasBleeding(body, p);
                    if (!bleeding && p.bloodiness < 0.15f) continue;
                    if (p.contactCollider == null) continue;
                    Rigidbody crb = p.contactCollider.attachedRigidbody;
                    if (crb != null && !crb.isKinematic) continue;
                    Vector3 last;
                    bool had = lastPrint.TryGetValue(p, out last);
                    Vector3 pt = p.contactPoint;
                    if (had && (last - pt).sqrMagnitude < 0.18f * 0.18f) continue;
                    lastPrint[p] = pt;
                    Vector3 v = Compat.GetVelocity(p.rb);
                    Vector3 tangential = Vector3.ProjectOnPlane(v, p.contactNormal);
                    Color c = bleeding ? bs.arterial : bs.venous;
                    if (hand && tangential.magnitude < 0.6f)
                    {
                        c.a = Mathf.Clamp01(Mathf.Max(p.bloodiness, bleeding ? 0.8f : 0f)) * 0.85f;
                        float ang = DecalAngle(p.contactNormal, p.rb.rotation * (p.id == PartId.HandL ? Vector3.left : Vector3.right));
                        Sandbox.Decals.Add(pt, p.contactNormal, 0.2f * body.Scale, ang - 90f, (int)SplatCell.Hand, c, DecalKind.Handprint);
                        p.bloodiness *= 0.75f;
                    }
                    else if (tangential.magnitude > 0.35f)
                    {
                        c.a = 0.75f;
                        float ang = DecalAngle(p.contactNormal, tangential);
                        float size = 0.12f * body.Scale + (bleeding ? 0.05f : 0f);
                        Sandbox.Decals.Add(pt, p.contactNormal, size, ang, rng.Value < 0.5f ? (int)SplatCell.Smear0 : (int)SplatCell.Smear1, c, DecalKind.Smear,
                            Mathf.Clamp(tangential.magnitude * 0.8f, 1.2f, 3f));
                        p.bloodiness *= 0.9f;
                    }
                }
            }
        }

        static bool HasBleeding(RagdollBody body, BodyPart p)
        {
            var list = body.Wounds.Wounds;
            for (int i = 0; i < list.Count; i++) if (list[i].Active && list[i].part == p && list[i].bleedRate * (1f - list[i].clot) > 0.002f) return true;
            return false;
        }

        /// <summary>Decal rotation (degrees about the normal) that aligns the decal's long axis with 'dir'.</summary>
        public static float DecalAngle(Vector3 normal, Vector3 dir)
        {
            normal = MathUtil.SafeNormal(normal, Vector3.up);
            Vector3 t0 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.95f ? Vector3.up : Vector3.forward).normalized;
            Vector3 d = Vector3.ProjectOnPlane(dir, normal);
            if (d.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(t0, d, normal);
        }

        /// <summary>A blood droplet hit something: splat decal on the world, paint on bodies, drips on walls.</summary>
        public void OnDropletHit(Vector3 point, Vector3 normal, Vector3 velocity, Component other)
        {
            if (decalsThisFrame > 40) return;
            decalsThisFrame++;
            var bs = Sandbox.Boot.blood;
            float amount = Mathf.Sqrt(Mathf.Max(0.05f, Sandbox.BloodAmount));
            Color c = Color.Lerp(bs.arterial, bs.venous, rng.Value);
            c.a = rng.Range(0.75f, 0.95f);
            var col = other as Collider;
            if (col == null || (Sandbox.Player != null && col == Sandbox.Player.Collider)) return;
            BodyPart part = Registry.PartOf(col);
            float speed = velocity.magnitude;
            float size = Mathf.Clamp(0.035f + speed * 0.012f, 0.03f, 0.16f) * amount * rng.Range(0.8f, 1.3f);
            if (part != null)
            {
                if (part.body != null && part.body.Damage != null) part.body.Damage.PaintBlood(part, point, size * 0.6f, c);
                return;
            }
            Transform attach = null;
            if (col != null && col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) attach = col.transform;
            Vector3 tangential = Vector3.ProjectOnPlane(velocity, normal);
            float stretch = 1f + Mathf.Clamp(tangential.magnitude / 4f, 0f, 2.2f);
            int cell;
            float ang;
            if (stretch > 1.5f)
            {
                cell = (int)SplatCell.Streak0 + rng.Range(0, 6);
                ang = DecalAngle(normal, tangential);
            }
            else
            {
                cell = rng.Range(0, 12);
                ang = rng.Range(0f, 360f);
                stretch = 1f;
            }
            Sandbox.Decals.Add(point, normal, size, ang, cell, c, DecalKind.Blood, stretch, attach);
            if (attach == null && normal.y < 0.35f && normal.y > -0.5f && rng.Value < 0.35f)
            {
                // drip line running down the wall
                Vector3 down = Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
                float dripLen = size * rng.Range(1.6f, 3.2f);
                float dAng = DecalAngle(normal, -down) - 90f;
                Sandbox.Decals.Add(point + down * dripLen * 0.35f, normal, dripLen, dAng, (int)SplatCell.Drip0 + rng.Range(0, 4), c, DecalKind.Blood, 1f);
            }
            if (speed > 4f && rng.Value < 0.08f && Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Splat, point, 0.25f, rng.Range(0.8f, 1.3f), 1f);
        }

        /// <summary>Big burst (stumps, gunshot exits, explosions).</summary>
        public void Burst(Vector3 pos, Vector3 dir, float strength, Vector3 inherit)
        {
            if (Sandbox.Fx == null || Sandbox.BloodAmount <= 0f) return;
            var bs = Sandbox.Boot.blood;
            Sandbox.Fx.EmitBloodSpray(pos, MathUtil.SafeNormal(dir, Vector3.up), 2f + 3f * strength, 45f, Mathf.RoundToInt(18 * strength), 0.025f, bs.arterial, inherit);
            if (Sandbox.Gore == GoreLevel.Full) Sandbox.Fx.EmitMist(pos, inherit + dir * strength, Mathf.RoundToInt(5 * strength), 0.12f, new Color(bs.arterial.r, bs.arterial.g, bs.arterial.b, 0.55f));
        }
    }

    /// <summary>Receives blood droplet particle collisions (world collision module, collision messages on).</summary>
    public class BloodCollisionHandler : MonoBehaviour
    {
        ParticleSystem ps;
        readonly List<ParticleCollisionEvent> events = new List<ParticleCollisionEvent>(64);

        void Awake() { ps = GetComponent<ParticleSystem>(); }

        void OnParticleCollision(GameObject other)
        {
            if (ps == null || Sandbox.Blood == null) return;
            int n = ps.GetCollisionEvents(other, events);
            for (int i = 0; i < n && i < 16; i++)
            {
                var e = events[i];
                Sandbox.Blood.OnDropletHit(e.intersection, e.normal, e.velocity, e.colliderComponent);
            }
        }
    }
    #endregion
}
