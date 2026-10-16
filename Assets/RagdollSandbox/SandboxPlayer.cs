// Ragdoll Sandbox - first-person player (CharacterController: walk, sprint, crouch, jump, push props, ride
// moving platforms) and the CameraRig (first person, follow cam, possess orbit cam, kill-cam, shake, FOV kick,
// recoil, dizziness when the possessed body is hurt). Also feeds possession input to the possessed ragdoll.
using System;
using UnityEngine;

namespace RagdollSandbox
{
    #region PlayerController
    [DefaultExecutionOrder(-100)]
    public class PlayerController : MonoBehaviour
    {
        CharacterController cc;
        public float Yaw, Pitch;
        Vector3 velocity;
        Vector3 external;
        bool grounded;
        float crouch;
        float stepTimer;
        Vector3 platformVel;
        readonly RaycastHit[] groundHits = new RaycastHit[8];

        public Collider Collider { get { return cc; } }
        public float PlanarSpeed { get { return new Vector2(velocity.x, velocity.z).magnitude; } }
        public Vector3 EyePosition { get { return transform.position + Vector3.up * (cc.height - 0.12f); } }
        public bool Grounded { get { return grounded; } }

        public static PlayerController Create(Transform parent, Vector3 pos, float yaw)
        {
            var go = new GameObject("Player");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var p = go.AddComponent<PlayerController>();
            p.Init(yaw);
            return p;
        }

        void Init(float yaw)
        {
            var ps = Sandbox.Boot.player;
            cc = gameObject.AddComponent<CharacterController>();
            cc.height = ps.height;
            cc.radius = 0.32f;
            cc.center = new Vector3(0f, ps.height * 0.5f, 0f);
            cc.stepOffset = 0.38f;
            cc.slopeLimit = 52f;
            cc.skinWidth = 0.03f;
            cc.minMoveDistance = 0f;
            cc.sharedMaterial = PhysMats.Player;
            Yaw = yaw;
            Pitch = 8f;
        }

        public void Tick()
        {
            if (cc == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var ps = Sandbox.Boot.player;
            var cam = Sandbox.Cam;
            bool possess = Sandbox.Possessed != null;
            bool canLook = CursorControl.Locked && !TimeControl.Paused;
            if (possess)
            {
                FeedPossessed();
                ApplyGravityOnly(dt);
                return;
            }
            if (cam != null && cam.Mode == CameraMode.Follow)
            {
                ApplyGravityOnly(dt);
                return;
            }
            if (canLook)
            {
                Vector2 md = InputBridge.MouseDelta;
                Yaw += md.x * 2.2f;
                Pitch = Mathf.Clamp(Pitch - md.y * 2.2f, -88f, 88f);
            }
            if (cam != null) Pitch = Mathf.Clamp(Pitch - cam.ConsumeRecoil(), -88f, 88f);
            bool blocked = TimeControl.Paused || InputBridge.GameplayBlocked;
            Vector2 move = Vector2.zero;
            if (!blocked)
            {
                if (InputBridge.Held(KeyId.W)) move.y += 1f;
                if (InputBridge.Held(KeyId.S)) move.y -= 1f;
                if (InputBridge.Held(KeyId.D)) move.x += 1f;
                if (InputBridge.Held(KeyId.A)) move.x -= 1f;
            }
            if (move.sqrMagnitude > 1f) move.Normalize();
            bool sprint = !blocked && InputBridge.Shift && move.y > 0.1f && !Sandbox.Tools.Grab.Active;
            bool wantCrouch = !blocked && InputBridge.Held(KeyId.C);
            if (ps.noclip) { Noclip(move, sprint, dt, blocked); return; }
            UpdateCrouch(wantCrouch, dt, ps);
            float speed = crouch > 0.5f ? ps.crouchSpeed : sprint ? ps.sprintSpeed : ps.walkSpeed;
            Quaternion yawRot = Quaternion.Euler(0f, Yaw, 0f);
            Vector3 wish = yawRot * new Vector3(move.x, 0f, move.y) * speed;
            float accel = grounded ? ps.acceleration : ps.acceleration * 0.3f;
            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            planar = Vector3.MoveTowards(planar, wish, accel * speed * dt);
            velocity.x = planar.x; velocity.z = planar.z;
            if (grounded)
            {
                if (velocity.y < 0f) velocity.y = -2f;
                if (!blocked && InputBridge.Down(KeyId.Space)) { velocity.y = ps.jumpSpeed; grounded = false; }
            }
            velocity += Physics.gravity * dt;
            if (velocity.y < -55f) velocity.y = -55f;
            external = Vector3.MoveTowards(external, Vector3.zero, (grounded ? 14f : 3f) * dt);
            Vector3 motion = (velocity + external + platformVel) * dt;
            CollisionFlags flags = cc.Move(motion);
            grounded = (flags & CollisionFlags.Below) != 0 || cc.isGrounded;
            if ((flags & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;
            UpdatePlatform();
            if (grounded && planar.magnitude > 0.5f)
            {
                stepTimer -= dt * planar.magnitude;
                if (stepTimer <= 0f) { stepTimer = 1.9f; Sandbox.Audio.Play(Sfx.Footstep, transform.position, 0.25f, UnityEngine.Random.Range(0.9f, 1.1f), 1f); }
            }
            if (transform.position.y < Sandbox.Boot.world.killPlaneY) Teleport(Sandbox.World.PlayerSpawn);
        }

        void ApplyGravityOnly(float dt)
        {
            velocity.x = 0f; velocity.z = 0f;
            if (grounded && velocity.y < 0f) velocity.y = -2f;
            velocity += Physics.gravity * dt;
            external = Vector3.MoveTowards(external, Vector3.zero, 10f * dt);
            CollisionFlags flags = cc.Move((velocity + external + platformVel) * dt);
            grounded = (flags & CollisionFlags.Below) != 0 || cc.isGrounded;
            UpdatePlatform();
        }

        void Noclip(Vector2 move, bool fast, float dt, bool blocked)
        {
            Quaternion look = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 v = look * new Vector3(move.x, 0f, move.y);
            if (!blocked && InputBridge.Held(KeyId.Space)) v += Vector3.up;
            if (!blocked && InputBridge.Held(KeyId.C)) v += Vector3.down;
            float sp = fast ? 16f : 7f;
            cc.enabled = false;
            transform.position += v * sp * dt;
            cc.enabled = true;
            velocity = Vector3.zero;
        }

        void UpdateCrouch(bool want, float dt, PlayerSettings ps)
        {
            float target = want ? 1f : 0f;
            if (!want && crouch > 0.01f)
            {
                // only stand up if there is headroom
                Vector3 bottom = transform.position + Vector3.up * (cc.radius + 0.05f);
                if (Physics.SphereCast(bottom, cc.radius * 0.9f, Vector3.up, out RaycastHit h, ps.height - cc.radius * 2f, ~0, QueryTriggerInteraction.Ignore) && h.collider != cc) target = crouch;
            }
            crouch = Mathf.MoveTowards(crouch, target, dt * 6f);
            float hgt = Mathf.Lerp(ps.height, ps.height * 0.58f, crouch);
            cc.height = hgt;
            cc.center = new Vector3(0f, hgt * 0.5f, 0f);
        }

        void UpdatePlatform()
        {
            platformVel = Vector3.zero;
            if (!grounded) return;
            Vector3 origin = transform.position + Vector3.up * (cc.radius + 0.05f);
            int n = Physics.SphereCastNonAlloc(origin, cc.radius * 0.9f, Vector3.down, groundHits, 0.3f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = groundHits[i].collider;
                if (c == cc) continue;
                IMovingSurface m;
                if (Registry.Movers.TryGetValue(c, out m))
                {
                    platformVel = m.GetSurfaceVelocity(groundHits[i].point);
                    if (platformVel.y < 0f) platformVel.y *= 1.05f;
                    break;
                }
            }
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody rb = hit.rigidbody;
            if (rb == null || rb.isKinematic) return;
            if (hit.moveDirection.y < -0.4f) return;
            Vector3 dir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            float speed = Mathf.Max(1f, PlanarSpeed);
            rb.AddForceAtPosition(dir * Sandbox.Boot.player.pushForce * speed * Time.deltaTime, hit.point, ForceMode.Impulse);
        }

        public void Teleport(Vector3 p)
        {
            cc.enabled = false;
            transform.position = p;
            cc.enabled = true;
            velocity = Vector3.zero;
            external = Vector3.zero;
        }

        public void Bounce(float speed) { velocity.y = Mathf.Max(velocity.y, speed); grounded = false; }

        public void Launch(Vector3 from, Vector3 vel)
        {
            Teleport(from);
            external = new Vector3(vel.x, 0f, vel.z);
            velocity.y = vel.y;
        }

        public void ExplosionPush(Vector3 pos, float radius, float force)
        {
            Vector3 c = transform.position + Vector3.up * 1f;
            Vector3 d = c - pos;
            float dist = d.magnitude;
            if (dist > radius * 1.3f) return;
            float k = 1f - dist / (radius * 1.3f);
            Vector3 dir = (d.normalized + Vector3.up * 0.4f).normalized;
            Vector3 push = dir * Mathf.Min(16f, force / 90f * k);
            external += new Vector3(push.x, 0f, push.z);
            velocity.y = Mathf.Max(velocity.y, push.y);
        }

        /// <summary>Builds the possession input (camera-relative WASD, run, crouch, jump, arm reaches) for the possessed body.</summary>
        void FeedPossessed()
        {
            var body = Sandbox.Possessed;
            if (body == null || Sandbox.Cam == null) return;
            bool blocked = TimeControl.Paused || InputBridge.GameplayBlocked;
            Vector2 move = Vector2.zero;
            if (!blocked)
            {
                if (InputBridge.Held(KeyId.W)) move.y += 1f;
                if (InputBridge.Held(KeyId.S)) move.y -= 1f;
                if (InputBridge.Held(KeyId.D)) move.x += 1f;
                if (InputBridge.Held(KeyId.A)) move.x -= 1f;
            }
            if (move.sqrMagnitude > 1f) move.Normalize();
            float camYaw = Sandbox.Cam.OrbitYaw;
            Quaternion yr = Quaternion.Euler(0f, camYaw, 0f);
            var inp = body.Brain.Input;
            inp.move = yr * new Vector3(move.x, 0f, move.y);
            inp.camYaw = camYaw;
            inp.run = !blocked && InputBridge.Shift;
            inp.crouch = !blocked && InputBridge.Held(KeyId.C);
            inp.jump = inp.jump || (!blocked && InputBridge.Down(KeyId.Space));
            bool overUi = !CursorControl.Locked;
            inp.reachL = !blocked && !overUi && InputBridge.Mouse(0);
            inp.reachR = !blocked && !overUi && InputBridge.Mouse(1);
            Ray r = Sandbox.Cam.CenterRay();
            inp.aimDir = r.direction;
            RaycastHit hit;
            if (Sandbox.Tools != null && Sandbox.Tools.RaycastFirst(r, 40f, out hit))
            {
                BodyPart bp = Registry.PartOf(hit.collider);
                inp.aimPoint = bp != null && bp.body == body ? r.origin + r.direction * 6f : hit.point;
            }
            else inp.aimPoint = r.origin + r.direction * 20f;
            body.Brain.Input = inp;
        }
    }
    #endregion

    #region CameraRig
    public enum CameraMode { FirstPerson, Follow, Possess, KillCam }

    public class CameraRig
    {
        public Camera Cam;
        Transform t;
        public CameraMode Mode = CameraMode.FirstPerson;
        CameraMode beforeKill = CameraMode.FirstPerson;
        public float OrbitYaw, OrbitPitch = 15f;
        float orbitDist = 3.5f;
        Vector3 focus;
        bool focusInit;
        float trauma, fovKick, recoilPending, roll;
        RagdollBody killTarget;
        float killUntil;
        float noiseT;
        readonly RaycastHit[] hits = new RaycastHit[16];
        float heartTimer;

        public static CameraRig Create(Transform parent)
        {
            var rig = new CameraRig();
            var go = new GameObject("Main Camera");
            go.transform.SetParent(parent, false);
            try { go.tag = "MainCamera"; } catch (Exception) { }
            rig.Cam = go.AddComponent<Camera>();
            rig.Cam.nearClipPlane = 0.03f;
            rig.Cam.farClipPlane = 450f;
            rig.Cam.fieldOfView = Sandbox.Boot.player.fieldOfView;
            rig.Cam.allowHDR = true;
            rig.Cam.clearFlags = CameraClearFlags.Skybox;
            go.AddComponent<AudioListener>();
            rig.t = go.transform;
            Sandbox.MainCamera = rig.Cam;
            if (Sandbox.Player != null)
            {
                rig.t.position = Sandbox.Player.EyePosition;
                rig.t.rotation = Quaternion.Euler(Sandbox.Player.Pitch, Sandbox.Player.Yaw, 0f);
            }
            SandboxEvents.Death += rig.OnDeath;
            return rig;
        }

        void OnDeath(RagdollBody b)
        {
            var cs = Sandbox.Boot.cameraSettings;
            if (!cs.killCam || b == null) return;
            if (Mode == CameraMode.KillCam) return;
            if (Sandbox.MainCamera != null && (b.CenterOfMass - Sandbox.MainCamera.transform.position).magnitude > 30f) return;
            killTarget = b;
            beforeKill = Mode;
            Mode = CameraMode.KillCam;
            killUntil = Time.unscaledTime + cs.killCamDuration;
            TimeControl.KillCamUntil = killUntil;
            OrbitYaw = t.eulerAngles.y + 30f;
            OrbitPitch = 18f;
        }

        public void SetMode(CameraMode m)
        {
            if (m == CameraMode.Possess || m == CameraMode.Follow)
            {
                if (Mode == CameraMode.FirstPerson) { OrbitYaw = t.eulerAngles.y; OrbitPitch = 15f; }
                focusInit = false;
            }
            if (Mode == CameraMode.FirstPerson && m != CameraMode.FirstPerson && Sandbox.Player != null) { }
            if (m == CameraMode.FirstPerson && Sandbox.Player != null)
            {
                Sandbox.Player.Yaw = t.eulerAngles.y;
            }
            Mode = m;
            orbitDist = m == CameraMode.Possess ? Sandbox.Boot.cameraSettings.possessDistance : Sandbox.Boot.cameraSettings.followDistance;
        }

        public void ToggleFollow()
        {
            if (Sandbox.Possessed != null) return;
            if (Mode == CameraMode.Follow) { SetMode(CameraMode.FirstPerson); SandboxEvents.RaiseToast("First person", Color.white); }
            else if (Sandbox.Focus != null) { SetMode(CameraMode.Follow); SandboxEvents.RaiseToast("Follow cam (F to return)", Color.white); }
        }

        public void AddShake(float k) { trauma = Mathf.Clamp01(trauma + k * Sandbox.Boot.cameraSettings.shakeScale); }
        public void AddFovKick(float deg) { fovKick = Mathf.Max(fovKick, deg); }
        public void AddRecoil(float deg) { recoilPending += deg; }

        public float ConsumeRecoil()
        {
            float r = recoilPending * Mathf.Min(1f, Time.unscaledDeltaTime * 25f);
            recoilPending -= r;
            return r;
        }

        public Ray CenterRay() { return new Ray(t.position, t.forward); }

        public void LateTick()
        {
            float dt = Time.unscaledDeltaTime;
            var cs = Sandbox.Boot.cameraSettings;
            noiseT += dt;
            trauma = Mathf.MoveTowards(trauma, 0f, dt * 1.4f);
            fovKick = Mathf.MoveTowards(fovKick, 0f, dt * 22f);
            if (Mode == CameraMode.KillCam && (Time.unscaledTime > killUntil || killTarget == null))
            {
                Mode = beforeKill == CameraMode.KillCam ? CameraMode.FirstPerson : beforeKill;
                if (Mode == CameraMode.Possess && Sandbox.Possessed == null) Mode = CameraMode.FirstPerson;
                killTarget = null;
            }
            if (Mode == CameraMode.Possess && Sandbox.Possessed == null) Mode = CameraMode.FirstPerson;
            if (Mode == CameraMode.Follow && Sandbox.Focus == null) Mode = CameraMode.FirstPerson;
            Vector3 pos;
            Quaternion rot;
            float dizzy = 0f;
            switch (Mode)
            {
                case CameraMode.FirstPerson:
                    {
                        var p = Sandbox.Player;
                        pos = p != null ? p.EyePosition : Vector3.up * 1.7f;
                        rot = p != null ? Quaternion.Euler(p.Pitch, p.Yaw, 0f) : Quaternion.identity;
                        break;
                    }
                case CameraMode.KillCam:
                    {
                        OrbitYaw += dt * 25f;
                        Vector3 target = killTarget != null ? killTarget.CenterOfMass : focus;
                        Orbit(target, 3.2f, dt, out pos, out rot, false);
                        break;
                    }
                default:
                    {
                        RagdollBody b = Mode == CameraMode.Possess ? Sandbox.Possessed : Sandbox.Focus;
                        if (CursorControl.Locked && !TimeControl.Paused)
                        {
                            Vector2 md = InputBridge.MouseDelta;
                            OrbitYaw += md.x * 2.2f;
                            OrbitPitch = Mathf.Clamp(OrbitPitch - md.y * 2.2f, -40f, 75f);
                        }
                        if (Mode == CameraMode.Follow && Sandbox.Tools != null && !Sandbox.Tools.Grab.Active && CursorControl.Locked)
                        {
                            float sc = InputBridge.Scroll;
                            if (Mathf.Abs(sc) > 0.01f && InputBridge.Ctrl) orbitDist = Mathf.Clamp(orbitDist - sc * 0.4f, 1.2f, 12f);
                        }
                        Vector3 target = b != null && b.Chest != null ? (b.Chest.attached ? b.Chest.transform.position : b.CenterOfMass) + Vector3.up * cs.followHeight : focus;
                        Orbit(target, orbitDist, dt, out pos, out rot, true);
                        if (Mode == CameraMode.Possess && b != null) dizzy = b.Vitals.Dizziness;
                        break;
                    }
            }
            // shake, roll, dizziness
            float s = trauma * trauma;
            Vector3 shake = new Vector3(Mathf.PerlinNoise(noiseT * 18f, 1.1f) - 0.5f, Mathf.PerlinNoise(2.3f, noiseT * 18f) - 0.5f, Mathf.PerlinNoise(noiseT * 18f, 5.7f) - 0.5f) * 0.5f * s;
            float shakeRoll = (Mathf.PerlinNoise(noiseT * 15f, 9.9f) - 0.5f) * 14f * s;
            roll = Mathf.Lerp(roll, Mathf.Sin(noiseT * 0.9f) * 9f * dizzy, dt * 2f);
            t.position = pos + rot * shake;
            t.rotation = rot * Quaternion.Euler(0f, 0f, shakeRoll + roll);
            Cam.fieldOfView = Sandbox.Boot.player.fieldOfView + fovKick + Mathf.Sin(noiseT * 1.3f) * 2.5f * dizzy;
            // heartbeat when the possessed body is hurt
            if (Mode == CameraMode.Possess && Sandbox.Possessed != null && Sandbox.Audio != null)
            {
                var v = Sandbox.Possessed.Vitals;
                float hurt = Mathf.Clamp01((1f - v.BloodNorm) * 1.3f + v.Pain * 0.6f);
                if (hurt > 0.25f && v.HeartRate > 5f)
                {
                    heartTimer -= dt;
                    if (heartTimer <= 0f)
                    {
                        heartTimer = 60f / Mathf.Max(30f, v.HeartRate);
                        Sandbox.Audio.Play2D(Sfx.Heartbeat, hurt * 0.8f, 1f);
                    }
                }
            }
        }

        void Orbit(Vector3 target, float dist, float dt, out Vector3 pos, out Quaternion rot, bool collide)
        {
            var cs = Sandbox.Boot.cameraSettings;
            if (!focusInit || !MathUtil.IsFinite(focus)) { focus = target; focusInit = true; }
            focus = MathUtil.Damp(focus, target, cs.smoothing, dt);
            rot = Quaternion.Euler(OrbitPitch, OrbitYaw, 0f);
            Vector3 back = rot * Vector3.back;
            float d = dist;
            if (collide)
            {
                int n = Physics.SphereCastNonAlloc(focus, 0.2f, back, hits, dist, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = hits[i].collider;
                    if (Registry.PartOf(c) != null) continue;
                    if (Sandbox.Player != null && c == Sandbox.Player.Collider) continue;
                    if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                    if (hits[i].distance > 0.01f) d = Mathf.Min(d, hits[i].distance);
                }
            }
            pos = focus + back * Mathf.Max(0.4f, d - 0.05f);
        }
    }
    #endregion
}
