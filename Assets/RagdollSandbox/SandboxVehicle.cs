// Ragdoll Sandbox - a chunky clay car with raycast suspension. It patrols a ring road around the plaza on its
// own (AI) and can be driven (E near the car). Ragdolls hit by it bounce on the hood, roll over the roof and
// tumble (the body reacts through MotorBrain.OnVehicleHit).
using System.Collections.Generic;
using UnityEngine;

namespace RagdollSandbox
{
    public class Vehicle
    {
        public GameObject go;
        public Rigidbody rb;
        public bool AI = true;
        public bool Driven;
        public int NetId;
        public int Index;
        public float Throttle, Steer;
        public bool Handbrake;
        readonly Transform[] wheelVis = new Transform[4];
        readonly Vector3[] mount = new Vector3[4];
        readonly float[] lastCompression = new float[4];
        readonly float[] spin = new float[4];
        readonly bool[] wheelGrounded = new bool[4];
        float steerAngle;
        int waypoint;
        float stuckTimer, reverseTimer;
        public const float WheelRadius = 0.36f;
        const float RestLength = 0.32f;
        public float Speed { get { return rb != null ? Vector3.Dot(Compat.GetVelocity(rb), go.transform.forward) : 0f; } }
        public bool Grounded { get { return wheelGrounded[0] || wheelGrounded[1] || wheelGrounded[2] || wheelGrounded[3]; } }

        static Material bodyMat, darkMat, glassMat, lightMat, chromeMat;
        static readonly Color[] paints = { new Color(0.86f, 0.32f, 0.26f), new Color(0.28f, 0.62f, 0.9f), new Color(0.96f, 0.78f, 0.25f), new Color(0.45f, 0.78f, 0.42f), new Color(0.78f, 0.5f, 0.85f) };

        public static Vehicle Create(Transform parent, Vector3 pos, float yaw, int index)
        {
            var v = new Vehicle();
            v.Index = index;
            v.Build(parent, pos, yaw);
            return v;
        }

        static void EnsureMaterials()
        {
            if (darkMat != null) return;
            darkMat = MaterialFactory.Lit("CarRubber", new Color(0.1f, 0.1f, 0.11f), null, 0.15f, 0f);
            glassMat = MaterialFactory.Lit("CarGlass", new Color(0.55f, 0.72f, 0.85f), null, 0.85f, 0.1f);
            chromeMat = MaterialFactory.Lit("CarChrome", new Color(0.78f, 0.78f, 0.8f), null, 0.75f, 0.8f);
            lightMat = MaterialFactory.LitEmissive("CarLight", new Color(1f, 0.95f, 0.75f), null, 0.6f);
            MaterialFactory.SetEmission(lightMat, new Color(1f, 0.9f, 0.6f) * 1.5f);
        }

        void Build(Transform parent, Vector3 pos, float yaw)
        {
            EnsureMaterials();
            var vs = Sandbox.Boot.vehicles;
            go = new GameObject("Clay Car " + (Index + 1));
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            var paint = MaterialFactory.Lit("CarPaint", paints[Index % paints.Length], TextureFactory.White, 0.45f, 0f);
            bodyMat = paint;
            // lower body + bumpers + bubbly clay cabin
            Visual("Body", MeshFactory.Box(new Vector3(1.9f, 0.55f, 4.0f), 0f), paint, new Vector3(0f, 0.68f, 0f), Quaternion.identity, Vector3.one);
            Visual("Hood", MeshFactory.Ellipsoid(new Vector3(0.95f, 0.22f, 0.95f), 10, 14), paint, new Vector3(0f, 0.92f, 1.15f), Quaternion.identity, Vector3.one);
            Visual("Cabin", MeshFactory.Ellipsoid(new Vector3(0.9f, 0.55f, 1.2f), 12, 16), paint, new Vector3(0f, 1.0f, -0.35f), Quaternion.identity, Vector3.one);
            Visual("Windshield", MeshFactory.Ellipsoid(new Vector3(0.82f, 0.42f, 1.05f), 10, 14), glassMat, new Vector3(0f, 1.07f, -0.3f), Quaternion.identity, Vector3.one);
            Visual("BumperF", MeshFactory.Capsule(0.13f, 2.0f, 10), chromeMat, new Vector3(0f, 0.5f, 2.02f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            Visual("BumperR", MeshFactory.Capsule(0.13f, 2.0f, 10), chromeMat, new Vector3(0f, 0.5f, -2.02f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            Visual("LightL", MeshFactory.Sphere(0.11f, 6, 8), lightMat, new Vector3(-0.65f, 0.72f, 2.0f), Quaternion.identity, Vector3.one);
            Visual("LightR", MeshFactory.Sphere(0.11f, 6, 8), lightMat, new Vector3(0.65f, 0.72f, 2.0f), Quaternion.identity, Vector3.one);
            // colliders: chassis, slanted hood (ragdolls roll up onto it), roof, mirrors (grab-and-hold targets)
            var bc = go.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, 0.68f, 0f);
            bc.size = new Vector3(1.9f, 0.55f, 4.0f);
            PhysMats.Apply(bc, "metal");
            ColliderChild("HoodCol", new Vector3(0f, 0.98f, 1.2f), Quaternion.Euler(-10f, 0f, 0f), new Vector3(1.8f, 0.22f, 1.5f));
            ColliderChild("RoofCol", new Vector3(0f, 1.25f, -0.35f), Quaternion.identity, new Vector3(1.6f, 0.5f, 1.9f));
            for (int s = -1; s <= 1; s += 2)
            {
                var m = Visual("Mirror", MeshFactory.Box(new Vector3(0.22f, 0.14f, 0.08f), 0f), paint, new Vector3(s * 1.05f, 1.08f, 0.55f), Quaternion.identity, Vector3.one);
                var mc = m.AddComponent<BoxCollider>();
                mc.size = new Vector3(0.22f, 0.14f, 0.08f);
                var stalk = Visual("MirrorStalk", MeshFactory.Box(new Vector3(0.16f, 0.03f, 0.03f), 0f), chromeMat, new Vector3(s * 0.92f, 1.04f, 0.55f), Quaternion.identity, Vector3.one);
                stalk.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            rb = go.AddComponent<Rigidbody>();
            rb.mass = vs.mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.centerOfMass = new Vector3(0f, 0.42f, 0.1f);
            rb.maxDepenetrationVelocity = 4f;
            Compat.SetDamping(rb, 0.02f, 0.4f);
            rb.solverIterations = 8;
            // wheels
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -0.86f : 0.86f), z = (i < 2 ? 1.3f : -1.3f);
                mount[i] = new Vector3(x, 0.62f, z);
                var w = new GameObject("Wheel" + i).transform;
                w.SetParent(go.transform, false);
                w.localPosition = mount[i] - Vector3.up * RestLength;
                var tire = Visual("Tire", MeshFactory.Cylinder(WheelRadius, 0.26f, 14, 1f), darkMat, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), Vector3.one);
                tire.transform.SetParent(w, false);
                var hub = Visual("Hub", MeshFactory.Cylinder(WheelRadius * 0.5f, 0.27f, 10, 1f), chromeMat, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), Vector3.one);
                hub.transform.SetParent(w, false);
                wheelVis[i] = w;
            }
            Layers.SetRecursive(go, Layers.Vehicle);
            Registry.Vehicles[rb] = this;
            waypoint = Sandbox.Vehicles != null ? Sandbox.Vehicles.NearestWaypoint(pos) : 0;
        }

        GameObject Visual(string name, Mesh mesh, Material mat, Vector3 local, Quaternion rot, Vector3 scale)
        {
            var c = new GameObject(name);
            c.transform.SetParent(go.transform, false);
            c.transform.localPosition = local;
            c.transform.localRotation = rot;
            c.transform.localScale = scale;
            c.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = c.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return c;
        }

        void ColliderChild(string name, Vector3 local, Quaternion rot, Vector3 size)
        {
            var c = new GameObject(name);
            c.transform.SetParent(go.transform, false);
            c.transform.localPosition = local;
            c.transform.localRotation = rot;
            var b = c.AddComponent<BoxCollider>();
            b.size = size;
            PhysMats.Apply(b, "metal");
        }

        public void FixedTick(float dt)
        {
            if (go == null || rb == null || dt <= 0f) return;
            if (Sandbox.IsClient) return;
            if (AI && !Driven) DriveAI(dt);
            var vs = Sandbox.Boot.vehicles;
            Transform t = go.transform;
            float targetSteer = Steer * vs.steerAngle * Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(Mathf.Abs(Speed) / 20f));
            steerAngle = Mathf.MoveTowards(steerAngle, targetSteer, 120f * dt);
            Vector3 up = t.up;
            float wheelMass = rb.mass * 0.25f;
            float fwdSpeed = Speed;
            int grounded = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 origin = t.TransformPoint(mount[i]);
                float len = RestLength + WheelRadius;
                RaycastHit hit;
                bool front = i < 2;
                Quaternion steerRot = front ? Quaternion.AngleAxis(steerAngle, up) : Quaternion.identity;
                Vector3 wf = steerRot * t.forward;
                Vector3 wr = steerRot * t.right;
                float travel = RestLength;
                wheelGrounded[i] = false;
                if (Physics.Raycast(origin, -up, out hit, len, SuspensionMask, QueryTriggerInteraction.Ignore))
                {
                    grounded++;
                    wheelGrounded[i] = true;
                    float compression = len - hit.distance;
                    float cv = (compression - lastCompression[i]) / dt;
                    lastCompression[i] = compression;
                    float fz = Mathf.Max(0f, vs.suspensionSpring * compression + vs.suspensionDamper * cv);
                    rb.AddForceAtPosition(up * fz, origin);
                    Vector3 pv = rb.GetPointVelocity(hit.point);
                    // the ground may move (platforms, other bodies)
                    if (hit.rigidbody != null) pv -= hit.rigidbody.GetPointVelocity(hit.point);
                    float vLat = Vector3.Dot(pv, wr);
                    float vLong = Vector3.Dot(pv, wf);
                    float mu = (!front && Handbrake) ? 0.35f : 1.15f;
                    float maxF = mu * fz;
                    float lat = -vLat * wheelMass / dt * 0.6f;
                    float lon = 0f;
                    if (Handbrake && !front) lon = -Mathf.Sign(vLong) * Mathf.Min(Mathf.Abs(vLong) * wheelMass / dt, maxF);
                    else if (Mathf.Abs(Throttle) > 0.01f)
                    {
                        bool braking = Throttle * vLong < -0.3f;
                        if (braking) lon = -Mathf.Sign(vLong) * Mathf.Min(Mathf.Abs(vLong) * wheelMass / dt, vs.motorForce * 0.4f);
                        else if (Mathf.Abs(fwdSpeed) < vs.maxSpeed || Throttle * fwdSpeed < 0f) lon = Throttle * vs.motorForce * 0.25f;
                    }
                    else lon = -vLong * 25f; // rolling resistance / engine brake
                    Vector3 f = wr * lat + wf * lon;
                    float fm = f.magnitude;
                    if (fm > maxF && fm > 1e-3f) f *= maxF / fm;
                    rb.AddForceAtPosition(f, hit.point);
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic) hit.rigidbody.AddForceAtPosition(-f - up * fz * 0.5f, hit.point);
                    travel = hit.distance - WheelRadius;
                    spin[i] += vLong / WheelRadius * dt * Mathf.Rad2Deg;
                }
                else
                {
                    lastCompression[i] = 0f;
                    spin[i] += fwdSpeed / WheelRadius * dt * Mathf.Rad2Deg * 0.98f;
                }
                if (wheelVis[i] != null)
                {
                    wheelVis[i].localPosition = mount[i] - Vector3.up * Mathf.Clamp(travel, 0.02f, RestLength);
                    wheelVis[i].localRotation = Quaternion.Euler(0f, front ? steerAngle : 0f, 0f) * Quaternion.Euler(spin[i], 0f, 0f);
                }
            }
            // self-righting when upside down and almost still (keeps the AI car from staying stuck on its roof)
            if (grounded == 0 && Vector3.Dot(up, Vector3.up) < 0.2f && Compat.GetVelocity(rb).magnitude < 1f)
            {
                Vector3 axis = Vector3.Cross(up, Vector3.up);
                rb.AddTorque(axis.normalized * rb.mass * 6f, ForceMode.Force);
            }
            if (!MathUtil.IsFinite(rb.position) || rb.position.y < Sandbox.Boot.world.killPlaneY) Respawn();
        }

        static int SuspensionMask { get { return Layers.GroundMask & ~((1 << Layers.Vehicle) | (1 << Layers.Ragdoll) | (1 << Layers.Gibs)); } }

        void DriveAI(float dt)
        {
            var mgr = Sandbox.Vehicles;
            Vector3 p = rb.position;
            Vector3 target = mgr.Waypoint(waypoint);
            Vector3 to = MathUtil.Flat(target - p);
            if (to.magnitude < 6f) { waypoint = (waypoint + 1) % mgr.WaypointCount; target = mgr.Waypoint(waypoint); to = MathUtil.Flat(target - p); }
            // look ahead to the next waypoint for smooth corners
            Vector3 next = mgr.Waypoint((waypoint + 1) % mgr.WaypointCount);
            Vector3 aim = Vector3.Lerp(target, next, Mathf.Clamp01(1f - to.magnitude / 14f) * 0.5f);
            Vector3 local = go.transform.InverseTransformPoint(aim);
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            Steer = Mathf.Clamp(angle / 30f, -1f, 1f);
            float desired = Sandbox.Boot.vehicles.aiSpeed * Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(Mathf.Abs(angle) / 50f));
            float sp = Speed;
            Handbrake = false;
            if (reverseTimer > 0f)
            {
                reverseTimer -= dt;
                Throttle = -0.7f;
                Steer = -Steer;
                return;
            }
            Throttle = Mathf.Clamp((desired - sp) * 0.35f, -1f, 1f);
            if (sp < 0.6f && Throttle > 0.3f) stuckTimer += dt; else stuckTimer = 0f;
            if (stuckTimer > 2.5f) { stuckTimer = 0f; reverseTimer = 1.6f; }
        }

        public void Respawn()
        {
            Vector3 p = Sandbox.Vehicles.Waypoint(Index * 2 % Sandbox.Vehicles.WaypointCount) + Vector3.up * 0.6f;
            Vector3 n = Sandbox.Vehicles.Waypoint((Index * 2 + 1) % Sandbox.Vehicles.WaypointCount);
            rb.position = p;
            rb.rotation = Quaternion.LookRotation(MathUtil.SafeNormal(MathUtil.Flat(n - p), Vector3.forward));
            Compat.SetVelocity(rb, Vector3.zero);
            rb.angularVelocity = Vector3.zero;
            waypoint = (Index * 2 + 1) % Sandbox.Vehicles.WaypointCount;
        }

        public void Destroy()
        {
            if (rb != null) Registry.Vehicles.Remove(rb);
            if (go != null) Object.Destroy(go);
            go = null; rb = null;
        }
    }

    /// <summary>Owns the cars, the AI ring road and the drive mode (E to enter / leave the nearest car).</summary>
    public class VehicleManager
    {
        public readonly List<Vehicle> Cars = new List<Vehicle>(6);
        readonly List<Vector3> waypoints = new List<Vector3>(16);
        Transform root;
        public Vehicle Driving;
        int nextNetId = 1;

        public int WaypointCount { get { return waypoints.Count; } }
        public Vector3 Waypoint(int i) { return waypoints[(i % waypoints.Count + waypoints.Count) % waypoints.Count]; }

        public int NearestWaypoint(Vector3 p)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < waypoints.Count; i++) { float d = (waypoints[i] - p).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
            return best;
        }

        public void Build(Transform parent)
        {
            root = new GameObject("Vehicles").transform;
            root.SetParent(parent, false);
            // ring road just outside the plaza curbs (the flat cross of the map), driven counter-clockwise
            float r = 19f, c = 15.5f;
            waypoints.Add(new Vector3(r, 0f, -c)); waypoints.Add(new Vector3(r, 0f, c));
            waypoints.Add(new Vector3(c, 0f, r)); waypoints.Add(new Vector3(-c, 0f, r));
            waypoints.Add(new Vector3(-r, 0f, c)); waypoints.Add(new Vector3(-r, 0f, -c));
            waypoints.Add(new Vector3(-c, 0f, -r)); waypoints.Add(new Vector3(c, 0f, -r));
            BuildRoad();
            if (!Sandbox.IsClient && Sandbox.Boot.vehicles.maxCars > 0) Spawn(Waypoint(0) + Vector3.up * 0.4f, MathUtil.Yaw(Waypoint(1) - Waypoint(0)), true);
        }

        void BuildRoad()
        {
            var mat = MaterialFactory.Asphalt != null ? MaterialFactory.Asphalt : MaterialFactory.Lit("Road", new Color(0.23f, 0.23f, 0.25f), TextureFactory.Concrete, 0.2f, 0f);
            for (int i = 0; i < waypoints.Count; i++)
            {
                Vector3 a = waypoints[i], b = waypoints[(i + 1) % waypoints.Count];
                Vector3 mid = (a + b) * 0.5f;
                Vector3 d = b - a;
                float len = d.magnitude + 4.5f;
                var go = new GameObject("Road");
                go.transform.SetParent(root, false);
                go.transform.SetPositionAndRotation(new Vector3(mid.x, -0.045f, mid.z), Quaternion.LookRotation(d.normalized));
                go.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(new Vector3(4.6f, 0.02f, len), 0.25f);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.isStatic = true;
            }
        }

        public Vehicle Spawn(Vector3 pos, float yaw, bool ai)
        {
            if (Sandbox.IsClient) return null;
            int cap = Mathf.Max(1, Sandbox.Boot.vehicles.maxCars);
            while (Cars.Count >= cap)
            {
                var old = Cars[0];
                if (old == Driving) ExitDriving();
                old.Destroy();
                Cars.RemoveAt(0);
            }
            var v = Vehicle.Create(root, pos, yaw, Cars.Count);
            v.AI = ai;
            v.NetId = nextNetId++;
            Cars.Add(v);
            if (Sandbox.IsHost && Sandbox.Net != null) Sandbox.Net.OnPropSpawned(v.go, NetSession.CarKind);
            return v;
        }

        /// <summary>Network clients create the host's cars as kinematic shells driven by snapshots.</summary>
        public Vehicle SpawnRemote(int netId, Vector3 pos, Quaternion rot)
        {
            var v = Vehicle.Create(root, pos, rot.eulerAngles.y, Cars.Count);
            v.NetId = netId;
            v.AI = false;
            v.rb.isKinematic = true;
            v.rb.interpolation = RigidbodyInterpolation.None;
            Cars.Add(v);
            return v;
        }

        public Vehicle Find(int netId)
        {
            for (int i = 0; i < Cars.Count; i++) if (Cars[i].NetId == netId) return Cars[i];
            return null;
        }

        public void Tick(float dt)
        {
            if (Driving == null) return;
            if (Driving.go == null) { ExitDriving(); return; }
            bool blocked = TimeControl.Paused || InputBridge.GameplayBlocked;
            float th = 0f, st = 0f;
            if (!blocked)
            {
                if (InputBridge.Held(KeyId.W)) th += 1f;
                if (InputBridge.Held(KeyId.S)) th -= 1f;
                if (InputBridge.Held(KeyId.D)) st += 1f;
                if (InputBridge.Held(KeyId.A)) st -= 1f;
            }
            Driving.Throttle = th;
            Driving.Steer = st;
            Driving.Handbrake = !blocked && InputBridge.Held(KeyId.Space);
        }

        public void FixedTick(float dt)
        {
            for (int i = Cars.Count - 1; i >= 0; i--)
            {
                var c = Cars[i];
                if (c.go == null) { Cars.RemoveAt(i); continue; }
                c.FixedTick(dt);
            }
        }

        public void ToggleDrive()
        {
            if (Sandbox.IsClient) return;
            if (Driving != null) { ExitDriving(); return; }
            if (Sandbox.Possessed != null || Sandbox.Player == null) return;
            Vector3 p = Sandbox.Player.transform.position;
            Vehicle best = null; float bd = 5f * 5f;
            for (int i = 0; i < Cars.Count; i++)
            {
                var c = Cars[i];
                if (c.go == null) continue;
                float d = (c.rb.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = c; }
            }
            if (best == null) return;
            Driving = best;
            best.Driven = true;
            best.AI = false;
            Sandbox.Player.SetHidden(true);
            Sandbox.Cam.SetMode(CameraMode.Vehicle);
            SandboxEvents.RaiseToast("Driving: WASD, Space handbrake, E to get out", new Color(1f, 0.85f, 0.6f));
        }

        public void ExitDriving()
        {
            if (Driving == null) return;
            var v = Driving;
            Driving = null;
            v.Driven = false;
            v.Throttle = 0f; v.Steer = 0f; v.Handbrake = true;
            if (Sandbox.Player != null && v.go != null)
            {
                Vector3 side = v.go.transform.position - v.go.transform.right * 2.2f;
                Sandbox.Player.Teleport(new Vector3(side.x, Mathf.Max(side.y, v.go.transform.position.y) + 0.2f, side.z));
                Sandbox.Player.SetHidden(false);
            }
            if (Sandbox.Cam != null && Sandbox.Cam.Mode == CameraMode.Vehicle) Sandbox.Cam.SetMode(CameraMode.FirstPerson);
        }

        public void ClearAll()
        {
            ExitDriving();
            for (int i = 0; i < Cars.Count; i++) Cars[i].Destroy();
            Cars.Clear();
            if (!Sandbox.IsClient && Sandbox.Boot.vehicles.maxCars > 0) Spawn(Waypoint(0) + Vector3.up * 0.4f, MathUtil.Yaw(Waypoint(1) - Waypoint(0)), true);
        }
    }
}
