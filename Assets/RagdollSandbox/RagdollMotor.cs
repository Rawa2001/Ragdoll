// Ragdoll Sandbox - active ragdoll motor control (no Animator).
//  * Pose / IKSolver: procedural target poses and analytic two-bone IK. IK only produces joint target rotations that
//    the muscles (slerp drives) must physically reach.
//  * MuscleController: per-joint strength (health, consciousness, oxygen, fatigue, death fade), reflexes, spasms,
//    and a weighted layer stack so behaviours blend smoothly instead of switching.
//  * BalanceController: centre of mass, support polygon, capture point, ground sensing, plus the bounded assist:
//    implicit (joint-drive) upright torques on pelvis and chest, pelvis height support and velocity tracking forces.
//    All assists scale with consciousness, damage and the behaviour's control weight, and fade out when falling.
//  * LocomotionController: per-foot Stance -> Lift -> Swing -> Plant state machine, capture-point / Raibert footholds,
//    Bezier swing arcs with obstacle clearance, toe-off push, foot locking, recovery steps, run/jump/crouch/hop/turn.
//  * MotorBrain: behaviour states (stand, fall, lie, get up, crawl, hop, headless, unconscious, dead), possession and
//    Euphoria-style weighted reactions: stagger, brace, protect head, grab-and-hold, clutch wounds, hunch, limp,
//    hold head, struggle, vehicle hits with flailing.
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
        /// <summary>Drive load of each joint (0..1+, spring torque demand / torque limit) for the debug overlay.</summary>
        public readonly float[] load = new float[PartInfo.Count];
        public float ActiveLevel = 1f;
        public float DeathFade = 1f;
        public float Brace;            // co-contraction (0..1)
        public float Stun;             // seconds of limp reaction
        public float SpasmAmount;      // random joint noise (shock, headless, dying)
        public float FlinchTimer;
        public float GlobalScale = 1f; // behaviour-level strength (falling limp, relaxed lying)
        public float LegScaleL = 1f, LegScaleR = 1f; // limp / swing leg softness
        public float Tone = 1f;        // smoothed global tone (so behaviour changes never pop)
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

        /// <summary>Base layer: sets the joint target outright.</summary>
        public void Set(PartId id, Quaternion local, float stiffness = 1f)
        {
            target[(int)id] = local;
            poseStiffness[(int)id] = stiffness;
        }

        /// <summary>Overlay layer: blends toward 'local' with weight w (0..1). Layers applied later win.</summary>
        public void Blend(PartId id, Quaternion local, float w, float stiffness = 1f)
        {
            if (w <= 0.001f) return;
            int i = (int)id;
            w = Mathf.Clamp01(w);
            target[i] = Quaternion.Slerp(target[i], local, w);
            poseStiffness[i] = Mathf.Lerp(poseStiffness[i], stiffness, w);
        }

        public void Apply(float dt)
        {
            var ms = Sandbox.Boot.muscles;
            var vit = body.Vitals;
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
            float stunMul = Stun > 0f ? 0.35f : 1f;
            float brace = Mathf.Clamp01(Brace + (FlinchTimer > 0f ? 0.8f : 0f));
            float stiffMul = Mathf.Lerp(1f, ms.coContraction, brace);
            float vitalsMul = vit.MuscleFactor;
            float targetTone = ms.strength * ActiveLevel * DeathFade * vitalsMul * stunMul * GlobalScale;
            Tone = Mathf.MoveTowards(Tone, targetTone, dt * (targetTone < Tone ? 6f : 3f));
            float global = Tone;

            UpdateSpasm(dt);

            for (int i = 0; i < body.AllPieces.Count; i++)
            {
                var p = body.AllPieces[i];
                if (p == null || p.jr == null || p.jr.joint == null) continue;
                var j = p.jr.joint;
                if (p.jr.kind == JointKind.Fracture)
                {
                    // the two halves of a broken bone: barely held, very floppy
                    float k = 18f * body.MassScale * Mathf.Clamp01(global);
                    j.slerpDrive = new JointDrive { positionSpring = k, positionDamper = 2.5f * body.MassScale, maximumForce = 40f * body.MassScale };
                    j.targetRotation = Quaternion.identity;
                    continue;
                }
                int id = (int)p.id;
                if (p.isPiece || !p.attached)
                {
                    j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 1.2f * body.MassScale, maximumForce = 1000f };
                    load[id] = 0f;
                    continue;
                }
                bool motor = !p.paralyzed && global > 0.001f;
                float s = 0f;
                if (motor)
                {
                    float health = Mathf.Lerp(0.3f, 1f, Mathf.Min(p.HealthFraction, p.parentPiece != null ? Mathf.Lerp(0.4f, 1f, p.parentPiece.HealthFraction) : 1f));
                    int fr = body.FractureCount[id] + (p.parentPiece != null ? body.FractureCount[(int)p.parentPiece.id] : 0);
                    float frac = fr > 0 ? Mathf.Pow(0.4f, fr) : 1f;
                    float fatigue = 1f - vit.Fatigue * 0.45f;
                    s = global * health * frac * fatigue * poseStiffness[id];
                    int leg = PartInfo.LegSide(p.id);
                    if (leg == 0) s *= LegScaleL; else if (leg == 1) s *= LegScaleR;
                }
                strength[id] = s;
                float spring, damper, maxT;
                GroupGains(p.jr.group, ms, out spring, out damper, out maxT);
                float springK = spring * s * stiffMul * body.MassScale;
                float damperK = damper * (0.3f + 0.7f * Mathf.Min(1f, s)) * Mathf.Sqrt(stiffMul) * body.MassScale;
                if (!motor) { springK = 0f; damperK = damper * 0.05f * body.MassScale; }
                float maxF = Mathf.Max(4f, maxT * Mathf.Max(s, 0.04f) * ms.torqueLimitScale * stiffMul * body.MassScale);
                j.slerpDrive = new JointDrive { positionSpring = springK, positionDamper = damperK, maximumForce = maxF };
                Quaternion t = target[id];
                if (SpasmAmount > 0.01f)
                {
                    Vector3 e = spasmCurrent[id] * SpasmAmount;
                    t = t * Quaternion.Euler(e);
                }
                if (MathUtil.IsFinite(t)) j.targetRotation = p.jr.ToJointSpace(t);
                // load = how much of the drive's torque budget the current error demands
                if (p.parentPiece != null && springK > 0f)
                {
                    Quaternion cur = Quaternion.Inverse(p.parentPiece.rb.rotation) * p.rb.rotation;
                    float err = Quaternion.Angle(cur, t) * Mathf.Deg2Rad;
                    load[id] = springK * err / Mathf.Max(1f, maxF);
                }
                else load[id] = 0f;
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
                case MuscleGroup.Spine: spring = ms.spineSpring; damper = ms.spineDamper; maxTorque = 650f; break;
                case MuscleGroup.Neck: spring = ms.neckSpring; damper = ms.neckDamper; maxTorque = 150f; break;
                case MuscleGroup.Head: spring = ms.neckSpring * 0.7f; damper = ms.neckDamper * 0.7f; maxTorque = 100f; break;
                case MuscleGroup.Shoulder: spring = ms.shoulderSpring; damper = ms.shoulderDamper; maxTorque = 220f; break;
                case MuscleGroup.Elbow: spring = ms.elbowSpring; damper = ms.elbowDamper; maxTorque = 130f; break;
                case MuscleGroup.Wrist: spring = ms.wristSpring; damper = ms.wristDamper; maxTorque = 40f; break;
                case MuscleGroup.Hip: spring = ms.hipSpring; damper = ms.hipDamper; maxTorque = 900f; break;
                case MuscleGroup.Knee: spring = ms.kneeSpring; damper = ms.kneeDamper; maxTorque = 850f; break;
                case MuscleGroup.Ankle: spring = ms.ankleSpring; damper = ms.ankleDamper; maxTorque = 420f; break;
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
            float k = Sandbox.Boot.muscles.reflexStrength * Sandbox.Boot.behaviour.reactionStrength;
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
        public static void Clear() { ids.Clear(); }
    }
    #endregion

    #region BalanceController
    /// <summary>What the behaviour layer asks the balance assist to do this physics step.</summary>
    public struct BalanceCommand
    {
        public float control;           // 0..1 overall assist weight (fades in/out with behaviours)
        public Quaternion pelvisTarget, chestTarget;
        public float upright;           // 0..1 upright torque weight
        public float chestUpright;
        public float heightTarget;      // world y of the pelvis centre of mass
        public float lift;              // fraction of the supported weight carried by the assist (0..1.2)
        public float heightWeight;      // weight of the height PD term
        public Vector3 velocityTarget;  // horizontal
        public float velocityWeight;
        public Vector3 centerTarget;    // horizontal position the COM should stay over (standing)
        public float centerWeight;
        public Vector3 extraForce;      // one-off forces (jump)
        public float liftChest;         // extra lift on the chest (get up, push-up)
        public float chestHeightTarget;
    }

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
        public float ComSupportDistance;// signed distance of the COM ground projection outside the support polygon
        public readonly bool[] FootGrounded = new bool[2];
        public readonly bool[] FootNearGround = new bool[2];
        public readonly BodyPart[] FootPart = new BodyPart[2];
        public readonly Vector3[] SolePos = new Vector3[2];
        public readonly Vector3[] FootFwd = new Vector3[2];
        public readonly float[] FootGroundY = new float[2];
        public readonly Vector3[] FootGroundN = new Vector3[2];
        public float UprightDot;        // chest up . world up
        public float HealthFactor = 1f;
        public BalanceCommand Cmd;
        public float AppliedTorqueFraction, AppliedLift, AppliedHorizontal;   // debug read-outs
        public readonly Vector2[] Hull = new Vector2[17];
        public int HullCount;

        readonly RaycastHit[] hits = new RaycastHit[16];
        readonly Vector2[] hullIn = new Vector2[8];
        ConfigurableJoint pelvisAnchor, chestAnchor;
        Quaternion pelvisStart = Quaternion.identity, chestStart = Quaternion.identity;
        Rigidbody pelvisAnchorRb, chestAnchorRb;
        bool enabledAssist = true;
        float smoothControl;

        public BalanceController(RagdollBody b) { body = b; }

        public float StandHeight { get { return Sandbox.Boot.balance.standHeightRatio * PartInfo.RefHeight * body.Scale; } }
        public float G { get { return Physics.gravity.magnitude; } }
        public float Control { get { return smoothControl; } }

        /// <summary>Creates the two invisible "world" joints whose implicit slerp drives provide the bounded upright
        /// assist (PhysX solves drives implicitly, so they stay stable at any stiffness unlike explicit PD torques).</summary>
        public void Init()
        {
            CreateAnchors();
        }

        void CreateAnchors()
        {
            DestroyAnchors();
            if (body.Pelvis != null) pelvisAnchor = MakeAnchor(body.Pelvis, out pelvisStart, out pelvisAnchorRb);
            if (body.Chest != null) chestAnchor = MakeAnchor(body.Chest, out chestStart, out chestAnchorRb);
        }

        ConfigurableJoint MakeAnchor(BodyPart part, out Quaternion start, out Rigidbody anchorBody)
        {
            var j = part.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = Vector3.zero;
            j.axis = Vector3.right;
            j.secondaryAxis = Vector3.up;
            j.connectedBody = null;
            j.connectedAnchor = part.rb.position;
            j.xMotion = ConfigurableJointMotion.Free;
            j.yMotion = ConfigurableJointMotion.Free;
            j.zMotion = ConfigurableJointMotion.Free;
            j.angularXMotion = ConfigurableJointMotion.Free;
            j.angularYMotion = ConfigurableJointMotion.Free;
            j.angularZMotion = ConfigurableJointMotion.Free;
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
            j.enablePreprocessing = false;
            j.enableCollision = true;
            j.breakForce = Mathf.Infinity;
            j.breakTorque = Mathf.Infinity;
            start = part.rb.rotation;
            anchorBody = part.rb;
            return j;
        }

        void DestroyAnchors()
        {
            if (pelvisAnchor != null) UnityEngine.Object.Destroy(pelvisAnchor);
            if (chestAnchor != null) UnityEngine.Object.Destroy(chestAnchor);
            pelvisAnchor = null; chestAnchor = null;
        }

        public void SetEnabled(bool on)
        {
            enabledAssist = on;
            if (!on) { Release(pelvisAnchor); Release(chestAnchor); }
        }

        public void Dispose() { DestroyAnchors(); }

        public void OnTopologyChanged()
        {
            // the anchors stay on the primary pelvis/chest segments; nothing to rebuild unless they vanished
            if ((pelvisAnchor == null && body.Pelvis != null) || (chestAnchor == null && body.Chest != null)) CreateAnchors();
        }

        static void Release(ConfigurableJoint j)
        {
            if (j == null) return;
            j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
        }

        public void ClearCommand()
        {
            var p = body.Pelvis;
            Quaternion r = p != null ? p.rb.rotation : Quaternion.identity;
            Cmd = new BalanceCommand { pelvisTarget = r, chestTarget = r };
        }

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
                BodyPart fp = body.LegPresent[side] ? (body.LegHasFoot[side] ? body.Part(foot) : body.DistalOf(shin)) : null;
                if (fp != null && !fp.attached) fp = null;
                FootPart[side] = fp;
                FootGrounded[side] = false;
                FootNearGround[side] = false;
                if (fp == null) continue;
                Quaternion r = fp.rb.rotation;
                if (fp.id == foot) SolePos[side] = fp.rb.position + r * (new Vector3(0f, -0.072f, 0.065f) * s);
                else SolePos[side] = fp.transform.TransformPoint(fp.segAxis * fp.segMax);
                FootFwd[side] = MathUtil.SafeNormal(MathUtil.Flat(fp.id == foot ? r * Vector3.forward : pelvis.rb.rotation * Vector3.forward), Vector3.forward);
                bool contact = fp.Grounded;
                RaycastHit hit;
                float gy = SolePos[side].y;
                Vector3 gnrm = Vector3.up;
                bool ray = Ground(SolePos[side] + Vector3.up * 0.08f * s, 0.22f * s, out hit);
                if (ray) { gy = hit.point.y; gnrm = hit.normal; }
                else if (contact) { gy = fp.groundPoint.y; gnrm = fp.groundNormal; }
                FootGroundY[side] = gy;
                FootGroundN[side] = gnrm;
                float gap = SolePos[side].y - gy;
                FootNearGround[side] = ray && gap < 0.08f * s;
                if (contact || (ray && gap < 0.03f * s))
                {
                    FootGrounded[side] = true;
                    SupportCount++;
                    sc += SolePos[side];
                    groundSum += gy;
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
            BuildHull();
            SupportDistance = HullCount > 0 ? DistanceOutsideHull(CapturePoint) : 10f;
            ComSupportDistance = HullCount > 0 ? DistanceOutsideHull(body.CenterOfMass) : 10f;
            Upright = Tilt < Sandbox.Boot.balance.fallAngle && PelvisHeight > StandHeight * 0.55f;
            float health = 0f;
            int hn = 0;
            for (int i = 0; i < body.AttachedParts.Count; i++)
            {
                var p = body.AttachedParts[i];
                if (PartInfo.IsArm(p.id)) continue;
                health += p.HealthFraction; hn++;
            }
            health = hn > 0 ? health / hn : 0f;
            HealthFactor = Mathf.Lerp(0.35f, 1f, health);
        }

        /// <summary>Raycasts down for walkable ground, ignoring this ragdoll's own colliders, organs and triggers.</summary>
        public bool Ground(Vector3 from, float maxDist, out RaycastHit best)
        {
            best = new RaycastHit();
            int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, maxDist, Layers.GroundMask, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                BodyPart bp = Registry.PartOf(h.collider);
                if (bp != null && bp.body == body) continue;
                if (Registry.Organs.ContainsKey(h.collider)) continue;
                if (h.distance < bd) { bd = h.distance; best = h; found = true; }
            }
            return found;
        }

        void BuildHull()
        {
            float s = body.Scale;
            int k = 0;
            for (int side = 0; side < 2; side++)
            {
                if (!FootGrounded[side]) continue;
                Vector3 c = SolePos[side];
                Vector3 f = FootFwd[side];
                Vector3 r = new Vector3(f.z, 0f, -f.x);
                bool isFoot = FootPart[side] != null && PartInfo.IsFoot(FootPart[side].id);
                float hl = (isFoot ? 0.12f : 0.035f) * s;
                float hw = (isFoot ? 0.045f : 0.035f) * s;
                hullIn[k++] = new Vector2(c.x + f.x * hl + r.x * hw, c.z + f.z * hl + r.z * hw);
                hullIn[k++] = new Vector2(c.x + f.x * hl - r.x * hw, c.z + f.z * hl - r.z * hw);
                hullIn[k++] = new Vector2(c.x - f.x * hl + r.x * hw, c.z - f.z * hl + r.z * hw);
                hullIn[k++] = new Vector2(c.x - f.x * hl - r.x * hw, c.z - f.z * hl - r.z * hw);
            }
            if (k == 0) { HullCount = 0; return; }
            ConvexHull(k);
        }

        /// <summary>Signed distance from p (XZ) to the support polygon built from grounded feet (negative inside).</summary>
        public float DistanceOutsideHull(Vector3 p)
        {
            if (HullCount == 0) return 10f;
            Vector2 q = new Vector2(p.x, p.z);
            bool inside = true;
            float minDist = float.MaxValue;
            for (int i = 0; i < HullCount; i++)
            {
                Vector2 a = Hull[i], b = Hull[(i + 1) % HullCount];
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
                while (k >= 2 && Cross(Hull[k - 2], Hull[k - 1], hullIn[i]) <= 0f) k--;
                Hull[k++] = hullIn[i];
            }
            for (int i = n - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && Cross(Hull[k - 2], Hull[k - 1], hullIn[i]) <= 0f) k--;
                Hull[k++] = hullIn[i];
            }
            HullCount = Mathf.Max(1, k - 1);
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

        /// <summary>Mass of leg segments whose foot is on the ground (they are carried by the ground, not the assist).</summary>
        float StanceLegMass()
        {
            float m = 0f;
            for (int i = 0; i < body.AttachedParts.Count; i++)
            {
                var p = body.AttachedParts[i];
                int leg = PartInfo.LegSide(p.id);
                if (leg >= 0 && FootGrounded[leg]) m += p.rb.mass;
            }
            return m;
        }

        /// <summary>Applies the command: implicit upright drives, bounded support and velocity forces.</summary>
        public void ApplyAssist(float dt)
        {
            var bs = Sandbox.Boot.balance;
            var pelvis = body.Pelvis;
            var chest = body.Chest;
            if (pelvis == null || !enabledAssist) return;
            float consciousness = Mathf.Clamp01(body.Vitals.Consciousness * 1.25f);
            float alive = body.Muscles.DeathFade;
            float c = Mathf.Clamp01(Cmd.control);
            smoothControl = Mathf.MoveTowards(smoothControl, c, dt * (c < smoothControl ? 5f : 2.5f));
            float k = smoothControl * consciousness * alive * HealthFactor;
            if (body.Vitals.Dead || !MathUtil.IsFinite(Cmd.pelvisTarget)) k = 0f;
            float ms = body.MassScale;

            // 1) upright: implicit slerp drives to the world (bounded by maximumForce = the assist budget)
            float budget = bs.assistTorque * ms * k * Mathf.Clamp01(Cmd.upright);
            AppliedTorqueFraction = bs.assistTorque > 0f ? budget / (bs.assistTorque * ms) : 0f;
            if (pelvisAnchor != null && pelvis.attached)
            {
                pelvisAnchor.slerpDrive = new JointDrive
                {
                    positionSpring = bs.pelvisUprightSpring * ms * (budget > 0f ? 1f : 0f),
                    positionDamper = bs.pelvisUprightDamper * ms * (budget > 0f ? 1f : 0f),
                    maximumForce = budget
                };
                pelvisAnchor.targetRotation = Quaternion.Inverse(Cmd.pelvisTarget) * pelvisStart;
            }
            else Release(pelvisAnchor);
            float chestBudget = bs.assistTorque * bs.chestAssistRatio * ms * k * Mathf.Clamp01(Cmd.chestUpright);
            if (chestAnchor != null && chest != null && chest.attached && MathUtil.IsFinite(Cmd.chestTarget))
            {
                chestAnchor.slerpDrive = new JointDrive
                {
                    positionSpring = bs.chestUprightSpring * ms * (chestBudget > 0f ? 1f : 0f),
                    positionDamper = bs.chestUprightDamper * ms * (chestBudget > 0f ? 1f : 0f),
                    maximumForce = chestBudget
                };
                chestAnchor.targetRotation = Quaternion.Inverse(Cmd.chestTarget) * chestStart;
            }
            else Release(chestAnchor);
            if (k <= 0.0001f) { AppliedLift = 0f; AppliedHorizontal = 0f; return; }

            float g = G;
            float mass = body.TotalMass;
            // 2) vertical support: carries the weight not borne by the stance legs, plus a PD toward the height target
            float carried = Mathf.Max(0f, mass - StanceLegMass());
            float pelvisY = pelvis.rb.worldCenterOfMass.y;
            float vy = body.ComVelocity.y;
            float fy = carried * g * Mathf.Clamp(Cmd.lift, 0f, 1.25f) + mass * Cmd.heightWeight * (bs.heightGain * (Cmd.heightTarget - pelvisY) - bs.heightDamping * vy);
            fy = Mathf.Clamp(fy, 0f, mass * g * 1.35f) * k;
            AppliedLift = mass > 0f ? fy / (mass * Mathf.Max(0.1f, g)) : 0f;
            // 3) horizontal: velocity tracking + COM centring, bounded so a real push still needs recovery steps
            Vector3 com = MathUtil.Flat(body.CenterOfMass);
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            Vector3 fh = mass * (bs.velocityGain * Cmd.velocityWeight * (MathUtil.Flat(Cmd.velocityTarget) - v) +
                                 bs.comGain * Cmd.centerWeight * (MathUtil.Flat(Cmd.centerTarget) - com));
            float fhMax = bs.maxHorizontalAssist * mass * Mathf.Max(1f, g);
            if (fh.magnitude > fhMax) fh = fh.normalized * fhMax;
            fh *= k;
            AppliedHorizontal = mass > 0f ? fh.magnitude / (mass * Mathf.Max(1f, g)) : 0f;
            Vector3 total = Vector3.up * fy + fh;
            if (MathUtil.IsFinite(total))
            {
                if (chest != null && chest.attached)
                {
                    pelvis.rb.AddForce(total * 0.7f, ForceMode.Force);
                    chest.rb.AddForce(total * 0.3f, ForceMode.Force);
                }
                else pelvis.rb.AddForce(total, ForceMode.Force);
            }
            if (Cmd.liftChest > 0f && chest != null && chest.attached)
            {
                float cy = chest.rb.worldCenterOfMass.y;
                float fc = mass * (Cmd.liftChest * g * 0.5f + bs.heightGain * 0.6f * (Cmd.chestHeightTarget - cy) - bs.heightDamping * 0.6f * Compat.GetVelocity(chest.rb).y);
                fc = Mathf.Clamp(fc, 0f, mass * g * 0.9f) * k;
                chest.rb.AddForce(Vector3.up * fc, ForceMode.Force);
            }
            if (Cmd.extraForce.sqrMagnitude > 0f && MathUtil.IsFinite(Cmd.extraForce)) pelvis.rb.AddForce(Cmd.extraForce, ForceMode.Force);
        }

        /// <summary>Adds the same velocity change to every attached segment (jumps, hops: the body moves as a unit).</summary>
        public void AddBodyVelocity(Vector3 dv)
        {
            if (!MathUtil.IsFinite(dv)) return;
            for (int i = 0; i < body.AttachedParts.Count; i++)
            {
                var p = body.AttachedParts[i];
                if (p != null && p.rb != null && !p.rb.isKinematic) p.rb.AddForce(dv, ForceMode.VelocityChange);
            }
        }
    }
    #endregion

    #region LocomotionController
    public enum GaitMode { Normal, Hop }
    public enum FootPhase { Stance, Lift, Swing, Plant }

    public class LocomotionController
    {
        public struct FootState
        {
            public FootPhase phase;
            public Vector3 planted;       // ankle target while in stance
            public float plantedYaw;
            public Vector3 lift, target, swingPos, swingVel;
            public float targetYaw, liftYaw;
            public float t, dur, clearance;
            public float lastPlant;
            public bool blocked;
            public float phaseTime;
        }

        readonly RagdollBody body;
        public readonly FootState[] Feet = new FootState[2];
        public Vector3 DesiredVelocity;
        public float DesiredYaw;
        public float HeadingYaw;
        public bool Run, Crouch;
        public GaitMode Mode = GaitMode.Normal;
        public float StumbleTimer;
        public float SlipTimer;
        public float RecoveryUrgency;     // 0..1, how badly the capture point is outside the support
        public int RecoverySteps;
        public float LimpSide = -1f;      // 0 left, 1 right, -1 none
        public float LimpAmount;
        public float WidenStance;         // extra stance width (stagger)
        public float Hunch;               // forward hunch from gut wounds (deg)
        public float LegReach = 1f;
        public float Bob;
        public float HeightTarget;
        public float Control = 1f;        // assist weight requested by the brain
        int lastSwing = 1;
        float doubleSupportUntil;
        bool initialized;
        float crouchAmount;
        float jumpTimer = -1f;
        int jumpPhase;           // 0 none, 1 crouch, 2 push, 3 air, 4 land
        float airTime;
        float hopTimer;
        int hopPhase;
        Rng rng;

        public LocomotionController(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 7 + 3); }

        public bool Airborne { get { return jumpPhase == 3 || hopPhase == 2; } }
        public bool Jumping { get { return jumpPhase > 0; } }
        public float CrouchAmount { get { return crouchAmount; } }
        public Quaternion Heading { get { return Quaternion.Euler(0f, HeadingYaw, 0f); } }

        public void ResetFeet() { initialized = false; }

        public void RequestJump()
        {
            if (jumpPhase == 0 && body.Balance.SupportCount > 0 && Mode == GaitMode.Normal) { jumpPhase = 1; jumpTimer = 0f; }
        }

        void Init()
        {
            initialized = true;
            if (body.Pelvis == null) return;
            HeadingYaw = MathUtil.Yaw(MathUtil.Flat(body.Pelvis.rb.rotation * Vector3.forward));
            DesiredYaw = HeadingYaw;
            for (int s = 0; s < 2; s++)
            {
                Feet[s].phase = FootPhase.Stance;
                Feet[s].planted = FootAnchor(s);
                Feet[s].plantedYaw = HeadingYaw + (s == 0 ? -5f : 5f);
                Feet[s].lastPlant = Time.time;
            }
            jumpPhase = 0;
            hopPhase = 0;
        }

        /// <summary>The leg's ground anchor: the ankle, or the distal end of the shin when the foot is missing.</summary>
        public Vector3 FootAnchor(int i)
        {
            var fp = body.Balance.FootPart[i];
            if (fp == null) return body.Pelvis.rb.position;
            if (PartInfo.IsFoot(fp.id)) return fp.rb.position;
            return fp.transform.TransformPoint(fp.segAxis * fp.segMax);
        }

        float AnchorHeight(int i)
        {
            var fp = body.Balance.FootPart[i];
            return (fp != null && PartInfo.IsFoot(fp.id) ? 0.075f : 0.03f) * body.Scale;
        }

        public bool IsSwinging(int side) { return Feet[side].phase == FootPhase.Swing || Feet[side].phase == FootPhase.Lift; }
        bool AnySwing() { return IsSwinging(0) || IsSwinging(1); }

        /// <summary>Runs the gait for one physics step and writes the balance command.
        /// Returns false if the legs cannot support the body (the brain should let it fall).</summary>
        public bool Tick(float dt, float strengthScale)
        {
            var ls = Sandbox.Boot.locomotion;
            var bs = Sandbox.Boot.balance;
            var bal = body.Balance;
            if (!initialized) Init();
            float s = body.Scale;
            if (StumbleTimer > 0f) StumbleTimer -= dt;
            if (SlipTimer > 0f) SlipTimer -= dt;

            // heading
            float yawErr = Mathf.DeltaAngle(HeadingYaw, DesiredYaw);
            float turnRate = ls.turnSpeed * (Mode == GaitMode.Hop ? 0.5f : 1f);
            HeadingYaw += Mathf.Clamp(yawErr, -turnRate * dt, turnRate * dt);
            Quaternion H = Heading;
            Vector3 fwd = H * Vector3.forward, right = H * Vector3.right;

            crouchAmount = Mathf.MoveTowards(crouchAmount, Crouch ? 1f : 0f, dt * 3f);
            float speed = DesiredVelocity.magnitude;
            float maxSpeed = Crouch ? ls.crouchSpeed : Run ? ls.runSpeed : ls.walkSpeed;
            if (Mode == GaitMode.Hop) maxSpeed = Mathf.Min(maxSpeed, ls.hopSpeed);
            Vector3 vDes = speed > maxSpeed ? DesiredVelocity / speed * maxSpeed : DesiredVelocity;
            vDes *= Mathf.Lerp(0.4f, 1f, strengthScale) * (SlipTimer > 0f ? 0.6f : 1f) * (1f - LimpAmount * 0.45f);
            float runFactor = Mathf.Clamp01((vDes.magnitude - ls.walkSpeed) / Mathf.Max(0.1f, ls.runSpeed - ls.walkSpeed));
            RecoveryUrgency = Mathf.Clamp01((bal.SupportDistance - bs.captureMargin * s) / (0.3f * s));

            // jump state
            float jumpLower = 0f;
            Vector3 extra = Vector3.zero;
            if (jumpPhase > 0)
            {
                jumpTimer += dt;
                if (jumpPhase == 1)
                {
                    jumpLower = 0.17f * s * Mathf.Clamp01(jumpTimer / 0.16f);
                    if (jumpTimer > 0.18f)
                    {
                        jumpPhase = 2; jumpTimer = 0f;
                        float up = Mathf.Sqrt(2f * Mathf.Max(0.5f, bal.G) * ls.jumpHeight * s) * Mathf.Lerp(0.5f, 1f, strengthScale);
                        bal.AddBodyVelocity(Vector3.up * up + MathUtil.Flat(vDes) * 0.25f);
                        for (int i = 0; i < 2; i++) { Feet[i].phase = FootPhase.Stance; }
                        if (Sandbox.Audio != null) Sandbox.Audio.PlayVoice(Sfx.Grunt0, body.Pelvis.rb.position, 0.35f, rng.Range(0.95f, 1.1f));
                    }
                }
                else if (jumpPhase == 2)
                {
                    if (jumpTimer > 0.1f) { jumpPhase = 3; jumpTimer = 0f; airTime = 0f; }
                }
                else if (jumpPhase == 3)
                {
                    airTime += dt;
                    if (airTime > 0.15f && bal.SupportCount > 0) { jumpPhase = 4; jumpTimer = 0f; PlantAll(); }
                    if (airTime > 2.5f) jumpPhase = 0;
                }
                else if (jumpPhase == 4) { jumpLower = 0.12f * s * (1f - Mathf.Clamp01(jumpTimer / 0.3f)); if (jumpTimer > 0.3f) jumpPhase = 0; }
            }

            bool airborne = jumpPhase == 2 || jumpPhase == 3;
            if (!airborne)
            {
                if (Mode == GaitMode.Hop) HopLogic(dt, vDes, fwd, strengthScale);
                else StepLogic(dt, vDes, fwd, right, runFactor, yawErr);
            }
            else
            {
                for (int i = 0; i < 2; i++) { Feet[i].phase = FootPhase.Stance; Feet[i].planted = AirFootTarget(i, right); }
            }

            // height target from the stance feet
            float lowest = float.MaxValue, highest = float.MinValue, sum = 0f;
            int cnt = 0;
            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) continue;
                float gy = Feet[i].phase == FootPhase.Stance || Feet[i].phase == FootPhase.Plant ? Feet[i].planted.y - AnchorHeight(i) : bal.FootGroundY[i];
                if (Feet[i].phase == FootPhase.Swing) gy = Mathf.Min(gy, Feet[i].target.y - AnchorHeight(i));
                lowest = Mathf.Min(lowest, gy); highest = Mathf.Max(highest, gy);
                sum += gy; cnt++;
            }
            float groundY = cnt > 0 ? Mathf.Lerp(lowest, sum / cnt, 0.6f) : bal.GroundHeight;
            if (cnt == 0) groundY = bal.GroundHeight;
            float stand = bal.StandHeight;
            float hTarget = groundY + stand * (1f - 0.3f * crouchAmount) - jumpLower - Mathf.Abs(Bob) * 0.015f * s - LimpAmount * 0.02f * s;
            if (Mode == GaitMode.Hop) hTarget -= 0.03f * s;
            for (int i = 0; i < 2; i++) if (bal.FootPart[i] != null && !PartInfo.IsFoot(bal.FootPart[i].id)) hTarget -= 0.025f * s;
            if (highest - lowest > 0.08f * s) hTarget -= Mathf.Min(0.08f * s, (highest - lowest) * 0.25f); // stairs: knees bend more
            HeightTarget = hTarget;

            // torso orientation: heading + lean into acceleration / speed + hunch
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            float lean = Mathf.Clamp(Vector3.Dot(vDes, fwd) * 3.5f + Vector3.Dot(vDes - v, fwd) * 5f, -8f, 14f) + runFactor * 7f + crouchAmount * 14f + Hunch * 0.5f;
            float roll = Mathf.Clamp(Vector3.Dot(vDes - v, right) * 4f, -6f, 6f);
            if (StumbleTimer > 0f) lean += 8f;
            float sway = 0f;
            for (int i = 0; i < 2; i++) if (IsSwinging(i)) sway += (i == 0 ? 1f : -1f) * 2.5f * Mathf.Sin(Mathf.Clamp01(Feet[i].t / Mathf.Max(0.01f, Feet[i].dur)) * Mathf.PI);
            Quaternion pelvisTarget = H * Quaternion.Euler(lean * 0.35f, 0f, -roll + sway * 0.4f);
            Quaternion chestTarget = H * Quaternion.Euler(lean, 0f, -roll * 0.5f);

            // support capacity of the legs
            float legStr = 0f;
            int ln = 0;
            for (int i = 0; i < 2; i++) if (bal.FootPart[i] != null && body.LegUsable[i]) { legStr += Mathf.Clamp01(body.Muscles.LegStrength(i) * 1.3f); ln++; }
            legStr = ln > 0 ? legStr / ln : 0f;
            if (Mode == GaitMode.Hop) legStr *= 0.9f;
            float support = 0f;
            for (int i = 0; i < 2; i++) if (bal.FootGrounded[i] || bal.FootNearGround[i] || (Feet[i].phase == FootPhase.Swing && bal.FootPart[i] != null)) support += 0.5f;
            if (Mode == GaitMode.Hop) support = Mathf.Min(1f, support * 2f);
            support = Mathf.Clamp01(support);

            // balance command
            var cmd = new BalanceCommand();
            cmd.control = Control * Mathf.Clamp01(legStr * 1.2f) * Mathf.Lerp(0.5f, 1f, strengthScale);
            cmd.pelvisTarget = pelvisTarget;
            cmd.chestTarget = chestTarget;
            cmd.upright = airborne ? 0.5f : 1f;
            cmd.chestUpright = airborne ? 0.6f : 1f;
            cmd.heightTarget = hTarget;
            cmd.lift = airborne ? 0f : bs.weightSupport * support;
            cmd.heightWeight = airborne ? 0f : support;
            cmd.velocityTarget = vDes;
            cmd.velocityWeight = airborne ? 0.05f : 1f;
            bool standing = vDes.sqrMagnitude < 0.01f && !AnySwing();
            cmd.centerTarget = bal.SupportCount > 0 ? bal.SupportCenter : MathUtil.Flat(body.CenterOfMass);
            cmd.centerWeight = standing && bal.SupportCount > 0 ? 1f : 0f;
            cmd.extraForce = extra;
            bal.Cmd = cmd;

            ApplyLegTargets(H, fwd, dt, strengthScale);
            FootForces(dt);
            ArmsAndSpine(fwd, runFactor);
            return legStr > 0.12f || airborne;
        }

        void PlantAll()
        {
            var bal = body.Balance;
            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) continue;
                Feet[i].phase = FootPhase.Stance;
                Feet[i].planted = FootAnchor(i);
                Feet[i].plantedYaw = HeadingYaw + (i == 0 ? -5f : 5f);
                Feet[i].lastPlant = Time.time;
            }
        }

        public void PlantCurrent() { PlantAll(); }

        Vector3 AirFootTarget(int i, Vector3 right)
        {
            float s = body.Scale;
            Vector3 cp = body.Balance.CapturePoint;
            Vector3 hip = body.Pelvis.rb.position;
            Vector3 p = new Vector3(cp.x, hip.y - 0.78f * s, cp.z) + right * ((i == 0 ? -1f : 1f) * Sandbox.Boot.locomotion.stanceWidth * 0.5f * s);
            return p;
        }

        void StepLogic(float dt, Vector3 vDes, Vector3 fwd, Vector3 right, float runFactor, float yawErr)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float s = body.Scale;
            bool moving = vDes.magnitude > 0.08f;
            bool turning = Mathf.Abs(yawErr) > 20f;

            for (int i = 0; i < 2; i++)
            {
                if (bal.FootPart[i] == null) { Feet[i].phase = FootPhase.Stance; continue; }
                Feet[i].phaseTime += dt;
                if (Feet[i].phase == FootPhase.Stance)
                {
                    // slip detection: planted foot dragged away
                    Vector3 anchorPos = FootAnchor(i);
                    float drift = MathUtil.Flat(anchorPos - Feet[i].planted).magnitude;
                    if (bal.FootGrounded[i] && drift > 0.08f * s) { Feet[i].planted = anchorPos; SlipTimer = 0.6f; }
                    if (!bal.FootGrounded[i] && drift > 0.25f * s) Feet[i].planted = anchorPos;
                }
                else if (Feet[i].phase == FootPhase.Plant)
                {
                    if (Feet[i].phaseTime > 0.06f) { Feet[i].phase = FootPhase.Stance; Feet[i].phaseTime = 0f; }
                }
                else if (Feet[i].phase == FootPhase.Lift)
                {
                    Feet[i].t += dt;
                    if (Feet[i].phaseTime > ls.liftTime) { Feet[i].phase = FootPhase.Swing; Feet[i].phaseTime = 0f; }
                }
            }

            // advance swings
            for (int i = 0; i < 2; i++)
            {
                if (Feet[i].phase != FootPhase.Swing) continue;
                Feet[i].t += dt;
                float u = Feet[i].t / Feet[i].dur;
                if (u < 0.65f) RetargetSwing(i, vDes, fwd, right, runFactor);
                var fp = bal.FootPart[i];
                bool contact = fp != null && (fp.Grounded || bal.FootGrounded[i]) && u > 0.5f;
                if (u >= 1f || contact || Feet[i].blocked)
                {
                    Feet[i].phase = FootPhase.Plant;
                    Feet[i].phaseTime = 0f;
                    Feet[i].planted = Feet[i].blocked || contact ? FootAnchor(i) : Feet[i].target;
                    if (!Feet[i].blocked && !contact) Feet[i].planted = Vector3.Lerp(FootAnchor(i), Feet[i].target, 0.5f);
                    Feet[i].plantedYaw = Feet[i].targetYaw;
                    Feet[i].lastPlant = Time.time;
                    Feet[i].blocked = false;
                    doubleSupportUntil = Time.time + Mathf.Lerp(0.06f, 0.01f, Mathf.Max(runFactor, RecoveryUrgency));
                    PlayStep(fp);
                }
            }
            if (AnySwing()) return;
            if (Time.time < doubleSupportUntil) return;
            int usable = (bal.FootPart[0] != null ? 1 : 0) + (bal.FootPart[1] != null ? 1 : 0);
            if (usable < 2) return; // the hop gait handles one leg

            // decide whether to step
            bool needBalance = bal.SupportCount > 0 && RecoveryUrgency > 0.01f;
            bool lostSupport = bal.SupportCount == 0;
            int foot = -1;
            if (needBalance || lostSupport)
            {
                Vector3 e = bal.CapturePoint - (bal.SupportCount > 0 ? bal.SupportCenter : MathUtil.Flat(body.CenterOfMass));
                float lateral = Vector3.Dot(e, right);
                if (Mathf.Abs(lateral) > 0.55f * e.magnitude) foot = lateral < 0f ? 0 : 1;
                else
                {
                    float d0 = (MathUtil.Flat(Feet[0].planted) - bal.CapturePoint).sqrMagnitude, d1 = (MathUtil.Flat(Feet[1].planted) - bal.CapturePoint).sqrMagnitude;
                    foot = d0 > d1 ? 0 : 1;
                }
                if (moving && !lostSupport) foot = -1; // while walking, the regular cadence handles the capture point
            }
            if (foot < 0 && moving)
            {
                // cadence: step with the trailing foot once the hip has passed it, or when the stance has lasted long enough
                Vector3 hip = MathUtil.Flat(body.Pelvis.rb.position);
                Vector3 dir = vDes.normalized;
                float a0 = Vector3.Dot(MathUtil.Flat(Feet[0].planted) - hip, dir), a1 = Vector3.Dot(MathUtil.Flat(Feet[1].planted) - hip, dir);
                int trailing = a0 < a1 ? 0 : 1;
                float behind = -Mathf.Min(a0, a1);
                float stepLen = Mathf.Lerp(ls.maxStride, ls.runStride, runFactor) * s;
                float since = Time.time - Mathf.Max(Feet[0].lastPlant, Feet[1].lastPlant);
                float limpHold = LimpSide >= 0f && (int)LimpSide == 1 - trailing ? LimpAmount * 0.12f : 0f;
                if (behind > stepLen * 0.28f || since > 0.55f + limpHold) foot = trailing;
            }
            if (foot < 0 && (turning || !moving))
            {
                // re-square the feet: wrong yaw, too close or too far apart
                for (int i = 0; i < 2 && foot < 0; i++)
                {
                    float yawOff = Mathf.Abs(Mathf.DeltaAngle(Feet[i].plantedYaw, HeadingYaw));
                    if (yawOff > 28f) foot = i;
                }
                if (foot < 0 && !moving)
                {
                    Vector3 rel = MathUtil.Flat(Feet[1].planted - Feet[0].planted);
                    float lat = Vector3.Dot(rel, right), lon = Vector3.Dot(rel, fwd);
                    float width = (ls.stanceWidth + WidenStance) * s;
                    if (lat < width * 0.45f || lat > width * 2.4f || Mathf.Abs(lon) > 0.32f * s)
                    {
                        float since = Time.time - Mathf.Max(Feet[0].lastPlant, Feet[1].lastPlant);
                        if (since > 0.25f) foot = 1 - lastSwing;
                    }
                    if (foot < 0 && bal.ComSupportDistance > -0.01f * s && Time.time - Mathf.Max(Feet[0].lastPlant, Feet[1].lastPlant) > 0.4f) foot = 1 - lastSwing;
                }
            }
            if (foot < 0) return;
            StartSwing(foot, vDes, fwd, right, runFactor, needBalance || lostSupport);
        }

        void StartSwing(int i, Vector3 vDes, Vector3 fwd, Vector3 right, float runFactor, bool emergency)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            var fp = bal.FootPart[i];
            if (fp == null) return;
            Feet[i].phase = FootPhase.Lift;
            Feet[i].phaseTime = 0f;
            Feet[i].t = 0f;
            float baseDur = Mathf.Lerp(ls.stepTimeWalk, ls.stepTimeRun, runFactor);
            if (emergency) { baseDur = Mathf.Min(baseDur, Mathf.Lerp(0.32f, 0.22f, RecoveryUrgency)); RecoverySteps++; }
            if (StumbleTimer > 0f) baseDur *= 0.8f;
            if (LimpSide >= 0f && (int)LimpSide == i) baseDur *= 1f + LimpAmount * 0.3f;
            Feet[i].dur = baseDur * (body.LegHasFoot[i] ? 1f : 1.2f) + ls.liftTime;
            Feet[i].lift = FootAnchor(i);
            Feet[i].liftYaw = Feet[i].plantedYaw;
            Feet[i].blocked = false;
            lastSwing = i;
            RetargetSwing(i, vDes, fwd, right, runFactor);
            ComputeClearance(i);
            // toe-off push: the lifting foot pushes the body forward (internal force pair against the ground)
            if (bal.FootGrounded[i] && vDes.sqrMagnitude > 0.01f)
            {
                float k = Mathf.Clamp01(vDes.magnitude / Mathf.Max(0.1f, ls.walkSpeed));
                Vector3 push = vDes.normalized * ls.toeOffImpulse * k * body.MassScale;
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
            float remaining = Mathf.Max(0f, Feet[i].dur - Feet[i].t);
            Vector3 com = MathUtil.Flat(body.CenterOfMass);
            Vector3 v = MathUtil.Flat(body.ComVelocity);
            float stanceTime = Mathf.Lerp(0.5f, 0.3f, runFactor);
            // Raibert foothold: neutral point + correction for the velocity error
            Vector3 target = com + v * (remaining + stanceTime * 0.5f) + (v - vDes) * ls.raibertGain;
            if (RecoveryUrgency > 0f)
            {
                // capture point step: put the foot at (or just beyond) where the body would come to rest
                Vector3 cp = bal.CapturePoint;
                Vector3 dir = MathUtil.SafeNormal(cp - (bal.SupportCount > 0 ? bal.SupportCenter : com), fwd);
                Vector3 cpTarget = cp + dir * Sandbox.Boot.balance.captureMargin * s * 1.5f;
                target = Vector3.Lerp(target, cpTarget, Mathf.Clamp01(0.4f + RecoveryUrgency));
            }
            float width = (ls.stanceWidth + WidenStance) * s;
            target += right * side * width * 0.5f;
            // stride clamping relative to the stance foot
            Vector3 otherP = MathUtil.Flat(Feet[other].planted);
            Vector3 rel = target - otherP;
            float along = Vector3.Dot(rel, fwd), lat = Vector3.Dot(rel, right);
            float maxStride = Mathf.Lerp(ls.maxStride, ls.runStride, runFactor) * s * (SlipTimer > 0f ? 0.65f : 1f) * LegReach * (1f + RecoveryUrgency * 0.5f);
            along = Mathf.Clamp(along, -maxStride * 0.75f, maxStride);
            float minLat = width * 0.5f, maxLat = width * 2.6f + RecoveryUrgency * 0.25f * s;
            if (side < 0f) lat = Mathf.Clamp(lat, -maxLat, -minLat); else lat = Mathf.Clamp(lat, minLat, maxLat);
            target = otherP + fwd * along + right * lat;
            // foothold ground query
            RaycastHit hit;
            float topY = body.Pelvis.rb.position.y + 0.25f * s;
            Vector3 n = Vector3.up;
            float gy = bal.GroundHeight;
            if (bal.Ground(new Vector3(target.x, topY, target.z), 2.2f * s, out hit))
            {
                gy = hit.point.y;
                n = hit.normal;
                if (n.y < 0.6f || gy > Feet[other].planted.y + ls.maxStepUp * s)
                {
                    // too steep or too high: shorten the step
                    target = Vector3.Lerp(target, otherP, 0.4f);
                    if (bal.Ground(new Vector3(target.x, topY, target.z), 2.2f * s, out hit)) { gy = hit.point.y; n = hit.normal; }
                }
            }
            float yaw = HeadingYaw + side * 5f;
            Vector3 footFwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 fwdOnGround = Vector3.ProjectOnPlane(footFwd, n).normalized;
            bool isFoot = bal.FootPart[i] != null && PartInfo.IsFoot(bal.FootPart[i].id);
            Feet[i].target = new Vector3(target.x, gy, target.z) + n * AnchorHeight(i) - fwdOnGround * (isFoot ? 0.065f * s : 0f);
            Feet[i].targetYaw = yaw;
        }

        void ComputeClearance(int i)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float s = body.Scale;
            float maxH = Mathf.Max(Feet[i].lift.y, Feet[i].target.y) - AnchorHeight(i);
            float topY = body.Pelvis.rb.position.y;
            for (int k = 1; k <= 3; k++)
            {
                Vector3 p = Vector3.Lerp(Feet[i].lift, Feet[i].target, k / 4f);
                RaycastHit hit;
                if (bal.Ground(new Vector3(p.x, topY, p.z), 2f * s, out hit)) maxH = Mathf.Max(maxH, hit.point.y);
            }
            float baseY = Mathf.Max(Feet[i].lift.y, Feet[i].target.y) - AnchorHeight(i);
            Feet[i].clearance = ls.stepHeight * s + Mathf.Max(0f, maxH - baseY) + (StumbleTimer > 0f ? 0.04f * s : 0f);
        }

        /// <summary>Called when a swinging foot hits an obstacle: lift higher, or trip.</summary>
        public void OnFootBlocked(int side, float speed)
        {
            if (Feet[side].phase != FootPhase.Swing) return;
            if (speed < 0.9f) { Feet[side].clearance += 0.08f * body.Scale; return; }
            if (rng.Value < Sandbox.Boot.locomotion.stumbleChance)
            {
                Feet[side].blocked = true;
                StumbleTimer = 0.9f;
                body.Muscles.Flinch(0.6f);
            }
            else Feet[side].clearance += 0.1f * body.Scale;
        }

        void HopLogic(float dt, Vector3 vDes, Vector3 fwd, float strengthScale)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            int leg = bal.FootPart[0] != null && body.LegUsable[0] ? 0 : 1;
            if (bal.FootPart[leg] == null) return;
            int other = 1 - leg;
            hopTimer += dt;
            bool wantMove = vDes.magnitude > 0.1f || RecoveryUrgency > 0.05f;
            if (bal.FootPart[other] != null) { Feet[other].phase = FootPhase.Swing; Feet[other].t = 0.5f; Feet[other].dur = 1f; Feet[other].target = FootAnchor(leg) + Vector3.up * 0.25f * body.Scale; }
            switch (hopPhase)
            {
                case 0:
                    Feet[leg].phase = FootPhase.Stance;
                    if (wantMove && bal.FootGrounded[leg] && hopTimer > 0.35f) { hopPhase = 1; hopTimer = 0f; }
                    break;
                case 1:
                    if (hopTimer > 0.12f)
                    {
                        hopPhase = 2; hopTimer = 0f;
                        Vector3 dir = vDes.sqrMagnitude > 0.01f ? vDes.normalized : MathUtil.SafeNormal(bal.CapturePoint - bal.SupportCenter, fwd);
                        float up = ls.hopForce * Mathf.Lerp(0.6f, 1f, strengthScale);
                        bal.AddBodyVelocity(Vector3.up * up + dir * Mathf.Min(1.2f, vDes.magnitude + RecoveryUrgency) * 0.8f);
                    }
                    break;
                case 2:
                    Feet[leg].phase = FootPhase.Stance;
                    Feet[leg].planted = AirFootTarget(leg, Vector3.zero) + Vector3.up * 0.04f * body.Scale;
                    if (hopTimer > 0.15f && bal.FootGrounded[leg]) { hopPhase = 0; hopTimer = 0f; Feet[leg].planted = FootAnchor(leg); PlayStep(bal.FootPart[leg]); }
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

        /// <summary>Leg IK from the actual hips to the stance/swing foot targets. Each bone's local target is taken
        /// relative to its actual parent, so feet land where planned even when the torso wobbles.</summary>
        void ApplyLegTargets(Quaternion H, Vector3 fwd, float dt, float strengthScale)
        {
            var bal = body.Balance;
            var mus = body.Muscles;
            float s = body.Scale;
            var pelvis = body.Pelvis;
            for (int i = 0; i < 2; i++)
            {
                PartId thigh = i == 0 ? PartId.ThighL : PartId.ThighR, shin = i == 0 ? PartId.ShinL : PartId.ShinR, foot = i == 0 ? PartId.FootL : PartId.FootR;
                if (!body.Has(thigh) || !body.Has(shin) || bal.FootPart[i] == null) continue;
                var th = body.Part(thigh);
                var sh = body.Part(shin);
                Vector3 hip = th.rb.position;
                Vector3 ankle;
                float yaw;
                float pitch = 0f;
                Vector3 groundN = Vector3.up;
                var f = Feet[i];
                if (f.phase == FootPhase.Swing || f.phase == FootPhase.Lift)
                {
                    float u = Mathf.Clamp01(f.t / Mathf.Max(0.01f, f.dur));
                    float e = MathUtil.SmoothStep01(u);
                    Vector3 a = f.lift, d = f.target;
                    Vector3 dir = MathUtil.SafeNormal(MathUtil.Flat(d - a), fwd);
                    float c = f.clearance * 1.4f;
                    Vector3 b = a + Vector3.up * c + dir * 0.03f * s, cc = d + Vector3.up * c - dir * 0.03f * s;
                    ankle = MathUtil.Bezier(a, b, cc, d, e);
                    Vector3 next = MathUtil.Bezier(a, b, cc, d, MathUtil.SmoothStep01(Mathf.Min(1f, u + 0.02f)));
                    Feet[i].swingVel = (next - ankle) / Mathf.Max(0.001f, f.dur * 0.02f);
                    Feet[i].swingPos = ankle;
                    yaw = Mathf.LerpAngle(f.liftYaw, f.targetYaw, e);
                    pitch = u < 0.25f ? Mathf.Lerp(22f, 0f, u / 0.25f) : u > 0.75f ? Mathf.Lerp(0f, -10f, (u - 0.75f) / 0.25f) : 0f;
                }
                else
                {
                    ankle = f.planted;
                    yaw = f.plantedYaw;
                    if (bal.FootGrounded[i]) groundN = bal.FootGroundN[i];
                    if (groundN.y < 0.5f) groundN = Vector3.up;
                    // heel-to-toe roll at the end of the stance when walking
                    float behind = Vector3.Dot(ankle - pelvis.rb.position, fwd);
                    if (DesiredVelocity.sqrMagnitude > 0.05f && behind < -0.12f * s) pitch = Mathf.Clamp(-(behind + 0.12f * s) * 70f / s, 0f, 22f);
                }
                float l1 = (PartInfo.Origin[(int)thigh] - PartInfo.Origin[(int)shin]).magnitude * s;
                float l2 = (PartInfo.Origin[(int)shin] - PartInfo.Origin[(int)foot]).magnitude * s;
                var anchorPart = bal.FootPart[i];
                if (!PartInfo.IsFoot(anchorPart.id)) l2 = Mathf.Max(0.05f * s, anchorPart.segMax);
                Vector3 kneeDir = Quaternion.Euler(0f, yaw - HeadingYaw, 0f) * fwd;
                Vector3 d1, d2, p1, p2, kneePos;
                IKSolver.TwoBone(hip, ankle, kneeDir, l1, l2, LegReach, out d1, out d2, out p1, out p2, out kneePos);
                Quaternion thighW = Pose.FromDirs(thigh, d1, p1);
                Quaternion shinW = Pose.FromDirs(shin, d2, p2);
                float stiff = f.phase == FootPhase.Swing ? 0.75f : 1f;
                mus.Set(thigh, Quaternion.Inverse(pelvis.rb.rotation) * thighW, stiff);
                var thighDistal = body.DistalOf(thigh);
                mus.Set(shin, Quaternion.Inverse(thighDistal != null ? thighDistal.rb.rotation : th.rb.rotation) * shinW, stiff);
                if (body.Has(foot))
                {
                    Vector3 ff = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Vector3 fg = Vector3.ProjectOnPlane(ff, groundN).normalized;
                    Quaternion footW = Quaternion.LookRotation(fg, groundN) * Quaternion.AngleAxis(pitch, Vector3.right);
                    var shinDistal = body.DistalOf(shin);
                    mus.Set(foot, Quaternion.Inverse(shinDistal != null ? shinDistal.rb.rotation : sh.rb.rotation) * footW, stiff);
                }
            }
        }

        /// <summary>Stance feet are held in place (anti-slide), swing feet are guided along their arc. Both are bounded
        /// forces paired with the pelvis so they never add net momentum.</summary>
        void FootForces(float dt)
        {
            var ls = Sandbox.Boot.locomotion;
            var bal = body.Balance;
            float g = bal.G;
            var pelvis = body.Pelvis;
            for (int i = 0; i < 2; i++)
            {
                var fp = bal.FootPart[i];
                if (fp == null) continue;
                Vector3 anchorPos = FootAnchor(i);
                if (Feet[i].phase == FootPhase.Swing || Feet[i].phase == FootPhase.Lift)
                {
                    PartId shin = i == 0 ? PartId.ShinL : PartId.ShinR;
                    float m = fp.rb.mass + (body.Has(shin) && fp.id != shin ? body.Part(shin).rb.mass : 0f);
                    Vector3 err = Feet[i].swingPos - anchorPos;
                    Vector3 verr = Feet[i].swingVel - fp.rb.GetPointVelocity(anchorPos);
                    Vector3 f = m * (ls.swingFootGain * err + ls.swingFootDamping * verr) + Vector3.up * m * g * 0.8f;
                    float max = ls.swingForceLimit * body.MassScale;
                    if (f.magnitude > max) f = f.normalized * max;
                    if (!MathUtil.IsFinite(f)) continue;
                    fp.rb.AddForceAtPosition(f, anchorPos, ForceMode.Force);
                    pelvis.rb.AddForce(-f, ForceMode.Force);
                }
                else if (bal.FootGrounded[i])
                {
                    Vector3 err = MathUtil.Flat(Feet[i].planted - anchorPos);
                    Vector3 vel = MathUtil.Flat(fp.rb.GetPointVelocity(anchorPos));
                    Vector3 f = (ls.footLockGain * err - ls.footLockDamping * vel) * body.MassScale;
                    float max = ls.footLockLimit * body.MassScale;
                    if (f.magnitude > max) f = f.normalized * max;
                    if (!MathUtil.IsFinite(f)) continue;
                    fp.rb.AddForceAtPosition(f, anchorPos, ForceMode.Force);
                    pelvis.rb.AddForce(-f, ForceMode.Force);
                }
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
            var bal = body.Balance;
            if (bal.FootPart[0] != null) legOff0 = Mathf.Clamp(Vector3.Dot(FootAnchor(0) - hipC, fwd) / (0.3f * s), -1f, 1f);
            if (bal.FootPart[1] != null) legOff1 = Mathf.Clamp(Vector3.Dot(FootAnchor(1) - hipC, fwd) / (0.3f * s), -1f, 1f);
            float moving = Mathf.Clamp01(DesiredVelocity.magnitude / 0.6f);
            float swing = ls.armSwing * (1f + runFactor * 0.8f) * moving;
            float elbow = Mathf.Lerp(14f, 85f, runFactor * moving);
            float oneLeg = Mode == GaitMode.Hop ? 45f : 0f;
            float lostArmComp = (body.ArmUsable[0] ? 0f : 14f) + (body.ArmUsable[1] ? 0f : 14f);
            // counter-swing: each arm swings opposite to the leg on its side
            mus.Set(PartId.UpperArmL, Pose.ArmUpperLocal(-1f, 9f + oneLeg + lostArmComp, 4f - swing * legOff0, 0f));
            mus.Set(PartId.ForearmL, Pose.ElbowLocal(-1f, elbow, 0f));
            mus.Set(PartId.HandL, Quaternion.identity);
            mus.Set(PartId.UpperArmR, Pose.ArmUpperLocal(1f, 9f + oneLeg + lostArmComp, 4f - swing * legOff1, 0f));
            mus.Set(PartId.ForearmR, Pose.ElbowLocal(1f, elbow, 0f));
            mus.Set(PartId.HandR, Quaternion.identity);
            float counter = (legOff0 - legOff1) * ls.spineCounterRotation * moving;
            float lean = runFactor * 6f + crouchAmount * 10f + Hunch;
            float breath = brain.BreathOffset;
            mus.Set(PartId.Abdomen, Pose.SpineLocal(lean * 0.45f + breath * 0.4f, counter * 0.5f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(lean * 0.35f - breath, counter * 0.5f, 0f));
        }
    }
    #endregion

    #region MotorBrain
    public enum MotorState { Stand, Fall, Lie, GetUp, Crawl, Hop, Hang, Headless, Unconscious, Dead }

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
        public float BreathOffset;
        public Vector3 GazeTarget;
        public bool HasCommand;
        public Vector3 CommandTarget;
        public bool ExternalControl;      // the self-test or a network player drives DesiredVelocity directly
        public Vector3 ExternalVelocity;
        Vector3 wanderTarget;
        float wanderTimer;
        int getUpAttempts;
        float getUpPhaseTime;
        public int GetUpPhase;
        float lieQuietTime;
        float impactTime = -10f;
        bool impacted;
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
        float breathPhase;
        float painVoiceCooldown;
        float fallTimer, lowTimer;
        float flailTimer;
        float holdHeadTimer;
        Vector3 braceTarget;
        bool braceHasTarget;
        Rng rng;

        // behaviour weights (smoothly blended, never switched)
        public float WStagger, WBrace, WProtect, WClutch, WHunch, WHoldHead, WFlail, WStruggle;
        public readonly float[] WGrab = new float[2];

        // grab-and-hold
        public readonly ConfigurableJoint[] Grip = new ConfigurableJoint[2];
        public readonly float[] GripStamina = { 1f, 1f };
        readonly Collider[] grabCandidates = new Collider[24];
        readonly Collider[] grabTargetCol = new Collider[2];
        readonly Vector3[] grabTargetLocal = new Vector3[2];
        readonly bool[] hasGrabTarget = new bool[2];
        float grabSearchTimer;
        float regripCooldown;

        public MotorBrain(RagdollBody b) { body = b; rng = new Rng(b.GetInstanceIDSafe() * 13 + 5); wanderTarget = b.transform.position; }

        public void SetPossessed(bool on)
        {
            Possessed = on;
            HasCommand = false;
            if (!on) { ReleaseGrip(0); ReleaseGrip(1); }
        }

        public void CommandWalkTo(Vector3 p) { HasCommand = true; CommandTarget = p; }

        public void SetGrabbed(BodyPart part) { grabbedPart = part; grabStruggle = part != null ? 1f : 0f; if (part != null) body.LastInteractionTime = Time.time; }

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
            if (s == MotorState.GetUp) { GetUpPhase = 0; getUpPhaseTime = 0f; }
            if (s == MotorState.Lie) lieQuietTime = 0f;
            if (s == MotorState.Crawl) { crawlPhase = 0; crawlTimer = 0f; }
            if (s == MotorState.Fall) { impacted = false; fallTimer = 0f; }
        }

        float Will { get { return Sandbox.Boot.vitals.willToLive; } }
        float React { get { return Sandbox.Boot.general.reactions ? Sandbox.Boot.behaviour.reactionStrength : 0f; } }

        bool CanStand
        {
            get
            {
                var v = body.Vitals;
                bool legs = body.LegUsable[0] && body.LegUsable[1] && !body.LegsParalyzed;
                return legs && v.Consciousness > 0.5f && body.BrainPresent;
            }
        }

        bool CanHop
        {
            get
            {
                int legs = (body.LegUsable[0] ? 1 : 0) + (body.LegUsable[1] ? 1 : 0);
                return legs == 1 && !body.LegsParalyzed && body.Vitals.Consciousness > 0.6f && body.BrainPresent && Will > 0.3f;
            }
        }

        bool CanCrawl
        {
            get { return (body.ArmUsable[0] || body.ArmUsable[1]) && !body.BodyParalyzed && body.Vitals.Consciousness > 0.4f && body.BrainPresent && Will > 0.15f; }
        }

        public void FixedTick(float dt)
        {
            StateTime += dt;
            var v = body.Vitals;
            var mus = body.Muscles;
            var bal = body.Balance;
            mus.GlobalScale = 1f;
            mus.Brace = 0f;
            mus.LegScaleL = 1f; mus.LegScaleR = 1f;
            body.Locomotion.Control = 1f;
            if (painVoiceCooldown > 0f) painVoiceCooldown -= dt;
            if (shieldTimer > 0f) shieldTimer -= dt;
            if (clutchCooldown > 0f) clutchCooldown -= dt;
            if (flailTimer > 0f) flailTimer -= dt;
            if (holdHeadTimer > 0f) holdHeadTimer -= dt;
            if (regripCooldown > 0f) regripCooldown -= dt;
            breathPhase += dt * v.BreathRate / 60f * Mathf.PI * 2f;
            BreathOffset = Mathf.Sin(breathPhase) * 1.6f * v.BreathDepth;
            bal.ClearCommand();

            // global transitions
            if (v.Dead) SetState(MotorState.Dead);
            else if (!body.HeadAttached) SetState(MotorState.Headless);
            else if (v.Unconscious) { if (State != MotorState.Unconscious) SetState(MotorState.Unconscious); }
            else if (State == MotorState.Unconscious || State == MotorState.Dead || State == MotorState.Headless) SetState(MotorState.Lie);

            bool zeroG = bal.G < 0.5f;
            if (zeroG && (State == MotorState.Stand || State == MotorState.Hop || State == MotorState.GetUp || State == MotorState.Crawl)) SetState(MotorState.Lie);
            if (AnyGrip() && State != MotorState.Dead && State != MotorState.Unconscious && State != MotorState.Headless && State != MotorState.Hang && IsHanging()) SetState(MotorState.Hang);

            UpdateIntent(dt);
            UpdateLimp();

            switch (State)
            {
                case MotorState.Stand: TickStand(dt); break;
                case MotorState.Hop: TickHop(dt); break;
                case MotorState.Fall: TickFall(dt); break;
                case MotorState.Lie: TickLie(dt, zeroG); break;
                case MotorState.GetUp: TickGetUp(dt); break;
                case MotorState.Crawl: TickCrawl(dt); break;
                case MotorState.Hang: TickHang(dt); break;
                case MotorState.Headless: TickHeadless(dt); break;
                case MotorState.Unconscious: TickLimp(dt); break;
                case MotorState.Dead: TickLimp(dt); break;
            }
            bool conscious = State != MotorState.Dead && State != MotorState.Unconscious && State != MotorState.Headless;
            UpdateBehaviourWeights(dt, conscious);
            if (conscious)
            {
                ApplyBehaviourLayers(dt);
                Gaze(dt);
                if (Possessed) PossessArms(dt);
                GrabAndHold(dt);
            }
            else { ReleaseGrip(0); ReleaseGrip(1); }
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
                if (jumpLatch && State == MotorState.Stand) { loc.RequestJump(); jumpLatch = false; }
                if (State != MotorState.Stand) jumpLatch = false;
                Input.jump = false;
                return;
            }
            if (ExternalControl)
            {
                loc.DesiredVelocity = ExternalVelocity;
                if (ExternalVelocity.sqrMagnitude > 0.01f) loc.DesiredYaw = MathUtil.Yaw(ExternalVelocity);
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

        /// <summary>Limping: a hurt (but usable) leg gets less stiffness and a shorter stance.</summary>
        void UpdateLimp()
        {
            var loc = body.Locomotion;
            loc.LimpSide = -1f;
            loc.LimpAmount = 0f;
            if (React <= 0f) return;
            float worst = 0f;
            int side = -1;
            for (int s = 0; s < 2; s++)
            {
                if (!body.LegUsable[s]) continue;
                var th = body.Part(s == 0 ? PartId.ThighL : PartId.ThighR);
                var sh = body.Part(s == 0 ? PartId.ShinL : PartId.ShinR);
                float h = Mathf.Min(th != null ? th.HealthFraction : 1f, sh != null ? sh.HealthFraction : 1f);
                float hurt = Mathf.Clamp01((0.75f - h) / 0.5f);
                if (hurt > worst) { worst = hurt; side = s; }
            }
            if (side < 0) return;
            loc.LimpSide = side;
            loc.LimpAmount = worst * React;
            if (side == 0) body.Muscles.LegScaleL = 1f - worst * 0.35f; else body.Muscles.LegScaleR = 1f - worst * 0.35f;
        }
        #endregion

        #region States
        void TickStand(float dt)
        {
            var bal = body.Balance;
            var loc = body.Locomotion;
            var v = body.Vitals;
            var bs = Sandbox.Boot.balance;
            if (!CanStand)
            {
                if (CanHop) { SetState(MotorState.Hop); return; }
                SetState(MotorState.Fall);
                return;
            }
            loc.Mode = GaitMode.Normal;
            loc.LegReach = 1f;
            float strengthScale = Mathf.Clamp01(body.Muscles.ActiveLevel * v.MuscleFactor * (1f - v.Dizziness * 0.4f));
            loc.Control = 1f - WFlail;
            bool supported = loc.Tick(dt, strengthScale);
            // falling: torso tipped over, pelvis sunk, or the capture point unrecoverably outside the support
            bool tipped = bal.Tilt > bs.fallAngle;
            bool sunk = bal.PelvisHeight < bal.StandHeight * 0.58f && !loc.Jumping;
            fallTimer = tipped ? fallTimer + dt : 0f;
            lowTimer = sunk ? lowTimer + dt : 0f;
            bool unrecoverable = bal.SupportCount > 0 && StateTime > 0.5f && bal.SupportDistance > bs.unrecoverableDistance * body.Scale && !loc.IsSwinging(0) && !loc.IsSwinging(1);
            bool lost = bal.SupportCount == 0 && !loc.Jumping && StateTime > 0.4f && bal.PelvisHeight < bal.StandHeight * 0.7f;
            if (fallTimer > 0.12f || lowTimer > 0.35f || unrecoverable || lost || !supported || body.Muscles.Stun > 0.25f || WFlail > 0.6f)
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
            loc.LegReach = 1f;
            float strengthScale = Mathf.Clamp01(body.Muscles.ActiveLevel * body.Vitals.MuscleFactor);
            loc.Tick(dt, strengthScale);
            if (!bal.Upright && StateTime > 0.4f) SetState(MotorState.Fall);
            if (bal.SupportDistance > 0.6f * body.Scale && StateTime > 0.5f && !loc.Airborne) SetState(MotorState.Fall);
        }

        void TickFall(float dt)
        {
            var mus = body.Muscles;
            var bal = body.Balance;
            fallTimer += dt;
            body.Locomotion.ResetFeet();
            // tuck the chin and legs; arms brace (layer); go limp at the moment of impact
            bool torsoContact = TorsoOnGround();
            if (torsoContact && !impacted) { impacted = true; impactTime = Time.time; }
            float sinceImpact = Time.time - impactTime;
            bool limp = impacted && sinceImpact < 0.45f;
            mus.GlobalScale = limp ? 0.22f : Mathf.Lerp(0.75f, 0.55f, Mathf.Clamp01(fallTimer));
            mus.Brace = limp ? 0f : 0.35f;
            mus.Set(PartId.Neck, Pose.SpineLocal(28f, 0f, 0f));
            mus.Set(PartId.Head, Pose.SpineLocal(18f, 0f, 0f));
            mus.Set(PartId.Abdomen, Pose.SpineLocal(15f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(10f, 0f, 0f));
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                mus.Set(s == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 28f, 8f, 0f));
                mus.Set(s == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(40f));
                mus.Set(s == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(10f, 0f));
            }
            // relaxed arms as the base; the brace layer reaches for the ground
            mus.Set(PartId.UpperArmL, Pose.ArmUpperLocal(-1f, 30f, 40f, 0f)); mus.Set(PartId.ForearmL, Pose.ElbowLocal(-1f, 30f, 0f));
            mus.Set(PartId.UpperArmR, Pose.ArmUpperLocal(1f, 30f, 40f, 0f)); mus.Set(PartId.ForearmR, Pose.ElbowLocal(1f, 30f, 0f));
            float speed = body.ComVelocity.magnitude;
            if ((torsoContact && speed < 1.2f && StateTime > 0.4f) || StateTime > 3.5f) SetState(MotorState.Lie);
        }

        bool TorsoOnGround()
        {
            return (body.Has(PartId.Chest) && body.Chest.Touching) || (body.Has(PartId.Pelvis) && body.Pelvis.Touching) ||
                   (body.Has(PartId.Abdomen) && body.Part(PartId.Abdomen).Touching) || (body.Has(PartId.Head) && body.Head.Touching);
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
            float delay = bs.getUpDelay * Mathf.Lerp(2f, 0.7f, Will) * (1f + body.Vitals.Pain);
            if (lieQuietTime < delay) return;
            int attempts = Mathf.Max(0, bs.getUpAttempts - InjuryPenalty());
            if (CanStand && getUpAttempts < attempts) { getUpAttempts++; SetState(MotorState.GetUp); return; }
            if (CanHop && getUpAttempts < attempts) { getUpAttempts++; SetState(MotorState.GetUp); return; }
            if (CanCrawl && (!CanStand || getUpAttempts >= attempts)) { SetState(MotorState.Crawl); return; }
            if (StateTime > 12f && getUpAttempts > 0) { getUpAttempts--; StateTime = 0f; }
        }

        int InjuryPenalty()
        {
            int p = 0;
            for (int i = 0; i < PartInfo.Count; i++) p += body.FractureCount[i];
            if (body.Vitals.BloodFraction < 0.8f) p++;
            if (body.Vitals.Pain > 0.6f) p++;
            return p;
        }

        void RelaxedPose(float floatiness)
        {
            var mus = body.Muscles;
            mus.Set(PartId.UpperArmL, Pose.ArmUpperLocal(-1f, 18f + floatiness * 40f, 10f + floatiness * 30f, 0f)); mus.Set(PartId.ForearmL, Pose.ElbowLocal(-1f, 25f, 0f)); mus.Set(PartId.HandL, Quaternion.identity);
            mus.Set(PartId.UpperArmR, Pose.ArmUpperLocal(1f, 18f + floatiness * 40f, 10f + floatiness * 30f, 0f)); mus.Set(PartId.ForearmR, Pose.ElbowLocal(1f, 25f, 0f)); mus.Set(PartId.HandR, Quaternion.identity);
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                mus.Set(s == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 12f + floatiness * 30f, 6f, 0f));
                mus.Set(s == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(15f + floatiness * 40f));
                mus.Set(s == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(15f, 0f));
            }
            mus.Set(PartId.Abdomen, Pose.SpineLocal(BreathOffset * 0.4f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(-BreathOffset, 0f, 0f));
            mus.Set(PartId.Neck, Quaternion.identity);
            mus.Set(PartId.Head, Quaternion.identity);
        }

        void TickLimp(float dt)
        {
            body.Muscles.GlobalScale = 1f;
            RelaxedPose(0f);
        }

        void TickHeadless(float dt)
        {
            var mus = body.Muscles;
            var v = body.Vitals;
            float drive = v.HeadlessDrive;
            mus.GlobalScale = 0.7f;
            mus.Spasm(drive * 1.1f);
            RelaxedPose(0f);
            if (body.Balance.Upright && body.LegUsable[0] && body.LegUsable[1] && drive > 0.5f)
            {
                body.Locomotion.DesiredVelocity = body.Locomotion.Heading * Vector3.forward * 0.6f * drive;
                body.Locomotion.Control = drive * 0.6f;
                body.Locomotion.Tick(dt, drive * 0.6f);
            }
        }

        /// <summary>Recovery: roll to prone, push up with the arms, knees under the hips, rise. Every phase is driven by
        /// the same bounded assist as standing (upright drives + support forces), ramped in so it never pops.</summary>
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
            if (chest == null || !chest.attached) { SetState(MotorState.Lie); return; }
            float assist = bs.getUpAssist * Mathf.Lerp(0.6f, 1.2f, Will) * Mathf.Lerp(0.5f, 1f, body.Vitals.MuscleFactor);
            Vector3 chestFwd = chest.rb.rotation * Vector3.forward;
            Vector3 bodyAxis = MathUtil.SafeNormal(chest.rb.position - pelvis.rb.position, Vector3.up);
            Vector3 headingDir = MathUtil.SafeNormal(MathUtil.Flat(bodyAxis), MathUtil.Flat(chest.rb.rotation * Vector3.up));
            if (headingDir.sqrMagnitude < 0.01f) headingDir = Vector3.forward;
            Quaternion H = Quaternion.LookRotation(headingDir, Vector3.up);
            float chestH = chest.rb.worldCenterOfMass.y - bal.GroundHeight;
            float pelvisH = pelvis.rb.worldCenterOfMass.y - bal.GroundHeight;
            float ramp = Mathf.Clamp01(getUpPhaseTime / 0.35f);
            mus.GlobalScale = 1f;
            mus.Brace = 0.3f;
            var cmd = new BalanceCommand();
            cmd.pelvisTarget = pelvis.rb.rotation;
            cmd.chestTarget = chest.rb.rotation;
            cmd.velocityTarget = Vector3.zero;
            cmd.velocityWeight = 0.3f;

            switch (GetUpPhase)
            {
                case 0: // roll to prone (face down) unless already prone or sitting up
                    {
                        float faceUp = Vector3.Dot(chestFwd, Vector3.up);
                        if (faceUp < -0.4f || (chestH > 0.32f * s && bal.Tilt < 70f)) { NextGetUpPhase(); break; }
                        float dir = Vector3.Dot(Vector3.Cross(chestFwd, Vector3.down), bodyAxis) >= 0f ? 1f : -1f;
                        Quaternion roll = Quaternion.AngleAxis(dir * 100f, bodyAxis);
                        cmd.pelvisTarget = roll * pelvis.rb.rotation;
                        cmd.chestTarget = roll * chest.rb.rotation;
                        cmd.upright = 0.6f * ramp; cmd.chestUpright = 0.8f * ramp;
                        cmd.control = assist;
                        int reach = dir > 0f ? 1 : 0;
                        float side = reach == 0 ? -1f : 1f;
                        mus.Blend(reach == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, -20f, 100f, 0f), 1f);
                        mus.Blend(reach == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 20f, 0f), 1f);
                        int leg = 1 - reach;
                        mus.Set(leg == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(leg == 0 ? -1f : 1f, 60f, -15f, 0f));
                        mus.Set(leg == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(80f));
                        if (getUpPhaseTime > 2f) NextGetUpPhase();
                        break;
                    }
                case 1: // push up with the arms (chest up, torso pitched forward)
                    {
                        PushUpArms(headingDir, 0.5f);
                        mus.Set(PartId.Neck, Pose.SpineLocal(-28f, 0f, 0f));
                        mus.Set(PartId.Head, Pose.SpineLocal(-18f, 0f, 0f));
                        LegsTuck(0.45f);
                        cmd.chestTarget = H * Quaternion.Euler(Mathf.Lerp(85f, 55f, ramp), 0f, 0f);
                        cmd.pelvisTarget = H * Quaternion.Euler(80f, 0f, 0f);
                        cmd.upright = 0.4f * ramp; cmd.chestUpright = ramp;
                        cmd.liftChest = 1f; cmd.chestHeightTarget = bal.GroundHeight + 0.5f * s;
                        cmd.control = assist;
                        if (chestH > 0.42f * s && getUpPhaseTime > 0.4f) NextGetUpPhase();
                        if (getUpPhaseTime > 2.4f) NextGetUpPhase();
                        break;
                    }
                case 2: // knees under the hips (kneel / crouch)
                    {
                        PushUpArms(headingDir, 0.8f);
                        LegsTuck(1f);
                        cmd.chestTarget = H * Quaternion.Euler(Mathf.Lerp(55f, 35f, ramp), 0f, 0f);
                        cmd.pelvisTarget = H * Quaternion.Euler(Mathf.Lerp(70f, 35f, ramp), 0f, 0f);
                        cmd.upright = ramp; cmd.chestUpright = ramp;
                        cmd.heightTarget = bal.GroundHeight + 0.5f * s;
                        cmd.heightWeight = 0.7f; cmd.lift = 0.5f;
                        cmd.liftChest = 0.5f; cmd.chestHeightTarget = bal.GroundHeight + 0.75f * s;
                        cmd.control = assist;
                        if (pelvisH > 0.42f * s && getUpPhaseTime > 0.5f) NextGetUpPhase();
                        if (getUpPhaseTime > 2.6f) NextGetUpPhase();
                        break;
                    }
                case 3: // rise to standing
                    {
                        float u = Mathf.Clamp01(getUpPhaseTime / 1.3f);
                        float hTarget = bal.GroundHeight + Mathf.Lerp(0.55f * s, bal.StandHeight, u);
                        for (int k = 0; k < 2; k++)
                        {
                            float side = k == 0 ? -1f : 1f;
                            float flex = Mathf.Lerp(95f, 8f, u), knee = Mathf.Lerp(120f, 8f, u);
                            mus.Set(k == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, flex, 6f, 0f));
                            mus.Set(k == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(knee));
                            mus.Set(k == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(Mathf.Lerp(-20f, 0f, u), 0f));
                        }
                        mus.Set(PartId.Abdomen, Pose.SpineLocal(Mathf.Lerp(25f, 0f, u), 0f, 0f));
                        mus.Set(PartId.Chest, Pose.SpineLocal(Mathf.Lerp(20f, 0f, u), 0f, 0f));
                        ArmsForward(Mathf.Lerp(60f, 10f, u));
                        cmd.pelvisTarget = H * Quaternion.Euler(Mathf.Lerp(30f, 0f, u), 0f, 0f);
                        cmd.chestTarget = H * Quaternion.Euler(Mathf.Lerp(40f, 0f, u), 0f, 0f);
                        cmd.upright = 1f; cmd.chestUpright = 1f;
                        cmd.heightTarget = hTarget; cmd.heightWeight = 1f; cmd.lift = Sandbox.Boot.balance.weightSupport;
                        cmd.centerTarget = bal.SupportCount > 0 ? bal.SupportCenter : MathUtil.Flat(body.CenterOfMass);
                        cmd.centerWeight = 0.6f;
                        cmd.control = assist;
                        if (pelvisH > bal.StandHeight * 0.86f && bal.Tilt < 28f)
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
            if (State == MotorState.GetUp) bal.Cmd = cmd;
        }

        void NextGetUpPhase() { GetUpPhase++; getUpPhaseTime = 0f; }

        void PushUpArms(Vector3 heading, float extension)
        {
            float s = body.Scale;
            Vector3 right = new Vector3(heading.z, 0f, -heading.x);
            for (int k = 0; k < 2; k++)
            {
                if (!body.ArmUsable[k]) continue;
                float side = k == 0 ? -1f : 1f;
                PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                Vector3 shoulder = body.Part(up).rb.position;
                Vector3 hand = new Vector3(shoulder.x, body.Balance.GroundHeight + 0.05f * s, shoulder.z) + heading * 0.12f * s + right * side * 0.12f * s;
                ArmIK(k, hand, Vector3.up * 0.3f + right * side - heading * 0.2f, 1f, 1.2f + extension, 1f);
            }
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
                float side = k == 0 ? -1f : 1f;
                mus.Set(k == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 20f, flex, 0f));
                mus.Set(k == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 30f, 0f));
            }
        }

        /// <summary>Army crawl (both legs gone or paralysed, or too hurt to stand) with an arm step planner. With one
        /// usable leg the leg pushes too (drag gait).</summary>
        void TickCrawl(float dt)
        {
            var mus = body.Muscles;
            var bal = body.Balance;
            var chest = body.Chest;
            var pelvis = body.Pelvis;
            float s = body.Scale;
            if (CanStand && body.Vitals.Consciousness > 0.6f && StateTime > 3f) { SetState(MotorState.GetUp); return; }
            if (CanHop && StateTime > 4f && Will > 0.5f) { SetState(MotorState.GetUp); return; }
            if (!CanCrawl || chest == null || !chest.attached) { SetState(MotorState.Lie); return; }
            Vector3 chestFwd = chest.rb.rotation * Vector3.forward;
            Vector3 bodyAxis = MathUtil.SafeNormal(chest.rb.position - pelvis.rb.position, Vector3.forward);
            if (Vector3.Dot(chestFwd, Vector3.up) > 0.2f)
            {
                // must be prone: roll over with the bounded assist
                var rc = new BalanceCommand();
                Quaternion roll = Quaternion.AngleAxis(100f, bodyAxis);
                rc.chestTarget = roll * chest.rb.rotation; rc.pelvisTarget = roll * pelvis.rb.rotation;
                rc.upright = 0.5f; rc.chestUpright = 0.8f; rc.control = Sandbox.Boot.balance.getUpAssist;
                bal.Cmd = rc;
                return;
            }
            Vector3 bodyDir = MathUtil.SafeNormal(MathUtil.Flat(bodyAxis), MathUtil.Flat(chest.rb.rotation * Vector3.up));
            Vector3 goal = Possessed ? Input.move : (HasCommand ? MathUtil.Flat(CommandTarget - chest.rb.position) :
                (Sandbox.MainCamera != null ? MathUtil.Flat(Sandbox.MainCamera.transform.position - chest.rb.position) : bodyDir));
            if (ExternalControl) goal = ExternalVelocity;
            Vector3 dir = goal.sqrMagnitude > 0.04f ? goal.normalized : Vector3.zero;
            if (!Possessed && !ExternalControl && goal.magnitude < 1.2f) dir = Vector3.zero;
            var cmd = new BalanceCommand();
            cmd.chestTarget = chest.rb.rotation; cmd.pelvisTarget = pelvis.rb.rotation;
            if (dir.sqrMagnitude > 0f)
            {
                // steer: bounded yaw drive toward the goal, torso kept flat
                Quaternion yawTo = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
                cmd.chestTarget = Quaternion.Slerp(chest.rb.rotation, yawTo, 0.5f);
                cmd.chestUpright = 0.35f;
                cmd.control = Sandbox.Boot.balance.getUpAssist * 0.6f;
            }
            else dir = bodyDir;
            bal.Cmd = cmd;
            mus.Set(PartId.Neck, Pose.SpineLocal(-35f, 0f, 0f));
            mus.Set(PartId.Head, Pose.SpineLocal(-25f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(-8f, 0f, 0f));
            mus.Set(PartId.Abdomen, Pose.SpineLocal(-5f, 0f, 0f));
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
                ArmIK(arm, reach, Vector3.up + right * side2, 0.9f, 1f, 1f);
                if (crawlTimer > 0.55f) { crawlPhase = 1; crawlTimer = 0f; }
            }
            else
            {
                Vector3 pull = new Vector3(shoulder.x, bal.GroundHeight + 0.04f * s, shoulder.z) - dir * 0.1f * s + right * side2 * 0.18f * s;
                ArmIK(arm, pull, Vector3.up + right * side2 - dir, 1f, 1.3f, 1f);
                BodyPart contact = hand != null && hand.attached && hand.Touching ? hand : (fore != null && fore.Touching ? fore : null);
                float f = Sandbox.Boot.locomotion.crawlForce * Mathf.Clamp01(strength * 1.3f + 0.1f) * body.MassScale * Mathf.Lerp(0.5f, 1f, Will);
                if (contact != null) BalanceController.PairForce(chest.rb, dir * f + Vector3.up * f * 0.3f, contact.rb);
                if (crawlTimer > 0.65f)
                {
                    crawlPhase = 0; crawlTimer = 0f;
                    if (body.ArmUsable[1 - arm]) crawlArm = 1 - arm;
                    if (Sandbox.Audio != null && rng.Value < 0.3f) Sandbox.Audio.PlayVoice(Sfx.Grunt1, chest.rb.position, 0.35f, rng.Range(0.9f, 1.1f));
                }
            }
            int other = 1 - arm;
            if (body.ArmUsable[other])
            {
                Vector3 sh = body.Part(other == 0 ? PartId.UpperArmL : PartId.UpperArmR).rb.position;
                ArmIK(other, new Vector3(sh.x, bal.GroundHeight + 0.05f * s, sh.z) + dir * 0.2f * s + right * (other == 0 ? -1f : 1f) * 0.15f * s, Vector3.up, 0.9f, 0.8f, 1f);
            }
        }

        /// <summary>Hanging by the hands from a ledge / pole / car: legs dangle and kick, grip drains.</summary>
        void TickHang(float dt)
        {
            var mus = body.Muscles;
            if (!AnyGrip()) { SetState(TorsoOnGround() ? MotorState.Lie : MotorState.Fall); return; }
            if (body.Balance.SupportCount > 0 && body.Balance.PelvisHeight > body.Balance.StandHeight * 0.8f && CanStand) { ReleaseGrip(0); ReleaseGrip(1); SetState(MotorState.Stand); return; }
            mus.GlobalScale = 0.8f;
            float kick = Mathf.Sin(StateTime * 5f) * 25f * Mathf.Clamp01(Will);
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? -1f : 1f;
                mus.Set(s == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 15f + kick * (s == 0 ? 1f : -1f), 6f, 0f));
                mus.Set(s == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(25f + Mathf.Abs(kick)));
                mus.Set(s == 0 ? PartId.FootL : PartId.FootR, Pose.AnkleLocal(20f, 0f));
            }
            mus.Set(PartId.Abdomen, Pose.SpineLocal(5f, 0f, 0f));
            mus.Set(PartId.Chest, Pose.SpineLocal(0f, 0f, 0f));
        }
        #endregion

        #region Behaviour layers (Euphoria-style blending)
        void UpdateBehaviourWeights(float dt, bool conscious)
        {
            float react = React;
            var bal = body.Balance;
            var v = body.Vitals;
            float rate = 4f * dt;
            // stagger: capture point outside the support while standing -> windmill arms, widen stance
            float stagger = State == MotorState.Stand || State == MotorState.Hop ? Mathf.Clamp01(body.Locomotion.RecoveryUrgency * 1.6f + (body.Locomotion.StumbleTimer > 0f ? 0.6f : 0f)) : 0f;
            WStagger = Mathf.MoveTowards(WStagger, conscious ? stagger * react : 0f, rate * 2f);
            body.Locomotion.WidenStance = WStagger * 0.1f;
            // brace: falling toward a surface
            float brace = 0f;
            if (State == MotorState.Fall && conscious)
            {
                braceHasTarget = PredictImpact(out braceTarget);
                brace = impacted && Time.time - impactTime < 0.45f ? 0.2f : 1f;
            }
            WBrace = Mathf.MoveTowards(WBrace, brace * react, rate * 3f);
            WProtect = Mathf.MoveTowards(WProtect, shieldTimer > 0f && conscious ? react : 0f, rate * 3f);
            WFlail = Mathf.MoveTowards(WFlail, flailTimer > 0f && conscious ? react : 0f, rate * 4f);
            // clutching / hunching / holding the head
            UpdateClutch(dt, conscious);
            WClutch = Mathf.MoveTowards(WClutch, clutchTimer > 0f && conscious ? react : 0f, rate);
            float gut = conscious ? GutPain() : 0f;
            WHunch = Mathf.MoveTowards(WHunch, gut * react, rate * 0.5f);
            body.Locomotion.Hunch = WHunch * 22f;
            if (conscious && v.HeadTrauma > 0.3f && holdHeadTimer <= 0f && rng.Value < dt * 0.15f) holdHeadTimer = rng.Range(2f, 4f);
            WHoldHead = Mathf.MoveTowards(WHoldHead, holdHeadTimer > 0f && conscious ? react : 0f, rate);
            WStruggle = Mathf.MoveTowards(WStruggle, grabbedPart != null && conscious ? react : 0f, rate);
        }

        float GutPain()
        {
            float r = 0f;
            var list = body.Wounds.Wounds;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (!w.Active || w.part == null || !w.part.attached) continue;
                if (w.part.id == PartId.Abdomen || w.part.id == PartId.Pelvis) r = Mathf.Max(r, Mathf.Clamp01(w.severity + w.depth * 8f));
            }
            return r;
        }

        /// <summary>Where the body is about to hit: a ray along the fall direction from the chest.</summary>
        bool PredictImpact(out Vector3 point)
        {
            point = Vector3.zero;
            var chest = body.Chest;
            if (chest == null) return false;
            Vector3 v = body.ComVelocity + Physics.gravity * 0.25f;
            Vector3 dir = MathUtil.SafeNormal(v, Vector3.down);
            RaycastHit hit;
            if (Physics.Raycast(chest.rb.position, dir, out hit, 2.5f * body.Scale, Layers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                BodyPart bp = Registry.PartOf(hit.collider);
                if (bp != null && bp.body == body) return false;
                point = hit.point;
                return true;
            }
            point = chest.rb.position + dir * 1.2f * body.Scale;
            return true;
        }

        void ApplyBehaviourLayers(float dt)
        {
            var mus = body.Muscles;
            float s = body.Scale;
            var chest = body.Chest;
            if (chest == null || !chest.attached) return;
            // stagger: arms windmill, slight crouch via wider stance
            if (WStagger > 0.01f)
            {
                float t = Time.time * 7f;
                for (int k = 0; k < 2; k++)
                {
                    float side = k == 0 ? -1f : 1f;
                    float ph = t + k * Mathf.PI;
                    mus.Blend(k == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 70f + Mathf.Sin(ph) * 25f, 30f + Mathf.Cos(ph) * 50f, 0f), WStagger, 1.1f);
                    mus.Blend(k == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 30f, 0f), WStagger);
                }
            }
            // vehicle hit: flailing limbs
            if (WFlail > 0.01f)
            {
                float t = Time.time * 11f;
                for (int k = 0; k < 2; k++)
                {
                    float side = k == 0 ? -1f : 1f;
                    mus.Blend(k == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 80f + Mathf.Sin(t + k) * 60f, Mathf.Cos(t * 1.3f + k) * 90f, 0f), WFlail);
                    mus.Blend(k == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 40f + Mathf.Sin(t * 0.7f) * 40f, 0f), WFlail);
                    mus.Blend(k == 0 ? PartId.ThighL : PartId.ThighR, Pose.HipLocal(side, 30f + Mathf.Sin(t * 0.9f + k * 2f) * 40f, 10f, 0f), WFlail * 0.8f);
                    mus.Blend(k == 0 ? PartId.ShinL : PartId.ShinR, Pose.KneeLocal(40f + Mathf.Sin(t + k) * 35f), WFlail * 0.8f);
                }
            }
            // clutch a wound with the free hand
            if (WClutch > 0.01f && clutchWound != null && clutchWound.Active && clutchHand >= 0 && body.ArmUsable[clutchHand] && clutchWound.part != null)
            {
                Vector3 p = clutchWound.WorldPosition + clutchWound.WorldNormal * 0.035f * s;
                float side = clutchHand == 0 ? -1f : 1f;
                ArmIK(clutchHand, p, Vector3.down + chest.rb.rotation * (Vector3.right * side) * 0.6f + Vector3.back * 0.3f, 1f, 1f, WClutch);
                clutchWound.part.bloodiness = 1f;
                var hp = body.Part(clutchHand == 0 ? PartId.HandL : PartId.HandR);
                if (hp != null) hp.bloodiness = 1f;
            }
            // hold the head after head trauma
            if (WHoldHead > 0.01f && body.Head != null && body.Head.attached)
            {
                int k = body.ArmUsable[1] ? 1 : 0;
                if (body.ArmUsable[k] && k != clutchHand)
                {
                    float side = k == 0 ? -1f : 1f;
                    Vector3 p = body.Head.rb.position + body.Head.rb.rotation * (new Vector3(side * 0.09f, 0.08f, 0.02f) * s);
                    ArmIK(k, p, Vector3.down + chest.rb.rotation * Vector3.right * side, 1f, 0.9f, WHoldHead);
                }
            }
            // hunch over after gut hits
            if (WHunch > 0.01f)
            {
                mus.Blend(PartId.Abdomen, Pose.SpineLocal(22f, 0f, 0f), WHunch);
                mus.Blend(PartId.Chest, Pose.SpineLocal(15f, 0f, 0f), WHunch);
                mus.Blend(PartId.Neck, Pose.SpineLocal(15f, 0f, 0f), WHunch * 0.6f);
            }
            // brace for impact: hands toward the predicted impact point, chin tucked
            if (WBrace > 0.01f)
            {
                Vector3 target = braceHasTarget ? braceTarget : chest.rb.position + Vector3.down * 0.8f * s;
                Vector3 right = chest.rb.rotation * Vector3.right;
                for (int k = 0; k < 2; k++)
                {
                    if (!body.ArmUsable[k] || WGrab[k] > 0.5f) continue;
                    float side = k == 0 ? -1f : 1f;
                    Vector3 p = target + right * side * 0.18f * s + Vector3.up * 0.05f * s;
                    ArmIK(k, p, Vector3.up * 0.4f + right * side, 1f, 1.1f, WBrace);
                }
                mus.Blend(PartId.Neck, Pose.SpineLocal(30f, 0f, 0f), WBrace);
                mus.Blend(PartId.Head, Pose.SpineLocal(20f, 0f, 0f), WBrace);
            }
            // protect the head (explosions, incoming hits)
            if (WProtect > 0.01f)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (!body.ArmUsable[k]) continue;
                    float side = k == 0 ? -1f : 1f;
                    mus.Blend(k == 0 ? PartId.UpperArmL : PartId.UpperArmR, Pose.ArmUpperLocal(side, 25f, 115f, side * 20f), WProtect, 1.2f);
                    mus.Blend(k == 0 ? PartId.ForearmL : PartId.ForearmR, Pose.ElbowLocal(side, 125f, 0f), WProtect, 1.2f);
                    mus.Blend(k == 0 ? PartId.HandL : PartId.HandR, Pose.WristLocal(side, 10f, 0f), WProtect);
                }
                mus.Brace = Mathf.Max(mus.Brace, 0.6f * WProtect);
            }
        }

        void UpdateClutch(float dt, bool conscious)
        {
            if (!conscious) { clutchTimer = 0f; return; }
            bool allowed = State == MotorState.Stand || State == MotorState.Lie || State == MotorState.Hop || State == MotorState.Crawl;
            if (clutchTimer > 0f)
            {
                clutchTimer -= dt;
                if (clutchWound == null || !clutchWound.Active || clutchWound.part == null || !clutchWound.part.attached || clutchHand < 0 || !body.ArmUsable[clutchHand] || !allowed) clutchTimer = 0f;
                if (clutchTimer <= 0f) clutchCooldown = rng.Range(2f, 5f);
            }
            else if (clutchCooldown <= 0f && allowed)
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

        int PickClutchHand(Wound w)
        {
            if (w.part == null) return -1;
            PartId id = w.part.id;
            int arm = PartInfo.ArmSide(id);
            if (arm == 0) return body.ArmUsable[1] ? 1 : -1;
            if (arm == 1) return body.ArmUsable[0] ? 0 : -1;
            if (PartInfo.IsLeg(id) && State == MotorState.Stand && (id == PartId.ShinL || id == PartId.ShinR || PartInfo.IsFoot(id))) return -1;
            float side = PartInfo.SideSign(id);
            int pref = side < 0f ? 0 : side > 0f ? 1 : (rng.Value < 0.5f ? 0 : 1);
            if (body.ArmUsable[pref]) return pref;
            if (body.ArmUsable[1 - pref]) return 1 - pref;
            return -1;
        }

        /// <summary>Arm IK blended with weight w: shoulder/elbow/wrist targets so the hand reaches 'target'.</summary>
        public void ArmIK(int k, Vector3 target, Vector3 elbowHint, float reach, float stiffness, float w)
        {
            if (!body.ArmUsable[k] || w <= 0.001f) return;
            PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR, fore = k == 0 ? PartId.ForearmL : PartId.ForearmR, hand = k == 0 ? PartId.HandL : PartId.HandR;
            var chest = body.Chest;
            var upper = body.Part(up);
            if (chest == null || upper == null) return;
            float s = body.Scale;
            float l1 = (PartInfo.Origin[(int)fore] - PartInfo.Origin[(int)up]).magnitude * s;
            float l2 = (PartInfo.Origin[(int)hand] - PartInfo.Origin[(int)fore]).magnitude * s + 0.07f * s;
            Vector3 d1, d2, p1, p2, elbow;
            float r = reach * Mathf.Lerp(0.75f, 1f, upper.HealthFraction);
            IKSolver.TwoBone(upper.rb.position, target, elbowHint, l1, l2, r, out d1, out d2, out p1, out p2, out elbow);
            Quaternion upW = Pose.FromDirs(up, d1, p1);
            Quaternion foreW = Pose.FromDirs(fore, d2, p2);
            var mus = body.Muscles;
            mus.Blend(up, Quaternion.Inverse(chest.rb.rotation) * upW, w, stiffness);
            mus.Blend(fore, Quaternion.Inverse(upW) * foreW, w, stiffness);
            mus.Blend(hand, Quaternion.identity, w, stiffness);
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
            if (State == MotorState.Fall || State == MotorState.GetUp) return;
            Quaternion neck, headL;
            IKSolver.LookAt(chest.rb.rotation, eye, target, 70f, 40f, out neck, out headL);
            body.Muscles.Set(PartId.Neck, neck);
            body.Muscles.Set(PartId.Head, headL);
        }

        public void OnImpact(BodyPart part, float impulse, Vector3 dir)
        {
            if (React <= 0f || body.Vitals.Dead || body.Vitals.Unconscious) return;
            var mus = body.Muscles;
            float k = Mathf.Clamp01(impulse / 80f);
            mus.Flinch(k);
            if (impulse > 70f) mus.StunFor(Mathf.Lerp(0.2f, 0.9f, Mathf.Clamp01((impulse - 70f) / 150f)));
            if (part != null && part.id == PartId.Head && impulse > 25f) holdHeadTimer = Mathf.Max(holdHeadTimer, 2.5f);
            if (painVoiceCooldown <= 0f && impulse > 25f && Sandbox.Audio != null && body.HeadAttached)
            {
                painVoiceCooldown = rng.Range(0.6f, 1.4f);
                Sfx v = impulse > 90f ? (rng.Value < 0.5f ? Sfx.Pain0 : Sfx.Pain1) : (rng.Value < 0.33f ? Sfx.Grunt0 : rng.Value < 0.5f ? Sfx.Grunt1 : Sfx.Grunt2);
                Sandbox.Audio.PlayVoice(v, body.Head.rb.position, Mathf.Lerp(0.5f, 1f, k), rng.Range(0.92f, 1.08f));
            }
        }

        /// <summary>Hit by a vehicle: flail, lose balance, brace for the landing.</summary>
        public void OnVehicleHit(float speed, Vector3 dir)
        {
            if (body.Vitals.Dead) return;
            flailTimer = Mathf.Clamp(0.4f + speed * 0.12f, 0.6f, 2f);
            body.Muscles.StunFor(0.25f);
            body.Vitals.AddAdrenaline(0.5f);
            if (State == MotorState.Stand || State == MotorState.Hop || State == MotorState.GetUp) SetState(MotorState.Fall);
            ReleaseGrip(0); ReleaseGrip(1);
            if (painVoiceCooldown <= 0f && Sandbox.Audio != null && body.Head != null)
            {
                painVoiceCooldown = 1f;
                Sandbox.Audio.PlayVoice(Sfx.Pain1, body.Head.rb.position, 1f, rng.Range(0.95f, 1.1f));
            }
        }

        public void OnPain(float amount)
        {
            if (React <= 0f || body.Vitals.Dead || body.Vitals.Unconscious || !body.HeadAttached) return;
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
                if (!want) { if (Grip[k] != null && !IsHanging()) ReleaseGrip(k); continue; }
                if (!body.ArmUsable[k]) continue;
                PartId up = k == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                Vector3 shoulder = body.Part(up).rb.position;
                float armLen = 0.62f * body.Scale;
                Vector3 to = Input.aimPoint - shoulder;
                Vector3 target = to.magnitude < armLen * 1.15f ? Input.aimPoint : shoulder + MathUtil.SafeNormal(Input.aimDir, to.normalized) * armLen;
                float side = k == 0 ? -1f : 1f;
                ArmIK(k, target, Vector3.down + body.Chest.rb.rotation * (Vector3.right * side), 1f, 1.4f, 1f);
                TryGrip(k, null);
            }
        }

        void Struggle(float dt)
        {
            if (grabbedPart == null || !grabbedPart.attached) { grabbedPart = null; return; }
            var v = body.Vitals;
            if (v.Dead || v.Unconscious || !body.BrainPresent) return;
            float k = Mathf.Clamp01(v.Consciousness) * Will * React;
            if (k <= 0.05f) return;
            body.Muscles.Brace = Mathf.Max(body.Muscles.Brace, 0.5f);
            int freeArm = PartInfo.IsArm(grabbedPart.id) ? (PartInfo.IsLeft(grabbedPart.id) ? 1 : 0) : (rng.Value < 0.5f ? 0 : 1);
            if (body.ArmUsable[freeArm] && State != MotorState.Stand) ArmIK(freeArm, grabbedPart.rb.position, Vector3.down, 1f, 1.2f, WStruggle);
            if (rng.Value < dt * 3f * k) body.Muscles.Spasm(0.5f * k);
            if (rng.Value < dt * 4f * k && body.Chest != null)
            {
                Vector3 j = rng.OnUnitSphere() * 6f * k * body.MassScale;
                BalanceController.PairImpulse(grabbedPart.rb, j, body.Chest.rb);
            }
        }
        #endregion

        #region Grab and hold
        bool AnyGrip() { return Grip[0] != null || Grip[1] != null; }

        /// <summary>Hanging = gripping something while the feet don't carry the body.</summary>
        bool IsHanging()
        {
            if (!AnyGrip()) return false;
            var bal = body.Balance;
            return bal.SupportCount == 0 && bal.PelvisHeight > bal.StandHeight * 0.45f;
        }

        /// <summary>When falling or staggering near something graspable (ledge, pole, car, prop), the hands reach for it
        /// and latch on. Grip strength drains with fatigue and pain; at zero the hand lets go.</summary>
        void GrabAndHold(float dt)
        {
            var bh = Sandbox.Boot.behaviour;
            float react = React;
            bool seek = react > 0f && bh.grabAndHold && (State == MotorState.Fall || WStagger > 0.45f || State == MotorState.Hang) && !Possessed;
            for (int k = 0; k < 2; k++)
            {
                // grip stamina
                if (Grip[k] != null)
                {
                    var v = body.Vitals;
                    float drain = (0.06f + v.Pain * 0.25f + v.Fatigue * 0.15f + (1f - body.Muscles.ArmStrength(k)) * 0.2f) * Mathf.Lerp(1.6f, 0.5f, Will) / Mathf.Max(0.2f, bh.gripStrength);
                    GripStamina[k] -= drain * dt;
                    var hand = body.Part(k == 0 ? PartId.HandL : PartId.HandR);
                    if (GripStamina[k] <= 0f || hand == null || !hand.attached) { ReleaseGrip(k); regripCooldown = 1.5f; }
                    else Grip[k].breakForce = GripForce(k);
                }
                else GripStamina[k] = Mathf.MoveTowards(GripStamina[k], 1f, dt * 0.15f);
                bool want = seek && body.ArmUsable[k] && (Grip[k] != null || (hasGrabTarget[k] && GripStamina[k] > 0.2f && regripCooldown <= 0f));
                WGrab[k] = Mathf.MoveTowards(WGrab[k], want ? react : 0f, dt * 5f);
            }
            if (!seek) { hasGrabTarget[0] = hasGrabTarget[1] = false; return; }
            grabSearchTimer -= dt;
            if (grabSearchTimer <= 0f) { grabSearchTimer = 0.15f; FindGrabTargets(); }
            for (int k = 0; k < 2; k++)
            {
                if (WGrab[k] <= 0.01f) continue;
                if (Grip[k] != null)
                {
                    // hold on: hand stays where it is (the grip joint does the work), arm keeps tension
                    continue;
                }
                if (!hasGrabTarget[k] || grabTargetCol[k] == null) continue;
                Vector3 target = grabTargetCol[k].transform.TransformPoint(grabTargetLocal[k]);
                float side = k == 0 ? -1f : 1f;
                ArmIK(k, target, Vector3.down + body.Chest.rb.rotation * Vector3.right * side, 1f, 1.3f, WGrab[k]);
                TryGrip(k, grabTargetCol[k]);
            }
        }

        float GripForce(int k)
        {
            var bh = Sandbox.Boot.behaviour;
            return bh.gripStrength * 1600f * body.MassScale * Mathf.Clamp01(GripStamina[k] + 0.15f) * Mathf.Clamp01(body.Muscles.ArmStrength(k) + 0.3f);
        }

        void FindGrabTargets()
        {
            var chest = body.Chest;
            if (chest == null) return;
            float s = body.Scale;
            for (int k = 0; k < 2; k++)
            {
                hasGrabTarget[k] = false;
                if (!body.ArmUsable[k] || Grip[k] != null) continue;
                var upper = body.Part(k == 0 ? PartId.UpperArmL : PartId.UpperArmR);
                Vector3 shoulder = upper.rb.position;
                float reach = 0.72f * s;
                int n = Physics.OverlapSphereNonAlloc(shoulder, reach, grabCandidates, Layers.GrabMask, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var c = grabCandidates[i];
                    if (c == null) continue;
                    BodyPart bp = Registry.PartOf(c);
                    if (bp != null && bp.body == body) continue;
                    var mc = c as MeshCollider;
                    if (mc != null && !mc.convex) continue;
                    if (c is TerrainCollider) continue;
                    Vector3 cp = c.ClosestPoint(shoulder);
                    Vector3 d = cp - shoulder;
                    float dist = d.magnitude;
                    if (dist < 0.08f * s || dist > reach) continue;
                    // prefer edges above the waist, on the hand's side, against the fall direction
                    float height = cp.y - body.Pelvis.rb.position.y;
                    float sideBias = Vector3.Dot(d, chest.rb.rotation * Vector3.right) * (k == 0 ? -1f : 1f);
                    float score = dist - Mathf.Clamp(height, -0.3f, 0.6f) * 0.6f - sideBias * 0.3f;
                    if (score < best) { best = score; grabTargetCol[k] = c; grabTargetLocal[k] = c.transform.InverseTransformPoint(cp); hasGrabTarget[k] = true; }
                }
            }
        }

        void TryGrip(int k, Collider preferred)
        {
            if (Grip[k] != null) return;
            var hand = body.Part(k == 0 ? PartId.HandL : PartId.HandR);
            if (hand == null || !hand.attached || !hand.Touching || hand.contactCollider == null) return;
            if (preferred != null && hand.contactCollider != preferred) return;
            BodyPart other = Registry.PartOf(hand.contactCollider);
            if (other != null && other.body == body) return;
            var j = hand.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = hand.transform.InverseTransformPoint(hand.contactPoint);
            Rigidbody target = hand.contactBody;
            j.connectedBody = target;
            j.connectedAnchor = target != null ? target.transform.InverseTransformPoint(hand.contactPoint) : hand.contactPoint;
            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Limited;
            j.lowAngularXLimit = new SoftJointLimit { limit = -50f };
            j.highAngularXLimit = new SoftJointLimit { limit = 50f };
            j.angularYLimit = new SoftJointLimit { limit = 50f };
            j.angularZLimit = new SoftJointLimit { limit = 50f };
            j.enablePreprocessing = false;
            j.breakForce = GripForce(k);
            j.breakTorque = j.breakForce * 0.4f;
            Grip[k] = j;
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Thump, hand.rb.position, 0.25f, 1.3f);
        }

        public void ReleaseGrip(int k)
        {
            if (Grip[k] != null) UnityEngine.Object.Destroy(Grip[k]);
            Grip[k] = null;
        }

        public void ReleaseAllGrips() { ReleaseGrip(0); ReleaseGrip(1); }
        #endregion
    }
    #endregion
}
