// =====================================================================================================
//  RAGDOLL SANDBOX  -  a realistic active-ragdoll physics playground, built entirely from code.
//
//  Setup: empty scene -> create an empty GameObject -> Add Component "Ragdoll Sandbox Boot" -> Play.
//  Everything (map, sky, lights, camera, player, character, meshes, textures, materials, particles,
//  audio, UI, EventSystem) is generated at runtime. Every tunable lives on this component.
//
//  Files (all in this folder, only this component is added by hand):
//    RagdollSandboxBoot.cs   Bootstrap, settings, time control, quality presets, safety net, rewind
//    SandboxCore.cs          Compat, input bridge (old + new input), math, physics materials, registry, events
//    SandboxFactories.cs     TextureFactory, MaterialFactory, MeshFactory
//    SandboxAudio.cs         ProceduralAudio (synthesized clips + pooled 3D sources)
//    SandboxPools.cs         Pools: particle FX, decals, light flashes, tracers, debris
//    SandboxWorld.cs         WorldBuilder + hazards (platforms, saws, press, spikes, cannon...)
//    RagdollBody.cs          RagdollBody (build, topology, update loop) and BodyPart
//    RagdollAnatomy.cs       Body shape (ring meshes), skin painting, skeleton and organ meshes
//    RagdollMotor.cs         MuscleController, IKSolver, BalanceController, LocomotionController, behaviours
//    RagdollDamage.cs        DamageSystem, VitalSystem, WoundSystem, OrganSystem, DismemberSystem (slicing)
//    SandboxBlood.cs         BloodSystem (droplets, spray, mist, splats, puddles, trails, prints)
//    SandboxExplosives.cs    ExplosionSystem + grenades, TNT, C4, barrels, landmines, mega blast
//    SandboxTools.cs         ToolSystem + GrabController + rope tool + prop spawner + viewmodels
//    SandboxPlayer.cs        First-person PlayerController + CameraRig (FPS / follow / possess / kill-cam)
//    SandboxHUD.cs           uGUI HUD built in code (hotbar, vitals, body diagram, settings, help, pause)
// =====================================================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region Settings
    public enum GoreLevel { Off, Reduced, Full }
    public enum GravityMode { Earth, Moon, Zero }
    public enum QualityPreset { Low, Medium, High, Ultra }

    [Serializable]
    public class GeneralSettings
    {
        [Tooltip("World seed. 0 = random every play.")]
        public int seed = 0;
        [Range(0, 6)] public int ragdollsAtStart = 1;
        public bool showHelpOnStart = true;
        [Tooltip("Free mode: idle ragdolls wander around the plaza.")]
        public bool wander = false;
        [Tooltip("Flinch, shield head, clutch wounds, pain voices.")]
        public bool reactions = true;
        [Tooltip("Ragdolls look at the player when nearby.")]
        public bool gazeAtPlayer = true;
        [Range(1, 8)] public int maxRagdolls = 6;
    }

    [Serializable]
    public class PhysicsSettingsData
    {
        [Range(0.005f, 0.02f)] public float fixedTimestep = 0.01f;
        [Range(4, 30)] public int solverIterations = 16;
        [Range(1, 12)] public int velocityIterations = 6;
        [Range(7f, 60f)] public float maxAngularVelocity = 30f;
        [Range(1f, 10f)] public float maxDepenetrationVelocity = 4f;
        public GravityMode gravity = GravityMode.Earth;
        [Range(0.02f, 1f)] public float slowMotionScale = 0.25f;
        [Range(0.0005f, 0.05f)] public float sleepThreshold = 0.005f;
        [Range(0.2f, 5f)] public float bounceThreshold = 2f;
        [Tooltip("Validate positions every frame and recover from NaN / out of bounds.")]
        public bool safetyNet = true;
        [Range(10f, 200f)] public float maxBodySpeed = 90f;
    }

    [Serializable]
    public class CharacterSettings
    {
        [Range(1.5f, 2.05f)] public float height = 1.75f;
        [Range(50f, 110f)] public float mass = 72f;
        [Tooltip("Mannequin look by default (see reference picture).")]
        public Color skinColor = new Color(0.83f, 0.83f, 0.84f);
        [Range(0f, 0.2f)] public float skinVariation = 0.04f;
        public bool shirt = false;
        public Color shirtColor = new Color(0.25f, 0.33f, 0.45f);
        public bool shorts = true;
        public Color shortsColor = new Color(0.32f, 0.33f, 0.36f);
        public bool shoes = false;
        public Color shoesColor = new Color(0.15f, 0.15f, 0.16f);
        [Tooltip("Per-part paint texture size for large parts (small parts use half).")]
        [Range(64, 512)] public int paintTextureSize = 256;
        [Range(10, 40)] public int meshSegments = 24;
        [Range(10, 40)] public int meshRings = 22;
    }

    [Serializable]
    public class MuscleSettings
    {
        [Range(0f, 3f)] public float strength = 1f;
        [Range(0f, 8000f)] public float spineSpring = 1900f;
        [Range(0f, 600f)] public float spineDamper = 130f;
        [Range(0f, 3000f)] public float neckSpring = 450f;
        [Range(0f, 300f)] public float neckDamper = 36f;
        [Range(0f, 3000f)] public float shoulderSpring = 340f;
        [Range(0f, 300f)] public float shoulderDamper = 28f;
        [Range(0f, 2000f)] public float elbowSpring = 190f;
        [Range(0f, 200f)] public float elbowDamper = 15f;
        [Range(0f, 600f)] public float wristSpring = 55f;
        [Range(0f, 60f)] public float wristDamper = 4.5f;
        [Range(0f, 12000f)] public float hipSpring = 2900f;
        [Range(0f, 900f)] public float hipDamper = 190f;
        [Range(0f, 12000f)] public float kneeSpring = 2900f;
        [Range(0f, 900f)] public float kneeDamper = 190f;
        [Range(0f, 8000f)] public float ankleSpring = 1400f;
        [Range(0f, 600f)] public float ankleDamper = 95f;
        [Tooltip("Multiplier on every joint's maximum torque.")]
        [Range(0.1f, 4f)] public float torqueLimitScale = 1f;
        [Range(0.2f, 4f)] public float deathFadeTime = 1.5f;
        [Range(0f, 0.4f)] public float unconsciousTone = 0.07f;
        [Tooltip("Stiffness multiplier when bracing / co-contracting.")]
        [Range(1f, 3f)] public float coContraction = 1.6f;
        [Range(0f, 2f)] public float reflexStrength = 1f;
        [Range(0f, 0.5f)] public float fatigueRate = 0.04f;
        [Range(0f, 0.5f)] public float fatigueRecovery = 0.08f;
        [Tooltip("Ignore consciousness and force the active level below.")]
        public bool overrideActiveLevel = false;
        [Range(0f, 1f)] public float activeLevel = 1f;
    }

    [Serializable]
    public class BalanceSettings
    {
        [Range(0f, 4000f)] public float pelvisUprightSpring = 950f;
        [Range(0f, 400f)] public float pelvisUprightDamper = 95f;
        [Range(0f, 4000f)] public float chestUprightSpring = 650f;
        [Range(0f, 400f)] public float chestUprightDamper = 62f;
        [Tooltip("Max external assist torque (Nm). Scales with consciousness, fades with damage.")]
        [Range(0f, 600f)] public float assistTorque = 160f;
        [Tooltip("Pelvis height regulation gain (per kg of supported mass).")]
        [Range(0f, 400f)] public float heightGain = 150f;
        [Range(0f, 60f)] public float heightDamping = 22f;
        [Tooltip("Centre-of-mass tracking gain over the support (per kg).")]
        [Range(0f, 120f)] public float comGain = 26f;
        [Range(0f, 30f)] public float comDamping = 9f;
        [Range(0.45f, 0.6f)] public float standHeightRatio = 0.535f;
        [Range(0f, 0.2f)] public float captureMargin = 0.045f;
        [Range(20f, 80f)] public float fallAngle = 52f;
        [Range(0f, 2f)] public float ankleCapacity = 1f;
        [Tooltip("Fraction of body weight the get-up assist may provide.")]
        [Range(0f, 1f)] public float getUpAssist = 0.38f;
        [Range(0, 10)] public int getUpAttempts = 4;
        [Range(2f, 20f)] public float getUpTimeout = 9f;
        [Range(0f, 5f)] public float getUpDelay = 1.3f;
        [Range(0f, 1f)] public float friction = 1f;
    }

    [Serializable]
    public class LocomotionSettings
    {
        [Range(0.2f, 3f)] public float walkSpeed = 1.35f;
        [Range(1f, 7f)] public float runSpeed = 3.4f;
        [Range(0.1f, 2f)] public float crouchSpeed = 0.6f;
        [Range(20f, 400f)] public float turnSpeed = 150f;
        [Range(0.2f, 0.8f)] public float stepTimeWalk = 0.42f;
        [Range(0.15f, 0.5f)] public float stepTimeRun = 0.28f;
        [Range(0.02f, 0.3f)] public float stepHeight = 0.085f;
        [Range(0.2f, 1f)] public float maxStride = 0.55f;
        [Range(0.4f, 1.6f)] public float runStride = 0.95f;
        [Range(0.08f, 0.35f)] public float stanceWidth = 0.17f;
        [Range(0f, 0.5f)] public float raibertGain = 0.12f;
        [Range(0f, 60f)] public float toeOffImpulse = 16f;
        [Range(0f, 4f)] public float jumpForce = 2.3f;
        [Range(0f, 60f)] public float armSwing = 24f;
        [Range(0f, 20f)] public float spineCounterRotation = 7f;
        [Range(0f, 2f)] public float slipSpeed = 0.35f;
        [Range(0f, 800f)] public float crawlForce = 300f;
        [Range(0f, 4f)] public float hopForce = 1.9f;
        [Range(0f, 4000f)] public float swingFootGain = 700f;
        [Range(0f, 200f)] public float swingFootDamping = 45f;
        [Range(0f, 1f)] public float stumbleChance = 0.6f;
    }

    [Serializable]
    public class DamageSettings
    {
        [Tooltip("Impulse (N*s) below which collisions do no damage.")]
        [Range(0f, 60f)] public float impactThreshold = 9f;
        [Range(0f, 3f)] public float impactDamageScale = 0.6f;
        [Range(0.2f, 4f)] public float fractureThresholdScale = 1f;
        [Range(0f, 200f)] public float crushDamagePerSecond = 55f;
        [Range(0f, 200f)] public float pistolDamage = 34f;
        [Range(0f, 80f)] public float shotgunPelletDamage = 10f;
        [Range(0f, 150f)] public float bladeDamage = 32f;
        [Range(0f, 60f)] public float shockDamagePerSecond = 6f;
        [Range(0f, 3f)] public float explosionDamageScale = 1f;
        [Range(0f, 3f)] public float deformationScale = 1f;
        [Range(0f, 0.08f)] public float maxDentDepth = 0.035f;
        [Range(0f, 1500f)] public float overwhelmingTrauma = 650f;
        [Range(0f, 3f)] public float knockoutScale = 1f;
    }

    [Serializable]
    public class VitalSettings
    {
        [Range(2f, 8f)] public float bloodVolume = 5f;
        [Range(40f, 100f)] public float restingHeartRate = 70f;
        [Tooltip("Remaining blood fraction at which the body passes out.")]
        [Range(0.3f, 0.9f)] public float unconsciousBloodFraction = 0.6f;
        [Tooltip("Remaining blood fraction at which the body dies.")]
        [Range(0.2f, 0.8f)] public float deathBloodFraction = 0.45f;
        [Range(5f, 200f)] public float clotTime = 45f;
        [Range(0f, 0.3f)] public float arterialBleedRate = 0.07f;
        [Range(0f, 0.5f)] public float painDecay = 0.07f;
        [Range(0f, 0.2f)] public float adrenalineDecay = 0.02f;
        [Tooltip("How hard a hurt body keeps trying to move, get up and crawl.")]
        [Range(0f, 1f)] public float willToLive = 0.7f;
        [Range(0.2f, 0.9f)] public float wakeThreshold = 0.45f;
        [Range(0f, 30f)] public float headlessSpasmTime = 4.5f;
        [Range(2f, 40f)] public float brainDeathTime = 9f;
        [Tooltip("Bleeding drains vitals even when gore is Off.")]
        public bool bleedWhenGoreOff = true;
    }

    [Serializable]
    public class DismemberSettings
    {
        public GoreLevel gore = GoreLevel.Full;
        [Range(500f, 40000f)] public float neckBreakForce = 6500f;
        [Range(500f, 40000f)] public float shoulderBreakForce = 7000f;
        [Range(500f, 40000f)] public float elbowBreakForce = 5200f;
        [Range(500f, 40000f)] public float wristBreakForce = 3600f;
        [Range(500f, 60000f)] public float hipBreakForce = 12500f;
        [Range(500f, 40000f)] public float kneeBreakForce = 8200f;
        [Range(500f, 40000f)] public float ankleBreakForce = 5200f;
        [Range(500f, 80000f)] public float spineBreakForce = 18000f;
        [Range(0.2f, 4f)] public float breakTorqueRatio = 0.35f;
        [Tooltip("Damage weakens joints: break force scales with (floor + (1-floor) * health).")]
        [Range(0f, 1f)] public float damagedJointFloor = 0.3f;
        [Tooltip("Blade sweep speed (deg/s of view rotation) needed to sever.")]
        [Range(10f, 1000f)] public float bladeSeverSpeed = 120f;
        public bool allowTorsoTear = true;
        [Range(0f, 3f)] public float stumpSpurt = 1f;
        [Range(0f, 400f)] public float ruptureThreshold = 85f;
    }

    [Serializable]
    public class BloodSettings
    {
        [Range(0f, 2.5f)] public float bloodAmount = 1f;
        [Range(100, 4000)] public int maxDecals = 1500;
        [Range(4, 128)] public int maxPuddles = 64;
        [Range(0.1f, 2.5f)] public float puddleMaxRadius = 1.1f;
        [Range(10f, 600f)] public float puddleDryTime = 140f;
        [Range(10f, 600f)] public float decalDryTime = 90f;
        [Range(0, 30)] public int footprintSteps = 9;
        [Range(500, 8000)] public int maxParticles = 3500;
        public bool paintOnBodies = true;
        public Color arterial = new Color(0.72f, 0.02f, 0.03f);
        public Color venous = new Color(0.4f, 0.01f, 0.02f);
        public Color dried = new Color(0.22f, 0.07f, 0.04f);
    }

    [Serializable]
    public class ExplosionSettings
    {
        [Range(0.5f, 10f)] public float grenadeFuse = 3f;
        [Range(1f, 20f)] public float grenadeRadius = 6f;
        [Range(0f, 5000f)] public float grenadeForce = 900f;
        [Range(0f, 500f)] public float grenadeDamage = 120f;
        [Range(1f, 20f)] public float tntRadius = 8f;
        [Range(0f, 6000f)] public float tntForce = 1400f;
        [Range(0f, 600f)] public float tntDamage = 160f;
        [Range(1f, 20f)] public float c4Radius = 7f;
        [Range(0f, 6000f)] public float c4Force = 1300f;
        [Range(0f, 600f)] public float c4Damage = 150f;
        [Range(1f, 20f)] public float barrelRadius = 7f;
        [Range(0f, 6000f)] public float barrelForce = 1200f;
        [Range(0f, 600f)] public float barrelDamage = 140f;
        [Range(1f, 20f)] public float mineRadius = 4.5f;
        [Range(0f, 6000f)] public float mineForce = 1150f;
        [Range(0f, 600f)] public float mineDamage = 180f;
        [Range(5f, 40f)] public float megaRadius = 18f;
        [Range(0f, 20000f)] public float megaForce = 4200f;
        [Range(0f, 2000f)] public float megaDamage = 420f;
        [Range(0, 80)] public int shrapnel = 24;
        [Tooltip("Velocity change clamp per body for stability (m/s).")]
        [Range(5f, 80f)] public float maxDeltaV = 26f;
        [Range(0f, 3f)] public float cameraShake = 1f;
        [Range(0f, 30f)] public float fovKick = 9f;
        [Range(0f, 1f)] public float chainDelay = 0.18f;
    }

    [Serializable]
    public class ToolSettings
    {
        [Range(100f, 20000f)] public float grabSpring = 2600f;
        [Range(0f, 2000f)] public float grabDamper = 120f;
        [Range(100f, 50000f)] public float grabMaxForce = 2400f;
        [Tooltip("Shift = strong pull (can rip limbs off).")]
        [Range(1f, 20f)] public float strongPullMultiplier = 7f;
        [Range(0.5f, 3f)] public float grabMinDistance = 0.8f;
        [Range(2f, 30f)] public float grabMaxDistance = 12f;
        [Range(0f, 2f)] public float throwBoost = 0.55f;
        [Range(0f, 200f)] public float hammerImpulse = 28f;
        [Range(0f, 400f)] public float heavyHammerImpulse = 70f;
        [Range(0f, 100f)] public float pistolImpulse = 9f;
        [Range(1, 20)] public int shotgunPellets = 9;
        [Range(0f, 15f)] public float shotgunSpread = 5.5f;
        [Range(0f, 20f)] public float shotgunPelletImpulse = 5f;
        [Range(0f, 30f)] public float throwSpeed = 15f;
        [Range(10, 300)] public int propCap = 90;
        [Range(1, 60)] public int ropeCap = 20;
        [Range(1f, 40f)] public float maxRange = 120f;
        public bool viewModels = true;
    }

    [Serializable]
    public class PlayerSettings
    {
        [Range(1f, 10f)] public float walkSpeed = 4.5f;
        [Range(2f, 20f)] public float sprintSpeed = 7.5f;
        [Range(0.5f, 5f)] public float crouchSpeed = 2.2f;
        [Range(1f, 12f)] public float jumpSpeed = 5.2f;
        [Range(0.1f, 5f)] public float mouseSensitivity = 1f;
        public bool invertY = false;
        [Range(50f, 110f)] public float fieldOfView = 75f;
        [Range(1.4f, 2.1f)] public float height = 1.8f;
        [Range(0f, 200f)] public float pushForce = 60f;
        [Range(0f, 30f)] public float acceleration = 12f;
        public bool noclip = false;
    }

    [Serializable]
    public class CameraSettingsData
    {
        [Range(1f, 10f)] public float followDistance = 3.6f;
        [Range(-1f, 3f)] public float followHeight = 0.45f;
        [Range(1f, 10f)] public float possessDistance = 3.2f;
        [Range(1f, 30f)] public float smoothing = 10f;
        [Range(0f, 3f)] public float shakeScale = 1f;
        public bool killCam = true;
        [Range(0.5f, 6f)] public float killCamDuration = 2.6f;
        [Range(0.05f, 1f)] public float killCamTimeScale = 0.25f;
    }

    [Serializable]
    public class AudioSettingsData
    {
        [Range(0f, 1f)] public float master = 0.9f;
        [Range(0f, 1f)] public float effects = 1f;
        [Range(0f, 1f)] public float ambient = 0.45f;
        [Range(0f, 1f)] public float voices = 0.8f;
        [Range(8, 96)] public int poolSize = 40;
    }

    [Serializable]
    public class GraphicsSettingsData
    {
        public QualityPreset preset = QualityPreset.High;
        [Tooltip("Lower decal/particle budgets automatically when the frame time is high.")]
        public bool autoReduce = true;
        [Range(15f, 120f)] public float targetFps = 50f;
        [Range(128, 1024)] public int worldTextureSize = 512;
        public bool dayNightCycle = false;
        [Range(1f, 60f)] public float dayLengthMinutes = 12f;
        [Range(0f, 24f)] public float timeOfDay = 15.5f;
        public bool fog = true;
        [Range(0f, 0.05f)] public float fogDensity = 0.0055f;
    }

    [Serializable]
    public class WorldSettings
    {
        [Range(60f, 200f)] public float mapSize = 120f;
        [Range(0f, 12f)] public float hillHeight = 5f;
        [Range(0f, 2f)] public float propDensity = 1f;
        public bool boundaryWalls = true;
        [Range(-200f, -5f)] public float killPlaneY = -30f;
        [Range(0f, 30f)] public float conveyorSpeed = 2.2f;
        [Range(0f, 10f)] public float platformSpeed = 1.2f;
        [Range(0f, 30f)] public float trampolineSpeed = 13f;
        [Range(0f, 80f)] public float cannonSpeed = 32f;
        [Range(1, 400)] public int brickCount = 220;
    }

    [Serializable]
    public class BonusSettings
    {
        public bool rewind = true;
        [Range(1f, 10f)] public float rewindSeconds = 5f;
    }
    #endregion

    #region Bootstrap
    /// <summary>Global access to the running systems.</summary>
    public static class Sandbox
    {
        public static RagdollSandboxBoot Boot;
        public static ProceduralAudio Audio;
        public static FxSystem Fx;
        public static DecalSystem Decals;
        public static BloodSystem Blood;
        public static ExplosionSystem Explosions;
        public static WorldBuilder World;
        public static PlayerController Player;
        public static CameraRig Cam;
        public static ToolSystem Tools;
        public static Hud Hud;
        public static RewindSystem Rewind;
        public static Camera MainCamera;
        public static Transform Root;
        public static readonly List<RagdollBody> Ragdolls = new List<RagdollBody>(8);
        public static RagdollBody Possessed;
        public static RagdollBody Hovered;
        public static float HoveredTime;
        public static bool XRay;
        public static int Seed;

        public static GoreLevel Gore { get { return Boot != null ? Boot.dismember.gore : GoreLevel.Full; } }
        public static float BloodAmount
        {
            get
            {
                if (Boot == null) return 1f;
                float g = Boot.dismember.gore == GoreLevel.Off ? 0f : Boot.dismember.gore == GoreLevel.Reduced ? 0.4f : 1f;
                return Boot.blood.bloodAmount * g;
            }
        }

        /// <summary>The ragdoll the HUD reports on: possessed, recently hovered, or nearest to the camera.</summary>
        public static RagdollBody Focus
        {
            get
            {
                if (Possessed != null && Possessed.Alive) return Possessed;
                if (Hovered != null && Time.unscaledTime - HoveredTime < 4f) return Hovered;
                RagdollBody best = null;
                float bd = float.MaxValue;
                Vector3 cp = MainCamera != null ? MainCamera.transform.position : Vector3.zero;
                for (int i = 0; i < Ragdolls.Count; i++)
                {
                    var r = Ragdolls[i];
                    if (r == null) continue;
                    float d = (r.CenterOfMass - cp).sqrMagnitude;
                    if (d < bd) { bd = d; best = r; }
                }
                return best;
            }
        }

        public static void Reset()
        {
            Boot = null; Audio = null; Fx = null; Decals = null; Blood = null; Explosions = null; World = null; Player = null;
            Cam = null; Tools = null; Hud = null; Rewind = null; MainCamera = null; Root = null;
            Ragdolls.Clear(); Possessed = null; Hovered = null; XRay = false;
        }
    }

    /// <summary>Slow motion, pause, kill-cam and gravity in one place so they never fight.</summary>
    public static class TimeControl
    {
        public static bool SlowMotion, Paused;
        public static float KillCamUntil = -1f;
        public static float BaseFixedDelta = 0.01f;
        public static float CurrentScale = 1f;

        public static void Apply()
        {
            var b = Sandbox.Boot;
            float scale = 1f;
            if (SlowMotion && b != null) scale = b.physics.slowMotionScale;
            if (Time.unscaledTime < KillCamUntil && b != null) scale = Mathf.Min(scale, b.cameraSettings.killCamTimeScale);
            if (Paused) scale = 0f;
            CurrentScale = scale;
            Time.timeScale = scale;
            float fixedScale = Mathf.Max(scale, 0.05f);
            Time.fixedDeltaTime = BaseFixedDelta * Mathf.Min(1f, fixedScale);
            Time.maximumDeltaTime = 0.1f;
        }

        public static void SetGravity(GravityMode g)
        {
            switch (g)
            {
                case GravityMode.Earth: Physics.gravity = new Vector3(0f, -9.81f, 0f); break;
                case GravityMode.Moon: Physics.gravity = new Vector3(0f, -1.62f, 0f); break;
                default: Physics.gravity = Vector3.zero; break;
            }
        }
    }

    /// <summary>Quality presets and the automatic budget reduction when frames get slow.</summary>
    public class QualityManager
    {
        float smoothedDt = 1f / 60f;
        float overBudgetTime, underBudgetTime;
        public float ParticleScale = 1f;
        public float DecalScale = 1f;
        public float Fps { get { return 1f / Mathf.Max(1e-4f, smoothedDt); } }

        public void ApplyPreset(QualityPreset p, RagdollSandboxBoot b)
        {
            int iters = 16, vel = 6, decals = b.blood.maxDecals;
            float shadowDist = 60f, particles = 1f, lod = 1f;
            switch (p)
            {
                case QualityPreset.Low: iters = 8; vel = 2; shadowDist = 25f; particles = 0.4f; decals = Mathf.Min(decals, 400); lod = 0.6f; break;
                case QualityPreset.Medium: iters = 12; vel = 4; shadowDist = 40f; particles = 0.7f; decals = Mathf.Min(decals, 900); lod = 0.8f; break;
                case QualityPreset.High: iters = 16; vel = 6; shadowDist = 60f; particles = 1f; lod = 1f; break;
                case QualityPreset.Ultra: iters = 20; vel = 8; shadowDist = 90f; particles = 1.3f; decals = Mathf.Max(decals, 2000); lod = 1.4f; break;
            }
            b.physics.solverIterations = iters;
            b.physics.velocityIterations = vel;
            Physics.defaultSolverIterations = iters;
            Physics.defaultSolverVelocityIterations = vel;
            ParticleScale = particles;
            DecalScale = 1f;
            if (Sandbox.Decals != null) Sandbox.Decals.SetCapacity(decals);
            QualitySettings.shadowDistance = shadowDist;
            QualitySettings.lodBias = lod;
            SetPipelineShadowDistance(shadowDist);
            var sun = Sandbox.World != null ? Sandbox.World.Sun : null;
            if (sun != null) sun.shadows = p == QualityPreset.Low ? LightShadows.Hard : LightShadows.Soft;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
                if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].ApplySolverSettings();
        }

        static void SetPipelineShadowDistance(float d)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return;
            try
            {
                var prop = rp.GetType().GetProperty("shadowDistance");
                if (prop != null && prop.CanWrite) prop.SetValue(rp, d, null);
            }
            catch (Exception) { }
        }

        public void Tick(RagdollSandboxBoot b)
        {
            float dt = Time.unscaledDeltaTime;
            smoothedDt = Mathf.Lerp(smoothedDt, dt, 0.05f);
            if (!b.graphics.autoReduce) { ParticleScale = Mathf.Max(ParticleScale, 0.4f); return; }
            float budget = 1f / Mathf.Max(10f, b.graphics.targetFps);
            if (smoothedDt > budget * 1.15f) { overBudgetTime += dt; underBudgetTime = 0f; }
            else if (smoothedDt < budget * 0.8f) { underBudgetTime += dt; overBudgetTime = 0f; }
            if (overBudgetTime > 1.5f)
            {
                overBudgetTime = 0f;
                ParticleScale = Mathf.Max(0.25f, ParticleScale * 0.8f);
                DecalScale = Mathf.Max(0.3f, DecalScale * 0.85f);
                if (Sandbox.Decals != null) Sandbox.Decals.SetCapacity(Mathf.RoundToInt(b.blood.maxDecals * DecalScale));
            }
            else if (underBudgetTime > 6f)
            {
                underBudgetTime = 0f;
                float maxP = b.graphics.preset == QualityPreset.Ultra ? 1.3f : b.graphics.preset == QualityPreset.Low ? 0.4f : b.graphics.preset == QualityPreset.Medium ? 0.7f : 1f;
                ParticleScale = Mathf.Min(maxP, ParticleScale * 1.1f);
                DecalScale = Mathf.Min(1f, DecalScale * 1.1f);
                if (Sandbox.Decals != null) Sandbox.Decals.SetCapacity(Mathf.RoundToInt(b.blood.maxDecals * DecalScale));
            }
        }
    }

    /// <summary>Validates every simulated body and recovers from NaN or runaway values instead of exploding.</summary>
    public class SafetyNet
    {
        float propTimer;

        public void Tick(RagdollSandboxBoot b)
        {
            if (!b.physics.safetyNet) return;
            float maxSpeed = b.physics.maxBodySpeed;
            float killY = b.world.killPlaneY;
            float bound = b.world.mapSize * 0.5f + 60f;
            for (int i = Sandbox.Ragdolls.Count - 1; i >= 0; i--)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null) { Sandbox.Ragdolls.RemoveAt(i); continue; }
                r.ValidatePhysics(maxSpeed, killY, bound);
            }
            propTimer -= Time.unscaledDeltaTime;
            if (propTimer <= 0f && Sandbox.World != null)
            {
                propTimer = 0.25f;
                Sandbox.World.ValidateProps(maxSpeed, killY, bound);
            }
        }
    }

    /// <summary>Bonus: hold Z to rewind the ragdolls up to N seconds (poses only; topology changes stop the rewind).</summary>
    public class RewindSystem
    {
        public bool Rewinding { get; private set; }

        public void FixedTick(RagdollSandboxBoot b)
        {
            if (!b.bonus.rewind) return;
            bool hold = InputBridge.Held(KeyId.Z) && !InputBridge.GameplayBlocked;
            if (hold && !Rewinding) { Rewinding = true; for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].BeginRewind(); }
            if (!hold && Rewinding) { Rewinding = false; for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].EndRewind(); }
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null) continue;
                if (Rewinding) r.RewindStep(2);
                else r.RecordRewind(b.bonus.rewindSeconds);
            }
        }
    }

    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public class RagdollSandboxBoot : MonoBehaviour
    {
        [Header("General")] public GeneralSettings general = new GeneralSettings();
        [Header("Physics")] public PhysicsSettingsData physics = new PhysicsSettingsData();
        [Header("Character")] public CharacterSettings character = new CharacterSettings();
        [Header("Muscles (active ragdoll)")] public MuscleSettings muscles = new MuscleSettings();
        [Header("Balance")] public BalanceSettings balance = new BalanceSettings();
        [Header("Locomotion")] public LocomotionSettings locomotion = new LocomotionSettings();
        [Header("Damage")] public DamageSettings damage = new DamageSettings();
        [Header("Vitals")] public VitalSettings vitals = new VitalSettings();
        [Header("Dismemberment / Gore")] public DismemberSettings dismember = new DismemberSettings();
        [Header("Blood")] public BloodSettings blood = new BloodSettings();
        [Header("Explosions")] public ExplosionSettings explosions = new ExplosionSettings();
        [Header("Tools")] public ToolSettings tools = new ToolSettings();
        [Header("Player (first person)")] public PlayerSettings player = new PlayerSettings();
        [Header("Camera")] public CameraSettingsData cameraSettings = new CameraSettingsData();
        [Header("Audio")] public AudioSettingsData audioSettings = new AudioSettingsData();
        [Header("Graphics / Quality")] public GraphicsSettingsData graphics = new GraphicsSettingsData();
        [Header("World")] public WorldSettings world = new WorldSettings();
        [Header("Bonus")] public BonusSettings bonus = new BonusSettings();

        public QualityManager Quality { get; private set; }
        Transform runtimeRoot;
        SafetyNet safety;
        bool built;
        QualityPreset appliedPreset;
        GravityMode appliedGravity;
        float appliedTimestep;

        // Saved global state, restored when the sandbox is destroyed (play mode stop).
        float savedFixedDelta, savedTimeScale, savedMaxDelta, savedSleep, savedBounce, savedDepen, savedMaxAng;
        int savedIters, savedVelIters;
        Vector3 savedGravity;
        bool savedFog, savedReuse;
        Material savedSky;
        AmbientMode savedAmbientMode;
        Color savedAmbientSky, savedAmbientEq, savedAmbientGround;
        readonly List<Behaviour> disabledSceneObjects = new List<Behaviour>();

        void Awake()
        {
            if (Sandbox.Boot != null && Sandbox.Boot != this)
            {
                Debug.LogWarning("[RagdollSandbox] Another RagdollSandboxBoot is already running; disabling this one.");
                enabled = false;
                return;
            }
            if (GetComponent<Camera>() != null || GetComponent<Light>() != null)
                Debug.LogWarning("[RagdollSandbox] Put RagdollSandboxBoot on an empty GameObject. It creates its own camera and lights.");
            Sandbox.Reset();
            Registry.Clear();
            SandboxEvents.ClearAll();
            Sandbox.Boot = this;
            runtimeRoot = new GameObject("Ragdoll Sandbox (runtime)").transform;
            runtimeRoot.position = Vector3.zero;
            Sandbox.Root = runtimeRoot;
            SaveGlobals();
            DisableSceneDefaults();
            try
            {
                Build();
                built = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[RagdollSandbox] Build failed: " + e);
            }
        }

        void SaveGlobals()
        {
            savedFixedDelta = Time.fixedDeltaTime; savedTimeScale = Time.timeScale; savedMaxDelta = Time.maximumDeltaTime;
            savedGravity = Physics.gravity; savedIters = Physics.defaultSolverIterations; savedVelIters = Physics.defaultSolverVelocityIterations;
            savedSleep = Physics.sleepThreshold; savedBounce = Physics.bounceThreshold; savedDepen = Physics.defaultMaxDepenetrationVelocity;
            savedMaxAng = Physics.defaultMaxAngularSpeed; savedReuse = Physics.reuseCollisionCallbacks;
            savedFog = RenderSettings.fog; savedSky = RenderSettings.skybox; savedAmbientMode = RenderSettings.ambientMode;
            savedAmbientSky = RenderSettings.ambientSkyColor; savedAmbientEq = RenderSettings.ambientEquatorColor; savedAmbientGround = RenderSettings.ambientGroundColor;
        }

        void RestoreGlobals()
        {
            Time.fixedDeltaTime = savedFixedDelta; Time.timeScale = savedTimeScale; Time.maximumDeltaTime = savedMaxDelta;
            Physics.gravity = savedGravity; Physics.defaultSolverIterations = savedIters; Physics.defaultSolverVelocityIterations = savedVelIters;
            Physics.sleepThreshold = savedSleep; Physics.bounceThreshold = savedBounce; Physics.defaultMaxDepenetrationVelocity = savedDepen;
            Physics.defaultMaxAngularSpeed = savedMaxAng; Physics.reuseCollisionCallbacks = savedReuse;
            RenderSettings.fog = savedFog; RenderSettings.skybox = savedSky; RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientSkyColor = savedAmbientSky; RenderSettings.ambientEquatorColor = savedAmbientEq; RenderSettings.ambientGroundColor = savedAmbientGround;
            AudioListener.pause = false;
        }

        /// <summary>Turns off cameras, lights, listeners and event systems that came with the scene template.</summary>
        void DisableSceneDefaults()
        {
            foreach (var c in Compat.FindAll<Camera>()) { if (c != null && c.enabled) { c.enabled = false; disabledSceneObjects.Add(c); } }
            foreach (var l in Compat.FindAll<Light>()) { if (l != null && l.enabled) { l.enabled = false; disabledSceneObjects.Add(l); } }
            foreach (var a in Compat.FindAll<AudioListener>()) { if (a != null && a.enabled) { a.enabled = false; disabledSceneObjects.Add(a); } }
            foreach (var e in Compat.FindAll<EventSystem>()) { if (e != null && e.enabled) { e.gameObject.SetActive(false); } }
        }

        void Build()
        {
            int seed = general.seed != 0 ? general.seed : (int)(DateTime.Now.Ticks & 0x7FFFFFFF) % 1000000 + 1;
            Sandbox.Seed = seed;
            Quality = new QualityManager();
            safety = new SafetyNet();
            ApplyPhysicsSettings();

            PhysMats.Build();
            MaterialFactory.Init();
            TextureFactory.BuildAll(seed, graphics.worldTextureSize);
            MaterialFactory.BuildWorldMaterials();
            BodyMeshLibrary.Build();

            Transform r = runtimeRoot;
            Sandbox.Audio = new ProceduralAudio();
            Sandbox.Audio.Build(r, audioSettings.poolSize, seed);
            ApplyAudioSettings();

            Sandbox.Fx = new FxSystem();
            Sandbox.Fx.Build(r, blood.maxParticles);
            Sandbox.Decals = new DecalSystem();
            Sandbox.Decals.Build(r, blood.maxDecals);
            Sandbox.Blood = new BloodSystem();
            Sandbox.Blood.Build(r);
            Sandbox.Explosions = new ExplosionSystem();
            Sandbox.Explosions.Build(r);

            Sandbox.World = new WorldBuilder();
            Sandbox.World.Build(r, seed);

            Sandbox.Player = PlayerController.Create(r, Sandbox.World.PlayerSpawn, Sandbox.World.PlayerSpawnYaw);
            Sandbox.Cam = CameraRig.Create(r);
            Sandbox.Tools = new ToolSystem();
            Sandbox.Tools.Build(r);
            Sandbox.Rewind = new RewindSystem();
            Sandbox.Hud = new Hud();
            Sandbox.Hud.Build(r);

            appliedPreset = graphics.preset;
            Quality.ApplyPreset(graphics.preset, this);
            appliedGravity = physics.gravity;
            TimeControl.SetGravity(physics.gravity);

            int count = Mathf.Clamp(general.ragdollsAtStart, 0, general.maxRagdolls);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Sandbox.World.RagdollSpawn + new Vector3((i % 3 - 1) * 1.6f, 0f, (i / 3) * 1.8f);
                SpawnRagdoll(p, Sandbox.World.RagdollSpawnYaw);
            }

            CursorControl.SetLocked(true);
            if (general.showHelpOnStart) Sandbox.Hud.ShowHelp(true);
            SandboxEvents.RaiseToast("Welcome to the Ragdoll Sandbox. Press H for help.", new Color(0.8f, 0.9f, 1f));
        }

        public void ApplyPhysicsSettings()
        {
            TimeControl.BaseFixedDelta = physics.fixedTimestep;
            appliedTimestep = physics.fixedTimestep;
            TimeControl.Apply();
            Physics.defaultSolverIterations = physics.solverIterations;
            Physics.defaultSolverVelocityIterations = physics.velocityIterations;
            Physics.defaultMaxAngularSpeed = physics.maxAngularVelocity;
            Physics.defaultMaxDepenetrationVelocity = physics.maxDepenetrationVelocity;
            Physics.sleepThreshold = physics.sleepThreshold;
            Physics.bounceThreshold = physics.bounceThreshold;
            Physics.reuseCollisionCallbacks = true;
            Physics.queriesHitBackfaces = false;
        }

        public void ApplyAudioSettings()
        {
            if (Sandbox.Audio == null) return;
            Sandbox.Audio.Master = audioSettings.master;
            Sandbox.Audio.SfxVolume = audioSettings.effects;
            Sandbox.Audio.AmbientVolume = audioSettings.ambient;
            Sandbox.Audio.VoiceVolume = audioSettings.voices;
        }

        public RagdollBody SpawnRagdoll(Vector3 pos, float yaw)
        {
            int alive = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) alive++;
            if (alive >= general.maxRagdolls)
            {
                RagdollBody oldest = null;
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null && Sandbox.Ragdolls[i] != Sandbox.Possessed) { oldest = Sandbox.Ragdolls[i]; break; }
                if (oldest != null) RemoveRagdoll(oldest);
                else return null;
            }
            var body = RagdollBody.Spawn(runtimeRoot, pos, yaw, character, NextRagdollIndex());
            if (body != null) Sandbox.Ragdolls.Add(body);
            return body;
        }

        int NextRagdollIndex()
        {
            for (int i = 0; i < 64; i++)
            {
                bool used = false;
                for (int k = 0; k < Sandbox.Ragdolls.Count; k++) if (Sandbox.Ragdolls[k] != null && Sandbox.Ragdolls[k].Index == i) { used = true; break; }
                if (!used) return i;
            }
            return Sandbox.Ragdolls.Count;
        }

        public void RemoveRagdoll(RagdollBody body)
        {
            if (body == null) return;
            if (Sandbox.Possessed == body) { Sandbox.Possessed = null; if (Sandbox.Cam != null) Sandbox.Cam.SetMode(CameraMode.FirstPerson); }
            if (Sandbox.Hovered == body) Sandbox.Hovered = null;
            if (Sandbox.Tools != null) Sandbox.Tools.ReleaseFrom(body);
            Sandbox.Ragdolls.Remove(body);
            body.DestroyBody();
        }

        /// <summary>R: respawn every ragdoll at its spawn. Shift+R: also reset props, blood and explosives.</summary>
        public void ResetSandbox(bool full)
        {
            if (Sandbox.Tools != null) Sandbox.Tools.ReleaseAll();
            int count = 0;
            for (int i = Sandbox.Ragdolls.Count - 1; i >= 0; i--)
            {
                if (Sandbox.Ragdolls[i] != null) count++;
                RemoveRagdoll(Sandbox.Ragdolls[i]);
            }
            Sandbox.Ragdolls.Clear();
            count = Mathf.Max(1, count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Sandbox.World.RagdollSpawn + new Vector3((i % 3 - 1) * 1.6f, 0f, (i / 3) * 1.8f);
                SpawnRagdoll(p, Sandbox.World.RagdollSpawnYaw);
            }
            if (full)
            {
                Sandbox.World.ResetProps();
                Sandbox.Decals.Clear();
                Sandbox.Blood.Clear();
                Sandbox.Explosions.Clear();
                Sandbox.Tools.ClearSpawned();
            }
            SandboxEvents.RaiseToast(full ? "Scene reset" : "Ragdoll reset", Color.white);
        }

        void Update()
        {
            if (!built) return;
            HandleGlobalKeys();
            if (Math.Abs(appliedTimestep - physics.fixedTimestep) > 1e-5f) ApplyPhysicsSettings();
            if (appliedPreset != graphics.preset) { appliedPreset = graphics.preset; Quality.ApplyPreset(graphics.preset, this); }
            if (appliedGravity != physics.gravity) { appliedGravity = physics.gravity; TimeControl.SetGravity(physics.gravity); }
            TimeControl.Apply();
            InputBridge.MouseSensitivity = player.mouseSensitivity;
            InputBridge.InvertY = player.invertY;
            ApplyAudioSettings();

            Quality.Tick(this);
            safety.Tick(this);
            Sandbox.World.Tick(Time.deltaTime);
            if (Sandbox.Player != null) Sandbox.Player.Tick();
            Sandbox.Tools.Tick();
            Sandbox.Explosions.Tick(Time.deltaTime);
            Sandbox.Blood.Tick(Time.deltaTime);
            Sandbox.Decals.Tick(Time.deltaTime);
            Sandbox.Fx.Tick(Time.deltaTime);
            Sandbox.Audio.Tick(Sandbox.World.WindStrength);
            Sandbox.Hud.Tick();
        }

        void FixedUpdate()
        {
            if (!built) return;
            Sandbox.World.FixedTick(Time.fixedDeltaTime);
            Sandbox.Tools.FixedTick(Time.fixedDeltaTime);
            Sandbox.Rewind.FixedTick(this);
        }

        void LateUpdate()
        {
            if (!built) return;
            if (Sandbox.Cam != null) Sandbox.Cam.LateTick();
            Sandbox.Tools.LateTick();
            Sandbox.Decals.LateTick();
            Sandbox.Hud.LateTick();
        }

        void HandleGlobalKeys()
        {
            if (InputBridge.Down(KeyId.Escape))
            {
                if (Sandbox.Hud.HelpVisible) Sandbox.Hud.ShowHelp(false);
                else SetPaused(!TimeControl.Paused);
            }
            if (InputBridge.Down(KeyId.Tab) && !TimeControl.Paused) Sandbox.Hud.ToggleSettings();
            if (InputBridge.Down(KeyId.H)) Sandbox.Hud.ShowHelp(!Sandbox.Hud.HelpVisible);
            if (InputBridge.Down(KeyId.F1)) Sandbox.Hud.ToggleHidden();
            if (TimeControl.Paused || InputBridge.GameplayBlocked) return;

            if (InputBridge.Down(KeyId.R)) ResetSandbox(InputBridge.Shift);
            if (InputBridge.Down(KeyId.T))
            {
                TimeControl.SlowMotion = !TimeControl.SlowMotion;
                SandboxEvents.RaiseToast(TimeControl.SlowMotion ? "Slow motion" : "Normal speed", new Color(0.7f, 0.85f, 1f));
            }
            if (InputBridge.Down(KeyId.G))
            {
                physics.gravity = (GravityMode)(((int)physics.gravity + 1) % 3);
                SandboxEvents.RaiseToast("Gravity: " + physics.gravity, new Color(0.7f, 0.85f, 1f));
            }
            if (InputBridge.Down(KeyId.X)) SetXRay(!Sandbox.XRay);
            if (InputBridge.Down(KeyId.V)) TogglePossess();
            if (InputBridge.Down(KeyId.F)) Sandbox.Cam.ToggleFollow();
            if (InputBridge.Down(KeyId.N))
            {
                general.wander = !general.wander;
                SandboxEvents.RaiseToast(general.wander ? "Wander on" : "Wander off", Color.white);
            }
            if (InputBridge.Down(KeyId.B)) Sandbox.Explosions.DetonateAllC4();
        }

        public void SetPaused(bool paused)
        {
            TimeControl.Paused = paused;
            TimeControl.Apply();
            Sandbox.Hud.ShowPause(paused);
            Sandbox.Audio.SetPaused(paused);
            CursorControl.SetLocked(!paused && !Sandbox.Hud.SettingsVisible);
        }

        public void SetXRay(bool on)
        {
            Sandbox.XRay = on;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].SetXRay(on);
            SandboxEvents.RaiseToast(on ? "Anatomy X-ray on" : "X-ray off", new Color(0.6f, 0.9f, 1f));
        }

        public void TogglePossess()
        {
            if (Sandbox.Possessed != null)
            {
                Sandbox.Possessed.SetPossessed(false);
                Sandbox.Possessed = null;
                Sandbox.Cam.SetMode(CameraMode.FirstPerson);
                SandboxEvents.RaiseToast("Back to first person", Color.white);
                return;
            }
            RagdollBody target = Sandbox.Hovered != null && Time.unscaledTime - Sandbox.HoveredTime < 1f ? Sandbox.Hovered : null;
            if (target == null || !target.Alive)
            {
                float best = float.MaxValue;
                Vector3 cp = Sandbox.MainCamera.transform.position;
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
                {
                    var r = Sandbox.Ragdolls[i];
                    if (r == null || !r.Alive) continue;
                    float d = (r.CenterOfMass - cp).sqrMagnitude;
                    if (d < best) { best = d; target = r; }
                }
            }
            if (target == null || !target.Alive) { SandboxEvents.RaiseToast("No living ragdoll to possess", new Color(1f, 0.7f, 0.6f)); return; }
            Sandbox.Possessed = target;
            target.SetPossessed(true);
            Sandbox.Cam.SetMode(CameraMode.Possess);
            SandboxEvents.RaiseToast("Possessing ragdoll: WASD move, Space jump, Shift run, C crouch, LMB/RMB arms. V to exit.", new Color(1f, 0.85f, 0.6f));
        }

        void OnDestroy()
        {
            if (Sandbox.Boot != this) return;
            RestoreGlobals();
            for (int i = 0; i < disabledSceneObjects.Count; i++) if (disabledSceneObjects[i] != null) disabledSceneObjects[i].enabled = true;
            CursorControl.SetLocked(false);
            SandboxEvents.ClearAll();
            Registry.Clear();
            Sandbox.Reset();
            if (runtimeRoot != null) Destroy(runtimeRoot.gameObject);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!built) return;
            if (focus && !TimeControl.Paused && Sandbox.Hud != null && !Sandbox.Hud.SettingsVisible) CursorControl.SetLocked(true);
        }
    }
    #endregion
}
