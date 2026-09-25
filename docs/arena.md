# The arena: training the Stage 3 Pursuer without a player

The slugcat Pursuer's adaptive tactics (the "Stage 3" layer, see [testing.md](testing.md))
learn from fights against you, at maybe twenty encounters an hour. The arena is a
headless copy of that fight: the Stage 3 learner, with the same policy code the game
runs, against an opponent built from the Stage 2 rules, in a simplified room, thousands
of encounters a second, several arenas at once. Its output is a **baseline tactics
file** that the mod loads for any save slot that has nothing learned yet, so a fresh
Pursuer starts from a trained prior and keeps adapting to you from there.

Nothing in the arena touches the game. It is built from the mod's game-independent
core (`src/Core`), like the tests are, so it runs anywhere .NET runs, and the mod DLL
does not contain it.

## Running it

```powershell
dotnet run --project tools/Hunted.Arena -c Release -- --instances 12 --episodes 400000 --rounds 0 --hours 8 --curriculum
```

| Option | Meaning | Default |
|---|---|---|
| `--instances N` | Arenas (each with its own policy) trained at the same time, one thread each. | 4 |
| `--episodes N` | Encounters per instance per round. | 300 |
| `--eval N` | Encounters each policy plays with exploration off after a round, to judge it. | 4000 |
| `--rounds N` | Rounds to run; `0` means until `--hours` passes or a `STOP` file appears in the output folder. | 1 |
| `--hours H` | Stop after the round that passes this many hours. | no limit |
| `--seed N` | Base seed; instance *i* uses `seed*1000+i`. Same seed, same run. | 1 |
| `--out DIR` | Output folder. | `arena-out` |
| `--from FILE` / `--resume` | Continue training from a tactics file (every instance starts from a copy) / from the baseline already in the output folder. | fresh |
| `--gear`, `--opponent-gear` | `none`, `rock`, `spear` or `explosive` for each side. | spear |
| `--spares N` | Spare spears lying in the room. | 3 |
| `--dodge P` | Chance per tick that the opponent jumps a thrown weapon. | 0 |
| `--max-ticks N` | Ticks per encounter before it is a draw (40 per second). | 2400 |
| `--fixed-room` | The fixed test room instead of a random layout per encounter. | random |
| `--curriculum` | Mix other lessons into the encounters, one after another: a dodging opponent, rocks on either side, an explosive spear, an unarmed opponent. Judging always uses the setup above. | off |
| `--block N` | Encounters per row of `report.csv`. | 500 |
| `--log-decisions` | Also write every trained decision (features, tactic, credit) to `decisions.csv` (short runs only: one row per decision). | off |
| `--install [DIR]` | Copy the baseline into the game's `ModConfigs` folder whenever it is written. | off |

A thread plays about ten thousand encounters a second, so a round of 400 000 encounters
per instance takes under a minute. Each round ends with a judgement on a fixed set of
encounters (same seed every round, same rooms and start positions for every policy):
every instance's policy with exploration and learning off, the **control** (the Stage 2
rules in the learner's seat: what "no learning" scores against the same opponent), and
four **yardsticks** (policies that always pick one tactic). The best instance by reward
per encounter becomes the best-so-far if it beats the previous best, and the best-so-far
is written as the baseline only while it beats every yardstick by more than the noise
floor (two standard errors at the judged size). A policy that cannot beat "always Wait"
is not worth shipping as a prior, and the summary says so instead of writing it.

## What it writes

In the output folder:

- `dion_hunted_baseline_tactics.txt`: the best policy over every round so far, written
  only when it clears the bar above, in the same format as the per-slot files the game
  writes. Copy it to `Rain World\RainWorld_Data\StreamingAssets\ModConfigs\` (or pass
  `--install`) and every slot without a `dion_hunted_tactics_<slot>_<slugcat>.txt` starts
  from it. `scripts/inspect_tactics.py <file>` prints what it would do in typical situations.
- `instances/instance_N.txt`: every instance's policy, to continue from or compare.
- `summary.txt`: appended each round: what the round trained on, the control, the
  fairness check, the four yardsticks, every instance's judgement, the best-so-far and
  whether it clears the bar, and each instance's curve over the round in ten blocks.
- `report.csv`: appended each round, one row per instance per block of `--block`
  encounters (round, instance, first episode, encounters, wins, deaths, trades, draws,
  mean reward, mean ticks, throws and hits per encounter, exploration), for plotting.
- `decisions.csv` with `--log-decisions`: the dataset the bandit trained on, one row per
  decision, for offline experiments with other models (guide §8.7).

Create a file named `STOP` in the output folder to end a long run after the current round.

## What is in the arena

The learner is the real thing: `TacticPolicy` and `TinyNet` from the mod, the twelve
features in the order and on the scales of `PursuerAI.ChooseTactic`, a decision every
twenty ticks while armed and engaging, `NoteThrow` when it lines up, and the rewards of
the game hooks (hit +2/+1/+0.5 to the decision that threw, wall hit -0.2, hurt -0.25..-1,
kill +3, death -3, with the two-second window). Its body follows the Stage 2 rules ported
from `PursuerAI`: a throwing position level with the target at throwing range with line of
sight (scored the way `SpearThrowPositionScore` scores, level spots first), a throw the
tick it is lined up (level within the game's angle window, in range, in sight), picking up
the nearest better weapon when unarmed, and the tracker's memory: contact holds for a
hundred ticks before it looks again, Engage lasts three hundred ticks after that, a lost
target is remembered for thirty seconds and gets a fresh fix after five hundred ticks.

The opponent uses the same rules plus two things a person does that the Stage 2 rules do
not, because without them the arena teaches camping:

- **It does not climb into an armed Pursuer's line of fire.** The pure rules climb a pole
  or jump a crate toward a learner standing above within throwing distance, and get
  speared on the way up while unable to reply. Constant Wait won 51% and died 32% against
  the pure rules, against 42/44 for the rules themselves; with this rule the opponent
  holds at the foot of the climb instead. (`OpponentAvoidsExposure`)
- **It walks away from a fight that is not happening.** After fifteen seconds without a
  throw from either side the encounter ends as a draw, as a player leaves a Pursuer that
  only stands there. (`OpponentPatienceTicks`, 600)

The summary reports a **fairness check** every round: the pure rules against the pure
rules, which come out even (wins within a few points of deaths). The control is the Stage
2 rules in the learner's seat against the player-like opponent, so it is not even: the
rules climb into the opponent's line and lose more than they win, which is the point.

What is simplified: the room (a floor, one-way platforms reached by poles or jumps, crates
for cover, no water, pipes or beams), the body (two chunks that walk, jump and climb; no
slides, pounces, wall jumps or crawling), throws (level from the main chunk at the game's
40 px per tick, dropping at the game's rate so a level throw reaches about 430 px and one
at 520 px passes under a target on the same floor; a miss loses the spear as it does for
the AI in the game), damage (a spear does 0.6 to 1.4 of a slugcat's one point of health, a
rock 0.2 and a long stun), and the opponent (it never dodges unless `--dodge` says so).
There are no predators and no bombs, so the threat and bomb features are always zero here
and the policy learns nothing about them until it meets them in the game.

The baseline is a prior. When the game loads it, the counters come off (decisions,
rewards, surprise averages): exploration starts at 30% as on a fresh slot, and the
surprise averages seed from real play, so the in-game learner explores the real game and
its surprise-driven re-exploration reopens when the game keeps contradicting what the
arena taught.

## What to expect

The duel is close to symmetric, so the learner's edge over the control is mostly fewer
deaths and fewer wasted throws, and its edge over the best yardstick is situational:
approach when level with the target or above it, hold when it is above you. Measured
with four instances, 30 000 encounters each, judged over 10 000 (seeds 21 to 26):

| | reward per encounter |
|---|---|
| control (Stage 2 rules in the learner's seat) | -0.17 to -0.08 |
| always Wait (the best yardstick) | +0.32 to +0.41 |
| best learned policy | +0.43 to +0.68 |

The margin over always-Wait, +0.02 to +0.36, is what the bar measures. A run that does
not clear it writes no baseline; more instances help more than more hours, because the
policy converges within the first few thousand encounters of each instance and the run
then picks the best of many.

## Reading the numbers

```
Round 3 (41 s), trained on a mix of spear vs spear, ... / spear vs none, ...
Control (Stage 2 rules in the learner's seat, same encounters): wins 34%  deaths 45%  trades 7%  draws 15%  reward/encounter -0.17
Fairness check (Stage 2 rules on both sides, no player-like rules): wins 42%  deaths 43%  trades 6%  draws 9%  reward/encounter +0.23
Always Wait      : wins 29%  deaths 27%  trades 8%  draws 36%  reward/encounter +0.32
Instance 4 (seed 1004, 1200000 encounters, 7412345 decisions): wins 36%  deaths 28%  trades 8%  draws 28%  reward/encounter +0.68   <- best this round
Best so far: round 3, wins 36% ... reward/encounter +0.68   (improved this round)
Margin over the best fixed tactic (always Wait): +0.36 (noise floor 0.13): clears the bar, worth shipping
```

- *trades*: both died in the same tick (they lined up on each other and threw together).
- *draws*: nobody died: time ran out, or the opponent lost patience with a fight that was not happening.
- A learning curve that climbs and then flattens has converged for that seed; one that
  swings after flattening is the exploration reopening on surprise (see testing.md).

## Continuing overnight

```powershell
./scripts/arena_overnight.ps1 -Instances 12 -Hours 8
```

builds the tool and runs it with `--rounds 0 --curriculum`, 400 000 encounters per instance
per round, for eight hours, judging after every round and rewriting the baseline whenever
a better policy clears the bar, logging to `arena-out\arena.log`. Add `-Resume` to pick up
from the baseline already there, `-Install` to copy each new baseline into the game. To
watch it:

```powershell
Get-Content arena-out\summary.txt -Tail 40
```
