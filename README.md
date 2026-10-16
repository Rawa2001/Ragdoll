# Ragdoll Sandbox

A realistic active-ragdoll physics playground for **Unity 6 (URP)**, built entirely from code at runtime. You walk around in first person with a hotbar of tools, next to a smooth grey mannequin. The mannequin stands, breathes, looks at you, walks, falls, gets back up, and keeps trying to move when it's hurt. It bleeds, breaks bones, loses limbs, spills organs, and eventually dies.

There are no prefabs, models, textures, materials, shaders, sounds, packages, tags or layers to import. The map, sky, lights, camera, character meshes, textures, particles, audio and UI are all generated when you press Play.

---

## Setup

1. Create a project from the **Universal 3D (URP)** template (Unity 6, or 2022.3 LTS).
2. Copy the `Assets/RagdollSandbox` folder into your project's `Assets` folder.
3. Open an empty scene (or the template's `SampleScene`).
4. Create an empty GameObject and add the **Ragdoll Sandbox Boot** component (`RagdollSandboxBoot`).
5. Press **Play**.

Any camera, light, audio listener or EventSystem already in the scene is switched off automatically and switched back on when you stop. Both *Active Input Handling* modes work: **Input Manager (Old)**, **Input System Package (New)** or **Both**.

---

## Controls

| Action | Key |
|---|---|
| Move / jump / sprint / crouch | WASD / Space / Shift / C |
| Look | Mouse |
| Select tool | 1-9, 0, or the mouse wheel (Q = tool sub-mode) |
| Use tool | LMB (primary), RMB (secondary) |
| Grab distance | Mouse wheel while grabbing |
| Strong pull (rips limbs off) | Hold Shift while grabbing |
| Order the ragdoll to walk somewhere | Ctrl + click on the ground |
| Settings panel + free cursor | Tab |
| Pause menu | Esc |
| Reset ragdolls / whole scene | R / Shift+R |
| Slow motion | T |
| Gravity (Earth / Moon / Zero) | G |
| Possess the ragdoll | V |
| Follow cam (Ctrl+wheel zoom) | F |
| Anatomy X-ray | X |
| Detonate all C4 | B |
| Wander on/off | N |
| Rewind the last 5 s | Hold Z |
| Help / hide HUD | H / F1 |

**Possessed ragdoll:** WASD walks relative to the camera, Shift runs, Space jumps, C crouches, and the mouse orbits the camera. LMB / RMB make the left / right arm reach toward the crosshair, and a hand grips whatever it touches (props, other bodies, ledges).

### Tools

| Slot | Tool | LMB | RMB |
|---|---|---|---|
| 1 | Grab (Q: rope) | Drag on a spring attached to the exact hit point; throw on release | Pin / unpin in place (rope mode: clear ropes) |
| 2 | Hammer | Punch | Heavy smash (fractures) |
| 3 | Blade | Hold and sweep across a limb; a fast sweep slices it at that ring | Stab |
| 4 | Pistol | Hitscan with entry and exit wounds; passes through limbs | |
| 5 | Shotgun | Pellet spread | |
| 6 | Explosives (Q: grenade, TNT, C4, barrel, landmine, mega blast) | Throw or place | Detonate C4 |
| 7 | Props (Q: crate, ball, barrel, anvil, plank, boulder) | Spawn at the crosshair | Throw |
| 8 | Shock | Electrocute (spasms; can stop the heart) | Defibrillate |
| 9 | Heal | Heal a part; revives if the ragdoll died recently | Fully restore that ragdoll |
| 0 | Spawn ragdoll | Spawn at the crosshair | Remove ragdoll |

---

## What's in it

- **Character:** 1.75 m and 72 kg, split into 17 rigid parts with realistic masses. Each part has a primitive collider and a `ConfigurableJoint` with anatomical limits and slerp-drive "muscles". The visuals are smooth ring-based (superellipse/lathe) meshes with muscle bulges, skinned into their neighbours at the joints so the body reads as one mannequin, as in the reference picture. Each part has its own runtime paint texture covering skin, shorts or shirt, bruises, cuts and blood.
- **Active ragdoll:** no Animator. Procedural poses and analytic two-bone IK become joint target rotations, which the drives must physically reach.
  - Balance uses the centre of mass, the support polygon and the capture point. Support, propulsion and upright control are internal force pairs between the torso and the stance feet; the feet's ankle torque is capped by the foot's real contact capacity. A small bounded assist torque scales with consciousness.
  - The step planner runs Stance → Lift → Swing → Plant, with Bezier swing arcs, obstacle clearance, slip and trip handling, and walk, run, jump and crouch gaits.
  - Behaviours: falling (shielding the head), getting up (roll, push up, kneel, rise), army crawl, hopping on one leg, headless spasms, and dying.
- **Anatomy and damage:**
  - Layers: skin, then muscle/fat, then bone and organs, plus a skeleton and arteries.
  - Organs: brain, beating heart, breathing lungs, liver, stomach, kidneys and intestines. They spill out on tethers when the torso is ruptured, and can be torn free.
  - Vitals: blood volume, heart rate, oxygen, pain, adrenaline, shock and consciousness.
  - Wounds clot (arteries don't). Bones fracture, with looser joints and a visible kink. Spinal injuries paralyse.
- **Dismemberment:** joints break under force. The blade cuts a limb at any ring into two physical halves with cross-section caps (skin, fat, muscle, bone, marrow). Stumps spurt in time with the heartbeat.
- **Blood:** droplet, spray and mist particles; splat decals (blobs, streaks, wall drips) with a global cap; painting onto bodies; growing, merging and drying puddles; bloody footprints and handprints; drag smears.
- **Explosions:** falloff, line-of-sight shielding, clamped impulses, shrapnel and chain reactions. Visuals include a flash, fireball, smoke, shockwave, debris, sparks and dust, plus scorch decals, camera shake and FOV kick.
- **Map (120 × 120 m):**
  - Spawn plaza
  - Rolling hills with rocks and trees
  - Stairs, ramps and a half-pipe
  - Obstacle course: beam, stepping stones, seesaws, moving platforms, sweepers, pendulums, trampolines, conveyors
  - Demolition zone: crates, brick walls, dominoes, explosive barrels, tires, breakable planks
  - Hazard pit: spikes, saws, hydraulic press, electric floor, cannon, landmines
  - Tower with an elevator, and a 12 m cliff
- **Bonus:** multiple ragdolls, a 5-second rewind, look presets and clothing toggles, a slow-motion kill-cam, and a rope tool.

## Files

| File | Contents |
|---|---|
| `RagdollSandboxBoot.cs` | The only component you add: every tunable, bootstrap, time control, quality presets, safety net, rewind |
| `SandboxCore.cs` | Unity 6 compatibility, input bridge (old and new input), math, physics materials, registry, events |
| `SandboxFactories.cs` | Procedural textures and sprites, materials (URP with built-in fallbacks), meshes |
| `SandboxAudio.cs` | Synthesized sounds and pooled 3D audio sources |
| `SandboxPools.cs` | Particle effects, flash lights, tracers, decals |
| `SandboxWorld.cs` | Map generation and hazards |
| `RagdollBody.cs` | Body parts, joints, topology, the per-body update loop |
| `RagdollAnatomy.cs` | Ring meshes, skin painting, skeleton, arteries and organ meshes |
| `RagdollMotor.cs` | Muscles, IK, balance, locomotion, behaviours |
| `RagdollDamage.cs` | Vitals, wounds, organs, damage, dismemberment and slicing |
| `SandboxBlood.cs` | Blood decals, puddles and prints |
| `SandboxExplosives.cs` | Explosions and explosive props |
| `SandboxTools.cs` | Hotbar tools, grab, rope, view models |
| `SandboxPlayer.cs` | First-person player and camera rig |
| `SandboxHUD.cs` | uGUI HUD built in code |

## Tunables

Every value is on the **Ragdoll Sandbox Boot** component, grouped by system:

- General
- Physics
- Character
- Muscles
- Balance
- Locomotion
- Damage
- Vitals
- Dismemberment / Gore
- Blood
- Explosions
- Tools
- Player
- Camera
- Audio
- Graphics / Quality
- World
- Bonus

Most of them can be changed while the game is running. The in-game settings panel (Tab) exposes the common ones.

If the ragdoll is too wobbly or too stiff on your machine, start with:

- **Balance → Assist Torque**
- **Muscles → Hip/Knee/Ankle Spring**
- **Balance → Height Gain**
- **Physics → Fixed Timestep**

## Notes

- Developed against Unity 6 / URP with a 2022.3 fallback. The code compiles under both input backends; it has not yet been play-tested inside Unity, so expect some tuning of the balance and gait values.
- URP transparency (X-ray skin, puddles, particles) is set up on runtime materials. This works in the Editor. In a player build, Unity may strip shader variants that no material asset uses. If X-ray or particles render wrong in a build, add one transparent URP Lit material and one URP Particles Unlit material to the project.
- The first press of Play takes a few seconds while textures and sounds are generated. Lower **Graphics → World Texture Size** to start faster.
