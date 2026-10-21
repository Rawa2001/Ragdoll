// F3 debug overlay (COM, capture point, support polygon, foot targets, swing paths, joint loads, states, counters)
// and the startup self-test that checks the ragdoll can stand and walk, and raises the assist when it cannot.
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace RagdollSandbox
{
    /// <summary>Draws the balance/locomotion state of every ragdoll as one dynamic line mesh (no GameObject per line,
    /// no per-frame allocation) and shows world labels and performance counters.</summary>
    public class DebugOverlay
    {
        const int MaxVerts = 12000;
        const int MaxLabels = 8;
        Mesh mesh;
        Material mat;
        readonly Vector3[] verts = new Vector3[MaxVerts];
        readonly Color32[] cols = new Color32[MaxVerts];
        readonly int[] idx = new int[MaxVerts];
        int count;
        GameObject canvasGo;
        Text counters;
        readonly Text[] labels = new Text[MaxLabels];
        readonly StringBuilder sb = new StringBuilder(1024);
        float textTimer;
        bool visible;

        public bool Visible { get { return visible; } }

        public void Build(Transform root)
        {
            mesh = new Mesh();
            mesh.name = "DebugLines";
            mesh.MarkDynamic();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            Shader s = Shader.Find("Hidden/Internal-Colored");
            if (s != null)
            {
                mat = new Material(s);
                mat.hideFlags = HideFlags.HideAndDontSave;
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                mat.renderQueue = 4000;
            }
            else mat = MaterialFactory.Line;

            canvasGo = new GameObject("Debug Overlay");
            canvasGo.transform.SetParent(root, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 60;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            Font font = Compat.BuiltinFont();
            counters = MakeText(canvasGo.transform, font, 17, TextAnchor.UpperLeft);
            var rt = counters.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(16f, -150f);
            rt.sizeDelta = new Vector2(560f, 560f);
            for (int i = 0; i < MaxLabels; i++)
            {
                labels[i] = MakeText(canvasGo.transform, font, 15, TextAnchor.LowerCenter);
                labels[i].rectTransform.sizeDelta = new Vector2(360f, 90f);
                labels[i].gameObject.SetActive(false);
            }
            canvasGo.SetActive(false);
        }

        static Text MakeText(Transform parent, Font font, int size, TextAnchor anchor)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = new Color(0.85f, 1f, 0.85f);
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = go.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
            sh.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        public void LateTick()
        {
            var b = Sandbox.Boot;
            bool want = b.debug.showOverlay && !Sandbox.InMenu;
            if (want != visible) { visible = want; canvasGo.SetActive(want); textTimer = 0f; }
            if (!visible) return;
            count = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r != null && !r.Destroyed && !r.Puppet) DrawBody(r);
            }
            mesh.Clear(false);
            if (count > 0)
            {
                mesh.SetVertices(verts, 0, count);
                mesh.SetColors(cols, 0, count);
                mesh.SetIndices(idx, 0, count, MeshTopology.Lines, 0, false);
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
                if (mat != null) Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0, Sandbox.MainCamera, 0, null, false, false);
            }
            textTimer -= Time.unscaledDeltaTime;
            if (textTimer <= 0f)
            {
                textTimer = 0.2f;
                UpdateCounters();
            }
            UpdateLabels();
        }

        void Line(Vector3 a, Vector3 c, Color32 col)
        {
            if (count + 2 > MaxVerts) return;
            verts[count] = a; cols[count] = col; idx[count] = count; count++;
            verts[count] = c; cols[count] = col; idx[count] = count; count++;
        }

        void Cross(Vector3 p, float s, Color32 col)
        {
            Line(p - Vector3.right * s, p + Vector3.right * s, col);
            Line(p - Vector3.up * s, p + Vector3.up * s, col);
            Line(p - Vector3.forward * s, p + Vector3.forward * s, col);
        }

        void Circle(Vector3 c, float r, Color32 col, int seg = 16)
        {
            Vector3 prev = c + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 p = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Line(prev, p, col);
                prev = p;
            }
        }

        static readonly Color32 ColCom = new Color32(255, 230, 40, 255);
        static readonly Color32 ColCapture = new Color32(255, 80, 220, 255);
        static readonly Color32 ColHull = new Color32(60, 255, 120, 255);
        static readonly Color32 ColHullBad = new Color32(255, 70, 60, 255);
        static readonly Color32 ColTarget = new Color32(70, 170, 255, 255);
        static readonly Color32 ColSwing = new Color32(140, 220, 255, 200);
        static readonly Color32 ColPlanted = new Color32(255, 255, 255, 220);
        static readonly Color32 ColVel = new Color32(255, 160, 40, 255);
        static readonly Color32 ColAssist = new Color32(255, 60, 60, 255);

        void DrawBody(RagdollBody r)
        {
            var bal = r.Balance;
            var loc = r.Locomotion;
            if (bal == null || loc == null) return;
            float gy = bal.HasGround ? bal.GroundHeight + 0.01f : r.CenterOfMass.y - 1f;
            Vector3 com = r.CenterOfMass;
            Vector3 comGround = new Vector3(com.x, gy, com.z);
            Cross(com, 0.08f, ColCom);
            Line(com, comGround, ColCom);
            Vector3 cp = new Vector3(bal.CapturePoint.x, gy, bal.CapturePoint.z);
            Circle(cp, 0.04f, ColCapture, 10);
            Line(comGround, cp, ColCapture);
            Line(com, com + MathUtil.Flat(r.ComVelocity) * 0.5f, ColVel);
            Line(com + Vector3.up * 0.02f, com + Vector3.up * 0.02f + loc.DesiredVelocity * 0.5f, ColTarget);

            var hc = bal.SupportDistance > 0f ? ColHullBad : ColHull;
            for (int i = 0; i < bal.HullCount; i++)
            {
                Vector2 a = bal.Hull[i], c = bal.Hull[(i + 1) % bal.HullCount];
                Line(new Vector3(a.x, gy, a.y), new Vector3(c.x, gy, c.y), hc);
            }
            for (int s = 0; s < 2; s++)
            {
                var f = loc.Feet[s];
                if (f.phase == FootPhase.Stance || f.phase == FootPhase.Plant)
                {
                    Circle(f.planted, 0.05f, ColPlanted, 8);
                }
                else
                {
                    Circle(f.target, 0.06f, ColTarget, 10);
                    // swing arc lift -> target with the clearance
                    Vector3 prev = f.lift;
                    for (int k = 1; k <= 10; k++)
                    {
                        float t = k / 10f;
                        Vector3 p = Vector3.Lerp(f.lift, f.target, t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * f.clearance);
                        Line(prev, p, ColSwing);
                        prev = p;
                    }
                    Line(f.swingPos, f.swingPos + Vector3.up * 0.1f, ColTarget);
                }
            }
            // per-joint drive load: a short bar along the bone, green (idle) -> red (saturated)
            var mus = r.Muscles;
            for (int i = 0; i < r.AllPieces.Count; i++)
            {
                var p = r.AllPieces[i];
                if (p == null || p.rb == null || !p.attached || p.HasAnatomicalJoint == false) continue;
                float l = Mathf.Clamp01(mus.load[(int)p.id]);
                var col = new Color32((byte)(60 + 195 * l), (byte)(255 - 200 * l), 60, 255);
                Vector3 c = p.WorldCenter;
                Line(c, c + Vector3.up * (0.04f + 0.16f * l), col);
            }
            // assist torque magnitude on the pelvis
            if (r.Pelvis != null)
            {
                Vector3 pc = r.Pelvis.WorldCenter;
                Line(pc, pc + Vector3.up * bal.AppliedTorqueFraction * 0.4f, ColAssist);
            }
        }

        void UpdateCounters()
        {
            var b = Sandbox.Boot;
            var q = b.Quality;
            sb.Length = 0;
            sb.Append("FPS ").Append(Mathf.RoundToInt(q.Fps)).Append("   ").Append(q.FrameMs.ToString("0.0")).Append(" ms");
            sb.Append("   quality ").Append(b.graphics.preset).Append(b.graphics.autoQuality ? " (auto " : " (").Append(Mathf.RoundToInt(q.AutoLevel * 100f)).Append("%)\n");
            int bodies = 0, rbs = 0, jobs = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null) continue;
                bodies++;
                rbs += r.AllPieces.Count;
                if (r.Skin != null) jobs += r.Skin.JobsPending;
            }
            sb.Append("bodies ").Append(bodies).Append("  segments ").Append(rbs).Append("  mesh jobs ").Append(jobs);
            sb.Append("  props ").Append(Sandbox.World != null ? Sandbox.World.ActivePropCount : 0).Append('\n');
            sb.Append("droplets ").Append(Sandbox.Blood.DropletCount).Append('/').Append(q.State.bloodDroplets);
            sb.Append("  raycasts ").Append(Sandbox.Blood.CastsLastFrame).Append('/').Append(q.State.bloodRaycasts);
            sb.Append("  decals ").Append(Sandbox.Decals.Count).Append('/').Append(Sandbox.Decals.Capacity);
            sb.Append("  puddles ").Append(Sandbox.Blood.PuddleCount).Append('\n');
            sb.Append("particles ").Append(Sandbox.Fx.ParticleCount).Append("  texture uploads/s ").Append(CanvasUploader.UploadsLastSecond).Append('\n');
            if (Sandbox.Net != null && Sandbox.Net.Active) sb.Append(Sandbox.Net.DebugLine()).Append('\n');
            var f = Sandbox.Focus;
            if (f != null && f.Balance != null)
            {
                var bal = f.Balance;
                sb.Append('\n').Append(f.Label).Append(": ").Append(f.Brain.State);
                if (f.Brain.State == MotorState.GetUp) sb.Append(" phase ").Append(f.Brain.GetUpPhase);
                sb.Append("\n  pelvis h ").Append(bal.PelvisHeight.ToString("0.00")).Append(" / ").Append(bal.StandHeight.ToString("0.00"));
                sb.Append("  tilt ").Append(Mathf.RoundToInt(bal.Tilt)).Append("°");
                sb.Append("\n  capture outside ").Append(bal.SupportDistance.ToString("0.00")).Append(" m  COM outside ").Append(bal.ComSupportDistance.ToString("0.00"));
                sb.Append("\n  assist ctrl ").Append(bal.Control.ToString("0.00")).Append("  torque ").Append(Mathf.RoundToInt(bal.AppliedTorqueFraction * 100f)).Append('%');
                sb.Append("  lift ").Append(bal.AppliedLift.ToString("0")).Append(" N  horiz ").Append(bal.AppliedHorizontal.ToString("0")).Append(" N");
                sb.Append("\n  feet L ").Append(f.Locomotion.Feet[0].phase).Append(bal.FootGrounded[0] ? "*" : "").Append("  R ").Append(f.Locomotion.Feet[1].phase).Append(bal.FootGrounded[1] ? "*" : "");
                sb.Append("  urgency ").Append(f.Locomotion.RecoveryUrgency.ToString("0.00")).Append("  recovery steps ").Append(f.Locomotion.RecoverySteps);
                var br = f.Brain;
                sb.Append("\n  stagger ").Append(br.WStagger.ToString("0.0")).Append(" brace ").Append(br.WBrace.ToString("0.0"))
                  .Append(" protect ").Append(br.WProtect.ToString("0.0")).Append(" clutch ").Append(br.WClutch.ToString("0.0"))
                  .Append(" grab ").Append(br.WGrab[0].ToString("0.0")).Append('/').Append(br.WGrab[1].ToString("0.0"));
                sb.Append("\n  joint load (max): ");
                int worst = -1; float wl = 0f;
                for (int i = 0; i < PartInfo.Count; i++) if (f.Muscles.load[i] > wl) { wl = f.Muscles.load[i]; worst = i; }
                if (worst >= 0) sb.Append(PartInfo.Names[worst]).Append(' ').Append(Mathf.RoundToInt(wl * 100f)).Append('%');
            }
            if (Sandbox.Test != null) sb.Append("\n\nself-test: ").Append(Sandbox.Test.Status);
            counters.text = sb.ToString();
        }

        void UpdateLabels()
        {
            var cam = Sandbox.MainCamera;
            int n = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count && n < MaxLabels; i++)
            {
                var r = Sandbox.Ragdolls[i];
                if (r == null || r.Destroyed || cam == null) continue;
                Vector3 top = r.CenterOfMass + Vector3.up * 1.05f * r.Scale;
                Vector3 sp = cam.WorldToScreenPoint(top);
                if (sp.z < 0.2f || sp.z > 40f) continue;
                var t = labels[n++];
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                var canvasRt = (RectTransform)canvasGo.transform;
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, sp, null, out local);
                t.rectTransform.anchoredPosition = local;
                if (textTimer >= 0.199f) t.text = StateName(r);
            }
            for (int i = n; i < MaxLabels; i++) if (labels[i].gameObject.activeSelf) labels[i].gameObject.SetActive(false);
        }

        static readonly string[] stateNames = System.Enum.GetNames(typeof(MotorState));
        static readonly string[] phaseNames = System.Enum.GetNames(typeof(FootPhase));
        string StateName(RagdollBody r)
        {
            sb.Length = 0;
            sb.Append(stateNames[(int)r.Brain.State]).Append("  L:").Append(phaseNames[(int)r.Locomotion.Feet[0].phase]).Append(" R:").Append(phaseNames[(int)r.Locomotion.Feet[1].phase]);
            return sb.ToString();
        }
    }

    /// <summary>Startup self-test: lets the first ragdoll stand for 3 s, then walk forward for 5 s, and logs the result.
    /// If it falls or does not cover the distance, the balance assist is raised and the test is repeated (up to 3
    /// times). Any player interaction or possession cancels the test.</summary>
    public class SelfTest
    {
        enum Phase { Settle, Stand, Walk, Done }
        Phase phase;
        RagdollBody subject;
        float t;
        int attempt;
        Vector3 walkStart, walkDir, spawnPos;
        float spawnYaw;
        float minPelvis, maxTilt;
        bool fell;
        float startTime;
        string status = "waiting";
        const int MaxAttempts = 3;

        public string Status { get { return status; } }
        public bool Running { get { return phase != Phase.Done; } }
        public bool IsSubject(RagdollBody r) { return r != null && r == subject && phase != Phase.Done; }

        public void Begin(RagdollBody body)
        {
            subject = body;
            if (body == null) { phase = Phase.Done; status = "no ragdoll"; return; }
            spawnPos = body.transform.position;
            spawnYaw = body.Locomotion != null ? body.Locomotion.HeadingYaw : 0f;
            attempt++;
            phase = Phase.Settle;
            t = 0f;
            startTime = Time.time;
            minPelvis = float.MaxValue; maxTilt = 0f; fell = false;
            status = "attempt " + attempt + ": settling";
        }

        public void Abort(string reason)
        {
            if (phase == Phase.Done) return;
            if (subject != null && subject.Brain != null) subject.Brain.ExternalControl = false;
            phase = Phase.Done;
            status = "cancelled (" + reason + ")";
            if (Sandbox.Boot.debug.logSelfTest) Debug.Log("[RagdollSandbox] Self-test cancelled: " + reason);
        }

        public void FixedTick(float dt)
        {
            if (phase == Phase.Done) return;
            var r = subject;
            if (r == null || r.Destroyed) { phase = Phase.Done; status = "subject removed"; return; }
            if (Sandbox.Net != null && Sandbox.Net.Active) { Abort("online session"); return; }
            if (r.Possessed) { Abort("possessed"); return; }
            if (r.LastInteractionTime > startTime) { Abort("player interacted with the subject"); return; }
            t += dt;
            var bal = r.Balance;
            var brain = r.Brain;
            switch (phase)
            {
                case Phase.Settle:
                    if (t > 1.2f)
                    {
                        phase = Phase.Stand; t = 0f;
                        status = "attempt " + attempt + ": standing";
                    }
                    break;
                case Phase.Stand:
                    Track(r);
                    if (t > 3f)
                    {
                        bool ok = !fell && minPelvis > bal.StandHeight * 0.75f && maxTilt < 35f;
                        Log("stand 3 s: " + (ok ? "PASS" : "FAIL") + "  min pelvis " + minPelvis.ToString("0.00") + " m (stand " + bal.StandHeight.ToString("0.00") + "), max tilt " + maxTilt.ToString("0") + " deg, state " + brain.State);
                        if (!ok) { Fail("could not stand"); return; }
                        phase = Phase.Walk; t = 0f;
                        walkStart = r.CenterOfMass;
                        walkDir = MathUtil.SafeNormal(MathUtil.Flat(Quaternion.Euler(0f, spawnYaw, 0f) * Vector3.forward), Vector3.forward);
                        brain.ExternalControl = true;
                        brain.ExternalVelocity = walkDir * Sandbox.Boot.locomotion.walkSpeed;
                        minPelvis = float.MaxValue; maxTilt = 0f;
                        status = "attempt " + attempt + ": walking";
                    }
                    break;
                case Phase.Walk:
                    brain.ExternalControl = true;
                    brain.ExternalVelocity = walkDir * Sandbox.Boot.locomotion.walkSpeed;
                    Track(r);
                    if (fell || t > 5f)
                    {
                        brain.ExternalControl = false;
                        float dist = Vector3.Dot(MathUtil.Flat(r.CenterOfMass - walkStart), walkDir);
                        float need = Sandbox.Boot.locomotion.walkSpeed * 5f * 0.4f;
                        bool ok = !fell && dist >= need;
                        Log("walk 5 s: " + (ok ? "PASS" : "FAIL") + "  distance " + dist.ToString("0.00") + " m (need " + need.ToString("0.00") + "), min pelvis " +
                            minPelvis.ToString("0.00") + " m, max tilt " + maxTilt.ToString("0") + " deg, state " + brain.State);
                        if (!ok) { Fail(fell ? "fell while walking" : "too slow"); return; }
                        phase = Phase.Done;
                        status = "PASSED (attempt " + attempt + ")";
                        Log("self-test passed on attempt " + attempt + ".");
                    }
                    break;
            }
        }

        void Track(RagdollBody r)
        {
            var bal = r.Balance;
            if (bal.PelvisHeight < minPelvis) minPelvis = bal.PelvisHeight;
            if (bal.Tilt > maxTilt) maxTilt = bal.Tilt;
            var s = r.Brain.State;
            if (s == MotorState.Fall || s == MotorState.Lie || s == MotorState.GetUp) fell = true;
        }

        void Fail(string why)
        {
            var b = Sandbox.Boot;
            var r = subject;
            if (r != null && r.Brain != null) r.Brain.ExternalControl = false;
            if (attempt >= MaxAttempts)
            {
                phase = Phase.Done;
                status = "FAILED after " + attempt + " attempts (" + why + ")";
                Log("self-test failed " + attempt + " times; keeping the raised assist values. Check the F3 overlay.");
                return;
            }
            var bs = b.balance;
            var sbuf = new StringBuilder(256);
            sbuf.Append("self-test failed (").Append(why).Append("), raising the assist: ");
            float a0 = bs.assistTorque; bs.assistTorque = Mathf.Min(1200f, bs.assistTorque * 1.35f + 40f);
            sbuf.Append("assistTorque ").Append(a0.ToString("0")).Append("->").Append(bs.assistTorque.ToString("0"));
            float w0 = bs.weightSupport; bs.weightSupport = Mathf.Min(1.1f, bs.weightSupport + 0.08f);
            sbuf.Append(", weightSupport ").Append(w0.ToString("0.00")).Append("->").Append(bs.weightSupport.ToString("0.00"));
            float h0 = bs.heightGain; bs.heightGain = Mathf.Min(200f, bs.heightGain * 1.25f);
            sbuf.Append(", heightGain ").Append(h0.ToString("0")).Append("->").Append(bs.heightGain.ToString("0"));
            float v0 = bs.velocityGain; bs.velocityGain = Mathf.Min(30f, bs.velocityGain * 1.2f);
            sbuf.Append(", velocityGain ").Append(v0.ToString("0.0")).Append("->").Append(bs.velocityGain.ToString("0.0"));
            float m0 = bs.maxHorizontalAssist; bs.maxHorizontalAssist = Mathf.Min(1f, bs.maxHorizontalAssist + 0.1f);
            sbuf.Append(", maxHorizontalAssist ").Append(m0.ToString("0.00")).Append("->").Append(bs.maxHorizontalAssist.ToString("0.00"));
            float s0 = b.muscles.strength; b.muscles.strength = Mathf.Min(3f, b.muscles.strength * 1.15f);
            sbuf.Append(", muscles.strength ").Append(s0.ToString("0.00")).Append("->").Append(b.muscles.strength.ToString("0.00"));
            float u0 = bs.pelvisUprightSpring; bs.pelvisUprightSpring = Mathf.Min(8000f, bs.pelvisUprightSpring * 1.25f);
            sbuf.Append(", pelvisUprightSpring ").Append(u0.ToString("0")).Append("->").Append(bs.pelvisUprightSpring.ToString("0"));
            Log(sbuf.ToString());
            // respawn the subject and retry
            if (r != null && !r.Destroyed) b.RemoveRagdoll(r);
            var nb = b.SpawnRagdoll(spawnPos, spawnYaw);
            Begin(nb);
        }

        static void Log(string s)
        {
            if (Sandbox.Boot != null && Sandbox.Boot.debug.logSelfTest) Debug.Log("[RagdollSandbox] " + s);
        }
    }
}
