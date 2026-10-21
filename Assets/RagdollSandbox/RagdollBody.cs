// Ragdoll Sandbox - RagdollBody and BodyPart: the physical humanoid (1.75 m, 72 kg reference).
//  * 17 anatomical bones, each simulated by one or more rigid segments (a bone splits into extra segments when it
//    fractures or is cut anywhere along its length). Every segment is one Rigidbody + one primitive collider.
//  * ConfigurableJoints with anatomical limits and slerp "muscle" drives. Joint targets are converted from desired
//    local rotations with the rest relation captured when each joint was connected (robust to re-connection).
//  * The skeleton is invisible: one continuous skinned mesh (RagdollSkin.cs) is driven by the segments.
//  * Topology (what is still connected to the chest) is rebuilt after every loss so motor control adapts live.
using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using PhysMatRef = UnityEngine.PhysicsMaterial;
#else
using PhysMatRef = UnityEngine.PhysicMaterial;
#endif

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

        /// <summary>Approximate flesh radius of each part (skin weights, UV charts, cut caps).</summary>
        public static readonly float[] Radius = { 0.15f, 0.135f, 0.16f, 0.055f, 0.09f, 0.048f, 0.036f, 0.03f, 0.048f, 0.036f, 0.03f, 0.075f, 0.05f, 0.035f, 0.075f, 0.05f, 0.035f };
        /// <summary>Radius of the main bone (cut caps show a bone circle of this size).</summary>
        public static readonly float[] BoneRadius = { 0.03f, 0.02f, 0.02f, 0.012f, 0.06f, 0.011f, 0.009f, 0.006f, 0.011f, 0.009f, 0.006f, 0.014f, 0.013f, 0.009f, 0.014f, 0.013f, 0.009f };

        static List<PartId>[] children;
        public static List<PartId> Children(PartId p)
        {
            if (children == null)
            {
                children = new List<PartId>[Count];
                for (int i = 0; i < Count; i++) children[i] = new List<PartId>(3);
                for (int i = 1; i < Count; i++) children[(int)Parent[i]].Add((PartId)i);
            }
            return children[(int)p];
        }

        public static bool IsLeft(PartId p) { return p >= PartId.UpperArmL && p <= PartId.HandL || p >= PartId.ThighL && p <= PartId.FootL; }
        public static bool IsRight(PartId p) { return p >= PartId.UpperArmR && p <= PartId.HandR || p >= PartId.ThighR && p <= PartId.FootR; }
        public static float SideSign(PartId p) { return IsLeft(p) ? -1f : IsRight(p) ? 1f : 0f; }
        public static bool IsArm(PartId p) { return p >= PartId.UpperArmL && p <= PartId.HandR; }
        public static bool IsLeg(PartId p) { return p >= PartId.ThighL; }
        public static bool IsTorso(PartId p) { return p <= PartId.Chest; }
        public static bool IsFoot(PartId p) { return p == PartId.FootL || p == PartId.FootR; }
        public static bool IsHand(PartId p) { return p == PartId.HandL || p == PartId.HandR; }
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
        public static int LegSide(PartId p) { return p >= PartId.ThighL && p <= PartId.FootL ? 0 : p >= PartId.ThighR ? 1 : -1; }
        public static int ArmSide(PartId p) { return p >= PartId.UpperArmL && p <= PartId.HandL ? 0 : p >= PartId.UpperArmR && p <= PartId.HandR ? 1 : -1; }

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

        /// <summary>Local axis along which a part's collider (and its segments) extend.</summary>
        public static Vector3 SegmentAxis(PartId p)
        {
            switch (ColKind[(int)p])
            {
                case ColliderKind.CapsuleX: return IsTorso(p) ? Vector3.up : (IsLeft(p) ? Vector3.left : Vector3.right);
                case ColliderKind.CapsuleY: return IsLeg(p) ? Vector3.down : Vector3.up;
                case ColliderKind.Box: return IsFoot(p) ? Vector3.forward : (IsLeft(p) ? Vector3.left : Vector3.right);
                default: return Vector3.up;
            }
        }

        /// <summary>Extent of the part's collider along SegmentAxis (reference scale, metres from the part origin).</summary>
        public static void SegmentRange(PartId p, out float min, out float max)
        {
            int i = (int)p;
            Vector3 a = SegmentAxis(p);
            float c = Vector3.Dot(ColCenter[i], a);
            float half;
            switch (ColKind[i])
            {
                case ColliderKind.CapsuleX:
                    half = IsTorso(p) ? ColSize[i].x : ColSize[i].y * 0.5f;
                    break;
                case ColliderKind.CapsuleY: case ColliderKind.CapsuleZ: half = ColSize[i].y * 0.5f; break;
                case ColliderKind.Sphere: half = ColSize[i].x; break;
                default: half = Mathf.Abs(Vector3.Dot(ColSize[i], a)) * 0.5f; break;
            }
            min = c - half; max = c + half;
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

        /// <summary>Loose joint used to hold the two halves of a fractured bone together.</summary>
        public static JointDef Fracture(PartId p)
        {
            Vector3 a = PartInfo.SegmentAxis(p);
            Vector3 ax = Mathf.Abs(a.x) > 0.5f ? Vector3.forward : Vector3.right;
            return new JointDef(ax, Vector3.Cross(a, ax).normalized, -28f, 28f, 24f, 18f);
        }
    }

    public enum JointKind { Anatomical, Fracture }

    /// <summary>Runtime data cached for each joint (joint-space conversion and base limits).</summary>
    public class JointRuntime
    {
        public ConfigurableJoint joint;
        public JointKind kind;
        /// <summary>Child rotation relative to the connected body when the joint was (re)connected (its rest pose).</summary>
        public Quaternion startLocal = Quaternion.identity;
        public Quaternion frame = Quaternion.identity, frameInv = Quaternion.identity;
        public JointDef def;
        public MuscleGroup group;
        public float breakForceBase, breakTorqueBase;
        public float restOffsetDeg;   // angle between the rest pose and the anatomical (T-pose) rest
        public float extraLimit;

        public void CacheFrame()
        {
            Vector3 right = def.axis.normalized;
            Vector3 forward = Vector3.Cross(def.axis, def.secondary).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;
            frame = Quaternion.LookRotation(forward, up);
            frameInv = Quaternion.Inverse(frame);
        }

        /// <summary>Converts a desired local rotation (child relative to its connected body) into
        /// ConfigurableJoint.targetRotation, relative to the rest pose captured at connection time.</summary>
        public Quaternion ToJointSpace(Quaternion desiredLocal)
        {
            return frameInv * (Quaternion.Inverse(desiredLocal) * startLocal) * frame;
        }

        /// <summary>Applies the anatomical limits (widened by 'extra' degrees, and by the rest offset when the joint was
        /// re-connected away from the T-pose). Unity measures the joint angle with the opposite sign of the child's
        /// local rotation, hence low = -max and high = -min.</summary>
        public void ApplyLimits(float extra)
        {
            if (joint == null) return;
            extraLimit = extra;
            float e = extra + restOffsetDeg;
            joint.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(-def.maxX - e, -177f, 0f) };
            joint.highAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(-def.minX + e, 0f, 177f) };
            joint.angularYLimit = new SoftJointLimit { limit = Mathf.Clamp(def.yLimit + e, 0f, 177f) };
            joint.angularZLimit = new SoftJointLimit { limit = Mathf.Clamp(def.zLimit + e, 0f, 177f) };
        }
    }

    /// <summary>Operations applied to a body's mesh/topology, recorded so that network clients and late joiners can
    /// replay them deterministically.</summary>
    public enum BodyOpType { Slice, Hole, Dent, Squash, Stretch, Fracture, Sever, Heal }

    public struct BodyOp
    {
        public BodyOpType type;
        public int seg;          // segment index
        public int seg2;         // new segment index created by the op (slice / fracture), -1 if none
        public Vector3 a, b;     // bind-space point / normal (or split data)
        public float f0, f1, f2; // radius, depth, amount ...
        public int flags;
    }
    #endregion

    #region BodyPart
    /// <summary>One rigid segment of a ragdoll. A bone is one segment until it fractures or is cut; then extra segments
    /// are created that share the bone's frame (so the skinned mesh bind pose stays valid).</summary>
    [DisallowMultipleComponent]
    public class BodyPart : MonoBehaviour, IDamageable
    {
        public PartId id;
        public RagdollBody body;
        public Rigidbody rb;
        public Collider col;
        public JointRuntime jr;               // joint to the parent segment (anatomical or fracture), null when severed
        public BodyPart parentPiece;
        public readonly List<BodyPart> children = new List<BodyPart>(3);
        public bool attached = true;          // connected to the living core (chest)
        public bool isPiece;                  // created by a cut or fracture (not the primary segment of its bone)
        public bool cutOff;                   // separated by a cut
        public bool paralyzed;
        public float baseMass;
        public int segIndex;                  // index in body.Segments (= skinning bone index)
        public Vector3 segAxis;               // local axis of the segment
        public float segMin, segMax;          // extent along segAxis (scaled metres from the bone origin)
        public readonly List<Collider> ignored = new List<Collider>(8);

        // Health and accumulated damage
        public float health, maxHealth;
        public float blunt, cutDamage, pierce, blast, crush, shock;
        public bool fractured;
        public bool ruptured;
        public float bloodiness;              // for bloody hand/footprints

        // X-ray inner layers
        public readonly List<Renderer> innerRenderers = new List<Renderer>(8);
        public Transform inner;

        // Contacts (updated from collision callbacks)
        public int lastContactFrame = -100;
        public int lastGroundFrame = -100;
        public Vector3 contactNormal = Vector3.up, contactPoint, groundNormal = Vector3.up, groundPoint;
        public Rigidbody contactBody;
        public Collider contactCollider;
        public float slideSpeed;

        public string DisplayName { get { return PartInfo.Names[(int)id] + (cutOff ? " (severed)" : isPiece ? " (broken)" : ""); } }
        public float HealthFraction { get { return maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f; } }
        public bool Grounded { get { return body != null && body.FixedFrame - lastGroundFrame <= 2; } }
        public bool Touching { get { return body != null && body.FixedFrame - lastContactFrame <= 2; } }
        public ConfigurableJoint Joint { get { return jr != null ? jr.joint : null; } }
        public bool HasAnatomicalJoint { get { return jr != null && jr.joint != null && jr.kind == JointKind.Anatomical; } }
        public float SegCenter { get { return (segMin + segMax) * 0.5f; } }

        public Vector3 WorldCenter { get { return rb != null ? rb.worldCenterOfMass : transform.position; } }

        /// <summary>World point -> bind (T-pose mesh) space. Segments share their bone's frame.</summary>
        public Vector3 ToBind(Vector3 world)
        {
            return transform.InverseTransformPoint(world) + PartInfo.Origin[(int)id] * body.Scale;
        }

        public Vector3 DirToBind(Vector3 worldDir) { return transform.InverseTransformDirection(worldDir); }

        public Vector3 FromBind(Vector3 bind) { return transform.TransformPoint(bind - PartInfo.Origin[(int)id] * body.Scale); }

        public float AxisCoord(Vector3 world) { return Vector3.Dot(transform.InverseTransformPoint(world), segAxis); }

        public void ApplyDamage(DamageInfo info)
        {
            if (body != null && body.Damage != null) body.Damage.Apply(this, info);
        }

        void OnCollisionEnter(Collision c)
        {
            if (body == null || body.Puppet) return;
            Track(c);
            body.Damage.OnCollision(this, c);
        }

        void OnCollisionStay(Collision c)
        {
            if (body == null || body.Puppet) return;
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
        public int NetId;
        public int OwnerPlayer = -1;          // network player controlling this body (-1 none)
        public float Scale = 1f;
        public float MassScale = 1f;
        public CharacterSettings Look;
        public BodyLook Style;
        public readonly BodyPart[] Parts = new BodyPart[PartInfo.Count];
        /// <summary>Every segment ever created for this body, in creation order (index = skinning bone index).</summary>
        public readonly List<BodyPart> AllPieces = new List<BodyPart>(32);
        public readonly List<BodyPart> AttachedParts = new List<BodyPart>(24);
        public readonly int[] FractureCount = new int[PartInfo.Count];
        public readonly List<BodyOp> OpLog = new List<BodyOp>(32);
        public int FixedFrame;
        public int TopologyVersion;
        public bool Puppet;                   // network client: transforms driven by snapshots, no simulation

        public MuscleController Muscles;
        public BalanceController Balance;
        public LocomotionController Locomotion;
        public MotorBrain Brain;
        public VitalSystem Vitals;
        public WoundSystem Wounds;
        public OrganSystem Organs;
        public DamageSystem Damage;
        public DismemberSystem Dismember;
        public BodySkin Skin;

        public float TotalMass;               // attached mass
        public Vector3 CenterOfMass;
        public Vector3 ComVelocity;
        public bool Possessed;
        public bool HeadAttached = true;
        public bool BrainPresent = true;
        public readonly bool[] LegUsable = new bool[2];
        public readonly bool[] LegHasFoot = new bool[2];
        public readonly bool[] LegBroken = new bool[2];
        public readonly bool[] LegPresent = new bool[2];
        public readonly bool[] ArmUsable = new bool[2];
        public bool LegsParalyzed, BodyParalyzed;
        public float SpawnTime;
        public float LastInteractionTime = -100f;   // last time the player hurt/grabbed this body (self-test abort)

        public event Action<BodyPart, DamageInfo> OnDamaged;
        public event Action<PartId, BodyPart> OnSevered;
        public event Action<Wound> OnBleedStart;
        public event Action OnDeath, OnUnconscious, OnWake;
        public event Action<PartId> OnFracture;

        Vector3 lastGoodPosition;
        float lastGoodTime;
        bool xray;
        bool destroyed;
        bool armsPosed;

        public bool Alive { get { return Vitals != null && !Vitals.Dead; } }
        public bool Destroyed { get { return destroyed; } }
        public BodyPart Pelvis { get { return Parts[(int)PartId.Pelvis]; } }
        public BodyPart Chest { get { return Parts[(int)PartId.Chest]; } }
        public BodyPart Head { get { return Parts[(int)PartId.Head]; } }
        public BodyPart Part(PartId id) { return Parts[(int)id]; }
        public bool Has(PartId id) { var p = Parts[(int)id]; return p != null && p.attached; }
        public string Label { get { return "Ragdoll " + (Index + 1); } }

        /// <summary>The most distal attached segment of a bone (the one its child joint connects to).</summary>
        public BodyPart DistalOf(PartId id)
        {
            var p = Parts[(int)id];
            if (p == null) return null;
            for (int guard = 0; guard < 8; guard++)
            {
                BodyPart next = null;
                for (int i = 0; i < p.children.Count; i++)
                {
                    var c = p.children[i];
                    if (c != null && c.id == id && c.Joint != null) { next = c; break; }
                }
                if (next == null) break;
                p = next;
            }
            return p;
        }

        #region Build
        public static RagdollBody Spawn(Transform parent, Vector3 feetPosition, float yaw, CharacterSettings look, int index, bool puppet = false)
        {
            var go = new GameObject("Ragdoll " + (index + 1));
            go.transform.SetParent(parent, false);
            go.transform.position = feetPosition;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var body = go.AddComponent<RagdollBody>();
            body.Puppet = puppet;
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
            Style = look.look;
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
            for (int i = 1; i < PartInfo.Count; i++) CreateAnatomicalJoint(Parts[i], Parts[(int)PartInfo.Parent[i]]);
            IgnoreInternalCollisions();
            Skin = new BodySkin(this);
            Skin.Build();
            BuildInnerLayers();
            Organs.Build();
            RebuildTopology();
            Muscles.Init();
            Balance.Init();
            if (Puppet) SetPuppet(true);
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
            go.layer = Layers.Ragdoll;
            var part = go.AddComponent<BodyPart>();
            part.id = id;
            part.body = this;
            part.maxHealth = PartInfo.MaxHealth[i];
            part.health = part.maxHealth;
            part.segAxis = PartInfo.SegmentAxis(id);
            float mn, mx;
            PartInfo.SegmentRange(id, out mn, out mx);
            part.segMin = mn * Scale; part.segMax = mx * Scale;
            var rb = go.AddComponent<Rigidbody>();
            part.rb = rb;
            part.baseMass = PartInfo.Mass[i] * MassScale;
            rb.mass = part.baseMass;
            ConfigureBody(part, phys);
            part.col = MakeCollider(go, PartInfo.ColKind[i], PartInfo.ColCenter[i] * Scale, PartInfo.ColSize[i] * Scale);
            part.col.sharedMaterial = MaterialFor(id);
            ResetMassProperties(part);
            part.segIndex = AllPieces.Count;
            Parts[i] = part;
            AllPieces.Add(part);
            Registry.Parts[part.col] = part;
            Registry.PartsByBody[rb] = part;
            Registry.RegisterDamageable(part.col, part);
        }

        static PhysMatRef MaterialFor(PartId id)
        {
            if (PartInfo.IsFoot(id)) return PhysMats.Foot;
            if (PartInfo.IsHand(id)) return PhysMats.Hand;
            if (PartInfo.IsTorso(id)) return PhysMats.Torso;
            return PhysMats.Body;
        }

        void ConfigureBody(BodyPart part, PhysicsSettingsData phys)
        {
            var rb = part.rb;
            Compat.SetDamping(rb, 0.02f, 0.08f);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            bool small = PartInfo.IsHand(part.id) || PartInfo.IsFoot(part.id) || part.id == PartId.Head;
            rb.collisionDetectionMode = small ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.Discrete;
            rb.maxAngularVelocity = phys.maxAngularVelocity;
            rb.maxDepenetrationVelocity = phys.maxDepenetrationVelocity;
            rb.solverIterations = phys.ragdollSolverIterations;
            rb.solverVelocityIterations = phys.ragdollVelocityIterations;
            rb.sleepThreshold = 0f;
        }

        /// <summary>Recomputes centre of mass and inertia from the collider, then enforces a minimum inertia (small parts
        /// with tiny inertia are the usual source of joint jitter).</summary>
        public void ResetMassProperties(BodyPart part)
        {
            var rb = part.rb;
            rb.ResetCenterOfMass();
            rb.ResetInertiaTensor();
            Vector3 inertia = rb.inertiaTensor;
            float minI = Mathf.Max(0.0012f, rb.mass * 0.0035f) * Scale * Scale;
            float extra = PartInfo.IsHand(part.id) || PartInfo.IsFoot(part.id) ? 1.6f : 1f;
            rb.inertiaTensor = new Vector3(Mathf.Max(inertia.x * extra, minI), Mathf.Max(inertia.y * extra, minI), Mathf.Max(inertia.z * extra, minI));
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

        void CreateAnatomicalJoint(BodyPart part, BodyPart parent)
        {
            var jr = new JointRuntime();
            jr.kind = JointKind.Anatomical;
            jr.def = JointDef.For(part.id);
            jr.group = PartInfo.Group[(int)part.id];
            var j = part.gameObject.AddComponent<ConfigurableJoint>();
            SetupJoint(j, jr, part, parent, Vector3.zero);
            var dm = Sandbox.Boot.dismember;
            float bf = BreakForceFor(part.id, dm);
            jr.breakForceBase = bf;
            jr.breakTorqueBase = bf * dm.breakTorqueRatio;
            part.jr = jr;
            part.parentPiece = parent;
            parent.children.Add(part);
            ApplyBreakForce(part);
        }

        /// <summary>Configures a joint on 'part' connected to 'parent' at the given anchor (part local). Records the
        /// current relative rotation as the rest pose, and widens the anatomical limits by any offset from the T-pose.</summary>
        void SetupJoint(ConfigurableJoint j, JointRuntime jr, BodyPart part, BodyPart parent, Vector3 anchorLocal)
        {
            j.autoConfigureConnectedAnchor = false;
            j.anchor = anchorLocal;
            j.axis = jr.def.axis;
            j.secondaryAxis = jr.def.secondary;
            j.connectedAnchor = parent.transform.InverseTransformPoint(part.transform.TransformPoint(anchorLocal));
            j.connectedBody = parent.rb;
            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Limited;
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 2f, maximumForce = 1000f };
            j.enablePreprocessing = false;
            j.enableCollision = false;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.04f * Scale;
            j.projectionAngle = 12f;
            jr.joint = j;
            jr.startLocal = Quaternion.Inverse(parent.transform.rotation) * part.transform.rotation;
            jr.restOffsetDeg = jr.kind == JointKind.Anatomical ? Quaternion.Angle(Quaternion.identity, jr.startLocal) : 0f;
            jr.CacheFrame();
            jr.ApplyLimits(0f);
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
            if (part.jr.kind == JointKind.Fracture)
            {
                bool off = dm.gore == GoreLevel.Off;
                part.jr.joint.breakForce = off ? Mathf.Infinity : part.jr.breakForceBase;
                part.jr.joint.breakTorque = off ? Mathf.Infinity : part.jr.breakTorqueBase;
                return;
            }
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
            // neighbours and near neighbours never collide (they overlap at the joints)
            for (int i = 1; i < PartInfo.Count; i++) Ignore(Parts[i], Parts[(int)PartInfo.Parent[i]]);
            Ignore(PartId.Pelvis, PartId.Chest);
            Ignore(PartId.Abdomen, PartId.ThighL); Ignore(PartId.Abdomen, PartId.ThighR);
            Ignore(PartId.Abdomen, PartId.UpperArmL); Ignore(PartId.Abdomen, PartId.UpperArmR);
            Ignore(PartId.Chest, PartId.Head); Ignore(PartId.Chest, PartId.ForearmL); Ignore(PartId.Chest, PartId.ForearmR);
            Ignore(PartId.Neck, PartId.UpperArmL); Ignore(PartId.Neck, PartId.UpperArmR);
            Ignore(PartId.Pelvis, PartId.ShinL); Ignore(PartId.Pelvis, PartId.ShinR);
            Ignore(PartId.ThighL, PartId.FootL); Ignore(PartId.ThighR, PartId.FootR);
            Ignore(PartId.UpperArmL, PartId.HandL); Ignore(PartId.UpperArmR, PartId.HandR);
            // thighs touch at the crotch on every step; letting them collide only produces trips
            Ignore(PartId.ThighL, PartId.ThighR);
        }

        void Ignore(PartId a, PartId b) { Ignore(Parts[(int)a], Parts[(int)b]); }

        public void Ignore(BodyPart a, BodyPart b)
        {
            if (a == null || b == null || a.col == null || b.col == null || a == b) return;
            Physics.IgnoreCollision(a.col, b.col, true);
            if (!a.ignored.Contains(b.col)) a.ignored.Add(b.col);
            if (!b.ignored.Contains(a.col)) b.ignored.Add(a.col);
        }

        /// <summary>Joints are created in the T-pose. After the first physics step has captured every rest pose, the
        /// arms are lowered into a relaxed standing pose (no joint re-capture can happen any more).</summary>
        void ApplyInitialPose()
        {
            armsPosed = true;
            if (Puppet) return;
            PoseArm(PartId.UpperArmL, PartId.ForearmL, PartId.HandL, -1f);
            PoseArm(PartId.UpperArmR, PartId.ForearmR, PartId.HandR, 1f);
        }

        void PoseArm(PartId upper, PartId fore, PartId hand, float side)
        {
            BodyPart u = Parts[(int)upper], f = Parts[(int)fore], h = Parts[(int)hand], c = Chest;
            if (u == null || f == null || h == null || c == null || !u.attached || !f.attached || !h.attached) return;
            Quaternion uLocal = Pose.ArmUpperLocal(side, 9f, 4f, 0f);
            Quaternion fLocal = Pose.ElbowLocal(side, 14f, 0f);
            Quaternion uRot = c.rb.rotation * uLocal;
            Quaternion fRot = uRot * fLocal;
            Quaternion hRot = fRot;
            Vector3 uPos = u.rb.position;
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
                int it = p.attached ? phys.ragdollSolverIterations : Mathf.Max(4, phys.ragdollSolverIterations / 2);
                p.rb.solverIterations = it;
                p.rb.solverVelocityIterations = phys.ragdollVelocityIterations;
                p.rb.maxAngularVelocity = phys.maxAngularVelocity;
                p.rb.maxDepenetrationVelocity = phys.maxDepenetrationVelocity;
            }
        }

        void BuildInnerLayers()
        {
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var part = Parts[i];
                var go = new GameObject("Inner");
                go.transform.SetParent(part.transform, false);
                go.transform.localScale = Vector3.one * Scale;
                part.inner = go.transform;
                AddInner(part, BodyMeshLibrary.Skeleton[i], MaterialFactory.Bone, "Bones");
                AddInner(part, BodyMeshLibrary.Arteries[i], MaterialFactory.Artery, "Arteries");
            }
        }

        public MeshRenderer AddInner(BodyPart part, Mesh mesh, Material mat, string name)
        {
            if (mesh == null || part.inner == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(part.inner, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.enabled = xray;
            part.innerRenderers.Add(mr);
            return mr;
        }
        #endregion

        #region Segments
        /// <summary>Splits a segment at an axis coordinate (local metres along segAxis). The original keeps the proximal
        /// part. A fracture joins the halves with a loose joint; a cut separates them. Children whose joints lie beyond
        /// the split move to the new segment. Returns the new distal segment (or null if the split is not possible).</summary>
        public BodyPart SplitSegment(BodyPart s, float split, bool fracture)
        {
            if (s == null || s.rb == null || destroyed) return null;
            float len = s.segMax - s.segMin;
            if (len < 0.03f * Scale) return null;
            split = Mathf.Clamp(split, s.segMin + len * 0.1f, s.segMax - len * 0.1f);
            float frac = (split - s.segMin) / len;
            var phys = Sandbox.Boot.physics;
            Vector3 pos = s.rb.position;
            Quaternion rot = s.rb.rotation;
            Vector3 angVel = s.rb.angularVelocity;

            var go = new GameObject(PartInfo.Names[(int)s.id] + (fracture ? " (broken)" : " (cut)"));
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(s.transform.position, s.transform.rotation);
            go.layer = Layers.Ragdoll;
            var n = go.AddComponent<BodyPart>();
            n.id = s.id;
            n.body = this;
            n.isPiece = true;
            n.cutOff = !fracture;
            n.maxHealth = s.maxHealth;
            n.health = s.health;
            n.fractured = s.fractured;
            n.segAxis = s.segAxis;
            n.segMin = split; n.segMax = s.segMax;
            s.segMax = split;
            n.paralyzed = s.paralyzed;
            var rb = go.AddComponent<Rigidbody>();
            n.rb = rb;
            float total = s.rb.mass;
            rb.mass = Mathf.Max(0.08f, total * (1f - frac));
            s.rb.mass = Mathf.Max(0.08f, total * frac);
            n.baseMass = s.baseMass * (1f - frac);
            s.baseMass *= frac;
            ConfigureBody(n, phys);
            n.col = CloneCollider(s.col, go);
            n.col.sharedMaterial = s.col.sharedMaterial;
            FitSegmentCollider(s);
            FitSegmentCollider(n);
            ResetMassProperties(s);
            ResetMassProperties(n);
            n.segIndex = AllPieces.Count;
            AllPieces.Add(n);
            Registry.Parts[n.col] = n;
            Registry.PartsByBody[rb] = n;
            Registry.RegisterDamageable(n.col, n);

            // inherit velocity at the new centre of mass
            Compat.SetVelocity(rb, s.rb.GetPointVelocity(rb.worldCenterOfMass));
            rb.angularVelocity = angVel;

            // collisions: same ignore list as the original, plus the original itself
            for (int i = 0; i < s.ignored.Count; i++)
            {
                var c = s.ignored[i];
                if (c == null) continue;
                Physics.IgnoreCollision(n.col, c, true);
                n.ignored.Add(c);
            }
            Ignore(s, n);

            // children beyond the split are re-connected to the new segment
            for (int i = s.children.Count - 1; i >= 0; i--)
            {
                var c = s.children[i];
                if (c == null) { s.children.RemoveAt(i); continue; }
                var cj = c.Joint;
                if (cj == null) continue;
                Vector3 anchorInS = s.transform.InverseTransformPoint(c.transform.TransformPoint(cj.anchor));
                if (Vector3.Dot(anchorInS, s.segAxis) <= split) continue;
                s.children.RemoveAt(i);
                Reconnect(c, n);
                n.children.Add(c);
                c.parentPiece = n;
            }

            if (fracture)
            {
                var jr = new JointRuntime();
                jr.kind = JointKind.Fracture;
                jr.def = JointDef.Fracture(s.id);
                jr.group = MuscleGroup.None;
                var j = go.AddComponent<ConfigurableJoint>();
                SetupJoint(j, jr, n, s, s.segAxis * split);
                jr.breakForceBase = BreakForceFor(s.id, Sandbox.Boot.dismember) * 0.6f;
                jr.breakTorqueBase = jr.breakForceBase * 0.3f;
                n.jr = jr;
                n.parentPiece = s;
                s.children.Add(n);
                ApplyBreakForce(n);
            }
            if (Skin != null) Skin.OnSegmentAdded(n);
            // inner bones for both halves
            SplitInnerBones(s, n);
            return n;
        }

        /// <summary>Moves an existing joint (on 'child') to a new connected body. The rest pose is re-captured at the
        /// current relative rotation; targets stay correct and the limits are widened by the offset from the T-pose.</summary>
        public void Reconnect(BodyPart child, BodyPart newParent)
        {
            var jr = child.jr;
            if (jr == null || jr.joint == null || newParent == null) return;
            var j = jr.joint;
            Vector3 worldAnchor = child.transform.TransformPoint(j.anchor);
            j.connectedAnchor = newParent.transform.InverseTransformPoint(worldAnchor);
            j.connectedBody = newParent.rb;
            jr.startLocal = Quaternion.Inverse(newParent.transform.rotation) * child.transform.rotation;
            if (jr.kind == JointKind.Anatomical) jr.restOffsetDeg = Quaternion.Angle(Quaternion.identity, jr.startLocal);
            jr.ApplyLimits(jr.extraLimit);
        }

        static Collider CloneCollider(Collider src, GameObject go)
        {
            var cap = src as CapsuleCollider;
            if (cap != null) { var c = go.AddComponent<CapsuleCollider>(); c.direction = cap.direction; c.radius = cap.radius; c.height = cap.height; c.center = cap.center; return c; }
            var box = src as BoxCollider;
            if (box != null) { var b = go.AddComponent<BoxCollider>(); b.size = box.size; b.center = box.center; return b; }
            var sph = src as SphereCollider;
            if (sph != null) { var s = go.AddComponent<SphereCollider>(); s.radius = sph.radius; s.center = sph.center; return s; }
            var bb = go.AddComponent<BoxCollider>();
            bb.size = Vector3.one * 0.05f;
            return bb;
        }

        /// <summary>Fits a segment's collider (in place, so ignore pairs are kept) to its [segMin, segMax] range.</summary>
        public void FitSegmentCollider(BodyPart p)
        {
            Vector3 a = p.segAxis;
            float len = Mathf.Max(0.01f * Scale, p.segMax - p.segMin);
            float mid = (p.segMin + p.segMax) * 0.5f;
            int i = (int)p.id;
            Vector3 baseCenter = PartInfo.ColCenter[i] * Scale;
            Vector3 perp = baseCenter - a * Vector3.Dot(baseCenter, a);
            Vector3 center = perp + a * mid;
            var cap = p.col as CapsuleCollider;
            if (cap != null)
            {
                Vector3 dirVec = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
                bool alongAxis = Mathf.Abs(Vector3.Dot(dirVec, a)) > 0.5f;
                float r0 = PartInfo.ColSize[i].x * Scale;
                if (alongAxis)
                {
                    float r = Mathf.Min(r0, Mathf.Max(0.01f * Scale, len * 0.5f));
                    cap.radius = r;
                    cap.height = Mathf.Max(len, r * 2f);
                }
                else
                {
                    // torso capsules lie across the bone axis: the radius is the half-thickness along the bone
                    cap.radius = Mathf.Min(r0, Mathf.Max(0.012f * Scale, len * 0.5f));
                    cap.height = Mathf.Max(PartInfo.ColSize[i].y * Scale, cap.radius * 2f);
                }
                cap.center = center;
                return;
            }
            var box = p.col as BoxCollider;
            if (box != null)
            {
                Vector3 size = PartInfo.ColKind[i] == ColliderKind.Box ? PartInfo.ColSize[i] * Scale : box.size;
                Vector3 absA = new Vector3(Mathf.Abs(a.x), Mathf.Abs(a.y), Mathf.Abs(a.z));
                size = Vector3.Scale(size, Vector3.one - absA) + absA * len;
                box.size = size;
                box.center = center;
                return;
            }
            var sph = p.col as SphereCollider;
            if (sph != null)
            {
                float r0 = PartInfo.ColSize[i].x * Scale;
                sph.radius = Mathf.Min(r0, Mathf.Max(0.02f * Scale, len * 0.5f));
                sph.center = center;
            }
        }

        void SplitInnerBones(BodyPart s, BodyPart n)
        {
            // regenerate the long bone of each half for the X-ray view
            if (s.inner == null) return;
            for (int i = s.innerRenderers.Count - 1; i >= 0; i--)
            {
                var r = s.innerRenderers[i];
                if (r != null && r.name == "Bones") { Destroy(r.gameObject); s.innerRenderers.RemoveAt(i); }
            }
            var go = new GameObject("Inner");
            go.transform.SetParent(n.transform, false);
            go.transform.localScale = Vector3.one * Scale;
            n.inner = go.transform;
            float mn, mx;
            PartInfo.SegmentRange(s.id, out mn, out mx);
            float len = Mathf.Max(0.001f, (mx - mn) * Scale);
            float t0s = Mathf.Clamp01((s.segMin - mn * Scale) / len), t1s = Mathf.Clamp01((s.segMax - mn * Scale) / len);
            float t0n = Mathf.Clamp01((n.segMin - mn * Scale) / len), t1n = Mathf.Clamp01((n.segMax - mn * Scale) / len);
            var m0 = BodyMeshLibrary.LongBoneMesh(s.id, t0s, t1s);
            var m1 = BodyMeshLibrary.LongBoneMesh(s.id, t0n, t1n);
            if (m0 != null) AddInner(s, m0, MaterialFactory.Bone, "Bones");
            if (m1 != null) AddInner(n, m1, MaterialFactory.Bone, "Bones");
        }
        #endregion

        #region Topology
        readonly Queue<BodyPart> bfs = new Queue<BodyPart>(32);

        /// <summary>Recomputes which segments are still connected to the chest (the living core) and the body's
        /// capabilities. Called on every loss (joint break, cut, fracture, paralysis) so the motor model adapts live.</summary>
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
            HeadAttached = Has(PartId.Head) && !Part(PartId.Head).cutOff && Part(PartId.Neck) != null && Has(PartId.Neck);
            BrainPresent = HeadAttached && Organs.BrainIntact;
            UpdateParalysis();
            for (int s = 0; s < 2; s++)
            {
                PartId thigh = s == 0 ? PartId.ThighL : PartId.ThighR;
                PartId shin = s == 0 ? PartId.ShinL : PartId.ShinR;
                PartId foot = s == 0 ? PartId.FootL : PartId.FootR;
                bool thighOk = Has(thigh) && WholeBone(thigh);
                bool shinOk = Has(shin) && WholeBone(shin) && Part(shin).HasAnatomicalJoint;
                LegPresent[s] = Has(thigh) && Has(shin);
                LegBroken[s] = (Has(thigh) && FractureCount[(int)thigh] > 0) || (Has(shin) && FractureCount[(int)shin] > 0);
                LegUsable[s] = thighOk && shinOk && !Part(thigh).paralyzed && !Part(shin).paralyzed && Part(thigh).HasAnatomicalJoint && !LegBroken[s];
                LegHasFoot[s] = LegUsable[s] && Has(foot) && WholeBone(foot) && Part(foot).HasAnatomicalJoint;
                PartId up = s == 0 ? PartId.UpperArmL : PartId.UpperArmR;
                PartId fore = s == 0 ? PartId.ForearmL : PartId.ForearmR;
                ArmUsable[s] = Has(up) && Has(fore) && WholeBone(up) && WholeBone(fore) && !Part(up).paralyzed && Part(up).HasAnatomicalJoint && Part(fore).HasAnatomicalJoint;
            }
            TopologyVersion++;
            if (Brain != null) Brain.OnTopologyChanged();
            if (Muscles != null) Muscles.OnTopologyChanged();
            if (Balance != null) Balance.OnTopologyChanged();
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                p.rb.sleepThreshold = p.attached && Alive ? 0f : 0.01f;
            }
            ApplySolverSettings();
        }

        /// <summary>True if the bone's primary segment still reaches its distal joint (not cut short).</summary>
        public bool WholeBone(PartId id)
        {
            var p = Parts[(int)id];
            if (p == null) return false;
            var d = DistalOf(id);
            float mn, mx;
            PartInfo.SegmentRange(id, out mn, out mx);
            return d != null && d.attached && d.segMax >= mx * Scale - 0.005f;
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
                if (upper && (p.id == PartId.Pelvis || PartInfo.IsLeg(p.id))) par = true;
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
            if (d.fromPlayer) LastInteractionTime = Time.time;
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
            for (int i = 0; i < AllPieces.Count; i++) if (AllPieces[i] != null) AllPieces[i].rb.sleepThreshold = 0.01f;
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
            if (!armsPosed && FixedFrame >= 2) ApplyInitialPose();
            if (Puppet) return;
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
            Balance.ApplyAssist(dt);
        }

        void Update()
        {
            if (destroyed) return;
            float dt = Time.deltaTime;
            Wounds.VisualTick(dt);
            Organs.VisualTick(dt);
            Brain.VisualTick(dt);
            Skin.Tick(dt);
        }

        void LateUpdate()
        {
            if (destroyed) return;
            Skin.LateTick();
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
                for (int r = 0; r < p.innerRenderers.Count; r++) if (p.innerRenderers[r] != null) p.innerRenderers[r].enabled = on;
            }
            if (Skin != null) Skin.SetXRay(on);
            Organs.SetXRay(on);
        }

        public bool XRayOn { get { return xray; } }

        /// <summary>Network clients: every segment becomes kinematic and is driven by snapshots.</summary>
        public void SetPuppet(bool on)
        {
            Puppet = on;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null) continue;
                p.rb.isKinematic = on;
                p.rb.interpolation = on ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
            }
            if (Balance != null) Balance.SetEnabled(!on);
        }

        /// <summary>Called by the safety net every frame. Recovers from NaN / runaway / out-of-world states.</summary>
        public void ValidatePhysics(float maxSpeed, float killY, float bound)
        {
            if (Puppet) return;
            bool bad = false;
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null || p.rb == null || p.rb.isKinematic) continue;
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
                    // stray severed piece: freeze it out of the way
                    p.rb.isKinematic = true;
                    p.transform.position = new Vector3(0f, killY + 1f, 0f);
                }
            }
            if (bad)
            {
                Debug.LogWarning("[RagdollSandbox] " + Label + " left the valid simulation space; respawning.");
                Vector3 spawn = Time.time - lastGoodTime < 30f && MathUtil.IsFinite(lastGoodPosition) ? lastGoodPosition : Sandbox.World.RagdollSpawn;
                var boot = Sandbox.Boot;
                bool wasPossessed = Possessed;
                boot.RemoveRagdoll(this);
                var nb = boot.SpawnRagdoll(spawn + Vector3.up * 0.05f, transform.eulerAngles.y);
                if (wasPossessed && nb != null) boot.Possess(nb);
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
            if (Brain != null) Brain.ReleaseAllGrips();
            if (Balance != null) Balance.Dispose();
            for (int i = 0; i < AllPieces.Count; i++)
            {
                var p = AllPieces[i];
                if (p == null) continue;
                if (Sandbox.Decals != null) Sandbox.Decals.DetachFrom(p.transform);
            }
            if (Skin != null) Skin.Dispose();
            Organs.DestroyAll();
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (!destroyed && Skin != null) Skin.Dispose();
        }
        #endregion

        #region Rewind (bonus)
        struct PoseFrame { public Vector3 p; public Quaternion r; public Vector3 v, w; }
        PoseFrame[] rewindBuf;
        int rewindPieces, rewindHead, rewindCount, rewindTopology = -1;
        bool rewinding;

        public void RecordRewind(float seconds)
        {
            if (destroyed || Puppet) return;
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
                p.rb.isKinematic = Puppet;
                if (i < pieces)
                {
                    var f = rewindBuf[idx + i];
                    p.rb.position = f.p;
                    p.rb.rotation = f.r;
                    Compat.SetVelocity(p.rb, f.v);
                    p.rb.angularVelocity = f.w;
                }
            }
            if (Locomotion != null) Locomotion.ResetFeet();
        }
        #endregion
    }
    #endregion
}
