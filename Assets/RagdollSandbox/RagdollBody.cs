// Ragdoll Sandbox - RagdollBody and BodyPart: the 17-part physical humanoid (1.75 m, 72 kg reference),
// its joints (ConfigurableJoint with anatomical limits and slerp "muscle" drives), connectivity/topology,
// the per-body update loop and the hooks used by every other system.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    #region Definitions
    public enum PartId
    {
        Pelvis, Abdomen, Chest, Neck, Head,
        UpperArmL, ForearmL, HandL, UpperArmR, ForearmR, HandR,
        ThighL, ShinL, FootL, ThighR, ShinR, FootR,
        Count
    }

    public enum MuscleGroup { None, Spine, Neck, Head, Shoulder, Elbow, Wrist, Hip, Knee, Ankle }

    public enum ColliderKind { CapsuleX, CapsuleY, CapsuleZ, Sphere, Box }

    /// <summary>Static anatomy tables. All positions are for the 1.75 m T-pose, feet centred at the origin, facing +Z.
    /// The character's left side is -X.</summary>
    public static class PartInfo
    {
        public const int Count = (int)PartId.Count;
        public const float RefHeight = 1.75f;

        public static readonly PartId[] Parent =
        {
            PartId.Count, PartId.Pelvis, PartId.Abdomen, PartId.Chest, PartId.Neck,
            PartId.Chest, PartId.UpperArmL, PartId.ForearmL, PartId.Chest, PartId.UpperArmR, PartId.ForearmR,
            PartId.Pelvis, PartId.ThighL, PartId.ShinL, PartId.Pelvis, PartId.ThighR, PartId.ShinR
        };

        public static readonly string[] Names =
        {
            "Pelvis", "Abdomen", "Chest", "Neck", "Head",
            "Left Upper Arm", "Left Forearm", "Left Hand", "Right Upper Arm", "Right Forearm", "Right Hand",
            "Left Thigh", "Left Shin", "Left Foot", "Right Thigh", "Right Shin", "Right Foot"
        };

        public static readonly string[] Short =
        {
            "pelvis", "abdomen", "chest", "neck", "head", "left arm", "left forearm", "left hand", "right arm", "right forearm", "right hand",
            "left leg", "left shin", "left foot", "right leg", "right shin", "right foot"
        };

        public static readonly float[] Mass = { 11f, 7f, 14f, 1.5f, 5f, 2.2f, 1.4f, 0.5f, 2.2f, 1.4f, 0.5f, 8f, 3.5f, 1.2f, 8f, 3.5f, 1.2f };
        public static readonly float[] MaxHealth = { 110f, 100f, 120f, 45f, 70f, 70f, 55f, 35f, 70f, 55f, 35f, 100f, 75f, 45f, 100f, 75f, 45f };
        /// <summary>Impact impulse (N*s) that fractures the main bone of the part.</summary>
        public static readonly float[] FractureImpulse = { 95f, 75f, 65f, 32f, 42f, 40f, 30f, 20f, 40f, 30f, 20f, 80f, 48f, 26f, 80f, 48f, 26f };

        public static readonly MuscleGroup[] Group =
        {
            MuscleGroup.None, MuscleGroup.Spine, MuscleGroup.Spine, MuscleGroup.Neck, MuscleGroup.Head,
            MuscleGroup.Shoulder, MuscleGroup.Elbow, MuscleGroup.Wrist, MuscleGroup.Shoulder, MuscleGroup.Elbow, MuscleGroup.Wrist,
            MuscleGroup.Hip, MuscleGroup.Knee, MuscleGroup.Ankle, MuscleGroup.Hip, MuscleGroup.Knee, MuscleGroup.Ankle
        };

        /// <summary>Transform origin of each part = its proximal joint pivot (pelvis: its centre).</summary>
        public static readonly Vector3[] Origin =
        {
            new Vector3(0f, 0.95f, 0f), new Vector3(0f, 1.03f, 0f), new Vector3(0f, 1.20f, 0f), new Vector3(0f, 1.47f, 0f), new Vector3(0f, 1.57f, 0f),
            new Vector3(-0.185f, 1.42f, 0f), new Vector3(-0.465f, 1.42f, 0f), new Vector3(-0.715f, 1.42f, 0f),
            new Vector3(0.185f, 1.42f, 0f), new Vector3(0.465f, 1.42f, 0f), new Vector3(0.715f, 1.42f, 0f),
            new Vector3(-0.09f, 0.90f, 0f), new Vector3(-0.09f, 0.48f, 0f), new Vector3(-0.09f, 0.075f, 0f),
            new Vector3(0.09f, 0.90f, 0f), new Vector3(0.09f, 0.48f, 0f), new Vector3(0.09f, 0.075f, 0f)
        };

        /// <summary>Distal end of each part (next joint, or tip), T-pose world.</summary>
        public static readonly Vector3[] Tip =
        {
            new Vector3(0f, 1.03f, 0f), new Vector3(0f, 1.20f, 0f), new Vector3(0f, 1.47f, 0f), new Vector3(0f, 1.57f, 0f), new Vector3(0f, 1.75f, 0f),
            new Vector3(-0.465f, 1.42f, 0f), new Vector3(-0.715f, 1.42f, 0f), new Vector3(-0.905f, 1.42f, 0f),
            new Vector3(0.465f, 1.42f, 0f), new Vector3(0.715f, 1.42f, 0f), new Vector3(0.905f, 1.42f, 0f),
            new Vector3(-0.09f, 0.48f, 0f), new Vector3(-0.09f, 0.075f, 0f), new Vector3(-0.09f, 0.0f, 0.19f),
            new Vector3(0.09f, 0.48f, 0f), new Vector3(0.09f, 0.075f, 0f), new Vector3(0.09f, 0.0f, 0.19f)
        };

        public static readonly ColliderKind[] ColKind =
        {
            ColliderKind.CapsuleX, ColliderKind.CapsuleX, ColliderKind.CapsuleX, ColliderKind.CapsuleY, ColliderKind.Sphere,
            ColliderKind.CapsuleX, ColliderKind.CapsuleX, ColliderKind.Box, ColliderKind.CapsuleX, ColliderKind.CapsuleX, ColliderKind.Box,
            ColliderKind.CapsuleY, ColliderKind.CapsuleY, ColliderKind.Box, ColliderKind.CapsuleY, ColliderKind.CapsuleY, ColliderKind.Box
        };

        /// <summary>Collider centre relative to the part origin.</summary>
        public static readonly Vector3[] ColCenter =
        {
            new Vector3(0f, 0f, -0.005f), new Vector3(0f, 0.085f, 0f), new Vector3(0f, 0.13f, -0.005f), new Vector3(0f, 0.05f, 0f), new Vector3(0f, 0.085f, 0.012f),
            new Vector3(-0.14f, 0f, 0f), new Vector3(-0.125f, 0f, 0f), new Vector3(-0.09f, 0f, 0.003f),
            new Vector3(0.14f, 0f, 0f), new Vector3(0.125f, 0f, 0f), new Vector3(0.09f, 0f, 0.003f),
            new Vector3(0f, -0.21f, 0f), new Vector3(0f, -0.2f, 0f), new Vector3(0f, -0.04f, 0.065f),
            new Vector3(0f, -0.21f, 0f), new Vector3(0f, -0.2f, 0f), new Vector3(0f, -0.04f, 0.065f)
        };

        /// <summary>Capsule: (radius, height, 0). Sphere: (radius,0,0). Box: size.</summary>
        public static readonly Vector3[] ColSize =
        {
            new Vector3(0.115f, 0.34f, 0f), new Vector3(0.1f, 0.29f, 0f), new Vector3(0.125f, 0.35f, 0f), new Vector3(0.048f, 0.13f, 0f), new Vector3(0.105f, 0f, 0f),
            new Vector3(0.047f, 0.30f, 0f), new Vector3(0.04f, 0.27f, 0f), new Vector3(0.18f, 0.035f, 0.088f),
            new Vector3(0.047f, 0.30f, 0f), new Vector3(0.04f, 0.27f, 0f), new Vector3(0.18f, 0.035f, 0.088f),
            new Vector3(0.072f, 0.46f, 0f), new Vector3(0.053f, 0.44f, 0f), new Vector3(0.095f, 0.07f, 0.255f),
            new Vector3(0.072f, 0.46f, 0f), new Vector3(0.053f, 0.44f, 0f), new Vector3(0.095f, 0.07f, 0.255f)
        };

        public static bool IsLeft(PartId p) { return p >= PartId.UpperArmL && p <= PartId.HandL || p >= PartId.ThighL && p <= PartId.FootL; }
        public static bool IsRight(PartId p) { return p >= PartId.UpperArmR && p <= PartId.HandR || p >= PartId.ThighR && p <= PartId.FootR; }
        public static float SideSign(PartId p) { return IsLeft(p) ? -1f : IsRight(p) ? 1f : 0f; }
        public static bool IsArm(PartId p) { return p >= PartId.UpperArmL && p <= PartId.HandR; }
        public static bool IsLeg(PartId p) { return p >= PartId.ThighL; }
        public static bool IsTorso(PartId p) { return p <= PartId.Chest; }
        public static bool IsLimbSegment(PartId p)
        {
            return p == PartId.UpperArmL || p == PartId.UpperArmR || p == PartId.ForearmL || p == PartId.ForearmR ||
                   p == PartId.ThighL || p == PartId.ThighR || p == PartId.ShinL || p == PartId.ShinR;
        }
        public static PartId Mirror(PartId p)
        {
            if (p >= PartId.UpperArmL && p <= PartId.HandL) return p + 3;
            if (p >= PartId.UpperArmR && p <= PartId.HandR) return p - 3;
            if (p >= PartId.ThighL && p <= PartId.FootL) return p + 3;
            if (p >= PartId.ThighR && p <= PartId.FootR) return p - 3;
            return p;
        }

        /// <summary>Rest bone direction (local) used by the pose/IK system.</summary>
        public static Vector3 BoneAxis(PartId p)
        {
            switch (p)
            {
                case PartId.UpperArmL: case PartId.ForearmL: case PartId.HandL: return Vector3.left;
                case PartId.UpperArmR: case PartId.ForearmR: case PartId.HandR: return Vector3.right;
                case PartId.ThighL: case PartId.ShinL: case PartId.ThighR: case PartId.ShinR: return Vector3.down;
                case PartId.FootL: case PartId.FootR: return Vector3.forward;
                default: return Vector3.up;
            }
        }

        /// <summary>Rest pole direction (local, perpendicular to the bone): knee cap, elbow tip, palm normal, foot top, front.</summary>
        public static Vector3 PoleAxis(PartId p)
        {
            switch (p)
            {
                case PartId.UpperArmL: case PartId.ForearmL: case PartId.UpperArmR: case PartId.ForearmR: return Vector3.back;
                case PartId.HandL: case PartId.HandR: return Vector3.down;
                case PartId.FootL: case PartId.FootR: return Vector3.up;
                default: return Vector3.forward;
            }
        }
    }

    /// <summary>Anatomical joint definition (left side / centre). Angles are "local rotation" degrees about the axis.</summary>
    public struct JointDef
    {
        public Vector3 axis, secondary;
        public float minX, maxX, yLimit, zLimit;

        public JointDef(Vector3 axis, Vector3 secondary, float minX, float maxX, float y, float z)
        {
            this.axis = axis; this.secondary = secondary; this.minX = minX; this.maxX = maxX; yLimit = y; zLimit = z;
        }

        public static JointDef For(PartId p)
        {
            JointDef d;
            switch (p)
            {
                case PartId.Abdomen: d = new JointDef(Vector3.right, Vector3.up, -22f, 35f, 22f, 20f); break;
                case PartId.Chest: d = new JointDef(Vector3.right, Vector3.up, -20f, 30f, 22f, 18f); break;
                case PartId.Neck: d = new JointDef(Vector3.right, Vector3.up, -30f, 40f, 35f, 25f); break;
                case PartId.Head: d = new JointDef(Vector3.right, Vector3.up, -35f, 30f, 45f, 25f); break;
                case PartId.UpperArmL: case PartId.UpperArmR: d = new JointDef(Vector3.forward, Vector3.up, -100f, 105f, 80f, 95f); break;
                case PartId.ForearmL: case PartId.ForearmR: d = new JointDef(Vector3.up, Vector3.forward, -3f, 150f, 3f, 75f); break;
                case PartId.HandL: case PartId.HandR: d = new JointDef(Vector3.forward, Vector3.up, -60f, 70f, 25f, 12f); break;
                case PartId.ThighL: case PartId.ThighR: d = new JointDef(Vector3.right, Vector3.up, -115f, 25f, 35f, 40f); break;
                case PartId.ShinL: case PartId.ShinR: d = new JointDef(Vector3.right, Vector3.up, -2f, 145f, 3f, 3f); break;
                case PartId.FootL: case PartId.FootR: d = new JointDef(Vector3.right, Vector3.up, -22f, 45f, 12f, 22f); break;
                default: d = new JointDef(Vector3.right, Vector3.up, -10f, 10f, 10f, 10f); break;
            }
            // Mirror for the right side: rotations about Y or Z flip sense, rotations about X keep it.
            if (PartInfo.IsRight(p) && Mathf.Abs(d.axis.x) < 0.5f) d.axis = -d.axis;
            return d;
        }
    }

    /// <summary>Runtime data cached for each joint at build time (joint-space conversion and base limits).</summary>
    public class JointRuntime
    {
        public ConfigurableJoint joint;
        public Quaternion startLocal = Quaternion.identity;
        public Quaternion frame = Quaternion.identity, frameInv = Quaternion.identity;
        public JointDef def;
        public MuscleGroup group;
        public float breakForceBase, breakTorqueBase;
        public bool fracturedLimits;

        public void CacheFrame()
        {
            Vector3 right = def.axis.normalized;
            Vector3 forward = Vector3.Cross(def.axis, def.secondary).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;
            frame = Quaternion.LookRotation(forward, up);
            frameInv = Quaternion.Inverse(frame);
        }

        /// <summary>Converts a desired local rotation (child relative to parent) into ConfigurableJoint.targetRotation.</summary>
        public Quaternion ToJointSpace(Quaternion desiredLocal)
        {
            return frameInv * (Quaternion.Inverse(desiredLocal) * startLocal) * frame;
        }

        public void ApplyLimits(float extra)
        {
            if (joint == null) return;
            joint.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(-def.maxX - extra, -177f, 0f) };
            joint.highAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(-def.minX + extra, 0f, 177f) };
            joint.angularYLimit = new SoftJointLimit { limit = Mathf.Clamp(def.yLimit + extra, 0f, 177f) };
            joint.angularZLimit = new SoftJointLimit { limit = Mathf.Clamp(def.zLimit + extra, 0f, 177f) };
        }
    }
    #endregion

    #region BodyPart
    /// <summary>One rigid segment of a ragdoll (or a sliced piece of one). Receives collisions and joint breaks.</summary>
    [DisallowMultipleComponent]
    public class BodyPart : MonoBehaviour, IDamageable
    {
        public PartId id;
        public RagdollBody body;
        public Rigidbody rb;
        public Collider col;
        public JointRuntime jr;               // joint to the parent (null for pelvis or once severed)
        public BodyPart parentPiece;
        public readonly List<BodyPart> children = new List<BodyPart>(3);
        public bool attached = true;          // connected to the living core (chest)
        public bool isPiece;                  // distal piece created by a blade cut
        public bool paralyzed;
        public float baseMass;

        // Health and accumulated damage
        public float health, maxHealth;
        public float blunt, cutDamage, pierce, blast, crush, shock;
        public bool fractured;
        public float fractureT = 0.5f;
        public bool ruptured;
        public float bloodiness;              // for bloody hand/footprints

        // Visuals
        public PartVisual visual;
        public readonly List<Renderer> innerRenderers = new List<Renderer>(8);

        // Contacts (updated from collision callbacks)
        public int lastContactFrame = -100;
        public int lastGroundFrame = -100;
        public Vector3 contactNormal = Vector3.up, contactPoint, groundNormal = Vector3.up, groundPoint;
        public Rigidbody contactBody;
        public Collider contactCollider;
        public float contactImpulse;
        public float slideSpeed;

        public string DisplayName { get { return PartInfo.Names[(int)id] + (isPiece ? " (piece)" : ""); } }
        public float HealthFraction { get { return maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f; } }
        public bool Grounded { get { return body != null && body.FixedFrame - lastGroundFrame <= 2; } }
        public bool Touching { get { return body != null && body.FixedFrame - lastContactFrame <= 2; } }
        public ConfigurableJoint Joint { get { return jr != null ? jr.joint : null; } }

        public Vector3 WorldCenter { get { return rb != null ? rb.worldCenterOfMass : transform.position; } }

        public void ApplyDamage(DamageInfo info)
        {
            if (body != null && body.Damage != null) body.Damage.Apply(this, info);
        }

        void OnCollisionEnter(Collision c)
        {
            if (body == null) return;
            Track(c);
            body.Damage.OnCollision(this, c);
        }

        void OnCollisionStay(Collision c)
        {
            if (body == null) return;
            Track(c);
        }

        void Track(Collision c)
        {
            BodyPart other = Registry.PartOf(c.collider);
            if (other != null && other.body == body) return;
            int n = c.contactCount;
            if (n <= 0) return;
            ContactPoint cp = c.GetContact(0);
            lastContactFrame = body.FixedFrame;
            contactNormal = cp.normal;
            contactPoint = cp.point;
            contactBody = c.rigidbody;
            contactCollider = c.collider;
            if (cp.normal.y > 0.45f)
            {
                lastGroundFrame = body.FixedFrame;
                groundNormal = cp.normal;
                groundPoint = cp.point;
                for (int i = 1; i < n && i < 4; i++)
                {
                    ContactPoint k = c.GetContact(i);
                    if (k.point.y < groundPoint.y) groundPoint = k.point;
                }
            }
            if (rb != null)
            {
                Vector3 v = Compat.GetVelocity(rb);
                if (c.rigidbody != null) v -= Compat.GetVelocity(c.rigidbody);
                slideSpeed = Vector3.ProjectOnPlane(v, cp.normal).magnitude;
            }
        }

        void OnJointBreak(float force)
        {
            // Any joint on this GameObject may have broken (grab, rope, impale, hand grip). The dismember system checks
            // on the next physics step whether it was the anatomical joint.
            if (body == null) return;
            body.Dismember.QueueBreakCheck(this);
        }

        void OnDestroy()
        {
            if (col != null) Registry.Unregister(col);
            if (rb != null) Registry.PartsByBody.Remove(rb);
        }
    }
    #endregion

    #region RagdollBody
    /// <summary>The whole character: builds itself, owns every per-body system and runs them in FixedUpdate.</summary>
    [DefaultExecutionOrder(-50)]
    public class RagdollBody : MonoBehaviour
    {
        public int Index;
        public float Scale = 1f;
        public float MassScale = 1f;
        public CharacterSettings Look;
        public readonly BodyPart[] Parts = new BodyPart[PartInfo.Count];
        public readonly List<BodyPart> AllPieces = new List<BodyPart>(24);
        public readonly List<BodyPart> AttachedParts = new List<BodyPart>(17);
        public int FixedFrame;
        public int TopologyVersion;

        public MuscleController Muscles;
        public BalanceController Balance;
        public LocomotionController Locomotion;
        public MotorBrain Brain;
        public VitalSystem Vitals;
        public WoundSystem Wounds;
        public OrganSystem Organs;
        public DamageSystem Damage;
        public DismemberSystem Dismember;

        public float TotalMass;               // attached mass
        public Vector3 CenterOfMass;
        public Vector3 ComVelocity;
        public bool Possessed;
        public bool HeadAttached = true;
        public bool BrainPresent = true;
        public readonly bool[] LegUsable = new bool[2];
        public readonly bool[] LegHasFoot = new bool[2];
        public readonly bool[] ArmUsable = new bool[2];
        public bool LegsParalyzed, BodyParalyzed;
        public float SpawnTime;

        public event Action<BodyPart, DamageInfo> OnDamaged;
        public event Action<PartId, BodyPart> OnSevered;
        public event Action<Wound> OnBleedStart;
        public event Action OnDeath, OnUnconscious, OnWake;
        public event Action<PartId> OnFracture;

        Vector3 lastGoodPosition;
        float lastGoodTime;
        bool xray;
        bool destroyed;

        public bool Alive { get { return Vitals != null && !Vitals.Dead; } }
        public BodyPart Pelvis { get { return Parts[(int)PartId.Pelvis]; } }
        public BodyPart Chest { get { return Parts[(int)PartId.Chest]; } }
        public BodyPart Head { get { return Parts[(int)PartId.Head]; } }
        public BodyPart Part(PartId id) { return Parts[(int)id]; }
        public bool Has(PartId id) { var p = Parts[(int)id]; return p != null && p.attached; }

        #region Build
        public static RagdollBody Spawn(Transform parent, Vector3 feetPosition, float yaw, CharacterSettings look, int index)
        {
            var go = new GameObject("Ragdoll " + (index + 1));
            go.transform.SetParent(parent, false);
            go.transform.position = feetPosition;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var body = go.AddComponent<RagdollBody>();
            try
            {
                body.Build(look, index);
            }
            catch (Exception e)
            {
                Debug.LogError("[RagdollSandbox] Ragdoll build failed: " + e);
                Destroy(go);
                return null;
            }
            return body;
        }

        void Build(CharacterSettings look, int index)
        {
            Index = index;
            Look = look;
            Scale = Mathf.Max(0.5f, look.height / PartInfo.RefHeight);
            float refMass = 0f;
            for (int i = 0; i < PartInfo.Count; i++) refMass += PartInfo.Mass[i];
            MassScale = look.mass / refMass;
            SpawnTime = Time.time;

            Damage = new DamageSystem(this);
            Vitals = new VitalSystem(this);
            Wounds = new WoundSystem(this);
            Organs = new OrganSystem(this);
            Dismember = new DismemberSystem(this);
            Muscles = new MuscleController(this);
            Balance = new BalanceController(this);
            Locomotion = new LocomotionController(this);
            Brain = new MotorBrain(this);

            var phys = Sandbox.Boot.physics;
            for (int i = 0; i < PartInfo.Count; i++) CreatePart((PartId)i, phys);
            for (int i = 1; i < PartInfo.Count; i++) CreateJoint(Parts[i]);
            IgnoreInternalCollisions();
            BodyVisuals.Build(this);
            Organs.Build();
            ApplyInitialPose();
            RebuildTopology();
            Muscles.Init();
            lastGoodPosition = transform.position;
            lastGoodTime = Time.time;
            if (Sandbox.XRay) SetXRay(true);
        }

        void CreatePart(PartId id, PhysicsSettingsData phys)
        {
            int i = (int)id;
            var go = new GameObject(PartInfo.Names[i]);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = PartInfo.Origin[i] * Scale;
            go.transform.localRotation = Quaternion.identity;
            var part = go.AddComponent<BodyPart>();
            part.id = id;
            part.body = this;
            part.maxHealth = PartInfo.MaxHealth[i];
            part.health = part.maxHealth;
            var rb = go.AddComponent<Rigidbody>();
            part.rb = rb;
            part.baseMass = PartInfo.Mass[i] * MassScale;
            rb.mass = part.baseMass;
            Compat.SetDamping(rb, 0.03f, 0.1f);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            bool ccd = id == PartId.Head || id == PartId.HandL || id == PartId.HandR || id == PartId.FootL || id == PartId.FootR;
            rb.collisionDetectionMode = ccd ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.ContinuousSpeculative;
            rb.maxAngularVelocity = phys.maxAngularVelocity;
            rb.maxDepenetrationVelocity = phys.maxDepenetrationVelocity;
            rb.solverIterations = phys.solverIterations;
            rb.solverVelocityIterations = phys.velocityIterations;
            rb.sleepThreshold = 0f;
            part.col = MakeCollider(go, PartInfo.ColKind[i], PartInfo.ColCenter[i] * Scale, PartInfo.ColSize[i] * Scale);
            if (id == PartId.FootL || id == PartId.FootR) part.col.sharedMaterial = PhysMats.Foot;
            else if (id == PartId.HandL || id == PartId.HandR) part.col.sharedMaterial = PhysMats.Hand;
            else if (PartInfo.IsTorso(id)) part.col.sharedMaterial = PhysMats.Torso;
            else part.col.sharedMaterial = PhysMats.Body;
            rb.ResetCenterOfMass();
            rb.ResetInertiaTensor();
            Vector3 inertia = rb.inertiaTensor;
            float minI = 0.0015f * MassScale;
            rb.inertiaTensor = new Vector3(Mathf.Max(inertia.x, minI), Mathf.Max(inertia.y, minI), Mathf.Max(inertia.z, minI));
            Parts[i] = part;
            AllPieces.Add(part);
            Registry.Parts[part.col] = part;
            Registry.PartsByBody[rb] = part;
            Registry.RegisterDamageable(part.col, part);
        }

        public static Collider MakeCollider(GameObject go, ColliderKind kind, Vector3 center, Vector3 size)
        {
            switch (kind)
            {
                case ColliderKind.Sphere:
                    var s = go.AddComponent<SphereCollider>(); s.center = center; s.radius = size.x; return s;
                case ColliderKind.Box:
                    var b = go.AddComponent<BoxCollider>(); b.center = center; b.size = size; return b;
                default:
                    var c = go.AddComponent<CapsuleCollider>();
                    c.center = center; c.radius = size.x; c.height = Mathf.Max(size.y, size.x * 2f);
                    c.direction = kind == ColliderKind.CapsuleX ? 0 : kind == ColliderKind.CapsuleY ? 1 : 2;
                    return c;
            }
        }

        void CreateJoint(BodyPart part)
        {
            PartId pid = PartInfo.Parent[(int)part.id];
            BodyPart parent = Parts[(int)pid];
            var jr = new JointRuntime();
            jr.def = JointDef.For(part.id);
            jr.group = PartInfo.Group[(int)part.id];
            var j = part.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = true;
            j.anchor = Vector3.zero;
            j.axis = jr.def.axis;
            j.secondaryAxis = jr.def.secondary;
            j.connectedBody = parent.rb;
            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Limited;
            jr.joint = j;
            jr.ApplyLimits(0f);
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 5f, maximumForce = 1000f };
            j.enablePreprocessing = false;
            j.enableCollision = false;
            jr.startLocal = Quaternion.Inverse(parent.transform.rotation) * part.transform.rotation;
            jr.CacheFrame();
            var dm = Sandbox.Boot.dismember;
            float bf = BreakForceFor(part.id, dm);
            jr.breakForceBase = bf;
            jr.breakTorqueBase = bf * dm.breakTorqueRatio;
            part.jr = jr;
            part.parentPiece = parent;
            parent.children.Add(part);
            ApplyBreakForce(part);
        }

        public static float BreakForceFor(PartId id, DismemberSettings dm)
        {
            switch (id)
            {
                case PartId.Abdomen: case PartId.Chest: return dm.spineBreakForce;
                case PartId.Neck: return dm.neckBreakForce * 1.15f;
                case PartId.Head: return dm.neckBreakForce;
                case PartId.UpperArmL: case PartId.UpperArmR: return dm.shoulderBreakForce;
                case PartId.ForearmL: case PartId.ForearmR: return dm.elbowBreakForce;
                case PartId.HandL: case PartId.HandR: return dm.wristBreakForce;
                case PartId.ThighL: case PartId.ThighR: return dm.hipBreakForce;
                case PartId.ShinL: case PartId.ShinR: return dm.kneeBreakForce;
                case PartId.FootL: case PartId.FootR: return dm.ankleBreakForce;
            }
            return 10000f;
        }

        /// <summary>Updates break thresholds from gore level and accumulated damage.</summary>
        public void ApplyBreakForce(BodyPart part)
        {
            if (part == null || part.jr == null || part.jr.joint == null) return;
            var dm = Sandbox.Boot.dismember;
            if (dm.gore == GoreLevel.Off || (!dm.allowTorsoTear && (part.id == PartId.Abdomen || part.id == PartId.Chest)))
            {
                part.jr.joint.breakForce = Mathf.Infinity;
                part.jr.joint.breakTorque = Mathf.Infinity;
                return;
            }
            float goreMul = dm.gore == GoreLevel.Reduced ? 1.6f : 1f;
            float h = Mathf.Min(part.HealthFraction, part.parentPiece != null ? part.parentPiece.HealthFraction : 1f);
            float k = (dm.damagedJointFloor + (1f - dm.damagedJointFloor) * h) * goreMul * MassScale;
            part.jr.joint.breakForce = part.jr.breakForceBase * k;
            part.jr.joint.breakTorque = part.jr.breakTorqueBase * k;
        }

        public void RefreshBreakForces()
        {
            for (int i = 0; i < AllPieces.Count; i++) ApplyBreakForce(AllPieces[i]);
        }

        void IgnoreInternalCollisions()
        {
            Ignore(PartId.Pelvis, PartId.Chest);
            Ignore(PartId.Abdomen, PartId.ThighL); Ignore(PartId.Abdomen, PartId.ThighR);
            Ignore(PartId.Abdomen, PartId.UpperArmL); Ignore(PartId.Abdomen, PartId.UpperArmR);
            Ignore(PartId.Chest, PartId.Head); Ignore(PartId.Chest, PartId.ForearmL); Ignore(PartId.Chest, PartId.ForearmR);
            Ignore(PartId.Neck, PartId.UpperArmL); Ignore(PartId.Neck, PartId.UpperArmR);
            Ignore(PartId.Pelvis, PartId.ShinL); Ignore(PartId.Pelvis, PartId.ShinR);
            Ignore(PartId.ThighL, PartId.FootL); Ignore(PartId.ThighR, PartId.FootR);
            Ignore(PartId.UpperArmL, PartId.HandL); Ignore(PartId.UpperArmR, PartId.HandR);
        }

        void Ignore(PartId a, PartId b) { Physics.IgnoreCollision(Parts[(int)a].col, Parts[(int)b].col, true); }

        /// <summary>Joints were created in the T-pose; now lower the arms into a relaxed standing pose.</summary>
        void ApplyInitialPose()
        {
            PoseArm(PartId.UpperArmL, PartId.ForearmL, PartId.HandL, -1f);
            PoseArm(PartId.UpperArmR, PartId.ForearmR, PartId.HandR, 1f);
        }

        void PoseArm(PartId upper, PartId fore, PartId hand, float side)
        {
            BodyPart u = Parts[(int)upper], f = Parts[(int)fore], h = Parts[(int)hand], c = Chest;
            Quaternion uLocal = Pose.ArmUpperLocal(side, 9f, 4f, 0f);
            Quaternion fLocal = Pose.ElbowLocal(side, 14f, 0f);
            Quaternion hLocal = Quaternion.identity;
            Quaternion uRot = c.transform.rotation * uLocal;
            Quaternion fRot = uRot * fLocal;
            Quaternion hRot = fRot * hLocal;
            Vector3 uPos = u.transform.position;
            Vector3 fPos = uPos + uRot * ((PartInfo.Origin[(int)fore] - PartInfo.Origin[(int)upper]) * Scale);
            Vector3 hPos = fPos + fRot * ((PartInfo.Origin[(int)hand] - PartInfo.Origin[(int)fore]) * Scale);
            u.transform.rotation = uRot;
            f.transform.SetPositionAndRotation(fPos, fRot);
            h.transform.SetPositionAndRotation(hPos, hRot);
            u.rb.position = uPos; u.rb.rotation = uRot;
            f.rb.position = fPos; f.rb.rotation = fRot;
            h.rb.position = hPos; h.rb.rotation = hRot;
        }

        public void ApplySolverSettings()
        {
            var phys = Sandbox.Boot.physics;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                p.rb.solverIterations = phys.solverIterations;
                p.rb.solverVelocityIterations = phys.velocityIterations;
                p.rb.maxAngularVelocity = phys.maxAngularVelocity;
                p.rb.maxDepenetrationVelocity = phys.maxDepenetrationVelocity;
            }
        }
        #endregion

        #region Topology
        readonly Queue<BodyPart> bfs = new Queue<BodyPart>(24);

        /// <summary>Recomputes which pieces are still connected to the chest (the living core) and the body's capabilities.
        /// Called on every loss (joint break, cut, paralysis) so the muscle/balance/locomotion model adapts live.</summary>
        public void RebuildTopology()
        {
            for (int i = 0; i < AllPieces.Count; i++) if (AllPieces[i] != null) AllPieces[i].attached = false;
            BodyPart core = Chest;
            if (core != null)
            {
                bfs.Clear();
                bfs.Enqueue(core);
                core.attached = true;
                while (bfs.Count > 0)
                {
                    var p = bfs.Dequeue();
                    if (p.parentPiece != null && p.Joint != null && !p.parentPiece.attached)
                    {
                        p.parentPiece.attached = true;
                        bfs.Enqueue(p.parentPiece);
                    }
                    for (int c = 0; c < p.children.Count; c++)
                    {
                        var ch = p.children[c];
                        if (ch == null || ch.attached || ch.Joint == null) continue;
                        ch.attached = true;
                        bfs.Enqueue(ch);
                    }
                }
            }
            AttachedParts.Clear();
            TotalMass = 0f;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || !p.attached) continue;
                AttachedParts.Add(p);
                TotalMass += p.rb.mass;
            }
            HeadAttached = Has(PartId.Head);
            BrainPresent = HeadAttached && Organs.BrainIntact;
            UpdateParalysis();
            for (int s = 0; s < 2; s++)
            {
                PartId thigh = s == 0 ? PartId.ThighL : PartId.ThighR;
                PartId shin = s == 0 ? PartId.ShinL : PartId.ShinR;
                PartId foot = s == 0 ? PartId.FootL : PartId.FootR;
                bool shinOk = Has(shin) && !Parts[(int)shin].paralyzed && !Dismember.IsShortened(Parts[(int)thigh]) && !Dismember.IsShortened(Parts[(int)shin]);
                LegUsable[s] = Has(thigh) && shinOk && !Parts[(int)thigh].paralyzed && Parts[(int)thigh].Joint != null;
                LegHasFoot[s] = LegUsable[s] && Has(foot);
                PartId up = s == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                PartId fore = s == 0 ? PartId.ForearmL : PartId.ForearmR;
                ArmUsable[s] = Has(up) && Has(fore) && !Parts[(int)up].paralyzed && !Dismember.IsShortened(Parts[(int)up]) && !Dismember.IsShortened(Parts[(int)fore]);
            }
            TopologyVersion++;
            if (Brain != null) Brain.OnTopologyChanged();
            if (Muscles != null) Muscles.OnTopologyChanged();
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null) continue;
                p.rb.sleepThreshold = p.attached && Alive ? 0f : 0.005f;
            }
        }

        void UpdateParalysis()
        {
            bool neck = Damage != null && Damage.SpinalInjury(PartId.Neck);
            bool upper = Damage != null && (Damage.SpinalInjury(PartId.Chest) || Damage.SpinalInjury(PartId.Abdomen));
            BodyParalyzed = neck;
            LegsParalyzed = neck || upper;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null) continue;
                bool par = false;
                if (neck && p.id != PartId.Head && p.id != PartId.Neck) par = true;
                if (upper && (p.id == PartId.Pelvis || p.id == PartId.Abdomen || PartInfo.IsLeg(p.id))) par = true;
                p.paralyzed = par;
            }
        }

        public void NotifySevered(PartId id, BodyPart piece)
        {
            var h = OnSevered; if (h != null) h(id, piece);
            SandboxEvents.RaiseSevered(this, id, piece);
        }
        public void NotifyDamaged(BodyPart p, DamageInfo d)
        {
            var h = OnDamaged; if (h != null) h(p, d);
            SandboxEvents.RaiseDamaged(this, p, d);
        }
        public void NotifyBleedStart(Wound w)
        {
            var h = OnBleedStart; if (h != null) h(w);
            SandboxEvents.RaiseBleedStart(this, w);
        }
        public void NotifyDeath()
        {
            var h = OnDeath; if (h != null) h();
            SandboxEvents.RaiseDeath(this);
            for (int i = 0; i < AllPieces.Count; i++) if (AllPieces[i] != null) AllPieces[i].rb.sleepThreshold = 0.005f;
        }
        public void NotifyUnconscious() { var h = OnUnconscious; if (h != null) h(); SandboxEvents.RaiseUnconscious(this); }
        public void NotifyWake() { var h = OnWake; if (h != null) h(); SandboxEvents.RaiseWoke(this); }
        public void NotifyFracture(PartId id) { var h = OnFracture; if (h != null) h(id); SandboxEvents.RaiseFractured(this, id); }
        #endregion

        #region Update loop
        void FixedUpdate()
        {
            if (destroyed || Pelvis == null) return;
            if (Sandbox.Rewind != null && Sandbox.Rewind.Rewinding) return;
            FixedFrame++;
            float dt = Time.fixedDeltaTime;
            Dismember.ProcessBreakChecks();
            ComputeCenterOfMass();
            Vitals.FixedTick(dt);
            Wounds.FixedTick(dt);
            Organs.FixedTick(dt);
            Damage.FixedTick(dt);
            Balance.Sense(dt);
            Brain.FixedTick(dt);
            Muscles.Apply(dt);
        }

        void Update()
        {
            if (destroyed) return;
            float dt = Time.deltaTime;
            Wounds.VisualTick(dt);
            Organs.VisualTick(dt);
            Brain.VisualTick(dt);
        }

        void LateUpdate()
        {
            if (destroyed) return;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p != null && p.visual != null) p.visual.LateTick();
            }
        }

        public void ComputeCenterOfMass()
        {
            Vector3 c = Vector3.zero, v = Vector3.zero;
            float m = 0f;
            for (int i = 0; i < AttachedParts.Count; i++)
            {
                var p = AttachedParts[i];
                if (p == null) continue;
                float pm = p.rb.mass;
                c += p.rb.worldCenterOfMass * pm;
                v += Compat.GetVelocity(p.rb) * pm;
                m += pm;
            }
            if (m > 0f) { CenterOfMass = c / m; ComVelocity = v / m; }
            else if (Pelvis != null) { CenterOfMass = Pelvis.rb.worldCenterOfMass; ComVelocity = Compat.GetVelocity(Pelvis.rb); }
        }
        #endregion

        #region Interaction hooks
        public void SetPossessed(bool on)
        {
            Possessed = on;
            Brain.SetPossessed(on);
        }

        public void SetXRay(bool on)
        {
            xray = on;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null) continue;
                if (p.visual != null) p.visual.SetXRay(on);
                for (int r = 0; r < p.innerRenderers.Count; r++) if (p.innerRenderers[r] != null) p.innerRenderers[r].enabled = on;
            }
            Organs.SetXRay(on);
        }

        public bool XRayOn { get { return xray; } }

        /// <summary>Called by the safety net every frame. Recovers from NaN / runaway / out-of-world states.</summary>
        public void ValidatePhysics(float maxSpeed, float killY, float bound)
        {
            bool bad = false;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                Vector3 pos = p.rb.position;
                Vector3 vel = Compat.GetVelocity(p.rb);
                if (!MathUtil.IsFinite(pos) || !MathUtil.IsFinite(vel) || !MathUtil.IsFinite(p.rb.rotation) || !MathUtil.IsFinite(p.rb.angularVelocity))
                {
                    bad = true;
                    break;
                }
                if (vel.sqrMagnitude > maxSpeed * maxSpeed) Compat.SetVelocity(p.rb, vel.normalized * maxSpeed);
                if (p.attached && (pos.y < killY || Mathf.Abs(pos.x) > bound || Mathf.Abs(pos.z) > bound)) bad = true;
                if (!p.attached && (pos.y < killY || Mathf.Abs(pos.x) > bound * 1.5f || Mathf.Abs(pos.z) > bound * 1.5f))
                {
                    // stray severed piece: just freeze it out of the way
                    p.rb.isKinematic = true;
                    p.transform.position = new Vector3(0f, killY + 1f, 0f);
                }
            }
            if (bad)
            {
                Debug.LogWarning("[RagdollSandbox] Ragdoll " + (Index + 1) + " left the valid simulation space; respawning.");
                Vector3 spawn = Time.time - lastGoodTime < 30f && MathUtil.IsFinite(lastGoodPosition) ? lastGoodPosition : Sandbox.World.RagdollSpawn;
                var boot = Sandbox.Boot;
                bool wasPossessed = Possessed;
                boot.RemoveRagdoll(this);
                var nb = boot.SpawnRagdoll(spawn + Vector3.up * 0.05f, transform.eulerAngles.y);
                if (wasPossessed && nb != null) { Sandbox.Possessed = nb; nb.SetPossessed(true); Sandbox.Cam.SetMode(CameraMode.Possess); }
                return;
            }
            if (Pelvis != null && Alive && Balance.Upright && Time.time - lastGoodTime > 1f)
            {
                lastGoodTime = Time.time;
                Vector3 p = Pelvis.rb.position;
                lastGoodPosition = new Vector3(p.x, Mathf.Max(p.y - 0.95f * Scale, Balance.GroundHeight), p.z);
            }
        }

        public void DestroyBody()
        {
            if (destroyed) return;
            destroyed = true;
            Wounds.StopLoops();
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null) continue;
                if (Sandbox.Decals != null) Sandbox.Decals.DetachFrom(p.transform);
                if (p.visual != null) p.visual.Dispose();
                if (p.transform.parent != transform) Destroy(p.gameObject);
            }
            Organs.DestroyAll();
            Destroy(gameObject);
        }
        #endregion

        #region Rewind (bonus)
        struct PoseFrame { public Vector3 p; public Quaternion r; public Vector3 v, w; }
        PoseFrame[] rewindBuf;
        int rewindPieces, rewindHead, rewindCount, rewindTopology = -1;
        bool rewinding;

        public void RecordRewind(float seconds)
        {
            if (destroyed) return;
            int frames = Mathf.Clamp(Mathf.CeilToInt(seconds / Mathf.Max(0.004f, Time.fixedDeltaTime)), 10, 2000);
            int pieces = AllPieces.Count;
            if (rewindBuf == null || rewindTopology != TopologyVersion || rewindPieces != pieces || rewindBuf.Length != frames * pieces)
            {
                rewindBuf = new PoseFrame[frames * pieces];
                rewindPieces = pieces;
                rewindTopology = TopologyVersion;
                rewindHead = 0;
                rewindCount = 0;
            }
            int framesCap = rewindBuf.Length / Mathf.Max(1, pieces);
            int baseIdx = rewindHead * pieces;
            for (int i = 0; i < pieces; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                rewindBuf[baseIdx + i] = new PoseFrame { p = p.rb.position, r = p.rb.rotation, v = Compat.GetVelocity(p.rb), w = p.rb.angularVelocity };
            }
            rewindHead = (rewindHead + 1) % framesCap;
            rewindCount = Mathf.Min(rewindCount + 1, framesCap);
        }

        public void BeginRewind()
        {
            if (rewindBuf == null || rewindCount == 0 || rewindTopology != TopologyVersion) return;
            rewinding = true;
            for (int i = 0; i < AllPieces.Count; i++) if (AllPieces[i] != null) AllPieces[i].rb.isKinematic = true;
        }

        public void RewindStep(int steps)
        {
            if (!rewinding || rewindBuf == null) return;
            int pieces = rewindPieces;
            int framesCap = rewindBuf.Length / Mathf.Max(1, pieces);
            for (int s = 0; s < steps && rewindCount > 1; s++)
            {
                rewindHead = (rewindHead - 1 + framesCap) % framesCap;
                rewindCount--;
            }
            int idx = ((rewindHead - 1 + framesCap) % framesCap) * pieces;
            for (int i = 0; i < pieces && i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                var f = rewindBuf[idx + i];
                p.rb.MovePosition(f.p);
                p.rb.MoveRotation(f.r);
            }
        }

        public void EndRewind()
        {
            if (!rewinding) return;
            rewinding = false;
            int pieces = rewindPieces;
            int framesCap = rewindBuf.Length / Mathf.Max(1, pieces);
            int idx = ((rewindHead - 1 + framesCap) % framesCap) * pieces;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                p.rb.isKinematic = false;
                if (i < pieces)
                {
                    var f = rewindBuf[idx + i];
                    p.rb.position = f.p;
                    p.rb.rotation = f.r;
                    Compat.SetVelocity(p.rb, f.v);
                    p.rb.angularVelocity = f.w;
                }
            }
        }
        #endregion
    }
    #endregion
}
