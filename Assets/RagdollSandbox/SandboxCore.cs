// Ragdoll Sandbox - core utilities shared by every system:
// Unity version compatibility, input (legacy Input Manager and the Input System package),
// math helpers, physics materials, the collider registry and the global event hub.
using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_6000_0_OR_NEWER
using PhysMat = UnityEngine.PhysicsMaterial;
using PhysCombine = UnityEngine.PhysicsMaterialCombine;
#else
using PhysMat = UnityEngine.PhysicMaterial;
using PhysCombine = UnityEngine.PhysicMaterialCombine;
#endif

namespace RagdollSandbox
{
    #region Compat
    /// <summary>Wraps the few APIs that were renamed in Unity 6 so the project compiles on 2022 LTS and Unity 6.</summary>
    public static class Compat
    {
        public static Vector3 GetVelocity(Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetVelocity(Rigidbody rb, Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = v;
#else
            rb.velocity = v;
#endif
        }

        public static void SetDamping(Rigidbody rb, float linear, float angular)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearDamping = linear;
            rb.angularDamping = angular;
#else
            rb.drag = linear;
            rb.angularDrag = angular;
#endif
        }

        public static float GetLinearDamping(Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearDamping;
#else
            return rb.drag;
#endif
        }

        public static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_2022_2_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<T>();
#endif
        }

        static Font cachedFont;
        public static Font BuiltinFont()
        {
            if (cachedFont != null) return cachedFont;
            try { cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { cachedFont = null; }
            if (cachedFont == null)
            {
                try { cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { cachedFont = null; }
            }
            if (cachedFont == null)
            {
                try { cachedFont = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 16); } catch (Exception) { cachedFont = null; }
            }
            return cachedFont;
        }
    }
    #endregion

    #region Input
    public enum KeyId
    {
        None, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        Space, LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, Escape, Tab, Enter, Backspace,
        Minus, Equals, F1, F2, F3, F4, UpArrow, DownArrow, LeftArrow, RightArrow
    }

    /// <summary>Input facade. Uses the Input System package when it is active (Unity 6 default) and the legacy
    /// Input Manager otherwise, so it works with either "Active Input Handling" setting.</summary>
    public static class InputBridge
    {
        public static float MouseSensitivity = 1f;
        public static bool InvertY;
        /// <summary>Set by the HUD while a text/slider has focus or a menu is open.</summary>
        public static bool GameplayBlocked;

#if !ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
        static int axisState; // 0 unknown, 1 available, 2 missing
        static Vector2 lastMousePos;
        static int lastMouseFrame = -1;
        static Vector2 fallbackDelta;
#endif

        public static bool Held(KeyId k)
        {
            if (k == KeyId.None) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            Key key = ToKey(k);
            if (key == Key.None) return false;
            return kb[key].isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(ToKeyCode(k));
#else
            return false;
#endif
        }

        public static bool Down(KeyId k)
        {
            if (k == KeyId.None) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            Key key = ToKey(k);
            if (key == Key.None) return false;
            return kb[key].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(ToKeyCode(k));
#else
            return false;
#endif
        }

        public static bool Up(KeyId k)
        {
            if (k == KeyId.None) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            Key key = ToKey(k);
            if (key == Key.None) return false;
            return kb[key].wasReleasedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyUp(ToKeyCode(k));
#else
            return false;
#endif
        }

        public static bool Shift { get { return Held(KeyId.LeftShift) || Held(KeyId.RightShift); } }
        public static bool Ctrl { get { return Held(KeyId.LeftCtrl) || Held(KeyId.RightCtrl); } }

        /// <summary>0 = left, 1 = right, 2 = middle.</summary>
        public static bool Mouse(int b)
        {
#if ENABLE_INPUT_SYSTEM
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return false;
            if (b == 0) return m.leftButton.isPressed;
            if (b == 1) return m.rightButton.isPressed;
            return m.middleButton.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(b);
#else
            return false;
#endif
        }

        public static bool MouseDown(int b)
        {
#if ENABLE_INPUT_SYSTEM
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return false;
            if (b == 0) return m.leftButton.wasPressedThisFrame;
            if (b == 1) return m.rightButton.wasPressedThisFrame;
            return m.middleButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(b);
#else
            return false;
#endif
        }

        public static bool MouseUp(int b)
        {
#if ENABLE_INPUT_SYSTEM
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return false;
            if (b == 0) return m.leftButton.wasReleasedThisFrame;
            if (b == 1) return m.rightButton.wasReleasedThisFrame;
            return m.middleButton.wasReleasedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonUp(b);
#else
            return false;
#endif
        }

        /// <summary>Mouse movement this frame in "legacy axis" units (about 0.1 per pixel), scaled by sensitivity.</summary>
        public static Vector2 MouseDelta
        {
            get
            {
                Vector2 d = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null) d = m.delta.ReadValue() * 0.1f;
#elif ENABLE_LEGACY_INPUT_MANAGER
                if (axisState == 0)
                {
                    try { Input.GetAxisRaw("Mouse X"); axisState = 1; }
                    catch (Exception) { axisState = 2; }
                }
                if (axisState == 1)
                {
                    d = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
                }
                else
                {
                    if (lastMouseFrame != Time.frameCount)
                    {
                        Vector2 p = Input.mousePosition;
                        fallbackDelta = lastMouseFrame < 0 ? Vector2.zero : (p - lastMousePos) * 0.1f;
                        lastMousePos = p;
                        lastMouseFrame = Time.frameCount;
                    }
                    d = fallbackDelta;
                }
#endif
                d *= MouseSensitivity;
                if (InvertY) d.y = -d.y;
                return d;
            }
        }

        /// <summary>Scroll wheel in notches (+1 = one notch up).</summary>
        public static float Scroll
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null) return 0f;
                float y = m.scroll.ReadValue().y;
                if (Mathf.Abs(y) > 10f) y /= 120f;
                return Mathf.Clamp(y, -3f, 3f);
#elif ENABLE_LEGACY_INPUT_MANAGER
                return Mathf.Clamp(Input.mouseScrollDelta.y, -3f, 3f);
#else
                return 0f;
#endif
            }
        }

        public static Vector2 MousePosition
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                var m = UnityEngine.InputSystem.Mouse.current;
                return m != null ? m.position.ReadValue() : Vector2.zero;
#elif ENABLE_LEGACY_INPUT_MANAGER
                return Input.mousePosition;
#else
                return Vector2.zero;
#endif
            }
        }

        public static int DigitDown()
        {
            if (Down(KeyId.Alpha1)) return 1;
            if (Down(KeyId.Alpha2)) return 2;
            if (Down(KeyId.Alpha3)) return 3;
            if (Down(KeyId.Alpha4)) return 4;
            if (Down(KeyId.Alpha5)) return 5;
            if (Down(KeyId.Alpha6)) return 6;
            if (Down(KeyId.Alpha7)) return 7;
            if (Down(KeyId.Alpha8)) return 8;
            if (Down(KeyId.Alpha9)) return 9;
            if (Down(KeyId.Alpha0)) return 10;
            return -1;
        }

#if ENABLE_INPUT_SYSTEM
        static Key ToKey(KeyId k)
        {
            switch (k)
            {
                case KeyId.A: return Key.A; case KeyId.B: return Key.B; case KeyId.C: return Key.C; case KeyId.D: return Key.D;
                case KeyId.E: return Key.E; case KeyId.F: return Key.F; case KeyId.G: return Key.G; case KeyId.H: return Key.H;
                case KeyId.I: return Key.I; case KeyId.J: return Key.J; case KeyId.K: return Key.K; case KeyId.L: return Key.L;
                case KeyId.M: return Key.M; case KeyId.N: return Key.N; case KeyId.O: return Key.O; case KeyId.P: return Key.P;
                case KeyId.Q: return Key.Q; case KeyId.R: return Key.R; case KeyId.S: return Key.S; case KeyId.T: return Key.T;
                case KeyId.U: return Key.U; case KeyId.V: return Key.V; case KeyId.W: return Key.W; case KeyId.X: return Key.X;
                case KeyId.Y: return Key.Y; case KeyId.Z: return Key.Z;
                case KeyId.Alpha0: return Key.Digit0; case KeyId.Alpha1: return Key.Digit1; case KeyId.Alpha2: return Key.Digit2;
                case KeyId.Alpha3: return Key.Digit3; case KeyId.Alpha4: return Key.Digit4; case KeyId.Alpha5: return Key.Digit5;
                case KeyId.Alpha6: return Key.Digit6; case KeyId.Alpha7: return Key.Digit7; case KeyId.Alpha8: return Key.Digit8;
                case KeyId.Alpha9: return Key.Digit9;
                case KeyId.Space: return Key.Space; case KeyId.LeftShift: return Key.LeftShift; case KeyId.RightShift: return Key.RightShift;
                case KeyId.LeftCtrl: return Key.LeftCtrl; case KeyId.RightCtrl: return Key.RightCtrl; case KeyId.LeftAlt: return Key.LeftAlt;
                case KeyId.Escape: return Key.Escape; case KeyId.Tab: return Key.Tab; case KeyId.Enter: return Key.Enter;
                case KeyId.Backspace: return Key.Backspace; case KeyId.Minus: return Key.Minus; case KeyId.Equals: return Key.Equals;
                case KeyId.F1: return Key.F1; case KeyId.F2: return Key.F2; case KeyId.F3: return Key.F3; case KeyId.F4: return Key.F4;
                case KeyId.UpArrow: return Key.UpArrow; case KeyId.DownArrow: return Key.DownArrow;
                case KeyId.LeftArrow: return Key.LeftArrow; case KeyId.RightArrow: return Key.RightArrow;
            }
            return Key.None;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        static KeyCode ToKeyCode(KeyId k)
        {
            switch (k)
            {
                case KeyId.A: return KeyCode.A; case KeyId.B: return KeyCode.B; case KeyId.C: return KeyCode.C; case KeyId.D: return KeyCode.D;
                case KeyId.E: return KeyCode.E; case KeyId.F: return KeyCode.F; case KeyId.G: return KeyCode.G; case KeyId.H: return KeyCode.H;
                case KeyId.I: return KeyCode.I; case KeyId.J: return KeyCode.J; case KeyId.K: return KeyCode.K; case KeyId.L: return KeyCode.L;
                case KeyId.M: return KeyCode.M; case KeyId.N: return KeyCode.N; case KeyId.O: return KeyCode.O; case KeyId.P: return KeyCode.P;
                case KeyId.Q: return KeyCode.Q; case KeyId.R: return KeyCode.R; case KeyId.S: return KeyCode.S; case KeyId.T: return KeyCode.T;
                case KeyId.U: return KeyCode.U; case KeyId.V: return KeyCode.V; case KeyId.W: return KeyCode.W; case KeyId.X: return KeyCode.X;
                case KeyId.Y: return KeyCode.Y; case KeyId.Z: return KeyCode.Z;
                case KeyId.Alpha0: return KeyCode.Alpha0; case KeyId.Alpha1: return KeyCode.Alpha1; case KeyId.Alpha2: return KeyCode.Alpha2;
                case KeyId.Alpha3: return KeyCode.Alpha3; case KeyId.Alpha4: return KeyCode.Alpha4; case KeyId.Alpha5: return KeyCode.Alpha5;
                case KeyId.Alpha6: return KeyCode.Alpha6; case KeyId.Alpha7: return KeyCode.Alpha7; case KeyId.Alpha8: return KeyCode.Alpha8;
                case KeyId.Alpha9: return KeyCode.Alpha9;
                case KeyId.Space: return KeyCode.Space; case KeyId.LeftShift: return KeyCode.LeftShift; case KeyId.RightShift: return KeyCode.RightShift;
                case KeyId.LeftCtrl: return KeyCode.LeftControl; case KeyId.RightCtrl: return KeyCode.RightControl; case KeyId.LeftAlt: return KeyCode.LeftAlt;
                case KeyId.Escape: return KeyCode.Escape; case KeyId.Tab: return KeyCode.Tab; case KeyId.Enter: return KeyCode.Return;
                case KeyId.Backspace: return KeyCode.Backspace; case KeyId.Minus: return KeyCode.Minus; case KeyId.Equals: return KeyCode.Equals;
                case KeyId.F1: return KeyCode.F1; case KeyId.F2: return KeyCode.F2; case KeyId.F3: return KeyCode.F3; case KeyId.F4: return KeyCode.F4;
                case KeyId.UpArrow: return KeyCode.UpArrow; case KeyId.DownArrow: return KeyCode.DownArrow;
                case KeyId.LeftArrow: return KeyCode.LeftArrow; case KeyId.RightArrow: return KeyCode.RightArrow;
            }
            return KeyCode.None;
        }
#endif
    }

    public static class CursorControl
    {
        public static bool Locked { get; private set; }
        public static void SetLocked(bool locked)
        {
            Locked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
    #endregion

    #region Math
    public static class MathUtil
    {
        public const float Deg2Rad = Mathf.Deg2Rad;

        public static float Damp(float current, float target, float lambda, float dt)
        {
            return Mathf.Lerp(current, target, 1f - Mathf.Exp(-lambda * dt));
        }

        public static Vector3 Damp(Vector3 current, Vector3 target, float lambda, float dt)
        {
            return Vector3.Lerp(current, target, 1f - Mathf.Exp(-lambda * dt));
        }

        public static Quaternion Damp(Quaternion current, Quaternion target, float lambda, float dt)
        {
            return Quaternion.Slerp(current, target, 1f - Mathf.Exp(-lambda * dt));
        }

        public static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        public static Vector3 SafeNormal(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            return m > 1e-6f ? v / m : fallback;
        }

        public static bool IsFinite(float f) { return !(float.IsNaN(f) || float.IsInfinity(f)); }
        public static bool IsFinite(Vector3 v) { return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z); }
        public static bool IsFinite(Quaternion q) { return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w); }

        /// <summary>Rotation needed to go from 'from' to 'to', as an axis * angle(rad) vector (shortest path).</summary>
        public static Vector3 RotationError(Quaternion from, Quaternion to)
        {
            Quaternion d = to * Quaternion.Inverse(from);
            if (d.w < 0f) { d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w; }
            float sinHalf = Mathf.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            if (sinHalf < 1e-6f) return Vector3.zero;
            float angle = 2f * Mathf.Atan2(sinHalf, d.w);
            return new Vector3(d.x, d.y, d.z) * (angle / sinHalf);
        }

        public static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
        }

        public static float SmoothStep01(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        public static float Remap(float v, float a0, float a1, float b0, float b1)
        {
            if (Mathf.Abs(a1 - a0) < 1e-6f) return b0;
            return Mathf.Lerp(b0, b1, Mathf.Clamp01((v - a0) / (a1 - a0)));
        }

        public static float Gauss(float x, float sigma) { return Mathf.Exp(-(x * x) / (2f * sigma * sigma)); }

        public static float WrapAngle(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            if (a < -180f) a += 360f;
            return a;
        }

        public static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16;
            return x;
        }

        public static float Hash01(int x, int y, int seed)
        {
            uint h = Hash((uint)x * 73856093U ^ (uint)y * 19349663U ^ (uint)seed * 83492791U);
            return (h & 0xFFFFFF) / 16777215f;
        }

        public static Quaternion YawRotation(float yawDeg) { return Quaternion.Euler(0f, yawDeg, 0f); }

        public static float Yaw(Vector3 forward)
        {
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        public static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-9f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        public static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            return (ClosestPointOnSegment(a, b, p) - p).magnitude;
        }

        public static Color Lerp3(Color a, Color b, Color c, float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? Color.Lerp(a, b, t * 2f) : Color.Lerp(b, c, (t - 0.5f) * 2f);
        }
    }

    /// <summary>Small deterministic random generator (xorshift) used by procedural generation.</summary>
    public class Rng
    {
        uint state;
        public Rng(int seed) { state = (uint)seed * 747796405U + 2891336453U; if (state == 0) state = 1; }
        public uint NextUInt() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        public float Value { get { return (NextUInt() & 0xFFFFFF) / 16777216f; } }
        public float Range(float a, float b) { return a + (b - a) * Value; }
        public int Range(int a, int bExclusive) { if (bExclusive <= a) return a; return a + (int)(NextUInt() % (uint)(bExclusive - a)); }
        public Vector3 OnUnitSphere()
        {
            float z = Range(-1f, 1f);
            float a = Range(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(1f - z * z);
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }
        public Vector2 InsideUnitCircle()
        {
            float a = Range(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(Value);
            return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        public bool Chance(float p) { return Value < p; }
    }
    #endregion

    #region PhysicsMaterials
    /// <summary>Shared physics materials (created once at boot).</summary>
    public static class PhysMats
    {
        public static PhysMat Body, Foot, Hand, Torso, Organ, Concrete, Grass, Wood, Metal, Rubber, Ice, Bouncy, Ball, Stone, Frictionless, Player;

        public static void Build()
        {
            Body = Make("Body", 0.6f, 0.7f, 0.02f, PhysCombine.Average, PhysCombine.Minimum);
            Torso = Make("Torso", 0.45f, 0.55f, 0.01f, PhysCombine.Average, PhysCombine.Minimum);
            Foot = Make("Foot", 1.1f, 1.35f, 0f, PhysCombine.Maximum, PhysCombine.Minimum);
            Hand = Make("Hand", 0.9f, 1.0f, 0f, PhysCombine.Maximum, PhysCombine.Minimum);
            Organ = Make("Organ", 0.8f, 0.9f, 0.05f, PhysCombine.Average, PhysCombine.Minimum);
            Concrete = Make("Concrete", 0.75f, 0.85f, 0.02f, PhysCombine.Average, PhysCombine.Average);
            Grass = Make("Grass", 0.7f, 0.8f, 0.01f, PhysCombine.Average, PhysCombine.Average);
            Wood = Make("Wood", 0.55f, 0.65f, 0.08f, PhysCombine.Average, PhysCombine.Average);
            Metal = Make("Metal", 0.4f, 0.5f, 0.1f, PhysCombine.Average, PhysCombine.Average);
            Rubber = Make("Rubber", 0.9f, 1.0f, 0.45f, PhysCombine.Average, PhysCombine.Maximum);
            Ice = Make("Ice", 0.03f, 0.05f, 0f, PhysCombine.Minimum, PhysCombine.Average);
            Bouncy = Make("Bouncy", 0.6f, 0.6f, 0.9f, PhysCombine.Average, PhysCombine.Maximum);
            Ball = Make("Ball", 0.6f, 0.7f, 0.65f, PhysCombine.Average, PhysCombine.Maximum);
            Stone = Make("Stone", 0.7f, 0.8f, 0.05f, PhysCombine.Average, PhysCombine.Average);
            Frictionless = Make("Frictionless", 0f, 0f, 0f, PhysCombine.Minimum, PhysCombine.Minimum);
            Player = Make("Player", 0f, 0f, 0f, PhysCombine.Minimum, PhysCombine.Minimum);
        }

        static PhysMat Make(string name, float dyn, float stat, float bounce, PhysCombine fric, PhysCombine bounceCombine)
        {
            var m = new PhysMat(name);
            m.dynamicFriction = dyn;
            m.staticFriction = stat;
            m.bounciness = bounce;
            m.frictionCombine = fric;
            m.bounceCombine = bounceCombine;
            return m;
        }

        public static void Apply(Collider c, string kind)
        {
            if (c == null) return;
            switch (kind)
            {
                case "concrete": c.sharedMaterial = Concrete; break;
                case "grass": c.sharedMaterial = Grass; break;
                case "wood": c.sharedMaterial = Wood; break;
                case "metal": c.sharedMaterial = Metal; break;
                case "rubber": c.sharedMaterial = Rubber; break;
                case "ice": c.sharedMaterial = Ice; break;
                case "bouncy": c.sharedMaterial = Bouncy; break;
                case "ball": c.sharedMaterial = Ball; break;
                case "stone": c.sharedMaterial = Stone; break;
                default: c.sharedMaterial = Concrete; break;
            }
        }
    }
    #endregion

    #region Layers
    /// <summary>Unnamed user layers used purely from code (no project settings needed). The collision matrix entries
    /// touched here are saved at boot and restored when the sandbox is destroyed.</summary>
    public static class Layers
    {
        public const int Ragdoll = 25, Debris = 26, Gibs = 27, Player = 28, Vehicle = 29;
        public static int GroundMask, GrabMask, BloodMask, ToolMask;
        static bool[,] saved;
        static readonly int[] used = { Ragdoll, Debris, Gibs, Player, Vehicle };

        public static void Setup()
        {
            saved = new bool[32, 32];
            for (int a = 0; a < 32; a++)
                for (int b = 0; b < 32; b++) saved[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            for (int i = 0; i < used.Length; i++)
                for (int j = 0; j < 32; j++) Physics.IgnoreLayerCollision(used[i], j, false);
            // pairs that never matter: debris vs debris / player / ragdolls, gibs vs player
            Physics.IgnoreLayerCollision(Debris, Debris, true);
            Physics.IgnoreLayerCollision(Debris, Player, true);
            Physics.IgnoreLayerCollision(Debris, Ragdoll, true);
            Physics.IgnoreLayerCollision(Gibs, Player, true);
            Physics.IgnoreLayerCollision(Gibs, Debris, true);
            GroundMask = ~((1 << Debris) | (1 << Player) | (1 << 2));
            GrabMask = ~((1 << Debris) | (1 << Player) | (1 << Gibs) | (1 << 2));
            BloodMask = ~((1 << Debris) | (1 << Player) | (1 << 2));
            ToolMask = ~((1 << Debris) | (1 << 2));
        }

        public static void Restore()
        {
            if (saved == null) return;
            for (int a = 0; a < 32; a++)
                for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, saved[a, b]);
            saved = null;
        }

        public static void SetRecursive(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++) SetRecursive(t.GetChild(i).gameObject, layer);
        }
    }
    #endregion

    #region Registry and events
    public enum DamageType { Blunt, Cut, Pierce, Blast, Crush, Shock, Fall, Burn }

    public struct DamageInfo
    {
        public DamageType type;
        public float amount;
        public Vector3 point;
        public Vector3 direction;
        public Vector3 normal;
        public float impulse;
        public bool hasExit;
        public Vector3 exitPoint;
        public float depth;
        public float radius;
        public bool fromPlayer;
        public Vector3 along;      // cut direction (blades, lasers)
        public bool crater;        // point-blank shotgun: one large crater
        public int attacker;       // network player id (-1 = local / world)

        public static DamageInfo Make(DamageType t, float amount, Vector3 point, Vector3 dir)
        {
            var d = new DamageInfo();
            d.type = t;
            d.amount = amount;
            d.point = point;
            d.direction = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.down;
            d.normal = -d.direction;
            d.radius = 0.05f;
            d.depth = 0.03f;
            d.attacker = -1;
            return d;
        }
    }

    /// <summary>Anything that tools, bullets and explosions can hurt.</summary>
    public interface IDamageable
    {
        void ApplyDamage(DamageInfo info);
    }

    /// <summary>Surfaces that carry the player along (moving platforms, conveyors).</summary>
    public interface IMovingSurface
    {
        Vector3 GetSurfaceVelocity(Vector3 worldPoint);
    }

    /// <summary>Allocation-free lookups from colliders to gameplay components (avoids GetComponent in hot loops).</summary>
    public static class Registry
    {
        public static readonly Dictionary<Collider, BodyPart> Parts = new Dictionary<Collider, BodyPart>(256);
        public static readonly Dictionary<Rigidbody, BodyPart> PartsByBody = new Dictionary<Rigidbody, BodyPart>(256);
        public static readonly Dictionary<Collider, IDamageable> Damageables = new Dictionary<Collider, IDamageable>(512);
        public static readonly Dictionary<Collider, IMovingSurface> Movers = new Dictionary<Collider, IMovingSurface>(32);
        public static readonly Dictionary<Collider, Organ> Organs = new Dictionary<Collider, Organ>(128);
        public static readonly Dictionary<Rigidbody, Vehicle> Vehicles = new Dictionary<Rigidbody, Vehicle>(8);

        public static void Clear()
        {
            Parts.Clear(); PartsByBody.Clear(); Damageables.Clear(); Movers.Clear(); Organs.Clear(); Vehicles.Clear();
        }

        public static BodyPart PartOf(Collider c)
        {
            if (c == null) return null;
            BodyPart p;
            if (Parts.TryGetValue(c, out p) && p != null) return p;
            return null;
        }

        public static BodyPart PartOf(Rigidbody rb)
        {
            if (rb == null) return null;
            BodyPart p;
            if (PartsByBody.TryGetValue(rb, out p) && p != null) return p;
            return null;
        }

        public static IDamageable DamageableOf(Collider c)
        {
            if (c == null) return null;
            IDamageable d;
            if (Damageables.TryGetValue(c, out d)) return d;
            return null;
        }

        public static void RegisterDamageable(Collider c, IDamageable d)
        {
            if (c != null) Damageables[c] = d;
        }

        public static void Unregister(Collider c)
        {
            if (c == null) return;
            Parts.Remove(c); Damageables.Remove(c); Movers.Remove(c); Organs.Remove(c);
        }
    }

    /// <summary>Global event hub. Systems raise events here; HUD, audio and statistics listen.</summary>
    public static class SandboxEvents
    {
        public static event Action<RagdollBody, BodyPart, DamageInfo> Damaged;
        public static event Action<RagdollBody, PartId, BodyPart> Severed;
        public static event Action<RagdollBody, Wound> BleedStart;
        public static event Action<RagdollBody> Death;
        public static event Action<RagdollBody> Unconscious;
        public static event Action<RagdollBody> Woke;
        public static event Action<RagdollBody, PartId> Fractured;
        public static event Action<RagdollBody, OrganType> OrganDestroyed;
        public static event Action<Vector3, float> Explosion;
        public static event Action<string, Color> Toast;
        /// <summary>Kill-feed style event: subject, what happened.</summary>
        public static event Action<string, string, Color> Kill;

        public static void RaiseDamaged(RagdollBody b, BodyPart p, DamageInfo d) { var h = Damaged; if (h != null) h(b, p, d); }
        public static void RaiseSevered(RagdollBody b, PartId id, BodyPart piece) { var h = Severed; if (h != null) h(b, id, piece); }
        public static void RaiseBleedStart(RagdollBody b, Wound w) { var h = BleedStart; if (h != null) h(b, w); }
        public static void RaiseDeath(RagdollBody b) { var h = Death; if (h != null) h(b); }
        public static void RaiseUnconscious(RagdollBody b) { var h = Unconscious; if (h != null) h(b); }
        public static void RaiseWoke(RagdollBody b) { var h = Woke; if (h != null) h(b); }
        public static void RaiseFractured(RagdollBody b, PartId id) { var h = Fractured; if (h != null) h(b, id); }
        public static void RaiseOrganDestroyed(RagdollBody b, OrganType o) { var h = OrganDestroyed; if (h != null) h(b, o); }
        public static void RaiseExplosion(Vector3 p, float r) { var h = Explosion; if (h != null) h(p, r); }
        public static void RaiseToast(string text, Color c) { var h = Toast; if (h != null) h(text, c); }
        /// <summary>Set while the network delivers the host's kill-feed entries (clients ignore their own replays).</summary>
        public static bool NetDelivery;
        public static void RaiseKill(string who, string what, Color c)
        {
            if (Sandbox.IsClient && !NetDelivery) return;
            var h = Kill; if (h != null) h(who, what, c);
        }

        public static void ClearAll()
        {
            Damaged = null; Severed = null; BleedStart = null; Death = null; Unconscious = null; Woke = null;
            Fractured = null; OrganDestroyed = null; Explosion = null; Toast = null; Kill = null;
        }
    }
    #endregion
}
