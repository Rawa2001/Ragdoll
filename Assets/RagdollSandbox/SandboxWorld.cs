// Ragdoll Sandbox - WorldBuilder: the ~120 x 120 m playground generated at runtime with a cohesive palette,
// procedural sky, sun, fog and optional day/night cycle. Zones: spawn plaza, rolling hills, stairs & ramps,
// obstacle course, demolition zone, hazard pit, tower & cliff. Plus hazards and prop bookkeeping (kill plane reset).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RagdollSandbox
{
    #region WorldBuilder
    public interface IWorldTick
    {
        void WorldFixedTick(float dt);
        void WorldTick(float dt);
    }

    public class WorldProp
    {
        public GameObject go;
        public Rigidbody rb;
        public Vector3 pos;
        public Quaternion rot;
        public Func<WorldProp, GameObject> rebuild;
    }

    public class WorldBuilder
    {
        public Light Sun;
        public Vector3 PlayerSpawn = new Vector3(0f, 0.05f, -6f);
        public float PlayerSpawnYaw = 0f;
        public Vector3 RagdollSpawn = new Vector3(0f, 0.02f, 1.5f);
        public float RagdollSpawnYaw = 180f;
        public float WindStrength = 0.3f;
        public float TimeOfDay;
        Transform root, staticRoot, propRoot;
        Material sky;
        readonly List<IWorldTick> tickers = new List<IWorldTick>(64);
        readonly List<WorldProp> props = new List<WorldProp>(512);
        readonly List<Light> lamps = new List<Light>(8);
        readonly List<Renderer> lampBulbs = new List<Renderer>(8);
        Rng rng;
        float size;
        float half;
        float hillHeight;

        public int PropCount { get { return props.Count; } }

        public void Build(Transform parent, int seed)
        {
            var ws = Sandbox.Boot.world;
            rng = new Rng(seed);
            size = ws.mapSize;
            half = size * 0.5f;
            hillHeight = ws.hillHeight;
            root = new GameObject("World").transform;
            root.SetParent(parent, false);
            staticRoot = new GameObject("Static").transform;
            staticRoot.SetParent(root, false);
            propRoot = new GameObject("Props").transform;
            propRoot.SetParent(root, false);
            BuildLighting();
            BuildTerrain();
            BuildPlaza();
            BuildStairsAndRamps(new Vector3(size / 3f, 0f, 0f));
            BuildObstacleCourse(new Vector3(0f, 0f, size / 3f));
            BuildDemolition(new Vector3(-size / 3f, 0f, 0f));
            BuildHazardPit(new Vector3(0f, 0f, -size / 3f));
            BuildTowerAndCliff(new Vector3(size / 3f, 0f, size / 3f));
            BuildHillsDecor();
            BuildPaths();
            if (ws.boundaryWalls) BuildBoundary();
            try { StaticBatchingUtility.Combine(staticRoot.gameObject); } catch (Exception) { }
            TimeOfDay = Sandbox.Boot.graphics.timeOfDay;
            ApplyTimeOfDay(TimeOfDay);
        }

        #region Lighting and sky
        void BuildLighting()
        {
            var go = new GameObject("Sun");
            go.transform.SetParent(root, false);
            Sun = go.AddComponent<Light>();
            Sun.type = LightType.Directional;
            Sun.shadows = LightShadows.Soft;
            Sun.shadowStrength = 0.85f;
            Sun.shadowBias = 0.04f;
            Sun.shadowNormalBias = 0.3f;
            Sun.intensity = 1.25f;
            Sun.color = new Color(1f, 0.93f, 0.82f);
            sky = MaterialFactory.Skybox();
            if (sky != null) RenderSettings.skybox = sky;
            RenderSettings.sun = Sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            var gs = Sandbox.Boot.graphics;
            RenderSettings.fog = gs.fog;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = gs.fogDensity;
        }

        public void ApplyTimeOfDay(float hours)
        {
            TimeOfDay = Mathf.Repeat(hours, 24f);
            float t = TimeOfDay / 24f;
            float sunAngle = (t - 0.25f) * 360f;            // 6:00 = horizon, 12:00 = zenith
            Sun.transform.rotation = Quaternion.Euler(sunAngle, -35f, 0f);
            float elev = Mathf.Sin((t - 0.25f) * Mathf.PI * 2f);
            float day = Mathf.Clamp01(elev * 3f + 0.2f);
            float golden = Mathf.Clamp01(1f - Mathf.Abs(elev) * 4f) * (elev > -0.1f ? 1f : 0f);
            Color noon = new Color(1f, 0.95f, 0.86f), dusk = new Color(1f, 0.62f, 0.38f), night = new Color(0.35f, 0.42f, 0.65f);
            Sun.color = Color.Lerp(Color.Lerp(noon, dusk, golden), night, 1f - day);
            Sun.intensity = Mathf.Lerp(0.08f, 1.3f, day);
            Sun.shadowStrength = Mathf.Lerp(0.3f, 0.85f, day);
            Color skyAmb = Color.Lerp(new Color(0.06f, 0.08f, 0.14f), new Color(0.55f, 0.62f, 0.75f), day);
            Color eqAmb = Color.Lerp(new Color(0.05f, 0.06f, 0.1f), Color.Lerp(new Color(0.48f, 0.47f, 0.45f), new Color(0.6f, 0.45f, 0.35f), golden), day);
            Color gndAmb = Color.Lerp(new Color(0.03f, 0.03f, 0.04f), new Color(0.3f, 0.27f, 0.22f), day);
            RenderSettings.ambientSkyColor = skyAmb;
            RenderSettings.ambientEquatorColor = eqAmb;
            RenderSettings.ambientGroundColor = gndAmb;
            RenderSettings.fogColor = Color.Lerp(new Color(0.06f, 0.07f, 0.1f), Color.Lerp(new Color(0.68f, 0.74f, 0.82f), new Color(0.85f, 0.65f, 0.5f), golden), day);
            if (sky != null)
            {
                sky.SetFloat("_Exposure", Mathf.Lerp(0.15f, 1.25f, day));
                sky.SetFloat("_AtmosphereThickness", Mathf.Lerp(0.8f, 1.05f, golden));
            }
            bool lampsOn = day < 0.45f;
            for (int i = 0; i < lamps.Count; i++) if (lamps[i] != null) lamps[i].enabled = lampsOn;
            for (int i = 0; i < lampBulbs.Count; i++) if (lampBulbs[i] != null) lampBulbs[i].sharedMaterial.color = lampsOn ? new Color(1f, 0.92f, 0.7f) : new Color(0.55f, 0.52f, 0.45f);
            WindStrength = Mathf.Lerp(0.35f, 0.18f, day);
        }
        #endregion

        #region Helpers
        GameObject StaticBox(string name, Vector3 center, Vector3 size3, Material mat, float uvScale, string phys, Quaternion rot = default(Quaternion), bool collider = true)
        {
            if (rot.w == 0f && rot.x == 0f && rot.y == 0f && rot.z == 0f) rot = Quaternion.identity;
            var go = new GameObject(name);
            go.transform.SetParent(staticRoot, false);
            go.transform.SetPositionAndRotation(center, rot);
            go.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(size3, uvScale);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            if (collider)
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.size = size3;
                PhysMats.Apply(bc, phys);
            }
            go.isStatic = true;
            return go;
        }

        GameObject StaticMesh(string name, Mesh mesh, Vector3 pos, Quaternion rot, Material mat, string phys, bool meshCollider, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : staticRoot, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (meshCollider)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                PhysMats.Apply(mc, phys);
            }
            go.isStatic = parent == null;
            return go;
        }

        /// <summary>Non-static moving geometry (kinematic rigidbody) parented under the world root.</summary>
        GameObject KinematicBox(string name, Vector3 center, Vector3 size3, Material mat, float uvScale, string phys, out Rigidbody rb)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(size3, uvScale);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = size3;
            PhysMats.Apply(bc, phys);
            rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            return go;
        }

        public GameObject DynamicBox(string name, Vector3 center, Vector3 size3, Material mat, float uvScale, string phys, float mass, Quaternion rot, Sfx sound)
        {
            var go = new GameObject(name);
            go.transform.SetParent(propRoot, false);
            go.transform.SetPositionAndRotation(center, rot);
            go.AddComponent<MeshFilter>().sharedMesh = MeshFactory.BoxCached(size3, uvScale);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = size3;
            PhysMats.Apply(bc, phys);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.sleepThreshold = 0.01f;
            Compat.SetDamping(rb, 0.05f, 0.1f);
            var pi = go.AddComponent<PropImpact>();
            pi.sound = sound;
            return go;
        }

        WorldProp Track(GameObject go, Func<WorldProp, GameObject> rebuild)
        {
            var wp = new WorldProp { go = go, rb = go.GetComponent<Rigidbody>(), pos = go.transform.position, rot = go.transform.rotation, rebuild = rebuild };
            props.Add(wp);
            return wp;
        }

        void AddTicker(IWorldTick t) { if (t != null) tickers.Add(t); }

        float GroundHeight(float x, float z)
        {
            return TerrainHeight(x, z);
        }
        #endregion

        #region Terrain
        /// <summary>Height of the rolling-hills heightfield; zone cells are flattened just below their floors.</summary>
        float TerrainHeight(float x, float z)
        {
            float cell = size / 3f;
            // distance to the cross of flat zones (centre column/row) and the NE tower cell
            float fx = Mathf.Abs(x), fz = Mathf.Abs(z);
            float flatCross = Mathf.Min(fx, fz) - cell * 0.5f;
            float tower = Mathf.Max(Mathf.Abs(x - cell), Mathf.Abs(z - cell)) - cell * 0.5f;
            float d = Mathf.Min(flatCross, tower);
            float w = MathUtil.SmoothStep01(d / 8f);
            float n = Noise.Perlin(x * 0.035f, z * 0.035f, 4) * 2f + 0.25f;
            float bumps = Noise.Perlin(x * 0.22f + 30f, z * 0.22f, 2) * 0.35f;
            float edge = MathUtil.SmoothStep01((Mathf.Max(fx, fz) - half + 14f) / 14f) * 3f;
            float h = (Mathf.Max(0f, n) * hillHeight + bumps + edge) * w;
            return h - 0.06f;
        }

        void BuildTerrain()
        {
            int res = Mathf.Clamp(Mathf.RoundToInt(size), 60, 160);
            var mesh = MeshFactory.Heightfield(size, size, res, res, TerrainHeight, 3f);
            var go = StaticMesh("Terrain", mesh, Vector3.zero, Quaternion.identity, MaterialFactory.Grass, "grass", true);
            go.GetComponent<MeshRenderer>().receiveShadows = true;
            // a skirt far below to hide the horizon gap
            StaticBox("Underground", new Vector3(0f, -3f, 0f), new Vector3(size * 3f, 1f, size * 3f), MaterialFactory.Dirt, 6f, "grass", Quaternion.identity, false);
        }

        void BuildHillsDecor()
        {
            float cell = size / 3f;
            int rocks = Mathf.RoundToInt(28 * Sandbox.Boot.world.propDensity);
            int trees = Mathf.RoundToInt(24 * Sandbox.Boot.world.propDensity);
            Vector3[] centers = { new Vector3(-cell, 0f, cell), new Vector3(-cell, 0f, -cell), new Vector3(cell, 0f, -cell) };
            for (int i = 0; i < rocks; i++)
            {
                Vector3 c = centers[i % 3];
                float x = c.x + rng.Range(-cell * 0.42f, cell * 0.42f), z = c.z + rng.Range(-cell * 0.42f, cell * 0.42f);
                float y = TerrainHeight(x, z);
                float r = rng.Range(0.25f, 1.4f);
                var m = MeshFactory.Rock(r, rng.Range(0, 100000), 0.3f);
                StaticMesh("Rock", m, new Vector3(x, y + r * 0.2f, z), Quaternion.Euler(rng.Range(0f, 20f), rng.Range(0f, 360f), rng.Range(0f, 20f)), MaterialFactory.Rock, "stone", true);
            }
            for (int i = 0; i < trees; i++)
            {
                Vector3 c = centers[i % 3];
                float x = c.x + rng.Range(-cell * 0.44f, cell * 0.44f), z = c.z + rng.Range(-cell * 0.44f, cell * 0.44f);
                float y = TerrainHeight(x, z);
                Tree(new Vector3(x, y, z), rng.Range(3.5f, 6.5f));
            }
            // bumpy field of small mounds for foot IK in the south-west
            for (int i = 0; i < 18; i++)
            {
                float x = -cell + rng.Range(-8f, 8f), z = -cell + rng.Range(-8f, 8f);
                var m = MeshFactory.Rock(rng.Range(0.3f, 0.6f), rng.Range(0, 9999), 0.15f);
                StaticMesh("Mound", m, new Vector3(x, TerrainHeight(x, z) - 0.15f, z), Quaternion.Euler(0f, rng.Range(0f, 360f), 0f), MaterialFactory.Grass, "grass", true);
            }
        }

        void Tree(Vector3 pos, float height)
        {
            var trunkMat = TreeBark();
            var leafMat = TreeLeaves();
            var trunk = MeshFactory.Lathe(new[] { new Vector2(0.28f, 0f), new Vector2(0.18f, 0.6f), new Vector2(0.14f, height * 0.6f), new Vector2(0.08f, height * 0.8f) }, 10, 1f, 0.5f);
            var t = StaticMesh("Tree", trunk, pos, Quaternion.Euler(0f, rng.Range(0f, 360f), 0f), trunkMat, "wood", false);
            var cap = t.AddComponent<CapsuleCollider>();
            cap.radius = 0.2f; cap.height = height * 0.8f; cap.center = new Vector3(0f, height * 0.4f, 0f);
            int blobs = rng.Range(3, 6);
            for (int i = 0; i < blobs; i++)
            {
                Vector3 off = new Vector3(rng.Range(-0.9f, 0.9f), height * rng.Range(0.68f, 0.95f), rng.Range(-0.9f, 0.9f));
                var m = MeshFactory.Rock(rng.Range(1f, 1.6f), rng.Range(0, 99999), 0.22f);
                StaticMesh("Leaves", m, pos + off, Quaternion.Euler(0f, rng.Range(0f, 360f), 0f), leafMat, "grass", false);
            }
        }

        Material bark, leaves;
        Material TreeBark() { if (bark == null) bark = MaterialFactory.Lit("Bark", new Color(0.42f, 0.3f, 0.22f), TextureFactory.Wood, 0.1f, 0f); return bark; }
        Material TreeLeaves() { if (leaves == null) leaves = MaterialFactory.Lit("Leaves", new Color(0.55f, 0.75f, 0.45f), TextureFactory.Grass, 0.1f, 0f); return leaves; }

        void BuildPaths()
        {
            float cell = size / 3f;
            // dirt walkways from the plaza edge (15 m) to the four zone floors (cell/2 + 2 m)
            float a = 14.5f, b = cell * 0.5f + 2.5f;
            float mid = (a + b) * 0.5f, len = Mathf.Max(0.5f, b - a);
            StaticBox("PathN", new Vector3(0f, -0.04f, mid), new Vector3(4f, 0.1f, len), MaterialFactory.Dirt, 3f, "grass");
            StaticBox("PathS", new Vector3(0f, -0.04f, -mid), new Vector3(4f, 0.1f, len), MaterialFactory.Dirt, 3f, "grass");
            StaticBox("PathE", new Vector3(mid, -0.04f, 0f), new Vector3(len, 0.1f, 4f), MaterialFactory.Dirt, 3f, "grass");
            StaticBox("PathW", new Vector3(-mid, -0.04f, 0f), new Vector3(len, 0.1f, 4f), MaterialFactory.Dirt, 3f, "grass");
        }

        void BuildBoundary()
        {
            float h = 60f;
            var mats = (Material)null;
            StaticBox("WallN", new Vector3(0f, h * 0.5f - 5f, half + 0.5f), new Vector3(size + 2f, h, 1f), mats, 1f, "concrete", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
            StaticBox("WallS", new Vector3(0f, h * 0.5f - 5f, -half - 0.5f), new Vector3(size + 2f, h, 1f), mats, 1f, "concrete", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
            StaticBox("WallE", new Vector3(half + 0.5f, h * 0.5f - 5f, 0f), new Vector3(1f, h, size + 2f), mats, 1f, "concrete", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
            StaticBox("WallW", new Vector3(-half - 0.5f, h * 0.5f - 5f, 0f), new Vector3(1f, h, size + 2f), mats, 1f, "concrete", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
            StaticBox("Ceiling", new Vector3(0f, 80f, 0f), new Vector3(size + 2f, 1f, size + 2f), mats, 1f, "concrete", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
        }
        #endregion

        #region Spawn plaza
        void BuildPlaza()
        {
            float s = 30f;
            StaticBox("PlazaFloor", new Vector3(0f, -0.15f, 0f), new Vector3(s, 0.3f, s), MaterialFactory.Tiles, 2f, "concrete");
            // curbs with entrances
            float gap = 6f, h = 0.22f, w = 0.4f;
            float segLen = (s - gap) * 0.5f;
            for (int side = 0; side < 4; side++)
            {
                for (int k = -1; k <= 1; k += 2)
                {
                    float along = k * (gap * 0.5f + segLen * 0.5f);
                    Vector3 c, sz;
                    if (side < 2) { c = new Vector3(along, h * 0.5f, (side == 0 ? 1f : -1f) * (s * 0.5f - w * 0.5f)); sz = new Vector3(segLen, h, w); }
                    else { c = new Vector3((side == 2 ? 1f : -1f) * (s * 0.5f - w * 0.5f), h * 0.5f, along); sz = new Vector3(w, h, segLen); }
                    StaticBox("Curb", c, sz, MaterialFactory.Concrete, 1f, "concrete");
                }
            }
            // lamp posts
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * (s * 0.5f - 1.5f), z = (i < 2 ? -1f : 1f) * (s * 0.5f - 1.5f);
                Lamp(new Vector3(x, 0f, z));
            }
            // benches
            Bench(new Vector3(-8f, 0f, 9f), 0f);
            Bench(new Vector3(8f, 0f, 9f), 0f);
            Bench(new Vector3(-10f, 0f, -6f), 90f);
            Bench(new Vector3(10f, 0f, -6f), -90f);
            // planters with bushes
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * 6f, z = (i < 2 ? -1f : 1f) * 11f;
                StaticBox("Planter", new Vector3(x, 0.3f, z), new Vector3(2.2f, 0.6f, 2.2f), MaterialFactory.Concrete, 1f, "concrete");
                StaticBox("Soil", new Vector3(x, 0.61f, z), new Vector3(2f, 0.02f, 2f), MaterialFactory.Dirt, 1f, "grass", Quaternion.identity, false);
                for (int b = 0; b < 3; b++)
                {
                    var m = MeshFactory.Rock(rng.Range(0.45f, 0.65f), rng.Range(0, 9999), 0.2f);
                    StaticMesh("Bush", m, new Vector3(x + rng.Range(-0.5f, 0.5f), 0.95f, z + rng.Range(-0.5f, 0.5f)), Quaternion.identity, TreeLeaves(), "grass", false);
                }
            }
            // a low stage for small fall tests
            StaticBox("Stage", new Vector3(0f, 0.6f, 11f), new Vector3(6f, 1.2f, 3f), MaterialFactory.Wood, 1f, "wood");
            StaticBox("StageStep", new Vector3(0f, 0.3f, 9.2f), new Vector3(2.4f, 0.6f, 0.6f), MaterialFactory.Wood, 1f, "wood");
            // a few props
            SpawnCrate(new Vector3(6f, 0.5f, 3f), 0f);
            SpawnCrate(new Vector3(6.2f, 1.5f, 3.1f), 15f);
            SpawnCrate(new Vector3(-6f, 0.5f, 4f), 30f);
            SpawnBall(new Vector3(-3f, 0.4f, -2f));
            SpawnSteelBarrel(new Vector3(9f, 0.45f, -2f));
            SpawnSteelBarrel(new Vector3(9.8f, 0.45f, -1.2f));
            for (int i = 0; i < 3; i++) SpawnTire(new Vector3(-9f, 0.15f + i * 0.3f, 2f), 0f);
        }

        void Lamp(Vector3 basePos)
        {
            var pole = MeshFactory.Cylinder(0.07f, 4.2f, 10, 1f);
            var go = StaticMesh("LampPost", pole, basePos + Vector3.up * 2.1f, Quaternion.identity, MaterialFactory.DarkMetal, "metal", false);
            var cc = go.AddComponent<CapsuleCollider>();
            cc.radius = 0.08f; cc.height = 4.2f;
            var bulbMat = MaterialFactory.Unlit("LampBulb", new Color(0.55f, 0.52f, 0.45f), null);
            var bulb = StaticMesh("Bulb", MeshFactory.Sphere(0.22f, 8, 12), basePos + Vector3.up * 4.3f, Quaternion.identity, bulbMat, "metal", false);
            lampBulbs.Add(bulb.GetComponent<MeshRenderer>());
            var lgo = new GameObject("LampLight");
            lgo.transform.SetParent(root, false);
            lgo.transform.position = basePos + Vector3.up * 4.1f;
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 14f;
            l.intensity = 2.2f;
            l.color = new Color(1f, 0.85f, 0.6f);
            l.shadows = LightShadows.None;
            l.enabled = false;
            lamps.Add(l);
        }

        void Bench(Vector3 pos, float yaw)
        {
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            StaticBox("BenchSeat", pos + q * new Vector3(0f, 0.45f, 0f), new Vector3(2f, 0.08f, 0.45f), MaterialFactory.Wood, 1f, "wood", q);
            StaticBox("BenchBack", pos + q * new Vector3(0f, 0.75f, -0.2f), new Vector3(2f, 0.4f, 0.06f), MaterialFactory.Wood, 1f, "wood", q);
            StaticBox("BenchLegL", pos + q * new Vector3(-0.85f, 0.22f, 0f), new Vector3(0.08f, 0.44f, 0.4f), MaterialFactory.DarkMetal, 1f, "metal", q);
            StaticBox("BenchLegR", pos + q * new Vector3(0.85f, 0.22f, 0f), new Vector3(0.08f, 0.44f, 0.4f), MaterialFactory.DarkMetal, 1f, "metal", q);
        }
        #endregion

        #region Props
        public GameObject SpawnCrate(Vector3 pos, float yaw)
        {
            Func<WorldProp, GameObject> make = wp => DynamicBox("Crate", wp.pos, Vector3.one, MaterialFactory.Crate, 0f, "wood", 20f, wp.rot, Sfx.WoodKnock);
            var wp0 = new WorldProp { pos = pos, rot = Quaternion.Euler(0f, yaw, 0f), rebuild = make };
            wp0.go = make(wp0);
            wp0.rb = wp0.go.GetComponent<Rigidbody>();
            props.Add(wp0);
            return wp0.go;
        }

        public GameObject SpawnBall(Vector3 pos)
        {
            Func<WorldProp, GameObject> make = wp => MakeBall(wp.pos, 0.35f, 3f);
            var wp0 = new WorldProp { pos = pos, rot = Quaternion.identity, rebuild = make };
            wp0.go = make(wp0);
            wp0.rb = wp0.go.GetComponent<Rigidbody>();
            props.Add(wp0);
            return wp0.go;
        }

        public GameObject MakeBall(Vector3 pos, float r, float mass)
        {
            var go = new GameObject("Ball");
            go.transform.SetParent(propRoot, false);
            go.transform.position = pos;
            go.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Sphere(r, 12, 18);
            go.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.PaintOrange;
            var sc = go.AddComponent<SphereCollider>(); sc.radius = r; sc.sharedMaterial = PhysMats.Ball;
            var rb = go.AddComponent<Rigidbody>(); rb.mass = mass; rb.interpolation = RigidbodyInterpolation.Interpolate;
            Compat.SetDamping(rb, 0.05f, 0.3f);
            go.AddComponent<PropImpact>().sound = Sfx.Thump;
            return go;
        }

        public GameObject SpawnSteelBarrel(Vector3 pos)
        {
            Func<WorldProp, GameObject> make = wp => MakeSteelBarrel(wp.pos, wp.rot);
            var wp0 = new WorldProp { pos = pos, rot = Quaternion.identity, rebuild = make };
            wp0.go = make(wp0);
            wp0.rb = wp0.go.GetComponent<Rigidbody>();
            props.Add(wp0);
            return wp0.go;
        }

        public GameObject MakeSteelBarrel(Vector3 pos, Quaternion rot)
        {
            var go = new GameObject("Barrel");
            go.transform.SetParent(propRoot, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<MeshFilter>().sharedMesh = ExplosionSystem.BarrelMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.BarrelSteel;
            var cc = go.AddComponent<CapsuleCollider>(); cc.radius = 0.3f; cc.height = 0.9f; cc.sharedMaterial = PhysMats.Metal;
            var rb = go.AddComponent<Rigidbody>(); rb.mass = 30f; rb.interpolation = RigidbodyInterpolation.Interpolate;
            go.AddComponent<PropImpact>().sound = Sfx.Clang;
            return go;
        }

        static Mesh tireMesh;
        public GameObject SpawnTire(Vector3 pos, float tilt)
        {
            Func<WorldProp, GameObject> make = wp => MakeTire(wp.pos, wp.rot);
            var wp0 = new WorldProp { pos = pos, rot = Quaternion.Euler(tilt, rng.Range(0f, 360f), 0f), rebuild = make };
            wp0.go = make(wp0);
            wp0.rb = wp0.go.GetComponent<Rigidbody>();
            props.Add(wp0);
            return wp0.go;
        }

        public GameObject MakeTire(Vector3 pos, Quaternion rot)
        {
            if (tireMesh == null) tireMesh = MeshFactory.Torus(0.32f, 0.13f, 18, 10, 1f);
            var go = new GameObject("Tire");
            go.transform.SetParent(propRoot, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<MeshFilter>().sharedMesh = tireMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.Rubber;
            var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = tireMesh; mc.convex = true; mc.sharedMaterial = PhysMats.Rubber;
            var rb = go.AddComponent<Rigidbody>(); rb.mass = 9f; rb.interpolation = RigidbodyInterpolation.Interpolate;
            go.AddComponent<PropImpact>().sound = Sfx.Thump;
            return go;
        }

        GameObject SpawnExplosiveBarrel(Vector3 pos)
        {
            Func<WorldProp, GameObject> make = wp =>
            {
                var ep = Sandbox.Explosions.Spawn(ExplosiveKind.Barrel, wp.pos, wp.rot, Vector3.zero, propRoot);
                ep.IsWorldProp = true;
                return ep.gameObject;
            };
            var wp0 = new WorldProp { pos = pos, rot = Quaternion.identity, rebuild = make };
            wp0.go = make(wp0);
            wp0.rb = wp0.go.GetComponent<Rigidbody>();
            props.Add(wp0);
            return wp0.go;
        }

        public void OnWorldExplosiveGone(ExplosiveProp p) { }

        public void ResetProps()
        {
            for (int i = 0; i < props.Count; i++)
            {
                var wp = props[i];
                if (wp.go == null || !wp.go.activeInHierarchy)
                {
                    if (wp.go != null) UnityEngine.Object.Destroy(wp.go);
                    if (wp.rebuild != null) { wp.go = wp.rebuild(wp); wp.rb = wp.go != null ? wp.go.GetComponent<Rigidbody>() : null; }
                    continue;
                }
                ResetOne(wp);
            }
            for (int i = 0; i < tickers.Count; i++)
            {
                var plank = tickers[i] as DestructiblePlankGroup;
                if (plank != null) plank.Reset();
            }
        }

        static void ResetOne(WorldProp wp)
        {
            if (wp.rb != null)
            {
                bool k = wp.rb.isKinematic;
                if (!k) { Compat.SetVelocity(wp.rb, Vector3.zero); wp.rb.angularVelocity = Vector3.zero; }
                wp.rb.position = wp.pos;
                wp.rb.rotation = wp.rot;
            }
            wp.go.transform.SetPositionAndRotation(wp.pos, wp.rot);
        }

        public void ValidateProps(float maxSpeed, float killY, float bound)
        {
            for (int i = 0; i < props.Count; i++)
            {
                var wp = props[i];
                if (wp.go == null || wp.rb == null || wp.rb.isKinematic) continue;
                Vector3 p = wp.rb.position;
                Vector3 v = Compat.GetVelocity(wp.rb);
                if (!MathUtil.IsFinite(p) || !MathUtil.IsFinite(v) || p.y < killY || Mathf.Abs(p.x) > bound || Mathf.Abs(p.z) > bound) { ResetOne(wp); continue; }
                if (v.sqrMagnitude > maxSpeed * maxSpeed) Compat.SetVelocity(wp.rb, v.normalized * maxSpeed);
            }
        }
        #endregion

        #region Stairs and ramps
        void BuildStairsAndRamps(Vector3 c)
        {
            float cell = size / 3f;
            StaticBox("StairsFloor", c + new Vector3(0f, -0.15f, 0f), new Vector3(cell - 4f, 0.3f, cell - 4f), MaterialFactory.Concrete, 2f, "concrete");
            float[] rises = { 0.12f, 0.17f, 0.22f };
            for (int k = 0; k < 3; k++)
            {
                float z = c.z - 10f + k * 7f;
                float rise = rises[k], run = 0.3f;
                int steps = 9;
                float x0 = c.x - 14f;
                for (int i = 0; i < steps; i++)
                {
                    float h = rise * (i + 1);
                    StaticBox("Step", new Vector3(x0 + run * (i + 0.5f), h * 0.5f, z), new Vector3(run, h, 3f), MaterialFactory.Concrete, 1f, "concrete");
                }
                float top = rise * steps;
                float xl = x0 + run * steps;
                StaticBox("Landing", new Vector3(xl + 1.5f, top * 0.5f, z), new Vector3(3f, top, 3f), MaterialFactory.Brick, 1.5f, "concrete");
                // down again via a ramp
                float rampLen = top / Mathf.Tan(22f * Mathf.Deg2Rad);
                var wedge = MeshFactory.Wedge(new Vector3(3f, top, rampLen), 2f);
                var wg = StaticMesh("Ramp", wedge, new Vector3(xl + 3f + rampLen * 0.5f, 0f, z), Quaternion.Euler(0f, -90f, 0f), MaterialFactory.Concrete, "concrete", true);
                wg.GetComponent<MeshCollider>().convex = false;
            }
            // steep ramps
            float[] slopes = { 15f, 25f, 35f };
            for (int k = 0; k < 3; k++)
            {
                float len = 7f;
                float h = Mathf.Tan(slopes[k] * Mathf.Deg2Rad) * len;
                var wedge = MeshFactory.Wedge(new Vector3(3.5f, h, len), 2f);
                StaticMesh("SteepRamp" + slopes[k], wedge, new Vector3(c.x + 8f + k * 4.5f - 4.5f, 0f, c.z - 12f), Quaternion.identity, MaterialFactory.Tread, "metal", true);
                StaticBox("RampTop", new Vector3(c.x + 8f + k * 4.5f - 4.5f, h * 0.5f, c.z - 12f + len * 0.5f + 1f), new Vector3(3.5f, h, 2f), MaterialFactory.Concrete, 1f, "concrete");
            }
            // wedges / bumps
            for (int i = 0; i < 6; i++)
            {
                var w = MeshFactory.Wedge(new Vector3(1.5f, 0.25f + i * 0.05f, 1f), 1f);
                StaticMesh("Wedge", w, new Vector3(c.x - 4f + i * 1.8f, 0f, c.z + 9f), Quaternion.Euler(0f, i % 2 == 0 ? 0f : 180f, 0f), MaterialFactory.PaintBlue, "concrete", true);
            }
            // half pipe
            var hp = MeshFactory.HalfPipe(3f, 10f, 0.3f, 18, 2f);
            StaticMesh("HalfPipe", hp, new Vector3(c.x + 8f, 0f, c.z + 6f), Quaternion.identity, MaterialFactory.Concrete, "concrete", true);
            // uneven stepping blocks
            for (int i = 0; i < 10; i++)
            {
                float h = 0.1f + rng.Range(0f, 0.35f);
                StaticBox("UnevenBlock", new Vector3(c.x - 10f + (i % 5) * 1.1f, h * 0.5f, c.z + 13f + (i / 5) * 1.1f), new Vector3(1f, h, 1f), MaterialFactory.Rock, 1f, "stone");
            }
        }
        #endregion

        #region Obstacle course
        void BuildObstacleCourse(Vector3 c)
        {
            float cell = size / 3f;
            StaticBox("CourseFloor", c + new Vector3(0f, -0.15f, 0f), new Vector3(cell - 4f, 0.3f, cell - 4f), MaterialFactory.Concrete, 2f, "concrete");
            // balance beam
            StaticBox("BeamPostA", c + new Vector3(-12f, 0.5f, -12f), new Vector3(0.3f, 1f, 0.3f), MaterialFactory.DarkMetal, 1f, "metal");
            StaticBox("BeamPostB", c + new Vector3(-12f, 0.5f, -4f), new Vector3(0.3f, 1f, 0.3f), MaterialFactory.DarkMetal, 1f, "metal");
            StaticBox("BalanceBeam", c + new Vector3(-12f, 1.05f, -8f), new Vector3(0.22f, 0.1f, 8.6f), MaterialFactory.Wood, 1f, "wood");
            StaticBox("BeamStep", c + new Vector3(-12f, 0.5f, -12.9f), new Vector3(0.9f, 1f, 0.8f), MaterialFactory.Wood, 1f, "wood");
            // stepping stones
            for (int i = 0; i < 9; i++)
            {
                float h = 0.25f + rng.Range(0f, 0.5f);
                var m = MeshFactory.Cylinder(0.42f, h, 14, 1f);
                var go = StaticMesh("SteppingStone", m, c + new Vector3(-6f + Mathf.Sin(i * 0.8f) * 0.8f, h * 0.5f, -12f + i * 1.0f), Quaternion.identity, MaterialFactory.Rock, "stone", false);
                var bc = go.AddComponent<BoxCollider>();
                bc.size = new Vector3(0.8f, h, 0.8f);
                PhysMats.Apply(bc, "stone");
            }
            // seesaws
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = c + new Vector3(0f + i * 4f, 0f, -10f);
                var pivotMesh = MeshFactory.Wedge(new Vector3(0.6f, 0.45f, 0.5f), 1f);
                StaticMesh("SeesawBaseA", pivotMesh, p + new Vector3(0f, 0f, -0.25f), Quaternion.identity, MaterialFactory.DarkMetal, "metal", true);
                StaticMesh("SeesawBaseB", pivotMesh, p + new Vector3(0f, 0f, 0.25f), Quaternion.Euler(0f, 180f, 0f), MaterialFactory.DarkMetal, "metal", true);
                var plank = DynamicBox("Seesaw", p + new Vector3(0f, 0.52f, 0f), new Vector3(0.6f, 0.08f, 4.2f), MaterialFactory.Wood, 1f, "wood", 22f, Quaternion.identity, Sfx.WoodKnock);
                var hj = plank.AddComponent<HingeJoint>();
                hj.axis = Vector3.right;
                hj.anchor = Vector3.zero;
                hj.useLimits = true;
                hj.limits = new JointLimits { min = -22f, max = 22f };
                Track(plank, null);
            }
            // moving platforms (horizontal) and an elevator
            {
                Rigidbody rb;
                var a = KinematicBox("MovingPlatform", c + new Vector3(8f, 0.8f, -10f), new Vector3(3f, 0.3f, 3f), MaterialFactory.Tread, 1f, "metal", out rb);
                var mover = a.AddComponent<KinematicMover>();
                mover.Setup(rb, c + new Vector3(8f, 0.8f, -10f), c + new Vector3(8f, 0.8f, -2f), 0f, Sandbox.Boot.world.platformSpeed, 1.5f);
                AddTicker(mover);
                Registry.Movers[a.GetComponent<Collider>()] = mover;
                var b = KinematicBox("MovingPlatform2", c + new Vector3(13f, 1.4f, -6f), new Vector3(2.5f, 0.3f, 2.5f), MaterialFactory.Tread, 1f, "metal", out rb);
                var mover2 = b.AddComponent<KinematicMover>();
                mover2.Setup(rb, c + new Vector3(13f, 1.4f, -6f), c + new Vector3(13f, 3.4f, -6f), 0.5f, Sandbox.Boot.world.platformSpeed * 0.7f, 2f);
                AddTicker(mover2);
                Registry.Movers[b.GetComponent<Collider>()] = mover2;
            }
            // rotating sweeper bars
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = c + new Vector3(-8f + i * 6f, 0f, 2f);
                var hub = MeshFactory.Cylinder(0.25f, 1.2f, 12, 1f);
                StaticMesh("SweeperHub", hub, p + Vector3.up * 0.6f, Quaternion.identity, MaterialFactory.Hazard, "metal", true);
                var barGo = new GameObject("SweeperBar");
                barGo.transform.SetParent(root, false);
                barGo.transform.position = p + Vector3.up * (0.45f + i * 0.4f);
                barGo.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(new Vector3(5.6f, 0.18f, 0.18f), 1f);
                barGo.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.PaintOrange;
                var bc = barGo.AddComponent<BoxCollider>(); bc.size = new Vector3(5.6f, 0.18f, 0.18f); PhysMats.Apply(bc, "metal");
                var rb = barGo.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                var rot = barGo.AddComponent<KinematicRotator>();
                rot.Setup(rb, Vector3.up, i == 0 ? 70f : -95f, 0f);
                AddTicker(rot);
            }
            // pendulum hammers
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = c + new Vector3(-4f + i * 4f, 0f, 9f);
                StaticBox("PendPostL", p + new Vector3(-1.6f, 2.2f, 0f), new Vector3(0.25f, 4.4f, 0.25f), MaterialFactory.DarkMetal, 1f, "metal");
                StaticBox("PendPostR", p + new Vector3(1.6f, 2.2f, 0f), new Vector3(0.25f, 4.4f, 0.25f), MaterialFactory.DarkMetal, 1f, "metal");
                StaticBox("PendBeam", p + new Vector3(0f, 4.45f, 0f), new Vector3(3.5f, 0.25f, 0.3f), MaterialFactory.DarkMetal, 1f, "metal");
                var pivot = new GameObject("Pendulum");
                pivot.transform.SetParent(root, false);
                pivot.transform.position = p + new Vector3(0f, 4.3f, 0f);
                var arm = new GameObject("Arm");
                arm.transform.SetParent(pivot.transform, false);
                arm.transform.localPosition = new Vector3(0f, -1.6f, 0f);
                arm.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Box(new Vector3(0.12f, 3.2f, 0.12f), 1f);
                arm.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.Metal;
                var head = new GameObject("HammerHead");
                head.transform.SetParent(pivot.transform, false);
                head.transform.localPosition = new Vector3(0f, -3.3f, 0f);
                head.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                head.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Cylinder(0.32f, 1.1f, 16, 1f);
                head.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.Hazard;
                var hc = head.AddComponent<CapsuleCollider>(); hc.radius = 0.32f; hc.height = 1.1f; hc.direction = 1; PhysMats.Apply(hc, "metal");
                var ac = arm.AddComponent<BoxCollider>(); ac.size = new Vector3(0.12f, 3.2f, 0.12f);
                var rb = pivot.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                var sw = pivot.AddComponent<KinematicRotator>();
                sw.Setup(rb, Vector3.forward, 0f, 0f);
                sw.Swing(55f, 2.6f + i * 0.35f, i * 1.1f);
                AddTicker(sw);
            }
            // trampoline pads
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = c + new Vector3(8f + i * 2.5f, 0f, 4f);
                StaticBox("TrampolineFrame", p + new Vector3(0f, 0.1f, 0f), new Vector3(1.9f, 0.2f, 1.9f), MaterialFactory.DarkMetal, 1f, "metal");
                var pad = StaticBox("TrampolinePad", p + new Vector3(0f, 0.21f, 0f), new Vector3(1.6f, 0.04f, 1.6f), MaterialFactory.Trampoline, 1f, "bouncy", Quaternion.identity, false);
                var trig = new GameObject("TrampolineTrigger");
                trig.transform.SetParent(root, false);
                trig.transform.position = p + new Vector3(0f, 0.45f, 0f);
                var tc = trig.AddComponent<BoxCollider>(); tc.isTrigger = true; tc.size = new Vector3(1.6f, 0.5f, 1.6f);
                var tr = trig.AddComponent<TrampolinePad>();
                tr.speed = Sandbox.Boot.world.trampolineSpeed * (0.8f + i * 0.2f);
                var rbT = trig.AddComponent<Rigidbody>(); rbT.isKinematic = true;
                StaticBox("TrampolineSolid", p + new Vector3(0f, 0.1f, 0f), new Vector3(1.6f, 0.2f, 1.6f), MaterialFactory.Trampoline, 1f, "bouncy", Quaternion.identity, true).GetComponent<MeshRenderer>().enabled = false;
            }
            // conveyor belts
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = c + new Vector3(-12f + i * 0f, 0.15f, 6f + i * 3f);
                var mat = new Material(MaterialFactory.Tread);
                mat.mainTextureScale = new Vector2(1f, 6f);
                Rigidbody rb;
                var belt = KinematicBox("Conveyor", p, new Vector3(1.6f, 0.3f, 8f), mat, 0f, "rubber", out rb);
                belt.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                rb.rotation = belt.transform.rotation;
                var conv = belt.AddComponent<Conveyor>();
                conv.Setup(rb, belt.transform.forward * (i == 0 ? 1f : -1f), Sandbox.Boot.world.conveyorSpeed, mat);
                AddTicker(conv);
                Registry.Movers[belt.GetComponent<Collider>()] = conv;
                StaticBox("ConveyorRailA", p + new Vector3(0f, 0.12f, 0.9f), new Vector3(8f, 0.15f, 0.1f), MaterialFactory.Hazard, 1f, "metal");
                StaticBox("ConveyorRailB", p + new Vector3(0f, 0.12f, -0.9f), new Vector3(8f, 0.15f, 0.1f), MaterialFactory.Hazard, 1f, "metal");
            }
        }
        #endregion

        #region Demolition zone
        void BuildDemolition(Vector3 c)
        {
            float cell = size / 3f;
            var ws = Sandbox.Boot.world;
            StaticBox("DemoFloor", c + new Vector3(0f, -0.15f, 0f), new Vector3(cell - 4f, 0.3f, cell - 4f), MaterialFactory.Asphalt, 3f, "concrete");
            // crate pyramid
            int rows = 4;
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < rows - r; k++)
                    SpawnCrate(c + new Vector3(-12f + k * 1.02f + r * 0.51f, 0.5f + r * 1.0f, -12f), 0f);
            // brick walls (running bond)
            Vector3 brick = new Vector3(0.4f, 0.2f, 0.2f);
            int bricks = Mathf.Clamp(ws.brickCount, 0, 600);
            int perWall = bricks / 2;
            for (int w = 0; w < 2; w++)
            {
                Vector3 o = c + new Vector3(-4f + w * 7f, 0f, -6f);
                int cols = 10;
                int nRows = Mathf.Max(1, perWall / cols);
                for (int r = 0; r < nRows; r++)
                    for (int k = 0; k < cols; k++)
                    {
                        float x = (k - cols * 0.5f) * brick.x + (r % 2) * brick.x * 0.5f;
                        var go = DynamicBox("Brick", o + new Vector3(x, brick.y * (r + 0.5f), 0f), brick * 0.98f, MaterialFactory.Brick, 0.4f, "stone", 3.2f, Quaternion.identity, Sfx.WoodKnock);
                        Track(go, null).rebuild = wp => DynamicBox("Brick", wp.pos, brick * 0.98f, MaterialFactory.Brick, 0.4f, "stone", 3.2f, wp.rot, Sfx.WoodKnock);
                        go.GetComponent<Rigidbody>().Sleep();
                    }
            }
            // domino row (curved)
            for (int i = 0; i < 22; i++)
            {
                float a = i * 0.12f;
                Vector3 p = c + new Vector3(4f + Mathf.Sin(a) * 6f, 0.6f, 4f + i * 0.45f - Mathf.Cos(a) * 1.5f);
                float yaw = Mathf.Atan2(Mathf.Cos(a) * 6f * 0.12f, 0.45f + Mathf.Sin(a) * 1.5f * 0.12f) * Mathf.Rad2Deg;
                var go = DynamicBox("Domino", p, new Vector3(0.5f, 1.2f, 0.1f), MaterialFactory.PaintBlue, 0f, "wood", 8f, Quaternion.Euler(0f, yaw, 0f), Sfx.WoodKnock);
                Track(go, null).rebuild = wp => DynamicBox("Domino", wp.pos, new Vector3(0.5f, 1.2f, 0.1f), MaterialFactory.PaintBlue, 0f, "wood", 8f, wp.rot, Sfx.WoodKnock);
            }
            // explosive barrels
            for (int i = 0; i < 6; i++) SpawnExplosiveBarrel(c + new Vector3(-12f + (i % 3) * 0.75f, 0.46f, 4f + (i / 3) * 0.75f));
            for (int i = 0; i < 3; i++) SpawnExplosiveBarrel(c + new Vector3(10f + i * 2.5f, 0.46f, -10f));
            // tire piles
            for (int p = 0; p < 3; p++)
                for (int k = 0; k < 4; k++) SpawnTire(c + new Vector3(-12f + p * 1.6f, 0.14f + k * 0.27f, 12f), 0f);
            // destructible planks: fences and a bridge
            var group = new DestructiblePlankGroup(this, propRoot);
            for (int i = 0; i < 8; i++) group.Add(c + new Vector3(6f + i * 0.3f, 0.9f, 12f), Quaternion.Euler(0f, 0f, 90f), new Vector3(1.8f, 0.2f, 0.05f));
            for (int i = 0; i < 6; i++) group.Add(c + new Vector3(10f, 1.05f, 6f + i * 0.42f), Quaternion.identity, new Vector3(3f, 0.06f, 0.4f));
            StaticBox("BridgeSupportA", c + new Vector3(8.4f, 0.5f, 7.05f), new Vector3(0.4f, 1f, 2.6f), MaterialFactory.Brick, 1f, "concrete");
            StaticBox("BridgeSupportB", c + new Vector3(11.6f, 0.5f, 7.05f), new Vector3(0.4f, 1f, 2.6f), MaterialFactory.Brick, 1f, "concrete");
            AddTicker(group);
        }
        #endregion

        #region Hazard pit
        void BuildHazardPit(Vector3 c)
        {
            float cell = size / 3f;
            StaticBox("HazardFloor", c + new Vector3(0f, -0.15f, 0f), new Vector3(cell - 4f, 0.3f, cell - 4f), MaterialFactory.Asphalt, 3f, "concrete");
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? 1f : -1f) * (cell * 0.5f - 2.4f), z = 0f;
                Vector3 sz = new Vector3(0.4f, 0.12f, cell - 5f);
                if (i >= 2) { x = 0f; z = (i == 2 ? 1f : -1f) * (cell * 0.5f - 2.4f); sz = new Vector3(cell - 5f, 0.12f, 0.4f); }
                StaticBox("HazardStripe", c + new Vector3(x, 0.06f, z), sz, MaterialFactory.Hazard, 1f, "concrete");
            }
            // spike bed
            {
                Vector3 p = c + new Vector3(-10f, 0f, -8f);
                StaticBox("SpikeBase", p + new Vector3(0f, 0.08f, 0f), new Vector3(4f, 0.16f, 4f), MaterialFactory.DarkMetal, 1f, "metal");
                var cone = MeshFactory.Cone(0.07f, 0.55f, 8);
                var spikes = new GameObject("Spikes");
                spikes.transform.SetParent(staticRoot, false);
                for (int x = 0; x < 8; x++)
                    for (int z = 0; z < 8; z++)
                    {
                        var s = StaticMesh("Spike", cone, p + new Vector3(-1.75f + x * 0.5f, 0.16f, -1.75f + z * 0.5f), Quaternion.identity, MaterialFactory.Spikes, "metal", false);
                        s.transform.SetParent(spikes.transform, true);
                    }
                var bed = new GameObject("SpikeBed");
                bed.transform.SetParent(root, false);
                bed.transform.position = p + new Vector3(0f, 0.42f, 0f);
                var bc = bed.AddComponent<BoxCollider>(); bc.size = new Vector3(4f, 0.5f, 4f); PhysMats.Apply(bc, "metal");
                bed.AddComponent<SpikeBed>();
                StaticBox("SpikeTower", p + new Vector3(0f, 2.5f, -3f), new Vector3(2f, 5f, 2f), MaterialFactory.Concrete, 1f, "concrete");
            }
            // spinning saw blades
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = c + new Vector3(-2f + i * 4f, 0f, -10f);
                StaticBox("SawStand", p + new Vector3(0f, 0.45f, 0f), new Vector3(0.3f, 0.9f, 1.4f), MaterialFactory.Hazard, 1f, "metal");
                var saw = new GameObject("SawBlade");
                saw.transform.SetParent(root, false);
                saw.transform.position = p + new Vector3(0f, 1.25f, 0f);
                saw.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                saw.AddComponent<MeshFilter>().sharedMesh = MeshFactory.SawBlade(0.7f, 0.025f, 24);
                saw.AddComponent<MeshRenderer>().sharedMaterial = MaterialFactory.Spikes;
                var colGo = new GameObject("SawCollider");
                colGo.transform.SetParent(saw.transform, false);
                colGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var mc = colGo.AddComponent<MeshCollider>();
                mc.sharedMesh = MeshFactory.Cylinder(0.68f, 0.05f, 16, 1f);
                mc.convex = true;
                PhysMats.Apply(mc, "metal");
                var rb = saw.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                var rot = saw.AddComponent<KinematicRotator>();
                rot.Setup(rb, Vector3.forward, 900f, 0f);
                rot.local = true;
                AddTicker(rot);
                saw.AddComponent<SawBlade>();
            }
            // hydraulic press
            {
                Vector3 p = c + new Vector3(8f, 0f, -9f);
                StaticBox("PressAnvil", p + new Vector3(0f, 0.3f, 0f), new Vector3(2.4f, 0.6f, 2.4f), MaterialFactory.DarkMetal, 1f, "metal");
                StaticBox("PressColL", p + new Vector3(-1.5f, 2.5f, 0f), new Vector3(0.4f, 5f, 0.4f), MaterialFactory.Hazard, 1f, "metal");
                StaticBox("PressColR", p + new Vector3(1.5f, 2.5f, 0f), new Vector3(0.4f, 5f, 0.4f), MaterialFactory.Hazard, 1f, "metal");
                StaticBox("PressTop", p + new Vector3(0f, 5.1f, 0f), new Vector3(3.4f, 0.4f, 1f), MaterialFactory.Hazard, 1f, "metal");
                Rigidbody rb;
                var head = KinematicBox("PressHead", p + new Vector3(0f, 3.6f, 0f), new Vector3(2.2f, 0.8f, 2.2f), MaterialFactory.Metal, 1f, "metal", out rb);
                var press = head.AddComponent<HydraulicPress>();
                press.Setup(rb, p + new Vector3(0f, 3.6f, 0f), p + new Vector3(0f, 1.0f, 0f));
                AddTicker(press);
            }
            // electric floor
            {
                Vector3 p = c + new Vector3(2f, 0f, 2f);
                var panels = new List<Renderer>();
                for (int x = 0; x < 3; x++)
                    for (int z = 0; z < 3; z++)
                    {
                        var g = StaticBox("ElectricPanel", p + new Vector3(-2f + x * 2f, 0.05f, -2f + z * 2f), new Vector3(1.9f, 0.1f, 1.9f), MaterialFactory.ElectricOff, 1f, "metal");
                        g.isStatic = false;
                        g.transform.SetParent(root, true);
                        panels.Add(g.GetComponent<MeshRenderer>());
                    }
                var trig = new GameObject("ElectricField");
                trig.transform.SetParent(root, false);
                trig.transform.position = p + new Vector3(0f, 0.5f, 0f);
                var tc = trig.AddComponent<BoxCollider>(); tc.isTrigger = true; tc.size = new Vector3(6f, 0.9f, 6f);
                var rbT = trig.AddComponent<Rigidbody>(); rbT.isKinematic = true;
                var ef = trig.AddComponent<ElectricFloor>();
                ef.panels = panels.ToArray();
                AddTicker(ef);
            }
            // cannon launcher
            {
                Vector3 p = c + new Vector3(-8f, 0f, 8f);
                Quaternion aim = Quaternion.Euler(-35f, 45f, 0f);
                StaticBox("CannonBase", p + new Vector3(0f, 0.5f, 0f), new Vector3(2.2f, 1f, 2.2f), MaterialFactory.DarkMetal, 1f, "metal");
                var barrel = MeshFactory.Lathe(new[] { new Vector2(0.7f, -1.5f), new Vector2(0.62f, -1.4f), new Vector2(0.58f, 1.6f), new Vector2(0.66f, 1.7f), new Vector2(0.66f, 1.9f), new Vector2(0.5f, 1.9f), new Vector2(0.5f, -1.2f) }, 18, 1f, 1f, true, false);
                var bg = StaticMesh("CannonBarrel", barrel, p + new Vector3(0f, 1.6f, 0f), aim * Quaternion.Euler(90f, 0f, 0f), MaterialFactory.DarkMetal, "metal", false);
                var trig = new GameObject("CannonBreech");
                trig.transform.SetParent(root, false);
                Vector3 dir = aim * Vector3.forward;
                trig.transform.position = p + new Vector3(0f, 1.6f, 0f) - dir * 1.1f + Vector3.up * 0.2f;
                var sc = trig.AddComponent<SphereCollider>(); sc.isTrigger = true; sc.radius = 1.1f;
                var rbT = trig.AddComponent<Rigidbody>(); rbT.isKinematic = true;
                var cannon = trig.AddComponent<Cannon>();
                cannon.Setup(dir, p + new Vector3(0f, 1.6f, 0f) + dir * 1.9f);
                AddTicker(cannon);
                StaticBox("CannonRamp", p + new Vector3(-2.2f, 0.5f, -2.2f), new Vector3(2f, 1f, 2f), MaterialFactory.Concrete, 1f, "concrete", Quaternion.Euler(0f, 45f, 0f));
            }
            // landmines field
            for (int i = 0; i < 5; i++)
            {
                Vector3 p = c + new Vector3(10f + (i % 3) * 2.2f, 0.01f, 6f + (i / 3) * 2.5f);
                Func<WorldProp, GameObject> make = wp =>
                {
                    var ep = Sandbox.Explosions.Spawn(ExplosiveKind.Landmine, wp.pos, wp.rot, Vector3.zero, propRoot);
                    ep.IsWorldProp = true;
                    return ep.gameObject;
                };
                var wp0 = new WorldProp { pos = p, rot = Quaternion.identity, rebuild = make };
                wp0.go = make(wp0);
                wp0.rb = wp0.go.GetComponent<Rigidbody>();
                props.Add(wp0);
            }
            StaticBox("MineSign", c + new Vector3(12f, 1f, 3.5f), new Vector3(1.6f, 0.9f, 0.08f), MaterialFactory.Hazard, 1f, "wood");
        }
        #endregion

        #region Tower and cliff
        void BuildTowerAndCliff(Vector3 c)
        {
            // cliff plateau with a long ramp to the top
            float top = 12f;
            Vector3 plateau = c + new Vector3(6f, 0f, 6f);
            StaticBox("Cliff", plateau + new Vector3(0f, top * 0.5f, 0f), new Vector3(12f, top, 10f), MaterialFactory.Rock, 3f, "stone");
            StaticBox("CliffTop", plateau + new Vector3(0f, top + 0.05f, 0f), new Vector3(12f, 0.1f, 10f), MaterialFactory.Grass, 3f, "grass");
            float run = 24f;
            float slope = Mathf.Atan2(top, run) * Mathf.Rad2Deg;
            var ramp = MeshFactory.Wedge(new Vector3(3f, top, run), 3f);
            StaticMesh("CliffRamp", ramp, plateau + new Vector3(-6f - run * 0.5f, 0f, -3.5f), Quaternion.Euler(0f, 90f, 0f), MaterialFactory.Concrete, "concrete", true);
            StaticBox("CliffRampRail", plateau + new Vector3(-6f - run * 0.5f, top * 0.5f + 0.3f, -5.1f), new Vector3(run, 0.15f, 0.1f), MaterialFactory.Hazard, 1f, "metal",
                Quaternion.Euler(0f, 0f, slope), true);
            // soft landing pile at the bottom of the drop
            for (int i = 0; i < 6; i++) SpawnTire(plateau + new Vector3(-2f + (i % 3) * 1.2f, 0.15f + (i / 3) * 0.3f, -7f), 0f);
            // tower with platforms at 5/10/15/20 m, served by an elevator
            Vector3 t = c + new Vector3(-8f, 0f, -8f);
            for (int l = 1; l <= 4; l++)
            {
                float y = l * 5f;
                StaticBox("TowerDeck", t + new Vector3(0f, y, 0f), new Vector3(4f, 0.3f, 4f), MaterialFactory.Tread, 1f, "metal");
                StaticBox("TowerRailN", t + new Vector3(0f, y + 0.6f, 2f), new Vector3(4f, 0.9f, 0.08f), MaterialFactory.Glass, 1f, "metal");
                StaticBox("TowerRailW", t + new Vector3(-2f, y + 0.6f, 0f), new Vector3(0.08f, 0.9f, 4f), MaterialFactory.Glass, 1f, "metal");
            }
            for (int k = 0; k < 4; k++)
            {
                float x = (k % 2 == 0 ? -1.8f : 1.8f), z = (k < 2 ? -1.8f : 1.8f);
                StaticBox("TowerLeg", t + new Vector3(x, 10.1f, z), new Vector3(0.3f, 20.2f, 0.3f), MaterialFactory.DarkMetal, 1f, "metal");
            }
            Rigidbody rb;
            var lift = KinematicBox("Elevator", t + new Vector3(3.6f, 0.15f, 0f), new Vector3(3f, 0.3f, 3f), MaterialFactory.Hazard, 1f, "metal", out rb);
            var mover = lift.AddComponent<KinematicMover>();
            mover.Setup(rb, t + new Vector3(3.6f, 0.15f, 0f), t + new Vector3(3.6f, 20.15f, 0f), 0f, 2.2f, 3f);
            AddTicker(mover);
            Registry.Movers[lift.GetComponent<Collider>()] = mover;
            StaticBox("ElevatorShaft", t + new Vector3(5.2f, 10f, 0f), new Vector3(0.2f, 20f, 3f), MaterialFactory.Glass, 1f, "metal");
        }
        #endregion

        public void Tick(float dt)
        {
            var gs = Sandbox.Boot.graphics;
            if (gs.dayNightCycle)
            {
                TimeOfDay += dt * 24f / Mathf.Max(1f, gs.dayLengthMinutes * 60f);
                gs.timeOfDay = Mathf.Repeat(TimeOfDay, 24f);
                ApplyTimeOfDay(TimeOfDay);
            }
            else if (Mathf.Abs(gs.timeOfDay - TimeOfDay) > 0.001f) ApplyTimeOfDay(gs.timeOfDay);
            RenderSettings.fog = gs.fog;
            RenderSettings.fogDensity = gs.fogDensity;
            for (int i = 0; i < tickers.Count; i++) tickers[i].WorldTick(dt);
        }

        public void FixedTick(float dt)
        {
            for (int i = 0; i < tickers.Count; i++) tickers[i].WorldFixedTick(dt);
        }
    }
    #endregion

    #region Hazards
    /// <summary>Plays material-appropriate impact sounds for props.</summary>
    public class PropImpact : MonoBehaviour
    {
        public Sfx sound = Sfx.Thud0;
        float last;
        Rigidbody rb;

        void Awake() { rb = GetComponent<Rigidbody>(); }

        void OnCollisionEnter(Collision c)
        {
            if (Sandbox.Audio == null || Time.time - last < 0.08f) return;
            float imp = c.impulse.magnitude;
            if (rb == null) rb = GetComponent<Rigidbody>();
            float m = rb != null ? rb.mass : 10f;
            float vol = Mathf.Clamp01(imp / (m * 3f + 10f));
            if (vol < 0.04f) return;
            last = Time.time;
            float pitch = Mathf.Lerp(1.3f, 0.7f, Mathf.Clamp01(m / 60f));
            Vector3 p = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Sandbox.Audio.Play(sound, p, vol, pitch * UnityEngine.Random.Range(0.92f, 1.08f));
        }
    }

    /// <summary>Kinematic platform moving between two points (ping-pong, smooth ends, pauses).</summary>
    public class KinematicMover : MonoBehaviour, IWorldTick, IMovingSurface
    {
        Rigidbody rb;
        Vector3 a, b;
        float speed, pause, t, dir = 1f, wait;
        Vector3 velocity;

        public void Setup(Rigidbody body, Vector3 from, Vector3 to, float phase, float spd, float pauseTime)
        {
            rb = body; a = from; b = to; speed = Mathf.Max(0.05f, spd); pause = pauseTime; t = Mathf.Clamp01(phase);
        }

        public void WorldFixedTick(float dt)
        {
            if (rb == null) return;
            float len = Mathf.Max(0.01f, (b - a).magnitude);
            if (wait > 0f) { wait -= dt; velocity = Vector3.zero; return; }
            t += dir * speed * dt / len;
            if (t >= 1f) { t = 1f; dir = -1f; wait = pause; }
            if (t <= 0f) { t = 0f; dir = 1f; wait = pause; }
            Vector3 p = Vector3.Lerp(a, b, MathUtil.SmoothStep01(t));
            velocity = (p - rb.position) / Mathf.Max(1e-4f, dt);
            rb.MovePosition(p);
        }

        public void WorldTick(float dt) { }

        public Vector3 GetSurfaceVelocity(Vector3 worldPoint) { return velocity; }
    }

    /// <summary>Kinematic rotation: constant spin or pendulum swing.</summary>
    public class KinematicRotator : MonoBehaviour, IWorldTick, IMovingSurface
    {
        Rigidbody rb;
        Vector3 axis;
        float degPerSec, angle;
        bool swing;
        float amplitude, period, phase;
        Quaternion baseRot;
        public bool local;
        Vector3 lastAngVel;

        public void Setup(Rigidbody body, Vector3 ax, float speed, float startAngle)
        {
            rb = body; axis = ax.normalized; degPerSec = speed; angle = startAngle; baseRot = body.rotation;
        }

        public void Swing(float amp, float per, float ph) { swing = true; amplitude = amp; period = per; phase = ph; }

        public void WorldFixedTick(float dt)
        {
            if (rb == null) return;
            if (swing) angle = Mathf.Sin((Time.time + phase) / period * Mathf.PI * 2f) * amplitude;
            else angle += degPerSec * dt;
            Quaternion q = local ? baseRot * Quaternion.AngleAxis(angle, axis) : Quaternion.AngleAxis(angle, axis) * baseRot;
            Quaternion prev = rb.rotation;
            rb.MoveRotation(q);
            Vector3 err = MathUtil.RotationError(prev, q);
            lastAngVel = err / Mathf.Max(1e-4f, dt);
        }

        public void WorldTick(float dt) { }

        public Vector3 GetSurfaceVelocity(Vector3 worldPoint) { return Vector3.Cross(lastAngVel, worldPoint - rb.position); }
    }

    /// <summary>Conveyor belt: the classic kinematic trick so friction carries objects along; scrolling texture.</summary>
    public class Conveyor : MonoBehaviour, IWorldTick, IMovingSurface
    {
        Rigidbody rb;
        Vector3 dir;
        float speed;
        Material mat;
        Vector3 home;
        float offset;

        public void Setup(Rigidbody body, Vector3 direction, float spd, Material m)
        {
            rb = body; dir = direction.normalized; speed = spd; mat = m; home = body.position;
        }

        public void WorldFixedTick(float dt)
        {
            if (rb == null) return;
            rb.position = home - dir * speed * dt;
            rb.MovePosition(home);
        }

        public void WorldTick(float dt)
        {
            if (mat == null) return;
            offset += speed * dt / 8f * 6f;
            mat.mainTextureOffset = new Vector2(0f, -offset);
        }

        public Vector3 GetSurfaceVelocity(Vector3 worldPoint) { return dir * speed; }
    }

    /// <summary>Trampoline: launches any rigidbody (and whole ragdolls) entering from above.</summary>
    public class TrampolinePad : MonoBehaviour
    {
        public float speed = 12f;
        readonly Dictionary<Rigidbody, float> last = new Dictionary<Rigidbody, float>();
        float lastRagdoll;

        void OnTriggerEnter(Collider other) { Bounce(other); }
        void OnTriggerStay(Collider other) { Bounce(other); }

        void Bounce(Collider other)
        {
            var cc = other as CharacterController;
            if (cc != null && Sandbox.Player != null) { Sandbox.Player.Bounce(speed * 0.8f); return; }
            Rigidbody rb = other.attachedRigidbody;
            if (rb == null || rb.isKinematic) return;
            BodyPart part = Registry.PartOf(rb);
            if (part != null && part.body != null)
            {
                if (Time.time - lastRagdoll < 0.4f) return;
                if (Compat.GetVelocity(rb).y > 2f) return;
                lastRagdoll = Time.time;
                var body = part.body;
                for (int i = 0; i < body.AllPieces.Count; i++)
                {
                    var p = body.AllPieces[i];
                    if (p == null || !p.attached && p != part) continue;
                    Vector3 v = Compat.GetVelocity(p.rb);
                    v.y = Mathf.Max(v.y, speed);
                    Compat.SetVelocity(p.rb, v);
                }
                if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Spring, transform.position, 0.8f, 1f);
                return;
            }
            float t;
            if (last.TryGetValue(rb, out t) && Time.time - t < 0.3f) return;
            if (Compat.GetVelocity(rb).y > 2f) return;
            last[rb] = Time.time;
            Vector3 vel = Compat.GetVelocity(rb);
            vel.y = speed;
            Compat.SetVelocity(rb, vel);
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Spring, transform.position, 0.6f, 1.1f);
        }
    }

    /// <summary>Spike bed: pierce damage scaled by impact speed; hard impacts impale (sticky joint that can tear free).</summary>
    public class SpikeBed : MonoBehaviour
    {
        readonly Dictionary<BodyPart, float> cool = new Dictionary<BodyPart, float>();

        void OnCollisionEnter(Collision c) { Hit(c, true); }
        void OnCollisionStay(Collision c) { Hit(c, false); }

        void Hit(Collision c, bool enter)
        {
            BodyPart part = Registry.PartOf(c.collider);
            if (part == null || part.body == null || c.contactCount == 0) return;
            float t;
            if (cool.TryGetValue(part, out t) && Time.time - t < 0.35f) return;
            cool[part] = Time.time;
            float speed = c.relativeVelocity.magnitude;
            float press = part.rb.mass * Physics.gravity.magnitude;
            float dmg = enter ? 8f + speed * 6f : Mathf.Clamp(press * 0.02f, 0.5f, 4f);
            var cp = c.GetContact(0);
            var d = DamageInfo.Make(DamageType.Pierce, dmg, cp.point, Vector3.down);
            d.normal = cp.normal;
            d.depth = Mathf.Clamp(0.02f + speed * 0.01f, 0.02f, 0.08f);
            part.ApplyDamage(d);
            if (enter && speed > 4.5f && Sandbox.Gore != GoreLevel.Off && part.GetComponent<FixedJoint>() == null)
            {
                var fj = part.gameObject.AddComponent<FixedJoint>();
                fj.breakForce = 1400f;
                fj.breakTorque = 600f;
                if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Stab, cp.point, 0.9f, 0.9f);
                SandboxEvents.RaiseToast("Impaled!", new Color(1f, 0.4f, 0.35f));
            }
        }
    }

    /// <summary>Spinning saw: slices body parts at the blade plane.</summary>
    public class SawBlade : MonoBehaviour
    {
        readonly Dictionary<BodyPart, float> cool = new Dictionary<BodyPart, float>();
        int loop = -1;

        void Start()
        {
            if (Sandbox.Audio != null) loop = Sandbox.Audio.StartLoop(Sfx.Saw, transform, 0.35f, 1f, 300);
        }

        void OnDestroy()
        {
            if (loop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(loop);
        }

        void OnCollisionEnter(Collision c) { Hit(c); }
        void OnCollisionStay(Collision c) { Hit(c); }

        void Hit(Collision c)
        {
            if (c.contactCount == 0) return;
            var cp = c.GetContact(0);
            BodyPart part = Registry.PartOf(c.collider);
            if (Sandbox.Fx != null) Sandbox.Fx.Impact(cp.point, cp.normal, true, false);
            if (part == null || part.body == null) return;
            float t;
            if (cool.TryGetValue(part, out t) && Time.time - t < 0.25f) return;
            cool[part] = Time.time;
            var d = DamageInfo.Make(DamageType.Cut, Sandbox.Boot.damage.bladeDamage * 0.8f, cp.point, -cp.normal);
            d.normal = cp.normal;
            d.depth = 0.05f;
            d.radius = 0.06f;
            part.ApplyDamage(d);
            if (Sandbox.Fx != null) Sandbox.Fx.EmitBloodSpray(cp.point, (cp.normal + transform.up).normalized, 5f, 25f, 16, 0.02f, Sandbox.Boot.blood.arterial, Vector3.zero);
            if (Sandbox.Gore == GoreLevel.Full && part.body.Dismember.Slice(part, cp.point, transform.forward))
            {
                if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Slice, cp.point, 1f, 0.8f);
            }
        }
    }

    /// <summary>Hydraulic press: slams down periodically and crushes whatever is trapped against the anvil.</summary>
    public class HydraulicPress : MonoBehaviour, IWorldTick, IMovingSurface
    {
        Rigidbody rb;
        Vector3 up, down;
        float cycle;
        Vector3 vel;
        readonly Dictionary<BodyPart, float> cool = new Dictionary<BodyPart, float>();
        bool slamSound;

        public void Setup(Rigidbody body, Vector3 top, Vector3 bottom) { rb = body; up = top; down = bottom; }

        public void WorldFixedTick(float dt)
        {
            if (rb == null) return;
            cycle += dt;
            float period = 5f;
            float c = cycle % period;
            float u;
            if (c < 1.6f) { u = 0f; slamSound = false; }
            else if (c < 2.0f)
            {
                u = MathUtil.SmoothStep01((c - 1.6f) / 0.4f);
                if (!slamSound && Sandbox.Audio != null) { slamSound = true; Sandbox.Audio.Play(Sfx.Press, rb.position, 0.9f, 1f); }
            }
            else if (c < 3.2f) u = 1f;
            else u = 1f - MathUtil.SmoothStep01((c - 3.2f) / 1.8f);
            Vector3 p = Vector3.Lerp(up, down, u);
            vel = (p - rb.position) / Mathf.Max(1e-4f, dt);
            rb.MovePosition(p);
        }

        public void WorldTick(float dt) { }
        public Vector3 GetSurfaceVelocity(Vector3 worldPoint) { return vel; }

        void OnCollisionStay(Collision c)
        {
            BodyPart part = Registry.PartOf(c.collider);
            if (part == null || part.body == null || c.contactCount == 0) return;
            float h = (rb.position - down).magnitude;
            if (h > 0.6f) return;
            float t;
            if (cool.TryGetValue(part, out t) && Time.time - t < 0.12f) return;
            cool[part] = Time.time;
            var cp = c.GetContact(0);
            var d = DamageInfo.Make(DamageType.Crush, Sandbox.Boot.damage.crushDamagePerSecond * 0.12f * (1f + (0.6f - h) * 3f), cp.point, Vector3.down);
            d.normal = Vector3.up;
            d.impulse = 0f;
            part.ApplyDamage(d);
            if (Sandbox.Audio != null && UnityEngine.Random.value < 0.25f) Sandbox.Audio.Play(Sfx.Crush, cp.point, 0.8f, UnityEngine.Random.Range(0.8f, 1.1f));
            if (Sandbox.Fx != null && Sandbox.Gore != GoreLevel.Off && UnityEngine.Random.value < 0.4f)
                Sandbox.Fx.EmitBloodSpray(cp.point, Vector3.up, 3f, 70f, 8, 0.02f, Sandbox.Boot.blood.venous, Vector3.zero);
            if (part.crush > 120f && part.Joint != null && Sandbox.Gore == GoreLevel.Full && UnityEngine.Random.value < 0.05f) part.body.Dismember.Sever(part, false);
        }
    }

    /// <summary>Electric floor: cycles on/off; parts inside take shock damage and spasm.</summary>
    public class ElectricFloor : MonoBehaviour, IWorldTick
    {
        public Renderer[] panels;
        bool on;
        float timer;
        int loop = -1;
        readonly Dictionary<BodyPart, float> cool = new Dictionary<BodyPart, float>();
        float zapTimer;

        public void WorldFixedTick(float dt) { }

        public void WorldTick(float dt)
        {
            timer += dt;
            bool want = (timer % 4.5f) < 2.2f;
            if (want != on)
            {
                on = want;
                for (int i = 0; i < panels.Length; i++) if (panels[i] != null) panels[i].sharedMaterial = on ? MaterialFactory.Electric : MaterialFactory.ElectricOff;
                if (Sandbox.Audio != null)
                {
                    if (on && loop < 0) loop = Sandbox.Audio.StartLoop(Sfx.Buzz, transform, 0.3f, 1f, 400);
                    if (!on && loop >= 0) { Sandbox.Audio.StopLoop(loop); loop = -1; }
                }
            }
            if (on)
            {
                zapTimer -= dt;
                if (zapTimer <= 0f && Sandbox.Fx != null)
                {
                    zapTimer = UnityEngine.Random.Range(0.08f, 0.3f);
                    Vector3 a = transform.position + new Vector3(UnityEngine.Random.Range(-3f, 3f), -0.4f, UnityEngine.Random.Range(-3f, 3f));
                    Sandbox.Fx.Lightning(a, a + new Vector3(UnityEngine.Random.Range(-0.6f, 0.6f), UnityEngine.Random.Range(0.2f, 0.7f), UnityEngine.Random.Range(-0.6f, 0.6f)),
                        new Color(0.6f, 0.85f, 1f, 1f), 0.03f, 0.08f);
                }
            }
        }

        void OnTriggerStay(Collider other)
        {
            if (!on) return;
            var cc = other as CharacterController;
            if (cc != null && Sandbox.Cam != null) { Sandbox.Cam.AddShake(0.05f); return; }
            BodyPart part = Registry.PartOf(other);
            if (part == null || part.body == null) return;
            float t;
            if (cool.TryGetValue(part, out t) && Time.time - t < 0.12f) return;
            cool[part] = Time.time;
            var d = DamageInfo.Make(DamageType.Shock, Sandbox.Boot.damage.shockDamagePerSecond * 0.12f * 1.5f, part.WorldCenter, Vector3.up);
            part.ApplyDamage(d);
            if (Sandbox.Fx != null) Sandbox.Fx.ElectricSparks(part.WorldCenter, Vector3.up, 3);
        }

        void OnDestroy() { if (loop >= 0 && Sandbox.Audio != null) Sandbox.Audio.StopLoop(loop); }
    }

    /// <summary>Cannon: whatever lands in the breech gets launched after a short fuse.</summary>
    public class Cannon : MonoBehaviour, IWorldTick
    {
        Vector3 dir, muzzle;
        float fuse = -1f;
        RagdollBody loadedBody;
        Rigidbody loadedProp;
        bool loadedPlayer;
        int fuseLoop = -1;

        public void Setup(Vector3 direction, Vector3 muzzlePos) { dir = direction.normalized; muzzle = muzzlePos; }

        void OnTriggerEnter(Collider other)
        {
            if (fuse >= 0f) return;
            var cc = other as CharacterController;
            if (cc != null) { loadedPlayer = true; Arm(); return; }
            BodyPart part = Registry.PartOf(other);
            if (part != null && part.body != null) { loadedBody = part.body; Arm(); return; }
            Rigidbody rb = other.attachedRigidbody;
            if (rb != null && !rb.isKinematic) { loadedProp = rb; Arm(); }
        }

        void Arm()
        {
            fuse = 1.3f;
            if (Sandbox.Audio != null) fuseLoop = Sandbox.Audio.StartLoop(Sfx.Fuse, transform, 0.4f, 1.2f, 500);
            SandboxEvents.RaiseToast("Cannon loaded!", new Color(1f, 0.8f, 0.5f));
        }

        public void WorldFixedTick(float dt) { }

        public void WorldTick(float dt)
        {
            if (fuse < 0f) return;
            fuse -= dt;
            if (fuse > 0f) return;
            fuse = -1f;
            if (fuseLoop >= 0 && Sandbox.Audio != null) { Sandbox.Audio.StopLoop(fuseLoop); fuseLoop = -1; }
            float speed = Sandbox.Boot.world.cannonSpeed;
            if (loadedBody != null)
            {
                for (int i = 0; i < loadedBody.AllPieces.Count; i++)
                {
                    var p = loadedBody.AllPieces[i];
                    if (p == null) continue;
                    p.rb.position = p.rb.position + (muzzle - loadedBody.CenterOfMass) * 0.8f;
                    Compat.SetVelocity(p.rb, dir * speed * UnityEngine.Random.Range(0.97f, 1.03f));
                }
                loadedBody.Vitals.AddAdrenaline(1f);
            }
            if (loadedProp != null) { loadedProp.position = muzzle; Compat.SetVelocity(loadedProp, dir * speed); }
            if (loadedPlayer && Sandbox.Player != null) Sandbox.Player.Launch(muzzle, dir * speed * 0.7f);
            loadedBody = null; loadedProp = null; loadedPlayer = false;
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.Cannon, muzzle, 1f, 1f, 6f);
            if (Sandbox.Fx != null)
            {
                for (int i = 0; i < 12; i++) FxSystem.Emit(Sandbox.Fx.Smoke, muzzle, dir * UnityEngine.Random.Range(2f, 8f) + UnityEngine.Random.onUnitSphere, UnityEngine.Random.Range(0.6f, 1.2f), UnityEngine.Random.Range(2f, 4f), new Color(0.8f, 0.8f, 0.8f, 1f));
                Sandbox.Fx.MuzzleFlash(muzzle, dir, 4f);
            }
            if (Sandbox.Cam != null) Sandbox.Cam.AddShake(0.4f);
        }
    }

    /// <summary>Breakable wooden planks: split into two pieces on strong impacts or damage. Resets with the scene.</summary>
    public class DestructiblePlankGroup : IWorldTick
    {
        struct Spec { public Vector3 pos; public Quaternion rot; public Vector3 size; }
        readonly WorldBuilder world;
        readonly Transform parent;
        readonly List<Spec> specs = new List<Spec>();
        readonly List<GameObject> spawned = new List<GameObject>();

        public DestructiblePlankGroup(WorldBuilder w, Transform p) { world = w; parent = p; }

        public void Add(Vector3 pos, Quaternion rot, Vector3 size)
        {
            specs.Add(new Spec { pos = pos, rot = rot, size = size });
            spawned.Add(Make(pos, rot, size));
        }

        GameObject Make(Vector3 pos, Quaternion rot, Vector3 size)
        {
            var go = world.DynamicBox("Plank", pos, size, MaterialFactory.Wood, 0.6f, "wood", Mathf.Max(1f, size.x * size.y * size.z * 600f), rot, Sfx.WoodKnock);
            go.transform.SetParent(parent, true);
            var bp = go.AddComponent<BreakablePlank>();
            bp.size = size;
            var col = go.GetComponent<Collider>();
            Registry.RegisterDamageable(col, bp);
            return go;
        }

        public void Reset()
        {
            for (int i = 0; i < spawned.Count; i++) if (spawned[i] != null) UnityEngine.Object.Destroy(spawned[i]);
            spawned.Clear();
            for (int i = 0; i < specs.Count; i++) spawned.Add(Make(specs[i].pos, specs[i].rot, specs[i].size));
            BreakablePlank.ClearDebris();
        }

        public void WorldFixedTick(float dt) { }
        public void WorldTick(float dt) { }
    }

    public class BreakablePlank : MonoBehaviour, IDamageable
    {
        public Vector3 size;
        public float health = 30f;
        bool broken;
        static readonly List<GameObject> debris = new List<GameObject>();

        public static void ClearDebris()
        {
            for (int i = 0; i < debris.Count; i++) if (debris[i] != null) Destroy(debris[i]);
            debris.Clear();
        }

        void OnCollisionEnter(Collision c)
        {
            float imp = c.impulse.magnitude;
            if (imp > 45f) ApplyDamage(DamageInfo.Make(DamageType.Blunt, (imp - 45f) * 0.5f, c.contactCount > 0 ? c.GetContact(0).point : transform.position, Vector3.down));
        }

        public void ApplyDamage(DamageInfo info)
        {
            if (broken) return;
            health -= info.amount;
            if (health > 0f) return;
            broken = true;
            Break(info.point);
        }

        void Break(Vector3 at)
        {
            var rb = GetComponent<Rigidbody>();
            Vector3 v = rb != null ? Compat.GetVelocity(rb) : Vector3.zero;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            Vector3 local = transform.InverseTransformPoint(at);
            float split = Mathf.Clamp(local[axis], -size[axis] * 0.3f, size[axis] * 0.3f);
            for (int k = 0; k < 2; k++)
            {
                float len = k == 0 ? size[axis] * 0.5f + split : size[axis] * 0.5f - split;
                Vector3 sz = size; sz[axis] = Mathf.Max(0.05f, len);
                Vector3 off = Vector3.zero; off[axis] = k == 0 ? (-size[axis] * 0.5f + len * 0.5f) : (size[axis] * 0.5f - len * 0.5f);
                var go = Sandbox.World.DynamicBox("PlankPiece", transform.TransformPoint(off), sz, MaterialFactory.Wood, 0.6f, "wood", Mathf.Max(0.5f, sz.x * sz.y * sz.z * 600f), transform.rotation, Sfx.WoodKnock);
                var r2 = go.GetComponent<Rigidbody>();
                Compat.SetVelocity(r2, v + UnityEngine.Random.onUnitSphere * 1.5f);
                debris.Add(go);
                if (debris.Count > 80) { if (debris[0] != null) Destroy(debris[0]); debris.RemoveAt(0); }
            }
            if (Sandbox.Fx != null) Sandbox.Fx.Impact(at, Vector3.up, false, true);
            if (Sandbox.Audio != null) Sandbox.Audio.Play(Sfx.BoneCrack, at, 0.8f, 0.6f);
            var col = GetComponent<Collider>();
            if (col != null) Registry.Unregister(col);
            Destroy(gameObject);
        }
    }
    #endregion
}
