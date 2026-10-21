// =====================================================================================================
//  RAGDOLL SANDBOX  -  a realistic active-ragdoll physics playground, built entirely from code.
//
//  Setup: empty scene -> create an empty GameObject -> Add Component "Ragdoll Sandbox Boot" -> Play.
//  Everything (map, sky, lights, camera, player, character, meshes, textures, materials, particles,
//  audio, UI, EventSystem, networking) is generated at runtime. Every tunable lives on this component.
//
//  Files (all in this folder, only this component is added by hand):
//    RagdollSandboxBoot.cs     Bootstrap, settings, time control, adaptive quality, safety net, rewind
//    SandboxCore.cs            Compat, input bridge (old + new input), math, physics materials, layers, registry, events
//    SandboxFactories.cs       TextureFactory, MaterialFactory, MeshFactory
//    SandboxAudio.cs           ProceduralAudio (synthesized clips + pooled 3D sources)
//    SandboxPools.cs           Particle FX, light flashes, tracers, decals (ring buffer, batched meshes)
//    SandboxWorld.cs           WorldBuilder + hazards (platforms, saws, press, spikes, cannon...)
//    RagdollBody.cs            RagdollBody (segments, joints, topology, update loop) and BodyPart
//    RagdollSkinTemplate.cs    The continuous body mesh (SDF + surface nets), skin weights, UV atlas, base paint
//    RagdollSkin.cs            BodySkin (single skinned body, job queue) and BodyCanvas (paint atlas)
//    RagdollMeshOps.cs         Slicing, holes, dents/squash/stretch, fracture re-weighting (threaded jobs)
//    RagdollAnatomy.cs         Skeleton, arteries and organ meshes
//    RagdollMotor.cs           Muscles, IK, balance assist, locomotion, Euphoria-style behaviours
//    RagdollDamage.cs          Vitals, wounds, organs, damage, fractures, dismemberment
//    SandboxBlood.cs           Droplet simulation, splats, puddles, prints
//    SandboxExplosives.cs      Explosions and explosive props
//    SandboxTools.cs           Hotbar tools, grab, rope, blade, laser, guns, view models
//    SandboxVehicle.cs         Drivable / AI clay car
//    SandboxPlayer.cs          First-person player + camera rig
//    SandboxNet.cs             UDP host/client networking (host-authoritative)
//    SandboxHUD.cs             uGUI HUD and menus built in code
//    SandboxDebug.cs           F3 debug overlay and the startup self-test
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
        public bool showMainMenu = true;
        public bool showHelpOnStart = false;
        [Tooltip("Free mode: idle ragdolls wander around the plaza.")]
        public bool wander = false;
        [Tooltip("Master switch for every reaction (flinch, stagger, brace, grab, clutch, pain voices).")]
        public bool reactions = true;
        [Tooltip("Ragdolls look at the player when nearby.")]
        public bool gazeAtPlayer = true;
        [Range(1, 8)] public int maxRagdolls = 6;
        [Tooltip("At startup, check that the ragdoll stands for 3 s and walks for 5 s; raise the assist if it fails.")]
        public bool selfTestOnStart = true;
    }

    [Serializable]
    public class PhysicsSettingsData
    {
        [Range(0.005f, 0.02f)] public float fixedTimestep = 0.01f;
        [Tooltip("Default solver iterations for props and the world.")]
        [Range(2, 20)] public int solverIterations = 6;
        [Range(1, 8)] public int velocityIterations = 2;
        [Tooltip("Solver iterations for ragdoll segments (the joints need more than props).")]
        [Range(4, 30)] public int ragdollSolverIterations = 12;
        [Range(1, 12)] public int ragdollVelocityIterations = 4;
        [Range(7f, 60f)] public float maxAngularVelocity = 30f;
        [Range(1f, 10f)] public float maxDepenetrationVelocity = 3f;
        public GravityMode gravity = GravityMode.Earth;
        [Range(0.02f, 1f)] public float slowMotionScale = 0.25f;
        [Range(0.0005f, 0.05f)] public float sleepThreshold = 0.01f;
        [Range(0.2f, 5f)] public float bounceThreshold = 2f;
        [Tooltip("Validate positions every frame and recover from NaN / out of bounds.")]
        public bool safetyNet = true;
        [Range(10f, 200f)] public float maxBodySpeed = 90f;
        [Range(10, 400)] public int maxActiveProps = 120;
    }

    [Serializable]
    public class CharacterSettings
    {
        [Tooltip("Clay: chunky plasticine with kneaded noise and fingerprints. Realistic: normal skin. Same mesh.")]
        public BodyLook look = BodyLook.Clay;
        [Range(1.5f, 2.05f)] public float height = 1.75f;
        [Range(50f, 110f)] public float mass = 72f;
        public Color skinColor = new Color(0.95f, 0.72f, 0.58f);
        [Range(0f, 0.2f)] public float skinVariation = 0.05f;
        public bool shirt = true;
        public Color shirtColor = new Color(0.22f, 0.5f, 0.85f);
        public bool pants = true;
        public Color pantsColor = new Color(0.25f, 0.27f, 0.36f);
        public bool shoes = true;
        public Color shoesColor = new Color(0.55f, 0.3f, 0.18f);
        public bool hair = true;
        public Color hairColor = new Color(0.35f, 0.2f, 0.12f);
        public Color eyeColor = new Color(0.25f, 0.4f, 0.55f);
        [Tooltip("Body paint atlas size (one per ragdoll).")]
        [Range(256, 2048)] public int bodyTextureSize = 1024;
        [Range(0f, 1f)] public float claySmoothness = 0.42f;
        [Range(0f, 1f)] public float skinSmoothness = 0.32f;
    }

    [Serializable]
    public class MuscleSettings
    {
        [Range(0f, 3f)] public float strength = 1f;
        [Range(0f, 8000f)] public float spineSpring = 1200f;
        [Range(0f, 600f)] public float spineDamper = 90f;
        [Range(0f, 3000f)] public float neckSpring = 300f;
        [Range(0f, 300f)] public float neckDamper = 25f;
        [Range(0f, 3000f)] public float shoulderSpring = 260f;
        [Range(0f, 300f)] public float shoulderDamper = 22f;
        [Range(0f, 2000f)] public float elbowSpring = 150f;
        [Range(0f, 200f)] public float elbowDamper = 12f;
        [Range(0f, 600f)] public float wristSpring = 40f;
        [Range(0f, 60f)] public float wristDamper = 3f;
        [Range(0f, 12000f)] public float hipSpring = 1600f;
        [Range(0f, 900f)] public float hipDamper = 120f;
        [Range(0f, 12000f)] public float kneeSpring = 1600f;
        [Range(0f, 900f)] public float kneeDamper = 120f;
        [Range(0f, 8000f)] public float ankleSpring = 700f;
        [Range(0f, 600f)] public float ankleDamper = 55f;
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
        [Tooltip("Upright drive stiffness of the pelvis assist (implicit joint drive, Nm/rad).")]
        [Range(0f, 8000f)] public float pelvisUprightSpring = 2500f;
        [Range(0f, 800f)] public float pelvisUprightDamper = 220f;
        [Range(0f, 8000f)] public float chestUprightSpring = 1500f;
        [Range(0f, 800f)] public float chestUprightDamper = 130f;
        [Tooltip("Max assist torque (Nm). Scales with consciousness, damage and the behaviour, fades out when falling.")]
        [Range(0f, 1200f)] public float assistTorque = 380f;
        [Range(0f, 1.5f)] public float chestAssistRatio = 0.6f;
        [Tooltip("Fraction of the weight not borne by the stance legs that the support assist carries.")]
        [Range(0f, 1.2f)] public float weightSupport = 0.85f;
        [Tooltip("Pelvis height regulation (m/s^2 per metre of error).")]
        [Range(0f, 200f)] public float heightGain = 40f;
        [Range(0f, 60f)] public float heightDamping = 9f;
        [Tooltip("Horizontal velocity tracking gain (1/s).")]
        [Range(0f, 30f)] public float velocityGain = 6f;
        [Tooltip("Centre-of-mass centring over the feet while standing (1/s^2).")]
        [Range(0f, 80f)] public float comGain = 12f;
        [Tooltip("Max horizontal assist force (x body weight). A push stronger than this needs recovery steps.")]
        [Range(0f, 1f)] public float maxHorizontalAssist = 0.3f;
        [Range(0.45f, 0.6f)] public float standHeightRatio = 0.53f;
        [Range(0f, 0.2f)] public float captureMargin = 0.05f;
        [Range(20f, 80f)] public float fallAngle = 55f;
        [Tooltip("Capture point distance outside the feet (m) beyond which the body gives up and falls.")]
        [Range(0.2f, 2f)] public float unrecoverableDistance = 0.6f;
        [Tooltip("Assist weight while getting up (multiplies the assist budget).")]
        [Range(0f, 2f)] public float getUpAssist = 1f;
        [Range(0, 10)] public int getUpAttempts = 5;
        [Range(2f, 20f)] public float getUpTimeout = 10f;
        [Range(0f, 5f)] public float getUpDelay = 1.2f;
    }

    [Serializable]
    public class LocomotionSettings
    {
        [Range(0.2f, 3f)] public float walkSpeed = 1.3f;
        [Range(1f, 7f)] public float runSpeed = 3.2f;
        [Range(0.1f, 2f)] public float crouchSpeed = 0.6f;
        [Range(20f, 400f)] public float turnSpeed = 180f;
        [Range(0.2f, 0.8f)] public float stepTimeWalk = 0.36f;
        [Range(0.15f, 0.5f)] public float stepTimeRun = 0.26f;
        [Tooltip("Duration of the Lift phase (heel off, knee starts to bend) before the swing.")]
        [Range(0f, 0.2f)] public float liftTime = 0.05f;
        [Range(0.02f, 0.3f)] public float stepHeight = 0.09f;
        [Range(0.2f, 1f)] public float maxStride = 0.62f;
        [Range(0.4f, 1.6f)] public float runStride = 0.95f;
        [Range(0.08f, 0.35f)] public float stanceWidth = 0.18f;
        [Range(0f, 0.5f)] public float raibertGain = 0.12f;
        [Range(0f, 60f)] public float toeOffImpulse = 10f;
        [Tooltip("Max height of a step up (stairs, kerbs).")]
        [Range(0.05f, 0.6f)] public float maxStepUp = 0.32f;
        [Range(0f, 1.2f)] public float jumpHeight = 0.35f;
        [Range(0f, 60f)] public float armSwing = 26f;
        [Range(0f, 20f)] public float spineCounterRotation = 7f;
        [Range(0f, 800f)] public float crawlForce = 300f;
        [Tooltip("Upward speed of each hop on one leg (m/s).")]
        [Range(0f, 5f)] public float hopForce = 2.4f;
        [Range(0.1f, 2f)] public float hopSpeed = 0.9f;
        [Range(0f, 4000f)] public float swingFootGain = 600f;
        [Range(0f, 200f)] public float swingFootDamping = 40f;
        [Range(0f, 2000f)] public float swingForceLimit = 380f;
        [Tooltip("Planted feet are held in place (anti-slide).")]
        [Range(0f, 10000f)] public float footLockGain = 2500f;
        [Range(0f, 400f)] public float footLockDamping = 60f;
        [Range(0f, 2000f)] public float footLockLimit = 300f;
        [Range(0f, 1f)] public float stumbleChance = 0.5f;
    }

    [Serializable]
    public class BehaviourSettings
    {
        [Tooltip("Strength of every Euphoria-style reaction (0 = none).")]
        [Range(0f, 2f)] public float reactionStrength = 1f;
        [Tooltip("Hands reach for ledges, poles, cars and props while falling and hang on.")]
        public bool grabAndHold = true;
        [Range(0.1f, 3f)] public float gripStrength = 1f;
        [Tooltip("Global muscle stiffness multiplier.")]
        [Range(0.2f, 2f)] public float stiffness = 1f;
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
        [Range(0f, 200f)] public float laserDamagePerSecond = 60f;
        [Range(0f, 60f)] public float shockDamagePerSecond = 6f;
        [Range(0f, 3f)] public float explosionDamageScale = 1f;
        [Tooltip("Mesh deformation strength (dents, squash, stretch).")]
        [Range(0f, 3f)] public float deformationScale = 1f;
        [Range(0f, 0.08f)] public float maxDentDepth = 0.035f;
        [Range(0.003f, 0.03f)] public float bulletHoleRadius = 0.009f;
        [Range(0.003f, 0.03f)] public float pelletHoleRadius = 0.007f;
        [Range(0.02f, 0.12f)] public float craterRadius = 0.05f;
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
        [Tooltip("How hard a hurt body keeps trying to move, get up, hop, crawl and hold on.")]
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
        [Tooltip("Blade sweep speed (deg/s of view rotation) needed to cut through.")]
        [Range(10f, 1000f)] public float bladeSeverSpeed = 120f;
        public bool allowTorsoTear = true;
        [Range(0f, 3f)] public float stumpSpurt = 1f;
        [Range(0f, 400f)] public float ruptureThreshold = 85f;
    }

    [Serializable]
    public class BloodSettings
    {
        [Range(0f, 2.5f)] public float bloodAmount = 1f;
        [Tooltip("Hard cap on live blood droplets.")]
        [Range(32, 4000)] public int maxDroplets = 900;
        [Tooltip("Collision raycasts per frame for droplets (round-robin).")]
        [Range(4, 256)] public int raycastsPerFrame = 48;
        [Range(100, 4000)] public int maxDecals = 1500;
        [Range(4, 128)] public int maxPuddles = 48;
        [Range(0.1f, 2.5f)] public float puddleMaxRadius = 1.1f;
        [Range(10f, 600f)] public float puddleDryTime = 140f;
        [Range(10f, 600f)] public float decalDryTime = 90f;
        [Range(0, 30)] public int footprintSteps = 9;
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
        [Tooltip("Shift = strong pull (can rip limbs off, stretches the skin).")]
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
        [Tooltip("Shotgun shots closer than this make one large crater.")]
        [Range(0f, 3f)] public float pointBlankDistance = 1.2f;
        [Range(1f, 60f)] public float laserRange = 30f;
        [Tooltip("Angular laser sweep speed (deg/s) needed to cut a limb (slower sweeps burn).")]
        [Range(1f, 400f)] public float laserCutSweep = 25f;
        [Range(0f, 30f)] public float throwSpeed = 15f;
        [Range(10, 300)] public int propCap = 80;
        [Range(1, 60)] public int ropeCap = 20;
        [Range(1f, 200f)] public float maxRange = 120f;
        public bool viewModels = true;
    }

    [Serializable]
    public class PlayerSettings
    {
        public string playerName = "Player";
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
        [Tooltip("Auto quality: lowers blood, decals, particles, physics iterations, shadows and render scale when the " +
                 "frame time is over budget, and restores them when stable.")]
        public bool autoQuality = true;
        [Range(20f, 144f)] public float targetFps = 60f;
        [Tooltip("Frame rate cap (0 = no cap).")]
        [Range(0, 240)] public int fpsCap = 0;
        public bool vSync = false;
        public bool shadows = true;
        [Range(10f, 150f)] public float shadowDistance = 60f;
        [Range(0.5f, 1.5f)] public float renderScale = 1f;
        [Tooltip("Run mesh cutting/holes on a worker thread.")]
        public bool threadedMeshJobs = true;
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
    public class VehicleSettings
    {
        [Range(400f, 4000f)] public float mass = 1100f;
        [Range(2f, 40f)] public float maxSpeed = 17f;
        [Range(1000f, 20000f)] public float motorForce = 6500f;
        [Range(5f, 45f)] public float steerAngle = 32f;
        [Range(1f, 25f)] public float aiSpeed = 9f;
        [Range(5000f, 80000f)] public float suspensionSpring = 32000f;
        [Range(100f, 8000f)] public float suspensionDamper = 2600f;
        [Range(1, 6)] public int maxCars = 3;
    }

    [Serializable]
    public class NetworkSettings
    {
        [Range(1024, 65535)] public int port = 27777;
        [Range(2, 4)] public int maxPlayers = 4;
        public string lastAddress = "127.0.0.1";
        [Range(10, 60)] public int snapshotRate = 20;
        [Range(10, 60)] public int inputRate = 30;
        [Tooltip("Client interpolation buffer (s).")]
        [Range(0.03f, 0.4f)] public float interpolationDelay = 0.1f;
        [Range(0f, 0.5f)] public float maxExtrapolation = 0.25f;
        [Range(2f, 30f)] public float timeout = 6f;
        [Tooltip("Bodies farther than this from every player are sent at a lower rate.")]
        [Range(5f, 100f)] public float farDistance = 30f;
    }

    [Serializable]
    public class DebugSettings
    {
        [Tooltip("F3: COM, support polygon, foot targets, joint loads, states, FPS and counters.")]
        public bool showOverlay = false;
        public bool logSelfTest = true;
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
        public static NetSession Net;
        public static DebugOverlay Debug;
        public static SelfTest Test;
        public static VehicleManager Vehicles;
        public static Camera MainCamera;
        public static Transform Root;
        public static readonly List<RagdollBody> Ragdolls = new List<RagdollBody>(8);
        public static RagdollBody Possessed;
        public static RagdollBody Hovered;
        public static float HoveredTime;
        public static bool XRay;
        public static int Seed;
        public static bool InMenu;

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
        public static bool IsClient { get { return Net != null && Net.IsClient; } }
        public static bool IsHost { get { return Net != null && Net.IsHost; } }

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
            Cam = null; Tools = null; Hud = null; Rewind = null; MainCamera = null; Root = null; Net = null; Debug = null; Test = null; Vehicles = null;
            Ragdolls.Clear(); Possessed = null; Hovered = null; XRay = false; InMenu = false;
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
            // online sessions never pause the shared simulation
            if (Paused && !(Sandbox.Net != null && Sandbox.Net.Active)) scale = 0f;
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

    /// <summary>The live budgets every system reads (set by the preset, then scaled by Auto quality).</summary>
    public struct QualityState
    {
        public float level;            // 0..1 auto scale (1 = full preset)
        public float particleScale;
        public float bloodScale;
        public int bloodDroplets;
        public int bloodRaycasts;
        public int decalCap;
        public int ragdollIterations;
        public float shadowDistance;
        public float renderScale;
        public int uploadsPerFrame;
    }

    /// <summary>Quality presets (Low/Medium/High/Ultra) and the adaptive Auto mode: a smoothed frame time drives a
    /// 0..1 scale that lowers blood, decals, particles, physics iterations, shadows and render scale when over budget
    /// and restores them (with hysteresis) when the frame rate is stable.</summary>
    public class QualityManager
    {
        float smoothedDt = 1f / 60f;
        float overTime, underTime;
        public QualityState State;
        QualityState baseState;
        public float AutoLevel = 1f;
        public float Fps { get { return 1f / Mathf.Max(1e-4f, smoothedDt); } }
        public float FrameMs { get { return smoothedDt * 1000f; } }
        public float ParticleScale { get { return State.particleScale; } }
        static System.Reflection.PropertyInfo shadowProp, scaleProp;
        static bool reflected;

        public void ApplyPreset(QualityPreset p, RagdollSandboxBoot b)
        {
            var s = new QualityState();
            switch (p)
            {
                case QualityPreset.Low:
                    s.particleScale = 0.4f; s.bloodDroplets = 250; s.bloodRaycasts = 16; s.decalCap = 400; s.ragdollIterations = 8; s.shadowDistance = 25f; s.renderScale = 0.8f; s.uploadsPerFrame = 1; break;
                case QualityPreset.Medium:
                    s.particleScale = 0.7f; s.bloodDroplets = 500; s.bloodRaycasts = 32; s.decalCap = 900; s.ragdollIterations = 10; s.shadowDistance = 40f; s.renderScale = 1f; s.uploadsPerFrame = 2; break;
                case QualityPreset.High:
                    s.particleScale = 1f; s.bloodDroplets = b.blood.maxDroplets; s.bloodRaycasts = b.blood.raycastsPerFrame; s.decalCap = b.blood.maxDecals; s.ragdollIterations = b.physics.ragdollSolverIterations; s.shadowDistance = b.graphics.shadowDistance; s.renderScale = b.graphics.renderScale; s.uploadsPerFrame = 2; break;
                default:
                    s.particleScale = 1.3f; s.bloodDroplets = Mathf.Max(b.blood.maxDroplets, 1800); s.bloodRaycasts = Mathf.Max(b.blood.raycastsPerFrame, 96); s.decalCap = Mathf.Max(b.blood.maxDecals, 2500); s.ragdollIterations = Mathf.Max(b.physics.ragdollSolverIterations, 16); s.shadowDistance = Mathf.Max(b.graphics.shadowDistance, 90f); s.renderScale = Mathf.Max(1f, b.graphics.renderScale); s.uploadsPerFrame = 3; break;
            }
            s.bloodScale = 1f;
            s.level = 1f;
            baseState = s;
            AutoLevel = 1f;
            Commit(b, true);
        }

        void Commit(RagdollSandboxBoot b, bool force)
        {
            var s = baseState;
            float k = b.graphics.autoQuality ? AutoLevel : 1f;
            s.level = k;
            s.particleScale = baseState.particleScale * Mathf.Lerp(0.3f, 1f, k);
            s.bloodScale = Mathf.Lerp(0.25f, 1f, k);
            s.bloodDroplets = Mathf.Max(48, Mathf.RoundToInt(baseState.bloodDroplets * Mathf.Lerp(0.25f, 1f, k)));
            s.bloodRaycasts = Mathf.Max(6, Mathf.RoundToInt(baseState.bloodRaycasts * Mathf.Lerp(0.3f, 1f, k)));
            s.decalCap = Mathf.Max(100, Mathf.RoundToInt(baseState.decalCap * Mathf.Lerp(0.35f, 1f, k)));
            s.ragdollIterations = Mathf.Max(6, Mathf.RoundToInt(baseState.ragdollIterations * Mathf.Lerp(0.6f, 1f, k)));
            s.shadowDistance = baseState.shadowDistance * Mathf.Lerp(0.45f, 1f, k);
            s.renderScale = baseState.renderScale * Mathf.Lerp(0.7f, 1f, k);
            s.uploadsPerFrame = k < 0.5f ? 1 : baseState.uploadsPerFrame;
            bool itersChanged = s.ragdollIterations != State.ragdollIterations;
            State = s;
            CanvasUploader.PerFrame = s.uploadsPerFrame;
            if (Sandbox.Decals != null) Sandbox.Decals.SetCapacity(s.decalCap);
            float shadow = b.graphics.shadows ? s.shadowDistance : 0f;
            QualitySettings.shadowDistance = shadow;
            SetPipeline(shadow, s.renderScale);
            var sun = Sandbox.World != null ? Sandbox.World.Sun : null;
            if (sun != null) sun.shadows = !b.graphics.shadows ? LightShadows.None : b.graphics.preset == QualityPreset.Low ? LightShadows.Hard : LightShadows.Soft;
            if (itersChanged || force)
            {
                b.physics.ragdollSolverIterations = Mathf.Max(b.physics.ragdollSolverIterations, 4);
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
                {
                    var r = Sandbox.Ragdolls[i];
                    if (r == null) continue;
                    for (int j = 0; j < r.AllPieces.Count; j++)
                    {
                        var p = r.AllPieces[j];
                        if (p != null && p.rb != null) p.rb.solverIterations = p.attached ? s.ragdollIterations : Mathf.Max(4, s.ragdollIterations / 2);
                    }
                }
            }
            Application.targetFrameRate = b.graphics.fpsCap > 0 ? b.graphics.fpsCap : -1;
            QualitySettings.vSyncCount = b.graphics.vSync ? 1 : 0;
        }

        /// <summary>URP's shadow distance and render scale live on the pipeline asset (set by reflection so the
        /// project still compiles without URP).</summary>
        static void SetPipeline(float shadow, float renderScale)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return;
            try
            {
                if (!reflected)
                {
                    reflected = true;
                    shadowProp = rp.GetType().GetProperty("shadowDistance");
                    scaleProp = rp.GetType().GetProperty("renderScale");
                }
                if (shadowProp != null && shadowProp.CanWrite) shadowProp.SetValue(rp, shadow, null);
                if (scaleProp != null && scaleProp.CanWrite) scaleProp.SetValue(rp, renderScale, null);
            }
            catch (Exception) { }
        }

        public void Tick(RagdollSandboxBoot b)
        {
            float dt = Time.unscaledDeltaTime;
            smoothedDt = Mathf.Lerp(smoothedDt, Mathf.Min(dt, 0.25f), 0.05f);
            if (!b.graphics.autoQuality) return;
            float budget = 1f / Mathf.Max(10f, b.graphics.targetFps);
            if (smoothedDt > budget * 1.15f) { overTime += dt; underTime = 0f; }
            else if (smoothedDt < budget * 0.85f) { underTime += dt; overTime = 0f; }
            else { overTime = 0f; underTime = 0f; }
            if (overTime > 1.2f && AutoLevel > 0f)
            {
                overTime = 0f;
                AutoLevel = Mathf.Max(0f, AutoLevel - 0.15f);
                Commit(b, false);
            }
            else if (underTime > 5f && AutoLevel < 1f)
            {
                underTime = 0f;
                AutoLevel = Mathf.Min(1f, AutoLevel + 0.1f);
                Commit(b, false);
            }
        }

        public void Refresh(RagdollSandboxBoot b) { Commit(b, true); }
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
            if (!b.bonus.rewind || Sandbox.IsClient || (Sandbox.Net != null && Sandbox.Net.Active)) { if (Rewinding) Stop(); return; }
            bool hold = InputBridge.Held(KeyId.Z) && !InputBridge.GameplayBlocked;
            if (hold && !Rewinding) { Rewinding = true; for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].BeginRewind(); }
            if (!hold && Rewinding) Stop();
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null) continue;
                if (Rewinding) r.RewindStep(2);
                else r.RecordRewind(b.bonus.rewindSeconds);
            }
        }

        void Stop()
        {
            Rewinding = false;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].EndRewind();
        }
    }

    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public class RagdollSandboxBoot : MonoBehaviour
    {
        [Header("General")] public GeneralSettings general = new GeneralSettings();
        [Header("Physics")] public PhysicsSettingsData physics = new PhysicsSettingsData();
        [Header("Character (look)")] public CharacterSettings character = new CharacterSettings();
        [Header("Muscles (active ragdoll)")] public MuscleSettings muscles = new MuscleSettings();
        [Header("Balance assist")] public BalanceSettings balance = new BalanceSettings();
        [Header("Locomotion")] public LocomotionSettings locomotion = new LocomotionSettings();
        [Header("Behaviour (Euphoria-style)")] public BehaviourSettings behaviour = new BehaviourSettings();
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
        [Header("Vehicles")] public VehicleSettings vehicles = new VehicleSettings();
        [Header("Network")] public NetworkSettings network = new NetworkSettings();
        [Header("Debug")] public DebugSettings debug = new DebugSettings();
        [Header("Bonus")] public BonusSettings bonus = new BonusSettings();

        public QualityManager Quality { get; private set; }
        Transform runtimeRoot;
        SafetyNet safety;
        bool built;
        QualityPreset appliedPreset;
        bool appliedAuto, appliedShadows;
        GravityMode appliedGravity;
        float appliedTimestep;
        int appliedFpsCap;
        float tabDownTime = -1f;

        // Saved global state, restored when the sandbox is destroyed (play mode stop).
        float savedFixedDelta, savedTimeScale, savedMaxDelta, savedSleep, savedBounce, savedDepen, savedMaxAng, savedShadowDist;
        int savedIters, savedVelIters, savedTargetFps, savedVSync;
        Vector3 savedGravity;
        bool savedFog, savedReuse, savedQueriesBackfaces;
        Material savedSky;
        AmbientMode savedAmbientMode;
        Color savedAmbientSky, savedAmbientEq, savedAmbientGround;
        readonly List<Behaviour> disabledSceneObjects = new List<Behaviour>();
        readonly List<GameObject> disabledEventSystems = new List<GameObject>();

        public bool Built { get { return built; } }

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
            savedMaxAng = Physics.defaultMaxAngularSpeed; savedReuse = Physics.reuseCollisionCallbacks; savedQueriesBackfaces = Physics.queriesHitBackfaces;
            savedFog = RenderSettings.fog; savedSky = RenderSettings.skybox; savedAmbientMode = RenderSettings.ambientMode;
            savedAmbientSky = RenderSettings.ambientSkyColor; savedAmbientEq = RenderSettings.ambientEquatorColor; savedAmbientGround = RenderSettings.ambientGroundColor;
            savedShadowDist = QualitySettings.shadowDistance; savedTargetFps = Application.targetFrameRate; savedVSync = QualitySettings.vSyncCount;
            Layers.Setup();
        }

        void RestoreGlobals()
        {
            Time.fixedDeltaTime = savedFixedDelta; Time.timeScale = savedTimeScale; Time.maximumDeltaTime = savedMaxDelta;
            Physics.gravity = savedGravity; Physics.defaultSolverIterations = savedIters; Physics.defaultSolverVelocityIterations = savedVelIters;
            Physics.sleepThreshold = savedSleep; Physics.bounceThreshold = savedBounce; Physics.defaultMaxDepenetrationVelocity = savedDepen;
            Physics.defaultMaxAngularSpeed = savedMaxAng; Physics.reuseCollisionCallbacks = savedReuse; Physics.queriesHitBackfaces = savedQueriesBackfaces;
            RenderSettings.fog = savedFog; RenderSettings.skybox = savedSky; RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientSkyColor = savedAmbientSky; RenderSettings.ambientEquatorColor = savedAmbientEq; RenderSettings.ambientGroundColor = savedAmbientGround;
            QualitySettings.shadowDistance = savedShadowDist; Application.targetFrameRate = savedTargetFps; QualitySettings.vSyncCount = savedVSync;
            Layers.Restore();
            AudioListener.pause = false;
        }

        /// <summary>Turns off cameras, lights, listeners and event systems that came with the scene template.</summary>
        void DisableSceneDefaults()
        {
            foreach (var c in Compat.FindAll<Camera>()) { if (c != null && c.enabled) { c.enabled = false; disabledSceneObjects.Add(c); } }
            foreach (var l in Compat.FindAll<Light>()) { if (l != null && l.enabled) { l.enabled = false; disabledSceneObjects.Add(l); } }
            foreach (var a in Compat.FindAll<AudioListener>()) { if (a != null && a.enabled) { a.enabled = false; disabledSceneObjects.Add(a); } }
            foreach (var e in Compat.FindAll<EventSystem>()) { if (e != null && e.gameObject.activeSelf) { e.gameObject.SetActive(false); disabledEventSystems.Add(e.gameObject); } }
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
            Sandbox.Fx.Build(r, 3000);
            Sandbox.Decals = new DecalSystem();
            Sandbox.Decals.Build(r, blood.maxDecals);
            Sandbox.Blood = new BloodSystem();
            Sandbox.Blood.Build(r);
            Sandbox.Explosions = new ExplosionSystem();
            Sandbox.Explosions.Build(r);

            Sandbox.World = new WorldBuilder();
            Sandbox.World.Build(r, seed);
            Sandbox.Vehicles = new VehicleManager();
            Sandbox.Vehicles.Build(r);

            Sandbox.Player = PlayerController.Create(r, Sandbox.World.PlayerSpawn, Sandbox.World.PlayerSpawnYaw);
            Sandbox.Cam = CameraRig.Create(r);
            Sandbox.Tools = new ToolSystem();
            Sandbox.Tools.Build(r);
            Sandbox.Rewind = new RewindSystem();
            Sandbox.Net = new NetSession();
            Sandbox.Debug = new DebugOverlay();
            Sandbox.Debug.Build(r);
            Sandbox.Hud = new Hud();
            Sandbox.Hud.Build(r);

            appliedPreset = graphics.preset;
            appliedAuto = graphics.autoQuality;
            appliedShadows = graphics.shadows;
            appliedFpsCap = graphics.fpsCap;
            Quality.ApplyPreset(graphics.preset, this);
            appliedGravity = physics.gravity;
            TimeControl.SetGravity(physics.gravity);

            SpawnInitialRagdolls();
            if (general.selfTestOnStart && Sandbox.Ragdolls.Count > 0)
            {
                Sandbox.Test = new SelfTest();
                Sandbox.Test.Begin(Sandbox.Ragdolls[0]);
            }

            if (general.showMainMenu)
            {
                Sandbox.InMenu = true;
                Sandbox.Hud.ShowMainMenu(true);
                CursorControl.SetLocked(false);
            }
            else
            {
                CursorControl.SetLocked(true);
                if (general.showHelpOnStart) Sandbox.Hud.ShowHelp(true);
            }
            SandboxEvents.RaiseToast("Welcome to the Ragdoll Sandbox. Press H for help.", new Color(0.8f, 0.9f, 1f));
        }

        void SpawnInitialRagdolls()
        {
            int count = Mathf.Clamp(general.ragdollsAtStart, 0, general.maxRagdolls);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Sandbox.World.RagdollSpawn + new Vector3((i % 3 - 1) * 1.6f, 0f, (i / 3) * 1.8f);
                SpawnRagdoll(p, Sandbox.World.RagdollSpawnYaw);
            }
        }

        /// <summary>Leaves the main menu and starts playing (offline, or as host / client once connected).</summary>
        public void StartPlaying()
        {
            Sandbox.InMenu = false;
            Sandbox.Hud.ShowMainMenu(false);
            CursorControl.SetLocked(true);
            if (general.showHelpOnStart) Sandbox.Hud.ShowHelp(true);
        }

        public void ReturnToMenu()
        {
            if (Sandbox.Net != null && Sandbox.Net.Active) Sandbox.Net.Shutdown("left the session");
            SetPaused(false);
            Sandbox.InMenu = true;
            Sandbox.Hud.ShowMainMenu(true);
            CursorControl.SetLocked(false);
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
            if (Sandbox.IsClient) { if (Sandbox.Net != null) Sandbox.Net.RequestSpawnRagdoll(pos, yaw); return null; }
            int alive = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) alive++;
            if (alive >= general.maxRagdolls)
            {
                RagdollBody oldest = null;
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
                {
                    var r = Sandbox.Ragdolls[i];
                    if (r != null && r != Sandbox.Possessed && r.OwnerPlayer < 0 && (Sandbox.Test == null || !Sandbox.Test.IsSubject(r))) { oldest = r; break; }
                }
                if (oldest != null) RemoveRagdoll(oldest);
                else return null;
            }
            var body = RagdollBody.Spawn(runtimeRoot, pos, yaw, character, NextRagdollIndex());
            if (body != null)
            {
                Sandbox.Ragdolls.Add(body);
                if (Sandbox.Net != null) Sandbox.Net.OnRagdollSpawned(body);
            }
            return body;
        }

        /// <summary>Network clients create the host's bodies as kinematic puppets.</summary>
        public RagdollBody SpawnPuppet(int netId, int index, Vector3 pos, float yaw)
        {
            var body = RagdollBody.Spawn(runtimeRoot, pos, yaw, character, index, true);
            if (body != null) { body.NetId = netId; Sandbox.Ragdolls.Add(body); }
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
            if (Sandbox.Net != null) Sandbox.Net.OnRagdollRemoved(body);
            Sandbox.Ragdolls.Remove(body);
            body.DestroyBody();
        }

        /// <summary>R: respawn every ragdoll at its spawn. Shift+R: also reset props, blood and explosives.</summary>
        public void ResetSandbox(bool full)
        {
            if (Sandbox.IsClient) { if (Sandbox.Net != null) Sandbox.Net.RequestReset(full); return; }
            if (Sandbox.Tools != null) Sandbox.Tools.ReleaseAll();
            int count = 0;
            var owners = new List<int>();
            for (int i = Sandbox.Ragdolls.Count - 1; i >= 0; i--)
            {
                var r = Sandbox.Ragdolls[i];
                if (r != null) { count++; if (r.OwnerPlayer >= 0) owners.Add(r.OwnerPlayer); }
                RemoveRagdoll(r);
            }
            Sandbox.Ragdolls.Clear();
            count = Mathf.Max(1, count - owners.Count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Sandbox.World.RagdollSpawn + new Vector3((i % 3 - 1) * 1.6f, 0f, (i / 3) * 1.8f);
                SpawnRagdoll(p, Sandbox.World.RagdollSpawnYaw);
            }
            if (Sandbox.Net != null) Sandbox.Net.RespawnPlayerBodies(owners);
            if (full)
            {
                Sandbox.World.ResetProps();
                Sandbox.Decals.Clear();
                Sandbox.Blood.Clear();
                Sandbox.Explosions.Clear();
                Sandbox.Tools.ClearSpawned();
                Sandbox.Vehicles.ClearAll();
            }
            if (Sandbox.Net != null) Sandbox.Net.OnReset(full);
            SandboxEvents.RaiseToast(full ? "Scene reset" : "Ragdoll reset", Color.white);
        }

        void Update()
        {
            if (!built) return;
            HandleGlobalKeys();
            if (Math.Abs(appliedTimestep - physics.fixedTimestep) > 1e-5f) ApplyPhysicsSettings();
            if (appliedPreset != graphics.preset) { appliedPreset = graphics.preset; Quality.ApplyPreset(graphics.preset, this); }
            if (appliedAuto != graphics.autoQuality || appliedShadows != graphics.shadows || appliedFpsCap != graphics.fpsCap)
            {
                appliedAuto = graphics.autoQuality; appliedShadows = graphics.shadows; appliedFpsCap = graphics.fpsCap;
                if (!graphics.autoQuality) Quality.AutoLevel = 1f;
                Quality.Refresh(this);
            }
            if (appliedGravity != physics.gravity) { appliedGravity = physics.gravity; TimeControl.SetGravity(physics.gravity); }
            TimeControl.Apply();
            InputBridge.MouseSensitivity = player.mouseSensitivity;
            InputBridge.InvertY = player.invertY;
            ApplyAudioSettings();

            Quality.Tick(this);
            safety.Tick(this);
            if (Sandbox.Net != null) Sandbox.Net.Tick(Time.unscaledDeltaTime);
            Sandbox.World.Tick(Time.deltaTime);
            if (Sandbox.Player != null) Sandbox.Player.Tick();
            Sandbox.Vehicles.Tick(Time.deltaTime);
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
            Sandbox.Vehicles.FixedTick(Time.fixedDeltaTime);
            Sandbox.Tools.FixedTick(Time.fixedDeltaTime);
            Sandbox.Rewind.FixedTick(this);
            if (Sandbox.Test != null) Sandbox.Test.FixedTick(Time.fixedDeltaTime);
            if (Sandbox.Net != null) Sandbox.Net.FixedTick(Time.fixedDeltaTime);
        }

        void LateUpdate()
        {
            if (!built) return;
            if (Sandbox.Net != null) Sandbox.Net.LateTick();
            if (Sandbox.Cam != null) Sandbox.Cam.LateTick();
            Sandbox.Tools.LateTick();
            Sandbox.Decals.LateTick();
            Sandbox.Debug.LateTick();
            Sandbox.Hud.LateTick();
        }

        void HandleGlobalKeys()
        {
            var hud = Sandbox.Hud;
            if (hud.ChatOpen) return; // typing: every key goes to the chat box
            if (InputBridge.Down(KeyId.Escape))
            {
                if (hud.HelpVisible) hud.ShowHelp(false);
                else if (hud.JoinPanelVisible) hud.ShowJoinPanel(false);
                else if (Sandbox.InMenu) { }
                else SetPaused(!TimeControl.Paused);
            }
            if (Sandbox.InMenu) return;
            // Tab: tap = settings panel + free cursor, hold = player list (online)
            if (InputBridge.Down(KeyId.Tab)) tabDownTime = Time.unscaledTime;
            bool tabHeld = InputBridge.Held(KeyId.Tab) && tabDownTime >= 0f;
            hud.ShowPlayerList(tabHeld && Time.unscaledTime - tabDownTime > 0.25f && Sandbox.Net != null && Sandbox.Net.Active);
            if (InputBridge.Up(KeyId.Tab))
            {
                if (tabDownTime >= 0f && Time.unscaledTime - tabDownTime <= 0.25f && !TimeControl.Paused) hud.ToggleSettings();
                tabDownTime = -1f;
            }
            if (InputBridge.Down(KeyId.H)) hud.ShowHelp(!hud.HelpVisible);
            if (InputBridge.Down(KeyId.F1)) hud.ToggleHidden();
            if (InputBridge.Down(KeyId.F3)) { debug.showOverlay = !debug.showOverlay; }
            if (InputBridge.Down(KeyId.Enter) && Sandbox.Net != null && Sandbox.Net.Active) { hud.OpenChat(); return; }
            if (TimeControl.Paused || InputBridge.GameplayBlocked) return;

            if (InputBridge.Down(KeyId.R)) ResetSandbox(InputBridge.Shift);
            if (InputBridge.Down(KeyId.T) && !Sandbox.IsClient)
            {
                TimeControl.SlowMotion = !TimeControl.SlowMotion;
                SandboxEvents.RaiseToast(TimeControl.SlowMotion ? "Slow motion" : "Normal speed", new Color(0.7f, 0.85f, 1f));
                if (Sandbox.Net != null) Sandbox.Net.OnSettingsChanged();
            }
            if (InputBridge.Down(KeyId.G) && !Sandbox.IsClient)
            {
                physics.gravity = (GravityMode)(((int)physics.gravity + 1) % 3);
                SandboxEvents.RaiseToast("Gravity: " + physics.gravity, new Color(0.7f, 0.85f, 1f));
                if (Sandbox.Net != null) Sandbox.Net.OnSettingsChanged();
            }
            if (InputBridge.Down(KeyId.X)) SetXRay(!Sandbox.XRay);
            if (InputBridge.Down(KeyId.V)) TogglePossess();
            if (InputBridge.Down(KeyId.F)) Sandbox.Cam.ToggleFollow();
            if (InputBridge.Down(KeyId.E)) Sandbox.Vehicles.ToggleDrive();
            if (InputBridge.Down(KeyId.N))
            {
                general.wander = !general.wander;
                SandboxEvents.RaiseToast(general.wander ? "Wander on" : "Wander off", Color.white);
            }
            if (InputBridge.Down(KeyId.B)) { if (Sandbox.IsClient) Sandbox.Net.SendToolAction(ToolId.Explosives, 9, false, Sandbox.Cam.CenterRay()); else Sandbox.Explosions.DetonateAllC4(); }
        }

        public void SetPaused(bool paused)
        {
            TimeControl.Paused = paused;
            TimeControl.Apply();
            Sandbox.Hud.ShowPause(paused);
            Sandbox.Audio.SetPaused(paused && !(Sandbox.Net != null && Sandbox.Net.Active));
            CursorControl.SetLocked(!paused && !Sandbox.Hud.SettingsVisible && !Sandbox.InMenu);
        }

        public void SetXRay(bool on)
        {
            Sandbox.XRay = on;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].SetXRay(on);
            SandboxEvents.RaiseToast(on ? "Anatomy X-ray on" : "X-ray off", new Color(0.6f, 0.9f, 1f));
        }

        public void Possess(RagdollBody target)
        {
            if (target == null) return;
            if (Sandbox.Possessed != null && Sandbox.Possessed != target) Sandbox.Possessed.SetPossessed(false);
            Sandbox.Possessed = target;
            target.SetPossessed(true);
            if (Sandbox.Vehicles != null) Sandbox.Vehicles.ExitDriving();
            Sandbox.Cam.SetMode(CameraMode.Possess);
        }

        public void TogglePossess()
        {
            if (Sandbox.IsClient) { if (Sandbox.Net != null) Sandbox.Net.TogglePossessRemote(); return; }
            if (Sandbox.Possessed != null)
            {
                Sandbox.Possessed.SetPossessed(false);
                Sandbox.Possessed = null;
                Sandbox.Cam.SetMode(CameraMode.FirstPerson);
                SandboxEvents.RaiseToast("Back to first person", Color.white);
                return;
            }
            RagdollBody target = Sandbox.Hovered != null && Time.unscaledTime - Sandbox.HoveredTime < 1f ? Sandbox.Hovered : null;
            if (target != null && (target.OwnerPlayer > 0 || !target.Alive)) target = null;
            if (target == null)
            {
                float best = float.MaxValue;
                Vector3 cp = Sandbox.MainCamera.transform.position;
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
                {
                    var r = Sandbox.Ragdolls[i];
                    if (r == null || !r.Alive || r.OwnerPlayer > 0) continue;
                    float d = (r.CenterOfMass - cp).sqrMagnitude;
                    if (d < best) { best = d; target = r; }
                }
            }
            if (target == null) { SandboxEvents.RaiseToast("No living ragdoll to possess", new Color(1f, 0.7f, 0.6f)); return; }
            Possess(target);
            if (Sandbox.Test != null) Sandbox.Test.Abort("player possessed the test subject");
            SandboxEvents.RaiseToast("Possessing ragdoll: WASD move, Space jump, Shift run, C crouch, LMB/RMB arms. V to exit.", new Color(1f, 0.85f, 0.6f));
        }

        void OnDestroy()
        {
            if (Sandbox.Boot != this) return;
            if (Sandbox.Net != null) Sandbox.Net.Shutdown("closed");
            RestoreGlobals();
            for (int i = 0; i < disabledSceneObjects.Count; i++) if (disabledSceneObjects[i] != null) disabledSceneObjects[i].enabled = true;
            for (int i = 0; i < disabledEventSystems.Count; i++) if (disabledEventSystems[i] != null) disabledEventSystems[i].SetActive(true);
            CursorControl.SetLocked(false);
            SandboxEvents.ClearAll();
            Registry.Clear();
            BodyTemplate.ClearCache();
            BodyCanvas.ReleaseStaging();
            InstanceIdExtensions.Clear();
            Sandbox.Reset();
            if (runtimeRoot != null) Destroy(runtimeRoot.gameObject);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!built) return;
            if (focus && !TimeControl.Paused && Sandbox.Hud != null && !Sandbox.Hud.SettingsVisible && !Sandbox.InMenu && !Sandbox.Hud.ChatOpen) CursorControl.SetLocked(true);
        }
    }
    #endregion
}
