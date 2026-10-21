// Ragdoll Sandbox - damage and anatomy simulation.
//  * VitalSystem: 5 L blood, heart rate/beats, breathing, blood oxygen, pain, adrenaline, shock, consciousness, death.
//  * WoundSystem: wounds with location, depth, severity, bleed rate, clotting (except arteries), drips and arterial spurts.
//  * OrganSystem: brain, heart, lungs, liver, stomach, kidneys, intestines; organ damage effects; rupture -> spilled organs
//    on soft tethers that can be torn free.
//  * DamageSystem: collision impulses, crush, falls, blades, bullets, blasts, shock; bruises, dents, gashes, fractures.
//  * DismemberSystem: joint severing and plane slicing anywhere along a bone (mesh + physics split, caps, stumps).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    #region VitalSystem
    public class VitalSystem
    {
        readonly RagdollBody body;
        public float MaxBlood, BloodVolume;
        public float HeartRate, HeartPhase;
        public int BeatCount;
        public float Oxygen = 1f, BrainOxygen = 1f;
        public float Pain, Adrenaline, Shock, Fatigue, Dizziness;
        public float Consciousness = 1f;
        public bool Dead, Unconscious, CardiacArrest;
        public string CauseOfDeath = "";
        public float BreathRate = 14f, BreathDepth = 1f;
        public float ExternalBleedRate, InternalBleedRate;
        public float KnockoutTimer, HeadTrauma, HeadlessTime, TotalTrauma;
        public float ShockTime;
        public float Exertion;
        float hypoxiaTime, lowBloodTime, deathTime = -1f;
        float lastBreathSound;
        bool breathIn;

        public VitalSystem(RagdollBody b)
        {
            body = b;
            MaxBlood = Sandbox.Boot.vitals.bloodVolume * Mathf.Lerp(0.8f, 1.2f, b.MassScale - 0.5f);
            BloodVolume = MaxBlood;
            HeartRate = Sandbox.Boot.vitals.restingHeartRate;
        }

        public float BloodFraction { get { return MaxBlood > 0f ? BloodVolume / MaxBlood : 0f; } }

        /// <summary>0 at the death threshold, 1 at full blood.</summary>
        public float BloodNorm
        {
            get
            {
                var vs = Sandbox.Boot.vitals;
                return Mathf.Clamp01((BloodFraction - vs.deathBloodFraction) / Mathf.Max(0.05f, 1f - vs.deathBloodFraction));
            }
        }

        public float MuscleFactor
        {
            get
            {
                float f = Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(BrainOxygen * 1.3f)) * Mathf.Lerp(0.35f, 1f, BloodNorm) * (1f + 0.2f * Adrenaline);
                return Mathf.Clamp(f, 0f, 1.2f);
            }
        }

        public float HeadlessDrive
        {
            get
            {
                if (body.BrainPresent) return 0f;
                float t = Sandbox.Boot.vitals.headlessSpasmTime;
                return Mathf.Clamp01(1f - HeadlessTime / Mathf.Max(0.1f, t)) * BloodNorm;
            }
        }

        public float Pressure
        {
            get
            {
                if (Dead || HeartRate < 5f) return 0.04f;
                return Mathf.Sqrt(Mathf.Clamp(HeartRate / 70f, 0.2f, 2.2f)) * Mathf.Clamp(BloodFraction / 0.6f, 0.15f, 1f);
            }
        }

        public void LoseBlood(float liters)
        {
            if (liters <= 0f || Dead && BloodVolume <= 0f) return;
            BloodVolume = Mathf.Max(0f, BloodVolume - liters);
        }

        public void AddPain(float p) { Pain = Mathf.Clamp01(Pain + p); body.Brain.OnPain(p * 100f); }
        public void AddAdrenaline(float a) { Adrenaline = Mathf.Clamp01(Adrenaline + a); }
        public void Knockout(float seconds) { KnockoutTimer = Mathf.Max(KnockoutTimer, seconds); }

        public void Kill(string cause)
        {
            if (Dead) return;
            Dead = true;
            CauseOfDeath = cause;
            deathTime = Time.time;
            Consciousness = 0f;
            Unconscious = true;
            body.NotifyDeath();
            SandboxEvents.RaiseToast("Ragdoll " + (body.Index + 1) + " died (" + cause + ")", new Color(1f, 0.4f, 0.35f));
        }

        public bool CanRevive { get { return Dead && Time.time - deathTime < 20f && body.BrainPresent && body.Organs.BrainIntact; } }

        public void Revive(bool full)
        {
            Dead = false;
            CauseOfDeath = "";
            CardiacArrest = false;
            hypoxiaTime = 0f;
            lowBloodTime = 0f;
            if (full) { BloodVolume = MaxBlood; Pain = 0f; Shock = 0f; TotalTrauma = 0f; HeadTrauma = 0f; }
            BloodVolume = Mathf.Max(BloodVolume, MaxBlood * 0.75f);
            Oxygen = 1f; BrainOxygen = 0.8f; HeartRate = 90f; Consciousness = 0.3f; KnockoutTimer = 0f;
            body.RebuildTopology();
        }

        public void FixedTick(float dt)
        {
            var vs = Sandbox.Boot.vitals;
            var org = body.Organs;
            if (Dead)
            {
                HeartRate = Mathf.MoveTowards(HeartRate, 0f, 60f * dt);
                Consciousness = 0f;
                BreathRate = 0f;
                BreathDepth = 0f;
                InternalBleedRate = org.InternalBleedRate * 0.1f;
                LoseBlood(InternalBleedRate * dt);
                return;
            }
            float bloodNorm = BloodNorm;
            // Heart
            float heartHealth = org.HeartHealth01;
            if (heartHealth <= 0f || !body.Has(PartId.Chest)) CardiacArrest = true;
            if (BloodFraction < vs.deathBloodFraction + 0.02f && Shock > 0.95f) CardiacArrest = true;
            float target;
            if (CardiacArrest) target = 0f;
            else
            {
                target = vs.restingHeartRate + 80f * (1f - bloodNorm) + 35f * Pain + 50f * Adrenaline + 40f * Exertion;
                target *= Mathf.Lerp(0.6f, 1f, heartHealth);
                if (Consciousness < 0.3f) target -= 10f;
                target = Mathf.Clamp(target, 30f, 190f);
            }
            HeartRate = Mathf.MoveTowards(HeartRate, target, (CardiacArrest ? 45f : 22f) * dt);
            if (HeartRate > 1f)
            {
                HeartPhase += HeartRate / 60f * dt;
                if (HeartPhase >= 1f) { HeartPhase -= 1f; BeatCount++; }
            }
            float perfusion = HeartRate > 15f ? Mathf.Clamp01(HeartRate / 60f) * Mathf.Clamp01((BloodFraction - 0.25f) / 0.45f) : 0f;
            // Breathing (needs the brain stem, an intact neck spine and working lungs)
            bool breathing = body.BrainPresent && !body.BodyParalyzed;
            float lungEff = org.LungEfficiency * (breathing ? 1f : 0f);
            BreathRate = breathing ? 13f + 18f * Exertion + 14f * (1f - bloodNorm) + 8f * Pain + 10f * (1f - Oxygen) : 0f;
            BreathDepth = breathing ? Mathf.Lerp(0.35f, 1.2f, lungEff) * (Consciousness > 0.2f ? 1f : 0.6f) : 0f;
            float intake = lungEff * 0.35f * (1f - Oxygen);
            float use = 0.012f + Exertion * 0.03f;
            Oxygen = Mathf.Clamp01(Oxygen + (intake - use * (1f - lungEff * 0.9f)) * dt);
            BrainOxygen = Mathf.MoveTowards(BrainOxygen, Oxygen * perfusion, dt * (Oxygen * perfusion < BrainOxygen ? 0.09f : 0.25f));
            if (!body.BrainPresent) BrainOxygen = 0f;
            if (BrainOxygen < 0.2f) hypoxiaTime += dt; else hypoxiaTime = Mathf.Max(0f, hypoxiaTime - dt * 2f);
            // Internal bleeding from organ damage
            InternalBleedRate = org.InternalBleedRate;
            LoseBlood(InternalBleedRate * Pressure * dt);
            // Shock, pain, adrenaline, fatigue
            float shockTarget = Mathf.Clamp01((1f - BloodFraction - 0.15f) / 0.3f) + Mathf.Clamp01(TotalTrauma / 900f) * 0.3f;
            Shock = Mathf.MoveTowards(Shock, Mathf.Clamp01(shockTarget), dt * 0.08f);
            Pain = Mathf.Max(0f, Pain - vs.painDecay * dt * (1f + Adrenaline));
            Adrenaline = Mathf.Max(0f, Adrenaline - vs.adrenalineDecay * dt);
            float exert = Mathf.Clamp01(body.ComVelocity.magnitude / 3.5f) + (body.Brain.State == MotorState.GetUp || body.Brain.State == MotorState.Crawl ? 0.6f : 0f);
            Exertion = Mathf.MoveTowards(Exertion, Mathf.Clamp01(exert), dt * 0.4f);
            var ms = Sandbox.Boot.muscles;
            Fatigue = Mathf.Clamp01(Fatigue + (Exertion * ms.fatigueRate - (1f - Exertion) * ms.fatigueRecovery) * dt);
            if (ShockTime > 0f) ShockTime = Mathf.Max(0f, ShockTime - dt * 0.5f);
            HeadTrauma = Mathf.Max(0f, HeadTrauma - dt * 0.01f);
            // Consciousness
            float c = 1f;
            c = Mathf.Min(c, MathUtil.Remap(BrainOxygen, 0.22f, 0.65f, 0f, 1f));
            c = Mathf.Min(c, MathUtil.Remap(BloodFraction, vs.deathBloodFraction + 0.03f, vs.unconsciousBloodFraction, 0f, 1f));
            c *= org.BrainHealth01 < 0.3f ? 0f : Mathf.Lerp(0.5f, 1f, org.BrainHealth01);
            if (KnockoutTimer > 0f) { KnockoutTimer -= dt; c = 0f; }
            if (Pain * (1f - 0.6f * Adrenaline) > 0.97f) KnockoutTimer = Mathf.Max(KnockoutTimer, 2.5f);
            c = Mathf.Clamp01(c + Adrenaline * 0.15f * (c > 0.05f ? 1f : 0f));
            c = Mathf.Pow(c, Mathf.Lerp(1.5f, 0.6f, vs.willToLive));
            if (!body.BrainPresent) c = 0f;
            Consciousness = c < Consciousness ? Mathf.MoveTowards(Consciousness, c, dt * 1.5f) : Mathf.MoveTowards(Consciousness, c, dt * 0.22f);
            if (!Unconscious && Consciousness < 0.25f)
            {
                Unconscious = true;
                body.NotifyUnconscious();
                if (body.BrainPresent) SandboxEvents.RaiseToast("Ragdoll " + (body.Index + 1) + " is unconscious", new Color(1f, 0.8f, 0.4f));
            }
            else if (Unconscious && Consciousness > vs.wakeThreshold)
            {
                Unconscious = false;
                body.NotifyWake();
                SandboxEvents.RaiseToast("Ragdoll " + (body.Index + 1) + " woke up", new Color(0.6f, 1f, 0.6f));
            }
            Dizziness = Mathf.Clamp01((1f - bloodNorm) * 1.2f + (1f - BrainOxygen) * 0.8f + HeadTrauma * 0.5f - 0.15f);
            // Death
            if (!body.BrainPresent)
            {
                HeadlessTime += dt;
                if (HeadlessTime > vs.headlessSpasmTime) Kill("decapitated");
            }
            else if (!org.BrainIntact) Kill("brain destroyed");
            if (BloodFraction < vs.deathBloodFraction) { lowBloodTime += dt; if (lowBloodTime > 2.5f) Kill("blood loss"); } else lowBloodTime = 0f;
            if (hypoxiaTime > vs.brainDeathTime) Kill(CardiacArrest ? "cardiac arrest" : "hypoxia");
            if (TotalTrauma > Sandbox.Boot.damage.overwhelmingTrauma) Kill("massive trauma");
            // Breathing audio for the possessed / nearby body
            if (breathing && Sandbox.Audio != null && Consciousness > 0.1f && body.Head != null && body.HeadAttached && BreathRate > 1f)
            {
                float period = 60f / BreathRate * 0.5f;
                if (Time.time - lastBreathSound > period)
                {
                    lastBreathSound = Time.time;
                    breathIn = !breathIn;
                    float vol = Mathf.Clamp01(0.05f + Exertion * 0.25f + (1f - bloodNorm) * 0.3f + Pain * 0.2f) * (body.Possessed ? 1.6f : 1f);
                    if (vol > 0.08f) Sandbox.Audio.Play(breathIn ? Sfx.BreathIn : Sfx.BreathOut, body.Head.rb.position, vol, Mathf.Lerp(0.9f, 1.15f, Exertion), 1f);
                }
            }
        }
    }
    #endregion

    #region WoundSystem
    public class Wound
    {
        public BodyPart part;
        public Vector3 localPos, localNormal;
        public float severity, depth, bleedRate, clot, age;
        public bool arterial, stump, Active = true;
        public DamageType type;
        public float dripAccum;
        public int lastBeat;
        public float puddleTimer;

        public Vector3 WorldPosition { get { return part != null ? part.transform.TransformPoint(localPos) : Vector3.zero; } }
        public Vector3 WorldNormal { get { return part != null ? part.transform.TransformDirection(localNormal) : Vector3.up; } }
        public float CurrentRate(float pressure) { return bleedRate * (1f - clot) * pressure; }
    }

    public class WoundSystem
    {
        readonly RagdollBody body;
        public readonly List<Wound> Wounds = new List<Wound>(32);
        public float ExternalRate;
        Rng rng;
        const int MaxWounds = 48;

        public WoundSystem(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 31 + 7); }

        public Wound Add(BodyPart part, Vector3 worldPos, Vector3 worldNormal, float severity, float depth, DamageType type, bool forceArterial = false, bool stump = false)
        {
            if (part == null) return null;
            Vector3 lp = part.transform.InverseTransformPoint(worldPos);
            Vector3 ln = part.transform.InverseTransformDirection(MathUtil.SafeNormal(worldNormal, Vector3.up));
            // merge with a nearby wound
            for (int i = 0; i < Wounds.Count; i++)
            {
                var w = Wounds[i];
                if (!w.Active || w.part != part || w.stump != stump) continue;
                if ((w.localPos - lp).sqrMagnitude < 0.045f * 0.045f)
                {
                    w.severity = Mathf.Min(1f, w.severity + severity * 0.6f);
                    w.depth = Mathf.Max(w.depth, depth);
                    w.bleedRate = Mathf.Max(w.bleedRate, RateFor(part, type, w.severity, w.arterial || forceArterial, stump)) * 1.1f;
                    w.clot *= 0.5f;
                    return w;
                }
            }
            bool arterial = forceArterial || (depth > 0.025f * body.Scale && NearArtery(part, lp));
            var nw = new Wound
            {
                part = part, localPos = lp, localNormal = ln, severity = Mathf.Clamp01(severity), depth = depth, type = type,
                arterial = arterial, stump = stump, lastBeat = body.Vitals.BeatCount
            };
            nw.bleedRate = RateFor(part, type, nw.severity, arterial, stump);
            if (Wounds.Count >= MaxWounds)
            {
                int weakest = 0;
                float wr = float.MaxValue;
                for (int i = 0; i < Wounds.Count; i++)
                {
                    float r = Wounds[i].Active ? Wounds[i].bleedRate * (1f - Wounds[i].clot) : -1f;
                    if (r < wr) { wr = r; weakest = i; }
                }
                Wounds.RemoveAt(weakest);
            }
            Wounds.Add(nw);
            if (nw.bleedRate > 0.004f || arterial) body.NotifyBleedStart(nw);
            if (arterial && part.attached && !body.Vitals.Dead) SandboxEvents.RaiseToast((stump ? "Severed artery: " : "Arterial bleeding: ") + PartInfo.Short[(int)part.id], new Color(1f, 0.35f, 0.3f));
            return nw;
        }

        float RateFor(BodyPart part, DamageType type, float severity, bool arterial, bool stump)
        {
            var vs = Sandbox.Boot.vitals;
            if (stump)
            {
                float k = 1f;
                switch (part.id)
                {
                    case PartId.ThighL: case PartId.ThighR: case PartId.Pelvis: k = 1f; break;
                    case PartId.ShinL: case PartId.ShinR: k = 0.6f; break;
                    case PartId.UpperArmL: case PartId.UpperArmR: k = 0.65f; break;
                    case PartId.ForearmL: case PartId.ForearmR: k = 0.45f; break;
                    case PartId.HandL: case PartId.HandR: case PartId.FootL: case PartId.FootR: k = 0.25f; break;
                    case PartId.Neck: case PartId.Chest: case PartId.Head: k = 1.3f; break;
                    case PartId.Abdomen: k = 1.4f; break;
                }
                return vs.arterialBleedRate * k;
            }
            if (arterial) return vs.arterialBleedRate * Mathf.Lerp(0.35f, 0.8f, severity);
            switch (type)
            {
                case DamageType.Cut: return 0.0015f + 0.012f * severity;
                case DamageType.Pierce: return 0.003f + 0.01f * severity;
                case DamageType.Blast: return 0.004f + 0.016f * severity;
                default: return 0.0008f + 0.004f * severity;
            }
        }

        bool NearArtery(BodyPart part, Vector3 localPos)
        {
            var lines = BodyMeshLibrary.ArteryLines[(int)part.id];
            if (lines == null) return false;
            Vector3 refPos = localPos / body.Scale;
            for (int i = 0; i + 1 < lines.Count; i += 2)
                if (MathUtil.DistancePointSegment(refPos, lines[i], lines[i + 1]) < 0.022f) return true;
            return false;
        }

        public void FixedTick(float dt)
        {
            var vs = Sandbox.Boot.vitals;
            float pressure = body.Vitals.Pressure;
            float total = 0f;
            bool countVitals = Sandbox.Gore != GoreLevel.Off || vs.bleedWhenGoreOff;
            for (int i = Wounds.Count - 1; i >= 0; i--)
            {
                var w = Wounds[i];
                if (w.part == null) { Wounds.RemoveAt(i); continue; }
                if (!w.Active) continue;
                w.age += dt;
                if (w.part.attached)
                {
                    float rate = w.CurrentRate(pressure);
                    if (countVitals) total += rate;
                    float clotTime = vs.clotTime * (0.5f + w.severity);
                    float maxClot = w.arterial ? (w.stump ? 0.8f : 0.6f) : 0.995f;
                    float speed = w.arterial ? 1f / (clotTime * 6f) : 1f / clotTime;
                    w.clot = Mathf.Min(maxClot, w.clot + (maxClot - w.clot) * speed * dt * (1f + body.Vitals.Shock * 0.5f));
                    if (!w.arterial && w.clot > 0.99f && w.age > 20f) w.Active = false;
                }
                else
                {
                    // detached pieces drain under gravity, quickly slowing
                    w.clot = Mathf.Min(0.995f, w.clot + dt / 25f);
                    if (w.clot > 0.99f) w.Active = false;
                }
            }
            ExternalRate = total;
            body.Vitals.ExternalBleedRate = total;
            body.Vitals.LoseBlood(total * dt);
        }

        public void VisualTick(float dt)
        {
            if (Wounds.Count == 0 || Sandbox.Fx == null) return;
            float amount = Sandbox.BloodAmount;
            if (amount <= 0.001f) return;
            var bs = Sandbox.Boot.blood;
            var dm = Sandbox.Boot.dismember;
            float pressure = body.Vitals.Pressure;
            int beat = body.Vitals.BeatCount;
            bool full = Sandbox.Gore == GoreLevel.Full;
            for (int i = 0; i < Wounds.Count; i++)
            {
                var w = Wounds[i];
                if (!w.Active || w.part == null) continue;
                float p = w.part.attached ? pressure : 0.2f * (1f - w.clot);
                float rate = w.bleedRate * (1f - w.clot) * p;
                if (rate < 1e-5f) continue;
                Vector3 pos = w.WorldPosition;
                Vector3 n = w.WorldNormal;
                Vector3 vel = w.part.rb != null ? w.part.rb.GetPointVelocity(pos) : Vector3.zero;
                // drips, pulsing with the heartbeat
                float ph = body.Vitals.HeartPhase;
                float pulse = body.Vitals.HeartRate > 5f ? 0.55f + 1.6f * Mathf.Exp(-Mathf.Pow((ph - 0.08f) * 8f, 2f)) : 0.5f;
                w.dripAccum += rate * 900f * amount * dt * pulse;
                int drops = 0;
                while (w.dripAccum >= 1f && drops < 6)
                {
                    w.dripAccum -= 1f;
                    drops++;
                    Vector3 v = vel * 0.8f + n * rng.Range(0.1f, 0.5f) + rng.OnUnitSphere() * 0.15f;
                    Sandbox.Fx.EmitBloodDrop(pos + n * 0.008f, v, rng.Range(0.012f, 0.028f) * Mathf.Sqrt(amount), bs.venous);
                }
                if (w.dripAccum > 6f) w.dripAccum = 6f;
                // arterial spurts in time with the heartbeat
                if (w.arterial && w.part.attached && beat != w.lastBeat)
                {
                    w.lastBeat = beat;
                    if (full || Sandbox.Gore == GoreLevel.Reduced)
                    {
                        float spurt = dm.stumpSpurt * (full ? 1f : 0.35f);
                        float speed = (1.2f + 3.2f * pressure) * spurt * (1f - w.clot * 0.7f);
                        int count = Mathf.RoundToInt((10f + 14f * pressure) * spurt);
                        Vector3 dir = (n + Vector3.up * 0.25f).normalized;
                        Sandbox.Fx.EmitBloodSpray(pos + n * 0.01f, dir, speed, 9f, count, 0.022f, bs.arterial, vel);
                        if (full) Sandbox.Fx.EmitMist(pos, vel + dir * speed * 0.3f, 2, 0.07f, new Color(bs.arterial.r, bs.arterial.g, bs.arterial.b, 0.5f));
                    }
                }
                // puddles under slow, low wounds
                w.puddleTimer -= dt;
                if (w.puddleTimer <= 0f)
                {
                    w.puddleTimer = 0.35f;
                    if (vel.sqrMagnitude < 0.6f * 0.6f && Sandbox.Blood != null) Sandbox.Blood.FlowFrom(pos, rate * 0.35f, body);
                }
            }
        }

        public Wound WorstClutchable()
        {
            Wound best = null;
            float bestRate = 0.0025f;
            float pressure = body.Vitals.Pressure;
            for (int i = 0; i < Wounds.Count; i++)
            {
                var w = Wounds[i];
                if (!w.Active || w.part == null || !w.part.attached) continue;
                if (w.part.id == PartId.HandL || w.part.id == PartId.HandR || w.part.id == PartId.Head) continue;
                float r = w.CurrentRate(pressure) * (w.stump ? 0.6f : 1f);
                if (r > bestRate) { bestRate = r; best = w; }
            }
            return best;
        }

        public void HealPart(BodyPart p)
        {
            for (int i = Wounds.Count - 1; i >= 0; i--) if (Wounds[i].part == p) Wounds.RemoveAt(i);
        }

        public void HealAll() { Wounds.Clear(); }

        /// <summary>Wounds beyond a split (axis coordinate of the segment) move to the new distal segment.</summary>
        public void MoveWounds(BodyPart from, BodyPart to, float split)
        {
            for (int i = 0; i < Wounds.Count; i++)
            {
                var w = Wounds[i];
                if (w.part != from) continue;
                if (Vector3.Dot(w.localPos, from.segAxis) > split) w.part = to;
            }
        }

        public void StopLoops() { }

        public int ActiveCount { get { int n = 0; for (int i = 0; i < Wounds.Count; i++) if (Wounds[i].Active) n++; return n; } }
    }
    #endregion

    #region OrganSystem
    public class Organ : IDamageable
    {
        public OrganType type;
        public RagdollBody body;
        public BodyPart host;
        public GameObject go;
        public Transform t;
        public MeshRenderer mr;
        public Material mat;
        public Color baseColor;
        public Vector3 restLocalPos;
        public Quaternion restLocalRot;
        public Vector3 restScale;
        public float health = 100f, radius, mass;
        public Rigidbody rb;
        public Collider col;
        public Joint tether;
        public bool spilled, detached, destroyed;
        public int chain = -1;

        public string DisplayName { get { return type.ToString(); } }

        public void ApplyDamage(DamageInfo info)
        {
            if (body != null) body.Organs.DamageOrgan(this, info.amount * 1.5f);
        }
    }

    public class OrganSystem
    {
        readonly RagdollBody body;
        public readonly List<Organ> All = new List<Organ>(20);
        public Organ Brain, Heart, LungL, LungR, Liver, Stomach, KidneyL, KidneyR;
        Rng rng;

        public OrganSystem(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 17 + 1); }

        public bool BrainIntact { get { return Brain != null && !Brain.destroyed && Brain.health > 0f && !Brain.detached; } }
        public float BrainHealth01 { get { return Brain != null ? Mathf.Clamp01(Brain.health / 100f) : 0f; } }
        public float HeartHealth01 { get { return Heart == null || Heart.destroyed || Heart.detached ? 0f : Mathf.Clamp01(Heart.health / 100f); } }

        public float LungEfficiency
        {
            get
            {
                float l = Lung(LungL) + Lung(LungR);
                return Mathf.Clamp01(l * 0.5f) * (body.Has(PartId.Chest) ? 1f : 0f);
            }
        }

        static float Lung(Organ o) { return o == null || o.destroyed || o.detached ? 0f : Mathf.Clamp01(o.health / 100f); }

        public float InternalBleedRate
        {
            get
            {
                float r = 0f;
                for (int i = 0; i < All.Count; i++)
                {
                    var o = All[i];
                    float dmg = 1f - Mathf.Clamp01(o.health / 100f);
                    if (dmg <= 0f) continue;
                    float k;
                    switch (o.type)
                    {
                        case OrganType.Liver: k = 0.014f; break;
                        case OrganType.Heart: k = 0.02f; break;
                        case OrganType.KidneyL: case OrganType.KidneyR: k = 0.005f; break;
                        case OrganType.Stomach: k = 0.004f; break;
                        case OrganType.Intestine: k = 0.0008f; break;
                        case OrganType.LungL: case OrganType.LungR: k = 0.004f; break;
                        default: k = 0.002f; break;
                    }
                    r += dmg * k;
                }
                return r;
            }
        }

        public void Build()
        {
            for (int i = 0; i < BodyMeshLibrary.Organs.Count; i++)
            {
                var d = BodyMeshLibrary.Organs[i];
                var o = Create(d.type, body.Part(d.host), BodyMeshLibrary.OrganMeshes[d.type], d.center - PartInfo.Origin[(int)d.host],
                    Quaternion.Euler(0f, 0f, d.rollDeg), d.color, d.mass, Mathf.Max(d.radii.x, Mathf.Max(d.radii.y, d.radii.z)));
                switch (d.type)
                {
                    case OrganType.Brain: Brain = o; break;
                    case OrganType.Heart: Heart = o; break;
                    case OrganType.LungL: LungL = o; break;
                    case OrganType.LungR: LungR = o; break;
                    case OrganType.Liver: Liver = o; break;
                    case OrganType.Stomach: Stomach = o; break;
                    case OrganType.KidneyL: KidneyL = o; break;
                    case OrganType.KidneyR: KidneyR = o; break;
                }
            }
            var path = BodyMeshLibrary.IntestinePath;
            for (int i = 0; i < BodyMeshLibrary.IntestineSegments; i++)
            {
                Vector3 a = path[i], b = path[i + 1];
                Vector3 c = (a + b) * 0.5f;
                Quaternion q = Quaternion.LookRotation(MathUtil.SafeNormal(b - a, Vector3.forward), Vector3.up);
                var o = Create(OrganType.Intestine, body.Part(PartId.Abdomen), BodyMeshLibrary.IntestineSegment, c - PartInfo.Origin[(int)PartId.Abdomen], q,
                    new Color(0.86f, 0.55f, 0.52f), 0.12f, 0.03f);
                o.chain = i;
            }
        }

        Organ Create(OrganType type, BodyPart host, Mesh mesh, Vector3 localRef, Quaternion rot, Color color, float mass, float radius)
        {
            var o = new Organ();
            o.type = type; o.body = body; o.host = host; o.mass = mass; o.radius = radius;
            o.go = new GameObject(type.ToString());
            o.go.layer = Layers.Gibs;
            o.t = o.go.transform;
            o.t.SetParent(host.inner != null ? host.inner : host.transform, false);
            o.t.localPosition = localRef;
            o.t.localRotation = rot;
            o.restLocalPos = localRef;
            o.restLocalRot = rot;
            o.restScale = Vector3.one;
            o.go.AddComponent<MeshFilter>().sharedMesh = mesh;
            o.mr = o.go.AddComponent<MeshRenderer>();
            o.mat = MaterialFactory.Lit(type + " mat", color, TextureFactory.Flesh, 0.8f, 0f);
            o.baseColor = color;
            o.mr.sharedMaterial = o.mat;
            o.mr.enabled = false;
            o.mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            All.Add(o);
            return o;
        }

        public void SetXRay(bool on)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.mr == null) continue;
                o.mr.enabled = on || o.spilled;
            }
        }

        /// <summary>Damages organs inside 'part' near the segment a-b (bullets) or a point (a == b).</summary>
        public void DamageAlong(BodyPart part, Vector3 a, Vector3 b, float radius, float amount)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.destroyed || o.host != part || o.t == null) continue;
                float d = MathUtil.DistancePointSegment(o.t.position, a, b);
                float reach = o.radius * body.Scale + radius;
                if (d > reach) continue;
                float f = 1f - d / reach;
                DamageOrgan(o, amount * (0.4f + 0.6f * f));
            }
        }

        public void DamageOrgan(Organ o, float amount)
        {
            if (o == null || o.destroyed || amount <= 0f) return;
            o.health -= amount;
            float dmg = 1f - Mathf.Clamp01(o.health / 100f);
            if (o.mat != null) o.mat.color = Color.Lerp(o.baseColor, new Color(0.22f, 0.02f, 0.06f), dmg * 0.8f);
            if (o.health <= 0f)
            {
                o.destroyed = true;
                o.health = 0f;
                SandboxEvents.RaiseOrganDestroyed(body, o.type);
                string name = o.type == OrganType.Intestine ? "Intestines" : o.type.ToString();
                if (o.type != OrganType.Intestine && !body.Vitals.Dead) SandboxEvents.RaiseToast(name + " destroyed", new Color(1f, 0.45f, 0.4f));
                if (o.type == OrganType.Heart) body.Vitals.CardiacArrest = true;
                if (o.type == OrganType.Brain) body.Vitals.Kill("brain destroyed");
            }
        }

        /// <summary>Ruptures a torso part: its organs spill out and dangle on soft tethers.</summary>
        public void Rupture(BodyPart part, Vector3 worldPoint, Vector3 outward)
        {
            if (Sandbox.Gore != GoreLevel.Full || part == null) return;
            if (part.ruptured) return;
            part.ruptured = true;
            Organ prevIntestine = null;
            int spilled = 0;
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.host != part || o.spilled || o.type == OrganType.Brain) continue;
                if (o.type == OrganType.LungL || o.type == OrganType.LungR || o.type == OrganType.Heart)
                {
                    // the ribcage keeps most chest organs inside unless the hole is near them
                    if ((o.t.position - worldPoint).magnitude > 0.14f * body.Scale) continue;
                }
                Spill(o, outward, o.type == OrganType.Intestine ? prevIntestine : null);
                if (o.type == OrganType.Intestine) prevIntestine = o;
                spilled++;
            }
            if (spilled > 0)
            {
                SandboxEvents.RaiseToast("Organs exposed", new Color(1f, 0.5f, 0.45f));
                if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Squelch0, worldPoint, 0.9f, 0.8f);
                body.Wounds.Add(part, worldPoint, outward, 1f, 0.08f * body.Scale, DamageType.Cut, false, false);
            }
        }

        void Spill(Organ o, Vector3 outward, Organ chainPrev)
        {
            o.spilled = true;
            o.t.SetParent(body.transform, true);
            o.mr.enabled = true;
            o.rb = o.go.AddComponent<Rigidbody>();
            o.rb.mass = o.mass * body.MassScale;
            Compat.SetDamping(o.rb, 0.4f, 0.6f);
            o.rb.interpolation = RigidbodyInterpolation.Interpolate;
            o.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            o.rb.maxDepenetrationVelocity = 2f;
            if (o.type == OrganType.Intestine)
            {
                var cc = o.go.AddComponent<CapsuleCollider>();
                cc.direction = 2; cc.radius = 0.016f * body.Scale; cc.height = 0.1f * body.Scale;
                o.col = cc;
            }
            else
            {
                var sc = o.go.AddComponent<SphereCollider>();
                sc.radius = o.radius * 0.7f;
                o.col = sc;
            }
            o.col.sharedMaterial = PhysMats.Organ;
            for (int i = 0; i < body.AllPieces.Count; i++) if (body.AllPieces[i] != null && body.AllPieces[i].col != null) Physics.IgnoreCollision(o.col, body.AllPieces[i].col, true);
            Registry.Organs[o.col] = o;
            Registry.RegisterDamageable(o.col, o);
            var sj = o.go.AddComponent<SpringJoint>();
            Rigidbody anchorBody = chainPrev != null && chainPrev.rb != null ? chainPrev.rb : o.host.rb;
            sj.connectedBody = anchorBody;
            sj.autoConfigureConnectedAnchor = false;
            sj.anchor = Vector3.zero;
            sj.connectedAnchor = anchorBody.transform.InverseTransformPoint(o.t.position);
            sj.spring = (o.type == OrganType.Intestine ? 260f : 180f) * body.MassScale;
            sj.damper = 4f;
            sj.minDistance = 0f;
            sj.maxDistance = (o.type == OrganType.Intestine ? 0.05f : rng.Range(0.05f, 0.18f)) * body.Scale;
            sj.breakForce = (o.type == OrganType.Intestine ? 220f : 300f) * body.MassScale;
            sj.enableCollision = false;
            o.tether = sj;
            Vector3 v = o.host.rb.GetPointVelocity(o.t.position) + outward * rng.Range(0.8f, 1.8f) + rng.OnUnitSphere() * 0.3f;
            Compat.SetVelocity(o.rb, v);
        }

        /// <summary>Moves organs whose rest position lies beyond a split to the new segment (frames are identical then).</summary>
        public void Reassign(BodyPart from, BodyPart to, float split)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.host != from || o.spilled) continue;
                if (Vector3.Dot(o.restLocalPos * body.Scale, from.segAxis) <= split) continue;
                o.host = to;
                o.t.SetParent(to.inner != null ? to.inner : to.transform, false);
                o.t.localPosition = o.restLocalPos;
                o.t.localRotation = o.restLocalRot;
            }
        }

        public void FixedTick(float dt)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (!o.spilled || o.detached) continue;
                if (o.tether == null)
                {
                    o.detached = true;
                    if (o.type == OrganType.Heart) { body.Vitals.CardiacArrest = true; DamageOrgan(o, 1000f); }
                    if (o.type == OrganType.LungL || o.type == OrganType.LungR) DamageOrgan(o, 1000f);
                    if (o.rb != null && Sandbox.Fx != null)
                    {
                        Sandbox.Fx.EmitBloodSpray(o.rb.position, Vector3.up, 1.5f, 50f, 8, 0.02f, Sandbox.Boot.blood.venous, Compat.GetVelocity(o.rb));
                        if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Squelch1, o.rb.position, 0.7f, rng.Range(0.9f, 1.2f));
                    }
                }
            }
        }

        public void VisualTick(float dt)
        {
            var v = body.Vitals;
            if (Heart != null && !Heart.destroyed && Heart.t != null && (Heart.mr.enabled))
            {
                float ph = v.HeartPhase;
                float pulse = Mathf.Exp(-Mathf.Pow((ph - 0.05f) * 9f, 2f)) + 0.6f * Mathf.Exp(-Mathf.Pow((ph - 0.3f) * 10f, 2f));
                float k = v.HeartRate > 1f ? pulse * 0.09f : 0f;
                Heart.t.localScale = Heart.restScale * (1f + k);
            }
            float breath = Mathf.Sin(body.Brain != null ? Time.time * v.BreathRate / 60f * Mathf.PI * 2f : 0f) * 0.5f + 0.5f;
            AnimateLung(LungL, breath, v.BreathDepth);
            AnimateLung(LungR, breath, v.BreathDepth);
        }

        static void AnimateLung(Organ o, float breath, float depth)
        {
            if (o == null || o.destroyed || o.t == null || o.mr == null || !o.mr.enabled || o.spilled) return;
            float k = breath * 0.08f * depth * Mathf.Clamp01(o.health / 100f);
            o.t.localScale = new Vector3(1f + k * 0.7f, 1f + k, 1f + k);
        }

        public void HealHost(BodyPart part)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.host != part || o.spilled) continue;
                o.health = 100f;
                o.destroyed = false;
                if (o.mat != null) o.mat.color = o.baseColor;
            }
            if (part.id == PartId.Chest && HeartHealth01 > 0f) body.Vitals.CardiacArrest = false;
        }

        public void DestroyAll()
        {
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o.col != null) Registry.Unregister(o.col);
                if (o.mat != null) UnityEngine.Object.Destroy(o.mat);
                if (o.go != null && o.spilled) UnityEngine.Object.Destroy(o.go);
            }
            All.Clear();
        }
    }
    #endregion

    #region DamageSystem
    public class DamageSystem
    {
        readonly RagdollBody body;
        readonly float[] lastSound = new float[PartInfo.Count];
        readonly bool[] spinal = new bool[PartInfo.Count];
        readonly float[] lastDent = new float[PartInfo.Count];
        float breakRefresh;
        Rng rng;

        public DamageSystem(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 3 + 11); }

        public bool SpinalInjury(PartId id) { return spinal[(int)id]; }

        public void FixedTick(float dt)
        {
            breakRefresh -= dt;
            if (breakRefresh <= 0f) { breakRefresh = 0.5f; body.RefreshBreakForces(); }
        }

        public void OnCollision(BodyPart part, Collision c)
        {
            BodyPart other = Registry.PartOf(c.collider);
            if (other != null && other.body == body) return;
            float impulse = c.impulse.magnitude;
            if (!MathUtil.IsFinite(impulse) || c.contactCount == 0) return;
            ContactPoint cp = c.GetContact(0);
            Vector3 n = cp.normal;
            Vector3 rel = c.relativeVelocity;
            // vehicles: Euphoria-style hit reaction
            Vehicle veh;
            if (c.rigidbody != null && Registry.Vehicles.TryGetValue(c.rigidbody, out veh) && veh != null)
            {
                float speed = rel.magnitude;
                if (speed > 3f) body.Brain.OnVehicleHit(speed, rel.normalized);
            }
            // trip detection
            if (PartInfo.IsFoot(part.id) && Mathf.Abs(n.y) < 0.5f)
            {
                int side = part.id == PartId.FootL ? 0 : 1;
                body.Locomotion.OnFootBlocked(side, MathUtil.Flat(rel).magnitude);
            }
            int idx = (int)part.id;
            if (impulse > 3f * body.MassScale && Time.time - lastSound[idx] > 0.09f && Sandbox.Audio != null)
            {
                lastSound[idx] = Time.time;
                float vol = Mathf.Clamp01(impulse / (70f * body.MassScale));
                float pitch = Mathf.Lerp(1.25f, 0.8f, Mathf.Clamp01(part.rb.mass / 12f));
                Sandbox.Audio.PlayRandomThud(cp.point, vol, pitch);
            }
            var ds = Sandbox.Boot.damage;
            float thr = ds.impactThreshold * body.MassScale;
            if (impulse <= thr) return;
            bool fall = c.rigidbody == null && rel.y > 6f;
            float dmg = (impulse - thr) * ds.impactDamageScale / Mathf.Sqrt(body.MassScale);
            var d = DamageInfo.Make(fall ? DamageType.Fall : DamageType.Blunt, dmg, cp.point, -n);
            d.normal = n;
            d.impulse = impulse;
            d.radius = Mathf.Clamp(0.03f + impulse * 0.0005f, 0.03f, 0.1f);
            Apply(part, d);
            body.Brain.OnImpact(part, impulse, -n);
        }

        public void Apply(BodyPart part, DamageInfo d)
        {
            if (part == null || body == null || body.Puppet) return;
            var ds = Sandbox.Boot.damage;
            var gore = Sandbox.Gore;
            float amount = d.amount;
            if (d.type == DamageType.Blast) amount *= ds.explosionDamageScale;
            if (amount <= 0f || !MathUtil.IsFinite(amount)) return;
            bool living = part.attached && !part.cutOff;
            part.health = Mathf.Max(0f, part.health - amount);
            switch (d.type)
            {
                case DamageType.Blunt: case DamageType.Fall: part.blunt += amount; break;
                case DamageType.Cut: part.cutDamage += amount; break;
                case DamageType.Pierce: part.pierce += amount; break;
                case DamageType.Blast: case DamageType.Burn: part.blast += amount; break;
                case DamageType.Crush: part.crush += amount; break;
                case DamageType.Shock: part.shock += amount; break;
            }
            var skin = body.Skin;
            var vit = body.Vitals;
            if (living)
            {
                vit.TotalTrauma += amount;
                vit.AddPain(amount * 0.012f * (PartInfo.IsHand(part.id) || PartInfo.IsFoot(part.id) ? 1.3f : 1f));
                vit.AddAdrenaline(0.05f + amount * 0.004f);
            }
            Vector3 inward = MathUtil.SafeNormal(d.direction, -d.normal);
            Vector3 outward = -inward;
            float s = body.Scale;
            float deform = ds.deformationScale;
            bool canDent = deform > 0f && Time.time - lastDent[(int)part.id] > 0.12f;
            Color bloodC = Sandbox.Boot.blood.arterial;
            switch (d.type)
            {
                case DamageType.Blunt:
                case DamageType.Fall:
                case DamageType.Crush:
                    Paint(part, d.point, StampKind.Bruise, Mathf.Clamp(0.025f + amount * 0.0018f, 0.025f, 0.09f) * s, Mathf.Clamp01(amount / 25f), Color.white, 0f, Vector3.zero);
                    if (skin != null && canDent && amount > 4f)
                    {
                        lastDent[(int)part.id] = Time.time;
                        skin.Dent(part, d.point, inward, Mathf.Clamp(0.05f + amount * 0.001f, 0.05f, 0.12f) * s, Mathf.Min(ds.maxDentDepth, amount * 0.0006f * deform) * s);
                    }
                    if (d.type == DamageType.Crush && part.crush > 40f && skin != null && canDent)
                        skin.Squash(part, d.point, d.normal.sqrMagnitude > 0f ? d.normal : Vector3.up, 0.12f * s, Mathf.Lerp(0.1f, 0.45f, Mathf.Clamp01((part.crush - 40f) / 120f)) * deform);
                    if (amount > 25f && gore != GoreLevel.Off && rng.Value < (amount - 25f) / 60f)
                    {
                        body.Wounds.Add(part, d.point, outward, Mathf.Clamp01(amount / 80f), 0.01f * s, DamageType.Cut);
                        Paint(part, d.point, StampKind.Cut, 0.025f * s, 1f, Color.white, 0.3f, rng.OnUnitSphere());
                    }
                    if (d.impulse >= PartInfo.FractureImpulse[(int)part.id] * ds.fractureThresholdScale * body.MassScale) Fracture(part, d.point, d.impulse);
                    if (part.id == PartId.Head && living)
                    {
                        float ko = (d.impulse - 22f * body.MassScale) * 0.12f * ds.knockoutScale;
                        if (ko > 0f) { vit.Knockout(ko); vit.HeadTrauma = Mathf.Clamp01(vit.HeadTrauma + amount / 60f); }
                        body.Organs.DamageAlong(part, d.point, d.point, 0.05f * s, amount * 0.35f);
                    }
                    else if (amount > 20f) body.Organs.DamageAlong(part, d.point, d.point + inward * 0.06f * s, 0.05f * s, amount * 0.25f);
                    if (d.type == DamageType.Crush && gore == GoreLevel.Full && part.crush > 90f && PartInfo.IsTorso(part.id)) body.Organs.Rupture(part, d.point, d.normal);
                    break;

                case DamageType.Cut:
                    {
                        float len = Mathf.Max(d.radius, 0.03f * s);
                        Vector3 along = d.along.sqrMagnitude > 0.5f ? d.along : Vector3.Cross(inward, part.transform.TransformDirection(part.segAxis));
                        if (along.sqrMagnitude < 1e-4f) along = rng.OnUnitSphere();
                        along.Normalize();
                        if (gore != GoreLevel.Off)
                        {
                            Paint(part, d.point, StampKind.Tear, len * 0.8f, 1f, Color.white, 0f, Vector3.zero);
                            Paint(part, d.point, StampKind.Cut, len, 1f, Color.white, Mathf.Clamp01(d.depth / (0.03f * s)), along);
                            body.Wounds.Add(part, d.point, outward, Mathf.Clamp01(amount / 40f), d.depth, DamageType.Cut);
                            if (skin != null && canDent && d.depth > 0.012f * s) { lastDent[(int)part.id] = Time.time; skin.Dent(part, d.point, inward, len * 0.6f, Mathf.Min(0.006f * s, d.depth * 0.25f)); }
                        }
                        else Paint(part, d.point, StampKind.Bruise, 0.03f * s, 0.6f, Color.white, 0f, Vector3.zero);
                        body.Organs.DamageAlong(part, d.point, d.point + inward * d.depth, 0.03f * s, amount * 1.2f);
                        if (PartInfo.IsTorso(part.id) && part.cutDamage > Sandbox.Boot.dismember.ruptureThreshold && d.depth > 0.04f * s) body.Organs.Rupture(part, d.point, outward);
                        if (part.id == PartId.Neck && d.depth > 0.03f * s && living) body.Wounds.Add(part, d.point, outward, 1f, d.depth, DamageType.Cut, true);
                        break;
                    }

                case DamageType.Pierce:
                    {
                        // real holes: entry, and an exit hole when the bullet passes through
                        float r = Mathf.Max(0.006f * s, d.radius);
                        if (gore != GoreLevel.Off)
                        {
                            Paint(part, d.point, StampKind.Tear, r * 2.2f, 1f, Color.white, 0f, Vector3.zero);
                            Paint(part, d.point, StampKind.Hole, r * 1.3f, 1f, Color.white, 0f, Vector3.zero);
                            if (skin != null) skin.Hole(part, d.point, inward, r, Mathf.Max(d.depth, 0.04f * s) * 0.6f, d.crater);
                            body.Wounds.Add(part, d.point, outward, Mathf.Clamp01(amount / 45f), Mathf.Max(d.depth, 0.03f * s), DamageType.Pierce);
                            if (d.hasExit)
                            {
                                Paint(part, d.exitPoint, StampKind.Tear, r * 3f, 1f, Color.white, 0f, Vector3.zero);
                                Paint(part, d.exitPoint, StampKind.Meat, r * 1.8f, 1f, bloodC, 0.3f, Vector3.zero);
                                if (skin != null) skin.Hole(part, d.exitPoint, -inward, r * 1.5f, Mathf.Max(d.depth, 0.04f * s) * 0.45f, false);
                                body.Wounds.Add(part, d.exitPoint, inward, Mathf.Clamp01(amount / 35f), 0.03f * s, DamageType.Pierce);
                                if (Sandbox.Fx != null) Sandbox.Fx.EmitBloodSpray(d.exitPoint, inward, 3.5f, 25f, 10, 0.02f, bloodC, part.rb != null ? Compat.GetVelocity(part.rb) : Vector3.zero);
                            }
                        }
                        else if (skin != null && canDent) { lastDent[(int)part.id] = Time.time; skin.Dent(part, d.point, inward, 0.02f * s, 0.004f * s); }
                        Vector3 end = d.hasExit ? d.exitPoint : d.point + inward * Mathf.Max(d.depth, 0.08f * s);
                        body.Organs.DamageAlong(part, d.point, end, 0.025f * s, amount * 2.2f);
                        if (amount > 20f && rng.Value < 0.2f) Fracture(part, d.point, PartInfo.FractureImpulse[(int)part.id] * 2f);
                        if (PartInfo.IsTorso(part.id) && part.pierce > 140f && gore == GoreLevel.Full) body.Organs.Rupture(part, d.point, outward);
                        break;
                    }

                case DamageType.Blast:
                    {
                        Paint(part, d.point, StampKind.Burn, Mathf.Clamp(0.04f + amount * 0.002f, 0.04f, 0.12f) * s, Mathf.Clamp01(amount / 40f), Color.white, 0f, Vector3.zero);
                        Paint(part, d.point, StampKind.Bruise, 0.06f * s, Mathf.Clamp01(amount / 30f), Color.white, 0f, Vector3.zero);
                        if (skin != null && canDent) { lastDent[(int)part.id] = Time.time; skin.Dent(part, d.point, inward, 0.09f * s, Mathf.Min(ds.maxDentDepth, amount * 0.0004f * deform) * s); }
                        if (amount > 18f && gore != GoreLevel.Off)
                        {
                            Paint(part, d.point, StampKind.Tear, Mathf.Clamp(amount * 0.001f, 0.02f, 0.08f) * s, 1f, Color.white, 0f, Vector3.zero);
                            Paint(part, d.point, StampKind.Meat, Mathf.Clamp(amount * 0.0007f, 0.015f, 0.06f) * s, 1f, bloodC, amount > 60f ? 0.8f : 0.3f, Vector3.zero);
                            body.Wounds.Add(part, d.point, outward, Mathf.Clamp01(amount / 70f), 0.02f * s, DamageType.Blast);
                        }
                        body.Organs.DamageAlong(part, d.point, d.point + inward * 0.1f * s, 0.06f * s, amount * 0.5f);
                        if (d.impulse >= PartInfo.FractureImpulse[(int)part.id] * ds.fractureThresholdScale * body.MassScale * 0.7f) Fracture(part, d.point, d.impulse);
                        if (gore == GoreLevel.Full && PartInfo.IsTorso(part.id) && part.blast > Sandbox.Boot.dismember.ruptureThreshold * 1.2f) body.Organs.Rupture(part, d.point, outward);
                        if (gore == GoreLevel.Full && amount > 70f && rng.Value < (amount - 70f) / 90f) body.Dismember.BlastSever(part);
                        if (part.id == PartId.Head && living) { vit.Knockout(amount * 0.1f * ds.knockoutScale); body.Organs.DamageAlong(part, d.point, d.point, 0.1f * s, amount * 0.4f); }
                        break;
                    }

                case DamageType.Burn:
                    // laser / fire: scorch stamp (accumulates), pain, a little charring of the clay
                    Paint(part, d.point, StampKind.Burn, 0.022f * s, Mathf.Clamp01(0.25f + amount / 6f), Color.white, 0f, Vector3.zero);
                    if (living) vit.AddPain(amount * 0.01f);
                    if (living && body.Brain != null && rng.Value < 0.05f) body.Brain.OnPain(amount * 8f);
                    break;

                case DamageType.Shock:
                    Paint(part, d.point, StampKind.Shock, 0.03f * s, 0.6f, Color.white, 0f, Vector3.zero);
                    body.Muscles.Spasm(1.1f);
                    if (living)
                    {
                        vit.ShockTime += Time.fixedDeltaTime * 3f;
                        if (vit.ShockTime > 3f && rng.Value < 0.02f) { vit.CardiacArrest = true; SandboxEvents.RaiseToast("Cardiac arrest!", new Color(1f, 0.4f, 0.4f)); }
                    }
                    break;
            }
            if (part.id == PartId.Head && part.health <= 0f && living && body.Organs.Brain != null) body.Organs.DamageOrgan(body.Organs.Brain, 1000f);
            if (part.id == PartId.Chest && part.health <= 0f && living && body.Organs.Heart != null && d.type != DamageType.Shock) body.Organs.DamageOrgan(body.Organs.Heart, 25f);
            body.ApplyBreakForce(part);
            for (int i = 0; i < part.children.Count; i++) body.ApplyBreakForce(part.children[i]);
            body.NotifyDamaged(part, d);
        }

        public void Paint(BodyPart part, Vector3 world, StampKind kind, float radius, float intensity, Color color, float depth, Vector3 along)
        {
            if (part == null || body.Skin == null) return;
            body.Skin.Paint(part, world, kind, radius, intensity, color, depth, along);
        }

        /// <summary>Paints blood that landed on the body (from the droplet simulation).</summary>
        public void PaintBlood(BodyPart part, Vector3 world, float radius, Color c)
        {
            if (!Sandbox.Boot.blood.paintOnBodies) return;
            Paint(part, world, StampKind.Blood, radius, 0.9f, c, 0f, Vector3.zero);
            part.bloodiness = Mathf.Min(1f, part.bloodiness + 0.15f);
        }

        /// <summary>Breaks a bone. Long bones (and the spine) split into two physical segments joined by a loose joint:
        /// the skinned mesh visibly kinks there. Several fractures per limb are possible.</summary>
        public void Fracture(BodyPart part, Vector3 point, float impulse)
        {
            if (part == null || body.Puppet) return;
            int id = (int)part.id;
            if (body.FractureCount[id] >= 3) return;
            bool splittable = PartInfo.IsLimbSegment(part.id) && part.segMax - part.segMin > 0.12f * body.Scale;
            if (splittable)
            {
                float split = part.AxisCoord(point);
                body.Skin.Fracture(part, split);
            }
            else if (!part.fractured)
            {
                // ribs, skull, pelvis, hands, feet: a crack + looser joints
                if (part.jr != null && part.jr.joint != null) part.jr.ApplyLimits(18f);
                body.FractureCount[id]++;
            }
            else return;
            part.fractured = true;
            Paint(part, point, StampKind.Crack, 0.035f * body.Scale, 1f, Color.white, 0f, part.transform.TransformDirection(part.segAxis));
            Paint(part, point, StampKind.Bruise, 0.05f * body.Scale, 1f, Color.white, 0f, Vector3.zero);
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.BoneCrack, point, 1f, rng.Range(0.9f, 1.15f));
            body.Vitals.AddPain(0.35f);
            body.Vitals.AddAdrenaline(0.3f);
            body.Brain.OnPain(60f);
            if (part.id == PartId.Neck || part.id == PartId.Chest || part.id == PartId.Abdomen)
            {
                float k = impulse / Mathf.Max(1f, PartInfo.FractureImpulse[id] * body.MassScale);
                if (part.id == PartId.Neck || k > 1.4f)
                {
                    spinal[id] = true;
                    SandboxEvents.RaiseToast(part.id == PartId.Neck ? "Broken neck - paralysed" : "Spine broken - legs paralysed", new Color(1f, 0.5f, 0.3f));
                }
                if (part.id == PartId.Chest && rng.Value < 0.5f) body.Organs.DamageOrgan(rng.Value < 0.5f ? body.Organs.LungL : body.Organs.LungR, 45f);
            }
            if (!splittable) { body.RebuildTopology(); body.NotifyFracture(part.id); }
            if (part.attached && !body.Vitals.Dead) SandboxEvents.RaiseToast(BoneName(part.id) + " fractured", new Color(1f, 0.75f, 0.45f));
        }

        /// <summary>Called when the fracture mesh job finished and the bone was split.</summary>
        public void OnFractureApplied(BodyPart s, BodyPart n, float split)
        {
            body.FractureCount[(int)s.id]++;
            s.fractured = true;
            n.fractured = true;
            body.Wounds.MoveWounds(s, n, split);
            body.Organs.Reassign(s, n, split);
            if (s.jr != null && s.jr.joint != null && s.jr.kind == JointKind.Anatomical) s.jr.ApplyLimits(8f);
            body.RebuildTopology();
            body.NotifyFracture(s.id);
        }

        public static string BoneName(PartId id)
        {
            switch (id)
            {
                case PartId.Head: return "Skull";
                case PartId.Neck: return "Neck";
                case PartId.Chest: return "Ribs";
                case PartId.Abdomen: return "Lumbar spine";
                case PartId.Pelvis: return "Pelvis";
                case PartId.UpperArmL: return "Left humerus";
                case PartId.UpperArmR: return "Right humerus";
                case PartId.ForearmL: return "Left forearm";
                case PartId.ForearmR: return "Right forearm";
                case PartId.HandL: return "Left hand";
                case PartId.HandR: return "Right hand";
                case PartId.ThighL: return "Left femur";
                case PartId.ThighR: return "Right femur";
                case PartId.ShinL: return "Left tibia";
                case PartId.ShinR: return "Right tibia";
                case PartId.FootL: return "Left foot";
                default: return "Right foot";
            }
        }

        /// <summary>Heal tool: restores a bone (health, wounds, set fractures rigid again, organs). Holes, cuts and
        /// missing parts persist; a full restore respawns the body.</summary>
        public void Heal(BodyPart part)
        {
            if (part == null) return;
            PartId id = part.id;
            for (int i = 0; i < body.AllPieces.Count; i++)
            {
                var p = body.AllPieces[i];
                if (p == null || p.id != id || !p.attached) continue;
                p.health = p.maxHealth;
                p.blunt = p.cutDamage = p.pierce = p.blast = p.crush = p.shock = 0f;
                p.fractured = false;
                if (p.jr != null && p.jr.joint != null)
                {
                    if (p.jr.kind == JointKind.Fracture)
                    {
                        // set the bone: the halves are locked together again
                        p.jr.joint.angularXMotion = ConfigurableJointMotion.Locked;
                        p.jr.joint.angularYMotion = ConfigurableJointMotion.Locked;
                        p.jr.joint.angularZMotion = ConfigurableJointMotion.Locked;
                    }
                    else p.jr.ApplyLimits(0f);
                }
                body.Wounds.HealPart(p);
                body.Organs.HealHost(p);
                p.bloodiness = 0f;
                body.ApplyBreakForce(p);
            }
            body.FractureCount[(int)id] = 0;
            if (id == PartId.Neck || id == PartId.Chest || id == PartId.Abdomen) spinal[(int)id] = false;
            body.RebuildTopology();
        }
    }
    #endregion

    #region DismemberSystem
    public class DismemberSystem
    {
        readonly RagdollBody body;
        Rng rng;

        public DismemberSystem(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 5 + 2); }

        readonly List<BodyPart> breakChecks = new List<BodyPart>(4);

        public void QueueBreakCheck(BodyPart part)
        {
            if (part != null && !breakChecks.Contains(part)) breakChecks.Add(part);
        }

        /// <summary>Severs segments whose joint was destroyed by the physics engine (break force exceeded).</summary>
        public void ProcessBreakChecks()
        {
            for (int i = breakChecks.Count - 1; i >= 0; i--)
            {
                var p = breakChecks[i];
                breakChecks.RemoveAt(i);
                if (p == null || p.jr == null || p.parentPiece == null) continue;
                if (p.jr.joint == null) Sever(p, true);
            }
        }

        /// <summary>Separates 'child' (and everything below it) from its parent at their joint; the mesh is split at the
        /// joint plane (or at the fracture for a broken bone).</summary>
        public void Sever(BodyPart child, bool fromBreak)
        {
            if (child == null || child.jr == null) return;
            BodyPart parent = child.parentPiece;
            bool fractureJoint = child.jr.kind == JointKind.Fracture;
            if (!fromBreak && child.jr.joint != null) UnityEngine.Object.Destroy(child.jr.joint);
            child.jr.joint = null;
            float s = body.Scale;
            Vector3 jointPos = child.transform.TransformPoint(fractureJoint ? child.segAxis * child.segMin : Vector3.zero);
            // mesh: separate at the joint plane (bind space)
            if (body.Skin != null && parent != null)
            {
                Vector3 bindPoint = fractureJoint ? PartInfo.Origin[(int)child.id] * s + child.segAxis * child.segMin : PartInfo.Origin[(int)child.id] * s;
                Vector3 bindNormal = fractureJoint ? child.segAxis : (PartInfo.IsFoot(child.id) ? Vector3.down : PartInfo.BoneAxis(child.id));
                body.Skin.SeverAt(child, parent, bindPoint, bindNormal);
            }
            if (parent != null) parent.children.Remove(child);
            child.parentPiece = null;
            child.cutOff = true;
            Vector3 outward = parent != null ? MathUtil.SafeNormal(child.WorldCenter - parent.WorldCenter, Vector3.up) : Vector3.up;
            bool wasAttached = child.attached;
            if (parent != null) body.Wounds.Add(parent, jointPos, outward, 1f, 0.05f * s, DamageType.Cut, true, true);
            body.Wounds.Add(child, jointPos, -outward, 0.8f, 0.05f * s, DamageType.Cut, false, true);
            if (Sandbox.Fx != null && Sandbox.BloodAmount > 0f)
            {
                Sandbox.Fx.EmitBloodSpray(jointPos, outward, 3f, 35f, 28, 0.025f, Sandbox.Boot.blood.arterial, Compat.GetVelocity(child.rb));
                Sandbox.Fx.EmitMist(jointPos, outward, 6, 0.12f, new Color(0.6f, 0.02f, 0.03f, 0.6f));
            }
            if (Sandbox.Audio != null)
            {
                Sandbox.Audio.Play(Sfx.BoneCrack, jointPos, 1f, rng.Range(0.85f, 1.05f));
                Sandbox.Audio.Play(Sfx.Squelch0, jointPos, 0.9f, rng.Range(0.9f, 1.1f));
            }
            if (child.id == PartId.Abdomen || child.id == PartId.Chest) body.Organs.Rupture(body.Part(PartId.Abdomen), jointPos, outward);
            body.RebuildTopology();
            body.NotifySevered(child.id, child);
            if (wasAttached && !body.Vitals.Dead)
            {
                body.Vitals.AddPain(0.6f);
                body.Vitals.AddAdrenaline(0.6f);
                body.Brain.OnPain(80f);
            }
            SandboxEvents.RaiseKill(body.Label, SeverName(child.id), new Color(1f, 0.35f, 0.3f));
        }

        public static string SeverName(PartId id)
        {
            switch (id)
            {
                case PartId.Head: return "head severed";
                case PartId.Neck: return "head and neck severed";
                case PartId.Chest: return "torso torn in half";
                case PartId.Abdomen: return "torn apart at the waist";
                case PartId.UpperArmL: return "left arm severed";
                case PartId.UpperArmR: return "right arm severed";
                case PartId.ForearmL: return "left forearm severed";
                case PartId.ForearmR: return "right forearm severed";
                case PartId.HandL: return "left hand severed";
                case PartId.HandR: return "right hand severed";
                case PartId.ThighL: return "left leg severed";
                case PartId.ThighR: return "right leg severed";
                case PartId.ShinL: return "left leg severed at the knee";
                case PartId.ShinR: return "right leg severed at the knee";
                case PartId.FootL: return "left foot severed";
                default: return "right foot severed";
            }
        }

        /// <summary>Explosive dismemberment: separate the part from its parent.</summary>
        public void BlastSever(BodyPart part)
        {
            if (part == null || Sandbox.Gore != GoreLevel.Full) return;
            if (part.id == PartId.Pelvis || part.id == PartId.Chest) return;
            if (part.Joint != null) Sever(part, false);
        }

        /// <summary>Cuts through a segment with a plane at any point and angle (blade path / laser sweep).
        /// The physics split and the mesh cut happen together when the (threaded) mesh job completes.</summary>
        public bool Slice(BodyPart part, Vector3 worldPoint, Vector3 planeNormal)
        {
            if (part == null || Sandbox.Gore == GoreLevel.Off || body.Skin == null || body.Puppet) return false;
            if (!body.Skin.Slice(part, worldPoint, planeNormal)) return false;
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Slice, worldPoint, 1f, rng.Range(0.9f, 1.1f));
            body.LastInteractionTime = Time.time;
            return true;
        }

        /// <summary>Mesh job finished: wounds on both cut faces, organs and wounds follow their side, topology.</summary>
        public void OnMeshCut(SliceJob job, bool sever, BodyPart proximal, BodyPart distal, Vector3 capP, Vector3 capD, Vector3 bindNormal)
        {
            if (sever || proximal == null || distal == null) return;
            float s = body.Scale;
            float split = distal.segMin;
            body.Organs.Reassign(proximal, distal, split);
            body.Wounds.MoveWounds(proximal, distal, split);
            Vector3 cP = capP.sqrMagnitude > 0f ? proximal.FromBind(capP) : proximal.transform.TransformPoint(proximal.segAxis * split);
            Vector3 cD = capD.sqrMagnitude > 0f ? distal.FromBind(capD) : cP;
            Vector3 axisW = proximal.transform.TransformDirection(bindNormal);
            bool arterial = PartInfo.IsLimbSegment(proximal.id) || proximal.id == PartId.Neck || PartInfo.IsTorso(proximal.id);
            body.Wounds.Add(proximal, cP, axisW, 1f, 0.05f * s, DamageType.Cut, arterial, true);
            body.Wounds.Add(distal, cD, -axisW, 0.8f, 0.05f * s, DamageType.Cut, false, true);
            Vector3 vel = Compat.GetVelocity(proximal.rb);
            if (Sandbox.Fx != null)
            {
                Sandbox.Fx.EmitBloodSpray(cP, axisW, 2.5f, 40f, 24, 0.022f, Sandbox.Boot.blood.arterial, vel);
                Sandbox.Fx.EmitBloodSpray(cD, -axisW, 2f, 40f, 14, 0.022f, Sandbox.Boot.blood.arterial, vel);
            }
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Squelch1, cP, 0.8f, rng.Range(0.9f, 1.1f));
            if (proximal.id == PartId.Abdomen || proximal.id == PartId.Chest || proximal.id == PartId.Pelvis)
            {
                body.Organs.Rupture(proximal, cP, axisW);
                body.Organs.Rupture(distal, cD, -axisW);
            }
            if (proximal.id == PartId.Head && body.Organs.Brain != null) body.Organs.DamageOrgan(body.Organs.Brain, 1000f);
            if (Sandbox.XRay) body.SetXRay(true);
            body.RebuildTopology();
            body.NotifySevered(proximal.id, distal);
            if (!body.Vitals.Dead) { body.Vitals.AddPain(0.6f); body.Vitals.AddAdrenaline(0.6f); body.Brain.OnPain(80f); }
            SandboxEvents.RaiseKill(body.Label, PartInfo.Short[(int)proximal.id] + " sliced", new Color(1f, 0.35f, 0.3f));
        }
    }
    #endregion
}
