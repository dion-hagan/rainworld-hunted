# Rain World Creature Systems Field Guide

Source basis: the decompiled Rain World v1.11.8 assembly (Assembly-CSharp.dll, with Remix, Downpour and Watcher present). Class, method and field names are the game's own. Code excerpts are trimmed for length and marked "simplified" where they are not verbatim. The Hunted mod (a second slugcat, "the Pursuer", that hunts the player) is used as a running example of a custom AI built on this machinery. Not affiliated with Videocult.

This document covers eight topics:

1. Overview: the two-layer world, the clock, the object model, reading the source
2. Creature AI: modules, utility comparison, perception, memory, relationships, personality, social systems
3. Pathfinding: the AI map, movement connections, path costs, the search, following the field
4. The offscreen world: the region graph, abstract time, the abstract brain, realize and abstractize, dens
5. Bodies and physics: body chunks, chunk connections, update order, grasps, damage, ropes
6. Procedural animation and inverse kinematics: body parts, tails, limbs, the lizard walk cycle, slugcat hands, two-bone IK, tentacles, drawing
7. Building a similar system in Unity: a beginner tutorial with complete scripts
8. Adaptive AI: adding a neural network so the creature adapts to the player over time

---

## Part 1. Overview

Rain World is not a platformer with monsters in it. It is a simulation of an ecosystem that happens to have a camera pointed at one room of it. Almost every design decision in the creature code follows from that: creatures exist whether or not you can see them, bodies are simulated rather than animated, and behaviour is chosen by comparing needs rather than by scripting.

### 1.1 Two layers of reality

Every creature lives in two representations at once. The abstract creature is a small record with a template, a position in the region graph and an offscreen brain. The realized creature is the physical body with chunks, an AI full of trackers and, when the camera is near, a graphics module drawing it. Only rooms near the player are realized, so at any moment most of the world is running the cheap abstract layer.

Picture the world as two panels. On the left is the region graph: rooms are circles (SU_A, SU_B, SU_C, a gate, a shelter) joined by lines for their exits. An abstract lizard is a dot on one room; a scavenger squad is a dot in a den. One abstract room ticks per frame, round robin, catching up many ticks at once. On the right is a single realized room, SU_C: a floor, a ledge, a slugcat made of two body chunks, a lizard made of three chunks with four limbs and a tail, and above the lizard its ArtificialIntelligence (a LizardAI) listing its modules: PathFinder, Tracker, ThreatTracker, PreyTracker, NoiseTracker, RelationshipTracker, UtilityComparer, which feed DetermineBehavior() and produce Hunt, Flee, Lurk and so on. A creature crosses between the two panels through AbstractCreature.Realize() and Abstractize().

| Layer | Class | Owns | Exists when |
|---|---|---|---|
| Abstract | `AbstractCreature` | `creatureTemplate`, `state`, `personality`, `pos` (a `WorldCoordinate`), `abstractAI` | Always, from spawn until death is saved |
| Abstract brain | `AbstractCreatureAI` | `path`, `destination`, `denPosition`, `timeBuffer`, `RealAI` | Always, for templates with `AI = true` |
| Realized | `Creature` (`Lizard`, `Player`, `Scavenger`, ...) | `bodyChunks`, `bodyChunkConnections`, `grasps`, `room` | While its room is realized |
| Realized brain | `ArtificialIntelligence` (`LizardAI`, ...) | `modules`: path finder and trackers | Created by `AbstractCreature.InitiateAI()` inside `Realize()` |
| Graphics | `GraphicsModule` (`LizardGraphics`, ...) | `bodyParts`: limbs, tail segments, head; sprites via a `SpriteLeaser` | While the camera can see it; culled otherwise |

### 1.2 The clock

The simulation runs at a fixed 40 ticks per second (`MainLoopProcess.framesPerSecond = 40`). Rendering runs at the display rate and receives a `timeStacker` between 0 and 1 saying how far the frame sits between the last tick and the next one. Every drawable interpolates: sprites are placed at `Vector2.Lerp(lastPos, pos, timeStacker)`, which is why a 40 Hz world looks smooth at 144 Hz. Every physics primitive therefore keeps both `pos` and `lastPos`, and that pair is also what the physics uses for swept collision.

One tick of `RainWorldGame.Update` does, in order: update every realized room (objects, then their graphics state), advance creatures travelling through pipes (`ShortcutHandler.Update`), let the `RoomRealizer` decide whether rooms should load or unload, and update one abstract room, chosen round robin, with a time budget equal to the number of rooms in the region so that each abstract room advances about once per second at full speed.

### 1.3 Four principles that recur everywhere

- Behaviour is a comparison, not a script. Every AI is a bag of modules that each report a utility between 0 and 1. A comparer picks the loudest one and the creature acts on that need until another shouts louder.
- Bodies are point masses joined by constraints. A creature is a few circles with velocity and distance constraints between them. Feet, tails and tentacles are extra particles hung off those circles; nothing is keyframed.
- Terrain is pre-digested for AI. Each room compiles an `AImap` once: which tiles are floor, wall, ceiling, corridor or air, and which movement connections exist between them. Path finding and limb placement both read it.
- Everything degrades gracefully offscreen. The abstract brain follows the same destinations at a coarse grain, so a lizard that chased you into a pipe keeps chasing you through the region graph while its body does not exist.

### 1.4 Reading the source yourself

The game ships as `Assembly-CSharp.dll` in `RainWorld_Data/Managed`. It decompiles cleanly with ILSpy's command line tool; version 9.1 installs without trouble:

```
dotnet tool install -g ilspycmd --version 9.1.0.7988
ilspycmd -p -o decomp "RainWorld_Data/Managed/Assembly-CSharp.dll"
```

Mods hook the result with MonoMod's HookGen: `HOOKS-Assembly-CSharp.dll` exposes an `On.ClassName.Method` event for every method, so `On.LizardAI.Update += (orig, self) => { orig(self); ... }` runs your code around the original. Property getters have no HookGen event and need a `MonoMod.RuntimeDetour.Hook` on the getter's `MethodInfo`.

The Hunted mod's Pursuer is used as a running example because it exercises most of this machinery from the outside: it replaces a creature's realized AI, drives the offscreen layer with its own destinations, spawns bodies into realized rooms and hooks the relationship and social systems.

### 1.5 Glossary

- WorldCoordinate: a position in the region: `room`, tile `x`/`y` (or -1 when only the node is known) and `abstractNode`, the index of an exit or den in that room. `TileDefined` and `NodeDefined` tell you which half is valid.
- Node: an `AbstractRoomNode`: an `Exit`, `Den`, `SideExit`, `SkyExit`, `SeaExit`, `RegionTransportation`, `BatHive` or `GarbageHoles`. Nodes are the vertices of the offscreen graph.
- Template: a `CreatureTemplate`, the species sheet. Movement costs, vision, relationships to every other species, danger, body size, offscreen speed, whether it uses dens.
- Module: an `AIModule` attached to an `ArtificialIntelligence`: it gets `Update()`, `NewRoom(room)` and can report a `Utility()`.
- Chunk: a `BodyChunk`: circle, mass, velocity. The atom of physics.
- Body part: a `BodyPart` owned by a graphics module: a particle that follows the chunks but does not push the world. Limbs, tails and heads are body parts.
- Realize / Abstractize: create or destroy the physical body while keeping the abstract record.

---

## Part 2. Creature AI

A Rain World brain is a list of modules. Each module watches one thing: the terrain, the creatures nearby, the sounds, the rain, its own injuries. A comparer asks every module how urgent it feels, the species class turns the winner into a behaviour, and the behaviour decides where the body should go this tick.

### 2.1 The skeleton (ArtificialIntelligence.cs, AIModule.cs)

The base class is tiny. `ArtificialIntelligence` holds the abstract creature, a list of modules and typed shortcuts to the common ones; `AddModule` fills the shortcut slot that matches the module's type, so `pathFinder`, `tracker`, `threatTracker`, `preyTracker`, `noiseTracker`, `utilityComparer` and the rest are just the modules you added.

```csharp
public class AIModule
{
    public ArtificialIntelligence AI;
    public AIModule(ArtificialIntelligence AI) { this.AI = AI; }
    public virtual void Update() { }
    public virtual void NewRoom(Room room) { }
    public virtual float Utility() { return 0f; }
}

// ArtificialIntelligence.Update, minus expedition and Watcher extras
public virtual void Update()
{
    timeInRoom++;
    for (int i = 0; i < modules.Count; i++) modules[i].Update();
}

public virtual void NewRoom(Room room)
{
    if (lastRoom != room.abstractRoom.index)
    {
        lastRoom = room.abstractRoom.index;
        timeInRoom = 0;
        for (int i = 0; i < modules.Count; i++) modules[i].NewRoom(room);
        // a destination in this room with no tile gets a random tile
    }
}
```

Two things follow from this shape. First, a module never sees the body directly; it goes through `AI.creature.realizedCreature`, which is why the same tracker classes serve lizards, scavengers, vultures and slugpups. Second, `NewRoom` is the only initialisation a module gets, so a module updated before its first `NewRoom` is working on a null room.

A bug encountered while building the Hunted mod illustrates this: the Pursuer's slugcat body was created straight into a realized room. `Player`'s constructor sets `room` from the abstract position, and `Creature.SpitOutOfShortCut` only calls `NewRoom` when the room changes, so the modules never got a room. The path finder threw every tick and the noise tracker threw inside the player's movement update, knocking the player over. Any custom AI should call `NewRoom(cat.room)` itself if `lastRoom` does not match.

### 2.2 The module catalogue

Constructor arguments are the tuning surface: a lizard and a scavenger differ mostly in these numbers.

| Module | Job | Constructor and notable knobs |
|---|---|---|
| `PathFinder` (StandardPather, LizardPather, flying and swimming variants) | Maintains a cost field from the destination back to every reachable tile and hands out the next `MovementConnection`. | `(AI, world, creature)`; `stepsPerFrame` (slugpup 30), `accessibilityStepsPerFrame`, `walkPastPointOfNoReturn` |
| `Tracker` | Memory of other creatures: where they are, when last seen, guesses where they went. | `(AI, seeAroundCorners, maxTrackedCreatures, framesToRememberCreatures, ghostSpeed, ghostPush, ghostPushSpeed, ghostDismissalRange)`. Lizard: 10, 10, -1, 0.35, 5, 5, 10; scavenger: 10, 10, -1, 0.5, 5, 5, 10 |
| `NoiseTracker` | Turns `InGameNoise` events into things to investigate; attaches noises to tracked creatures when they match. | `(AI, tracker)`; `hearingSkill`; utility rises with unexplained noise |
| `PreyTracker` | Ranks tracked creatures the AI would eat; estimates the chance of catching each. | `(AI, maxRemembered, persistanceBias, sureToGetPreyDistance, sureToLosePreyDistance, successEstimationDistReliance)`. Lizard: 5, 2, 3, 70, 0.5 |
| `ThreatTracker` | Sums danger from tracked creatures and fixed threat points into a per-tile threat map; finds places to flee to. | `(AI, maxRememberedCreatures)`; `accessibilityConsideration`; `FleeTo(occupyTile, reevaluations, maxDistance, considerLeavingRoom)` |
| `RelationshipTracker` | Keeps a `DynamicRelationship` per tracked creature and sorts each creature into the module that should care about it. | `(AI, tracker)`; needs the AI to implement `IUseARelationshipTracker` |
| `UtilityComparer` | Compares the smoothed, weighted utilities of registered modules. | `AddComparedModule(module, smoother, weight, continuationBonus)` |
| `StuckTracker` | Notices the creature is not getting anywhere; sub-modules track past positions and a move backlog. | `(AI, trackPastPositions, trackNotFollowingCurrentGoal)`; `totalTrackedLastPositions`, `pastPosStuckDistance` |
| `ItemTracker` | Memory of objects (spears, food). | `(AI, seeAroundCorners, maxTrackedItems, framesToRememberItems, forgetDistance, stopTrackingCarried)` |
| `FriendTracker` | Follows and protects a befriended creature; tamed lizards and slugpups. | `(AI)`; `desiredCloseness`, `RunSpeed()`, `CareAboutRain()` |
| `RainTracker` | Utility that ramps up as the cycle ends, so creatures head for dens. | `(AI)` |
| `DenFinder` | Runs an abstract-space search for the nearest den the species may use. | `(AI, creature)`; `GetDenPosition()` |
| `DiscomfortTracker` | Marks tiles near creatures the AI is Uncomfortable with; feeds `TravelPreference`. | `(AI, tracker, uncomfortableWithUnknownNoises)`; scavengers pass `InverseLerp(0.3, 0.2, personality.bravery)` |
| `InjuryTracker` | Utility from missing health. | `(AI, sCurveSlope)`; lizards use a `LizardInjuryTracker` |
| `AgressionTracker` (sic) | Anger per creature, rising with attacks and decaying; drives Fighting. | `(AI, angerSpeedUp, angerSpeedDown)`; lizard 0.001, 0.001 |
| `ObstacleTracker` | Remembers tiles and objects that blocked a move. | `(AI, trackObjects, trackTiles, mapDecayPerReport, ...)` |
| Species extras | `MissionTracker` and `LurkTracker` for lizards, `SuperHearing` for some breeds, an outpost module and a communication module for scavengers. | Registered the same way |

### 2.3 From utilities to a behaviour (UtilityComparer.cs, LizardAI.cs)

The comparer does one thing per tick: for each registered module it computes `Mathf.Pow(module.Utility(), exponent) * weight`, runs that through the module's smoother if it has one, and remembers the highest.

```csharp
public class UtilityTracker
{
    public float weight, continuationBonus, smoothedUtility, exponent = 1f;
    public AIModule module;
    public FloatTweener.FloatTween smoother;

    public void UpdateSmoothing()
    {
        if (smoother != null) smoothedUtility = smoother.Tween(smoothedUtility, UnSmoothedUtility());
    }
    float UnSmoothedUtility() => module == null ? 0f : Mathf.Pow(module.Utility(), exponent) * weight;
    public float SmoothedUtility() => smoother != null ? smoothedUtility : UnSmoothedUtility();
}

public override void Update()
{
    float best = float.MinValue;
    highestUtilityTracker = null;
    for (int i = 0; i < uTrackers.Count; i++)
    {
        uTrackers[i].UpdateSmoothing();
        if (uTrackers[i].SmoothedUtility() > best) { best = uTrackers[i].SmoothedUtility(); highestUtilityTracker = uTrackers[i]; }
    }
}
```

The smoother matters as much as the weight. A `FloatTweenBasic(TweenType.Tick, 1f/30f)` lets a utility move by at most one thirtieth per tick, so a lizard that just glimpsed you takes most of a second to fully commit to the hunt and does not flicker between fleeing and hunting when two utilities are close. Scavengers smooth their noise interest with 0.002 per tick, which is why they investigate slowly and stubbornly.

This is how a lizard registers its needs, with the real numbers:

```csharp
AddModule(new LizardPather(this, world, creature));
AddModule(new Tracker(this, 10, 10, -1, 0.35f, 5, 5, 10));
AddModule(new NoiseTracker(this, tracker));
AddModule(new DenFinder(this, creature));
AddModule(new RainTracker(this));
AddModule(new PreyTracker(this, 5, 2f, 3f, 70f, 0.5f));
AddModule(new ThreatTracker(this, 3));
AddModule(new AgressionTracker(this, 0.001f, 0.001f));
AddModule(new UtilityComparer(this));
AddModule(new MissionTracker(this));
AddModule(new RelationshipTracker(this, tracker));
AddModule(new StuckTracker(this, trackPastPositions: true, trackNotFollowingCurrentGoal: true));
AddModule(new DiscomfortTracker(this, tracker, 0f));

utilityComparer.AddComparedModule(threatTracker, smoother, 1f, 1f);
utilityComparer.AddComparedModule(preyTracker, smoother, 0.6f, 1f);
utilityComparer.AddComparedModule(rainTracker, null, 0.9f, 1f);
utilityComparer.AddComparedModule(new LizardInjuryTracker(this, lizard), null,
    creature.creatureTemplate.type == CreatureTemplate.Type.RedLizard ? 0.4f : 0.9f, 1f);
utilityComparer.AddComparedModule(agressionTracker, null, 0.5f, 1.2f);
```

Fear outranks appetite (weight 1 against 0.6), a red lizard barely cares about being hurt (0.4), and anger gets a continuation bonus so a fight, once started, is sticky. The winning module is mapped to a named behaviour by the species class:

```csharp
private Behavior DetermineBehavior()
{
    Behavior behavior = Behavior.Idle;
    AIModule top = utilityComparer.HighestUtilityModule();
    currentUtility = utilityComparer.HighestUtility();
    if (top != null)
    {
        if (top is ThreatTracker) behavior = Behavior.Flee;
        else if (top is RainTracker) behavior = Behavior.EscapeRain;
        else if (top is PreyTracker) behavior = Behavior.Hunt;
        else if (top is LizardInjuryTracker) behavior = Behavior.Injured;
        else if (top is AgressionTracker) behavior = Behavior.Fighting;
        else if (top is MissionTracker) behavior = Behavior.ActingOutMission;
        else if (top is LurkTracker) behavior = Behavior.Lurk;
        else if (top is NoiseTracker) behavior = Behavior.InvestigateSound;
        else if (top is FriendTracker) behavior = Behavior.FollowFriend;
    }
    if (currentUtility < 0.05f) { currentUtility = 0.05f; behavior = Behavior.Idle; }
    // then overrides: carrying prey -> ReturnPrey, frustrated hunts, stranded creatures...
    return behavior;
}
```

The lizard behaviours are Idle, Hunt, Flee, Travelling, EscapeRain, ReturnPrey, Injured, Fighting, Frustrated, ActingOutMission, Lurk, InvestigateSound, GoToSpitPos and FollowFriend. The behaviour then sets a destination: hunting goes to the prey's best-guess position, fleeing asks `threatTracker.FleeTo` for a tile with low accumulated threat, rain escape goes to the den finder's result, lurking picks an ambush tile near a corridor. A scavenger has the same shape with its own list: Idle, Flee, Attack, EscapeRain, Injured, Scavange (sic), Travel, Investigate, FindPackLeader, LeaveRoom, GuardOutpost and CommunicateWithPlayer.

### 2.4 Perception (ArtificialIntelligence.cs, NoiseTracker.cs)

Seeing is a score, not a ray. `VisualScore(point, bonus)` starts at 1 for a point next to the eye and falls to 0 at `creatureTemplate.visualRadius * (1 + bonus)`, then subtracts penalties: crossing the water surface costs `1 - throughSurfaceVision`, looking into or out of deep water costs `1 - waterVision`, a target in a narrow space (from the AI map) costs 0.5, and any `VisionObscurer` in the room (fog, darkness effects) can lower it further. Only if the score is positive does the game cast a ray through the tiles with `Room.VisualContact`. The bonus is the target's `BodyChunk.VisibilityBonus(movementBasedVision)`: moving bodies are easier to see, scaled by the watcher's template.

Hearing is a distance test. `Room.InGameNoise` is called by anything loud (footsteps, landings, throws, explosions) and reaches every creature through `Creature.HeardNoise`. The noise tracker keeps it when the creature is within `noise.strength * (1 - Deaf) * hearingSkill * (1 - room.BackgroundNoise)`, times 0.2 underwater. If a tracked creature is at that position the noise is attributed to it; otherwise it becomes an unexplained sound whose interest is the tracker's utility, which is what makes lizards come to investigate a dropped rock.

### 2.5 Memory: representations and ghosts (Tracker.cs)

The `Tracker` stores one `CreatureRepresentation` per creature it knows about, with a `priority`, an `age`, a `forgetCounter`, `VisualContact` and `TicksSinceSeen`. Small or unimportant creatures get a `SimpleCreatureRepresentation` that just remembers a last seen coordinate. Important ones get an `ElaborateCreatureRepresentation` with a list of `Ghost`s: hypotheses about where the creature went. When a ghost's tile is visible and the creature is not there, the ghost is dismissed; when it is not visible, the ghost moves along the AI map at the represented creature's `offScreenSpeed * ghostSpeed`, following legal connections and even leaving through shortcuts into neighbouring rooms. `EstimatedChanceOfFinding` shrinks as ghosts multiply, and the number of ghosts is capped at `maxGhosts * chance^0.25`, clamped to at least 2. That single mechanism is why a lizard that lost sight of you at a pipe checks the pipe, and why it eventually gives up.

### 2.6 Relationships, static and dynamic (CreatureTemplate.cs, RelationshipTracker.cs)

Every template carries a `relationships[]` array with one entry per species, each a `Relationship { type, intensity }`. The twelve types:

| Type | Effect on the AI |
|---|---|
| `DoesntTrack` | The tracker never creates a representation. |
| `Ignores` | Tracked but no module claims it. |
| `Eats` | Sorted into the prey tracker; intensity scales attractiveness. |
| `Afraid` | Sorted into the threat tracker; intensity scales the threat it radiates. |
| `StayOutOfWay` | Mild avoidance without fleeing. |
| `Attacks` | Aggression without appetite. Scavengers use it for the player after a theft; the Pursuer forces it for the player permanently. |
| `AgressiveRival` | Fights over territory or prey. |
| `Uncomfortable` | Sorted into the discomfort tracker: keeps a distance, avoids paths near it. |
| `Antagonizes` | Harasses without trying to kill. |
| `PlaysWith` | Approaches and interacts; slugpups. |
| `SocialDependent` | The relationship depends on reputation or social memory (scavengers toward the player). |
| `Pack` | Same group: follows a leader, shares targets. |

The static table is the default. Species whose AI implements `IUseARelationshipTracker` can override it per individual: `UpdateDynamicRelationship(dRelation)` is called for each tracked creature and returns the relationship for this creature right now, and `ModuleToTrackRelationship(relationship)` says which module should own it. A lizard therefore fears a scavenger with a spear more than an unarmed one, a scavenger treats a player with high reputation as a friend and a thief as an enemy, and the Pursuer's slugcat body returns Attacks 1.0 for the player, Afraid 0.5 for scavengers and the template default for everything else.

```csharp
public interface IUseARelationshipTracker
{
    AIModule ModuleToTrackRelationship(CreatureTemplate.Relationship relationship);
    CreatureTemplate.Relationship UpdateDynamicRelationship(RelationshipTracker.DynamicRelationship dRelation);
    RelationshipTracker.TrackedCreatureState CreateTrackedCreatureState(RelationshipTracker.DynamicRelationship rel);
}
```

### 2.7 Personality (AbstractCreature.cs)

Every abstract creature owns a `Personality` generated from its entity ID, so it is stable across saves.

```csharp
public Personality(EntityID ID)
{
    Random.State state = Random.state;
    Random.InitState(ID.RandomSeed);
    sympathy = Custom.PushFromHalf(Random.value, 1.5f);
    energy   = Custom.PushFromHalf(Random.value, 1.5f);
    bravery  = Custom.PushFromHalf(Random.value, 1.5f);
    nervous    = Mathf.Lerp(Random.value, Mathf.Lerp(energy, 1f - bravery, 0.5f), Mathf.Pow(Random.value, 0.25f));
    aggression = Mathf.Lerp(Random.value, (energy + bravery) / 2f * (1f - sympathy), Mathf.Pow(Random.value, 0.25f));
    dominance  = Mathf.Lerp(Random.value, (energy + bravery + aggression) / 3f, Mathf.Pow(Random.value, 0.25f));
    nervous    = Custom.PushFromHalf(nervous, 2.5f);
    aggression = Custom.PushFromHalf(aggression, 2.5f);
    Random.state = state;
}
```

Three traits are independent and pushed away from the middle; the other three are mostly derived (a brave, energetic, unsympathetic creature is usually aggressive) with a random escape hatch. Scavenger bravery sets how uncomfortable they are near unknown noises, dominance decides who wins a contested grab, nervousness shortens how long a creature stays after a scare. The Pursuer is spawned with aggression and bravery 1, sympathy 0 and nervousness 0.05.

### 2.8 Social events and reputation (SocialEventRecognizer.cs, CreatureCommunities.cs)

Each realized room has a `SocialEventRecognizer`. Creatures report to it: `WeaponAttack(weapon, thrower, victim, hit)`, `Theft(item, thief, victim)`, `Killing(killer, victim)`, `CreaturePutItemOnGround`. It classifies them into `EventID`s (LethalAttackAttempt, LethalAttack, NonLethalAttackAttempt, NonLethalAttack, Theft, Killing, ItemOffering, ItemTransaction) and broadcasts to every AI in the room that implements `IReactToSocialEvents`. That is how a scavenger squad turns on you when you spear one of them, and how gifting a pearl works.

Longer memory lives in `CreatureCommunities`: a like-of-player value per community (Scavengers, Lizards, Cicadas, GarbageWorms, Deer, JetFish), per region and per player. `InfluenceLikeOfPlayer(community, region, player, influence, interRegionBleed, interCommunityBleed)` is called on kills and gifts, with the template's `communityInfluence` scaling how much a death of that species matters. Individual scavengers add a per-creature `SocialMemory` on top.

### 2.9 Case study: how a scavenger decides to throw (ScavengerAI.cs)

Scavengers rank held and visible items with `WeaponScore`, a small integer ranking that puts explosive spears above plain spears above rocks, gives an ignited explosive spear or a spear stuck in a wall a score of 0, and returns 999 for any spear when the scavenger really wants one. In Attack the AI keeps a `ViolenceType` (None, ForFun, Warning or lethal) that decides whether a throw is meant to hit, then searches for a throwing position: candidate tiles near the current one are scored by `SpearThrowPositionScore`, which rewards a clear horizontal line to where the target can move, a comfortable distance band and standing on a floor, and penalises tiles the target could reach quickly. When the creature stands on a tile whose score is good and the target is on a near-horizontal line, it throws. The Hunted mod ported that scoring function into the Pursuer's slugcat AI, which is why the Pursuer looks for ledges opposite you rather than charging.

### 2.10 Writing your own brain

A custom AI is a subclass that adds modules in its constructor, implements the relationship interface, and overrides `Update` to read the modules and set a destination. The Pursuer's slugcat brain does this on top of the slugpup AI, because `Player.AI` is typed as `SlugNPCAI` and the body only reads input from an AI of that type:

```csharp
public class PursuerAI : SlugNPCAI, IUseARelationshipTracker, IReactToSocialEvents
{
    public PursuerAI(AbstractCreature creature, World world) : base(creature, world)
    {
        // the slugpup constructor already added StandardPather, Tracker, FriendTracker,
        // RelationshipTracker, StuckTracker, ItemTracker, NoiseTracker, PreyTracker,
        // ThreatTracker and UtilityComparer; only the weights change
        SetWeight(preyTracker, 0f);
        SetWeight(friendTracker, 0f);
        SetWeight(threatTracker, 1f);
    }

    public override void Update()
    {
        if (cat.room != null && lastRoom != cat.room.abstractRoom.index) NewRoom(cat.room);
        timeInRoom++;
        for (int i = 0; i < modules.Count; i++) modules[i].Update();

        target = FindTarget();               // the player, from the tracker
        mode = rainSoon ? Mode.EscapeRain
             : threatTracker.Utility() > (lethalThreat ? 0.75f : 0.5f) ? Mode.Flee
             : target != null && target.TicksSinceSeen < 200 ? Mode.Engage
             : wantsBetterWeapon ? Mode.Scavenge
             : target != null ? Mode.Search : Mode.Travel;

        creature.abstractAI.SetDestination(CoordinateFor(mode));
        Move();                              // path -> input package, ported from the slugpup
    }

    CreatureTemplate.Relationship IUseARelationshipTracker.UpdateDynamicRelationship(RelationshipTracker.DynamicRelationship d)
    {
        if (d.trackerRep.representedCreature.realizedCreature is Player p && !p.isNPC)
            return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Attacks, 1f);
        return StaticRelationship(d.trackerRep.representedCreature);
    }
}
```

Installing it is one hook: in `On.AbstractCreature.InitiateAI`, set `self.abstractAI.RealAI = new PursuerAI(self, self.world)` for the marked creature and skip the original.

---
## Part 3. Pathfinding

Path finding in Rain World is a per-creature cost field rooted at the destination, refilled a few steps per tick, over a room map that was compiled once when the room loaded. The creature never holds a path; every tick it asks which neighbouring connection is cheapest and takes it.

### 3.1 The AI map (AImapper.cs, AImap.cs, AItile.cs)

When a room is realized an `AImapper` runs on a background thread and produces one `AImap`: a grid of `AItile` plus a terrain proximity array.

- `AItile.acc` is one of `OffScreen, Floor, CurvedFloor, Corridor, Climb, Wall, Ceiling, Air, Solid, Sand`. `Corridor` is a one-tile-wide passage, `Climb` a pole or beam, `Wall` and `Ceiling` are air tiles touching those surfaces.
- `incomingPaths` and `outgoingPaths` list every `MovementConnection` that starts or ends at the tile.
- `narrowSpace`, `walkable`, `floorAltitude`, `smoothedFloorAltitude`, `visibility`, `fallRiskTile` and the water flags feed perception, lurking and fall avoidance.
- `terrainProximity` is the distance to the nearest solid tile, used by big creatures and by the threat tracker's accessibility logic.

A `MovementConnection` is `(type, startCoord, destinationCoord, distance)`. The types are the whole vocabulary of movement in the game: `Standard, ReachOverGap, ReachUp, DoubleReachUp, ReachDown, SemiDiagonalReach, DropToFloor, DropToClimb, DropToWater, LizardTurn, OpenDiagonal, Slope, CeilingSlope, ShortCut, NPCTransportation, BigCreatureShortCutSqueeze, OutsideRoom, SideHighway, SkyHighway, SeaHighway, RegionTransportation, BetweenRooms, OffScreenMovement, OffScreenUnallowed`.

The map is species-neutral. What a species can do with it lives in the template: `pathingPreferencesTiles` is a `PathCost` per accessibility value and `pathingPreferencesConnections` a `PathCost` per movement type. A `PathCost` is `(resistance, legality)` with legality one of `Allowed, Unwanted, IllegalConnection, IllegalTile, SolidTile, Unallowed`; legality compares before resistance, so a creature takes any allowed route before an unwanted one regardless of distance. `AImap.TileAccessibleToCreature` and `ConnectionCostForCreature` combine map and template, and `CreatureSpecificAImap` caches the result per species per room.

### 3.2 The search (PathFinder.cs)

`PathFinder` keeps a `PathingCell` per tile with `costToGoal`, `heuristicValue`, `generation`, `reachable` and `possibleToGetBackFrom`. `SetDestination` bumps the generation and seeds the open list with the destination cell. Each tick, `Update` pops up to `stepsPerFrame` cells and expands them backwards along incoming connections, so the field grows from the goal outward and every reached cell knows its cost to the goal.

```csharp
protected void CheckNeighbours(PathingCell checkNow)
{
    int i = 0;
    while (true)
    {
        MovementConnection con = ConnectionAtCoordinate(outGoing: false, checkNow.worldCoordinate, i++);
        if (con == default(MovementConnection)) break;
        PathingCell from = PathingCellAtWorldCoordinate(con.startCoord);
        PathCost step = CheckConnectionCost(from, checkNow, con, followingPath: false);
        if (!step.Allowed || !from.reachable) continue;
        PathCost total = checkNow.costToGoal + step;
        PathCost heuristic = HeuristicForCell(from, total);
        if (from.generation < pathGeneration)
        {
            from.costToGoal = total; from.heuristicValue = heuristic;
            from.generation = pathGeneration; AddToCheckNextList(from);
        }
        else if (from.inCheckNextList && from.costToGoal > total)
        {
            from.costToGoal = total;   // re-sorted by the lower heuristic
        }
    }
}
```

Design notes:

- Generations instead of clearing. Changing destination does not reset the grid; cells from an old generation are stale and get overwritten as the new wave reaches them. `creatureFollowingGeneration` lets the creature keep walking on the previous field until the new one reaches its tile, so retargeting never stalls the body.
- Budgeted. `stepsPerFrame` is per creature; a `PathfinderResourceDivider` on the game splits a global budget between all creatures asking for accessibility mapping in the same tick.
- Shared accessibility. Before pathing, an `AccessibilityMapper` flood-fills which tiles the creature can reach at all. `CreaturesPathingIdentical(A, B)` lets creatures of the same species share one mapper.
- Get-back-ability. `possibleToGetBackFrom` marks tiles you can reach but not return from (a drop). Unless `walkPastPointOfNoReturn` is set, the follower refuses such connections, which is why lizards do not jump into pits after you.
- Preferences at query time. `CheckConnectionCost` calls the AI's virtual `TravelPreference(connection, cost)`, so a module can raise the cost of tiles near a threat (the slugpup adds `threatTracker.ThreatOfTile(...) * 100`) or near a creature it is uncomfortable with.

### 3.3 Following the field (StandardPather.cs, Lizard.cs, SlugNPCAI.cs)

`StandardPather.FollowPath(originPos, actuallyFollowing)` looks at the outgoing connections of the tile the creature stands on and picks the best by legality first, then by freshest generation, then by `costToGoal + stepCost`. Connections already followed several times recently are marked unwanted, which breaks oscillation. The result is a single `MovementConnection`; turning it into motion is the creature class's job.

Lizards do it in `Lizard.FollowConnection(runSpeed)`: for a Standard or Slope step it adds velocity to the head chunk toward the destination tile, nudges up or sideways when the destination is a Climb tile, applies `lizardParams.floorLeverage` as upward force when it has grip, and snaps the body toward the middle of corridor tiles. `MovementAnimationEnded(connection, success)` reports back, feeding the stuck and obstacle trackers. The slugpup does it differently: `SlugNPCAI` converts the upcoming connections into a fake controller `Player.InputPackage` (x, y, jump, throw, pickup), so the NPC slugcat runs the same movement code as the player. The Pursuer inherits that layer.

### 3.4 When it goes wrong

The `StuckTracker` keeps the last 20 positions (slugpup: `totalTrackedLastPositions = 20`, checked from 5 back, stuck when within 1 tile) and a `MoveBacklog` of attempted connections; its utility rises with the stuck counter and species AIs react by picking a random nearby destination or leaving the room. The `ObstacleTracker` records tiles and objects that stopped a move, and `PathFinder.forbiddenEntrance` temporarily bans an exit the creature just bounced off. Offscreen, `AbstractCreatureAI.FollowPath` has its own fallback: if the creature is not at a node and has been idle for 200 abstract ticks it teleports to a random node it is allowed to use, with a log line beginning "Abstr Stuck".

The Pursuer relies on the vanilla path finder unchanged. It only ever calls `creature.abstractAI.SetDestination(coord)`, which forwards to the realized `PathFinder` when the body exists and to the abstract path otherwise. Its additions sit above that: choosing which coordinate to want (the player's tile, an attack position, a shelter, a flee tile from `threatTracker.FleeTo`).

---

## Part 4. The offscreen world

Rooms you cannot see still tick. They tick coarsely, one room per frame, and the creatures in them move as records along a graph of exits and dens rather than as bodies.

### 4.1 The region graph (AbstractRoom.cs, World.cs, WorldLoader.cs)

A `World` holds one `AbstractRoom` per room of the region (plus gates and an `offScreenDen`). Each abstract room has `nodes[]` of `AbstractRoomNode`, a `connections[]` array saying which room each exit leads to, and precomputed `ConnectionLength(nodeA, nodeB)` distances in tiles between its own nodes, taken from the room's AI map when the world loaded. A `WorldCoordinate` with only `room` and `abstractNode` set is a position on this graph. The graph is the ROOMS section of `world_xx.txt`: room name, then its connections in node order, plus optional tags such as SHELTER, GATE and SWARMROOM.

### 4.2 How offscreen time passes (RainWorldGame.cs, AbstractRoom.cs)

```csharp
// RainWorldGame.Update, story mode
updateAbstractRoom++;
if (updateAbstractRoom >= world.NumberOfRooms) updateAbstractRoom = 0;
world.GetAbstractRoom(updateAbstractRoom + world.firstRoomIndex).Update(world.NumberOfRooms);
```

Every frame exactly one abstract room updates, and it is told that `NumberOfRooms` ticks have passed. A region of 60 rooms updates each room every 60 frames (1.5 seconds) with a time budget of 60 ticks, which keeps offscreen time flowing at the same average rate as onscreen time while costing one room's worth of work per frame. `AbstractRoom.Update(timePassed)` calls `Update(timePassed)` on every entity and `InDenUpdate` on creatures inside dens; realized creatures skip their abstract AI because the real one is running.

### 4.3 The abstract brain (AbstractCreatureAI.cs)

`AbstractCreatureAI` owns a `path` (a list of `WorldCoordinate`s computed by `AbstractSpacePathFinder` over nodes and room connections), a `destination`, an optional `migrationDestination`, a `denPosition` from the den finder and a `timeBuffer` of unspent ticks.

```csharp
public virtual void Update(int time)
{
    timeBuffer += time;
    if (parent.Room.realizedRoom == null)
    {
        AbstractBehavior(time);
        if (destination.CompareDisregardingTile(parent.pos) && !DoIHaveAPathToCoordinate(destination))
        {
            SetDestination(parent.pos);   // "has a destination but no path to it"
            migrationDestination = null;
        }
    }
}

public virtual void AbstractBehavior(int time)
{
    if (parent.realizedCreature != null) { MigrationBehavior(time); return; }
    if (followCreature != null) MoveWithCreature(followCreature, goToCreatureDestination: false);
    if (path.Count > 0) FollowPath(time);
    else if (!MigrationBehavior(time) && TimeInfluencedRandomRoll(parent.creatureTemplate.roamInRoomChance, time))
        RandomMoveWithinRoom();
}
```

`FollowPath` spends the buffer: the next hop costs `ConnectionLength / (creatureTemplate.offScreenSpeed * offscreenSpeedFac)` ticks, and the creature moves one node (`parent.Move(...)`) each time the buffer covers it. Moving into a room that is realized goes through `AbstractCreature.ChangeRooms`, which calls `Realize()` and pushes the new body into a pipe with `ShortcutHandler.CreatureEnterFromAbstractRoom`, so a lizard chasing you arrives through the same pipe you used. Species subclasses (`LizardAbstractAI`, `ScavengerAbstractAI`, `SlugNPCAbstractAI`) override `AbstractBehavior` to add hunting grounds, squad logic, returning to dens at night (`WantToStayInDenUntilEndOfCycle`) and the roaming chances from the template (`roamBetweenRoomsChance`, `roamInRoomChance`).

### 4.4 Realize and abstractize (AbstractCreature.cs, RoomRealizer.cs)

`AbstractCreature.Realize()` constructs the species body, calls `InitiateAI()` to build the realized brain and sets it as `abstractAI.RealAI`, then realizes any objects stuck to it (held spears, carried creatures). `Abstractize(coord)` does the reverse: the body is destroyed, `RealAI` is nulled, and only the record with its last position and state survives. Rooms decide this, not creatures: the `RoomRealizer` tracks the player's room, realizes it and candidate neighbours, deletes rooms the player has not visited recently, and keeps the sum of `RoomPerformanceEstimation` under a `performanceBudget` of 1500, shaving the most expensive distant rooms when it goes over. Everything in a room that unloads is abstractized in place.

### 4.5 Dens, spawns and lineages

Creatures are spawned by the CREATURES section of the world file into a den node of a room, with optional spawn data and LINEAGE chains that replace a dead creature with the next species in the chain after some cycles. A creature in a den is in `abstractRoom.entitiesInDens` and counts down `remainInDenCounter`; night creatures and those with `ignoreCycle` follow their own timers. `IsExitingDen()` realizes the creature if the room is loaded and sends it out through the den's pipe. Dens are also where creatures store prey (`stowFoodInDen`) and where scavengers keep the region's treasury.

### 4.6 What the Hunted mod does with this layer

The Pursuer's offscreen movement is not the vanilla abstract AI: a creature walking the graph at `offScreenSpeed` would take many cycles to cross a region. Instead the mod keeps its own state (region, shelter, gear) in the save data and applies "two shelters per survived cycle" on a shelter graph built from the world files. The vanilla layer takes over only inside the player's region: the abstract creature is spawned into a room with `AbstractRoom.AddEntity`, and its `AbstractBehavior` hook keeps calling `SetDestination` toward the player's room, so the base `FollowPath` code walks it there node by node and the game realizes it through a pipe when it reaches a loaded room.

---

## Part 5. Bodies and physics

There is no Unity rigidbody anywhere in a Rain World creature. A body is a handful of circles with velocity, a few distance constraints between them, and swept collision against a tile grid.

### 5.1 BodyChunk (BodyChunk.cs)

A `BodyChunk` has `pos`, `lastPos`, `vel`, `rad`, `mass`, a `rotationChunk` it takes its facing from, and flags such as `collideWithTerrain`, `collideWithSlopes`, `collideWithObjects`, `goThroughFloors` and `restrictInRoomRange`. Its `Update()` is an explicit Euler step with friction (simplified):

```csharp
public void Update()
{
    vel.y -= owner.gravity;
    if (owner.room.water && submergedBelow)
    {
        vel.y += owner.buoyancy * owner.EffectiveRoomGravity * submersion;
        vel *= Mathf.Lerp(owner.airFriction, owner.waterFriction, submersion);
    }
    else vel *= owner.airFriction;

    lastLastPos = lastPos;
    lastPos = pos;
    pos += vel;      // then swept terrain collision using lastPos -> pos
    // SharedPhysics.HorizontalCollision / VerticalCollision / SlopesVertically
}
```

Gravity, air friction, water friction, buoyancy, surface friction and bounce are properties of the owning `PhysicalObject`, so a spear and a lizard fall differently by construction. The terrain pass moves the chunk out of solid tiles along the axis it entered, records `contactPoint` (which side it touched; the AI and animation read this constantly) and calls `TerrainImpact(chunk, direction, speed, firstContact)` on the owner when it lands hard enough. That is where fall damage, landing sounds and the slugcat's roll come from.

### 5.2 BodyChunkConnection (PhysicalObject.cs)

Chunks are held together by distance constraints, solved once per tick by position correction (the projection step of position-based dynamics). Verbatim:

```csharp
public class BodyChunkConnection
{
    public BodyChunk chunk1, chunk2;
    public float distance, elasticity, weightSymmetry;
    public bool active;
    public Type type;   // Normal, Pull (rope: only when too far), Push (only when too close)

    public BodyChunkConnection(BodyChunk c1, BodyChunk c2, float distance, Type type, float elasticity, float weightSymmetry)
    {
        chunk1 = c1; chunk2 = c2; this.distance = distance; this.type = type; this.elasticity = elasticity;
        this.weightSymmetry = weightSymmetry == -1f ? c2.mass / (c1.mass + c2.mass) : weightSymmetry;
        active = true;
        c1.rotationChunk = c2; c2.rotationChunk = c1;
    }

    public void Update()
    {
        if (!active) return;
        float d = Vector2.Distance(chunk1.pos, chunk2.pos);
        if (type == Type.Normal || (type == Type.Pull && d > distance) || (type == Type.Push && d < distance))
        {
            Vector2 dir = Custom.DirVec(chunk1.pos, chunk2.pos);
            Vector2 push1 = dir * ((distance - d) * weightSymmetry * elasticity);
            Vector2 push2 = dir * ((distance - d) * (1f - weightSymmetry) * elasticity);
            chunk1.pos -= push1; chunk1.vel -= push1;
            chunk2.pos += push2; chunk2.vel += push2;
        }
    }
}
```

Three details make this feel alive. The correction is applied to `vel` as well as `pos`, so constraints transfer momentum instead of teleporting. `weightSymmetry` defaults to the mass ratio, so a heavy body drags a light head rather than the reverse. `elasticity` below 1 leaves the constraint slightly unsatisfied each tick, giving springy necks and tails without a spring model. A slugcat is two chunks (radius 9 and 8, mass split in half) with one Normal connection; a lizard is three chunks in a line with two; a vulture or a centipede is a longer chain.

### 5.3 Update order (PhysicalObject.cs, SharedPhysics.cs)

```
PhysicalObject.Update:
  for each chunk: chunk.Update()             // integrate + terrain
  abstractPhysicalObject.pos.Tile = room.GetTilePosition(FirstChunk().pos)
  for each connection: connection.Update()   // constraints
  drop any grasps the grabber has released
  base.Update(eu)
  update appendages (hittable non-chunk parts)
```

Creature subclasses then run their own logic (`Creature.Update` -> `Lizard.Update` -> `Act()` -> `FollowConnection`), which mostly means adding velocity to chunks. Object-to-object collision is done by the room after all objects have moved: objects sit in `collisionLayer` lists and pairs on the same layer whose chunks overlap get `Collide(other, myChunk, otherChunk)` and are pushed apart with `PushOutOf(pos, rad, exceptedChunk)`. Projectiles do not use overlap tests: `SharedPhysics.TraceProjectileAgainstBodyChunks` sweeps the spear's segment from `lastPos` to `pos` against every chunk in range, which is why fast spears never tunnel.

### 5.4 Grasps and sticks (Creature.cs, AbstractPhysicalObject.cs)

Holding is a `Creature.Grasp`: grabber, grabbed object, which chunk of it, and a `Shareability` (can several creatures hold the same thing). `Grab(obj, graspUsed, chunkGrabbed, shareability, dominance, overrideEquallyDominant, pacifying)` resolves contested grabs by `dominance`, which is where personality enters physics. Each tick the grabber's code moves the held chunk to the hand position. Because held objects must survive abstraction, every grasp is mirrored by an `AbstractPhysicalObject.CreatureGripStick` on the abstract side; realizing the creature realizes the stuck objects too. The Hunted spawner gives the Pursuer its gear by creating those sticks before the body exists.

### 5.5 Damage

All harm goes through `Creature.Violence(source, directionAndMomentum, hitChunk, hitAppendage, damageType, damage, stunBonus)`. The template's `baseDamageResistance`, `baseStunResistance` and a per-damage-type `damageRestistances` table scale it; `instantDeathDamageLimit` is the one-shot threshold. Species override it to add reactions before calling `Die()` or `Stun(ticks)`. Hooking `Violence` is the standard way to learn who hurt whom, which Part 8 uses as its reward signal.

### 5.6 Ropes (Rope.cs)

Anything string-like that must wrap around terrain (a lizard's tongue, the Saint's tongue, tentacle rope mode) uses `Rope`: two endpoints and a list of `bends`, each a tile corner the rope is currently wrapped around. `Update(newA, newB)` sweeps the moved segments against corners, adds a bend when a segment crosses one and removes bends whose neighbours no longer wrap them, and `totalLength` sums the polyline.

---
## Part 6. Procedural animation and inverse kinematics

Rain World has no animation clips for creatures. The physics decides where the two or three chunks of a body are; a separate graphics module hangs particles off them for the head, tail, limbs and cosmetics, drives each particle with a small rule, and draws whatever results. Inverse kinematics appears only where a joint must be drawn between two known points.

### 6.1 Physics and graphics are different objects (GraphicsModule.cs, BodyPart.cs)

A creature's `GraphicsModule` is created by `InitiateGraphicsModule()` when the camera needs it and can be culled (`ShouldBeCulled`, `cullRange`) when the creature is far from the camera. It owns a `bodyParts[]` array of `BodyPart`s and implements `InitiateSprites`, `DrawSprites(sLeaser, rCam, timeStacker, camPos)`, `ApplyPalette` and `AddToContainer`. Nothing in a graphics module pushes the physics back; that separation is what lets the game skip animation entirely for offscreen or culled creatures.

A `BodyPart` is a particle: `pos`, `lastPos`, `vel`, `rad`, `terrainContact`. Its toolbox is three methods every animation rule is built from:

```csharp
// pull (or push, when push is true) this part to within connectionRad of pnt,
// with optional springiness and velocity inheritance from the host chunk
public void ConnectToPoint(Vector2 pnt, float connectionRad, bool push, float elasticMovement,
                           Vector2 hostVel, float adaptVel, float exaggerateVel)
{
    if (elasticMovement > 0f) vel += Custom.DirVec(pos, pnt) * (Vector2.Distance(pos, pnt) * elasticMovement);
    vel += hostVel * exaggerateVel;
    if (push || !Custom.DistLess(pos, pnt, connectionRad))
    {
        float d = Vector2.Distance(pos, pnt);
        Vector2 correction = Custom.DirVec(pos, pnt) * (connectionRad - d);
        pos -= correction; vel -= correction;
    }
    vel -= hostVel; vel *= 1f - adaptVel; vel += hostVel;   // damp relative to the host
}
public void PushFromPoint(Vector2 pnt, float pushRad, float elasticity) { /* opposite */ }
public void PushOutOfTerrain(Room room, Vector2 basePoint) { /* keep the part out of solid tiles, on the base's side */ }
```

### 6.2 Tails: chains of segments (TailSegment.cs)

A tail is an array of `TailSegment`s, each connected to the previous with a maximum distance `connectionRad`. Segment 0 is connected to a chunk position. The update is one distance constraint plus friction plus terrain push-out:

```csharp
public override void Update()
{
    lastPos = pos;
    pos += vel;
    vel *= airFriction;
    stretched = 1f;
    if (connectedSegment != null && !Custom.DistLess(pos, connectedSegment.pos, connectionRad))
    {
        float d = Vector2.Distance(pos, connectedSegment.pos);
        Vector2 dir = Custom.DirVec(pos, connectedSegment.pos);
        Vector2 mine = dir * ((connectionRad - d) * (1f - affectPrevious));
        Vector2 theirs = dir * ((connectionRad - d) * affectPrevious);
        pos -= mine; vel -= mine;
        if (pullInPreviousPosition) connectedSegment.pos += theirs;
        connectedSegment.vel += theirs;
        stretched = Mathf.Clamp((connectionRad / (d * 0.5f) + 2f) / 3f, 0.2f, 1f);   // drawn thinner when stretched
    }
    PushOutOfTerrain(owner.owner.room, connectedSegment != null ? connectedSegment.pos : connectedPoint.Value);
}
```

`affectPrevious` is how much a segment tugs the one before it: a slugcat's tail barely affects the body, a lizard's heavy tail does. Species code adds forces on top: the slugcat tail gets gravity, a sideways swing from the body's velocity and a "tail up" impulse when jumping; the lizard tail gets `tailStiffness` (decaying along the tail by `tailStiffnessDecline`) pulling each segment toward the line of the spine. Drawing a tail is a `TriangleMesh.MakeLongMesh(segments, pointyTip, customColor)` whose vertices are placed each frame at the interpolated segment positions, offset sideways by `rad * stretched`.

### 6.3 Limbs: hunting a position (Limb.cs)

A `Limb` is a body part with a target it moves toward and one of four `Mode`s: `HuntAbsolutePosition` (go to a world point), `HuntRelativePosition` (go to a point in the body's frame, rotated with it), `Retracted` (stick to the connection chunk) and `Dangle` (free particle). The two knobs are `huntSpeed`, the maximum step per tick, and `quickness`, how fast the velocity turns toward the target.

```csharp
public override void Update()
{
    lastPos = pos;
    if (mode == Mode.HuntRelativePosition)
        absoluteHuntPos = connection.pos + Custom.RotateAroundOrigo(relativeHuntPos,
                          Custom.AimFromOneVectorToAnother(connection.rotationChunk.pos, connection.pos));
    if (mode == Mode.HuntRelativePosition || mode == Mode.HuntAbsolutePosition)
    {
        if (Custom.DistLess(absoluteHuntPos, pos, huntSpeed)) { vel = absoluteHuntPos - pos; reachedSnapPosition = true; }
        else { vel = Vector2.Lerp(vel, Custom.DirVec(pos, absoluteHuntPos) * huntSpeed, quickness); reachedSnapPosition = false; }
    }
    else if (mode == Mode.Retracted) { vel = connection.vel; pos = connection.pos; reachedSnapPosition = true; }

    if (mode != Mode.Retracted)
    {
        pos += vel;
        if (mode == Mode.HuntRelativePosition) pos += connection.vel;
        vel *= airFriction;
        if (pushOutOfTerrain) PushOutOfTerrain(owner.owner.room, connection.pos);
    }
}
```

The other half is `FindGrip(room, attachedPos, searchFromPos, maximumRadiusFromAttachedPos, goalPos, forbiddenXDirs, forbiddenYDirs, behindWalls)`. It looks at the nine tiles around `searchFromPos`, and for each solid tile computes the closest point on that tile's exposed face to `goalPos` (a floor tile offers its top edge, a wall its side), keeps the candidate nearest the goal that is still within reach of the attachment, and sets it as the absolute hunt position. When the limb arrives, `GrabbedTerrain()` fires. There is no ray cast and no collider query; the tile grid is the collider.

### 6.4 A lizard's walk cycle (LizardLimb.cs)

`LizardLimb` turns those two primitives into a gait. Each leg is in one of two states, tracked by `reachingForTerrain`:

1. Planted. The foot holds its grip point while the body moves on. Each tick it measures how far behind the shoulder's travel line the foot has fallen (`Custom.DistanceToLine` against the perpendicular of the body direction blended toward `lizard.limbsAimFor`, the tile the lizard is walking to). Once that exceeds `jointDist * StepLength` (from `lizardParams.stepLength`) the leg lifts.
2. Swinging. The hunt position becomes a point ahead of the shoulder: `Lerp(pos, connection.pos, liftFeet) + a * (jointDist + 1)`, so `liftFeet` controls how high the foot arcs. Then `FindGrip` is called with the goal pushed forward 50 px along the body direction, nudged down by `feetDown`, and displaced sideways by `legPairDisplacement` alternating per leg so the pair does not land on the same tile. Reaching the grip plants the foot again (`GrabbedTerrain`).

Pair coordination is a single rule: a leg will not lift while its partner has been gripping for less than `limbGripDelay` ticks, and a leg that has been holding an extra long step waits for its partner. Injury lowers `health`, which lowers `huntSpeed` and `quickness` and randomly disables the leg (`currentlyDisabled`, the leg goes to `Dangle` with gravity), which is why a speared lizard limps instead of playing a limp animation. Swimming sets all legs to dangle and adds a sinusoidal paddle. Every breed differs only by `LizardBreedParams`: `limbSize`, `limbThickness`, `stepLength`, `liftFeet`, `feetDown`, `noGripSpeed`, `limbSpeed`, `limbQuickness`, `limbGripDelay`, `smoothenLegMovement`, `legPairDisplacement`, `walkBob`, plus the tail and head fields.

### 6.5 Slugcat hands (SlugcatHand.cs, PlayerGraphics.cs)

`SlugcatHand : Limb` chooses its target from the player's current `AnimationIndex` and `BodyModeIndex` each tick: when holding an object it hunts a point between the body and the held object's chunk; when climbing a pole it hunts the pole's centre line; on a wall it calls `FindGrip` around the body with the goal in the input direction; when crawling it hunts a relative position ahead of the body; otherwise it hunts a relative rest position. `PlayerGraphics` also keeps a head (`GenericBodyPart`), four tail segments, a `drawPositions` array of smoothed chunk positions, `disbalanceAmount` for the wobble when stopping, `breath` and `blink` timers, and a `PlayerObjectLooker` that picks something interesting to look at and turns the head toward it. None of these are animation states; they are continuous rules on particles.

### 6.6 Inverse kinematics (RWCustom/Custom.cs)

Where the game needs an elbow or a knee it uses one function: analytic two-bone IK by the law of cosines.

```csharp
// va = shoulder, vc = hand, A = upper length, B = lower length, flip = +/-1 chooses the bend side
public static Vector2 InverseKinematic(Vector2 va, Vector2 vc, float A, float B, float flip)
{
    float d = Vector2.Distance(va, vc);
    float angle = Mathf.Acos(Mathf.Clamp((d * d + A * A - B * B) / (2f * d * A), 0.2f, 0.98f)) * (flip * 180f / (float)Math.PI);
    return va + DegToVec(AimFromOneVectorToAnother(va, vc) + angle) * A;   // the elbow
}
```

The clamp on the cosine (0.2 to 0.98) is a deliberate cheat: it never lets the joint fully straighten or fully fold, so limbs always look bent and the function never returns NaN when the hand drifts out of reach. The endpoints come from the particle system (shoulder chunk, hand limb), so IK here is purely a drawing step. Users in the code: deer legs and antlers (`DeerGraphics`), big spider legs (`BigSpiderGraphics`, upper and lower 0.7 of leg length), scavenger arms (18 + 18 px) and legs (`ScavengerGraphics`, whose `ScavengerHand` and `ScavengerLeg` are limbs), Miros bird knees (`MirosBird`) and several Watcher creatures. Lizard legs are drawn without IK: a straight segment from shoulder to foot, thickened by `limbThickness`.

### 6.7 Tentacles (Tentacle.cs, VultureTentacle.cs)

Long grabbing appendages that must wrap around terrain (daddy long legs limbs, vulture wings, deer antlers, Watcher loach legs) use `Tentacle`. It combines three representations: `segments`, a list of tiles the tentacle currently occupies from base to tip, maintained like a path with its own mini search (`PCell` with generation, heuristic and parent); `tChunks`, particles (`TentacleChunk`) spaced along the tentacle at parameter `tPos` that render and collide, each snapped toward its segment's tile when stuck; and optionally a `Rope` per chunk for rope mode. A tentacle is steered by `MoveGrabDest(point, path)`, which ray traces a tile path from the base to the wanted grab point and stores it as `grabPath`; each update `AlignWithGrabPath` moves one segment at a time toward that path and `AdjustLength` grows or shrinks the tile list toward `idealLength`. `TentacleProps` configures the feel: `stiff`, `rope`, `shorten`, `goalAttractionSpeedTip`, `alignToSegmentSpeed`, `backtrackSpeed`, `chunkVelocityCap`, `terrainHitsBeforePhase` and the tile update cadence. `VultureTentacle` adds a `Mode` of `Climb` or `Fly`, which is why a vulture's wings can also be its arms when it climbs out of a pipe.

### 6.8 Drawing

Sprites are Futile `FSprite`s and `TriangleMesh`es leased from the camera through a `RoomCamera.SpriteLeaser`. `DrawSprites` runs every rendered frame with the `timeStacker`, and every position it uses is `Vector2.Lerp(part.lastPos, part.pos, timeStacker) - camPos`. Colours come from `ApplyPalette`, which reads the room's `RoomPalette` so a creature darkens in a dark room; the Hunted mod recolours the Pursuer by overriding this call and leaving sprite 9 (the face) for the eyes.

---

## Part 7. Building a similar system in Unity (beginner tutorial)

This is a from-scratch walkthrough for someone who has opened Unity a handful of times. You will build a 2D creature the Rain World way: a chunk body with constraints, a tail, legs that find their own footholds, drawn elbows from two-bone IK, and a small modular AI that hunts a player over a grid path. Everything is plain C# inside MonoBehaviours; no packages beyond what a 2D project ships with.

Before you start: make a new project with the 2D template. In Edit > Project Settings > Time set Fixed Timestep to 0.025 (40 Hz, same as the game). Add a Tilemap with a Tilemap Collider 2D and a Composite Collider 2D on a layer called Ground; paint a floor, a wall and a ledge. Add a sprite called Player you can move with any controller you like. All scripts go in Assets/Scripts.

### Step 1. Two clocks: simulate in FixedUpdate, draw with interpolation

Unity gives you the same split Rain World uses: `FixedUpdate` runs at the fixed timestep, `Update` runs per frame. Keep every simulated position as a `pos`/`lastPos` pair and draw the lerp. The fraction below is the game's `timeStacker`.

```csharp
public static class SimClock
{
    // 0 at the moment of the last FixedUpdate, 1 just before the next one
    public static float TimeStacker => Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
}
```

### Step 2. The chunk and its constraint

Plain classes, not components. A chunk integrates itself and collides with the ground layer by sweeping a circle from `lastPos` to `pos`; a connection is the game's `BodyChunkConnection` almost line for line.

```csharp
using UnityEngine;

public class Chunk
{
    public Vector2 pos, lastPos, vel;
    public float rad, mass;
    public bool onGround;            // what Rain World calls contactPoint.y == -1
    public Chunk(Vector2 p, float r, float m) { pos = lastPos = p; rad = r; mass = m; }

    public void Step(float gravity, float airFriction, LayerMask ground)
    {
        vel.y -= gravity;
        vel *= airFriction;
        lastPos = pos;
        Vector2 target = pos + vel;
        onGround = false;

        // swept circle: stop at the first solid thing between lastPos and target
        Vector2 delta = target - pos;
        RaycastHit2D hit = Physics2D.CircleCast(pos, rad, delta.normalized, delta.magnitude, ground);
        if (hit.collider != null)
        {
            pos = hit.centroid + hit.normal * 0.01f;           // centre of the circle at impact
            float into = Vector2.Dot(vel, hit.normal);
            if (into < 0f) vel -= hit.normal * into;            // remove the velocity going into the wall
            if (hit.normal.y > 0.5f) onGround = true;
            // slide the remainder along the surface
            Vector2 rest = (target - pos); rest -= hit.normal * Vector2.Dot(rest, hit.normal);
            RaycastHit2D hit2 = Physics2D.CircleCast(pos, rad, rest.normalized, rest.magnitude, ground);
            pos = hit2.collider != null ? hit2.centroid + hit2.normal * 0.01f : pos + rest;
        }
        else pos = target;
    }
}

public class Connection
{
    public Chunk a, b; public float distance, elasticity, weightSymmetry;
    public Connection(Chunk a, Chunk b, float distance, float elasticity)
    {
        this.a = a; this.b = b; this.distance = distance; this.elasticity = elasticity;
        weightSymmetry = b.mass / (a.mass + b.mass);        // the heavy chunk moves less
    }
    public void Solve()
    {
        float d = Vector2.Distance(a.pos, b.pos);
        Vector2 dir = (b.pos - a.pos).normalized;
        Vector2 pushA = dir * ((distance - d) * weightSymmetry * elasticity);
        Vector2 pushB = dir * ((distance - d) * (1f - weightSymmetry) * elasticity);
        a.pos -= pushA; a.vel -= pushA;
        b.pos += pushB; b.vel += pushB;
    }
}
```

Units: the game works in pixels with gravity 0.9 per tick and air friction 0.999; in Unity world units try gravity 0.02, airFriction 0.99, radii around 0.3, and tune from there. Both are per tick, not per second.

### Step 3. A body component

```csharp
using UnityEngine;

public class ChunkBody : MonoBehaviour
{
    public LayerMask ground;
    public float gravity = 0.02f, airFriction = 0.99f;
    public Chunk[] chunks; public Connection[] connections;
    public Chunk Main => chunks[0];

    void Awake()
    {
        Vector2 p = transform.position;
        chunks = new[] { new Chunk(p, 0.3f, 1f), new Chunk(p + Vector2.left * 0.5f, 0.3f, 1f), new Chunk(p + Vector2.left * 1f, 0.28f, 0.8f) };
        connections = new[] { new Connection(chunks[0], chunks[1], 0.5f, 0.9f), new Connection(chunks[1], chunks[2], 0.5f, 0.9f) };
    }

    void FixedUpdate()
    {
        foreach (var c in chunks) c.Step(gravity, airFriction, ground);   // integrate + terrain
        foreach (var k in connections) k.Solve();                        // constraints
    }

    // where the AI adds muscle: a push on one chunk
    public void Push(int chunk, Vector2 force) { chunks[chunk].vel += force; }

    void Update()
    {
        Vector2 draw = Vector2.Lerp(Main.lastPos, Main.pos, SimClock.TimeStacker);
        transform.position = draw;
    }
}
```

Put this on an empty object with a sprite child and press play: three circles fall, land, and settle in a line. Push the head with `Push(0, Vector2.right * 0.05f)` from a test script and the body follows it, head first, tail last. That drag-along is the lizard's whole locomotion model.

### Step 4. A tail

```csharp
using UnityEngine;

public class Tail : MonoBehaviour
{
    public ChunkBody body; public int segments = 6; public float spacing = 0.22f, gravity = 0.015f, friction = 0.96f, affectPrevious = 0.25f;
    public LayerMask ground; LineRenderer line;
    Vector2[] pos, lastPos, vel;

    void Start()
    {
        line = GetComponent<LineRenderer>(); line.positionCount = segments + 1;
        pos = new Vector2[segments]; lastPos = new Vector2[segments]; vel = new Vector2[segments];
        for (int i = 0; i < segments; i++) pos[i] = lastPos[i] = body.chunks[^1].pos + Vector2.left * spacing * (i + 1);
    }

    void FixedUpdate()
    {
        Chunk root = body.chunks[^1];
        for (int i = 0; i < segments; i++)
        {
            lastPos[i] = pos[i];
            vel[i].y -= gravity; vel[i] *= friction; pos[i] += vel[i];
            Vector2 anchor = i == 0 ? root.pos : pos[i - 1];
            float d = Vector2.Distance(pos[i], anchor);
            if (d > spacing)
            {
                Vector2 dir = (anchor - pos[i]).normalized * (d - spacing);
                pos[i] += dir * (1f - affectPrevious); vel[i] += dir * (1f - affectPrevious);
                if (i > 0) { pos[i - 1] -= dir * affectPrevious; vel[i - 1] -= dir * affectPrevious; }
            }
            Collider2D hit = Physics2D.OverlapCircle(pos[i], 0.08f, ground);
            if (hit != null) { pos[i] = hit.ClosestPoint(pos[i]) + (pos[i] - (Vector2)hit.bounds.center).normalized * 0.1f; vel[i] *= 0.5f; }
        }
    }

    void Update()
    {
        float t = SimClock.TimeStacker;
        line.SetPosition(0, Vector2.Lerp(body.chunks[^1].lastPos, body.chunks[^1].pos, t));
        for (int i = 0; i < segments; i++) line.SetPosition(i + 1, Vector2.Lerp(lastPos[i], pos[i], t));
    }
}
```

### Step 5. A leg that finds its own footholds

This is the `Limb` idea: a particle that hunts a target at `huntSpeed`, a `FindGrip` that asks the colliders near a goal for the closest surface point, and the planted/swinging state machine of `LizardLimb`.

```csharp
using UnityEngine;

public class Leg
{
    public Vector2 pos, lastPos, vel, huntPos, grip;
    public bool planted; public float reach, huntSpeed, quickness; public int side; // side: -1 or +1 within a pair

    public Leg(Vector2 p, float reach, float huntSpeed, float quickness, int side)
    { pos = lastPos = huntPos = grip = p; this.reach = reach; this.huntSpeed = huntSpeed; this.quickness = quickness; this.side = side; }

    // Rain World's FindGrip: the surface point nearest the goal that the leg can still reach
    public bool FindGrip(Vector2 shoulder, Vector2 goal, LayerMask ground)
    {
        float best = float.MaxValue; Vector2 bestPoint = grip; bool found = false;
        foreach (Collider2D col in Physics2D.OverlapCircleAll(goal, reach, ground))
        {
            Vector2 p = col.ClosestPoint(goal);
            float score = (p - goal).sqrMagnitude;
            if (score < best && Vector2.Distance(shoulder, p) <= reach) { best = score; bestPoint = p; found = true; }
        }
        if (found) { huntPos = bestPoint; }
        return found;
    }

    public void Update(Vector2 shoulder, Vector2 travelDir, float stepLength, float liftFeet, LayerMask ground)
    {
        lastPos = pos;
        if (planted)
        {
            float behind = Vector2.Dot(shoulder - pos, travelDir);   // how far the foot fell behind
            if (behind > reach * stepLength || Vector2.Distance(shoulder, pos) > reach) planted = false;
            else { pos = grip; vel = Vector2.zero; return; }
        }
        // swinging: aim ahead and slightly up, then look for terrain near that point
        Vector2 goal = shoulder + travelDir * reach * 0.9f + Vector2.up * liftFeet + new Vector2(-travelDir.y, travelDir.x) * side * 0.15f;
        if (!FindGrip(shoulder, goal + Vector2.down * 0.6f, ground)) huntPos = goal;
        if (Vector2.Distance(huntPos, pos) < huntSpeed) { pos = huntPos; if (Physics2D.OverlapCircle(pos, 0.05f, ground)) { grip = pos; planted = true; } }
        else vel = Vector2.Lerp(vel, (huntPos - pos).normalized * huntSpeed, quickness);
        pos += vel; vel *= 0.8f;
    }
}
```

Attach two or four legs to a chunk in a `Legs` component: call `leg.Update(chunk.pos, bodyDirection, 0.6f, 0.3f, ground)` every FixedUpdate, with the rule that a leg may only unplant when its pair partner has been planted for a few ticks. Draw each leg as a line from the shoulder to the interpolated foot. Move the body with `Push` and the legs step by themselves; change `stepLength` and `liftFeet` and you have a different breed.

### Step 6. Elbows and knees with two-bone IK

```csharp
public static class IK
{
    // Rain World's Custom.InverseKinematic, with the same cosine clamp so joints never lock or NaN
    public static Vector2 Elbow(Vector2 shoulder, Vector2 hand, float upper, float lower, float flip)
    {
        float d = Vector2.Distance(shoulder, hand);
        float cos = Mathf.Clamp((d * d + upper * upper - lower * lower) / (2f * d * upper), 0.2f, 0.98f);
        float bend = Mathf.Acos(cos) * flip;                            // radians
        Vector2 dir = (hand - shoulder).normalized;
        Vector2 rotated = new Vector2(dir.x * Mathf.Cos(bend) - dir.y * Mathf.Sin(bend), dir.x * Mathf.Sin(bend) + dir.y * Mathf.Cos(bend));
        return shoulder + rotated * upper;
    }
}
// drawing a leg: line.SetPositions(new Vector3[]{ shoulder, IK.Elbow(shoulder, foot, 0.35f, 0.35f, side), foot });
```

Use a three-point LineRenderer per leg. The flip sign decides whether the knee points forward or back, so front legs and hind legs get opposite signs, like the deer and the big spider in the game.
### Step 7. A modular AI

Perception, memory and needs are separate objects. Start with three modules and a comparer.

```csharp
using System.Collections.Generic;
using UnityEngine;

public abstract class AIModule
{
    protected CreatureAI AI;
    protected AIModule(CreatureAI ai) { AI = ai; }
    public virtual void Tick() { }
    public virtual float Utility() => 0f;            // 0..1, how urgent this need is right now
}

public class Tracker : AIModule                       // sees the player; remembers where it last was
{
    public Transform player; public LayerMask ground; public float visualRadius = 8f;
    public Vector2 lastSeen; public int ticksSinceSeen = int.MaxValue; public bool visible;
    public Tracker(CreatureAI ai, Transform player, LayerMask ground) : base(ai) { this.player = player; this.ground = ground; }
    public override void Tick()
    {
        Vector2 eye = AI.body.Main.pos, target = player.position;
        float score = Mathf.InverseLerp(visualRadius, 0f, Vector2.Distance(eye, target));   // 1 near, 0 at the radius
        visible = score > 0f && !Physics2D.Linecast(eye, target, ground);                    // then the ray
        if (visible) { lastSeen = target; ticksSinceSeen = 0; } else if (ticksSinceSeen < int.MaxValue) ticksSinceSeen++;
    }
}

public class PreyModule : AIModule                    // wants to reach the tracked player
{
    Tracker t; public PreyModule(CreatureAI ai, Tracker t) : base(ai) { this.t = t; }
    public override float Utility() => t.ticksSinceSeen < 400 ? Mathf.Lerp(1f, 0.3f, t.ticksSinceSeen / 400f) : 0f;
}

public class ThreatModule : AIModule                  // afraid of a hazard object; fear fades with distance
{
    public Transform hazard; public float radius = 5f;
    public ThreatModule(CreatureAI ai, Transform hazard) : base(ai) { this.hazard = hazard; }
    public override float Utility() => hazard == null ? 0f : Mathf.InverseLerp(radius, 1f, Vector2.Distance(AI.body.Main.pos, hazard.position));
}

public class UtilityComparer
{
    class Entry { public AIModule m; public float weight, smoothed, smoothing; }
    readonly List<Entry> entries = new();
    public void Add(AIModule m, float weight, float smoothing) => entries.Add(new Entry { m = m, weight = weight, smoothing = smoothing });
    public AIModule Highest(out float utility)
    {
        AIModule best = null; utility = 0f;
        foreach (var e in entries)
        {
            e.smoothed = Mathf.MoveTowards(e.smoothed, e.m.Utility() * e.weight, e.smoothing);   // the game's Tick tween
            if (e.smoothed > utility) { utility = e.smoothed; best = e.m; }
        }
        return best;
    }
}
```

### Step 8. Grid pathfinding with per-creature costs

Rain World's map is a tile grid with movement types; the simplest Unity equivalent is a grid sampled from the tilemap collider, where each cell knows whether it is floor (solid below), air, or wall-adjacent, and a species-specific cost function decides which cells are allowed. A plain A* is fine at this size.

```csharp
using System.Collections.Generic;
using UnityEngine;

public enum Acc { Solid, Floor, Air, Wall }

public class AIMap : MonoBehaviour
{
    public LayerMask ground; public Vector2 origin; public int width = 64, height = 32; public float cell = 0.5f;
    public Acc[,] map;

    void Awake()
    {
        map = new Acc[width, height];
        for (int x = 0; x < width; x++) for (int y = 0; y < height; y++)
        {
            Vector2 p = Center(x, y);
            if (Physics2D.OverlapBox(p, Vector2.one * cell * 0.9f, 0f, ground)) { map[x, y] = Acc.Solid; continue; }
            bool below = Physics2D.OverlapBox(p + Vector2.down * cell, Vector2.one * cell * 0.9f, 0f, ground);
            bool side = Physics2D.OverlapBox(p + Vector2.left * cell, Vector2.one * cell * 0.9f, 0f, ground) || Physics2D.OverlapBox(p + Vector2.right * cell, Vector2.one * cell * 0.9f, 0f, ground);
            map[x, y] = below ? Acc.Floor : side ? Acc.Wall : Acc.Air;
        }
    }
    public Vector2 Center(int x, int y) => origin + new Vector2((x + 0.5f) * cell, (y + 0.5f) * cell);
    public Vector2Int CellOf(Vector2 p) => new Vector2Int(Mathf.Clamp((int)((p.x - origin.x) / cell), 0, width - 1), Mathf.Clamp((int)((p.y - origin.y) / cell), 0, height - 1));

    // the species sheet: what does this creature pay to enter a cell? negative means not allowed
    public delegate float CostFn(Acc from, Acc to, Vector2Int step);

    public List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal, CostFn cost)
    {
        var open = new List<Vector2Int> { start }; var came = new Dictionary<Vector2Int, Vector2Int>();
        var g = new Dictionary<Vector2Int, float> { [start] = 0f };
        Vector2Int[] dirs = { new(1,0), new(-1,0), new(0,1), new(0,-1), new(1,1), new(-1,1), new(1,-1), new(-1,-1) };
        while (open.Count > 0)
        {
            open.Sort((a, b) => (g[a] + Vector2Int.Distance(a, goal)).CompareTo(g[b] + Vector2Int.Distance(b, goal)));
            Vector2Int cur = open[0]; open.RemoveAt(0);
            if (cur == goal) { var path = new List<Vector2Int> { cur }; while (came.ContainsKey(cur)) { cur = came[cur]; path.Add(cur); } path.Reverse(); return path; }
            foreach (var d in dirs)
            {
                Vector2Int n = cur + d;
                if (n.x < 0 || n.y < 0 || n.x >= width || n.y >= height) continue;
                float c = cost(map[cur.x, cur.y], map[n.x, n.y], d);
                if (c < 0f) continue;
                float ng = g[cur] + c * d.magnitude;
                if (!g.ContainsKey(n) || ng < g[n]) { g[n] = ng; came[n] = cur; if (!open.Contains(n)) open.Add(n); }
            }
        }
        return null;
    }
}

// a ground creature: walks on floors, may climb a wall cell, never enters solid, may drop but not fly up
public static class Species
{
    public static float Lizard(Acc from, Acc to, Vector2Int step)
    {
        if (to == Acc.Solid) return -1f;
        if (to == Acc.Floor) return 1f;
        if (to == Acc.Wall) return 3f;                     // climbing is slow
        if (to == Acc.Air) return step.y < 0 ? 1.5f : -1f;  // may drop, may not fly up
        return -1f;
    }
}
```

### Step 9. Put it together: a creature that hunts

```csharp
using System.Collections.Generic;
using UnityEngine;

public class CreatureAI : MonoBehaviour
{
    public enum Behavior { Idle, Hunt, Flee }
    public ChunkBody body; public AIMap map; public Transform player, hazard; public LayerMask ground;
    public float runForce = 0.03f;
    Tracker tracker; UtilityComparer comparer; List<AIModule> modules = new();
    public Behavior behavior; List<Vector2Int> path; int repathTimer;

    void Start()
    {
        tracker = new Tracker(this, player, ground);
        var prey = new PreyModule(this, tracker); var threat = new ThreatModule(this, hazard);
        modules.AddRange(new AIModule[] { tracker, prey, threat });
        comparer = new UtilityComparer();
        comparer.Add(threat, 1f, 1f / 30f);     // fear outranks appetite, both smoothed over about a second
        comparer.Add(prey, 0.6f, 1f / 30f);
    }

    void FixedUpdate()
    {
        foreach (var m in modules) m.Tick();
        AIModule top = comparer.Highest(out float u);
        behavior = u < 0.05f ? Behavior.Idle : top is ThreatModule ? Behavior.Flee : Behavior.Hunt;

        Vector2 dest = behavior switch
        {
            Behavior.Hunt => tracker.lastSeen,
            Behavior.Flee => body.Main.pos + ((Vector2)(body.Main.pos - (Vector2)hazard.position)).normalized * 6f,
            _ => body.Main.pos
        };
        if (--repathTimer <= 0 || path == null) { path = map.FindPath(map.CellOf(body.Main.pos), map.CellOf(dest), Species.Lizard); repathTimer = 20; }
        Follow();
    }

    void Follow()   // the equivalent of Lizard.FollowConnection: push the head toward the next cell
    {
        if (path == null || path.Count < 2) return;
        Vector2Int here = map.CellOf(body.Main.pos);
        int i = path.IndexOf(here); Vector2Int next = path[Mathf.Clamp(i + 1, 0, path.Count - 1)];
        Vector2 dir = (map.Center(next.x, next.y) - body.Main.pos).normalized;
        body.Push(0, dir * runForce);
        if (dir.y > 0.5f && body.Main.onGround) body.Push(0, Vector2.up * runForce * 6f);   // "floor leverage" for a step up
    }
}
```

Drop a hazard object near the creature and it runs; move it away and the creature turns back toward where it last saw you. Because the decision is a comparison of smoothed utilities there is no flicker at the boundary, and adding a fourth need (hunger, rain, injury) is one more class and one `comparer.Add`.

### Step 10 (optional). An abstract layer

Once you have more than one screen of level, keep creatures far from the camera as records: a struct with a room index, a node index, a destination and a time buffer, updated once a second by walking a room graph, and only instantiate the `ChunkBody` when the record enters a loaded room. The rule from the game to copy exactly is the time budget: update one room per frame and hand it roomCount ticks, so the whole world advances at real time with constant cost.

### Where to go from here

- Relative hunt positions for hands and heads (rotate a local offset by the body's facing, add the chunk's velocity) give you idle posture for free.
- Ghosts: when the tracker loses sight, spawn a few guesses that walk the AI map away from the last seen cell and delete any that become visible. Your creature will check corners.
- Relationships as data: a table of (species, species) to (type, intensity) turns one AI class into an ecosystem.
- Meshes for tails instead of lines: a strip mesh with width from a "stretched" factor reads as flesh.

Note: the Unity scripts are written to compile against a current Unity 2D project but were not executed in an editor; treat constants as starting points.

---

## Part 8. Adaptive AI: adding a neural network

The module architecture is unusually friendly to learning, because the decision surface is already a handful of numbers: module weights, thresholds, and a few discrete tactical choices. You do not replace the AI with a network. You let a small network nudge those numbers based on what has worked against this particular player.

### 8.1 Where learning can plug in

| Lever | What varies | Signal that says it worked | Risk |
|---|---|---|---|
| Utility weights | `UtilityTracker.weight` per module (flee vs engage vs scavenge) | Damage dealt per encounter, encounters survived | Low: bounded, easy to reset |
| Tactical choice | Throw now / reposition / close in / wait, given the situation | Hit landed within 2 s of the choice | Medium: needs a decent feature vector |
| Positioning | Which candidate attack tile to prefer (a learned term added to `SpearThrowPositionScore`) | Line of sight held, hit landed | Medium |
| Player model | Predict where the player goes when threatened (which exit, up or down) | Prediction error against what the player did | Low, and the most "adapts to you" feeling |
| Movement | Jump timing, pole catches | Falls, stuck counter | High: can break the body; do it offline |

Start with utility weights and the player model. Both are cheap, both are safe to run online, and together they produce the behaviour players describe as "it learned my tricks": it stops fleeing from a player who never presses an advantage, and it starts waiting at the pipe you always run to.

### 8.2 Constraints inside a Rain World mod

- Runtime: Mono on .NET Framework 4.7.2. No GPU, no ONNX runtime, no NuGet ML packages that load cleanly under BepInEx. A hand-written multilayer perceptron is the practical choice and is enough: a 16 -> 24 -> 4 network is under a thousand multiply-adds, evaluated a few times per second, not per tick.
- Data volume: one player produces perhaps twenty encounters an hour. Anything that needs thousands of episodes to converge is out. Contextual bandits, tiny policy-gradient updates and evolutionary hill-climbing over a parameter vector all work at this scale; deep reinforcement learning does not.
- Persistence: learned parameters must survive death, so they must not live in the death-persistent save string the Hunted tracker uses (that reverts on a bad cycle by design). Write them to a per-save-slot JSON next to the save data or in the mod's config folder, versioned, with a reset option in Remix.
- Fairness: the player must still be able to win. Cap every learned parameter inside a designer-set range, decay toward the defaults slowly, and keep a floor of randomness so the Pursuer stays readable rather than optimal.

### 8.3 Features and rewards from the existing modules

Everything a network needs is already computed by the trackers each tick; the feature vector is a read, not a new sensor.

```csharp
public struct Situation            // 16 floats, all roughly in 0..1
{
    public float dx, dy;           // player offset in tiles / 20, signed
    public float distance;         // tiles / 20
    public float lineOfSight;      // Tracker.CreatureRepresentation.VisualContact ? 1 : 0
    public float ticksSinceSeen;   // / 400
    public float playerAbove;      // dy > 0.05
    public float playerOnPole;     // player.animation == ClimbOnBeam etc.
    public float playerHasSpear;   // from ItemTracker / grasps
    public float playerMoving;     // player.mainBodyChunk.vel.magnitude / 10
    public float myWeapon;         // GearTier / 4
    public float myThreat;         // threatTracker.Utility()
    public float attackPosScore;   // SpearThrowPositionScore of the current tile / max
    public float exitsNearPlayer;  // count of exits within 6 tiles / 4
    public float rainProgress;     // world.rainCycle.CycleProgression
    public float recentHits;       // hits landed in the last 60 s / 3
    public float recentMisses;     // throws missed in the last 60 s / 3
}
```

Rewards come from hooks the mod already has or trivially adds: `On.Creature.Violence` where `source.owner` is a spear thrown by the Pursuer (hit: +1, lethal: +3), a wall-hit miss (-0.2), the Pursuer taking damage (-1), the player leaving the room after being seen (-0.3 for losing them), and an encounter-level bonus when the player dies. Assign each reward to the decisions made in the previous two seconds (a short eligibility window), which is all the credit assignment a throw needs.

### 8.4 A network small enough to write by hand

```csharp
public sealed class TinyNet
{
    readonly int nIn, nHid, nOut;
    readonly float[] w1, b1, w2, b2;            // row-major
    readonly float[] hid;

    public TinyNet(int nIn, int nHid, int nOut, int seed)
    {
        this.nIn = nIn; this.nHid = nHid; this.nOut = nOut;
        var rng = new System.Random(seed);
        w1 = new float[nIn * nHid]; b1 = new float[nHid]; w2 = new float[nHid * nOut]; b2 = new float[nOut]; hid = new float[nHid];
        float s1 = 1f / (float)System.Math.Sqrt(nIn), s2 = 1f / (float)System.Math.Sqrt(nHid);
        for (int i = 0; i < w1.Length; i++) w1[i] = (float)(rng.NextDouble() * 2 - 1) * s1;
        for (int i = 0; i < w2.Length; i++) w2[i] = (float)(rng.NextDouble() * 2 - 1) * s2;
    }

    public float[] Forward(float[] x, float[] outBuf)
    {
        for (int h = 0; h < nHid; h++)
        {
            float s = b1[h];
            for (int i = 0; i < nIn; i++) s += w1[i * nHid + h] * x[i];
            hid[h] = (float)System.Math.Tanh(s);
        }
        for (int o = 0; o < nOut; o++)
        {
            float s = b2[o];
            for (int h = 0; h < nHid; h++) s += w2[h * nOut + o] * hid[h];
            outBuf[o] = s;                      // raw scores; caller applies softmax or picks argmax
        }
        return outBuf;
    }

    // one step of gradient descent on output o toward target t (squared error), after a Forward on x
    public void Train(float[] x, int o, float t, float lr)
    {
        float[] outBuf = new float[nOut]; Forward(x, outBuf);
        float err = outBuf[o] - t;
        for (int h = 0; h < nHid; h++)
        {
            float gHid = err * w2[h * nOut + o] * (1f - hid[h] * hid[h]);   // tanh derivative
            w2[h * nOut + o] -= lr * err * hid[h];
            b1[h] -= lr * gHid;
            for (int i = 0; i < nIn; i++) w1[i * nHid + h] -= lr * gHid * x[i];
        }
        b2[o] -= lr * err;
    }

    public string Serialize() => string.Join(",", System.Array.ConvertAll(Pack(), f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    float[] Pack() { var all = new float[w1.Length + b1.Length + w2.Length + b2.Length]; int k = 0; foreach (var a in new[] { w1, b1, w2, b2 }) { a.CopyTo(all, k); k += a.Length; } return all; }
    // Deserialize: split, parse with InvariantCulture, copy back in the same order
}
```

Used as a contextual bandit: the four outputs estimate the expected reward of throw, reposition, close in and wait for the current situation. Pick the best with probability 0.85 and a random one otherwise, remember (situation, action, tick), and when a reward arrives within the window call `Train(situation, action, reward, 0.01f)`. This is Q-learning with a one-step horizon, which is honest for a game where the consequence of a throw is known within a second.

### 8.5 Wiring it into the Pursuer

```csharp
// inside PursuerAI.Think(), replacing the fixed Engage rule
if (mode == Mode.Engage && decideCooldown-- <= 0)
{
    decideCooldown = 20;                                       // decide twice a second, not every tick
    Situation s = Sense(target);
    int action = policy.Choose(s.ToArray());                   // bandit over TinyNet outputs
    switch (action)
    {
        case 0: if (GoodAttackPos()) throwAtTarget = Math.Sign(target.pos.x - creature.pos.x); break;
        case 1: coord = FindAttackPosition(excludeCurrent: true); break;
        case 2: coord = target.BestGuessForPosition(); break;
        case 3: coord = creature.pos; break;                   // hold and watch
    }
    history.Add((s, action, timeInRoom));
}

// reward hooks (Hooks.cs)
On.Creature.Violence += (orig, self, source, dir, chunk, app, type, dmg, stun) =>
{
    orig(self, source, dir, chunk, app, type, dmg, stun);
    if (source?.owner is Weapon w && w.thrownBy is Player p && PursuerMark.IsMarked(p.abstractCreature) && self is Player victim && !victim.isNPC)
        HuntedSession.Current?.Learner.Reward(dmg >= victim.Template.instantDeathDamageLimit ? 3f : 1f);
};
```

`Reward(r)` walks the history backwards over the last 80 ticks and trains each (situation, action) toward r discounted by age. Persist `policy.Serialize()` at `SaveState.SessionEnded` to a per-slot file and load it when the game starts.

### 8.6 The player model

A second, smaller network predicts the player's escape: input the situation plus the layout around the player (which of up to four exits is nearest, whether there is a pole above, water below), output a probability per exit. Train it every time the player leaves the room while the Pursuer is tracking them (the exit actually used is the label). Then, in Search mode, instead of walking to the last seen tile, the Pursuer walks to the predicted exit and waits. This needs no reward shaping, converges after a dozen examples for a player with habits, and the failure mode is harmless: an ambush at the wrong pipe.

### 8.7 Alternatives worth knowing

- Evolution over cycles. Keep a vector of ten tunables (flee threshold, engage memory, throw range band, weight per mode). Each cycle, mutate a copy slightly; if that cycle's damage-per-encounter beat the parent, keep the child. No gradients, no features, works with any parameter the designer already exposes. This is the safest first step.
- Offline training, online inference. Record situations and outcomes to a log, train a larger network on a PC with any framework, and ship weights as a text file the mod loads. You lose per-player adaptation but gain a better baseline; the two combine well: shipped weights as the prior, online bandit updates on top.
- In Unity the same design maps onto ML-Agents for offline training and Sentis for inference, with the situation struct as the observation and the four tactical actions as a discrete branch. Keep the utility comparer as the outer loop and let the agent choose only inside Engage.

### 8.8 Testing a learning creature

1. Unit-test `TinyNet` in the game-independent test project: it must fit XOR and must round-trip through `Serialize`.
2. Add a hotkey that prints the four action values for the current situation to the log, and an overlay line showing the chosen action; you cannot tune what you cannot see.
3. Build an encounter loop with the existing keys: F6 to bring the Pursuer, fight, F11 to reset, repeat. Log reward per encounter and plot it; it should trend up over ten to twenty encounters against a consistent play style and stay flat against random play.
4. Play against it badly on purpose (always flee to the same pipe) and confirm it starts waiting there; then change habit and confirm it unlearns within a few encounters. That second test catches over-confident learning rates.
5. Ship with a Remix toggle and a reset button, and log the learned parameters at session start so bug reports carry them.
