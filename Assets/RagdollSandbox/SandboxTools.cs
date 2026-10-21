// Ragdoll Sandbox - ToolSystem (hotbar 1-9, 0, -), per-user tool state (the local player, and on a host every
// remote player too), GrabController (physics spring to the exact hit point, throwing, strong pull that stretches
// the skin, pinning), rope tool, hammer, blade plane cuts, laser (burns, sweeps cut), pistol/shotgun hitscan with
// entry/exit holes and point-blank craters, explosives, prop and car spawner, shock/defibrillator, heal/revive,
// ragdoll spawning, Ctrl+click walk orders, aim lines and simple procedural first-person view models.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    #region ToolSystem
    public enum ToolId { Grab, Hammer, Blade, Pistol, Shotgun, Explosives, Props, Shock, Heal, Spawn, Laser }

    /// <summary>One frame of tool input. The local player fills it from the keyboard/mouse; remote players' inputs
    /// arrive over the network (button presses as counters, so a dropped packet never loses a click).</summary>
    public struct ToolInput
    {
        public Ray aim;
        public Vector3 right, up;
        public bool held0, held1, down0, down1, up0, up1, shift, ctrl, blocked;
        public float scroll;
    }

    public class ToolSystem
    {
        public const int ToolCount = 11;
        public static readonly string[] ToolNames = { "Grab", "Hammer", "Blade", "Pistol", "Shotgun", "Explosives", "Props", "Shock", "Heal", "Spawn Ragdoll", "Laser" };
        public static readonly string[] ToolKeys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-" };
        public static readonly string[] ToolHelp =
        {
            "LMB drag: grab with a physics spring. Wheel: distance. Shift: strong pull (stretches skin, rips limbs). RMB: pin. Q: rope mode.",
            "LMB: punch. RMB: heavy smash (fractures).",
            "Hold LMB and drag across a limb: the drag defines the cut plane. Fast drags cut through. RMB: stab.",
            "LMB: fire. Bullets punch real entry and exit holes and can pass through limbs.",
            "LMB: fire a spread of pellets. Point blank: one large crater.",
            "LMB: throw/place. RMB or B: detonate C4. Q: cycle grenade/TNT/C4/barrel/landmine/mega.",
            "LMB: spawn at the crosshair. RMB: throw (cars drive off). Q: cycle crate/ball/barrel/anvil/plank/boulder/clay car.",
            "Hold LMB on a body: electrocute (spasms). RMB on the chest: defibrillate.",
            "LMB: heal a part (wounds, fractures, revive if recently dead). RMB: fully restore that ragdoll.",
            "LMB: spawn a ragdoll at the crosshair. RMB: remove the targeted ragdoll.",
            "Hold LMB: the beam burns. Sweep it quickly across a limb to cut it at any angle."
        };
        public static readonly string[] ExplosiveNames = { "Grenade", "TNT", "C4", "Explosive barrel", "Landmine", "Mega blast" };
        public static readonly string[] PropNames = { "Crate", "Ball", "Steel barrel", "Anvil", "Plank", "Boulder", "Clay car" };
        public const int DetonateSub = 9;

        public ToolUser Local;
        public readonly ToolUser[] Remote = new ToolUser[8];
        ViewModels view;
        Transform root;
        readonly List<GameObject> spawned = new List<GameObject>(128);
        readonly RaycastHit[] hits = new RaycastHit[32];
        LineRenderer aimLine, trailLine;
        readonly Vector3[] trail = new Vector3[24];
        int trailCount;
        BodyPart highlighted;

        public ToolId Current { get { return Local.Current; } }
        public GrabController Grab { get { return Local.Grab; } }
        public ViewModels View { get { return view; } }
        public string SubName { get { return Local.SubName; } }

        public void Build(Transform parent)
        {
            root = new GameObject("Tools").transform;
            root.SetParent(parent, false);
            Local = new ToolUser(this, root, -1, true);
            view = new ViewModels();
            view.Build(Sandbox.MainCamera.transform);
            view.Show(Local.Current);
            aimLine = MakeLine("AimLine", 0.004f, 0.002f, MaterialFactory.LineAdd);
            aimLine.positionCount = 2;
            trailLine = MakeLine("BladeTrail", 0.006f, 0.006f, MaterialFactory.LineAdd);
        }

        LineRenderer MakeLine(string name, float w0, float w1, Material m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = m;
            lr.startWidth = w0; lr.endWidth = w1;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        public bool Blocked
        {
            get
            {
                if (TimeControl.Paused || InputBridge.GameplayBlocked || Sandbox.InMenu) return true;
                if (!CursorControl.Locked) return true;
                return false;
            }
        }

        public Ray AimRay { get { return Sandbox.Cam != null ? Sandbox.Cam.CenterRay() : new Ray(Vector3.zero, Vector3.forward); } }

        /// <summary>The remote player's tool user on the host (created on demand).</summary>
        public ToolUser RemoteUser(int player)
        {
            if (player < 0 || player >= Remote.Length) return null;
            if (Remote[player] == null) Remote[player] = new ToolUser(this, root, player, false);
            return Remote[player];
        }

        public void RemoveRemote(int player)
        {
            if (player < 0 || player >= Remote.Length || Remote[player] == null) return;
            Remote[player].Dispose();
            Remote[player] = null;
        }

        public void Select(ToolId t) { Local.Select(t); if (view != null) view.Show(Local.Current); }

        public void Tick()
        {
            var lu = Local;
            bool possess = Sandbox.Possessed != null;
            bool driving = Sandbox.Vehicles != null && Sandbox.Vehicles.Driving != null;
            bool blocked = Blocked || possess || driving;
            // local input
            var inp = new ToolInput();
            Ray ray = AimRay;
            inp.aim = ray;
            var ct = Sandbox.MainCamera.transform;
            inp.right = ct.right; inp.up = ct.up;
            inp.blocked = blocked;
            if (!blocked)
            {
                inp.held0 = InputBridge.Mouse(0); inp.held1 = InputBridge.Mouse(1);
                inp.down0 = InputBridge.MouseDown(0); inp.down1 = InputBridge.MouseDown(1);
                inp.up0 = InputBridge.MouseUp(0); inp.up1 = InputBridge.MouseUp(1);
                inp.shift = InputBridge.Shift; inp.ctrl = InputBridge.Ctrl;
                inp.scroll = InputBridge.Scroll;
                int digit = InputBridge.DigitDown();
                if (digit > 0) Select((ToolId)(digit - 1));
                if (InputBridge.Down(KeyId.Minus)) Select(ToolId.Laser);
                if (InputBridge.Down(KeyId.Q)) { lu.CycleSub(); view.Show(lu.Current); }
                if (Mathf.Abs(inp.scroll) > 0.01f && !lu.Grab.Active && !inp.ctrl)
                {
                    int next = ((int)lu.Current + (inp.scroll < 0f ? 1 : -1) + ToolCount) % ToolCount;
                    Select((ToolId)next);
                }
            }
            else
            {
                // released while blocked: keep the release edge so grabs and drags end cleanly
                inp.up0 = true; inp.up1 = true;
            }
            lu.Input = inp;
            bool execute = !Sandbox.IsClient;
            if (!execute && !blocked)
            {
                // a client only sends input; give the view model some immediate feedback
                if (inp.down0 && (lu.Current == ToolId.Pistol || lu.Current == ToolId.Shotgun)) view.Recoil(lu.Current == ToolId.Shotgun ? 1.4f : 0.6f);
                else if ((inp.down0 || inp.down1) && lu.Current == ToolId.Hammer) view.Swing(inp.down1 ? 1.3f : 0.8f);
                else if (inp.down0 && (lu.Current == ToolId.Explosives || lu.Current == ToolId.Props)) view.Throw();
            }
            lu.Tick(execute);
            if (Sandbox.IsHost)
                for (int i = 0; i < Remote.Length; i++) if (Remote[i] != null) Remote[i].Tick(true);
            UpdateHighlight();
            if (Sandbox.Hud != null) Sandbox.Hud.SetTool((int)lu.Current, lu.SubName);
        }

        public void FixedTick(float dt)
        {
            if (Sandbox.IsClient) return;
            Local.FixedTick(dt);
            for (int i = 0; i < Remote.Length; i++) if (Remote[i] != null) Remote[i].FixedTick(dt);
        }

        public void LateTick()
        {
            Local.LateTick();
            for (int i = 0; i < Remote.Length; i++) if (Remote[i] != null) Remote[i].LateTick();
            if (view != null) view.LateTick(Local.Current, Local.Grab.Active, Local.Current == ToolId.Blade && Local.Input.held0);
            UpdateAimLines();
        }

        void UpdateHighlight()
        {
            var lu = Local;
            BodyPart show = lu.Current == ToolId.Grab && lu.Grab.Active ? lu.Grab.Part : lu.HoverPart;
            if (show != highlighted)
            {
                if (highlighted != null && highlighted.body != null && highlighted.body.Skin != null) highlighted.body.Skin.SetHighlight(0f);
                highlighted = show;
                if (highlighted != null && highlighted.body != null && highlighted.body.Skin != null) highlighted.body.Skin.SetHighlight(1f);
            }
            if (lu.HoverPart != null && lu.HoverPart.body != null) { Sandbox.Hovered = lu.HoverPart.body; Sandbox.HoveredTime = Time.unscaledTime; }
            if (Sandbox.Hud != null) lu.UpdateHoverLabel(show);
        }

        /// <summary>Blade: a thin line to the hover point (green on a body within reach) and the drag trail that
        /// defines the cut plane. Laser: a red pointer line while not firing.</summary>
        void UpdateAimLines()
        {
            var lu = Local;
            bool fp = Sandbox.Cam != null && Sandbox.Cam.Mode == CameraMode.FirstPerson && !lu.Input.blocked;
            bool blade = fp && lu.Current == ToolId.Blade;
            bool laser = fp && lu.Current == ToolId.Laser && !lu.LaserFiring;
            if ((blade || laser) && lu.HasHover)
            {
                aimLine.enabled = true;
                Vector3 a = view.Muzzle;
                Vector3 b = lu.Hover.point;
                bool reach = blade ? lu.Hover.distance < ToolUser.BladeReach : lu.Hover.distance < Sandbox.Boot.tools.laserRange;
                Color c = laser ? new Color(1f, 0.15f, 0.1f, 0.5f) : reach && lu.HoverPart != null ? new Color(0.4f, 1f, 0.5f, 0.75f) : new Color(1f, 1f, 1f, 0.25f);
                if (blade && !reach) b = a + (b - a).normalized * ToolUser.BladeReach;
                aimLine.startColor = c; aimLine.endColor = new Color(c.r, c.g, c.b, c.a * 0.6f);
                aimLine.SetPosition(0, a);
                aimLine.SetPosition(1, b);
            }
            else if (aimLine.enabled) aimLine.enabled = false;
            // blade drag trail (also on clients, from the local hover)
            if (blade && lu.Input.held0 && lu.HasHover && lu.Hover.distance < ToolUser.BladeReach && lu.HoverPart != null)
            {
                if (trailCount == 0 || (trail[trailCount - 1] - lu.Hover.point).sqrMagnitude > 0.0004f)
                {
                    if (trailCount == trail.Length) { Array.Copy(trail, 1, trail, 0, trail.Length - 1); trailCount--; }
                    trail[trailCount++] = lu.Hover.point;
                }
            }
            else if (!lu.Input.held0) trailCount = 0;
            if (trailCount >= 2)
            {
                trailLine.enabled = true;
                trailLine.positionCount = trailCount;
                for (int i = 0; i < trailCount; i++) trailLine.SetPosition(i, trail[i]);
                trailLine.startColor = new Color(1f, 0.35f, 0.3f, 0.3f);
                trailLine.endColor = new Color(1f, 0.9f, 0.85f, 0.9f);
            }
            else if (trailLine.enabled) trailLine.enabled = false;
        }

        /// <summary>Nearest non-trigger hit that isn't the player's own controller.</summary>
        public bool RaycastFirst(Ray ray, float range, out RaycastHit best)
        {
            best = new RaycastHit();
            int n = Physics.RaycastNonAlloc(ray, hits, range, Layers.ToolMask, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue;
            bool found = false;
            Collider self = Sandbox.Player != null ? Sandbox.Player.Collider : null;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].collider == self) continue;
                if (hits[i].distance < bd) { bd = hits[i].distance; best = hits[i]; found = true; }
            }
            return found;
        }

        public RaycastHit[] HitBuffer { get { return hits; } }

        public void TrackSpawned(GameObject go)
        {
            if (go == null) return;
            spawned.Add(go);
            int cap = Mathf.Min(Sandbox.Boot.tools.propCap, Sandbox.Boot.physics.maxActiveProps);
            while (spawned.Count > cap) { if (spawned[0] != null) UnityEngine.Object.Destroy(spawned[0]); spawned.RemoveAt(0); }
        }

        public void ReleaseFrom(RagdollBody b)
        {
            Local.ReleaseFrom(b);
            for (int i = 0; i < Remote.Length; i++) if (Remote[i] != null) Remote[i].ReleaseFrom(b);
            if (highlighted != null && highlighted.body == b) highlighted = null;
        }

        public void ReleaseAll()
        {
            Local.ReleaseAll();
            for (int i = 0; i < Remote.Length; i++) if (Remote[i] != null) Remote[i].ReleaseAll();
            highlighted = null;
        }

        public void ClearSpawned()
        {
            for (int i = 0; i < spawned.Count; i++) if (spawned[i] != null) UnityEngine.Object.Destroy(spawned[i]);
            spawned.Clear();
        }

        /// <summary>A discrete action requested by a network client (B = detonate C4).</summary>
        public void RemoteAction(int player, ToolId tool, int sub, bool secondary, Ray ray)
        {
            if (tool == ToolId.Explosives && sub == DetonateSub) Sandbox.Explosions.DetonateAllC4();
        }
    }

    /// <summary>The tool state of one user: the local player, or (on the host) one remote player.</summary>
    public class ToolUser
    {
        public const float BladeReach = 2.8f;
        readonly ToolSystem sys;
        public readonly int Player;      // -1 local
        public readonly bool IsLocal;
        public ToolId Current = ToolId.Grab;
        public int ExplosiveIndex, PropIndex, GrabMode;
        public ToolInput Input;
        public GrabController Grab;
        public RopeTool Rope;
        float cooldown, cooldownMax = 1f;
        Rng rng;

        public bool HasHover;
        public RaycastHit Hover;
        public BodyPart HoverPart;

        // blade
        bool bladeTracking;
        Vector3 bladeStartDir, bladeEntry;
        float bladeStartTime;
        BodyPart bladePart;

        // shock
        BodyPart shockPart;
        Vector3 shockLocal;
        int shockLoop = -1;
        float shockTick;

        // laser
        public bool LaserFiring;
        public Vector3 LaserEnd;
        BodyPart laserPart;
        Vector3 laserPoint, laserNormal, laserPrevDir;
        bool laserHasPrev;
        float laserCutCooldown, laserDecalTimer;
        int laserLoop = -1;
        LineRenderer beam;

        public float CooldownFraction { get { return cooldown > 0f ? Mathf.Clamp01(cooldown / cooldownMax) : 0f; } }

        public ToolUser(ToolSystem s, Transform root, int player, bool local)
        {
            sys = s;
            Player = player;
            IsLocal = local;
            rng = new Rng(4321 + player * 77);
            Grab = new GrabController(root);
            Rope = new RopeTool(root);
            var go = new GameObject("LaserBeam");
            go.transform.SetParent(root, false);
            beam = go.AddComponent<LineRenderer>();
            beam.sharedMaterial = MaterialFactory.LineAdd;
            beam.positionCount = 2;
            beam.startWidth = 0.018f; beam.endWidth = 0.01f;
            beam.startColor = new Color(1f, 0.25f, 0.15f, 1f); beam.endColor = new Color(1f, 0.6f, 0.4f, 1f);
            beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.enabled = false;
        }

        public void Dispose()
        {
            ReleaseAll();
            StopLaser();
            if (beam != null) UnityEngine.Object.Destroy(beam.gameObject);
        }

        public Vector3 Muzzle
        {
            get
            {
                if (IsLocal && sys.View != null) return sys.View.Muzzle;
                return Input.aim.origin + Input.aim.direction * 0.55f + Input.right * 0.18f - Input.up * 0.14f;
            }
        }

        /// <summary>Network: a remote client's selection.</summary>
        public void SetSelection(ToolId t, int explosive, int prop, int grabMode)
        {
            if (t != Current) { if (Current == ToolId.Grab) Grab.Release(false); StopShock(); StopLaser(); bladeTracking = false; Current = t; }
            ExplosiveIndex = Mathf.Clamp(explosive, 0, ToolSystem.ExplosiveNames.Length - 1);
            PropIndex = Mathf.Clamp(prop, 0, ToolSystem.PropNames.Length - 1);
            if (grabMode != GrabMode) { GrabMode = grabMode; Grab.Release(false); Rope.CancelPending(); }
        }

        public void Select(ToolId t)
        {
            if (t == Current) { CycleSub(); return; }
            if (Current == ToolId.Grab && t != ToolId.Grab) Grab.Release(false);
            StopShock();
            StopLaser();
            bladeTracking = false;
            Current = t;
            if (IsLocal && Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Click, 0.4f, 1.2f);
        }

        public void CycleSub()
        {
            switch (Current)
            {
                case ToolId.Explosives: ExplosiveIndex = (ExplosiveIndex + 1) % ToolSystem.ExplosiveNames.Length; Toast(ToolSystem.ExplosiveNames[ExplosiveIndex]); break;
                case ToolId.Props: PropIndex = (PropIndex + 1) % ToolSystem.PropNames.Length; Toast(ToolSystem.PropNames[PropIndex]); break;
                case ToolId.Grab: GrabMode = 1 - GrabMode; Grab.Release(false); Rope.CancelPending(); Toast(GrabMode == 0 ? "Grab" : "Rope"); break;
            }
        }

        void Toast(string s) { if (IsLocal) SandboxEvents.RaiseToast(s, Color.white); }

        // Sub-mode label, rebuilt only when the selection changes (no per-frame string allocations).
        string subName = "";
        int subKey = -1;

        public string SubName
        {
            get
            {
                int key = (int)Current * 1000 + ExplosiveIndex * 100 + PropIndex * 10 + GrabMode * 2 + (Rope.HasPending ? 1 : 0);
                if (key == subKey) return subName;
                subKey = key;
                switch (Current)
                {
                    case ToolId.Explosives: subName = ToolSystem.ExplosiveNames[ExplosiveIndex] + "  [Q]"; break;
                    case ToolId.Props: subName = ToolSystem.PropNames[PropIndex] + "  [Q]"; break;
                    case ToolId.Grab: subName = (GrabMode == 0 ? "Grab" : "Rope" + (Rope.HasPending ? " - click the second point" : "")) + "  [Q]"; break;
                    default: subName = ""; break;
                }
                return subName;
            }
        }

        // Hover label cache
        string hoverLabel = "";
        int hoverKey = int.MinValue;
        object hoverObj;

        public void UpdateHoverLabel(BodyPart show)
        {
            if (show != null)
            {
                int pct = Mathf.RoundToInt(show.HealthFraction * 100f);
                var b = show.body;
                int frac = b != null ? b.FractureCount[(int)show.id] : 0;
                int key = pct * 64 + (show.attached ? 1 : 0) + (b != null && b.Alive ? 2 : 0) + Mathf.Min(frac, 7) * 4 + (show.cutOff ? 32 : 0);
                if (hoverObj != (object)show || key != hoverKey)
                {
                    hoverObj = show; hoverKey = key;
                    string state = show.attached ? (b != null && b.Alive ? "" : "  (dead)") : "  (severed)";
                    string fr = frac > 1 ? "  " + frac + " fractures" : frac == 1 || show.fractured ? "  fractured" : "";
                    hoverLabel = show.DisplayName + "  " + pct + "%" + fr + state + (b != null ? "   " + b.Label : "");
                }
                Sandbox.Hud.SetHover(hoverLabel, Color.Lerp(new Color(1f, 0.35f, 0.3f), new Color(0.6f, 1f, 0.6f), show.HealthFraction));
            }
            else if (HasHover)
            {
                Organ o;
                Vehicle v;
                if (Registry.Organs.TryGetValue(Hover.collider, out o))
                {
                    int key = Mathf.RoundToInt(o.health);
                    if (hoverObj != (object)o || key != hoverKey) { hoverObj = o; hoverKey = key; hoverLabel = o.DisplayName + "  " + key + "%"; }
                    Sandbox.Hud.SetHover(hoverLabel, new Color(1f, 0.6f, 0.6f));
                }
                else if (Registry.DamageableOf(Hover.collider) is ExplosiveProp) Sandbox.Hud.SetHover("Explosive", new Color(1f, 0.6f, 0.3f));
                else if (Hover.rigidbody != null && Registry.Vehicles.TryGetValue(Hover.rigidbody, out v) && v != null) Sandbox.Hud.SetHover(Hover.distance < 5f ? "Clay car  [E] drive" : "Clay car", new Color(1f, 0.85f, 0.55f));
                else Sandbox.Hud.SetHover("", Color.white);
            }
            else Sandbox.Hud.SetHover("", Color.white);
        }

        /// <summary>Runs the tool. With execute = false (network client) only the hover is updated; the host runs the action.</summary>
        public void Tick(bool execute)
        {
            if (cooldown > 0f) cooldown -= Time.deltaTime;
            if (laserCutCooldown > 0f) laserCutCooldown -= Time.deltaTime;
            var ts = Sandbox.Boot.tools;
            HasHover = sys.RaycastFirst(Input.aim, ts.maxRange, out Hover);
            HoverPart = HasHover ? Registry.PartOf(Hover.collider) : null;
            if (!execute) { LaserFiring = Current == ToolId.Laser && Input.held0 && !Input.blocked; return; }
            if (Input.blocked)
            {
                if (Grab.Active && !Grab.Pinned) Grab.Release(true);
                StopShock();
                StopLaser();
                if (bladeTracking) FinishSlash(Input.aim.direction);
                return;
            }
            // Ctrl + click: command the focused ragdoll to walk there
            if (Input.ctrl && Input.down0)
            {
                if (HasHover)
                {
                    var b = IsLocal ? Sandbox.Focus : HoverPart != null ? HoverPart.body : null;
                    if (b != null && b.Alive && b.OwnerPlayer < 0) { b.Brain.CommandWalkTo(Hover.point); Toast(b.Label + ": walking there"); }
                }
                return;
            }
            switch (Current)
            {
                case ToolId.Grab: TickGrab(); break;
                case ToolId.Hammer: TickHammer(); break;
                case ToolId.Blade: TickBlade(); break;
                case ToolId.Pistol:
                    if (Input.down0 && cooldown <= 0f) { SetCooldown(0.16f); FireGun(1, 0f, Sandbox.Boot.damage.pistolDamage, ts.pistolImpulse, 3, Sandbox.Boot.damage.bulletHoleRadius); if (IsLocal) sys.View.Recoil(0.6f); }
                    break;
                case ToolId.Shotgun:
                    if (Input.down0 && cooldown <= 0f)
                    {
                        SetCooldown(0.85f);
                        FireShotgun();
                        if (IsLocal) sys.View.Recoil(1.4f);
                    }
                    break;
                case ToolId.Explosives: TickExplosives(); break;
                case ToolId.Props: TickProps(); break;
                case ToolId.Shock: TickShock(); break;
                case ToolId.Heal: TickHeal(); break;
                case ToolId.Spawn: TickSpawn(); break;
                case ToolId.Laser: TickLaser(); break;
            }
        }

        void SetCooldown(float c) { cooldown = c; cooldownMax = c; }

        public void FixedTick(float dt)
        {
            Grab.FixedTick(dt);
            if (shockPart != null)
            {
                shockTick -= dt;
                if (shockTick <= 0f)
                {
                    shockTick = 0.1f;
                    if (shockPart == null || shockPart.body == null) { StopShock(); return; }
                    var d = DamageInfo.Make(DamageType.Shock, Sandbox.Boot.damage.shockDamagePerSecond * 0.1f, shockPart.transform.TransformPoint(shockLocal), Vector3.up);
                    d.fromPlayer = true; d.attacker = Player;
                    shockPart.ApplyDamage(d);
                    shockPart.body.Muscles.Spasm(1.4f);
                    if (shockPart.rb != null) shockPart.rb.AddForce(rng.OnUnitSphere() * 15f, ForceMode.Impulse);
                }
            }
            if (LaserFiring && laserPart != null && laserPart.body != null)
            {
                var d = DamageInfo.Make(DamageType.Burn, Sandbox.Boot.damage.laserDamagePerSecond * dt, laserPoint, Input.aim.direction);
                d.normal = laserNormal; d.fromPlayer = true; d.attacker = Player;
                laserPart.ApplyDamage(d);
                if (laserPart.rb != null) laserPart.rb.AddForceAtPosition(Input.aim.direction * 2f, laserPoint, ForceMode.Force);
            }
        }

        public void LateTick()
        {
            Grab.LateTick();
            Rope.LateTick();
            if (shockPart != null && Sandbox.Fx != null && Time.frameCount % 2 == 0)
            {
                Vector3 target = shockPart.transform.TransformPoint(shockLocal);
                Sandbox.Fx.Lightning(Muzzle, target, new Color(0.6f, 0.85f, 1f, 1f), 0.025f, 0.06f);
                Sandbox.Fx.ElectricSparks(target, (Input.aim.origin - target).normalized, 2);
                if (shockLoop >= 0) Sandbox.Audio.SetLoopPosition(shockLoop, target);
            }
            if (LaserFiring)
            {
                beam.enabled = true;
                beam.SetPosition(0, Muzzle);
                beam.SetPosition(1, LaserEnd);
                float flicker = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 60f);
                beam.startWidth = 0.016f * flicker; beam.endWidth = 0.009f * flicker;
            }
            else if (beam.enabled) beam.enabled = false;
        }

        #region Grab and rope
        void TickGrab()
        {
            var ts = Sandbox.Boot.tools;
            if (GrabMode == 1)
            {
                if (Input.down0 && HasHover) Rope.Click(Hover);
                if (Input.down1) Rope.ClearAll();
                return;
            }
            if (!Grab.Active)
            {
                if (Input.down0 && HasHover && Hover.rigidbody != null && !Hover.rigidbody.isKinematic)
                    Grab.Begin(Hover.rigidbody, Hover.point, Hover.distance);
                return;
            }
            if (Mathf.Abs(Input.scroll) > 0.01f) Grab.Distance = Mathf.Clamp(Grab.Distance + Input.scroll * 0.6f, ts.grabMinDistance, ts.grabMaxDistance);
            if (Input.down1) { Grab.Pinned = !Grab.Pinned; Toast(Grab.Pinned ? "Pinned in place" : "Unpinned"); if (!Grab.Pinned && !Input.held0) Grab.Release(false); }
            Grab.Strong = Input.shift;
            if (!Grab.Pinned) Grab.SetRay(Input.aim);
            if ((Input.up0 || !Input.held0) && !Grab.Pinned) Grab.Release(true);
        }

        public void ReleaseFrom(RagdollBody b)
        {
            if (Grab.Active && Grab.Part != null && Grab.Part.body == b) Grab.Release(false);
            if (shockPart != null && shockPart.body == b) StopShock();
            if (bladePart != null && bladePart.body == b) { bladePart = null; bladeTracking = false; }
            if (laserPart != null && laserPart.body == b) laserPart = null;
            Rope.RemoveFor(b);
        }

        public void ReleaseAll()
        {
            Grab.Release(false);
            StopShock();
            Rope.ClearAll();
        }
        #endregion

        #region Hammer
        void TickHammer()
        {
            bool light = Input.down0, heavy = Input.down1;
            if ((!light && !heavy) || cooldown > 0f) return;
            var ts = Sandbox.Boot.tools;
            SetCooldown(heavy ? 0.6f : 0.25f);
            if (IsLocal) sys.View.Swing(heavy ? 1.3f : 0.8f);
            if (!HasHover || Hover.distance > 3.2f) { Sandbox.Audio.Play(Sfx.Whoosh, Input.aim.origin, 0.4f, 1.2f); return; }
            float impulse = heavy ? ts.heavyHammerImpulse : ts.hammerImpulse;
            Vector3 dir = Input.aim.direction;
            if (Hover.rigidbody != null && !Hover.rigidbody.isKinematic) Hover.rigidbody.AddForceAtPosition(dir * impulse, Hover.point, ForceMode.Impulse);
            IDamageable dmg = Registry.DamageableOf(Hover.collider);
            if (dmg != null)
            {
                var d = DamageInfo.Make(DamageType.Blunt, impulse * 0.65f, Hover.point, dir);
                d.normal = Hover.normal;
                d.impulse = impulse * (heavy ? 1.6f : 1f);
                d.fromPlayer = true; d.attacker = Player;
                dmg.ApplyDamage(d);
                var part = dmg as BodyPart;
                if (part != null && part.body != null) part.body.Brain.OnImpact(part, impulse * 1.5f, dir);
            }
            Sandbox.Audio.PlayRandomThud(Hover.point, heavy ? 1f : 0.7f, heavy ? 0.8f : 1f);
            if (HoverPart == null) Sandbox.Fx.Impact(Hover.point, Hover.normal, IsMetal(Hover.collider), IsWood(Hover.collider));
            if (IsLocal && Sandbox.Cam != null) Sandbox.Cam.AddShake(heavy ? 0.12f : 0.05f);
            NetFx(NetFxKind.Impact, Hover.point, Hover.normal);
        }

        static bool IsMetal(Collider c)
        {
            var pm = c.sharedMaterial;
            return pm != null && (pm == PhysMats.Metal);
        }

        static bool IsWood(Collider c)
        {
            var pm = c.sharedMaterial;
            return pm != null && pm == PhysMats.Wood;
        }

        void NetFx(NetFxKind k, Vector3 a, Vector3 b)
        {
            if (Sandbox.IsHost && Sandbox.Net != null) Sandbox.Net.BroadcastFx(k, a, b);
        }
        #endregion

        #region Blade
        void TickBlade()
        {
            var ds = Sandbox.Boot.damage;
            if (Input.down1 && cooldown <= 0f && HasHover && Hover.distance < BladeReach - 0.2f)
            {
                SetCooldown(0.4f);
                if (IsLocal) sys.View.Swing(1f);
                IDamageable dmg = Registry.DamageableOf(Hover.collider);
                if (dmg != null)
                {
                    var d = DamageInfo.Make(DamageType.Pierce, ds.bladeDamage * 0.9f, Hover.point, Input.aim.direction);
                    d.normal = Hover.normal; d.depth = 0.08f; d.radius = 0.006f; d.fromPlayer = true; d.attacker = Player;
                    dmg.ApplyDamage(d);
                    Sandbox.Audio.Play(Sfx.Stab, Hover.point, 0.9f, rng.Range(0.9f, 1.1f));
                }
                return;
            }
            bool held = Input.held0;
            Ray ray = Input.aim;
            BodyPart under = HasHover && Hover.distance < BladeReach ? HoverPart : null;
            if (!held)
            {
                if (bladeTracking) FinishSlash(ray.direction);
                return;
            }
            if (Input.down0) { if (IsLocal) sys.View.Swing(0.6f); Sandbox.Audio.Play(Sfx.Whoosh, ray.origin, 0.3f, 1.4f); }
            if (!bladeTracking)
            {
                if (under != null)
                {
                    bladeTracking = true;
                    bladePart = under;
                    bladeStartDir = ray.direction;
                    bladeEntry = Hover.point;
                    bladeStartTime = Time.unscaledTime;
                }
                else if (HasHover && Hover.distance < BladeReach && cooldown <= 0f)
                {
                    // slashing props and organs
                    var dmg = Registry.DamageableOf(Hover.collider);
                    Organ o;
                    if (Registry.Organs.TryGetValue(Hover.collider, out o) && o.tether != null) { UnityEngine.Object.Destroy(o.tether); SetCooldown(0.2f); }
                    else if (dmg is BreakablePlank) { dmg.ApplyDamage(DamageInfo.Make(DamageType.Cut, 40f, Hover.point, ray.direction)); SetCooldown(0.2f); }
                }
                return;
            }
            // leaving the part, or dragged far enough -> resolve the slash
            if (under != bladePart || Vector3.Angle(bladeStartDir, ray.direction) > 14f) FinishSlash(ray.direction);
        }

        /// <summary>The drag defines the cut plane: it contains the eye and both the start and end aim directions,
        /// so the cut follows exactly the line the player dragged across the limb, at any angle.</summary>
        void FinishSlash(Vector3 endDir)
        {
            bladeTracking = false;
            var part = bladePart;
            bladePart = null;
            if (part == null || part.body == null) return;
            var ds = Sandbox.Boot.damage;
            float dur = Mathf.Max(0.016f, Time.unscaledTime - bladeStartTime);
            float sweep = Vector3.Angle(bladeStartDir, endDir);
            float speed = sweep / dur;
            Vector3 eye = Input.aim.origin;
            Vector3 n = Vector3.Cross(bladeStartDir, endDir);
            Vector3 point = bladeEntry;
            if (n.sqrMagnitude < 1e-6f || sweep < 1.5f)
            {
                var stab = DamageInfo.Make(DamageType.Cut, ds.bladeDamage * 0.5f, point, bladeStartDir);
                stab.depth = 0.02f; stab.radius = 0.02f; stab.fromPlayer = true; stab.attacker = Player;
                part.ApplyDamage(stab);
                Sandbox.Audio.Play(Sfx.Squelch1, point, 0.6f, 1.2f);
                return;
            }
            n.Normalize();
            Vector3 along = Vector3.Cross(n, (point - eye).normalized).normalized;
            float strength = Mathf.Clamp01(speed / Mathf.Max(1f, Sandbox.Boot.dismember.bladeSeverSpeed));
            var d = DamageInfo.Make(DamageType.Cut, ds.bladeDamage * Mathf.Lerp(0.5f, 1.3f, strength), point, (point - eye).normalized);
            d.depth = Mathf.Lerp(0.015f, 0.06f, strength) * part.body.Scale;
            d.radius = 0.05f * part.body.Scale;
            d.along = along;
            d.fromPlayer = true; d.attacker = Player;
            part.ApplyDamage(d);
            Sandbox.Audio.Play(Sfx.Slice, point, 0.9f, rng.Range(0.9f, 1.1f));
            if (Sandbox.Fx != null) Sandbox.Fx.EmitBloodSpray(point, along, 3f, 30f, 10, 0.02f, Sandbox.Boot.blood.arterial, Vector3.zero);
            if (speed >= Sandbox.Boot.dismember.bladeSeverSpeed && Sandbox.Gore != GoreLevel.Off)
                part.body.Dismember.Slice(part, point, n);
        }
        #endregion

        #region Laser
        void TickLaser()
        {
            var ts = Sandbox.Boot.tools;
            bool fire = Input.held0;
            if (!fire) { StopLaser(); return; }
            Ray ray = Input.aim;
            RaycastHit h;
            bool hit = sys.RaycastFirst(ray, ts.laserRange, out h);
            LaserFiring = true;
            LaserEnd = hit ? h.point : ray.origin + ray.direction * ts.laserRange;
            if (laserLoop < 0 && Sandbox.Audio != null) laserLoop = Sandbox.Audio.StartLoop(Sfx.Buzz, null, 0.35f, 2.2f, 601 + Player);
            if (laserLoop >= 0) Sandbox.Audio.SetLoopPosition(laserLoop, LaserEnd);
            BodyPart part = hit ? Registry.PartOf(h.collider) : null;
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            float sweep = laserHasPrev ? Vector3.Angle(laserPrevDir, ray.direction) / dt : 0f;
            // a fast sweep across a limb cuts it with the plane swept by the beam
            if (part != null && laserHasPrev && laserPart == part && sweep >= ts.laserCutSweep && laserCutCooldown <= 0f && Sandbox.Gore != GoreLevel.Off)
            {
                Vector3 n = Vector3.Cross(laserPrevDir, ray.direction);
                if (n.sqrMagnitude > 1e-8f)
                {
                    n.Normalize();
                    if (part.body.Dismember.Slice(part, h.point, n))
                    {
                        laserCutCooldown = 0.35f;
                        if (Sandbox.Fx != null) Sandbox.Fx.Impact(h.point, h.normal, true, false);
                    }
                }
            }
            if (hit)
            {
                laserPart = part;
                laserPoint = h.point;
                laserNormal = h.normal;
                if (Sandbox.Fx != null && Time.frameCount % 3 == 0) Sandbox.Fx.ElectricSparks(h.point, h.normal, 1);
                if (part == null)
                {
                    laserDecalTimer -= Time.deltaTime;
                    if (laserDecalTimer <= 0f)
                    {
                        laserDecalTimer = 0.08f;
                        Rigidbody hrb = h.collider.attachedRigidbody;
                        Sandbox.Decals.Add(h.point, h.normal, 0.06f, rng.Range(0f, 360f), (int)SplatCell.Scorch0 + rng.Range(0, 2), new Color(0.1f, 0.08f, 0.07f, 0.8f), DecalKind.Scorch, 1f,
                            hrb != null && !hrb.isKinematic ? h.collider.transform : null);
                        IDamageable dmg = Registry.DamageableOf(h.collider);
                        if (dmg != null) dmg.ApplyDamage(DamageInfo.Make(DamageType.Burn, ts.laserRange > 0f ? Sandbox.Boot.damage.laserDamagePerSecond * 0.08f : 0f, h.point, ray.direction));
                    }
                }
            }
            else laserPart = null;
            laserPrevDir = ray.direction;
            laserHasPrev = true;
        }

        void StopLaser()
        {
            LaserFiring = false;
            laserPart = null;
            laserHasPrev = false;
            if (laserLoop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(laserLoop);
            laserLoop = -1;
        }
        #endregion

        #region Guns
        void FireShotgun()
        {
            var ts = Sandbox.Boot.tools;
            var ds = Sandbox.Boot.damage;
            // point blank: all pellets in one large crater
            if (HasHover && HoverPart != null && Hover.distance <= ts.pointBlankDistance)
            {
                Vector3 muzzle = Muzzle;
                Sandbox.Audio.Play(Sfx.Shotgun, muzzle, 1f, rng.Range(0.95f, 1.05f), 3f);
                Sandbox.Fx.MuzzleFlash(muzzle, Input.aim.direction, 1.6f);
                if (IsLocal && Sandbox.Cam != null) { Sandbox.Cam.AddRecoil(3.5f); Sandbox.Cam.AddShake(0.1f); }
                var d = DamageInfo.Make(DamageType.Pierce, ds.shotgunPelletDamage * ts.shotgunPellets * 0.8f, Hover.point, Input.aim.direction);
                d.normal = Hover.normal;
                d.radius = ds.craterRadius * HoverPart.body.Scale;
                d.crater = true;
                d.depth = 0.08f * HoverPart.body.Scale;
                d.impulse = ts.shotgunPelletImpulse * ts.shotgunPellets;
                d.fromPlayer = true; d.attacker = Player;
                HoverPart.ApplyDamage(d);
                if (HoverPart.rb != null) HoverPart.rb.AddForceAtPosition(Input.aim.direction * d.impulse, Hover.point, ForceMode.Impulse);
                HoverPart.body.Brain.OnImpact(HoverPart, d.impulse * 3f, Input.aim.direction);
                if (Sandbox.Fx != null) Sandbox.Fx.EmitBloodSpray(Hover.point, Hover.normal, 3f, 50f, 18, 0.024f, Sandbox.Boot.blood.arterial, Vector3.zero);
                NetFx(NetFxKind.Shotgun, muzzle, Hover.point);
                return;
            }
            FireGun(ts.shotgunPellets, ts.shotgunSpread, ds.shotgunPelletDamage, ts.shotgunPelletImpulse, 1, ds.pelletHoleRadius);
        }

        void FireGun(int pellets, float spreadDeg, float damage, float impulse, int maxPenetrations, float holeRadius)
        {
            Ray aim = Input.aim;
            Vector3 muzzle = Muzzle;
            Sandbox.Audio.Play(pellets > 1 ? Sfx.Shotgun : Sfx.Gunshot, muzzle, 1f, rng.Range(0.95f, 1.05f), 3f);
            Sandbox.Fx.MuzzleFlash(muzzle, aim.direction, pellets > 1 ? 1.6f : 1f);
            if (IsLocal && Sandbox.Cam != null) { Sandbox.Cam.AddRecoil(pellets > 1 ? 3.5f : 1.6f); Sandbox.Cam.AddShake(pellets > 1 ? 0.08f : 0.03f); }
            for (int p = 0; p < pellets; p++)
            {
                Vector3 dir = aim.direction;
                if (spreadDeg > 0f)
                {
                    Vector2 r = rng.InsideUnitCircle() * Mathf.Tan(spreadDeg * Mathf.Deg2Rad);
                    dir = (dir + Input.right * r.x + Input.up * r.y).normalized;
                }
                Vector3 end = Bullet(new Ray(aim.origin, dir), muzzle, damage, impulse, maxPenetrations, holeRadius);
                NetFx(pellets > 1 ? NetFxKind.Shotgun : NetFxKind.Gunshot, muzzle, end);
            }
        }

        Vector3 Bullet(Ray ray, Vector3 muzzle, float damage, float impulse, int maxPen, float holeRadius)
        {
            float range = Sandbox.Boot.tools.maxRange;
            var hits = sys.HitBuffer;
            int n = Physics.RaycastNonAlloc(ray, hits, range, Layers.ToolMask, QueryTriggerInteraction.Ignore);
            SortHits(hits, n);
            Vector3 end = ray.origin + ray.direction * range;
            float energy = 1f;
            int pens = 0;
            Collider self = Sandbox.Player != null ? Sandbox.Player.Collider : null;
            Collider lastExitCol = null;
            for (int i = 0; i < n && energy > 0.05f; i++)
            {
                var h = hits[i];
                if (h.collider == self || h.collider == lastExitCol) continue;
                BodyPart part = Registry.PartOf(h.collider);
                IDamageable dmg = Registry.DamageableOf(h.collider);
                if (part != null)
                {
                    // exit point: cast back from beyond the collider
                    Vector3 exit = h.point;
                    bool hasExit = false;
                    float probe = 0.8f;
                    RaycastHit back;
                    if (h.collider.Raycast(new Ray(h.point + ray.direction * probe, -ray.direction), out back, probe))
                    {
                        exit = back.point;
                        hasExit = (exit - h.point).magnitude > 0.01f;
                    }
                    var d = DamageInfo.Make(DamageType.Pierce, damage * energy, h.point, ray.direction);
                    d.normal = h.normal;
                    d.hasExit = hasExit && energy > 0.3f;
                    d.exitPoint = exit;
                    d.depth = hasExit ? (exit - h.point).magnitude : 0.06f;
                    d.radius = part.body != null ? holeRadius * part.body.Scale : holeRadius;
                    d.impulse = impulse * energy;
                    d.fromPlayer = true; d.attacker = Player;
                    part.ApplyDamage(d);
                    if (part.rb != null) part.rb.AddForceAtPosition(ray.direction * impulse * energy, h.point, ForceMode.Impulse);
                    if (part.body != null) part.body.Brain.OnImpact(part, impulse * energy * 4f, ray.direction);
                    if (Sandbox.Fx != null)
                    {
                        Sandbox.Fx.EmitBloodSpray(h.point, h.normal, 2f, 35f, 6, 0.018f, Sandbox.Boot.blood.arterial, Vector3.zero);
                        Sandbox.Fx.EmitMist(h.point, h.normal * 0.5f, 2, 0.06f, new Color(0.6f, 0.02f, 0.03f, 0.4f));
                    }
                    Sandbox.Audio.Play(Sfx.Squelch1, h.point, 0.5f, rng.Range(1.1f, 1.4f));
                    energy *= hasExit ? 0.55f : 0f;
                    pens++;
                    lastExitCol = h.collider;
                    end = hasExit ? exit : h.point;
                    if (pens >= maxPen) { if (!hasExit) end = h.point; energy = Mathf.Min(energy, 0.04f); }
                    continue;
                }
                if (dmg != null)
                {
                    var d = DamageInfo.Make(DamageType.Pierce, damage * energy * 1.2f, h.point, ray.direction);
                    d.normal = h.normal;
                    d.fromPlayer = true; d.attacker = Player;
                    dmg.ApplyDamage(d);
                }
                if (h.rigidbody != null && !h.rigidbody.isKinematic) h.rigidbody.AddForceAtPosition(ray.direction * impulse * energy, h.point, ForceMode.Impulse);
                bool metal = IsMetal(h.collider), wood = IsWood(h.collider);
                Sandbox.Fx.Impact(h.point, h.normal, metal, wood);
                Rigidbody hrb = h.collider.attachedRigidbody;
                Sandbox.Decals.Add(h.point, h.normal, 0.07f, rng.Range(0f, 360f), (int)SplatCell.Hole0 + rng.Range(0, 2), new Color(1f, 1f, 1f, 0.9f), DecalKind.BulletHole, 1f,
                    hrb != null && !hrb.isKinematic ? h.collider.transform : null);
                if (metal) Sandbox.Audio.Play(Sfx.Clang, h.point, 0.35f, rng.Range(1.4f, 1.9f));
                end = h.point;
                break;
            }
            Sandbox.Fx.Tracer(muzzle, end, new Color(1f, 0.85f, 0.55f, 0.8f), 0.012f, 0.05f);
            return end;
        }

        static void SortHits(RaycastHit[] hits, int n)
        {
            for (int i = 1; i < n; i++)
            {
                var key = hits[i];
                int j = i - 1;
                while (j >= 0 && hits[j].distance > key.distance) { hits[j + 1] = hits[j]; j--; }
                hits[j + 1] = key;
            }
        }
        #endregion

        #region Explosives and props
        void TickExplosives()
        {
            if (Input.down1) { Sandbox.Explosions.DetonateAllC4(); return; }
            if (!Input.down0 || cooldown > 0f) return;
            SetCooldown(0.35f);
            var kind = (ExplosiveKind)ExplosiveIndex;
            Ray r = Input.aim;
            float speed = Sandbox.Boot.tools.throwSpeed;
            if (IsLocal) sys.View.Throw();
            switch (kind)
            {
                case ExplosiveKind.Grenade:
                case ExplosiveKind.TNT:
                case ExplosiveKind.C4:
                    {
                        Vector3 start = r.origin + r.direction * 0.6f + Input.right * 0.15f - Input.up * 0.1f;
                        var ep = Sandbox.Explosions.Spawn(kind, start, Quaternion.LookRotation(r.direction), r.direction * speed * (kind == ExplosiveKind.C4 ? 0.9f : 0.7f) + Vector3.up * 2f);
                        if (kind == ExplosiveKind.TNT && ep != null) ep.LightFuse(4f);
                        Sandbox.Audio.Play(Sfx.Whoosh, start, 0.4f, 1f);
                        break;
                    }
                case ExplosiveKind.Barrel:
                    if (HasHover) Sandbox.Explosions.Spawn(kind, Hover.point + Hover.normal * 0.5f, Quaternion.identity, Vector3.zero);
                    break;
                case ExplosiveKind.Landmine:
                    if (HasHover && Hover.normal.y > 0.6f) Sandbox.Explosions.Spawn(kind, Hover.point + Hover.normal * 0.01f, Quaternion.FromToRotation(Vector3.up, Hover.normal), Vector3.zero);
                    break;
                default:
                    if (HasHover)
                    {
                        Sandbox.Explosions.Spawn(kind, Hover.point + Hover.normal * 0.4f, Quaternion.identity, Vector3.zero);
                        SandboxEvents.RaiseToast("MEGA BLAST armed - run!", new Color(1f, 0.6f, 0.2f));
                    }
                    break;
            }
        }

        void TickProps()
        {
            bool place = Input.down0, throwIt = Input.down1;
            if ((!place && !throwIt) || cooldown > 0f) return;
            SetCooldown(PropIndex == 6 ? 1f : 0.2f);
            Ray r = Input.aim;
            float yaw = MathUtil.Yaw(MathUtil.SafeNormal(MathUtil.Flat(r.direction), Vector3.forward));
            if (PropIndex == 6)
            {
                // clay car: LMB parks one at the crosshair, RMB launches an AI car along the view direction
                Vector3 cp = HasHover ? Hover.point + Vector3.up * 0.6f : r.origin + r.direction * 8f;
                var car = Sandbox.Vehicles.Spawn(cp, yaw, throwIt);
                if (car != null && throwIt) Compat.SetVelocity(car.rb, MathUtil.Flat(r.direction).normalized * Sandbox.Boot.vehicles.aiSpeed);
                Toast(throwIt ? "Clay car launched" : "Clay car parked - E to drive");
                return;
            }
            Vector3 pos;
            Vector3 vel = Vector3.zero;
            if (throwIt) { pos = r.origin + r.direction * 1.2f; vel = r.direction * Sandbox.Boot.tools.throwSpeed; if (IsLocal) sys.View.Throw(); }
            else if (HasHover) pos = Hover.point + Hover.normal * 0.6f;
            else pos = r.origin + r.direction * 4f;
            var go = MakeProp(PropIndex, pos, Quaternion.Euler(0f, yaw, 0f));
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) Compat.SetVelocity(rb, vel);
            sys.TrackSpawned(go);
            if (Sandbox.Net != null) Sandbox.Net.OnPropSpawned(go, PropIndex);
        }

        public GameObject MakeProp(int index, Vector3 pos, Quaternion rot)
        {
            var w = Sandbox.World;
            switch (index)
            {
                case 0: return w.DynamicBox("Crate", pos, Vector3.one, MaterialFactory.Crate, 0f, "wood", 20f, rot, Sfx.WoodKnock);
                case 1: return w.MakeBall(pos, 0.35f, 3f);
                case 2: return w.MakeSteelBarrel(pos, rot);
                case 3:
                    {
                        var go = w.DynamicBox("Anvil", pos, new Vector3(0.6f, 0.4f, 0.3f), MaterialFactory.DarkMetal, 0f, "metal", 100f, rot, Sfx.Clang);
                        var top = new GameObject("AnvilTop");
                        top.transform.SetParent(go.transform, false);
                        top.transform.localPosition = new Vector3(0.1f, 0.28f, 0f);
                        top.AddComponent<MeshFilter>().sharedMesh = MeshFactory.BoxCached(new Vector3(0.85f, 0.16f, 0.32f), 0f);
                        top.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.DarkMetal;
                        var bc = top.AddComponent<BoxCollider>(); bc.size = new Vector3(0.85f, 0.16f, 0.32f);
                        return go;
                    }
                case 4: return w.DynamicBox("Plank", pos, new Vector3(2f, 0.05f, 0.25f), MaterialFactory.Wood, 0.6f, "wood", 8f, rot, Sfx.WoodKnock);
                default:
                    {
                        var go = new GameObject("Boulder");
                        go.transform.position = pos;
                        go.transform.rotation = rot;
                        var mesh = MeshFactory.Rock(0.55f, rng.Range(0, 99999), 0.25f);
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        go.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.Rock;
                        // a simple sphere collider instead of a convex mesh (cheaper contacts)
                        var sc = go.AddComponent<SphereCollider>(); sc.radius = 0.5f; sc.sharedMaterial = PhysMats.Stone;
                        var rb = go.AddComponent<Rigidbody>(); rb.mass = 250f; rb.interpolation = RigidbodyInterpolation.Interpolate;
                        go.AddComponent<PropImpact>().sound = Sfx.Thump;
                        return go;
                    }
            }
        }
        #endregion

        #region Shock, heal, spawn
        void TickShock()
        {
            if (Input.down1 && HasHover && HoverPart != null && HoverPart.body != null)
            {
                var b = HoverPart.body;
                Sandbox.Audio.Play(Sfx.Zap, Hover.point, 1f, 0.7f);
                Sandbox.Fx.Lightning(Muzzle, Hover.point, new Color(0.8f, 0.9f, 1f, 1f), 0.05f, 0.15f);
                b.Muscles.Spasm(1.5f);
                b.LastInteractionTime = Time.time;
                if (b.Vitals.CanRevive || (b.Vitals.CardiacArrest && !b.Vitals.Dead && b.Organs.HeartHealth01 > 0f))
                {
                    bool wasDead = b.Vitals.Dead;
                    b.Vitals.CardiacArrest = false;
                    if (wasDead) b.Vitals.Revive(false);
                    SandboxEvents.RaiseToast(wasDead ? "Defibrillated - revived!" : "Heart restarted", new Color(0.6f, 1f, 0.7f));
                }
                return;
            }
            bool held = Input.held0;
            if (!held) { StopShock(); return; }
            if (shockPart == null && HasHover && HoverPart != null && Hover.distance < 6f)
            {
                shockPart = HoverPart;
                shockLocal = shockPart.transform.InverseTransformPoint(Hover.point);
                if (shockLoop < 0) shockLoop = Sandbox.Audio.StartLoop(Sfx.Buzz, null, 0.6f, 1.2f, 600 + Player);
            }
            if (shockPart != null && (HoverPart != shockPart || !HasHover)) StopShock();
        }

        void StopShock()
        {
            shockPart = null;
            if (shockLoop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(shockLoop);
            shockLoop = -1;
        }

        void TickHeal()
        {
            if (!HasHover || HoverPart == null || HoverPart.body == null) return;
            var b = HoverPart.body;
            if (Input.down0)
            {
                b.Damage.Heal(HoverPart);
                if (b.Vitals.CanRevive) { b.Vitals.Revive(false); Toast("Revived"); }
                else Toast(HoverPart.DisplayName + " healed");
                b.Vitals.BloodVolume = Mathf.Min(b.Vitals.MaxBlood, b.Vitals.BloodVolume + 0.4f);
                if (IsLocal) Sandbox.Audio.Play2D(Sfx.Beep, 0.4f, 0.8f);
            }
            else if (Input.down1)
            {
                Vector3 p = b.Pelvis != null && b.Pelvis.rb != null ? b.Pelvis.rb.position : b.transform.position;
                float y = b.Balance.HasGround ? b.Balance.GroundHeight : p.y - 0.9f;
                float yaw = b.Locomotion.HeadingYaw;
                bool possessed = b.Possessed;
                int owner = b.OwnerPlayer;
                Sandbox.Boot.RemoveRagdoll(b);
                var nb = Sandbox.Boot.SpawnRagdoll(new Vector3(p.x, y + 0.02f, p.z), yaw);
                if (nb != null)
                {
                    if (possessed && owner < 0) Sandbox.Boot.Possess(nb);
                    if (owner >= 0 && Sandbox.Net != null) Sandbox.Net.AssignBody(owner, nb);
                }
                Toast("Ragdoll fully restored");
            }
        }

        void TickSpawn()
        {
            if (Input.down0 && HasHover && Hover.normal.y > 0.5f)
            {
                Vector3 toEye = MathUtil.Flat(Input.aim.origin - Hover.point);
                var b = Sandbox.Boot.SpawnRagdoll(Hover.point + Vector3.up * 0.03f, MathUtil.Yaw(toEye.sqrMagnitude > 0.01f ? toEye : Vector3.forward));
                if (b != null) Toast("Spawned " + b.Label);
            }
            else if (Input.down1)
            {
                RagdollBody target = HoverPart != null ? HoverPart.body : null;
                if (target == null && Sandbox.Ragdolls.Count > 0) target = Sandbox.Ragdolls[Sandbox.Ragdolls.Count - 1];
                if (target != null && target.OwnerPlayer < 0) { Sandbox.Boot.RemoveRagdoll(target); Toast("Ragdoll removed"); }
            }
        }
        #endregion
    }
    #endregion


    #region GrabController
    /// <summary>Physics grab: a ConfigurableJoint with linear drives from the exact hit point to a kinematic anchor.</summary>
    public class GrabController
    {
        readonly Rigidbody anchor;
        ConfigurableJoint joint;
        Rigidbody target;
        public BodyPart Part;
        public float Distance;
        public bool Strong, Pinned;
        Vector3 desired, lastAnchor, anchorVel;
        float effMass;
        float stretchTimer;
        readonly LineRenderer line;

        public bool Active { get { return joint != null && target != null; } }

        public GrabController(Transform parent)
        {
            var go = new GameObject("GrabAnchor");
            go.transform.SetParent(parent, false);
            anchor = go.AddComponent<Rigidbody>();
            anchor.isKinematic = true;
            anchor.interpolation = RigidbodyInterpolation.Interpolate;
            var lgo = new GameObject("GrabLine");
            lgo.transform.SetParent(parent, false);
            line = lgo.AddComponent<LineRenderer>();
            line.sharedMaterial = MaterialFactory.LineAdd;
            line.positionCount = 2;
            line.startWidth = 0.01f; line.endWidth = 0.004f;
            line.startColor = new Color(1f, 0.85f, 0.4f, 0.6f); line.endColor = new Color(1f, 0.85f, 0.4f, 0.2f);
            line.enabled = false;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        public void Begin(Rigidbody rb, Vector3 point, float distance)
        {
            Release(false);
            target = rb;
            Part = Registry.PartOf(rb);
            Distance = Mathf.Clamp(distance, Sandbox.Boot.tools.grabMinDistance, Sandbox.Boot.tools.grabMaxDistance);
            anchor.position = point;
            anchor.transform.position = point;
            desired = point;
            lastAnchor = point;
            effMass = rb.mass;
            if (Part != null && Part.body != null && Part.attached) effMass = Part.body.TotalMass;
            joint = rb.gameObject.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody = anchor;
            joint.anchor = rb.transform.InverseTransformPoint(point);
            joint.connectedAnchor = Vector3.zero;
            joint.xMotion = ConfigurableJointMotion.Free;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Free;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.enablePreprocessing = false;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = Mathf.Clamp(effMass * 0.02f, 0.05f, 5f), maximumForce = 1000f };
            ApplyDrive();
            Pinned = false;
            if (Part != null && Part.body != null) Part.body.Brain.SetGrabbed(Part);
            line.enabled = true;
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Thump, point, 0.25f, 1.6f);
        }

        void ApplyDrive()
        {
            if (joint == null) return;
            var ts = Sandbox.Boot.tools;
            float k = Mathf.Clamp(effMass / 20f, 0.25f, 6f);
            float strong = Strong ? ts.strongPullMultiplier : 1f;
            var d = new JointDrive { positionSpring = ts.grabSpring * k * strong, positionDamper = ts.grabDamper * Mathf.Pow(k, 0.7f) * Mathf.Sqrt(strong), maximumForce = ts.grabMaxForce * Mathf.Clamp(k, 0.5f, 6f) * strong };
            joint.xDrive = d; joint.yDrive = d; joint.zDrive = d;
        }

        public void SetRay(Ray r) { desired = r.origin + r.direction * Distance; }

        public void FixedTick(float dt)
        {
            if (joint == null || target == null)
            {
                if (joint != null || target != null) Release(false);
                return;
            }
            ApplyDrive();
            Vector3 p = Pinned ? anchor.position : desired;
            // strong pull visibly stretches the skin of a body part toward the hand
            if (Strong && Part != null && Part.body != null && Part.body.Skin != null && Part.attached)
            {
                stretchTimer -= dt;
                Vector3 grabPoint = target.transform.TransformPoint(joint.anchor);
                Vector3 pull = p - grabPoint;
                float strain = pull.magnitude;
                if (stretchTimer <= 0f && strain > 0.12f && Sandbox.Boot.damage.deformationScale > 0f)
                {
                    stretchTimer = 0.25f;
                    float s = Part.body.Scale;
                    Part.body.Skin.Stretch(Part, grabPoint, pull / strain, 0.09f * s, Mathf.Min(0.05f, (strain - 0.1f) * 0.08f) * s * Sandbox.Boot.damage.deformationScale);
                    Part.body.LastInteractionTime = Time.time;
                }
            }
            anchorVel = Vector3.Lerp(anchorVel, (p - lastAnchor) / Mathf.Max(1e-4f, dt), 0.5f);
            lastAnchor = p;
            anchor.MovePosition(p);
            target.WakeUp();
        }

        public void LateTick()
        {
            if (!Active) { if (line.enabled) line.enabled = false; return; }
            line.SetPosition(0, anchor.transform.position);
            line.SetPosition(1, target.transform.TransformPoint(joint.anchor));
        }

        public void Release(bool throwIt)
        {
            if (joint != null)
            {
                if (throwIt && target != null)
                {
                    var ts = Sandbox.Boot.tools;
                    Vector3 v = Compat.GetVelocity(target);
                    Vector3 boost = (anchorVel - v) * ts.throwBoost;
                    if (boost.magnitude > 25f) boost = boost.normalized * 25f;
                    Compat.SetVelocity(target, v + boost);
                    if (Part != null && Part.body != null && Part.attached)
                    {
                        var parts = Part.body.AttachedParts;
                        for (int i = 0; i < parts.Count; i++)
                            if (parts[i] != Part) Compat.SetVelocity(parts[i].rb, Compat.GetVelocity(parts[i].rb) + boost * 0.5f);
                    }
                    if (boost.magnitude > 4f && Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Whoosh, target.position, 0.5f, 1f);
                }
                UnityEngine.Object.Destroy(joint);
            }
            if (Part != null && Part.body != null) Part.body.Brain.SetGrabbed(null);
            joint = null;
            target = null;
            Part = null;
            Pinned = false;
            line.enabled = false;
        }
    }

    /// <summary>Rope tool: click two points (bodies or world) to tie them with a distance-limited joint drawn as a sagging line.</summary>
    public class RopeTool
    {
        class Rope
        {
            public ConfigurableJoint joint;
            public Rigidbody a, b;
            public Vector3 localA, localB, worldB;
            public float length;
            public LineRenderer lr;
        }

        readonly Transform root;
        readonly List<Rope> ropes = new List<Rope>();
        bool pending;
        Rigidbody pendA;
        Vector3 pendLocal, pendWorld;
        static readonly Vector3[] pts = new Vector3[12];

        public bool HasPending { get { return pending; } }

        public RopeTool(Transform parent) { root = parent; }

        public void CancelPending() { pending = false; }

        public void Click(RaycastHit hit)
        {
            Rigidbody rb = hit.rigidbody;
            if (!pending)
            {
                pending = true;
                pendA = rb;
                pendWorld = hit.point;
                pendLocal = rb != null ? rb.transform.InverseTransformPoint(hit.point) : hit.point;
                if (Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Click, 0.4f, 1f);
                return;
            }
            pending = false;
            Rigidbody a = pendA, b = rb;
            Vector3 la = pendLocal, wa = pendA != null ? pendA.transform.TransformPoint(pendLocal) : pendWorld;
            Vector3 lb = rb != null ? rb.transform.InverseTransformPoint(hit.point) : hit.point;
            if (a == null && b == null) return;
            if (a == null) { a = b; b = null; la = lb; lb = wa; }
            if (a == b) return;
            if (ropes.Count >= Sandbox.Boot.tools.ropeCap) RemoveAt(0);
            var r = new Rope { a = a, b = b, localA = la, localB = lb, worldB = b == null ? lb : Vector3.zero };
            Vector3 pa = a.transform.TransformPoint(la);
            Vector3 pb = b != null ? b.transform.TransformPoint(lb) : lb;
            r.length = Mathf.Max(0.2f, (pa - pb).magnitude);
            var j = a.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = la;
            j.connectedBody = b;
            j.connectedAnchor = b != null ? lb : pb;
            j.xMotion = ConfigurableJointMotion.Limited;
            j.yMotion = ConfigurableJointMotion.Limited;
            j.zMotion = ConfigurableJointMotion.Limited;
            j.angularXMotion = ConfigurableJointMotion.Free;
            j.angularYMotion = ConfigurableJointMotion.Free;
            j.angularZMotion = ConfigurableJointMotion.Free;
            j.linearLimit = new SoftJointLimit { limit = r.length };
            j.linearLimitSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
            j.breakForce = 6000f;
            j.enablePreprocessing = false;
            r.joint = j;
            var go = new GameObject("Rope");
            go.transform.SetParent(root, false);
            r.lr = go.AddComponent<LineRenderer>();
            r.lr.sharedMaterial = MaterialFactory.Line;
            r.lr.positionCount = pts.Length;
            r.lr.startWidth = 0.025f; r.lr.endWidth = 0.025f;
            r.lr.startColor = new Color(0.65f, 0.55f, 0.4f, 1f); r.lr.endColor = new Color(0.6f, 0.5f, 0.35f, 1f);
            r.lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            ropes.Add(r);
            if (Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Click, 0.5f, 0.8f);
        }

        void RemoveAt(int i)
        {
            var r = ropes[i];
            if (r.joint != null) UnityEngine.Object.Destroy(r.joint);
            if (r.lr != null) UnityEngine.Object.Destroy(r.lr.gameObject);
            ropes.RemoveAt(i);
        }

        public void ClearAll()
        {
            for (int i = ropes.Count - 1; i >= 0; i--) RemoveAt(i);
            pending = false;
        }

        public void RemoveFor(RagdollBody body)
        {
            for (int i = ropes.Count - 1; i >= 0; i--)
            {
                var r = ropes[i];
                BodyPart pa = Registry.PartOf(r.a), pb = Registry.PartOf(r.b);
                if ((pa != null && pa.body == body) || (pb != null && pb.body == body)) RemoveAt(i);
            }
        }

        public void LateTick()
        {
            for (int i = ropes.Count - 1; i >= 0; i--)
            {
                var r = ropes[i];
                if (r.joint == null || r.a == null || (r.b == null && r.joint.connectedBody != null)) { RemoveAt(i); continue; }
                Vector3 pa = r.a.transform.TransformPoint(r.localA);
                Vector3 pb = r.b != null ? r.b.transform.TransformPoint(r.localB) : r.localB;
                float d = (pa - pb).magnitude;
                float slack = Mathf.Max(0f, r.length - d);
                float sag = Mathf.Sqrt(Mathf.Max(0f, r.length * slack)) * 0.5f;
                for (int k = 0; k < pts.Length; k++)
                {
                    float u = k / (float)(pts.Length - 1);
                    pts[k] = Vector3.Lerp(pa, pb, u) + Vector3.down * sag * 4f * u * (1f - u);
                }
                r.lr.SetPositions(pts);
            }
        }
    }
    #endregion

    #region ViewModels
    /// <summary>Simple procedural first-person tool models with bob, recoil, swing and throw animations.</summary>
    public class ViewModels
    {
        Transform holder;
        readonly GameObject[] models = new GameObject[11];
        Transform muzzle;
        readonly Transform[] muzzles = new Transform[11];
        float recoil, swing, throwT, bob;
        Vector3 basePos = new Vector3(0.24f, -0.22f, 0.45f);

        public Vector3 Muzzle { get { return muzzle != null ? muzzle.position : (Sandbox.MainCamera != null ? Sandbox.MainCamera.transform.position + Sandbox.MainCamera.transform.forward * 0.5f : Vector3.zero); } }

        public void Build(Transform cam)
        {
            holder = new GameObject("ViewModel").transform;
            holder.SetParent(cam, false);
            holder.localPosition = basePos;
            var skin = MaterialFactory.Lit("VMSkin", Sandbox.Boot.character.skinColor, null, 0.25f, 0f);
            var dark = MaterialFactory.DarkMetal;
            var wood = MaterialFactory.Wood;
            var steel = MaterialFactory.Spikes;
            models[0] = Model("Hand", m =>
            {
                Part(m, MeshFactory.Box(new Vector3(0.08f, 0.03f, 0.1f), 0f), skin, new Vector3(0f, 0f, 0.05f), Vector3.zero);
                for (int f = 0; f < 4; f++) Part(m, MeshFactory.Box(new Vector3(0.016f, 0.018f, 0.06f), 0f), skin, new Vector3(-0.03f + f * 0.02f, 0f, 0.13f), new Vector3(-15f, 0f, 0f));
                Part(m, MeshFactory.Box(new Vector3(0.02f, 0.02f, 0.05f), 0f), skin, new Vector3(0.05f, 0f, 0.06f), new Vector3(0f, -35f, 0f));
            }, 0);
            models[1] = Model("Hammer", m =>
            {
                Part(m, MeshFactory.Cylinder(0.015f, 0.35f, 8, 1f), wood, new Vector3(0f, 0.05f, 0.1f), new Vector3(70f, 0f, 0f));
                Part(m, MeshFactory.Box(new Vector3(0.05f, 0.05f, 0.14f), 0f), dark, new Vector3(0f, 0.11f, 0.26f), new Vector3(70f, 90f, 0f));
            }, 1);
            models[2] = Model("Blade", m =>
            {
                Part(m, MeshFactory.Box(new Vector3(0.025f, 0.03f, 0.1f), 0f), dark, new Vector3(0f, 0f, 0.02f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.004f, 0.035f, 0.24f), 0f), steel, new Vector3(0f, 0.005f, 0.19f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.06f, 0.012f, 0.012f), 0f), dark, new Vector3(0f, 0f, 0.075f), Vector3.zero);
            }, 2);
            models[3] = Model("Pistol", m =>
            {
                Part(m, MeshFactory.Box(new Vector3(0.035f, 0.04f, 0.19f), 0f), dark, new Vector3(0f, 0.03f, 0.08f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.03f, 0.1f, 0.045f), 0f), dark, new Vector3(0f, -0.03f, 0.0f), new Vector3(15f, 0f, 0f));
            }, 3, new Vector3(0f, 0.035f, 0.18f));
            models[4] = Model("Shotgun", m =>
            {
                Part(m, MeshFactory.Cylinder(0.014f, 0.55f, 8, 1f), dark, new Vector3(0f, 0.03f, 0.25f), new Vector3(90f, 0f, 0f));
                Part(m, MeshFactory.Box(new Vector3(0.045f, 0.05f, 0.25f), 0f), wood, new Vector3(0f, 0f, -0.05f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.04f, 0.03f, 0.12f), 0f), wood, new Vector3(0f, 0.005f, 0.2f), Vector3.zero);
            }, 4, new Vector3(0f, 0.03f, 0.53f));
            models[5] = Model("Explosive", m =>
            {
                var mf = Part(m, ExplosionSystem.GrenadeMesh, ExplosionSystem.GrenadeMat, new Vector3(0f, 0f, 0.08f), Vector3.zero);
                mf.transform.localScale = Vector3.one * 1.3f;
            }, 5);
            models[6] = Model("Props", m => { Part(m, MeshFactory.Box(new Vector3(0.12f, 0.12f, 0.12f), 0f), MaterialFactory.Crate, new Vector3(0f, 0f, 0.1f), new Vector3(10f, 20f, 0f)); }, 6);
            models[7] = Model("Shock", m =>
            {
                Part(m, MeshFactory.Box(new Vector3(0.05f, 0.05f, 0.16f), 0f), MaterialFactory.PaintBlue, new Vector3(0f, 0f, 0.05f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.008f, 0.008f, 0.05f), 0f), MaterialFactory.Gold, new Vector3(0.012f, 0f, 0.15f), Vector3.zero);
                Part(m, MeshFactory.Box(new Vector3(0.008f, 0.008f, 0.05f), 0f), MaterialFactory.Gold, new Vector3(-0.012f, 0f, 0.15f), Vector3.zero);
            }, 7, new Vector3(0f, 0f, 0.18f));
            models[8] = Model("Heal", m =>
            {
                Part(m, MeshFactory.Cylinder(0.02f, 0.14f, 10, 1f), MaterialFactory.Glass, new Vector3(0f, 0f, 0.08f), new Vector3(90f, 0f, 0f));
                Part(m, MeshFactory.Cylinder(0.003f, 0.06f, 6, 1f), steel, new Vector3(0f, 0f, 0.18f), new Vector3(90f, 0f, 0f));
                Part(m, MeshFactory.Box(new Vector3(0.05f, 0.012f, 0.012f), 0f), MaterialFactory.PaintGreen, new Vector3(0f, 0f, 0.01f), Vector3.zero);
            }, 8, new Vector3(0f, 0f, 0.21f));
            models[9] = Model("Spawn", m => { Part(m, MeshFactory.Box(new Vector3(0.06f, 0.1f, 0.03f), 0f), MaterialFactory.PaintOrange, new Vector3(0f, 0f, 0.08f), new Vector3(-20f, 0f, 0f)); }, 9);
            models[10] = Model("Laser", m =>
            {
                Part(m, MeshFactory.Box(new Vector3(0.05f, 0.06f, 0.2f), 0f), MaterialFactory.DarkMetal, new Vector3(0f, 0.01f, 0.08f), Vector3.zero);
                Part(m, MeshFactory.Cylinder(0.018f, 0.08f, 10, 1f), MaterialFactory.Gold, new Vector3(0f, 0.015f, 0.21f), new Vector3(90f, 0f, 0f));
                Part(m, MeshFactory.Box(new Vector3(0.03f, 0.09f, 0.04f), 0f), MaterialFactory.DarkMetal, new Vector3(0f, -0.04f, 0.0f), new Vector3(15f, 0f, 0f));
                Part(m, MeshFactory.Sphere(0.012f, 6, 8), MaterialFactory.Glow, new Vector3(0f, 0.045f, 0.1f), Vector3.zero);
            }, 10, new Vector3(0f, 0.015f, 0.26f));
        }

        GameObject Model(string name, Action<Transform> build, int index, Vector3 muzzleLocal = default(Vector3))
        {
            var go = new GameObject(name);
            go.transform.SetParent(holder, false);
            build(go.transform);
            var mz = new GameObject("Muzzle").transform;
            mz.SetParent(go.transform, false);
            mz.localPosition = muzzleLocal == Vector3.zero ? new Vector3(0f, 0f, 0.2f) : muzzleLocal;
            muzzles[index] = mz;
            go.SetActive(false);
            return go;
        }

        static GameObject Part(Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 euler)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public void Show(ToolId t)
        {
            for (int i = 0; i < models.Length; i++) if (models[i] != null) models[i].SetActive(i == (int)t && Sandbox.Boot.tools.viewModels);
            muzzle = muzzles[(int)t];
        }

        public void Recoil(float k) { recoil = Mathf.Max(recoil, k); }
        public void Swing(float k) { swing = Mathf.Max(swing, k); }
        public void Throw() { throwT = 1f; }

        public void LateTick(ToolId t, bool grabbing, bool bladeHeld)
        {
            if (holder == null) return;
            bool visible = Sandbox.Cam != null && Sandbox.Cam.Mode == CameraMode.FirstPerson && Sandbox.Boot.tools.viewModels;
            if (holder.gameObject.activeSelf != visible) holder.gameObject.SetActive(visible);
            if (!visible) return;
            float dt = Time.unscaledDeltaTime;
            recoil = Mathf.MoveTowards(recoil, 0f, dt * 6f);
            swing = Mathf.MoveTowards(swing, 0f, dt * 4f);
            throwT = Mathf.MoveTowards(throwT, 0f, dt * 3f);
            float speed = Sandbox.Player != null ? Sandbox.Player.PlanarSpeed : 0f;
            bob += dt * (2f + speed * 1.6f);
            Vector3 p = basePos + new Vector3(Mathf.Sin(bob) * 0.006f, Mathf.Abs(Mathf.Cos(bob)) * 0.008f, 0f) * Mathf.Clamp01(speed / 3f + 0.2f);
            p += new Vector3(0f, recoil * 0.015f, -recoil * 0.05f);
            p += new Vector3(-swing * 0.08f, swing * 0.04f, swing * 0.1f);
            p += new Vector3(0f, throwT * 0.08f, throwT * 0.1f);
            if (grabbing) p += new Vector3(0f, 0.02f, 0.05f);
            holder.localPosition = p;
            holder.localRotation = Quaternion.Euler(-recoil * 12f + swing * 35f - throwT * 30f + (bladeHeld ? 15f : 0f), -swing * 25f, swing * 20f);
        }
    }
    #endregion
}
