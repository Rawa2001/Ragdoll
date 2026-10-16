// Ragdoll Sandbox - active ragdoll motor control.
//  * Pose / IKSolver: procedural target poses and analytic two-bone IK. IK never moves transforms: it only
//    produces joint target rotations that the muscles (slerp drives) must physically reach.
//  * MuscleController: per-joint strength (health, consciousness, oxygen, fatigue, death fade), reflexes, spasms.
//  * BalanceController: centre of mass, support polygon, capture point, upright PD and pelvis height regulation
//    applied as internal force/torque pairs between torso and stance feet (ground reaction), plus a small bounded
//    assist torque that scales with consciousness.
//  * LocomotionController: per-foot Stance -> Lift -> Swing -> Plant state machine with capture-point footholds,
//    Bezier swing arcs, toe-off push, slip/trip handling, walk/run/jump/crouch.
//  * MotorBrain: behaviour states (stand, fall, lie, get up, crawl, hop, headless, unconscious, dead), possession,
//    reactions (flinch, shield head, clutch wounds), gaze and breathing.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    #region Pose and IK
    public static class Pose
    {
        static readonly Quaternion[] restInv = new Quaternion[PartInfo.Count];
        static bool init;

        static void Init()
        {
            if (init) return;
            init = true;
            for (int i = 0; i < PartInfo.Count; i++)
            {
                PartId id = (PartId)i;
                restInv[i] = Quaternion.Inverse(Quaternion.LookRotation(PartInfo.BoneAxis(id), PartInfo.PoleAxis(id)));
            }
        }

        /// <summary>Rotation (in whatever frame the directions are given) that points the part's bone along 'bone'
        /// with its pole (knee cap, elbow tip, palm, foot top) toward 'pole'.</summary>
        public static Quaternion FromDirs(PartId id, Vector3 bone, Vector3 pole)
        {
            Init();
            bone = MathUtil.SafeNormal(bone, PartInfo.BoneAxis(id));
            Vector3 p = pole - bone * Vector3.Dot(pole, bone);
            if (p.sqrMagnitude < 1e-6f) p = Vector3.Cross(bone, Mathf.Abs(bone.y) < 0.9f ? Vector3.up : Vector3.right);
            return Quaternion.LookRotation(bone, p.normalized) * restInv[(int)id];
        }

        /// <summary>Upper arm relative to the chest. side -1 = left. abduct: outward from hanging, flex: forward.</summary>
        public static Quaternion ArmUpperLocal(float side, float abductDeg, float flexDeg, float twistDeg)
        {
            PartId id = side < 0f ? PartId.UpperArmL : PartId.UpperArmR;
            Quaternion flex = Quaternion.AngleAxis(-flexDeg, Vector3.right);
            Vector3 d = flex * (Quaternion.AngleAxis(side * abductDeg, Vector3.forward) * Vector3.down);
            Vector3 pole = flex * (Vector3.back + Vector3.right * side * 0.25f);
            Quaternion r = FromDirs(id, d, pole);
            if (Mathf.Abs(twistDeg) > 0.01f) r = r * Quaternion.AngleAxis(twistDeg, PartInfo.BoneAxis(id));
            return r;
        }

        public static Quaternion ElbowLocal(float side, float flexDeg, float pronationDeg)
        {
            Vector3 hinge = side < 0f ? Vector3.up : Vector3.down;
            Vector3 bone = side < 0f ? Vector3.left : Vector3.right;
            return Quaternion.AngleAxis(flexDeg, hinge) * Quaternion.AngleAxis(pronationDeg, bone);
        }

        public static Quaternion WristLocal(float side, float flexDeg, float deviationDeg)
        {
            Vector3 ax = side < 0f ? Vector3.forward : Vector3.back;
            return Quaternion.AngleAxis(flexDeg, ax) * Quaternion.AngleAxis(deviationDeg, Vector3.up);
        }

        /// <summary>Thigh relative to pelvis. flex = forward, abduct = outward.</summary>
        public static Quaternion HipLocal(float side, float flexDeg, float abductDeg, float twistDeg)
        {
            return Quaternion.AngleAxis(-flexDeg, Vector3.right) * Quaternion.AngleAxis(side * abductDeg, Vector3.forward) * Quaternion.AngleAxis(twistDeg, Vector3.up);
        }

        public static Quaternion KneeLocal(float flexDeg) { return Quaternion.AngleAxis(flexDeg, Vector3.right); }

        public static Quaternion AnkleLocal(float plantarDeg, float inversionDeg) { return Quaternion.AngleAxis(plantarDeg, Vector3.right) * Quaternion.AngleAxis(inversionDeg, Vector3.forward); }

        public static Quaternion SpineLocal(float flexDeg, float twistDeg, float sideDeg)
        {
            return Quaternion.AngleAxis(flexDeg, Vector3.right) * Quaternion.AngleAxis(twistDeg, Vector3.up) * Quaternion.AngleAxis(sideDeg, Vector3.forward);
        }
    }

    public static class IKSolver
    {
        /// <summary>Analytic two-bone IK. Returns bone directions and per-bone pole vectors (bend plane toward poleDir).</summary>
        public static void TwoBone(Vector3 root, Vector3 target, Vector3 poleDir, float l1, float l2, float reach,
            out Vector3 d1, out Vector3 d2, out Vector3 pole1, out Vector3 pole2, out Vector3 joint)
        {
            Vector3 toT = target - root;
            float dist = toT.magnitude;
            Vector3 dirT = dist > 1e-5f ? toT / dist : Vector3.down;
            float maxLen = (l1 + l2) * 0.999f * Mathf.Clamp(reach, 0.3f, 1f);
            float minLen = Mathf.Abs(l1 - l2) + 0.02f;
            dist = Mathf.Clamp(dist, minLen, maxLen);
            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));
            Vector3 pd = poleDir - dirT * Vector3.Dot(poleDir, dirT);
            if (pd.sqrMagnitude < 1e-6f) pd = Vector3.Cross(dirT, Mathf.Abs(dirT.y) < 0.9f ? Vector3.up : Vector3.right);
            pd.Normalize();
            joint = root + (dirT * cosA + pd * sinA) * l1;
            Vector3 end = root + dirT * dist;
            d1 = (joint - root) / l1;
            d2 = MathUtil.SafeNormal(end - joint, dirT);
            pole1 = pd - d1 * Vector3.Dot(pd, d1);
            pole2 = pd - d2 * Vector3.Dot(pd, d2);
            pole1 = MathUtil.SafeNormal(pole1, pd);
            pole2 = MathUtil.SafeNormal(pole2, pd);
        }

        /// <summary>Head/neck look-at: returns local targets for neck and head limited relative to the chest.</summary>
        public static void LookAt(Quaternion chestRot, Vector3 eyePos, Vector3 target, float maxYaw, float maxPitch, out Quaternion neckLocal, out Quaternion headLocal)
        {
            Vector3 dir = target - eyePos;
            Vector3 local = Quaternion.Inverse(chestRot) * dir;
            if (local.sqrMagnitude < 1e-6f) local = Vector3.forward;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            yaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
            pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
            neckLocal = Quaternion.AngleAxis(yaw * 0.4f, Vector3.up) * Quaternion.AngleAxis(pitch * 0.4f, Vector3.right);
            headLocal = Quaternion.AngleAxis(yaw * 0.6f, Vector3.up) * Quaternion.AngleAxis(pitch * 0.6f, Vector3.right);
        }
    }
    #endregion

    #region MuscleController
    public class MuscleController
    {
        readonly RagdollBody body;
        public readonly Quaternion[] target = new Quaternion[PartInfo.Count];
        public readonly float[] poseStiffness = new float[PartInfo.Count];
        public readonly float[] strength = new float[PartInfo.Count];
        public float ActiveLevel = 1f;
        public float DeathFade = 1f;
        public float Brace;            // co-contraction (0..1)
        public float Stun;             // seconds of limp reaction
        public float SpasmAmount;      // random joint noise (shock, headless, dying)
        public float FlinchTimer;
        public float GlobalScale = 1f; // behaviour-level strength (falling limp, relaxed lying)
        float twitchTime = -1f;
        bool twitched;
        readonly Vector3[] spasmTarget = new Vector3[PartInfo.Count];
        readonly Vector3[] spasmCurrent = new Vector3[PartInfo.Count];
        float spasmTimer;
        Rng rng;

        public MuscleController(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() + 99); }

        public void Init()
        {
            for (int i = 0; i < PartInfo.Count; i++) { target[i] = Quaternion.identity; poseStiffness[i] = 1f; strength[i] = 1f; }
            SetRelaxedArms();
        }

        public void SetRelaxedArms()
        {
            target[(int)PartId.UpperArmL] = Pose.ArmUpperLocal(-1f, 9f, 4f, 0f);
            target[(int)PartId.UpperArmR] = Pose.ArmUpperLocal(1f, 9f, 4f, 0f);
            target[(int)PartId.ForearmL] = Pose.ElbowLocal(-1f, 14f, 0f);
            target[(int)PartId.ForearmR] = Pose.ElbowLocal(1f, 14f, 0f);
        }

        public void OnTopologyChanged() { }

        public void Set(PartId id, Quaternion local, float stiffness = 1f)
        {
            target[(int)id] = local;
            poseStiffness[(int)id] = stiffness;
        }

        public void Apply(float dt)
        {
            var ms = Sandbox.Boot.muscles;
            var vit = body.Vitals;
            // Active level from consciousness (or the UI override).
            float c = vit.Consciousness;
            float auto = c > 0.55f ? 1f : c > 0.25f ? Mathf.Lerp(ms.unconsciousTone, 1f, (c - 0.25f) / 0.3f) : ms.unconsciousTone;
            if (!body.BrainPresent && !vit.Dead) auto = Mathf.Max(auto, vit.HeadlessDrive);
            ActiveLevel = ms.overrideActiveLevel ? ms.activeLevel : auto;
            if (vit.Dead)
            {
                if (twitchTime < 0f) twitchTime = Time.time + ms.deathFadeTime * 0.55f;
                DeathFade = Mathf.MoveTowards(DeathFade, 0f, dt / Mathf.Max(0.1f, ms.deathFadeTime));
                if (!twitched && Time.time >= twitchTime) { twitched = true; SpasmAmount = Mathf.Max(SpasmAmount, 1.2f); spasmTimer = 0f; }
            }
            else { DeathFade = Mathf.MoveTowards(DeathFade, 1f, dt * 0.5f); twitchTime = -1f; twitched = false; }

            if (Stun > 0f) Stun -= dt;
            if (FlinchTimer > 0f) FlinchTimer -= dt;
            float stunMul = Stun > 0f ? 0.3f : 1f;
            float brace = Mathf.Clamp01(Brace + (FlinchTimer > 0f ? 0.8f : 0f));
            float stiffMul = Mathf.Lerp(1f, ms.coContraction, brace);
            float vitalsMul = vit.MuscleFactor;
            float global = ms.strength * ActiveLevel * DeathFade * vitalsMul * stunMul * GlobalScale;

            UpdateSpasm(dt);

            for (int i = 0; i < body.AllPieces.Count; i++)
            {
                var p = body.AllPieces[i];
                if (p == null || p.jr == null || p.jr.joint == null) continue;
                var j = p.jr.joint;
                int id = (int)p.id;
                bool motor = p.attached && !p.isPiece && !p.paralyzed && global > 0.001f;
                if (p.isPiece || !p.attached)
                {
                    j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 1.5f * body.MassScale, maximumForce = 1000f };
                    continue;
                }
                float s = 0f;
                if (motor)
                {
                    float health = Mathf.Lerp(0.25f, 1f, Mathf.Min(p.HealthFraction, p.parentPiece != null ? Mathf.Lerp(0.4f, 1f, p.parentPiece.HealthFraction) : 1f));
                    float frac = (p.fractured ? 0.22f : 1f) * (p.parentPiece != null && p.parentPiece.fractured ? 0.45f : 1f);
                    float fatigue = 1f - vit.Fatigue * 0.45f;
                    s = global * health * frac * fatigue * poseStiffness[id];
                }
                strength[id] = s;
                float spring, damper, maxT;
                GroupGains(p.jr.group, ms, out spring, out damper, out maxT);
                float springK = spring * s * stiffMul * body.MassScale;
                float damperK = damper * (0.25f + 0.75f * Mathf.Min(1f, s)) * Mathf.Sqrt(stiffMul) * body.MassScale;
                if (!motor) { springK = 0f; damperK = damper * 0.06f * body.MassScale; }
                float maxF = Mathf.Max(5f, maxT * Mathf.Max(s, 0.05f) * ms.torqueLimitScale * stiffMul * body.MassScale);
                j.slerpDrive = new JointDrive { positionSpring = springK, positionDamper = damperK, maximumForce = maxF };
                Quaternion t = target[id];
                if (SpasmAmount > 0.01f)
                {
                    Vector3 e = spasmCurrent[id] * SpasmAmount;
                    t = t * Quaternion.Euler(e);
                }
                if (MathUtil.IsFinite(t)) j.targetRotation = p.jr.ToJointSpace(t);
            }
        }

        void UpdateSpasm(float dt)
        {
            if (SpasmAmount <= 0.01f) return;
            spasmTimer -= dt;
            if (spasmTimer <= 0f)
            {
                spasmTimer = rng.Range(0.06f, 0.22f);
                for (int i = 0; i < PartInfo.Count; i++)
                    spasmTarget[i] = new Vector3(rng.Range(-40f, 40f), rng.Range(-25f, 25f), rng.Range(-30f, 30f));
            }
            for (int i = 0; i < PartInfo.Count; i++) spasmCurrent[i] = Vector3.Lerp(spasmCurrent[i], spasmTarget[i], 1f - Mathf.Exp(-25f * dt));
            SpasmAmount = Mathf.MoveTowards(SpasmAmount, 0f, dt * 0.35f);
        }

        public static void GroupGains(MuscleGroup g, MuscleSettings ms, out float spring, out float damper, out float maxTorque)
        {
            switch (g)
            {
                case MuscleGroup.Spine: spring = ms.spineSpring; damper = ms.spineDamper; maxTorque = 700f; break;
                case MuscleGroup.Neck: spring = ms.neckSpring; damper = ms.neckDamper; maxTorque = 170f; break;
                case MuscleGroup.Head: spring = ms.neckSpring * 0.7f; damper = ms.neckDamper * 0.7f; maxTorque = 110f; break;
                case MuscleGroup.Shoulder: spring = ms.shoulderSpring; damper = ms.shoulderDamper; maxTorque = 230f; break;
                case MuscleGroup.Elbow: spring = ms.elbowSpring; damper = ms.elbowDamper; maxTorque = 140f; break;
                case MuscleGroup.Wrist: spring = ms.wristSpring; damper = ms.wristDamper; maxTorque = 45f; break;
                case MuscleGroup.Hip: spring = ms.hipSpring; damper = ms.hipDamper; maxTorque = 950f; break;
                case MuscleGroup.Knee: spring = ms.kneeSpring; damper = ms.kneeDamper; maxTorque = 900f; break;
                case MuscleGroup.Ankle: spring = ms.ankleSpring; damper = ms.ankleDamper; maxTorque = 480f; break;
                default: spring = 0f; damper = 2f; maxTorque = 50f; break;
            }
        }

        public float LegStrength(int side)
        {
            PartId thigh = side == 0 ? PartId.ThighL : PartId.ThighR;
            PartId shin = side == 0 ? PartId.ShinL : PartId.ShinR;
            return Mathf.Min(strength[(int)thigh], strength[(int)shin]);
        }

        public float ArmStrength(int side)
        {
            PartId up = side == 0 ? PartId.UpperArmL : PartId.UpperArmR;
            PartId fore = side == 0 ? PartId.ForearmL : PartId.ForearmR;
            return Mathf.Min(strength[(int)up], strength[(int)fore]);
        }

        public void Flinch(float amount)
        {
            float k = Sandbox.Boot.muscles.reflexStrength;
            if (k <= 0f) return;
            FlinchTimer = Mathf.Max(FlinchTimer, 0.12f + 0.15f * Mathf.Clamp01(amount) * k);
        }

        public void StunFor(float seconds) { Stun = Mathf.Max(Stun, seconds * Sandbox.Boot.muscles.reflexStrength); }
        public void Spasm(float amount) { SpasmAmount = Mathf.Max(SpasmAmount, amount); }
    }

    public static class InstanceIdExtensions
    {
        static int counter = 1;
        static readonly Dictionary<UnityEngine.Object, int> ids = new Dictionary<UnityEngine.Object, int>();
        /// <summary>Stable small id per object without using GetInstanceID (deprecated in newer Unity versions).</summary>
        public static int GetInstanceIDSafe(this UnityEngine.Object o)
        {
            if (o == null) return 0;
            int id;
            if (!ids.TryGetValue(o, out id)) { id = counter++; ids[o] = id; }
            return id;
        }
    }
    #endregion

    #region BalanceController
    public class BalanceController
    {
        readonly RagdollBody body;
        public float GroundHeight;
        public Vector3 GroundNormal = Vector3.up;
        public bool HasGround;
        public float Tilt;              // chest tilt from vertical (deg)
        public float PelvisTilt;
        public bool Upright = true;
        public float PelvisHeight;      // pelvis COM above ground
        public float ComHeight;
        public Vector3 CapturePoint;
        public float Omega = 3.1f;
        public Vector3 SupportCenter;
        public int SupportCount;
        public float SupportDistance;   // signed distance of the capture point outside the support polygon
        public readonly bool[] FootGrounded = new bool[2];
        public readonly BodyPart[] FootPart = new BodyPart[2];
        public readonly Vector3[] SolePos = new Vector3[2];
        public readonly Vector3[] FootFwd = new Vector3[2];
        public float AssistBudget;
        public float UprightDot;        // chest up . world up

        readonly RaycastHit[] hits = new RaycastHit[16];
        readonly Vector2[] hullIn = new Vector2[8];
        readonly Vector2[] hullOut = new Vector2[17];
        int hullCount;

        public BalanceController(RagdollBody b) { body = b; }

        public float StandHeight { get { return Sandbox.Boot.balance.standHeightRatio * PartInfo.RefHeight * body.Scale; } }
        public float G { get { return Physics.gravity.magnitude; } }

        public void Sense(float dt)
        {
            var pelvis = body.Pelvis;
            var chest = body.Chest;
            if (pelvis == null) return;
            Vector3 chestUp = chest != null ? chest.rb.rotation * Vector3.up : Vector3.up;
            UprightDot = Vector3.Dot(chestUp, Vector3.up);
            Tilt = Vector3.Angle(chestUp, Vector3.up);
            PelvisTilt = Vector3.Angle(pelvis.rb.rotation * Vector3.up, Vector3.up);
            float s = body.Scale;
            SupportCount = 0;
            Vector3 sc = Vector3.zero;
            float groundSum = 0f;
            int gn = 0;
            for (int side = 0; side < 2; side++)
            {
                PartId foot = side == 0 ? PartId.FootL : PartId.FootR;
                PartId shin = side == 0 ? PartId.ShinL : PartId.ShinR;
                BodyPart fp = body.LegUsable[side] ? (body.LegHasFoot[side] ? body.Part(foot) : body.Part(shin)) : null;
                FootPart[side] = fp;
                FootGrounded[side] = false;
                if (fp == null) continue;
                Quaternion r = fp.rb.rotation;
                if (fp.id == foot) SolePos[side] = fp.rb.position + r * (new Vector3(0f, -0.075f, 0.065f) * s);
                else SolePos[side] = fp.rb.position + r * (new Vector3(0f, -0.4f, 0f) * s);
                FootFwd[side] = MathUtil.SafeNormal(MathUtil.Flat(fp.id == foot ? r * Vector3.forward : pelvis.rb.rotation * Vector3.forward), Vector3.forward);
                if (fp.Grounded)
                {
                    FootGrounded[side] = true;
                    SupportCount++;
                    sc += SolePos[side];
                    groundSum += fp.groundPoint.y;
                    gn++;
                }
            }
            if (SupportCount > 0) SupportCenter = sc / SupportCount;
            if (gn > 0)
            {
                GroundHeight = groundSum / gn;
                HasGround = true;
                GroundNormal = Vector3.up;
            }
            else
            {
                RaycastHit hit;
                if (Ground(pelvis.rb.position, 3f * s, out hit)) { GroundHeight = hit.point.y; GroundNormal = hit.normal; HasGround = true; }
                else { HasGround = false; GroundHeight = pelvis.rb.position.y - StandHeight * 2f; }
            }
            PelvisHeight = pelvis.rb.worldCenterOfMass.y - GroundHeight;
            ComHeight = Mathf.Max(0.3f * s, body.CenterOfMass.y - GroundHeight);
            float g = Mathf.Max(0.1f, G);
            Omega = Mathf.Sqrt(g / ComHeight);
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            CapturePoint = MathUtil.Flat(body.CenterOfMass) + v / Omega;
            SupportDistance = SupportCount > 0 ? DistanceOutsideSupport(CapturePoint) : 10f;
            Upright = Tilt < Sandbox.Boot.balance.fallAngle && PelvisHeight > StandHeight * 0.55f;
            float health = 0f;
            int hn = 0;
            for (int i = 0; i < body.AttachedParts.Count; i++) { health += body.AttachedParts[i].HealthFraction; hn++; }
            health = hn > 0 ? health / hn : 0f;
            AssistBudget = Sandbox.Boot.balance.assistTorque * Mathf.Clamp01(body.Vitals.Consciousness) * Mathf.Lerp(0.2f, 1f, health) * body.Muscles.DeathFade;
        }

        /// <summary>Raycasts down for walkable ground, ignoring this ragdoll's own colliders and triggers.</summary>
        public bool Ground(Vector3 from, float maxDist, out RaycastHit best)
        {
            best = new RaycastHit();
            int n = Physics.RaycastNonAlloc(from + Vector3.up * 0.05f, Vector3.down, hits, maxDist, ~0, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                BodyPart bp = Registry.PartOf(h.collider);
                if (bp != null && bp.body == body) continue;
                Organ og;
                if (Registry.Organs.TryGetValue(h.collider, out og)) continue;
                if (h.distance < bd) { bd = h.distance; best = h; found = true; }
            }
            return found;
        }

        /// <summary>Signed distance from p (XZ) to the support polygon built from grounded feet (negative inside).</summary>
        public float DistanceOutsideSupport(Vector3 p)
        {
            float s = body.Scale;
            int k = 0;
            for (int side = 0; side < 2; side++)
            {
                if (!FootGrounded[side]) continue;
                Vector3 c = SolePos[side];
                Vector3 f = FootFwd[side];
                Vector3 r = new Vector3(f.z, 0f, -f.x);
                float hl = (FootPart[side] != null && (FootPart[side].id == PartId.FootL || FootPart[side].id == PartId.FootR) ? 0.125f : 0.04f) * s;
                float hw = 0.045f * s;
                hullIn[k++] = new Vector2(c.x + f.x * hl + r.x * hw, c.z + f.z * hl + r.z * hw);
                hullIn[k++] = new Vector2(c.x + f.x * hl - r.x * hw, c.z + f.z * hl - r.z * hw);
                hullIn[k++] = new Vector2(c.x - f.x * hl + r.x * hw, c.z - f.z * hl + r.z * hw);
                hullIn[k++] = new Vector2(c.x - f.x * hl - r.x * hw, c.z - f.z * hl - r.z * hw);
            }
            if (k == 0) return 10f;
            ConvexHull(k);
            Vector2 q = new Vector2(p.x, p.z);
            bool inside = true;
            float minDist = float.MaxValue;
            for (int i = 0; i < hullCount; i++)
            {
                Vector2 a = hullOut[i], b = hullOut[(i + 1) % hullCount];
                Vector2 e = b - a;
                float cross = e.x * (q.y - a.y) - e.y * (q.x - a.x);
                if (cross < 0f) inside = false;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, e) / Mathf.Max(1e-8f, e.sqrMagnitude));
                float d = (a + e * t - q).magnitude;
                if (d < minDist) minDist = d;
            }
            return inside ? -minDist : minDist;
        }

        void ConvexHull(int n)
        {
            // insertion sort by x then y (n <= 8)
            for (int i = 1; i < n; i++)
            {
                Vector2 key = hullIn[i];
                int j = i - 1;
                while (j >= 0 && (hullIn[j].x > key.x || (hullIn[j].x == key.x && hullIn[j].y > key.y))) { hullIn[j + 1] = hullIn[j]; j--; }
                hullIn[j + 1] = key;
            }
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(hullOut[k - 2], hullOut[k - 1], hullIn[i]) <= 0f) k--;
                hullOut[k++] = hullIn[i];
            }
            for (int i = n - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && Cross(hullOut[k - 2], hullOut[k - 1], hullIn[i]) <= 0f) k--;
                hullOut[k++] = hullIn[i];
            }
            hullCount = Mathf.Max(1, k - 1);
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) { return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x); }

        public static void PairForce(Rigidbody a, Vector3 forceOnA, Rigidbody b)
        {
            if (a == null || b == null || !MathUtil.IsFinite(forceOnA)) return;
            a.AddForce(forceOnA, ForceMode.Force);
            b.AddForce(-forceOnA, ForceMode.Force);
        }

        public static void PairImpulse(Rigidbody a, Vector3 impulseOnA, Rigidbody b)
        {
            if (a == null || b == null || !MathUtil.IsFinite(impulseOnA)) return;
            a.AddForce(impulseOnA, ForceMode.Impulse);
            b.AddForce(-impulseOnA, ForceMode.Impulse);
        }

        /// <summary>PD torque toward 'target'. Explicit torques are clamped by the body's own inertia so they stay
        /// numerically stable at the fixed timestep; the stiff posture itself comes from the implicit joint drives.</summary>
        public static Vector3 UprightTorque(Rigidbody rb, Quaternion target, float kp, float kd)
        {
            Vector3 err = MathUtil.RotationError(rb.rotation, target);
            Vector3 inertia = rb.inertiaTensor;
            float imin = Mathf.Max(1e-4f, Mathf.Min(inertia.x, Mathf.Min(inertia.y, inertia.z)));
            float dt = Mathf.Max(0.002f, Time.fixedDeltaTime);
            float kpMax = 0.45f * imin / (dt * dt);
            float kdMax = 0.5f * imin / dt;
            return err * Mathf.Min(kp, kpMax) - rb.angularVelocity * Mathf.Min(kd, kdMax);
        }

        /// <summary>Bounded external assist torque (scales with consciousness, fades with damage).</summary>
        public void Assist(Rigidbody rb, Vector3 torque, float scale = 1f)
        {
            float max = AssistBudget * scale;
            if (max <= 0f || rb == null) return;
            if (torque.magnitude > max) torque = torque.normalized * max;
            if (MathUtil.IsFinite(torque)) rb.AddTorque(torque, ForceMode.Force);
        }

        /// <summary>Applies the stance support: pelvis height, COM tracking and upright torques, with reactions on the
        /// stance feet (friction- and ankle-capacity-limited) and a bounded assist for the remainder.</summary>
        public void Support(float heightTarget, Vector3 comTargetXZ, bool usePosition, Vector3 velTargetXZ, Quaternion pelvisTarget, Quaternion chestTarget,
            float legStrength, float uprightScale, float extraLift)
        {
            var bs = Sandbox.Boot.balance;
            var pelvis = body.Pelvis;
            var chest = body.Chest;
            if (pelvis == null || SupportCount == 0) return;
            float g = G;
            float mass = body.TotalMass;
            float legMass = 0f;
            for (int side = 0; side < 2; side++)
            {
                if (!FootGrounded[side]) continue;
                PartId thigh = side == 0 ? PartId.ThighL : PartId.ThighR, shin = side == 0 ? PartId.ShinL : PartId.ShinR, foot = side == 0 ? PartId.FootL : PartId.FootR;
                if (body.Has(thigh)) legMass += body.Part(thigh).rb.mass;
                if (body.Has(shin)) legMass += body.Part(shin).rb.mass;
                if (body.Has(foot)) legMass += body.Part(foot).rb.mass;
            }
            float supported = Mathf.Max(5f, mass - legMass);
            float pelvisY = pelvis.rb.worldCenterOfMass.y;
            float vy = body.ComVelocity.y;
            float hErr = heightTarget - pelvisY;
            float fy = supported * (g + bs.heightGain * hErr - bs.heightDamping * vy) + extraLift;
            fy = Mathf.Clamp(fy, 0f, 2.8f * mass * Mathf.Max(g, 1f)) * Mathf.Clamp01(legStrength);

            Vector3 com = MathUtil.Flat(body.CenterOfMass);
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            Vector3 fh = mass * (bs.comDamping * (velTargetXZ - v));
            if (usePosition) fh += mass * bs.comGain * (MathUtil.Flat(comTargetXZ) - com);
            float mu = 0.9f * bs.friction;
            float fhMax = mu * fy;
            if (fh.magnitude > fhMax) fh = fh.normalized * fhMax;

            // Upright torques: pelvis paired with the feet (ankle strategy), chest paired with the pelvis (spine).
            Vector3 tp = UprightTorque(pelvis.rb, pelvisTarget, bs.pelvisUprightSpring * body.MassScale, bs.pelvisUprightDamper * body.MassScale) * uprightScale;
            Vector3 tc = chest != null && chest.attached ? UprightTorque(chest.rb, chestTarget, bs.chestUprightSpring * body.MassScale, bs.chestUprightDamper * body.MassScale) * uprightScale : Vector3.zero;
            if (tc.magnitude > 450f) tc = tc.normalized * 450f;

            // Distribute ground reaction by proximity of the COM to each stance foot.
            float w0 = 0f, w1 = 0f;
            if (FootGrounded[0]) w0 = 1f / (0.05f + (MathUtil.Flat(SolePos[0]) - com).magnitude);
            if (FootGrounded[1]) w1 = 1f / (0.05f + (MathUtil.Flat(SolePos[1]) - com).magnitude);
            float ws = w0 + w1;
            if (ws <= 0f) return;
            w0 /= ws; w1 /= ws;

            Vector3 total = Vector3.up * fy + fh;
            pelvis.rb.AddForce(total, ForceMode.Force);
            Vector3 feetTorque = Vector3.zero;
            for (int side = 0; side < 2; side++)
            {
                float w = side == 0 ? w0 : w1;
                if (w <= 0f) continue;
                var fp = FootPart[side];
                fp.rb.AddForceAtPosition(-total * w, SolePos[side], ForceMode.Force);
                // ankle capacity limited reaction torque
                float load = fy * w;
                Vector3 f = FootFwd[side];
                Vector3 r = new Vector3(f.z, 0f, -f.x);
                Vector3 desired = -tp * w;
                float pitch = Vector3.Dot(desired, r), roll = Vector3.Dot(desired, f), yaw = desired.y;
                float s = body.Scale * bs.ankleCapacity;
                float capPitch = load * 0.11f * s, capRoll = load * 0.045f * s, capYaw = load * 0.05f * s;
                pitch = Mathf.Clamp(pitch, -capPitch, capPitch);
                roll = Mathf.Clamp(roll, -capRoll, capRoll);
                yaw = Mathf.Clamp(yaw, -capYaw, capYaw);
                Vector3 applied = r * pitch + f * roll + Vector3.up * yaw;
                fp.rb.AddTorque(applied, ForceMode.Force);
                feetTorque -= applied;
            }
            // Pelvis receives what the feet can react, plus a bounded assist for the rest.
            pelvis.rb.AddTorque(feetTorque, ForceMode.Force);
            Assist(pelvis.rb, tp - feetTorque);
            if (chest != null && chest.attached && tc.sqrMagnitude > 0f)
            {
                chest.rb.AddTorque(tc, ForceMode.Force);
                pelvis.rb.AddTorque(-tc, ForceMode.Force);
            }
        }
    }
    #endregion

    #region LocomotionController
    public enum GaitMode { Normal, Hop }

    public class LocomotionController
    {
        enum FootPhase { Stance, Swing }

        struct FootState
        {
            public FootPhase phase;
            public Vector3 planted;       // ankle target while in stance
            public float plantedYaw;
            public Vector3 lift, target, swingPos, swingVel;
            public float targetYaw, liftYaw;
            public float t, dur, clearance;
            public float lastPlant;
            public bool blocked;
        }

        readonly RagdollBody body;
        readonly FootState[] feet = new FootState[2];
        public Vector3 DesiredVelocity;
        public float DesiredYaw;
        public float HeadingYaw;
        public bool Run, Crouch;
        public GaitMode Mode = GaitMode.Normal;
        public float StumbleTimer;
        public float SlipTimer;
        int lastSwing = 1;
        float doubleSupportUntil;
        bool initialized;
        float crouchAmount;
        float jumpTimer = -1f;
        int jumpPhase;           // 0 none, 1 crouch, 2 push, 3 air, 4 land
        float airTime;
        float hopTimer;
        int hopPhase;
        public float LegReach = 1f;
        Rng rng;
        public float Bob;

        public LocomotionController(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 7 + 3); }

        public bool Airborne { get { return jumpPhase == 3; } }
        public bool Jumping { get { return jumpPhase > 0; } }
        public float CrouchAmount { get { return crouchAmount; } }

        public void ResetFeet()
        {
            initialized = false;
        }

        public void RequestJump()
        {
            if (jumpPhase == 0 && body.Balance.SupportCount > 0) { jumpPhase = 1; jumpTimer = 0f; }
        }

        void Init()
        {
            initialized = true;
            HeadingYaw = MathUtil.Yaw(MathUtil.Flat(body.Pelvis.rb.rotation * Vector3.forward));
            DesiredYaw = HeadingYaw;
            for (int s = 0; s < 2; s++)
            {
                feet[s].phase = FootPhase.Stance;
                var fp = body.Balance.FootPart[s];
                feet[s].planted = FootAnchor(s);
                feet[s].plantedYaw = HeadingYaw + (s == 0 ? -6f : 6f);
                feet[s].lastPlant = Time.time;
            }
        }

        public Quaternion Heading { get { return Quaternion.Euler(0f, HeadingYaw, 0f); } }

        /// <summary>The leg's ground anchor: the ankle, or the distal end of the shin when the foot is missing.</summary>
        public Vector3 FootAnchor(int i)
        {
            var fp = body.Balance.FootPart[i];
            if (fp == null) return body.Pelvis.rb.position;
            if (body.LegHasFoot[i]) return fp.rb.position;
            return fp.rb.position + fp.rb.rotation * (Vector3.down * 0.39f * body.Scale);
        }

        float AnchorHeight(int i) { return (body.LegHasFoot[i] ? 0.075f : 0.035f) * body.Scale; }

        /// <summary>Runs the gait. Returns false if the body could not be supported (should fall).</summary>
        public bool Tick(float dt, float strengthScale)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            if (!initialized) Init();
            float s = body.Scale;
            float g = bal.G;
            if (StumbleTimer > 0f) StumbleTimer -= dt;
            if (SlipTimer > 0f) SlipTimer -= dt;

            // Heading
            float yawErr = Mathf.DeltaAngle(HeadingYaw, DesiredYaw);
            HeadingYaw += Mathf.Clamp(yawErr, -ls.turnSpeed * dt, ls.turnSpeed * dt);
            Quaternion H = Heading;
            Vector3 fwd = H * Vector3.forward, right = H * Vector3.right;

            crouchAmount = Mathf.MoveTowards(crouchAmount, Crouch ? 1f : 0f, dt * 3f);
            float speed = DesiredVelocity.magnitude;
            float maxSpeed = Crouch ? ls.crouchSpeed : Run ? ls.runSpeed : ls.walkSpeed;
            if (Mode == GaitMode.Hop) maxSpeed = Mathf.Min(maxSpeed, 0.9f);
            Vector3 vDes = speed > maxSpeed ? DesiredVelocity / speed * maxSpeed : DesiredVelocity;
            vDes *= Mathf.Lerp(0.35f, 1f, strengthScale) * (SlipTimer > 0f ? 0.6f : 1f);
            float runFactor = Mathf.Clamp01((vDes.magnitude - ls.walkSpeed) / Mathf.Max(0.1f, ls.runSpeed - ls.walkSpeed));

            // Jump state
            float jumpLower = 0f, extraLift = 0f;
            if (jumpPhase > 0)
            {
                jumpTimer += dt;
                if (jumpPhase == 1) { jumpLower = 0.2f * s * Mathf.Clamp01(jumpTimer / 0.18f); if (jumpTimer > 0.2f) { jumpPhase = 2; jumpTimer = 0f; } }
                else if (jumpPhase == 2)
                {
                    extraLift = ls.jumpForce * body.TotalMass * g * strengthScale;
                    if (jumpTimer > 0.14f || bal.SupportCount == 0) { jumpPhase = 3; jumpTimer = 0f; airTime = 0f; }
                }
                else if (jumpPhase == 3)
                {
                    airTime += dt;
                    if (airTime > 0.12f && bal.SupportCount > 0) { jumpPhase = 4; jumpTimer = 0f; PlantAll(); }
                    if (airTime > 2.5f) jumpPhase = 0;
                }
                else if (jumpPhase == 4) { jumpLower = 0.1f * s * (1f - Mathf.Clamp01(jumpTimer / 0.3f)); if (jumpTimer > 0.3f) jumpPhase = 0; }
            }

            // Stance/swing state machine
            if (jumpPhase != 3) StepLogic(dt, vDes, fwd, right, runFactor, yawErr);
            else
            {
                // airborne: legs prepare to land under the predicted centre of mass
                for (int i = 0; i < 2; i++) { feet[i].phase = FootPhase.Stance; feet[i].planted = AirFootTarget(i, right); }
            }

            // Height target adapts to the stance feet heights
            float lowest = float.MaxValue, highest = float.MinValue;
            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) continue;
                float gy = feet[i].phase == FootPhase.Stance ? feet[i].planted.y - AnchorHeight(i) : bal.GroundHeight;
                lowest = Mathf.Min(lowest, gy); highest = Mathf.Max(highest, gy);
            }
            float groundY = lowest < float.MaxValue ? Mathf.Lerp(lowest, highest, 0.35f) : bal.GroundHeight;
            if (bal.SupportCount > 0) groundY = Mathf.Lerp(groundY, bal.GroundHeight, 0.5f);
            float stand = bal.StandHeight;
            float hTarget = groundY + stand * (1f - 0.33f * crouchAmount) - jumpLower - Mathf.Abs(Bob) * 0.02f * s;
            if (Mode == GaitMode.Hop) hTarget -= 0.03f * s;
            // stump leg (no foot) is shorter
            for (int i = 0; i < 2; i++) if (bal.FootPart[i] != null && !body.LegHasFoot[i]) hTarget -= 0.04f * s;

            // Pelvis and chest targets: heading + lean with acceleration/velocity
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            float lean = Mathf.Clamp(Vector3.Dot(vDes, fwd) * 4f + Vector3.Dot(vDes - v, fwd) * 6f, -8f, 16f) + runFactor * 6f + crouchAmount * 12f;
            float roll = Mathf.Clamp(Vector3.Dot(vDes - v, right) * 5f, -6f, 6f);
            if (StumbleTimer > 0f) lean += 10f;
            float sway = 0f;
            for (int i = 0; i < 2; i++) if (feet[i].phase == FootPhase.Swing) sway += (i == 0 ? 1f : -1f) * 3f * Mathf.Sin(feet[i].t / Mathf.Max(0.01f, feet[i].dur) * Mathf.PI);
            Quaternion pelvisTarget = H * Quaternion.Euler(lean * 0.4f, 0f, -roll + sway * 0.5f);
            Quaternion chestTarget = H * Quaternion.Euler(lean, 0f, -roll * 0.5f);

            float legStr = 0f;
            int ln = 0;
            for (int i = 0; i < 2; i++) if (bal.FootGrounded[i]) { legStr += Mathf.Clamp01(body.Muscles.LegStrength(i) * 1.25f); ln++; }
            legStr = ln > 0 ? legStr / ln : 0f;
            legStr = Mathf.Clamp01(legStr) * strengthScale;

            Vector3 comTarget = bal.SupportCount > 0 ? bal.SupportCenter : MathUtil.Flat(body.CenterOfMass);
            bool usePos = vDes.sqrMagnitude < 0.01f && !AnySwing();
            if (bal.SupportCount > 0 && jumpPhase != 3)
                bal.Support(hTarget, comTarget, usePos, vDes, pelvisTarget, chestTarget, legStr, Mathf.Lerp(0.3f, 1f, strengthScale), extraLift);
            else
            {
                // airborne: keep the torso composed with the bounded assist only
                bal.Assist(body.Pelvis.rb, BalanceController.UprightTorque(body.Pelvis.rb, pelvisTarget, 300f, 40f), 0.6f);
            }

            // Hop gait: periodic jumps on the single leg while moving
            if (Mode == GaitMode.Hop) HopLogic(dt, vDes, fwd, strengthScale);

            // Desired pelvis placement: above the support when standing, slightly ahead of itself when walking.
            Vector3 pelvisPos = body.Pelvis.rb.position;
            Vector3 desiredPelvis;
            float blend;
            if (bal.SupportCount > 0 && usePos)
            {
                Vector3 c = bal.SupportCenter - fwd * 0.02f * s;
                desiredPelvis = new Vector3(c.x, hTarget, c.z);
                blend = 0.6f;
            }
            else
            {
                desiredPelvis = new Vector3(pelvisPos.x, hTarget, pelvisPos.z) + vDes * 0.06f;
                blend = jumpPhase == 3 ? 0f : 0.3f;
            }
            ApplyLegTargets(H, fwd, right, dt, strengthScale, pelvisTarget, desiredPelvis, blend);
            SwingForces(dt);
            ArmsAndSpine(fwd, runFactor);
            return legStr > 0.12f || jumpPhase == 3;
        }

        bool AnySwing() { return feet[0].phase == FootPhase.Swing || feet[1].phase == FootPhase.Swing; }

        void PlantAll()
        {
            var bal = body.Balance;
            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) continue;
                feet[i].phase = FootPhase.Stance;
                feet[i].planted = FootAnchor(i);
                feet[i].plantedYaw = HeadingYaw + (i == 0 ? -6f : 6f);
                feet[i].lastPlant = Time.time;
            }
        }

        public void PlantCurrent() { PlantAll(); }

        Vector3 AirFootTarget(int i, Vector3 right)
        {
            float s = body.Scale;
            Vector3 cp = body.Balance.CapturePoint;
            Vector3 hip = body.Pelvis.rb.position;
            Vector3 p = new Vector3(cp.x, hip.y - 0.75f * s, cp.z) + right * ((i == 0 ? -1f : 1f) * Sandbox.Boot.locomotion.stanceWidth * 0.5f * s);
            return p;
        }

        void StepLogic(float dt, Vector3 vDes, Vector3 fwd, Vector3 right, float runFactor, float yawErr)
        {
            var ls = Sandbox.Boot.locomotion;
            var bs = Sandbox.Boot.balance;
            var bal = body.Balance;
            float s = body.Scale;
            bool moving = vDes.magnitude > 0.08f;
            bool turning = Mathf.Abs(yawErr) > 22f;

            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) { feet[i].phase = FootPhase.Stance; continue; }
                if (feet[i].phase == FootPhase.Stance)
                {
                    // slip detection: planted foot sliding away
                    var fp = bal.FootPart[i];
                    Vector3 anchorPos = FootAnchor(i);
                    float drift = MathUtil.Flat(anchorPos - feet[i].planted).magnitude;
                    if (bal.FootGrounded[i] && drift > 0.07f * s)
                    {
                        feet[i].planted = anchorPos;
                        SlipTimer = 0.8f;
                    }
                    if (!bal.FootGrounded[i] && drift > 0.25f * s) feet[i].planted = anchorPos;
                }
            }

            // Advance swings
            for (int i = 0; i < 2; i++)
            {
                if (feet[i].phase != FootPhase.Swing) continue;
                feet[i].t += dt;
                float u = feet[i].t / feet[i].dur;
                if (u < 0.65f) RetargetSwing(i, vDes, fwd, right, runFactor);
                var fp = bal.FootPart[i];
                bool contact = fp != null && fp.Grounded && u > 0.55f;
                if (u >= 1f || contact || feet[i].blocked)
                {
                    feet[i].phase = FootPhase.Stance;
                    feet[i].planted = feet[i].blocked || contact ? FootAnchor(i) : feet[i].target;
                    feet[i].plantedYaw = feet[i].targetYaw;
                    feet[i].lastPlant = Time.time;
                    feet[i].blocked = false;
                    doubleSupportUntil = Time.time + Mathf.Lerp(0.07f, 0f, runFactor);
                    PlayStep(fp);
                }
            }
            if (AnySwing()) return;
            if (Time.time < doubleSupportUntil) return;
            int usable = (bal.FootPart[0] != null ? 1 : 0) + (bal.FootPart[1] != null ? 1 : 0);
            if (usable < 2) return; // hop gait handles single leg

            // Balance step: capture point outside support
            bool needBalance = bal.SupportCount > 0 && bal.SupportDistance > bs.captureMargin * s;
            if (!moving && !turning && !needBalance)
            {
                // re-square the feet when standing still if they are far from a comfortable stance
                float spread = MathUtil.Flat(feet[0].planted - feet[1].planted).magnitude;
                if (spread < ls.stanceWidth * 2.2f * s && spread > ls.stanceWidth * 0.5f * s) return;
            }
            int foot;
            if (needBalance && !moving)
            {
                Vector3 e = bal.CapturePoint - bal.SupportCenter;
                float lateral = Vector3.Dot(e, right);
                if (Mathf.Abs(lateral) > 0.6f * e.magnitude) foot = lateral < 0f ? 0 : 1;
                else
                {
                    float d0 = (MathUtil.Flat(feet[0].planted) - bal.CapturePoint).sqrMagnitude, d1 = (MathUtil.Flat(feet[1].planted) - bal.CapturePoint).sqrMagnitude;
                    foot = d0 > d1 ? 0 : 1;
                }
            }
            else
            {
                foot = 1 - lastSwing;
                // prefer the trailing foot
                float a0 = Vector3.Dot(feet[0].planted - body.Pelvis.rb.position, vDes), a1 = Vector3.Dot(feet[1].planted - body.Pelvis.rb.position, vDes);
                if (moving && Mathf.Abs(a0 - a1) > 0.1f * s) foot = a0 < a1 ? 0 : 1;
            }
            StartSwing(foot, vDes, fwd, right, runFactor, needBalance && !moving);
        }

        void StartSwing(int i, Vector3 vDes, Vector3 fwd, Vector3 right, float runFactor, bool emergency)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float s = body.Scale;
            var fp = bal.FootPart[i];
            if (fp == null) return;
            feet[i].phase = FootPhase.Swing;
            feet[i].t = 0f;
            float baseDur = Mathf.Lerp(ls.stepTimeWalk, ls.stepTimeRun, runFactor);
            if (emergency) baseDur = Mathf.Min(baseDur, 0.3f);
            if (StumbleTimer > 0f) baseDur *= 0.75f;
            feet[i].dur = baseDur * (body.LegHasFoot[i] ? 1f : 1.25f);
            feet[i].lift = FootAnchor(i);
            feet[i].liftYaw = feet[i].plantedYaw;
            feet[i].blocked = false;
            lastSwing = i;
            RetargetSwing(i, vDes, fwd, right, runFactor);
            ComputeClearance(i);
            // toe-off push: the stance/lifting foot pushes against the ground
            if (bal.FootGrounded[i])
            {
                float k = Mathf.Clamp01(vDes.magnitude / Mathf.Max(0.1f, ls.walkSpeed));
                Vector3 push = MathUtil.SafeNormal(vDes, fwd) * ls.toeOffImpulse * k * body.MassScale;
                push += Vector3.up * runFactor * body.TotalMass * bal.G * 0.045f;
                BalanceController.PairImpulse(body.Pelvis.rb, push, fp.rb);
            }
        }

        void RetargetSwing(int i, Vector3 vDes, Vector3 fwd, Vector3 right, float runFactor)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float s = body.Scale;
            int other = 1 - i;
            float side = i == 0 ? -1f : 1f;
            float remaining = Mathf.Max(0f, feet[i].dur - feet[i].t);
            Vector3 com = MathUtil.Flat(body.CenterOfMass);
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            Vector3 target = com + v * (remaining + feet[i].dur * 0.5f) + (v - vDes) * ls.raibertGain;
            if (bal.SupportDistance > 0f && vDes.sqrMagnitude < 0.01f)
                target = Vector3.Lerp(target, bal.CapturePoint + (bal.CapturePoint - bal.SupportCenter).normalized * 0.05f * s, 0.6f);
            target += right * side * ls.stanceWidth * 0.5f * s;
            // stride clamping relative to the other foot
            Vector3 otherP = MathUtil.Flat(feet[other].planted);
            Vector3 rel = target - otherP;
            float along = Vector3.Dot(rel, fwd), lat = Vector3.Dot(rel, right);
            float maxStride = Mathf.Lerp(ls.maxStride, ls.runStride, runFactor) * s * (SlipTimer > 0f ? 0.65f : 1f) * LegReach;
            along = Mathf.Clamp(along, -maxStride * 0.7f, maxStride);
            float minLat = ls.stanceWidth * 0.55f * s, maxLat = ls.stanceWidth * 2.6f * s;
            if (side < 0f) lat = Mathf.Clamp(lat, -maxLat, -minLat); else lat = Mathf.Clamp(lat, minLat, maxLat);
            target = otherP + fwd * along + right * lat;
            // Ground query at the foothold
            RaycastHit hit;
            float topY = body.Pelvis.rb.position.y + 0.3f * s;
            Vector3 n = Vector3.up;
            float gy = bal.GroundHeight;
            if (bal.Ground(new Vector3(target.x, topY, target.z), 2.2f * s, out hit))
            {
                gy = hit.point.y;
                n = hit.normal;
                if (n.y < 0.6f)
                {
                    target = Vector3.Lerp(target, otherP, 0.35f);
                    if (bal.Ground(new Vector3(target.x, topY, target.z), 2.2f * s, out hit)) { gy = hit.point.y; n = hit.normal; }
                }
            }
            float yaw = HeadingYaw + side * 6f;
            Vector3 footFwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 fwdOnGround = Vector3.ProjectOnPlane(footFwd, n).normalized;
            feet[i].target = new Vector3(target.x, gy, target.z) + n * AnchorHeight(i) - fwdOnGround * (body.LegHasFoot[i] ? 0.065f * s : 0f);
            feet[i].targetYaw = yaw;
        }

        void ComputeClearance(int i)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float s = body.Scale;
            float maxH = Mathf.Max(feet[i].lift.y, feet[i].target.y) - AnchorHeight(i);
            float topY = body.Pelvis.rb.position.y;
            for (int k = 1; k <= 3; k++)
            {
                Vector3 p = Vector3.Lerp(feet[i].lift, feet[i].target, k / 4f);
                RaycastHit hit;
                if (bal.Ground(new Vector3(p.x, topY, p.z), 2f * s, out hit)) maxH = Mathf.Max(maxH, hit.point.y);
            }
            float baseY = Mathf.Max(feet[i].lift.y, feet[i].target.y) - AnchorHeight(i);
            feet[i].clearance = ls.stepHeight * s + Mathf.Max(0f, maxH - baseY) + (StumbleTimer > 0f ? 0.04f * s : 0f);
        }

        /// <summary>Called when a swinging foot hits an obstacle: trip/stumble.</summary>
        public void OnFootBlocked(int side, float speed)
        {
            if (feet[side].phase != FootPhase.Swing) return;
            if (speed < 0.9f) { feet[side].clearance += 0.08f * body.Scale; return; }
            if (rng.Value < Sandbox.Boot.locomotion.stumbleChance)
            {
                feet[side].blocked = true;
                StumbleTimer = 0.9f;
                body.Muscles.Flinch(0.6f);
            }
            else feet[side].clearance += 0.1f * body.Scale;
        }

        public bool IsSwinging(int side) { return feet[side].phase == FootPhase.Swing; }

        void HopLogic(float dt, Vector3 vDes, Vector3 fwd, float strengthScale)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            int leg = bal.FootPart[0] != null ? 0 : 1;
            if (bal.FootPart[leg] == null) return;
            hopTimer += dt;
            bool wantMove = vDes.magnitude > 0.1f || bal.SupportDistance > 0.06f * body.Scale;
            switch (hopPhase)
            {
                case 0:
                    if (wantMove && bal.FootGrounded[leg] && hopTimer > 0.3f) { hopPhase = 1; hopTimer = 0f; }
                    break;
                case 1:
                    if (hopTimer > 0.12f)
                    {
                        hopPhase = 2; hopTimer = 0f;
                        Vector3 dir = vDes.sqrMagnitude > 0.01f ? vDes.normalized : MathUtil.SafeNormal(bal.CapturePoint - bal.SupportCenter, fwd);
                        Vector3 j = Vector3.up * ls.hopForce * body.TotalMass * 0.95f * strengthScale + dir * body.TotalMass * 0.9f * strengthScale;
                        BalanceController.PairImpulse(body.Pelvis.rb, j, bal.FootPart[leg].rb);
                    }
                    break;
                case 2:
                    feet[leg].phase = FootPhase.Stance;
                    feet[leg].planted = AirFootTarget(leg, Vector3.zero) + Vector3.up * 0.05f * body.Scale;
                    if (hopTimer > 0.15f && bal.FootGrounded[leg]) { hopPhase = 0; hopTimer = 0f; feet[leg].planted = FootAnchor(leg); PlayStep(bal.FootPart[leg]); }
                    if (hopTimer > 1.5f) { hopPhase = 0; hopTimer = 0f; }
                    break;
            }
        }

        void PlayStep(BodyPart fp)
        {
            if (fp == null || Sandbox.Audio == null) return;
            float vol = Mathf.Clamp01(0.15f + Compat.GetVelocity(fp.rb).magnitude * 0.1f);
            Sandbox.Audio.Play(Sfx.Footstep, fp.rb.position, vol * 0.5f, rng.Range(0.85f, 1.15f), 1f);
            if (Sandbox.Blood != null) Sandbox.Blood.OnFootPlant(fp);
        }

        /// <summary>Leg IK from a blend of the actual and the desired hip positions; hip targets are expressed relative to
        /// the desired (upright) pelvis so the implicit joint drives hold the whole chain from the planted feet up.</summary>
        void ApplyLegTargets(Quaternion H, Vector3 fwd, Vector3 right, float dt, float strengthScale, Quaternion pelvisRef, Vector3 pelvisPosDesired, float blend)
        {
            var bal = body.Balance;
            var mus = body.Muscles;
            float s = body.Scale;
            Quaternion pelvisRot = pelvisRef;
            for (int i = 0; i < 2; i++)
            {
                PartId thigh = i == 0 ? PartId.ThighL : PartId.ThighR, shin = i == 0 ? PartId.ShinL : PartId.ShinR, foot = i == 0 ? PartId.FootL : PartId.FootR;
                if (!body.Has(thigh) || !body.Has(shin)) continue;
                var th = body.Part(thigh);
                Vector3 hipActual = th.rb.position;
                float hAdjust = Mathf.Clamp((body.Balance.StandHeight - bal.PelvisHeight) * 0.4f, -0.08f * s, 0.08f * s);
                Vector3 hipDesired = pelvisPosDesired + pelvisRef * ((PartInfo.Origin[(int)thigh] - PartInfo.Origin[(int)PartId.Pelvis]) * s);
                Vector3 hip = Vector3.Lerp(hipActual + Vector3.up * hAdjust, hipDesired, blend);
                Vector3 ankle;
                float yaw;
                float pitch = 0f;
                Vector3 groundN = Vector3.up;
                if (feet[i].phase == FootPhase.Swing)
                {
                    float u = Mathf.Clamp01(feet[i].t / Mathf.Max(0.01f, feet[i].dur));
                    float e = MathUtil.SmoothStep01(u);
                    Vector3 a = feet[i].lift, d = feet[i].target;
                    Vector3 dir = MathUtil.SafeNormal(MathUtil.Flat(d - a), fwd);
                    float c = feet[i].clearance * 1.35f;
                    Vector3 b = a + Vector3.up * c + dir * 0.04f * s, cc = d + Vector3.up * c - dir * 0.04f * s;
                    ankle = MathUtil.Bezier(a, b, cc, d, e);
                    Vector3 next = MathUtil.Bezier(a, b, cc, d, MathUtil.SmoothStep01(Mathf.Min(1f, u + 0.02f)));
                    feet[i].swingVel = (next - ankle) / Mathf.Max(0.001f, feet[i].dur * 0.02f);
                    feet[i].swingPos = ankle;
                    yaw = Mathf.LerpAngle(feet[i].liftYaw, feet[i].targetYaw, e);
                    pitch = u < 0.3f ? Mathf.Lerp(18f, 0f, u / 0.3f) : u > 0.75f ? Mathf.Lerp(0f, -12f, (u - 0.75f) / 0.25f) : 0f;
                }
                else
                {
                    ankle = feet[i].planted;
                    yaw = feet[i].plantedYaw;
                    if (bal.FootGrounded[i]) groundN = bal.FootPart[i].groundNormal;
                    // heel-to-toe roll during stance when walking
                    float behind = Vector3.Dot(ankle - body.Pelvis.rb.position, fwd);
                    if (DesiredVelocity.sqrMagnitude > 0.05f && behind < -0.15f * s) pitch = Mathf.Clamp(-(behind + 0.15f * s) * 60f / s, 0f, 20f);
                }
                float l1 = (PartInfo.Origin[(int)thigh] - PartInfo.Origin[(int)shin]).magnitude * s;
                float l2 = (PartInfo.Origin[(int)shin] - PartInfo.Origin[(int)foot]).magnitude * s;
                if (!body.LegHasFoot[i]) l2 *= 0.92f;
                Vector3 knee = Quaternion.Euler(0f, yaw - HeadingYaw, 0f) * fwd;
                Vector3 d1, d2, p1, p2, kneePos;
                IKSolver.TwoBone(hip, ankle, knee, l1, l2, LegReach, out d1, out d2, out p1, out p2, out kneePos);
                Quaternion thighW = Pose.FromDirs(thigh, d1, p1);
                Quaternion shinW = Pose.FromDirs(shin, d2, p2);
                mus.Set(thigh, Quaternion.Inverse(pelvisRot) * thighW);
                mus.Set(shin, Quaternion.Inverse(thighW) * shinW);
                if (body.Has(foot))
                {
                    Vector3 ff = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Vector3 fg = Vector3.ProjectOnPlane(ff, groundN).normalized;
                    Quaternion footW = Quaternion.LookRotation(fg, groundN) * Quaternion.AngleAxis(pitch, Vector3.right);
                    mus.Set(foot, Quaternion.Inverse(shinW) * footW);
                }
            }
        }

        void SwingForces(float dt)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float g = bal.G;
            for (int i = 0; i < 2; i++)
            {
                if (feet[i].phase != FootPhase.Swing) continue;
                var fp = bal.FootPart[i];
                if (fp == null) continue;
                PartId shin = i == 0 ? PartId.ShinL : PartId.ShinR;
                float m = fp.rb.mass + (body.Has(shin) && fp.id != shin ? body.Part(shin).rb.mass : 0f);
                Vector3 anchorPos = FootAnchor(i);
                Vector3 err = feet[i].swingPos - anchorPos;
                Vector3 verr = feet[i].swingVel - fp.rb.GetPointVelocity(anchorPos);
                Vector3 f = m * (ls.swingFootGain * err + ls.swingFootDamping * verr) + Vector3.up * m * g * 0.85f;
                float max = 450f * body.MassScale;
                if (f.magnitude > max) f = f.normalized * max;
                if (!MathUtil.IsFinite(f)) continue;
                fp.rb.AddForceAtPosition(f, anchorPos, ForceMode.Force);
                body.Pelvis.rb.AddForce(-f, ForceMode.Force);
            }
        }

        void ArmsAndSpine(Vector3 fwd, float runFactor)
        {
            var ls = Sandbox.Boot.locomotion;
            var mus = body.Muscles;
            var brain = body.Brain;
            float s = body.Scale;
            Vector3 hipC = body.Pelvis.rb.position;
            float legOff0 = 0f, legOff1 = 0f;
            if (body.Balance.FootPart[0] != null) legOff0 = Mathf.Clamp(Vector3.Dot(body.Balance.FootPart[0].rb.position - hipC, fwd) / (0.3f * s), -1f, 1f);
            if (body.Balance.FootPart[1] != null) legOff1 = Mathf.Clamp(Vector3.Dot(body.Balance.FootPart[1].rb.position - hipC, fwd) / (0.3f * s), -1f, 1f);
            float moving = Mathf.Clamp01(DesiredVelocity.magnitude / 0.6f);
            float swing = ls.armSwing * (1f + runFactor * 0.8f) * moving;
            float elbow = Mathf.Lerp(14f, 80f, runFactor * moving);
            float stumbleSpread = StumbleTimer > 0f ? 45f : 0f;
            float oneLeg = Mode == GaitMode.Hop ? 50f : 0f;
            // armless sides keep balance by spreading the remaining arm a little
            float lostArmComp = (body.ArmUsable[0] ? 0f : 12f) + (body.ArmUsable[1] ? 0f : 12f);
            if (!brain.ArmOverride[0])
            {
                mus.Set(PartId.UpperArmL, Pose.ArmUpperLocal(-1f, 9f + stumbleSpread + oneLeg + lostArmComp, 4f - swing * legOff0 + stumbleSpread * 0.5f, 0f));
                mus.Set(PartId.ForearmL, Pose.ElbowLocal(-1f, elbow + stumbleSpread * 0.3f, 0f));
                mus.Set(PartId.HandL, Quaternion.identity);
            }
            if (!brain.ArmOverride[1])
            {
                mus.Set(PartId.UpperArmR, Pose.ArmUpperLocal(1f, 9f + stumbleSpread + oneLeg + lostArmComp, 4f - swing * legOff1 + stumbleSpread * 0.5f, 0f));
                mus.Set(PartId.ForearmR, Pose.ElbowLocal(1f, elbow + stumbleSpread * 0.3f, 0f));
                mus.Set(PartId.HandR, Quaternion.identity);
            }
            // spine counter-rotation against the pelvis
            float counter = (legOff0 - legOff1) * ls.spineCounterRotation * moving;
            float lean = runFactor * 6f + crouchAmount * 10f;
            float breath = brain.BreathOffset;
            mus.Set(PartId.Abdomen, Pose.SpineLocal(lean * 0.4f + breath * 0.4f, counter * 0.5f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(lean * 0.3f - breath, counter * 0.5f, 0f));
        }
    }
    #endregion

    #region MotorBrain
    public enum MotorState { Stand, Fall, Lie, GetUp, Crawl, Hop, Headless, Unconscious, Dead }

    public struct PossessInput
    {
        public Vector3 move;      // world-space desired direction (magnitude 0..1)
        public float camYaw;
        public bool run, crouch, jump, reachL, reachR;
        public Vector3 aimPoint;
        public Vector3 aimDir;
    }

    public class MotorBrain
    {
        readonly RagdollBody body;
        public MotorState State = MotorState.Stand;
        public float StateTime;
        public bool Possessed;
        public PossessInput Input;
        bool jumpLatch;
        public readonly bool[] ArmOverride = new bool[2];
        public float BreathOffset;
        public Vector3 GazeTarget;
        public bool HasCommand;
        public Vector3 CommandTarget;
        Vector3 wanderTarget;
        float wanderTimer;
        int getUpAttempts;
        float getUpPhaseTime;
        int getUpPhase;
        float lieQuietTime;
        float fallImpactTime = -10f;
        float crawlTimer;
        int crawlArm;
        int crawlPhase;
        float clutchTimer, clutchCooldown;
        Wound clutchWound;
        int clutchHand = -1;
        float shieldTimer;
        Vector3 shieldFrom;
        float saccadeTimer;
        Vector3 saccade;
        float grabStruggle;
        BodyPart grabbedPart;
        readonly FixedJoint[] handGrip = new FixedJoint[2];
        float breathPhase;
        float painVoiceCooldown;
        Rng rng;

        public MotorBrain(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 13 + 5); wanderTarget = b.transform.position; }

        public void SetPossessed(bool on)
        {
            Possessed = on;
            HasCommand = false;
            if (!on) { ReleaseGrip(0); ReleaseGrip(1); }
        }

        public void CommandWalkTo(Vector3 p) { HasCommand = true; CommandTarget = p; }

        public void SetGrabbed(BodyPart part) { grabbedPart = part; grabStruggle = part != null ? 1f : 0f; }

        public void OnTopologyChanged()
        {
            if (body.Locomotion != null) body.Locomotion.ResetFeet();
            for (int s = 0; s < 2; s++) if (!body.ArmUsable[s]) ReleaseGrip(s);
        }

        void SetState(MotorState s)
        {
            if (State == s) return;
            State = s;
            StateTime = 0f;
            if (s == MotorState.Stand || s == MotorState.Hop) body.Locomotion.PlantCurrent();
            if (s == MotorState.GetUp) { getUpPhase = 0; getUpPhaseTime = 0f; }
            if (s == MotorState.Lie) lieQuietTime = 0f;
            if (s == MotorState.Crawl) { crawlPhase = 0; crawlTimer = 0f; }
        }

        bool CanStand
        {
            get
            {
                var v = body.Vitals;
                bool legs = body.LegUsable[0] && body.LegUsable[1] && !body.LegsParalyzed;
                bool bothBroken = body.Has(PartId.ThighL) && body.Has(PartId.ThighR) && (body.Part(PartId.ThighL).fractured || body.Part(PartId.ShinL).fractured) &&
                                  (body.Part(PartId.ThighR).fractured || body.Part(PartId.ShinR).fractured);
                return legs && !bothBroken && v.Consciousness > 0.5f && body.BrainPresent;
            }
        }

        bool CanHop
        {
            get
            {
                int legs = (body.LegUsable[0] ? 1 : 0) + (body.LegUsable[1] ? 1 : 0);
                return legs == 1 && !body.LegsParalyzed && body.Vitals.Consciousness > 0.6f && body.BrainPresent && Sandbox.Boot.vitals.willToLive > 0.3f;
            }
        }

        bool CanCrawl
        {
            get { return (body.ArmUsable[0] || body.ArmUsable[1]) && !body.BodyParalyzed && body.Vitals.Consciousness > 0.4f && body.BrainPresent && Sandbox.Boot.vitals.willToLive > 0.15f; }
        }

        public void FixedTick(float dt)
        {
            StateTime += dt;
            var v = body.Vitals;
            var mus = body.Muscles;
            var bal = body.Balance;
            var loc = body.Locomotion;
            mus.GlobalScale = 1f;
            mus.Brace = 0f;
            ArmOverride[0] = false; ArmOverride[1] = false;
            if (painVoiceCooldown > 0f) painVoiceCooldown -= dt;
            if (shieldTimer > 0f) shieldTimer -= dt;
            if (clutchCooldown > 0f) clutchCooldown -= dt;
            breathPhase += dt * v.BreathRate / 60f * Mathf.PI * 2f;
            BreathOffset = Mathf.Sin(breathPhase) * 1.6f * v.BreathDepth;

            // Global transitions
            if (v.Dead) SetState(MotorState.Dead);
            else if (!body.HeadAttached) SetState(MotorState.Headless);
            else if (v.Unconscious) { if (State != MotorState.Unconscious) SetState(MotorState.Unconscious); }
            else if (State == MotorState.Unconscious || State == MotorState.Dead || State == MotorState.Headless) SetState(MotorState.Lie);

            bool zeroG = bal.G < 0.5f;
            if (zeroG && (State == MotorState.Stand || State == MotorState.Hop || State == MotorState.GetUp || State == MotorState.Crawl)) SetState(MotorState.Lie);

            UpdateIntent(dt);

            switch (State)
            {
                case MotorState.Stand: TickStand(dt); break;
                case MotorState.Hop: TickHop(dt); break;
                case MotorState.Fall: TickFall(dt); break;
                case MotorState.Lie: TickLie(dt, zeroG); break;
                case MotorState.GetUp: TickGetUp(dt); break;
                case MotorState.Crawl: TickCrawl(dt); break;
                case MotorState.Headless: TickHeadless(dt); break;
                case MotorState.Unconscious: TickLimp(dt, Sandbox.Boot.muscles.unconsciousTone); break;
                case MotorState.Dead: TickLimp(dt, 1f); break;
            }
            if (State != MotorState.Dead && State != MotorState.Unconscious && State != MotorState.Headless)
            {
                Reactions(dt);
                Gaze(dt);
                if (Possessed) PossessArms(dt);
            }
            if (grabbedPart != null) Struggle(dt);
        }

        public void VisualTick(float dt) { }

        #region Intent
        void UpdateIntent(float dt)
        {
            var loc = body.Locomotion;
            var gs = Sandbox.Boot.general;
            Vector3 com = body.CenterOfMass;
            loc.Run = false; loc.Crouch = false;
            if (Possessed)
            {
                Vector3 mv = Input.move;
                loc.DesiredVelocity = mv * (Input.run ? Sandbox.Boot.locomotion.runSpeed : Sandbox.Boot.locomotion.walkSpeed);
                loc.Run = Input.run;
                loc.Crouch = Input.crouch;
                loc.DesiredYaw = mv.sqrMagnitude > 0.01f ? MathUtil.Yaw(mv) : Input.camYaw;
                if (Input.jump) jumpLatch = true;
                if (jumpLatch && (State == MotorState.Stand))
                {
                    loc.RequestJump();
                    jumpLatch = false;
                }
                if (State != MotorState.Stand) jumpLatch = false;
                Input.jump = false;
                return;
            }
            Vector3 target = Vector3.zero;
            bool go = false;
            if (HasCommand)
            {
                target = CommandTarget;
                go = true;
                if (MathUtil.Flat(target - com).magnitude < 0.45f * body.Scale) { HasCommand = false; go = false; }
            }
            else if (gs.wander && State == MotorState.Stand)
            {
                wanderTimer -= dt;
                if (wanderTimer <= 0f)
                {
                    wanderTimer = rng.Range(5f, 11f);
                    Vector3 center = Sandbox.World != null ? Sandbox.World.RagdollSpawn : body.transform.position;
                    Vector2 r = rng.InsideUnitCircle() * 9f;
                    wanderTarget = center + new Vector3(r.x, 0f, r.y);
                }
                if (MathUtil.Flat(wanderTarget - com).magnitude > 0.6f) { target = wanderTarget; go = true; }
            }
            if (go)
            {
                Vector3 d = MathUtil.Flat(target - com);
                float dist = d.magnitude;
                Vector3 dir = dist > 1e-3f ? d / dist : Vector3.zero;
                float speed = Mathf.Min(Sandbox.Boot.locomotion.walkSpeed, dist * 1.2f);
                loc.DesiredVelocity = dir * speed;
                loc.DesiredYaw = MathUtil.Yaw(dir);
            }
            else
            {
                loc.DesiredVelocity = Vector3.zero;
                // idle: turn to face the player when they walk around us
                if (gs.gazeAtPlayer && Sandbox.MainCamera != null)
                {
                    Vector3 toCam = MathUtil.Flat(Sandbox.MainCamera.transform.position - com);
                    if (toCam.magnitude < 9f && toCam.magnitude > 0.5f)
                    {
                        float yaw = MathUtil.Yaw(toCam);
                        if (Mathf.Abs(Mathf.DeltaAngle(loc.HeadingYaw, yaw)) > 65f) loc.DesiredYaw = yaw;
                    }
                }
            }
        }
        #endregion

        #region States
        void TickStand(float dt)
        {
            var bal = body.Balance;
            var loc = body.Locomotion;
            var v = body.Vitals;
            if (!CanStand)
            {
                if (CanHop) { SetState(MotorState.Hop); return; }
                SetState(MotorState.Fall);
                return;
            }
            loc.Mode = GaitMode.Normal;
            loc.LegReach = LegReachFactor();
            float strengthScale = Mathf.Clamp01(body.Muscles.ActiveLevel * v.MuscleFactor * (1f - v.Dizziness * 0.4f));
            bool supported = loc.Tick(dt, strengthScale);
            bool fallen = !bal.Upright && !loc.Jumping;
            bool lostSupport = bal.SupportCount == 0 && !loc.Jumping && StateTime > 0.3f && bal.PelvisHeight < bal.StandHeight * 0.75f;
            bool unrecoverable = bal.SupportCount > 0 && StateTime > 0.5f && bal.SupportDistance > 0.55f * body.Scale && !loc.IsSwinging(0) && !loc.IsSwinging(1);
            if (fallen || lostSupport || unrecoverable || !supported || body.Muscles.Stun > 0.25f)
                SetState(MotorState.Fall);
            if (v.Dizziness > 0.6f && rng.Value < dt * 0.2f) SetState(MotorState.Fall);
        }

        void TickHop(float dt)
        {
            var loc = body.Locomotion;
            var bal = body.Balance;
            if (CanStand) { SetState(MotorState.Stand); return; }
            if (!CanHop) { SetState(MotorState.Fall); return; }
            loc.Mode = GaitMode.Hop;
            loc.LegReach = LegReachFactor();
            float strengthScale = Mathf.Clamp01(body.Muscles.ActiveLevel * body.Vitals.MuscleFactor);
            loc.Tick(dt, strengthScale);
            if (!bal.Upright && StateTime > 0.4f) SetState(MotorState.Fall);
            if (bal.SupportDistance > 0.6f * body.Scale && StateTime > 0.5f) SetState(MotorState.Fall);
        }

        float LegReachFactor()
        {
            float r = 1f;
            for (int s = 0; s < 2; s++)
            {
                PartId thigh = s == 0 ? PartId.ThighL : PartId.ThighR, shin = s == 0 ? PartId.ShinL : PartId.ShinR;
                if (body.Has(thigh) && body.Part(thigh).fractured) r = Mathf.Min(r, 0.7f);
                if (body.Has(shin) && body.Part(shin).fractured) r = Mathf.Min(r, 0.75f);
            }
            return r;
        }

        void TickFall(float dt)
        {
            var mus = body.Muscles;
            var bal = body.Balance;
            // Tuck and protect the head with the arms, then go limp just before / at impact.
            bool impact = Time.time - fallImpactTime < 0.6f;
            mus.GlobalScale = impact ? 0.3f : 0.6f;
            mus.Brace = impact ? 0f : 0.4f;
            ProtectHead(0); ProtectHead(1);
            mus.Set(PartId.Neck, Pose.SpineLocal(25f, 0f, 0f));
            mus.Set(PartId.Head, Pose.SpineLocal(15f, 0f, 0f));
            mus.Set(PartId.Abdomen, Pose.SpineLocal(15f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(10f, 0f, 0f));
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                mus.Set(s == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 30f, 8f, 0f));
                mus.Set(s == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(40f));
                mus.Set(s == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(10f, 0f));
            }
            float speed = body.ComVelocity.magnitude;
            if (TorsoOnGround()) { if (fallImpactTime < Time.time - 2f) fallImpactTime = Time.time; }
            if ((TorsoOnGround() && speed < 1.2f && StateTime > 0.4f) || StateTime > 3f) SetState(MotorState.Lie);
        }

        bool TorsoOnGround()
        {
            return (body.Has(PartId.Chest) && body.Chest.Touching) || (body.Has(PartId.Pelvis) && body.Pelvis.Touching) ||
                   (body.Has(PartId.Abdomen) && body.Part(PartId.Abdomen).Touching) || (body.Has(PartId.Head) && body.Head.Touching);
        }

        void ProtectHead(int s)
        {
            if (!body.ArmUsable[s]) return;
            float side = s == 0 ? -1f : 1f;
            ArmOverride[s] = true;
            var mus = body.Muscles;
            mus.Set(s == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 25f, 115f, side * 20f));
            mus.Set(s == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 125f, 0f));
            mus.Set(s == 0 ? PartId.HandL : PartId.HandR, Pose.WristLocal(side, 10f, 0f));
        }

        void TickLie(float dt, bool zeroG)
        {
            var mus = body.Muscles;
            var bs = Sandbox.Boot.balance;
            float speed = body.ComVelocity.magnitude;
            if (speed < 0.6f) lieQuietTime += dt; else lieQuietTime = 0f;
            mus.GlobalScale = zeroG ? 0.6f : 0.4f;
            RelaxedPose(zeroG ? 0.4f : 0f);
            if (zeroG) return;
            float will = Sandbox.Boot.vitals.willToLive;
            float delay = bs.getUpDelay * Mathf.Lerp(2f, 0.7f, will) * (1f + body.Vitals.Pain);
            if (lieQuietTime < delay) return;
            if (CanStand && getUpAttempts < bs.getUpAttempts) { getUpAttempts++; SetState(MotorState.GetUp); return; }
            if (CanHop && getUpAttempts < bs.getUpAttempts) { getUpAttempts++; SetState(MotorState.GetUp); return; }
            if (CanCrawl && (!CanStand || getUpAttempts >= bs.getUpAttempts)) { SetState(MotorState.Crawl); return; }
            // regain attempts slowly while resting
            if (StateTime > 12f && getUpAttempts > 0) { getUpAttempts--; StateTime = 0f; }
        }

        void RelaxedPose(float floatiness)
        {
            var mus = body.Muscles;
            if (!ArmOverride[0]) { mus.Set(PartId.UpperArmL, Pose.ArmUpperLocal(-1f, 18f + floatiness * 40f, 10f + floatiness * 30f, 0f)); mus.Set(PartId.ForearmL, Pose.ElbowLocal(-1f, 25f, 0f)); mus.Set(PartId.HandL, Quaternion.identity); }
            if (!ArmOverride[1]) { mus.Set(PartId.UpperArmR, Pose.ArmUpperLocal(1f, 18f + floatiness * 40f, 10f + floatiness * 30f, 0f)); mus.Set(PartId.ForearmR, Pose.ElbowLocal(1f, 25f, 0f)); mus.Set(PartId.HandR, Quaternion.identity); }
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                mus.Set(s == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 12f + floatiness * 30f, 6f, 0f));
                mus.Set(s == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(15f + floatiness * 40f));
                mus.Set(s == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(15f, 0f));
            }
            mus.Set(PartId.Abdomen, Pose.SpineLocal(BreathOffset * 0.4f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(-BreathOffset, 0f, 0f));
        }

        void TickLimp(float dt, float scale)
        {
            var mus = body.Muscles;
            mus.GlobalScale = 1f;
            RelaxedPose(0f);
            mus.Set(PartId.Neck, Quaternion.identity);
            mus.Set(PartId.Head, Quaternion.identity);
        }

        void TickHeadless(float dt)
        {
            var mus = body.Muscles;
            var v = body.Vitals;
            // Erratic motor spasm that fades with blood loss.
            float drive = v.HeadlessDrive;
            mus.GlobalScale = 0.7f;
            mus.Spasm(drive * 1.1f);
            RelaxedPose(0f);
            if (State == MotorState.Headless && body.Balance.Upright && body.LegUsable[0] && body.LegUsable[1] && drive > 0.5f)
            {
                body.Locomotion.DesiredVelocity = body.Locomotion.Heading * Vector3.forward * 0.6f * drive;
                body.Locomotion.Tick(dt, drive * 0.6f);
            }
        }

        /// <summary>Recovery: roll to prone, push up with the arms, bring the feet under, stand up. Time limited.</summary>
        void TickGetUp(float dt)
        {
            var bs = Sandbox.Boot.balance;
            var mus = body.Muscles;
            var bal = body.Balance;
            var pelvis = body.Pelvis;
            var chest = body.Chest;
            float s = body.Scale;
            getUpPhaseTime += dt;
            if (!CanStand && !CanHop) { SetState(CanCrawl ? MotorState.Crawl : MotorState.Lie); return; }
            if (StateTime > bs.getUpTimeout) { SetState(MotorState.Lie); return; }
            float c = body.Vitals.Consciousness;
            float weight = body.TotalMass * bal.G;
            float assist = bs.getUpAssist * weight * c * Mathf.Lerp(0.4f, 1f, body.Vitals.MuscleFactor) * Sandbox.Boot.vitals.willToLive / 0.7f;
            Vector3 chestFwd = chest.rb.rotation * Vector3.forward;
            Vector3 chestUp = chest.rb.rotation * Vector3.up;
            Vector3 bodyAxis = MathUtil.SafeNormal(chest.rb.position - pelvis.rb.position, Vector3.up);
            Vector3 headingDir = MathUtil.SafeNormal(MathUtil.Flat(bodyAxis), MathUtil.Flat(chestUp));
            if (headingDir.sqrMagnitude < 0.01f) headingDir = Vector3.forward;
            float chestH = chest.rb.worldCenterOfMass.y - bal.GroundHeight;
            float pelvisH = pelvis.rb.worldCenterOfMass.y - bal.GroundHeight;
            mus.GlobalScale = 1f;
            mus.Brace = 0.3f;

            switch (getUpPhase)
            {
                case 0: // roll to prone
                    {
                        float faceUp = Vector3.Dot(chestFwd, Vector3.up);
                        if (faceUp < -0.35f || (chestH > 0.3f * s && bal.Tilt < 70f)) { NextGetUpPhase(); break; }
                        Vector3 rollAxis = bodyAxis;
                        float dir = Vector3.Dot(Vector3.Cross(chestFwd, Vector3.down), rollAxis) >= 0f ? 1f : -1f;
                        bal.Assist(chest.rb, rollAxis * dir * 140f * body.MassScale, 1.2f);
                        bal.Assist(pelvis.rb, rollAxis * dir * 90f * body.MassScale, 1.2f);
                        int reach = dir > 0f ? 1 : 0;
                        ArmOverride[reach] = true;
                        float side = reach == 0 ? -1f : 1f;
                        mus.Set(reach == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, -20f, 100f, 0f));
                        mus.Set(reach == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 20f, 0f));
                        int leg = 1 - reach;
                        mus.Set(leg == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(leg == 0 ? -1f : 1f, 60f, -15f, 0f));
                        mus.Set(leg == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(80f));
                        if (getUpPhaseTime > 2.2f) NextGetUpPhase();
                        break;
                    }
                case 1: // push up with the arms
                    {
                        PushUpArms(headingDir, 0.5f);
                        mus.Set(PartId.Neck, Pose.SpineLocal(-30f, 0f, 0f));
                        mus.Set(PartId.Head, Pose.SpineLocal(-20f, 0f, 0f));
                        float target = 0.5f * s;
                        float lift = HandSupportLift(chest, target, chestH, assist);
                        Quaternion chestTarget = Quaternion.LookRotation(headingDir, Vector3.up) * Quaternion.Euler(55f, 0f, 0f);
                        bal.Assist(chest.rb, BalanceController.UprightTorque(chest.rb, chestTarget, 300f, 40f), 1.3f);
                        LegsTuck(0.4f);
                        if (chestH > 0.42f * s && getUpPhaseTime > 0.4f) NextGetUpPhase();
                        if (getUpPhaseTime > 2.6f) NextGetUpPhase();
                        break;
                    }
                case 2: // knees under the hips (kneel)
                    {
                        PushUpArms(headingDir, 0.8f);
                        LegsTuck(1f);
                        float lift = HandSupportLift(chest, 0.62f * s, chestH, assist);
                        // pelvis up, reacting against shins/knees on the ground
                        float fy = body.TotalMass * 0.45f * (bal.G * 0.6f + 120f * (0.5f * s - pelvisH) - 15f * Compat.GetVelocity(pelvis.rb).y);
                        fy = Mathf.Clamp(fy, 0f, weight * 0.9f);
                        int pushers = 0;
                        for (int k = 0; k < 2; k++) { var sh = body.Part(k == 0 ? PartId.ShinL : PartId.ShinR); if (sh != null && sh.attached && sh.Touching) pushers++; }
                        if (pushers > 0)
                        {
                            for (int k = 0; k < 2; k++)
                            {
                                var sh = body.Part(k == 0 ? PartId.ShinL : PartId.ShinR);
                                if (sh != null && sh.attached && sh.Touching) BalanceController.PairForce(pelvis.rb, Vector3.up * fy / pushers, sh.rb);
                            }
                        }
                        pelvis.rb.AddForce(Vector3.up * Mathf.Min(assist * 0.5f, Mathf.Max(0f, fy * 0.5f)), ForceMode.Force);
                        Quaternion pTarget = Quaternion.LookRotation(headingDir, Vector3.up) * Quaternion.Euler(20f, 0f, 0f);
                        bal.Assist(pelvis.rb, BalanceController.UprightTorque(pelvis.rb, pTarget, 260f, 35f), 1.3f);
                        if (pelvisH > 0.42f * s && getUpPhaseTime > 0.5f) NextGetUpPhase();
                        if (getUpPhaseTime > 2.8f) NextGetUpPhase();
                        break;
                    }
                case 3: // rise to standing
                    {
                        float u = Mathf.Clamp01(getUpPhaseTime / 1.4f);
                        float hTarget = Mathf.Lerp(0.55f * s, bal.StandHeight, u);
                        Quaternion H = Quaternion.LookRotation(headingDir, Vector3.up);
                        Quaternion pTarget = H * Quaternion.Euler(Mathf.Lerp(30f, 0f, u), 0f, 0f);
                        Quaternion cTarget = H * Quaternion.Euler(Mathf.Lerp(45f, 0f, u), 0f, 0f);
                        for (int k = 0; k < 2; k++)
                        {
                            float side = k == 0 ? -1f : 1f;
                            float flex = Mathf.Lerp(95f, 10f, u), knee = Mathf.Lerp(120f, 10f, u);
                            mus.Set(k == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, flex, 6f, 0f));
                            mus.Set(k == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(knee));
                            mus.Set(k == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(Mathf.Lerp(-20f, 0f, u), 0f));
                        }
                        mus.Set(PartId.Abdomen, Pose.SpineLocal(Mathf.Lerp(25f, 0f, u), 0f, 0f));
                        mus.Set(PartId.Chest, Pose.SpineLocal(Mathf.Lerp(20f, 0f, u), 0f, 0f));
                        ArmsForward(Mathf.Lerp(60f, 10f, u));
                        float fy = body.TotalMass * (bal.G + 140f * (hTarget - pelvisH) - 20f * Compat.GetVelocity(pelvis.rb).y);
                        fy = Mathf.Clamp(fy, 0f, weight * 1.6f) * Mathf.Clamp01(Mathf.Min(body.Muscles.LegStrength(0), body.Muscles.LegStrength(1)) * 1.3f + 0.2f);
                        int feetDown = 0;
                        for (int k = 0; k < 2; k++) if (bal.FootPart[k] != null && bal.FootPart[k].Touching) feetDown++;
                        if (feetDown > 0)
                        {
                            for (int k = 0; k < 2; k++)
                                if (bal.FootPart[k] != null && bal.FootPart[k].Touching) BalanceController.PairForce(pelvis.rb, Vector3.up * fy / feetDown, bal.FootPart[k].rb);
                        }
                        pelvis.rb.AddForce(Vector3.up * Mathf.Min(assist, fy), ForceMode.Force);
                        bal.Assist(pelvis.rb, BalanceController.UprightTorque(pelvis.rb, pTarget, 600f, 70f), 1.5f);
                        bal.Assist(chest.rb, BalanceController.UprightTorque(chest.rb, cTarget, 450f, 50f), 1.5f);
                        if (pelvisH > bal.StandHeight * 0.86f && bal.Tilt < 30f)
                        {
                            body.Locomotion.HeadingYaw = MathUtil.Yaw(headingDir);
                            body.Locomotion.DesiredYaw = body.Locomotion.HeadingYaw;
                            SetState(CanStand ? MotorState.Stand : MotorState.Hop);
                            getUpAttempts = 0;
                        }
                        if (getUpPhaseTime > 3.5f) SetState(MotorState.Lie);
                        break;
                    }
            }
        }

        void NextGetUpPhase() { getUpPhase++; getUpPhaseTime = 0f; }

        void PushUpArms(Vector3 heading, float extension)
        {
            var chest = body.Chest;
            float s = body.Scale;
            Vector3 right = new Vector3(heading.z, 0f, -heading.x);
            for (int k = 0; k < 2; k++)
            {
                if (!body.ArmUsable[k]) continue;
                float side = k == 0 ? -1f : 1f;
                PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                Vector3 shoulder = body.Part(up).rb.position;
                Vector3 hand = new Vector3(shoulder.x, body.Balance.GroundHeight + 0.05f * s, shoulder.z) + heading * 0.12f * s + right * side * 0.12f * s;
                ArmIK(k, hand, Vector3.up * 0.3f + right * side - heading * 0.2f, 1f, 1.2f + extension);
            }
        }

        float HandSupportLift(BodyPart chest, float target, float current, float assist)
        {
            var bal = body.Balance;
            float upper = body.TotalMass * 0.55f;
            float fy = upper * (bal.G * 0.7f + 110f * (target - current) - 14f * Compat.GetVelocity(chest.rb).y);
            fy = Mathf.Clamp(fy, 0f, upper * bal.G * 1.4f);
            int hands = 0;
            for (int k = 0; k < 2; k++)
            {
                if (!body.ArmUsable[k]) continue;
                var h = body.Part(k == 0 ? PartId.HandL : PartId.HandR);
                var fa = body.Part(k == 0 ? PartId.ForearmL : PartId.ForearmR);
                if ((h != null && h.attached && h.Touching) || (fa != null && fa.Touching)) hands++;
            }
            float armStr = Mathf.Max(body.Muscles.ArmStrength(0), body.Muscles.ArmStrength(1));
            fy *= Mathf.Clamp01(armStr * 1.4f + 0.15f);
            if (hands > 0)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (!body.ArmUsable[k]) continue;
                    var h = body.Part(k == 0 ? PartId.HandL : PartId.HandR);
                    var fa = body.Part(k == 0 ? PartId.ForearmL : PartId.ForearmR);
                    BodyPart contact = h != null && h.attached && h.Touching ? h : (fa != null && fa.Touching ? fa : null);
                    if (contact != null) BalanceController.PairForce(chest.rb, Vector3.up * fy / hands, contact.rb);
                }
            }
            chest.rb.AddForce(Vector3.up * Mathf.Min(assist * 0.5f, fy * 0.6f), ForceMode.Force);
            return fy;
        }

        void LegsTuck(float amount)
        {
            var mus = body.Muscles;
            for (int k = 0; k < 2; k++)
            {
                float side = k == 0 ? -1f : 1f;
                mus.Set(k == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, Mathf.Lerp(20f, 95f, amount), 10f, 0f));
                mus.Set(k == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(Mathf.Lerp(30f, 125f, amount)));
                mus.Set(k == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(30f, 0f));
            }
            mus.Set(PartId.Abdomen, Pose.SpineLocal(20f * amount, 0f, 0f));
        }

        void ArmsForward(float flex)
        {
            var mus = body.Muscles;
            for (int k = 0; k < 2; k++)
            {
                if (ArmOverride[k]) continue;
                float side = k == 0 ? -1f : 1f;
                ArmOverride[k] = true;
                mus.Set(k == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 20f, flex, 0f));
                mus.Set(k == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 30f, 0f));
            }
        }

        /// <summary>Army crawl with a procedural arm step planner: reach, plant, pull.</summary>
        void TickCrawl(float dt)
        {
            var mus = body.Muscles;
            var bal = body.Balance;
            var chest = body.Chest;
            var pelvis = body.Pelvis;
            float s = body.Scale;
            if (CanStand && body.Vitals.Consciousness > 0.6f && StateTime > 3f) { SetState(MotorState.GetUp); return; }
            if (!CanCrawl) { SetState(MotorState.Lie); return; }
            Vector3 chestFwd = chest.rb.rotation * Vector3.forward;
            // must be prone: roll over first
            if (Vector3.Dot(chestFwd, Vector3.up) > 0.2f)
            {
                Vector3 axis = MathUtil.SafeNormal(chest.rb.position - pelvis.rb.position, Vector3.forward);
                bal.Assist(chest.rb, axis * 120f * body.MassScale, 1.2f);
                return;
            }
            Vector3 bodyDir = MathUtil.SafeNormal(MathUtil.Flat(chest.rb.position - pelvis.rb.position), MathUtil.Flat(chest.rb.rotation * Vector3.up));
            Vector3 goal = Possessed ? Input.move : (HasCommand ? MathUtil.Flat(CommandTarget - chest.rb.position) :
                (Sandbox.MainCamera != null ? MathUtil.Flat(Sandbox.MainCamera.transform.position - chest.rb.position) : bodyDir));
            Vector3 dir = goal.sqrMagnitude > 0.04f ? goal.normalized : Vector3.zero;
            if (!Possessed && goal.magnitude < 1.2f) dir = Vector3.zero;
            // steer: bounded yaw assist toward the goal
            if (dir.sqrMagnitude > 0f)
            {
                float yawErr = Vector3.SignedAngle(bodyDir, dir, Vector3.up);
                bal.Assist(chest.rb, Vector3.up * Mathf.Clamp(yawErr, -40f, 40f) * 2.2f * body.MassScale, 0.8f);
            }
            else dir = bodyDir;
            mus.Set(PartId.Neck, Pose.SpineLocal(-35f, 0f, 0f));
            mus.Set(PartId.Head, Pose.SpineLocal(-25f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(-8f, 0f, 0f));
            mus.Set(PartId.Abdomen, Pose.SpineLocal(-5f, 0f, 0f));
            // legs: limp if paralysed / missing, otherwise frog pushes
            for (int k = 0; k < 2; k++)
            {
                float side = k == 0 ? -1f : 1f;
                bool pushPhase = (crawlArm == k) && crawlPhase == 1;
                mus.Set(k == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, pushPhase ? 50f : 10f, 25f, 0f));
                mus.Set(k == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(pushPhase ? 70f : 15f));
            }
            int arm = crawlArm;
            if (!body.ArmUsable[arm]) arm = 1 - arm;
            if (!body.ArmUsable[arm]) return;
            crawlTimer += dt;
            float side2 = arm == 0 ? -1f : 1f;
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            PartId upId = arm == 0 ? PartId.UpperArmL : PartId.UpperArmR;
            Vector3 shoulder = body.Part(upId).rb.position;
            var hand = body.Part(arm == 0 ? PartId.HandL : PartId.HandR);
            var fore = body.Part(arm == 0 ? PartId.ForearmL : PartId.ForearmR);
            float strength = body.Muscles.ArmStrength(arm);
            if (crawlPhase == 0)
            {
                Vector3 reach = new Vector3(shoulder.x, bal.GroundHeight + 0.04f * s, shoulder.z) + dir * 0.45f * s + right * side2 * 0.12f * s;
                ArmIK(arm, reach, Vector3.up + right * side2, 0.9f, 1f);
                if (crawlTimer > 0.55f) { crawlPhase = 1; crawlTimer = 0f; }
            }
            else
            {
                Vector3 pull = new Vector3(shoulder.x, bal.GroundHeight + 0.04f * s, shoulder.z) - dir * 0.1f * s + right * side2 * 0.18f * s;
                ArmIK(arm, pull, Vector3.up + right * side2 - dir, 1f, 1.3f);
                BodyPart contact = hand != null && hand.attached && hand.Touching ? hand : (fore != null && fore.Touching ? fore : null);
                float f = Sandbox.Boot.locomotion.crawlForce * Mathf.Clamp01(strength * 1.3f + 0.1f) * body.MassScale * Mathf.Lerp(0.5f, 1f, Sandbox.Boot.vitals.willToLive);
                if (contact != null)
                {
                    BalanceController.PairForce(chest.rb, dir * f + Vector3.up * f * 0.3f, contact.rb);
                }
                if (crawlTimer > 0.65f)
                {
                    crawlPhase = 0; crawlTimer = 0f;
                    if (body.ArmUsable[1 - arm]) crawlArm = 1 - arm;
                    if (Sandbox.Audio != null && rng.Value < 0.3f) Sandbox.Audio.PlayVoice(Sfx.Grunt1, chest.rb.position, 0.35f, rng.Range(0.9f, 1.1f));
                }
            }
            int other = 1 - arm;
            if (body.ArmUsable[other] && !ArmOverride[other])
            {
                Vector3 sh = body.Part(other == 0 ? PartId.UpperArmL : PartId.UpperArmR).rb.position;
                ArmIK(other, new Vector3(sh.x, bal.GroundHeight + 0.05f * s, sh.z) + dir * 0.2f * s + right * (other == 0 ? -1f : 1f) * 0.15f * s,
                    Vector3.up, 0.9f, 0.8f);
            }
        }
        #endregion

        #region Arms, reactions, gaze
        /// <summary>Arm IK: drives shoulder/elbow/wrist targets so the hand reaches 'target'. elbowHint is the elbow direction.</summary>
        public void ArmIK(int k, Vector3 target, Vector3 elbowHint, float reach, float stiffness)
        {
            if (!body.ArmUsable[k]) return;
            PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR, fore = k == 0 ? PartId.ForearmL : PartId.ForearmR, hand = k == 0 ? PartId.HandL : PartId.HandR;
            var chest = body.Chest;
            var upper = body.Part(up);
            float s = body.Scale;
            float l1 = (PartInfo.Origin[(int)fore] - PartInfo.Origin[(int)up]).magnitude * s;
            float l2 = (PartInfo.Origin[(int)hand] - PartInfo.Origin[(int)fore]).magnitude * s + 0.07f * s;
            Vector3 d1, d2, p1, p2, elbow;
            float r = reach * Mathf.Lerp(0.75f, 1f, upper.HealthFraction);
            IKSolver.TwoBone(upper.rb.position, target, elbowHint, l1, l2, r, out d1, out d2, out p1, out p2, out elbow);
            Quaternion upW = Pose.FromDirs(up, d1, p1);
            Quaternion foreW = Pose.FromDirs(fore, d2, p2);
            var mus = body.Muscles;
            mus.Set(up, Quaternion.Inverse(chest.rb.rotation) * upW, stiffness);
            mus.Set(fore, Quaternion.Inverse(upW) * foreW, stiffness);
            mus.Set(hand, Quaternion.identity, stiffness);
            ArmOverride[k] = true;
        }

        void Reactions(float dt)
        {
            if (!Sandbox.Boot.general.reactions) return;
            var mus = body.Muscles;
            // Shield head after a nearby explosion
            if (shieldTimer > 0f && State != MotorState.GetUp && State != MotorState.Crawl)
            {
                ProtectHead(0); ProtectHead(1);
                mus.Brace = Mathf.Max(mus.Brace, 0.6f);
                return;
            }
            // Clutch a bleeding wound with a free hand
            if (State == MotorState.Stand || State == MotorState.Lie || State == MotorState.Hop)
            {
                if (clutchTimer > 0f)
                {
                    clutchTimer -= dt;
                    if (clutchWound == null || !clutchWound.Active || clutchWound.part == null || !clutchWound.part.attached || clutchHand < 0 || !body.ArmUsable[clutchHand]) clutchTimer = 0f;
                    else
                    {
                        Vector3 p = clutchWound.WorldPosition + clutchWound.WorldNormal * 0.035f * body.Scale;
                        float side = clutchHand == 0 ? -1f : 1f;
                        ArmIK(clutchHand, p, Vector3.down + body.Chest.rb.rotation * (Vector3.right * side) * 0.6f + Vector3.back * 0.3f, 1f, 1f);
                        if (clutchWound.part != null) clutchWound.part.bloodiness = 1f;
                        var hp = body.Part(clutchHand == 0 ? PartId.HandL : PartId.HandR);
                        if (hp != null) hp.bloodiness = 1f;
                    }
                    if (clutchTimer <= 0f) clutchCooldown = rng.Range(2f, 5f);
                }
                else if (clutchCooldown <= 0f)
                {
                    Wound w = body.Wounds.WorstClutchable();
                    if (w != null)
                    {
                        int hand = PickClutchHand(w);
                        if (hand >= 0) { clutchWound = w; clutchHand = hand; clutchTimer = rng.Range(3f, 7f); }
                        else clutchCooldown = 3f;
                    }
                    else clutchCooldown = 1.5f;
                }
            }
        }

        int PickClutchHand(Wound w)
        {
            if (w.part == null) return -1;
            PartId id = w.part.id;
            bool leftArmPart = id == PartId.UpperArmL || id == PartId.ForearmL || id == PartId.HandL;
            bool rightArmPart = id == PartId.UpperArmR || id == PartId.ForearmR || id == PartId.HandR;
            if (leftArmPart) return body.ArmUsable[1] ? 1 : -1;
            if (rightArmPart) return body.ArmUsable[0] ? 0 : -1;
            if (PartInfo.IsLeg(id) && State == MotorState.Stand && (id == PartId.ShinL || id == PartId.ShinR || id == PartId.FootL || id == PartId.FootR)) return -1;
            float side = PartInfo.SideSign(id);
            int pref = side < 0f ? 0 : side > 0f ? 1 : (rng.Value < 0.5f ? 0 : 1);
            if (body.ArmUsable[pref]) return pref;
            if (body.ArmUsable[1 - pref]) return 1 - pref;
            return -1;
        }

        void Gaze(float dt)
        {
            var head = body.Head;
            var chest = body.Chest;
            if (head == null || !head.attached || chest == null) return;
            saccadeTimer -= dt;
            if (saccadeTimer <= 0f) { saccadeTimer = rng.Range(0.8f, 2.8f); saccade = rng.OnUnitSphere() * 0.25f; }
            Vector3 eye = head.rb.position + head.rb.rotation * (new Vector3(0f, 0.08f, 0.08f) * body.Scale);
            Vector3 target;
            if (Possessed) target = Input.aimPoint;
            else if (shieldTimer > 0f) target = shieldFrom;
            else if (clutchTimer > 0f && clutchWound != null) target = clutchWound.WorldPosition;
            else if (State == MotorState.Crawl) target = eye + MathUtil.Flat(chest.rb.rotation * Vector3.up) * 2f;
            else if (Sandbox.Boot.general.gazeAtPlayer && Sandbox.MainCamera != null && (Sandbox.MainCamera.transform.position - eye).magnitude < 12f)
                target = Sandbox.MainCamera.transform.position + saccade;
            else target = eye + body.Locomotion.Heading * Vector3.forward * 3f + saccade * 3f;
            GazeTarget = target;
            Quaternion neck, headL;
            IKSolver.LookAt(chest.rb.rotation, eye, target, 70f, 40f, out neck, out headL);
            if (State == MotorState.Fall || State == MotorState.GetUp) return;
            body.Muscles.Set(PartId.Neck, neck);
            body.Muscles.Set(PartId.Head, headL);
        }

        public void OnImpact(BodyPart part, float impulse, Vector3 dir)
        {
            if (!Sandbox.Boot.general.reactions || body.Vitals.Dead || body.Vitals.Unconscious) return;
            var mus = body.Muscles;
            float k = Mathf.Clamp01(impulse / 80f);
            mus.Flinch(k);
            if (impulse > 70f) mus.StunFor(Mathf.Lerp(0.2f, 0.9f, Mathf.Clamp01((impulse - 70f) / 150f)));
            if (painVoiceCooldown <= 0f && impulse > 25f && Sandbox.Audio != null && body.HeadAttached)
            {
                painVoiceCooldown = rng.Range(0.6f, 1.4f);
                Sfx v = impulse > 90f ? (rng.Value < 0.5f ? Sfx.Pain0 : Sfx.Pain1) : (rng.Value < 0.33f ? Sfx.Grunt0 : rng.Value < 0.5f ? Sfx.Grunt1 : Sfx.Grunt2);
                Sandbox.Audio.PlayVoice(v, body.Head.rb.position, Mathf.Lerp(0.5f, 1f, k), rng.Range(0.92f, 1.08f));
            }
        }

        public void OnPain(float amount)
        {
            if (!Sandbox.Boot.general.reactions || body.Vitals.Dead || body.Vitals.Unconscious || !body.HeadAttached) return;
            if (painVoiceCooldown > 0f || Sandbox.Audio == null) return;
            painVoiceCooldown = rng.Range(0.8f, 1.8f);
            Sfx v = amount > 30f ? (rng.Value < 0.5f ? Sfx.Pain0 : Sfx.Pain1) : amount > 12f ? Sfx.Grunt2 : Sfx.Gasp;
            Sandbox.Audio.PlayVoice(v, body.Head.rb.position, Mathf.Clamp01(0.4f + amount / 50f), rng.Range(0.92f, 1.1f));
        }

        public void OnExplosionNearby(Vector3 pos, float radius)
        {
            if (body.Vitals.Dead || body.Vitals.Unconscious) return;
            shieldTimer = 1.2f;
            shieldFrom = pos;
            body.Muscles.Flinch(1f);
            body.Vitals.AddAdrenaline(0.35f);
        }

        void PossessArms(float dt)
        {
            for (int k = 0; k < 2; k++)
            {
                bool want = k == 0 ? Input.reachL : Input.reachR;
                if (!want) { ReleaseGrip(k); continue; }
                if (!body.ArmUsable[k]) continue;
                PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                Vector3 shoulder = body.Part(up).rb.position;
                float armLen = 0.62f * body.Scale;
                Vector3 to = Input.aimPoint - shoulder;
                Vector3 target = to.magnitude < armLen * 1.15f ? Input.aimPoint : shoulder + MathUtil.SafeNormal(Input.aimDir, to.normalized) * armLen;
                float side = k == 0 ? -1f : 1f;
                ArmIK(k, target, Vector3.down + body.Chest.rb.rotation * (Vector3.right * side), 1f, 1.4f);
                TryGrip(k);
            }
        }

        void TryGrip(int k)
        {
            if (handGrip[k] != null) return;
            var hand = body.Part(k == 0 ? PartId.HandL : PartId.HandR);
            if (hand == null || !hand.attached || !hand.Touching || hand.contactCollider == null) return;
            BodyPart other = Registry.PartOf(hand.contactCollider);
            if (other != null && other.body == body) return;
            var fj = hand.gameObject.AddComponent<FixedJoint>();
            if (hand.contactBody != null) fj.connectedBody = hand.contactBody;
            fj.breakForce = 2200f * body.MassScale * Mathf.Clamp01(body.Muscles.ArmStrength(k) + 0.2f);
            fj.breakTorque = 900f * body.MassScale;
            fj.enablePreprocessing = false;
            handGrip[k] = fj;
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Thump, hand.rb.position, 0.25f, 1.3f);
        }

        void ReleaseGrip(int k)
        {
            if (handGrip[k] != null) { UnityEngine.Object.Destroy(handGrip[k]); }
            handGrip[k] = null;
        }

        void Struggle(float dt)
        {
            if (grabbedPart == null || !grabbedPart.attached) { grabbedPart = null; return; }
            var v = body.Vitals;
            if (v.Dead || v.Unconscious || !body.BrainPresent) return;
            // conscious bodies fight back: reach for the grabbed limb and kick
            float k = Mathf.Clamp01(v.Consciousness) * Sandbox.Boot.vitals.willToLive;
            if (k <= 0.05f) return;
            body.Muscles.Brace = Mathf.Max(body.Muscles.Brace, 0.5f);
            int freeArm = PartInfo.IsArm(grabbedPart.id) ? (PartInfo.IsLeft(grabbedPart.id) ? 1 : 0) : (rng.Value < 0.5f ? 0 : 1);
            if (body.ArmUsable[freeArm] && State != MotorState.Stand) ArmIK(freeArm, grabbedPart.rb.position, Vector3.down, 1f, 1.2f);
            if (rng.Value < dt * 3f * k) body.Muscles.Spasm(0.5f * k);
            if (rng.Value < dt * 4f * k)
            {
                Vector3 j = rng.OnUnitSphere() * 6f * k * body.MassScale;
                BalanceController.PairImpulse(grabbedPart.rb, j, body.Chest.rb);
            }
        }
        #endregion
    }
    #endregion
}
