// Ragdoll Sandbox - procedural audio. Every sound is synthesized at boot with AudioClip.Create and played
// through a pool of 3D AudioSources (plus a few looping sources for wind, saws, buzz and fuses).
using System;
using UnityEngine;

namespace RagdollSandbox
{
    #region ProceduralAudio
    public enum Sfx
    {
        Thud0, Thud1, Thud2, BoneCrack, Squelch0, Squelch1, Slice, Stab, Gunshot, Shotgun, Explosion, ExplosionSmall,
        Heartbeat, BreathIn, BreathOut, Grunt0, Grunt1, Grunt2, Pain0, Pain1, Gasp, Groan, Wind, Buzz, Saw, Clang, WoodKnock,
        Splat, Click, Beep, Fuse, Footstep, Whoosh, Spring, Crush, Press, Cannon, Zap, Thump, Count
    }

    /// <summary>Simple RBJ biquad filter.</summary>
    public struct Biquad
    {
        float b0, b1, b2, a1, a2, x1, x2, y1, y2;

        public static Biquad LowPass(float freq, float q, float sr) { var f = new Biquad(); f.SetLowPass(freq, q, sr); return f; }
        public static Biquad HighPass(float freq, float q, float sr) { var f = new Biquad(); f.SetHighPass(freq, q, sr); return f; }
        public static Biquad BandPass(float freq, float q, float sr) { var f = new Biquad(); f.SetBandPass(freq, q, sr); return f; }

        public void SetLowPass(float freq, float q, float sr)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, sr * 0.45f) / sr, cs = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q);
            float a0 = 1f + alpha;
            b0 = (1f - cs) * 0.5f / a0; b1 = (1f - cs) / a0; b2 = b0; a1 = -2f * cs / a0; a2 = (1f - alpha) / a0;
        }

        public void SetHighPass(float freq, float q, float sr)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, sr * 0.45f) / sr, cs = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q);
            float a0 = 1f + alpha;
            b0 = (1f + cs) * 0.5f / a0; b1 = -(1f + cs) / a0; b2 = b0; a1 = -2f * cs / a0; a2 = (1f - alpha) / a0;
        }

        public void SetBandPass(float freq, float q, float sr)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, sr * 0.45f) / sr, cs = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q);
            float a0 = 1f + alpha;
            b0 = alpha / a0; b1 = 0f; b2 = -alpha / a0; a1 = -2f * cs / a0; a2 = (1f - alpha) / a0;
        }

        public float Process(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            if (float.IsNaN(y)) { y = 0f; x1 = x2 = y1 = y2 = 0f; }
            return y;
        }
    }

    public class ProceduralAudio
    {
        public const int SR = 44100;
        readonly AudioClip[] clips = new AudioClip[(int)Sfx.Count];
        AudioSource[] pool;
        float[] poolStart;
        int next;
        AudioSource ui, wind;
        readonly AudioSource[] loops = new AudioSource[12];
        readonly Transform[] loopFollow = new Transform[12];
        readonly int[] loopOwner = new int[12];
        Transform root;
        Rng rng;
        float lastGruntTime;

        public float Master = 1f, SfxVolume = 1f, AmbientVolume = 0.5f, VoiceVolume = 1f;

        public AudioClip Clip(Sfx s) { return clips[(int)s]; }

        public void Build(Transform parent, int poolSize, int seed)
        {
            rng = new Rng(seed);
            root = new GameObject("Audio").transform;
            root.SetParent(parent, false);
            for (int i = 0; i < clips.Length; i++) clips[i] = Synthesize((Sfx)i);
            pool = new AudioSource[Mathf.Clamp(poolSize, 8, 128)];
            poolStart = new float[pool.Length];
            for (int i = 0; i < pool.Length; i++)
            {
                var go = new GameObject("Src" + i);
                go.transform.SetParent(root, false);
                pool[i] = Configure3D(go.AddComponent<AudioSource>());
            }
            for (int i = 0; i < loops.Length; i++)
            {
                var go = new GameObject("Loop" + i);
                go.transform.SetParent(root, false);
                loops[i] = Configure3D(go.AddComponent<AudioSource>());
                loops[i].loop = true;
                loopOwner[i] = -1;
            }
            var u = new GameObject("UI");
            u.transform.SetParent(root, false);
            ui = u.AddComponent<AudioSource>();
            ui.playOnAwake = false;
            ui.spatialBlend = 0f;
            var w = new GameObject("Wind");
            w.transform.SetParent(root, false);
            wind = w.AddComponent<AudioSource>();
            wind.playOnAwake = false;
            wind.spatialBlend = 0f;
            wind.loop = true;
            wind.clip = clips[(int)Sfx.Wind];
            wind.volume = 0.25f * AmbientVolume * Master;
            wind.Play();
        }

        static AudioSource Configure3D(AudioSource s)
        {
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 2f;
            s.maxDistance = 90f;
            s.dopplerLevel = 0f;
            s.spread = 20f;
            return s;
        }

        public AudioSource Play(Sfx s, Vector3 pos, float volume = 1f, float pitch = 1f, float minDistance = 2f)
        {
            if (pool == null) return null;
            AudioClip c = clips[(int)s];
            if (c == null || volume <= 0.001f) return null;
            int idx = -1;
            for (int k = 0; k < pool.Length; k++)
            {
                int i = (next + k) % pool.Length;
                if (!pool[i].isPlaying) { idx = i; break; }
            }
            if (idx < 0)
            {
                float oldest = float.MaxValue;
                for (int i = 0; i < pool.Length; i++) if (poolStart[i] < oldest) { oldest = poolStart[i]; idx = i; }
            }
            next = (idx + 1) % pool.Length;
            var src = pool[idx];
            src.transform.position = pos;
            src.clip = c;
            src.pitch = Mathf.Clamp(pitch * (Time.timeScale < 0.9f ? Mathf.Lerp(0.6f, 1f, Time.timeScale) : 1f), 0.2f, 3f);
            src.volume = Mathf.Clamp01(volume * SfxVolume * Master);
            src.minDistance = minDistance;
            src.Play();
            poolStart[idx] = Time.unscaledTime;
            return src;
        }

        public AudioSource PlayRandomThud(Vector3 pos, float volume, float pitch)
        {
            int v = rng.Range(0, 3);
            return Play(v == 0 ? Sfx.Thud0 : v == 1 ? Sfx.Thud1 : Sfx.Thud2, pos, volume, pitch * rng.Range(0.9f, 1.1f));
        }

        public void PlayVoice(Sfx s, Vector3 pos, float volume, float pitch)
        {
            if (Time.unscaledTime - lastGruntTime < 0.12f) return;
            lastGruntTime = Time.unscaledTime;
            Play(s, pos, volume * VoiceVolume, pitch, 1.5f);
        }

        public void Play2D(Sfx s, float volume = 1f, float pitch = 1f)
        {
            if (ui == null) return;
            AudioClip c = clips[(int)s];
            if (c == null) return;
            ui.pitch = pitch;
            ui.PlayOneShot(c, Mathf.Clamp01(volume * Master));
        }

        /// <summary>Starts a looping sound following a transform. Returns a handle (or -1).</summary>
        public int StartLoop(Sfx s, Transform follow, float volume, float pitch, int owner)
        {
            for (int i = 0; i < loops.Length; i++)
            {
                if (loopOwner[i] >= 0) continue;
                loopOwner[i] = owner;
                loopFollow[i] = follow;
                loops[i].clip = clips[(int)s];
                loops[i].volume = Mathf.Clamp01(volume * SfxVolume * Master);
                loops[i].pitch = pitch;
                if (follow != null) loops[i].transform.position = follow.position;
                loops[i].Play();
                return i;
            }
            return -1;
        }

        public void SetLoop(int handle, float volume, float pitch)
        {
            if (handle < 0 || handle >= loops.Length) return;
            loops[handle].volume = Mathf.Clamp01(volume * SfxVolume * Master);
            loops[handle].pitch = pitch;
        }

        public void SetLoopPosition(int handle, Vector3 pos)
        {
            if (handle < 0 || handle >= loops.Length) return;
            loops[handle].transform.position = pos;
        }

        public void StopLoop(int handle)
        {
            if (handle < 0 || handle >= loops.Length) return;
            loops[handle].Stop();
            loopOwner[handle] = -1;
            loopFollow[handle] = null;
        }

        public void Tick(float windStrength)
        {
            for (int i = 0; i < loops.Length; i++)
            {
                if (loopOwner[i] < 0) continue;
                if (loopFollow[i] != null) loops[i].transform.position = loopFollow[i].position;
            }
            if (wind != null) wind.volume = Mathf.Clamp01(windStrength * AmbientVolume * Master);
            AudioListener.volume = 1f;
        }

        public void SetPaused(bool paused)
        {
            AudioListener.pause = paused;
            if (ui != null) ui.ignoreListenerPause = true;
        }

        #region Synthesis
        AudioClip Synthesize(Sfx s)
        {
            var r = new Rng(1000 + (int)s * 17);
            float[] d;
            switch (s)
            {
                case Sfx.Thud0: d = Thud(0.28f, 95f, 42f, 0.07f, r); break;
                case Sfx.Thud1: d = Thud(0.3f, 75f, 36f, 0.09f, r); break;
                case Sfx.Thud2: d = Thud(0.22f, 120f, 55f, 0.05f, r); break;
                case Sfx.BoneCrack: d = BoneCrack(r); break;
                case Sfx.Squelch0: d = Squelch(0.38f, 1f, r); break;
                case Sfx.Squelch1: d = Squelch(0.28f, 1.4f, r); break;
                case Sfx.Slice: d = Slice(r); break;
                case Sfx.Stab: d = Mix(Squelch(0.25f, 1.6f, r), Thud(0.2f, 140f, 70f, 0.03f, r), 0.6f); break;
                case Sfx.Gunshot: d = Gunshot(0.8f, 1f, r); break;
                case Sfx.Shotgun: d = Shotgun(r); break;
                case Sfx.Explosion: d = Explosion(3.2f, 1f, r); break;
                case Sfx.ExplosionSmall: d = Explosion(1.7f, 0.6f, r); break;
                case Sfx.Heartbeat: d = Heartbeat(); break;
                case Sfx.BreathIn: d = Breath(1.1f, 950f, true, r); break;
                case Sfx.BreathOut: d = Breath(1.2f, 650f, false, r); break;
                case Sfx.Grunt0: d = Voice(0.32f, 118f, 92f, 600f, 1050f, 2400f, 0.15f, r); break;
                case Sfx.Grunt1: d = Voice(0.26f, 132f, 105f, 520f, 950f, 2300f, 0.2f, r); break;
                case Sfx.Grunt2: d = Voice(0.38f, 108f, 85f, 650f, 1150f, 2500f, 0.12f, r); break;
                case Sfx.Pain0: d = Voice(0.75f, 175f, 128f, 820f, 1250f, 2600f, 0.1f, r); break;
                case Sfx.Pain1: d = Voice(0.6f, 190f, 150f, 750f, 1150f, 2700f, 0.15f, r); break;
                case Sfx.Gasp: d = Gasp(r); break;
                case Sfx.Groan: d = Voice(1.2f, 98f, 82f, 480f, 860f, 2300f, 0.25f, r); break;
                case Sfx.Wind: d = Wind(r); break;
                case Sfx.Buzz: d = Buzz(r); break;
                case Sfx.Saw: d = SawLoop(r); break;
                case Sfx.Clang: d = Modal(1.2f, new[] { 523f, 1187f, 1873f, 2711f, 3400f }, new[] { 0.9f, 0.6f, 0.45f, 0.3f, 0.2f }, 0.5f, r); break;
                case Sfx.WoodKnock: d = Modal(0.3f, new[] { 210f, 455f, 870f, 1400f }, new[] { 0.09f, 0.06f, 0.04f, 0.03f }, 0.9f, r); break;
                case Sfx.Splat: d = Squelch(0.16f, 2.2f, r); break;
                case Sfx.Click: d = Click(r); break;
                case Sfx.Beep: d = Tone(0.12f, 1850f, 0.004f); break;
                case Sfx.Fuse: d = Fuse(r); break;
                case Sfx.Footstep: d = Thud(0.13f, 160f, 80f, 0.025f, r); break;
                case Sfx.Whoosh: d = Whoosh(0.45f, r); break;
                case Sfx.Spring: d = Boing(); break;
                case Sfx.Crush: d = Crush(r); break;
                case Sfx.Press: d = Press(r); break;
                case Sfx.Cannon: d = Explosion(1.8f, 0.8f, r); break;
                case Sfx.Zap: d = Zap(r); break;
                case Sfx.Thump: d = Thud(0.5f, 60f, 30f, 0.15f, r); break;
                default: d = new float[64]; break;
            }
            Normalize(d, s == Sfx.Wind ? 0.6f : 0.95f);
            var clip = AudioClip.Create(s.ToString(), d.Length, 1, SR, false);
            clip.SetData(d, 0);
            return clip;
        }

        static void Normalize(float[] d, float peak)
        {
            float m = 0f;
            for (int i = 0; i < d.Length; i++) m = Mathf.Max(m, Mathf.Abs(d[i]));
            if (m < 1e-6f) return;
            float k = peak / m;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        static float[] Mix(float[] a, float[] b, float bGain)
        {
            int n = Mathf.Max(a.Length, b.Length);
            var o = new float[n];
            for (int i = 0; i < n; i++) o[i] = (i < a.Length ? a[i] : 0f) + (i < b.Length ? b[i] * bGain : 0f);
            return o;
        }

        static float[] Thud(float len, float f0, float f1, float decay, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var lp = Biquad.LowPass(700f, 0.7f, SR);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float f = Mathf.Lerp(f1, f0, Mathf.Exp(-t / 0.05f));
                phase += 2f * Mathf.PI * f / SR;
                float body = Mathf.Sin(phase) * Mathf.Exp(-t / decay);
                float click = lp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-t / 0.012f) * 1.4f;
                d[i] = body + click;
            }
            return d;
        }

        static float[] BoneCrack(Rng r)
        {
            int n = (int)(0.3f * SR);
            var d = new float[n];
            int clicks = r.Range(5, 9);
            for (int c = 0; c < clicks; c++)
            {
                int start = (int)(r.Range(0f, 0.05f) * SR);
                var bp = Biquad.BandPass(r.Range(1800f, 4200f), 2.5f, SR);
                float amp = r.Range(0.5f, 1f);
                for (int i = start; i < n && i < start + SR / 30; i++)
                {
                    float t = (i - start) / (float)SR;
                    d[i] += bp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-t / 0.004f) * amp * 3f;
                }
            }
            var lp = Biquad.LowPass(300f, 0.8f, SR);
            var hp = Biquad.HighPass(1500f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                d[i] += Mathf.Sin(2f * Mathf.PI * 110f * t) * Mathf.Exp(-t / 0.04f) * 0.6f;
                d[i] += hp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-t / 0.07f) * 0.25f;
                d[i] = d[i] * 0.8f + lp.Process(d[i]) * 0.4f;
            }
            return d;
        }

        static float[] Squelch(float len, float speed, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var bp = new Biquad();
            float lowPhase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float u = t / len;
                float fc = 300f + 900f * Mathf.Sin(u * Mathf.PI * speed) * Mathf.Sin(u * Mathf.PI * 3.1f * speed) + 200f;
                if ((i & 31) == 0) bp.SetBandPass(Mathf.Abs(fc) + 150f, 3.5f, SR);
                float env = Mathf.Clamp01(t / 0.012f) * Mathf.Exp(-t / (len * 0.4f));
                lowPhase += 2f * Mathf.PI * (70f + 30f * Mathf.Sin(t * 25f)) / SR;
                d[i] = bp.Process(r.Range(-1f, 1f)) * env * 2.2f + Mathf.Sin(lowPhase) * env * 0.35f;
            }
            return d;
        }

        static float[] Slice(Rng r)
        {
            int n = (int)(0.42f * SR);
            var d = new float[n];
            var bp = new Biquad();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                if ((i & 31) == 0) bp.SetBandPass(Mathf.Lerp(1800f, 6500f, t / 0.25f), 1.6f, SR);
                float whooshEnv = Mathf.Sin(Mathf.Clamp01(t / 0.25f) * Mathf.PI);
                d[i] = bp.Process(r.Range(-1f, 1f)) * whooshEnv * 0.9f;
                float tr = t - 0.06f;
                if (tr > 0f)
                {
                    float ring = Mathf.Sin(2f * Mathf.PI * 2150f * tr) + 0.7f * Mathf.Sin(2f * Mathf.PI * 3420f * tr) + 0.4f * Mathf.Sin(2f * Mathf.PI * 5230f * tr);
                    d[i] += ring * Mathf.Exp(-tr / 0.09f) * 0.22f;
                }
            }
            return d;
        }

        static float[] Gunshot(float len, float weight, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var hp = Biquad.HighPass(1200f, 0.7f, SR);
            var lpBody = Biquad.LowPass(900f, 0.8f, SR);
            var lpTail = Biquad.LowPass(500f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float noise = r.Range(-1f, 1f);
                float crack = hp.Process(noise) * Mathf.Exp(-t / 0.006f) * 2.5f;
                float body = lpBody.Process(noise) * Mathf.Exp(-t / 0.05f) * 2f * weight;
                float boom = Mathf.Sin(2f * Mathf.PI * (55f + 40f * Mathf.Exp(-t / 0.03f)) * t) * Mathf.Exp(-t / 0.12f) * 0.9f * weight;
                float tail = lpTail.Process(noise) * Mathf.Exp(-t / 0.28f) * 0.7f;
                d[i] = crack + body + boom + tail;
            }
            return d;
        }

        static float[] Shotgun(Rng r)
        {
            var d = Gunshot(1.15f, 1.6f, r);
            AddClick(d, (int)(0.62f * SR), r, 0.45f);
            AddClick(d, (int)(0.8f * SR), r, 0.5f);
            return d;
        }

        static void AddClick(float[] d, int at, Rng r, float amp)
        {
            var bp = Biquad.BandPass(2500f, 2f, SR);
            for (int i = at; i < d.Length && i < at + SR / 40; i++)
            {
                float t = (i - at) / (float)SR;
                d[i] += bp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-t / 0.005f) * amp * 4f;
            }
        }

        static float[] Explosion(float len, float size, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var lp = new Biquad();
            var lp2 = new Biquad();
            float brown = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float noise = r.Range(-1f, 1f);
                brown = Mathf.Clamp(brown + noise * 0.08f, -1f, 1f) * 0.995f;
                if ((i & 63) == 0)
                {
                    float fc = Mathf.Lerp(150f, 2600f * size + 400f, Mathf.Exp(-t / (0.25f * len)));
                    lp.SetLowPass(fc, 0.8f, SR);
                    lp2.SetLowPass(fc * 0.7f, 0.7f, SR);
                }
                float env = Mathf.Clamp01(t / 0.004f) * Mathf.Exp(-t / (0.3f * len));
                float roar = lp2.Process(lp.Process(noise * 0.6f + brown * 1.5f)) * env * 3f;
                float crack = noise * Mathf.Exp(-t / 0.01f) * 1.2f;
                float sub = Mathf.Sin(2f * Mathf.PI * (32f + 30f * Mathf.Exp(-t / 0.08f)) * t) * Mathf.Exp(-t / (0.22f * len)) * 1.3f * size;
                d[i] = roar + crack + sub;
            }
            int debris = (int)(18 * size);
            for (int k = 0; k < debris; k++)
            {
                int at = (int)(r.Range(0.25f, len * 0.7f) * SR);
                float amp = r.Range(0.05f, 0.2f) * Mathf.Exp(-at / (float)SR / len * 2f);
                var bp = Biquad.BandPass(r.Range(800f, 3500f), 3f, SR);
                for (int i = at; i < n && i < at + SR / 50; i++)
                    d[i] += bp.Process(r.Range(-1f, 1f)) * amp * 3f * Mathf.Exp(-(i - at) / (float)SR / 0.006f);
            }
            return d;
        }

        static float[] Heartbeat()
        {
            int n = (int)(0.75f * SR);
            var d = new float[n];
            var lp = Biquad.LowPass(180f, 0.9f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float lub = Mathf.Sin(2f * Mathf.PI * 52f * t) * Mathf.Clamp01(t / 0.01f) * Mathf.Exp(-t / 0.07f);
                float t2 = t - 0.27f;
                float dub = t2 > 0f ? Mathf.Sin(2f * Mathf.PI * 68f * t2) * Mathf.Clamp01(t2 / 0.008f) * Mathf.Exp(-t2 / 0.06f) * 0.75f : 0f;
                d[i] = lp.Process(lub + dub) * 2f;
            }
            return d;
        }

        static float[] Breath(float len, float fc, bool inhale, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var bp = Biquad.BandPass(fc, 0.8f, SR);
            var bp2 = Biquad.BandPass(fc * 2.3f, 1.5f, SR);
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                float env = inhale ? Mathf.Pow(Mathf.Sin(u * Mathf.PI), 1.4f) : Mathf.Pow(Mathf.Sin(Mathf.Min(1f, u * 1.2f) * Mathf.PI), 1.2f) * (1f - u * 0.4f);
                float x = r.Range(-1f, 1f);
                d[i] = (bp.Process(x) + bp2.Process(x) * 0.4f) * env;
            }
            return d;
        }

        /// <summary>Formant voice: glottal pulse train through three resonators (grunts, pain, groans).</summary>
        static float[] Voice(float len, float f0a, float f0b, float F1, float F2, float F3, float breathiness, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var r1 = Biquad.BandPass(F1, 6f, SR);
            var r2 = Biquad.BandPass(F2, 8f, SR);
            var r3 = Biquad.BandPass(F3, 10f, SR);
            float phase = 0f;
            float jitter = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float u = t / len;
                if ((i & 255) == 0) jitter = r.Range(-0.03f, 0.03f);
                float f0 = Mathf.Lerp(f0a, f0b, Mathf.Sqrt(u)) * (1f + jitter);
                phase += f0 / SR;
                if (phase > 1f) phase -= 1f;
                float glottal = phase < 0.4f ? Mathf.Sin(phase / 0.4f * Mathf.PI) : -0.3f * Mathf.Sin((phase - 0.4f) / 0.6f * Mathf.PI);
                float src = glottal + r.Range(-1f, 1f) * breathiness;
                float env = Mathf.Clamp01(t / 0.025f) * Mathf.Clamp01((len - t) / (len * 0.35f));
                float v = r1.Process(src) * 1.0f + r2.Process(src) * 0.6f + r3.Process(src) * 0.25f;
                d[i] = v * env;
            }
            return d;
        }

        static float[] Gasp(Rng r)
        {
            int n = (int)(0.55f * SR);
            var d = new float[n];
            var bp = new Biquad();
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                if ((i & 31) == 0) bp.SetBandPass(Mathf.Lerp(700f, 1600f, u), 2.5f, SR);
                float env = Mathf.Sin(Mathf.Clamp01(u * 1.3f) * Mathf.PI) * (u < 0.77f ? 1f : 0f);
                d[i] = bp.Process(r.Range(-1f, 1f)) * env * 1.5f;
            }
            return d;
        }

        static float[] Wind(Rng r)
        {
            float len = 8f;
            int n = (int)(len * SR);
            var d = new float[n];
            var lp = new Biquad();
            float brown = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                brown = Mathf.Clamp(brown + r.Range(-1f, 1f) * 0.04f, -1f, 1f) * 0.998f;
                if ((i & 127) == 0) lp.SetLowPass(350f + 250f * Mathf.Sin(t * 0.7f) + 120f * Mathf.Sin(t * 1.9f), 1.2f, SR);
                float gust = 0.6f + 0.4f * Mathf.Sin(t * 0.55f + Mathf.Sin(t * 0.21f) * 2f);
                d[i] = lp.Process(brown * 0.8f + r.Range(-1f, 1f) * 0.1f) * gust;
            }
            int fade = SR;
            for (int i = 0; i < fade; i++)
            {
                float a = i / (float)fade;
                d[i] = d[i] * a + d[n - fade + i] * (1f - a);
            }
            var o = new float[n - fade];
            Array.Copy(d, 0, o, 0, o.Length);
            return o;
        }

        static float[] Buzz(Rng r)
        {
            int n = SR;
            var d = new float[n];
            var lp = Biquad.LowPass(3000f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float sq = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 100f * t)) * 0.4f + Mathf.Sin(2f * Mathf.PI * 300f * t) * 0.3f;
                float crackle = r.Value < 0.004f ? r.Range(-1f, 1f) * 3f : 0f;
                d[i] = lp.Process(sq + crackle + r.Range(-1f, 1f) * 0.15f);
            }
            return d;
        }

        static float[] SawLoop(Rng r)
        {
            int n = SR;
            var d = new float[n];
            var bp = Biquad.BandPass(1800f, 1f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float saw = ((t * 180f) % 1f) * 2f - 1f;
                float saw2 = ((t * 360f) % 1f) * 2f - 1f;
                d[i] = saw * 0.4f + saw2 * 0.25f + bp.Process(r.Range(-1f, 1f)) * 0.6f * (0.7f + 0.3f * Mathf.Sin(t * 2f * Mathf.PI * 30f));
            }
            return d;
        }

        static float[] Modal(float len, float[] freqs, float[] decays, float noiseAmt, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var hp = Biquad.HighPass(800f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float s = 0f;
                for (int k = 0; k < freqs.Length; k++) s += Mathf.Sin(2f * Mathf.PI * freqs[k] * t + k) * Mathf.Exp(-t / decays[k]) / (k + 1);
                s += hp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-t / 0.006f) * noiseAmt;
                d[i] = s;
            }
            return d;
        }

        static float[] Click(Rng r)
        {
            int n = (int)(0.03f * SR);
            var d = new float[n];
            var bp = Biquad.BandPass(3000f, 2f, SR);
            for (int i = 0; i < n; i++) d[i] = bp.Process(r.Range(-1f, 1f)) * Mathf.Exp(-i / (float)SR / 0.004f);
            return d;
        }

        static float[] Tone(float len, float f, float fade)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                d[i] = Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Clamp01(t / fade) * Mathf.Clamp01((len - t) / fade);
            }
            return d;
        }

        static float[] Fuse(Rng r)
        {
            int n = SR;
            var d = new float[n];
            var hp = Biquad.HighPass(2500f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float crackle = r.Value < 0.01f ? r.Range(-1f, 1f) * 2f : 0f;
                d[i] = hp.Process(r.Range(-1f, 1f) * 0.5f + crackle);
            }
            return d;
        }

        static float[] Whoosh(float len, Rng r)
        {
            int n = (int)(len * SR);
            var d = new float[n];
            var bp = new Biquad();
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                if ((i & 31) == 0) bp.SetBandPass(Mathf.Lerp(400f, 1400f, Mathf.Sin(u * Mathf.PI)), 1.2f, SR);
                d[i] = bp.Process(r.Range(-1f, 1f)) * Mathf.Sin(u * Mathf.PI);
            }
            return d;
        }

        static float[] Boing()
        {
            int n = (int)(0.55f * SR);
            var d = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float f = 180f + 380f * (1f - Mathf.Exp(-t / 0.06f)) + 25f * Mathf.Sin(t * 2f * Mathf.PI * 14f);
                phase += 2f * Mathf.PI * f / SR;
                d[i] = Mathf.Sin(phase) * Mathf.Exp(-t / 0.18f);
            }
            return d;
        }

        static float[] Crush(Rng r)
        {
            int n = (int)(0.6f * SR);
            var d = new float[n];
            var lp = Biquad.LowPass(900f, 0.8f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float grain = r.Value < 0.03f ? r.Range(-1f, 1f) * 2f : r.Range(-0.3f, 0.3f);
                d[i] = lp.Process(grain) * Mathf.Exp(-t / 0.2f) * 2f + Mathf.Sin(2f * Mathf.PI * 70f * t) * Mathf.Exp(-t / 0.08f) * 0.5f;
            }
            return d;
        }

        static float[] Press(Rng r)
        {
            int n = (int)(1.1f * SR);
            var d = new float[n];
            var bp = Biquad.BandPass(1200f, 0.9f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float hiss = bp.Process(r.Range(-1f, 1f)) * Mathf.Sin(Mathf.Clamp01(t / 0.9f) * Mathf.PI) * 0.6f;
                float t2 = t - 0.85f;
                float thump = t2 > 0f ? Mathf.Sin(2f * Mathf.PI * 50f * t2) * Mathf.Exp(-t2 / 0.06f) : 0f;
                d[i] = hiss + thump;
            }
            return d;
        }

        static float[] Zap(Rng r)
        {
            int n = (int)(0.35f * SR);
            var d = new float[n];
            var hp = Biquad.HighPass(900f, 0.7f, SR);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float burst = (Mathf.Sin(t * 2f * Mathf.PI * 60f) > 0.2f) ? 1f : 0.2f;
                d[i] = hp.Process(r.Range(-1f, 1f)) * burst * Mathf.Exp(-t / 0.15f) + Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 120f * t)) * 0.2f * Mathf.Exp(-t / 0.1f);
            }
            return d;
        }
        #endregion
    }
    #endregion
}
