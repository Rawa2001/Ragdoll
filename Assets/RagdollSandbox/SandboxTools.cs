// Ragdoll Sandbox - ToolSystem (hotbar 1-9/0), GrabController (physics spring to the exact hit point, throwing,
// strong pull, pinning), rope tool, hammer, blade slicing, pistol/shotgun hitscan with entry/exit wounds,
// explosives, prop spawner, shock/defibrillator, heal/revive, ragdoll spawning, Ctrl+click walk orders and
// simple procedural first-person view models.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RagdollSandbox
{
    #region ToolSystem
    public enum ToolId { Grab, Hammer, Blade, Pistol, Shotgun, Explosives, Props, Shock, Heal, Spawn }

    public class ToolSystem
    {
        public static readonly string[] ToolNames = { "Grab", "Hammer", "Blade", "Pistol", "Shotgun", "Explosives", "Props", "Shock", "Heal", "Spawn Ragdoll" };
        public static readonly string[] ToolHelp =
        {
            "LMB drag: grab with a physics spring. Wheel: distance. Shift: strong pull (rips limbs). RMB: pin. Q: rope mode.",
            "LMB: punch. RMB: heavy smash (fractures).",
            "Hold LMB and sweep across a limb to slice it. Fast sweeps sever. RMB: stab.",
            "LMB: fire. Bullets leave entry and exit wounds and can pass through limbs.",
            "LMB: fire a spread of pellets.",
            "LMB: throw/place. RMB or B: detonate C4. Q: cycle grenade/TNT/C4/barrel/landmine/mega.",
            "LMB: spawn at the crosshair. RMB: throw. Q: cycle crate/ball/barrel/anvil/plank/boulder.",
            "Hold LMB on a body: electrocute (spasms). RMB on the chest: defibrillate.",
            "LMB: heal a part (wounds, fractures, revive if recently dead). RMB: fully restore that ragdoll.",
            "LMB: spawn a ragdoll at the crosshair. RMB: remove the targeted ragdoll."
        };
        public static readonly string[] ExplosiveNames = { "Grenade", "TNT", "C4", "Explosive barrel", "Landmine", "Mega blast" };
        public static readonly string[] PropNames = { "Crate", "Ball", "Steel barrel", "Anvil", "Plank", "Boulder" };

        public ToolId Current = ToolId.Grab;
        public int ExplosiveIndex, PropIndex, GrabMode;
        public GrabController Grab;
        public RopeTool Rope;
        ViewModels view;
        Transform root;
        readonly List<GameObject> spawned = new List<GameObject>(128);
        readonly RaycastHit[] hits = new RaycastHit[32];
        float cooldown;
        Rng rng = new Rng(4321);

        // hover
        public bool HasHover;
        public RaycastHit Hover;
        public BodyPart HoverPart;
        BodyPart highlighted;

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

        public void Build(Transform parent)
        {
            root = new GameObject("Tools").transform;
            root.SetParent(parent, false);
            Grab = new GrabController(root);
            Rope = new RopeTool(root);
            view = new ViewModels();
            view.Build(Sandbox.MainCamera.transform);
            view.Show(Current);
        }

        public bool Blocked
        {
            get
            {
                if (TimeControl.Paused || InputBridge.GameplayBlocked) return true;
                if (!CursorControl.Locked) return true;
                return false;
            }
        }

        public Ray AimRay { get { return Sandbox.Cam != null ? Sandbox.Cam.CenterRay() : new Ray(Vector3.zero, Vector3.forward); } }

        public void Select(ToolId t)
        {
            if (t == Current) { CycleSub(); return; }
            if (Current == ToolId.Grab && t != ToolId.Grab) Grab.Release(false);
            StopShock();
            bladeTracking = false;
            Current = t;
            if (view != null) view.Show(t);
            if (Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Click, 0.4f, 1.2f);
        }

        void CycleSub()
        {
            switch (Current)
            {
                case ToolId.Explosives: ExplosiveIndex = (ExplosiveIndex + 1) % ExplosiveNames.Length; SandboxEvents.RaiseToast(ExplosiveNames[ExplosiveIndex], Color.white); break;
                case ToolId.Props: PropIndex = (PropIndex + 1) % PropNames.Length; SandboxEvents.RaiseToast(PropNames[PropIndex], Color.white); break;
                case ToolId.Grab: GrabMode = 1 - GrabMode; Grab.Release(false); Rope.CancelPending(); SandboxEvents.RaiseToast(GrabMode == 0 ? "Grab" : "Rope", Color.white); break;
            }
            if (view != null) view.Show(Current);
        }

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
                    case ToolId.Explosives: subName = ExplosiveNames[ExplosiveIndex] + "  [Q]"; break;
                    case ToolId.Props: subName = PropNames[PropIndex] + "  [Q]"; break;
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

        public void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            if (cooldown > 0f) cooldown -= Time.deltaTime;
            UpdateHover();
            bool possess = Sandbox.Possessed != null;
            if (Blocked || possess)
            {
                if (Grab.Active && !InputBridge.Mouse(0) && !Grab.Pinned) Grab.Release(true);
                StopShock();
                if (Sandbox.Hud != null) Sandbox.Hud.SetTool((int)Current, SubName);
                return;
            }
            int digit = InputBridge.DigitDown();
            if (digit > 0) Select((ToolId)(digit - 1));
            if (InputBridge.Down(KeyId.Q)) CycleSub();
            float scroll = InputBridge.Scroll;
            if (Mathf.Abs(scroll) > 0.01f && !Grab.Active)
            {
                int next = ((int)Current + (scroll < 0f ? 1 : -1) + 10) % 10;
                Select((ToolId)next);
            }
            // Ctrl + click: command the focused ragdoll to walk there
            if (InputBridge.Ctrl && InputBridge.MouseDown(0))
            {
                if (HasHover)
                {
                    var b = Sandbox.Focus;
                    if (b != null && b.Alive) { b.Brain.CommandWalkTo(Hover.point); SandboxEvents.RaiseToast("Ragdoll " + (b.Index + 1) + ": walking there", new Color(0.7f, 0.9f, 1f)); }
                }
                return;
            }
            switch (Current)
            {
                case ToolId.Grab: TickGrab(scroll); break;
                case ToolId.Hammer: TickHammer(); break;
                case ToolId.Blade: TickBlade(); break;
                case ToolId.Pistol: if (InputBridge.MouseDown(0) && cooldown <= 0f) { cooldown = 0.16f; FireGun(1, 0f, Sandbox.Boot.damage.pistolDamage, Sandbox.Boot.tools.pistolImpulse, 3); view.Recoil(0.6f); } break;
                case ToolId.Shotgun:
                    if (InputBridge.MouseDown(0) && cooldown <= 0f)
                    {
                        cooldown = 0.85f;
                        var ts = Sandbox.Boot.tools;
                        FireGun(ts.shotgunPellets, ts.shotgunSpread, Sandbox.Boot.damage.shotgunPelletDamage, ts.shotgunPelletImpulse, 1);
                        view.Recoil(1.4f);
                    }
                    break;
                case ToolId.Explosives: TickExplosives(); break;
                case ToolId.Props: TickProps(); break;
                case ToolId.Shock: TickShock(); break;
                case ToolId.Heal: TickHeal(); break;
                case ToolId.Spawn: TickSpawn(); break;
            }
            if (Sandbox.Hud != null) Sandbox.Hud.SetTool((int)Current, SubName);
        }

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
                    d.fromPlayer = true;
                    shockPart.ApplyDamage(d);
                    shockPart.body.Muscles.Spasm(1.4f);
                    if (shockPart.rb != null) shockPart.rb.AddForce(rng.OnUnitSphere() * 15f, ForceMode.Impulse);
                }
            }
        }

        public void LateTick()
        {
            Grab.LateTick();
            Rope.LateTick();
            if (view != null) view.LateTick(Current, Grab.Active, bladeTracking || InputBridge.Mouse(0) && Current == ToolId.Blade);
            if (shockPart != null && Sandbox.Fx != null && Time.frameCount % 2 == 0)
            {
                Vector3 target = shockPart.transform.TransformPoint(shockLocal);
                Sandbox.Fx.Lightning(view.Muzzle, target, new Color(0.6f, 0.85f, 1f, 1f), 0.025f, 0.06f);
                Sandbox.Fx.ElectricSparks(target, (Sandbox.MainCamera.transform.position - target).normalized, 2);
                if (shockLoop >= 0) Sandbox.Audio.SetLoopPosition(shockLoop, target);
            }
        }

        void UpdateHover()
        {
            HasHover = RaycastFirst(AimRay, Sandbox.Boot.tools.maxRange, out Hover);
            HoverPart = HasHover ? Registry.PartOf(Hover.collider) : null;
            BodyPart show = Current == ToolId.Grab && Grab.Active ? Grab.Part : HoverPart;
            if (show != highlighted)
            {
                if (highlighted != null && highlighted.visual != null) highlighted.visual.SetHighlight(0f);
                highlighted = show;
                if (highlighted != null && highlighted.visual != null) highlighted.visual.SetHighlight(1f);
            }
            if (HoverPart != null && HoverPart.body != null) { Sandbox.Hovered = HoverPart.body; Sandbox.HoveredTime = Time.unscaledTime; }
            if (Sandbox.Hud == null) return;
            if (show != null)
            {
                int pct = Mathf.RoundToInt(show.HealthFraction * 100f);
                int key = pct * 8 + (show.attached ? 1 : 0) + (show.body.Alive ? 2 : 0) + (show.fractured ? 4 : 0);
                if (hoverObj != (object)show || key != hoverKey)
                {
                    hoverObj = show; hoverKey = key;
                    string state = show.attached ? (show.body.Alive ? "" : "  (dead)") : "  (severed)";
                    string frac = show.fractured ? "  fractured" : "";
                    hoverLabel = show.DisplayName + "  " + pct + "%" + frac + state + "   Ragdoll " + (show.body.Index + 1);
                }
                Sandbox.Hud.SetHover(hoverLabel, Color.Lerp(new Color(1f, 0.35f, 0.3f), new Color(0.6f, 1f, 0.6f), show.HealthFraction));
            }
            else if (HasHover)
            {
                Organ o;
                if (Registry.Organs.TryGetValue(Hover.collider, out o))
                {
                    int key = Mathf.RoundToInt(o.health);
                    if (hoverObj != (object)o || key != hoverKey) { hoverObj = o; hoverKey = key; hoverLabel = o.DisplayName + "  " + key + "%"; }
                    Sandbox.Hud.SetHover(hoverLabel, new Color(1f, 0.6f, 0.6f));
                }
                else if (Registry.DamageableOf(Hover.collider) is ExplosiveProp) Sandbox.Hud.SetHover("Explosive", new Color(1f, 0.6f, 0.3f));
                else Sandbox.Hud.SetHover("", Color.white);
            }
            else Sandbox.Hud.SetHover("", Color.white);
        }

        /// <summary>Nearest non-trigger hit that isn't the player's own controller.</summary>
        public bool RaycastFirst(Ray ray, float range, out RaycastHit best)
        {
            best = new RaycastHit();
            int n = Physics.RaycastNonAlloc(ray, hits, range, ~0, QueryTriggerInteraction.Ignore);
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

        #region Grab and rope
        void TickGrab(float scroll)
        {
            var ts = Sandbox.Boot.tools;
            if (GrabMode == 1)
            {
                if (InputBridge.MouseDown(0) && HasHover) Rope.Click(Hover);
                if (InputBridge.MouseDown(1)) Rope.ClearAll();
                return;
            }
            if (!Grab.Active)
            {
                if (InputBridge.MouseDown(0) && HasHover && Hover.rigidbody != null && !Hover.rigidbody.isKinematic)
                    Grab.Begin(Hover.rigidbody, Hover.point, Hover.distance);
                return;
            }
            if (Mathf.Abs(scroll) > 0.01f) Grab.Distance = Mathf.Clamp(Grab.Distance + scroll * 0.6f, ts.grabMinDistance, ts.grabMaxDistance);
            if (InputBridge.MouseDown(1)) { Grab.Pinned = !Grab.Pinned; SandboxEvents.RaiseToast(Grab.Pinned ? "Pinned in place" : "Unpinned", Color.white); if (!Grab.Pinned && !InputBridge.Mouse(0)) Grab.Release(false); }
            Grab.Strong = InputBridge.Shift;
            if (!Grab.Pinned) Grab.SetRay(AimRay);
            if (InputBridge.MouseUp(0) && !Grab.Pinned) Grab.Release(true);
        }

        public void ReleaseFrom(RagdollBody b)
        {
            if (Grab.Active && Grab.Part != null && Grab.Part.body == b) Grab.Release(false);
            if (shockPart != null && shockPart.body == b) StopShock();
            if (bladePart != null && bladePart.body == b) { bladePart = null; bladeTracking = false; }
            if (highlighted != null && highlighted.body == b) highlighted = null;
            Rope.RemoveFor(b);
        }

        public void ReleaseAll()
        {
            Grab.Release(false);
            StopShock();
            Rope.ClearAll();
            highlighted = null;
        }
        #endregion

        #region Hammer
        void TickHammer()
        {
            bool light = InputBridge.MouseDown(0), heavy = InputBridge.MouseDown(1);
            if ((!light && !heavy) || cooldown > 0f) return;
            var ts = Sandbox.Boot.tools;
            cooldown = heavy ? 0.6f : 0.25f;
            view.Swing(heavy ? 1.3f : 0.8f);
            if (!HasHover || Hover.distance > 3.2f) { Sandbox.Audio.Play(Sfx.Whoosh, Sandbox.MainCamera.transform.position, 0.4f, 1.2f); return; }
            float impulse = heavy ? ts.heavyHammerImpulse : ts.hammerImpulse;
            Vector3 dir = AimRay.direction;
            if (Hover.rigidbody != null && !Hover.rigidbody.isKinematic) Hover.rigidbody.AddForceAtPosition(dir * impulse, Hover.point, ForceMode.Impulse);
            IDamageable dmg = Registry.DamageableOf(Hover.collider);
            if (dmg != null)
            {
                var d = DamageInfo.Make(DamageType.Blunt, impulse * 0.65f, Hover.point, dir);
                d.normal = Hover.normal;
                d.impulse = impulse * (heavy ? 1.6f : 1f);
                d.fromPlayer = true;
                dmg.ApplyDamage(d);
                var part = dmg as BodyPart;
                if (part != null && part.body != null) part.body.Brain.OnImpact(part, impulse * 1.5f, dir);
            }
            Sandbox.Audio.PlayRandomThud(Hover.point, heavy ? 1f : 0.7f, heavy ? 0.8f : 1f);
            if (HoverPart == null) Sandbox.Fx.Impact(Hover.point, Hover.normal, IsMetal(Hover.collider), IsWood(Hover.collider));
            if (Sandbox.Cam != null) Sandbox.Cam.AddShake(heavy ? 0.12f : 0.05f);
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
        #endregion

        #region Blade
        void TickBlade()
        {
            var ds = Sandbox.Boot.damage;
            if (InputBridge.MouseDown(1) && cooldown <= 0f && HasHover && Hover.distance < 2.6f)
            {
                cooldown = 0.4f;
                view.Swing(1f);
                IDamageable dmg = Registry.DamageableOf(Hover.collider);
                if (dmg != null)
                {
                    var d = DamageInfo.Make(DamageType.Pierce, ds.bladeDamage * 0.9f, Hover.point, AimRay.direction);
                    d.normal = Hover.normal; d.depth = 0.08f; d.fromPlayer = true;
                    dmg.ApplyDamage(d);
                    Sandbox.Audio.Play(Sfx.Stab, Hover.point, 0.9f, rng.Range(0.9f, 1.1f));
                }
                return;
            }
            bool held = InputBridge.Mouse(0);
            Ray ray = AimRay;
            BodyPart under = HasHover && Hover.distance < 2.8f ? HoverPart : null;
            if (!held)
            {
                if (bladeTracking) FinishSlash(ray.direction);
                return;
            }
            if (InputBridge.MouseDown(0)) { view.Swing(0.6f); Sandbox.Audio.Play(Sfx.Whoosh, Sandbox.MainCamera.transform.position, 0.3f, 1.4f); }
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
                else
                {
                    // slashing props and organs
                    if (HasHover && Hover.distance < 2.8f && cooldown <= 0f)
                    {
                        var dmg = Registry.DamageableOf(Hover.collider);
                        Organ o;
                        if (Registry.Organs.TryGetValue(Hover.collider, out o) && o.tether != null) { UnityEngine.Object.Destroy(o.tether); cooldown = 0.2f; }
                        else if (dmg is BreakablePlank) { dmg.ApplyDamage(DamageInfo.Make(DamageType.Cut, 40f, Hover.point, ray.direction)); cooldown = 0.2f; }
                    }
                }
                return;
            }
            // leaving the part, or swept far enough -> resolve the slash
            if (under != bladePart || Vector3.Angle(bladeStartDir, ray.direction) > 14f) FinishSlash(ray.direction);
        }

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
            Vector3 camPos = Sandbox.MainCamera.transform.position;
            Vector3 n = Vector3.Cross(bladeStartDir, endDir);
            Vector3 point = bladeEntry;
            if (n.sqrMagnitude < 1e-6f || sweep < 1.5f)
            {
                var stab = DamageInfo.Make(DamageType.Cut, ds.bladeDamage * 0.5f, point, bladeStartDir);
                stab.depth = 0.02f; stab.radius = 0.02f; stab.fromPlayer = true;
                part.ApplyDamage(stab);
                Sandbox.Audio.Play(Sfx.Squelch1, point, 0.6f, 1.2f);
                return;
            }
            n.Normalize();
            float strength = Mathf.Clamp01(speed / Mathf.Max(1f, Sandbox.Boot.dismember.bladeSeverSpeed));
            var d = DamageInfo.Make(DamageType.Cut, ds.bladeDamage * Mathf.Lerp(0.5f, 1.3f, strength), point, (point - camPos).normalized);
            d.depth = Mathf.Lerp(0.015f, 0.06f, strength) * part.body.Scale;
            d.radius = 0.05f * part.body.Scale;
            d.fromPlayer = true;
            part.ApplyDamage(d);
            Sandbox.Audio.Play(Sfx.Slice, point, 0.9f, rng.Range(0.9f, 1.1f));
            if (Sandbox.Fx != null) Sandbox.Fx.EmitBloodSpray(point, Vector3.Cross(n, (point - camPos).normalized).normalized, 3f, 30f, 10, 0.02f, Sandbox.Boot.blood.arterial, Vector3.zero);
            if (speed >= Sandbox.Boot.dismember.bladeSeverSpeed && Sandbox.Gore == GoreLevel.Full)
                part.body.Dismember.Slice(part, point, n);
        }
        #endregion

        #region Guns
        void FireGun(int pellets, float spreadDeg, float damage, float impulse, int maxPenetrations)
        {
            Ray aim = AimRay;
            Vector3 muzzle = view.Muzzle;
            Sandbox.Audio.Play(pellets > 1 ? Sfx.Shotgun : Sfx.Gunshot, muzzle, 1f, rng.Range(0.95f, 1.05f), 3f);
            Sandbox.Fx.MuzzleFlash(muzzle, aim.direction, pellets > 1 ? 1.6f : 1f);
            if (Sandbox.Cam != null) { Sandbox.Cam.AddRecoil(pellets > 1 ? 3.5f : 1.6f); Sandbox.Cam.AddShake(pellets > 1 ? 0.08f : 0.03f); }
            for (int p = 0; p < pellets; p++)
            {
                Vector3 dir = aim.direction;
                if (spreadDeg > 0f)
                {
                    Vector2 r = rng.InsideUnitCircle() * Mathf.Tan(spreadDeg * Mathf.Deg2Rad);
                    Transform ct = Sandbox.MainCamera.transform;
                    dir = (dir + ct.right * r.x + ct.up * r.y).normalized;
                }
                Bullet(new Ray(aim.origin, dir), muzzle, damage, impulse, maxPenetrations);
            }
        }

        void Bullet(Ray ray, Vector3 muzzle, float damage, float impulse, int maxPen)
        {
            float range = Sandbox.Boot.tools.maxRange;
            int n = Physics.RaycastNonAlloc(ray, hits, range, ~0, QueryTriggerInteraction.Ignore);
            SortHits(n);
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
                    d.impulse = impulse * energy;
                    d.fromPlayer = true;
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
                    d.fromPlayer = true;
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
        }

        void SortHits(int n)
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
            if (InputBridge.MouseDown(1)) { Sandbox.Explosions.DetonateAllC4(); return; }
            if (!InputBridge.MouseDown(0) || cooldown > 0f) return;
            cooldown = 0.35f;
            var kind = (ExplosiveKind)ExplosiveIndex;
            Ray r = AimRay;
            Transform ct = Sandbox.MainCamera.transform;
            float speed = Sandbox.Boot.tools.throwSpeed;
            view.Throw();
            switch (kind)
            {
                case ExplosiveKind.Grenade:
                case ExplosiveKind.TNT:
                case ExplosiveKind.C4:
                    {
                        Vector3 start = ct.position + ct.forward * 0.6f + ct.right * 0.15f - ct.up * 0.1f;
                        var ep = Sandbox.Explosions.Spawn(kind, start, Quaternion.LookRotation(ct.forward), r.direction * speed * (kind == ExplosiveKind.C4 ? 0.9f : 0.7f) + Vector3.up * 2f);
                        if (kind == ExplosiveKind.TNT) ep.LightFuse(4f);
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
            bool place = InputBridge.MouseDown(0), throwIt = InputBridge.MouseDown(1);
            if ((!place && !throwIt) || cooldown > 0f) return;
            cooldown = 0.2f;
            Transform ct = Sandbox.MainCamera.transform;
            Vector3 pos;
            Vector3 vel = Vector3.zero;
            if (throwIt) { pos = ct.position + ct.forward * 1.2f; vel = ct.forward * Sandbox.Boot.tools.throwSpeed; view.Throw(); }
            else if (HasHover) pos = Hover.point + Hover.normal * 0.6f;
            else pos = ct.position + ct.forward * 4f;
            var go = MakeProp(PropIndex, pos, Quaternion.Euler(0f, ct.eulerAngles.y, 0f));
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) Compat.SetVelocity(rb, vel);
            spawned.Add(go);
            while (spawned.Count > Sandbox.Boot.tools.propCap) { if (spawned[0] != null) UnityEngine.Object.Destroy(spawned[0]); spawned.RemoveAt(0); }
        }

        GameObject MakeProp(int index, Vector3 pos, Quaternion rot)
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
                        top.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(new Vector3(0.85f, 0.16f, 0.32f), 0f);
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
                        var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = mesh; mc.convex = true; mc.sharedMaterial = PhysMats.Stone;
                        var rb = go.AddComponent<Rigidbody>(); rb.mass = 250f; rb.interpolation = RigidbodyInterpolation.Interpolate;
                        go.AddComponent<PropImpact>().sound = Sfx.Thump;
                        return go;
                    }
            }
        }

        public void ClearSpawned()
        {
            for (int i = 0; i < spawned.Count; i++) if (spawned[i] != null) UnityEngine.Object.Destroy(spawned[i]);
            spawned.Clear();
        }
        #endregion

        #region Shock, heal, spawn
        void TickShock()
        {
            if (InputBridge.MouseDown(1) && HasHover && HoverPart != null && HoverPart.body != null)
            {
                var b = HoverPart.body;
                Sandbox.Audio.Play(Sfx.Zap, Hover.point, 1f, 0.7f);
                Sandbox.Fx.Lightning(view.Muzzle, Hover.point, new Color(0.8f, 0.9f, 1f, 1f), 0.05f, 0.15f);
                b.Muscles.Spasm(1.5f);
                if (b.Vitals.CanRevive || (b.Vitals.CardiacArrest && !b.Vitals.Dead && b.Organs.HeartHealth01 > 0f))
                {
                    bool wasDead = b.Vitals.Dead;
                    b.Vitals.CardiacArrest = false;
                    if (wasDead) b.Vitals.Revive(false);
                    SandboxEvents.RaiseToast(wasDead ? "Defibrillated - revived!" : "Heart restarted", new Color(0.6f, 1f, 0.7f));
                }
                return;
            }
            bool held = InputBridge.Mouse(0);
            if (!held) { StopShock(); return; }
            if (shockPart == null && HasHover && HoverPart != null && Hover.distance < 6f)
            {
                shockPart = HoverPart;
                shockLocal = shockPart.transform.InverseTransformPoint(Hover.point);
                if (shockLoop < 0) shockLoop = Sandbox.Audio.StartLoop(Sfx.Buzz, null, 0.6f, 1.2f, 600);
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
            if (InputBridge.MouseDown(0))
            {
                b.Damage.Heal(HoverPart);
                if (b.Vitals.CanRevive) { b.Vitals.Revive(false); SandboxEvents.RaiseToast("Revived", new Color(0.6f, 1f, 0.7f)); }
                else SandboxEvents.RaiseToast(HoverPart.DisplayName + " healed", new Color(0.6f, 1f, 0.7f));
                b.Vitals.BloodVolume = Mathf.Min(b.Vitals.MaxBlood, b.Vitals.BloodVolume + 0.4f);
                Sandbox.Audio.Play2D(Sfx.Beep, 0.4f, 0.8f);
            }
            else if (InputBridge.MouseDown(1))
            {
                Vector3 p = b.Pelvis != null ? b.Pelvis.rb.position : b.transform.position;
                float y = b.Balance.HasGround ? b.Balance.GroundHeight : p.y - 0.9f;
                float yaw = b.Locomotion.HeadingYaw;
                bool possessed = b.Possessed;
                Sandbox.Boot.RemoveRagdoll(b);
                var nb = Sandbox.Boot.SpawnRagdoll(new Vector3(p.x, y + 0.02f, p.z), yaw);
                if (possessed && nb != null) { Sandbox.Possessed = nb; nb.SetPossessed(true); Sandbox.Cam.SetMode(CameraMode.Possess); }
                SandboxEvents.RaiseToast("Ragdoll fully restored", new Color(0.6f, 1f, 0.7f));
            }
        }

        void TickSpawn()
        {
            if (InputBridge.MouseDown(0) && HasHover && Hover.normal.y > 0.5f)
            {
                Vector3 toCam = MathUtil.Flat(Sandbox.MainCamera.transform.position - Hover.point);
                var b = Sandbox.Boot.SpawnRagdoll(Hover.point + Vector3.up * 0.03f, MathUtil.Yaw(toCam.sqrMagnitude > 0.01f ? toCam : Vector3.forward));
                if (b != null) SandboxEvents.RaiseToast("Spawned ragdoll " + (b.Index + 1), new Color(0.7f, 0.9f, 1f));
            }
            else if (InputBridge.MouseDown(1))
            {
                RagdollBody target = HoverPart != null ? HoverPart.body : null;
                if (target == null && Sandbox.Ragdolls.Count > 0) target = Sandbox.Ragdolls[Sandbox.Ragdolls.Count - 1];
                if (target != null) { Sandbox.Boot.RemoveRagdoll(target); SandboxEvents.RaiseToast("Ragdoll removed", Color.white); }
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
        readonly GameObject[] models = new GameObject[10];
        Transform muzzle;
        readonly Transform[] muzzles = new Transform[10];
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
