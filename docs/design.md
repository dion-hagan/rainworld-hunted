# Hunted — Rain World Mod Design Document

**Status:** Draft v0.2 — AI plan revised to start from scavenger AI
**Target:** Rain World 1.9.x+ (Remix mod loader), Downpour/More Slugcats optional
**Language / tooling:** C#, BepInEx + MonoMod hooks (`On.*`). Python/PyTorch only if the optional neural-net stage is pursued

---

## 1. Concept

At the start of a campaign, a second slugcat — **the Pursuer** — spawns in a random shelter somewhere in the world. It is tracked purely as data while it's in another region. Every cycle the player survives, the Pursuer closes the distance by two shelters. Once it reaches the player's region it becomes a real, realized creature that tracks the player down and tries to kill them, driven by combat AI adapted from Rain World's scavengers. It starts unarmed and upgrades itself by scavenging items and killing scavengers for their gear.

The fantasy: a slow, inevitable clock ticking underneath the normal campaign. Early on it's a rumor on the map. Late in a run, it's the thing waiting outside your shelter door.

## 2. Goals

- **Persistent dread.** The player always knows roughly where the Pursuer is and how many cycles until it arrives.
- **A fight that feels like another player, not a lizard.** Movement, spear throws, dodges, and item use should read as slugcat-like and aggressive.
- **Emergent escalation.** The Pursuer's loadout reflects what it has done — a spear-and-bomb Pursuer late in a run should feel earned by the world, not scripted.
- **Fair.** Every encounter is telegraphed, and the player has real counterplay (outrunning, rain, terrain, predators, fighting back).

## 3. Non-Goals (v1)

- **A neural-net AI.** v1 reuses scavenger AI (see §7). A learned policy is an optional later stage, only if the borrowed AI feels too predictable.
- **Multiplayer / Jolly Co-op support.** Targeting multiple players adds a lot of edge cases; design for it, don't ship it.
- **Scavenger trading/diplomacy.** The Pursuer only takes gear by force.
- **Story integration** (Iterators, echoes, endings). The Pursuer is a systemic threat, not a narrative character.

## 4. Core Loop

```
Campaign start
  └─ Pick random spawn shelter (≥ N shelter-hops from player)
Each successful cycle (player sleeps in a shelter)
  └─ Recompute path on shelter graph → advance 2 hops
  └─ Roll offscreen gear upgrades
Pursuer enters player's region
  └─ Realized as abstract creature, migrates toward player room-by-room
Pursuer reaches player's room / nearby rooms
  └─ Realized; Hunting AI takes over (search → engage → kill)
Resolution
  └─ Player dies  → Pursuer retreats K hops, keeps gear
  └─ Pursuer dies → drops gear, respawns after M cycles far away
```

## 5. World Model & Offscreen Tracking

### 5.1 The shelter graph

Rain World only loads one region at a time, so the Pursuer's offscreen position lives entirely in save data as `(regionAcronym, shelterRoomName)`.

At mod init, build a **global shelter graph** by parsing every region's `world_xx.txt`:

- **Nodes:** every shelter room in every region available to the current campaign (respect slugcat-specific region/room exclusions).
- **Edges:** two shelters are connected if a room-connection path exists between them that passes through no other shelter. Edge weight = number of rooms traversed.
- **Gate edges:** region gates link shelter subgraphs across regions. The Pursuer ignores karma requirements.

Cache the graph per campaign. It only needs recomputing if the mod list changes.

> **Implementation note (v0.1):** shelters are dead-end rooms, so "a path that passes through no other shelter" would connect every shelter to every other one. The implemented graph connects two shelters when no third shelter lies between them (relative-neighbourhood graph over room distances), which keeps the intended shelter-by-shelter pacing and still contains a spanning tree of all reachable shelters. The graph is rebuilt at session start; it takes milliseconds.

### 5.2 Advancing each cycle

Hook the successful-sleep path (win-state / save on shelter close):

1. Find the player's shelter node.
2. Dijkstra from the Pursuer's node to the player's node.
3. Advance 2 nodes along that path (configurable in Remix options).
4. If the Pursuer is within 2 hops of the player, mark it `Arrived` — next cycle it starts in the player's region.

**Decision points:**

| Player outcome | Pursuer behavior (default) |
|---|---|
| Successful sleep | Advances 2 hops |
| Starved sleep | Advances 3 hops (it smells weakness) |
| Death (any cause) | Does not advance |
| Player passes through a gate | Pursuer path recomputes next cycle; no bonus |

### 5.3 In-region behavior

When the Pursuer is in the player's loaded region but not in a realized room, it exists as an `AbstractCreature` whose abstract AI migrates room-to-room toward the player's current room (like lizards and scavengers do offscreen). When the player's room or an adjacent one is realized, the Pursuer realizes too.

### 5.4 Rain

The Pursuer is subject to rain. Its abstract AI heads for the nearest shelter as the cycle timer runs low; if it gets caught, it dies like anything else. This gives the player a strong, readable counter: survive until the rain, then make it to a shelter the Pursuer can't reach in time.

## 6. The Pursuer Creature

### 6.1 Base implementation

The Pursuer's body changes between stages (see §7):

- **Stage 1 — reskinned elite scavenger.** Register a custom `CreatureTemplate.Type` (e.g., `HuntedPursuer`) that inherits from the elite scavenger template, with a recolored, slugcat-leaning look. It moves and fights exactly like a scavenger. The goal is to validate the whole loop fast, not to look right.
- **Stage 2 — real slugcat body.** The Pursuer becomes a `Player` object driven by an AI instead of a controller, following the pattern Downpour uses for slugpups (`SlugNPC`). Each tick the AI produces a `Player.InputPackage` (`x`, `y`, `jmp`, `thrw`, `pckp`), so the Pursuer inherits real slugcat movement: pole climbing, slides, pounces, wall jumps, spear throws. Without Downpour, the same approach works by hooking `Player.checkInput` for the Pursuer instance.

Everything above the body (offscreen tracking, save data, gear, death rules) is shared, so swapping stages doesn't touch the rest of the mod.

> **Implementation note (v0.1):** the Pursuer uses the vanilla elite scavenger template itself (a regular scavenger without Downpour/Watcher) rather than a registered custom type, so every creature relationship in the game applies to it unchanged: it is part of the ecosystem for free. The mod tells the Pursuer apart by identity, not by template.

### 6.2 Stats & look

- Base stats: Hunter-like (fast, strong throws, good spear damage). Configurable.
- Visual: dark, desaturated body with a bright, distinct eye color so it's never mistaken for the player. Stage 1 recolors `ScavengerGraphics`; Stage 2 uses a custom palette via `PlayerGraphics` hooks.
- Audio: a unique low sound cue when it enters an adjacent room.

### 6.3 Weapons & upgrades

Spawns with nothing. Gear tiers, weakest to strongest:

| Tier | Item | Notes |
|---|---|---|
| 0 | Unarmed | Pounce, grab, bite (low damage) |
| 1 | Rock | Stun |
| 2 | Spear | Primary kill tool |
| 3 | Explosive spear | High damage, splash |
| 4 | Scavenger bomb | Area denial, flush player from cover |

**Onscreen acquisition:** item-seeking is a behavior mode (§7.2). The Pursuer picks up anything better than what it holds, and will detour to kill scavengers carrying higher-tier gear if it can win the fight (compare its loadout vs. the scavenger's; avoid scavenger packs).

**Offscreen acquisition:** each cycle, roll per region traversed. Regions with high scavenger density (e.g., Outskirts toll areas, Garbage Wastes) give better odds of rock→spear and spear→explosive upgrades. Inventory is stored in save data (up to 2 held items, matching slugcat hands).

### 6.4 Death & persistence

- **Player killed:** normal death and karma loss. The Pursuer retreats K shelters (default 4) so the player isn't spawn-camped. It keeps its gear.
- **Pursuer killed:** drops its items. Respawns after M cycles (default 5) at a shelter ≥ N hops away, unarmed.
- **Optional hard mode:** Pursuer respawns with its previous loadout.

## 7. AI Architecture

### 7.1 Approach

Start from scavenger AI rather than a neural net. Scavengers already do most of what the Pursuer needs: rank and collect weapons, throw spears and bombs, hold grudges against the player, assess threats, and retreat when outmatched. Downpour's elite scavengers are already close to the target aggression.

The AI evolves in three stages. Each stage keeps the same interface to the rest of the mod.

### 7.2 Stage 1 — Reskinned elite scavenger

Run the scavenger AI mostly unchanged, with a few hooks:

- **Permanent grudge.** Force maximum aggression toward the player; never pacified by gifts or reputation.
- **Target lock.** The player is always the primary target. Other scavengers are only targets when they carry better gear (see RaidScav below).
- **No pack behavior.** The Pursuer doesn't join or follow scavenger groups, and other scavengers treat it as hostile.
- **Abstract migration toward the player** instead of normal scavenger wandering (see §5.3).

**What this proves:** whether the loop is fun — pacing of the countdown, arrival, fairness of encounters, gear escalation. **What it doesn't:** slugcat-like movement.

### 7.3 Stage 2 — Slugcat body with borrowed scavenger logic

Split the AI into two layers:

- **Movement layer:** the slugpup AI's existing code that steers a `Player` body along pathfinder routes. Needs tuning for speed and aggression — slugpups are built to follow, not hunt.
- **Decision layer:** scavenger-style logic ported over — weapon scoring, threat assessment, throw decisions (range, line of sight, lead on moving targets), retreat conditions.

Before committing, spike on two unknowns: how well the slugpup movement code handles fast, aggressive pursuit, and how much throwing logic needs to be written from scratch versus adapted.

**Behavior states** (hand-written, shared by Stage 2 and any later stage):

| State | Enter when | Goal |
|---|---|---|
| Travel | Player not known | Follow pathfinder toward last known player room |
| Search | Player recently seen, now lost | Sweep nearby cells, check hiding spots |
| Scavenge | Better item within X tiles, player not visible | Reach and pick up item |
| RaidScav | Lone scav with better gear, Pursuer favored | Kill scav, take item |
| Engage | Player visible or very close | Close distance and kill |
| Flee | Rain imminent, or HP critical & outgunned | Reach shelter / break line of sight |

### 7.4 Stage 3 (optional) — Neural combat policy

Only worth building if Stage 2 feels too predictable after playtesting. If so, the plan from v0.1 still applies in condensed form:

- A small MLP (2–3 layers, 128–256 units) implemented in plain C#, weights exported from PyTorch. It replaces the movement layer and throw decisions; the state machine and pathfinder still decide where to go.
- Observations: Pursuer body state, target relative position/velocity, next pathfinder waypoints, a local tile grid, nearby threats and projectiles.
- Actions: the controller itself (`x`, `y`, `jump`, `throw`, `pickup`).
- Training: behavior cloning from recorded human play first; PPO fine-tuning via an Arena-mode training harness only as a stretch goal, since in-game training is slow without a headless simulator.
- The Stage 2 AI stays as the fallback if weights fail to load or the policy gets stuck.

## 8. Player-Facing Design

- **Map marker:** the region map shows the Pursuer's region (and shelter, on Easy) at cycle start.
- **Countdown:** sleep screen shows "The Pursuer draws closer — N shelters away."
- **Proximity cues:** unique sound when it's in an adjacent room; ambient music shift when it's in-region.
- **Spawn safety:** the Pursuer never starts a cycle in the player's shelter or an adjacent room; there is a grace period of X seconds after shelter doors open.
- **Remix config:** hops per cycle, spawn distance, respawn delay, retreat distance, map visibility, difficulty tier, disable offscreen upgrades.

## 9. Save Data

Stored per save slot, serialized alongside the vanilla save string (hook `SaveState` serialization; wrap in a mod-tagged key so vanilla ignores it if the mod is removed):

```json
{
  "version": 1,
  "state": "Traveling | Arrived | Dead",
  "region": "SU",
  "shelter": "SU_S01",
  "inventory": ["Spear", null],
  "respawnCycle": null,
  "kills": { "player": 2, "scavs": 5 },
  "rngSeed": 123456
}
```

Use a stored seed so offscreen rolls are deterministic per save (makes bugs reproducible).

> **Implementation note (v0.1):** the entry lives in the *death-persistent* save data (`HUNTED<dpB>key=value|...`), because that is the part of the save the game writes on death as well as on sleep; the retreat rule needs to survive a death.

## 10. Requirements

### P0 — Must have

- Shelter graph built from world files; handles gates and campaign-specific exclusions.
- Offscreen tracking and 2-hop advance on successful sleep, persisted across save/load.
- Pursuer realizes in player's region and migrates toward the player.
- Stage 1 Pursuer: reskinned elite scavenger with permanent grudge and target lock on the player.
- Item pickup and gear tiers; drops gear on death.
- Death/retreat/respawn rules; spawn-safety guarantees.

**Acceptance examples:**

- Given the Pursuer is 6 hops away, when the player sleeps successfully 3 times, then the Pursuer is in the player's region at the next cycle start.
- Given the Pursuer is holding a spear, when it dies, then a spear is dropped at its body position.
- Given the player just respawned after being killed, then the Pursuer is ≥ K hops away.

### P1 — Should have

- Stage 2 Pursuer: real slugcat body with slugpup movement and scavenger-derived decisions.
- RaidScav behavior; offscreen upgrade rolls.
- Map marker, sleep-screen countdown, proximity audio.
- Remix config menu.

### P2 — Later

- Stage 3 neural combat policy, with difficulty tiers.
- Multiple Pursuers / Jolly Co-op targeting.
- Pursuer "memory" — learns the player's favorite shelters and ambushes there.

## 11. Milestones

1. **Graph & tracker** — shelter graph, save data, debug overlay showing Pursuer position. No creature yet.
2. **Stage 1 Pursuer** — reskinned elite scavenger spawns, migrates, and hunts. Game is playable end to end.
3. **Gear & scavs** — upgrade tiers, RaidScav, offscreen rolls, death/retreat/respawn rules.
4. **Playtest & tune** — pacing, fairness, config defaults. Decide whether the loop is worth taking further.
5. **Stage 2 spike** — test slugpup movement under aggressive pursuit; estimate throw-logic work.
6. **Stage 2 Pursuer** — slugcat body with borrowed scavenger decisions; UI cues and Remix options.
7. **(Optional) Stage 3** — recording mod, behavior cloning, C# inference.

## 12. Risks

| Risk | Mitigation |
|---|---|
| Scavenger AI resists being hooked into a lone, always-hostile hunter | Keep hooks narrow (aggression, target selection, group membership); fall back to a custom AI subclass if needed |
| Stage 1 looks wrong for a slugcat | Accept it — Stage 1 is for validating the loop, not the look |
| Slugpup movement too passive for hunting | Spike early (milestone 5); tune or replace movement layer before building Stage 2 on it |
| Pursuer gets stuck in geometry | Stuck detector → teleport to nearest valid pathfinder node offscreen only |
| Unfair deaths frustrate players | Telegraphs, grace periods, retreat rule, difficulty tiers |
| Game updates break hooks | Keep hooks narrow; isolate in one `Hooks` class |
| Other mods add regions/shelters | Graph built from loaded world files, so modded regions are included automatically |

## 13. Open Questions

- Should the Pursuer ever enter the player's shelter if it arrives first? (Design — current answer: no.)
- Does starving count as "successful" for advancement, or its own bonus rule? (Design)
- Should the Pursuer interact with other creatures' food chain (eat, be hunted by lizards) or be ignored by them? (Design/Eng — v0.1 answer: it is a full member of the ecosystem.)
- Is Stage 2's borrowed AI predictable enough that Stage 3 is worth it? (Playtest — decide after milestone 6.)
- Map-visibility default: exact shelter, region only, or hidden? (Playtest)
