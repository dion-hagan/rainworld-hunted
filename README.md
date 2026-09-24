# Hunted (Rain World mod)

A second slugcat, **the Pursuer**, spawns in a random shelter somewhere in the world
at the start of your campaign. Every cycle you survive it closes in by two shelters.
When it reaches your region it becomes a real creature that tracks you down room by
room and tries to kill you, scavenging gear as it goes. Kill it and it respawns far
away a few cycles later; die and it backs off so it never camps your spawn.

This is **Stage 1** of the [design](docs/design.md): the Pursuer is an elite
scavenger driven by the game's own scavenger AI, with a permanent grudge and a target
lock on you. It lives inside the ecosystem: lizards, vultures and other scavengers
treat it like any scavenger, and other scavengers are hostile to it.

Targets Rain World 1.11.8 (Remix). Downpour / Watcher are optional: without them
the Pursuer is a regular scavenger instead of an elite.

## Install

1. Subscribe on the Steam Workshop, *or* copy this repo's `mod/` folder to
   `Rain World\RainWorld_Data\StreamingAssets\mods\dion_hunted\` (or run
   `./scripts/deploy.ps1`, which builds and copies it).
2. In-game: **Remix → enable Hunted → apply/restart**.
3. Start or continue any story campaign. The red tracker line at the top of the
   screen shows where the Pursuer is.

## Testing it without waiting cycles

See [docs/testing.md](docs/testing.md): the **Testing** tab in Remix has hotkeys to
summon the Pursuer into your region, bring it into your room, advance the offscreen
tracking by a cycle, change its gear, kill it or reset it, plus the on-screen tracker.

## How it works

- **Shelter graph.** At session start the mod reads every region's `world_xx.txt`
  (through the game's asset resolver, so merged and modded regions count) for the
  current slugcat's timeline, and builds a graph of all shelters. Two shelters are
  neighbours when no third shelter sits between them, so the Pursuer walks the world
  shelter by shelter; gates are ordinary edges (it ignores karma).
- **Offscreen tracking.** The Pursuer's state (`Traveling` / `Arrived` / `Dead`,
  shelter, gear, kills) is one `HUNTED` entry in the death-persistent save data, so it
  is saved and reverted exactly when karma is: a successful sleep advances it two
  shelters (three if you starved), a death makes it retreat, quitting changes nothing.
- **In region.** When it arrives, an elite scavenger spawns near its shelter (never
  in your shelter or next door, with a grace period) and its abstract AI is replaced by
  one that always heads for your room. In your room the vanilla scavenger AI does the
  fighting; hooks force the relationship to *attack*, disable trading, gifts and pack
  behaviour, and make other scavengers treat it as an enemy.
- **Death.** Its gear drops where it dies. Killing it does not cost you scavenger
  reputation.

## Building

```powershell
dotnet build src/Hunted.csproj      # needs Rain World installed (see Directory.Build.props)
dotnet test tests/Hunted.Tests      # game-independent: parser, graph, tracker, save format
./scripts/deploy.ps1                # build + copy into the game's mods folder
```

The game-independent half (`src/Core`) has no game references and is covered by
xunit tests; the `Game` half is the hooks and the session runtime.

## Status

Milestones 1–3 of the design (graph and tracker, Stage 1 Pursuer, gear/death rules)
are implemented. Not yet: RaidScav (hunting scavengers for their gear), map marker,
sleep-screen countdown, proximity audio, Stage 2 slugcat body.

*AI-assisted code, reviewed by a human.*
