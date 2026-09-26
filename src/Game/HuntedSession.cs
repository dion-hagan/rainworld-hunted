using System;
using System.Collections.Generic;
using System.Text;
using Hunted.Core;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>
    /// The per-game runtime of the mod: owns the shelter graph, the Pursuer's
    /// committed state for this cycle, the live creature (when it is in the
    /// player's region), and the bookkeeping the save hooks need to resolve the
    /// cycle when the player sleeps or dies.
    /// </summary>
    public sealed class HuntedSession
    {
        public const string SaveKey = "HUNTED";
        private const string SavePrefix = SaveKey + "<dpB>";
        private const int TicksPerSecond = 40;

        public static HuntedSession Current { get; private set; }

        /// <summary>One line about the Pursuer for the next sleep/death screen, set when a cycle is resolved.</summary>
        public static string LastCycleSummary;

        public readonly RainWorldGame game;
        public readonly SaveState saveState;
        public readonly ShelterGraph graph;

        /// <summary>State at the start of this cycle, or the resolved state once the cycle has been saved.</summary>
        public PursuerState State { get; private set; }
        public AbstractCreature Creature { get; private set; }
        public bool Resolved { get; private set; }
        public bool OverlayVisible;
        /// <summary>What the slugcat body has learned about fighting this player.</summary>
        public PursuerLearner Learner { get; private set; }
        /// <summary>Testing: a tactic the slugcat Pursuer runs instead of asking the learner (nothing is recorded), or null. The Force-move hotkey cycles the six moves.</summary>
        public Tactic? ForcedTactic { get; private set; }

        public bool PursuerDiedThisCycle { get; private set; }
        public bool PlayerKilledByPursuer { get; private set; }
        public int ScavKillsThisCycle { get; private set; }

        private string lastKnownRoom;
        private List<string> lastKnownInventory;
        private bool lastKnownAlive = true;
        private InWorldSnapshot frozenSnapshot;
        private int graceTicks;
        private int stuckTicks;
        private int retargetTicks;
        private int cachedRoomsAway = -1;
        private int roomsAwayRefresh;
        private int virtualCycles;
        private bool testKill;
        private readonly List<string> log = new List<string>();

        private HuntedSession(RainWorldGame game, SaveState saveState)
        {
            this.game = game;
            this.saveState = saveState;
            graph = GameShelterGraph.Build(saveState.saveStateNumber);
            if (TryLoad(saveState.deathPersistentSaveData, out PursuerState loaded))
            {
                State = loaded;
                HuntedLog.Info("Loaded Pursuer state: " + State);
            }
            else
            {
                State = PursuerTracker.Initialize(graph, saveState.denPosition, saveState.seed, Config, saveState.cycleNumber, log);
                WriteToSaveData(saveState.deathPersistentSaveData);
                FlushLog("new campaign");
            }
            OverlayVisible = Options.Instance == null || Options.Instance.ShowOverlay.Value;
            Learner = new PursuerLearner(game, saveState);
        }

        public static void Start(RainWorldGame game, SaveState saveState)
        {
            End();
            if (game == null || saveState == null)
            {
                return;
            }
            try
            {
                Current = new HuntedSession(game, saveState);
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not start the Hunted session", e);
                Current = null;
            }
        }

        public static void End()
        {
            try
            {
                Current?.Learner.Save();
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not save learned tactics at session end", e);
            }
            Current = null;
        }

        public TrackerConfig Config => Options.Instance != null ? Options.Instance.ToTrackerConfig() : new TrackerConfig();

        private static int GraceSeconds => Options.Instance != null ? Options.Instance.GraceSeconds.Value : 20;

        public bool IsPursuer(AbstractCreature creature)
        {
            return creature != null && (PursuerMark.IsMarked(creature) || (Creature != null && ReferenceEquals(creature, Creature)));
        }

        public bool OwnsSaveData(DeathPersistentSaveData data)
        {
            return data != null && saveState != null && ReferenceEquals(saveState.deathPersistentSaveData, data);
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>After the RainWorldGame constructor: the world and players exist.</summary>
        public void OnGameStarted()
        {
            World world = game.world;
            if (world == null || world.singleRoomWorld)
            {
                return;
            }
            HuntedLog.Info("Cycle " + saveState.cycleNumber + " in " + WorldRooms.RegionName(world) + ", player den " + saveState.denPosition + ". Pursuer: " + State + " (" + PursuerTracker.Describe(PursuerTracker.HopsAway(graph, State, saveState.denPosition)) + " away)");
            if (State.Status == PursuerStatus.Arrived && WorldRooms.IsRegion(world, State.Region))
            {
                SpawnFromState(world, 2, true);
            }
        }

        /// <summary>The player went through a gate (or warped): the active world changed.</summary>
        public void OnWorldSwitched(World oldWorld, World newWorld)
        {
            if (newWorld == null || newWorld == oldWorld)
            {
                return;
            }
            if (Creature != null)
            {
                if (Creature.world == newWorld)
                {
                    HuntedLog.Info("The Pursuer followed the player through the gate into " + WorldRooms.RegionName(newWorld) + ".");
                    return;
                }
                lastKnownRoom = Creature.Room != null ? Creature.Room.name : lastKnownRoom;
                lastKnownInventory = PursuerSpawner.ReadInventory(Creature);
                lastKnownAlive = Creature.state.alive && !PursuerDiedThisCycle;
                HuntedLog.Info("The Pursuer stayed behind in " + WorldRooms.RegionName(oldWorld) + " at " + lastKnownRoom + ".");
                Creature = null;
            }
            if (State.Status == PursuerStatus.Arrived && WorldRooms.IsRegion(newWorld, State.Region) && lastKnownInventory == null)
            {
                SpawnFromState(newWorld, 3, true);
            }
        }

        /// <summary>Once per game tick.</summary>
        public void Update()
        {
            if (game.GamePaused)
            {
                return;
            }
            Learner.Advance();
            if (graceTicks > 0)
            {
                graceTicks--;
            }
            if (Creature != null && Creature.Room != null)
            {
                lastKnownRoom = Creature.Room.name;
            }
            if (--roomsAwayRefresh <= 0)
            {
                roomsAwayRefresh = 20;
                AbstractCreature player = TargetPlayer();
                cachedRoomsAway = Creature != null && player != null && Creature.world == game.world ? WorldRooms.Distance(game.world, Creature.pos.room, player.pos.room) : -1;
            }
        }

        /// <summary>Once per rendered frame (RawUpdate): key presses must be sampled here, not at the 40Hz tick.</summary>
        public void FrameUpdate()
        {
            if (game.GamePaused)
            {
                return;
            }
            Hotkeys();
        }

        // ------------------------------------------------------------------ creature

        private void SpawnFromState(World world, int minFromPlayer, bool grace)
        {
            ShelterNode node = graph.Get(State.Shelter);
            AbstractRoom preferred = null;
            if (node != null)
            {
                preferred = world.GetAbstractRoom(node.EntranceRoom ?? "") ?? world.GetAbstractRoom(node.Name);
            }
            if (preferred == null && State.LastRoom != null)
            {
                preferred = world.GetAbstractRoom(State.LastRoom);
            }
            SpawnAt(world, preferred, minFromPlayer, grace);
        }

        private void SpawnAt(World world, AbstractRoom preferred, int minFromPlayer, bool grace)
        {
            Despawn();
            AbstractCreature player = TargetPlayer();
            int playerRoom = player != null ? player.pos.room : -1;
            if (playerRoom < 0 && saveState.denPosition != null)
            {
                AbstractRoom den = world.GetAbstractRoom(saveState.denPosition);
                playerRoom = den != null ? den.index : -1;
            }
            AbstractRoom room = WorldRooms.PickSpawnRoom(world, preferred, playerRoom, minFromPlayer, false);
            if (room == null)
            {
                HuntedLog.Warn("No room found to spawn the Pursuer in " + WorldRooms.RegionName(world) + ".");
                return;
            }
            int nodeIdx = WorldRooms.PickNode(room, PursuerSpawner.Template(PursuerBodies.Selected()));
            if (nodeIdx < 0)
            {
                HuntedLog.Warn("Room " + room.name + " has no node the Pursuer can use.");
                return;
            }
            PursuerBody body = PursuerBodies.Selected();
            Creature = PursuerSpawner.Spawn(world, room, nodeIdx, State.Inventory, body);
            graceTicks = grace ? GraceSeconds * TicksPerSecond : 0;
            stuckTicks = 0;
            retargetTicks = 0;
            lastKnownRoom = room.name;
            lastKnownInventory = null;
            lastKnownAlive = true;
            frozenSnapshot = null;
            PursuerDiedThisCycle = false;
            int away = playerRoom >= 0 ? WorldRooms.Distance(world, room.index, playerRoom) : -1;
            HuntedLog.Info("The Pursuer (" + PursuerBodies.Name(body) + ") is in " + room.name + " (" + away + " rooms from the player), holding " + GearTier.Describe(PursuerSpawner.TrimToBody(State.Inventory, body)) + (grace ? ", grace " + GraceSeconds + "s." : "."));
        }

        public void Despawn()
        {
            if (Creature == null)
            {
                return;
            }
            AbstractCreature old = Creature;
            Creature = null;
            try
            {
                PursuerSpawner.Despawn(old);
            }
            catch (Exception e)
            {
                HuntedLog.Error("Despawn failed", e);
            }
        }

        public AbstractCreature TargetPlayer()
        {
            if (game.Players == null)
            {
                return null;
            }
            foreach (AbstractCreature p in game.Players)
            {
                if (p != null && p.state != null && p.state.alive && p.Room != null)
                {
                    return p;
                }
            }
            return game.Players.Count > 0 ? game.Players[0] : null;
        }

        // ------------------------------------------------------------------ abstract AI

        /// <summary>Replaces the abstract behavior of either body for the Pursuer: always head for the player's room.</summary>
        public void AbstractBehavior(AbstractCreatureAI ai, int time)
        {
            AbstractCreature parent = ai.parent;
            World world = ai.world;
            if (parent.state.dead || world == null || world.game == null || graceTicks > 0)
            {
                return;
            }
            AbstractCreature player = TargetPlayer();
            if (player == null || player.world != world)
            {
                return;
            }
            int target = TargetRoom(world, player);
            if (target < 0)
            {
                return;
            }
            bool rainSoon = world.rainCycle != null && world.rainCycle.TimeUntilRain < 40 * TicksPerSecond;

            if (parent.realizedCreature != null)
            {
                if (!(ai is ScavengerAbstractAI))
                {
                    return; // the slugcat body's own AI travels between rooms by itself
                }
                // The scavenger's real AI moves the body; we only keep its migration goal on the player.
                if (!rainSoon && parent.pos.room != target)
                {
                    int node = WorldRooms.PickNodeToward(world.GetAbstractRoom(target), parent.pos.room, parent.creatureTemplate);
                    if (node > -1)
                    {
                        ai.MigrateTo(new WorldCoordinate(target, -1, -1, node));
                    }
                }
                return;
            }
            if (rainSoon || parent.pos.room == target)
            {
                return; // hunker down offscreen, or wait at the door
            }
            if (ai.path.Count > 0 && ai.destination.room == target)
            {
                ai.FollowPath(time);
                stuckTicks = 0;
                return;
            }
            retargetTicks -= time;
            if (retargetTicks > 0 && ai.path.Count > 0)
            {
                ai.FollowPath(time);
                return;
            }
            retargetTicks = 3 * TicksPerSecond;
            AbstractRoom targetRoom = world.GetAbstractRoom(target);
            int nodeIdx = WorldRooms.PickNodeToward(targetRoom, parent.pos.room, parent.creatureTemplate);
            if (nodeIdx > -1)
            {
                ai.SetDestination(new WorldCoordinate(target, -1, -1, nodeIdx));
                if (ai is ScavengerAbstractAI scav)
                {
                    scav.longTermMigration = ai.destination;
                }
            }
            if (ai.path.Count > 0)
            {
                ai.FollowPath(time);
                stuckTicks = 0;
                return;
            }
            stuckTicks += time;
            if (stuckTicks > 15 * TicksPerSecond)
            {
                stuckTicks = 0;
                Unstick(world, parent, targetRoom);
            }
        }

        /// <summary>
        /// Where the slugcat body should walk to reach the player: the player's own
        /// position, or the room outside the shelter or gate the player is hiding in.
        /// Null when the player is not in this world.
        /// </summary>
        public WorldCoordinate? HuntTargetCoordinate(World world)
        {
            AbstractCreature player = TargetPlayer();
            if (player == null || player.world != world)
            {
                return null;
            }
            AbstractRoom room = world.GetAbstractRoom(player.pos.room);
            if (room == null || room.offScreenDen)
            {
                return null;
            }
            if (!room.shelter && !room.gate)
            {
                return player.pos;
            }
            int outside = TargetRoom(world, player);
            AbstractRoom outsideRoom = outside >= 0 ? world.GetAbstractRoom(outside) : null;
            if (outsideRoom == null)
            {
                return null;
            }
            int node = WorldRooms.PickNodeToward(outsideRoom, room.index, PursuerSpawner.Template(PursuerBodies.Selected()));
            return node > -1 ? new WorldCoordinate(outsideRoom.index, -1, -1, node) : (WorldCoordinate?)null;
        }

        private WorldCoordinate? rainShelterCoord;
        private int rainShelterRoom = -1;
        private int rainShelterAge;

        /// <summary>The nearest shelter to hide in from the rain that is not the one the player is using.</summary>
        public WorldCoordinate? RainShelterCoordinate(World world, int fromRoom)
        {
            if (rainShelterCoord.HasValue && rainShelterRoom == fromRoom && ++rainShelterAge < 400)
            {
                return rainShelterCoord;
            }
            rainShelterRoom = fromRoom;
            rainShelterAge = 0;
            rainShelterCoord = null;
            AbstractCreature player = TargetPlayer();
            int playerRoom = player != null ? player.pos.room : -1;
            Dictionary<int, int> dist = WorldRooms.Distances(world, fromRoom);
            AbstractRoom best = null;
            int bestD = int.MaxValue;
            foreach (KeyValuePair<int, int> pair in dist)
            {
                AbstractRoom r = world.GetAbstractRoom(pair.Key);
                if (r != null && r.shelter && r.index != playerRoom && pair.Value < bestD)
                {
                    best = r;
                    bestD = pair.Value;
                }
            }
            if (best != null)
            {
                int node = WorldRooms.PickNode(best, PursuerSpawner.Template(PursuerBodies.Selected()));
                if (node > -1)
                {
                    rainShelterCoord = new WorldCoordinate(best.index, -1, -1, node);
                }
            }
            return rainShelterCoord;
        }

        /// <summary>The cycle is over (sleep or death): remember the creature and take it out of the world before the game saves it.</summary>
        public void OnSessionEnding()
        {
            if (frozenSnapshot == null)
            {
                frozenSnapshot = Snapshot();
            }
            Despawn();
            Learner.Save();
        }

        private int TargetRoom(World world, AbstractCreature player)
        {
            AbstractRoom room = world.GetAbstractRoom(player.pos.room);
            if (room == null || room.offScreenDen)
            {
                return -1;
            }
            if (room.shelter || room.gate)
            {
                foreach (int c in room.connections)
                {
                    if (c > -1 && world.GetAbstractRoom(c) != null)
                    {
                        return c;
                    }
                }
                return -1;
            }
            return room.index;
        }

        /// <summary>No abstract path to the player: jump to an unloaded room next to the player's room.</summary>
        private void Unstick(World world, AbstractCreature parent, AbstractRoom targetRoom)
        {
            if (targetRoom == null)
            {
                return;
            }
            AbstractRoom pick = null;
            foreach (int c in targetRoom.connections)
            {
                AbstractRoom r = c > -1 ? world.GetAbstractRoom(c) : null;
                if (WorldRooms.IsOrdinaryRoom(r) && r.realizedRoom == null && r.index != parent.pos.room)
                {
                    pick = r;
                    break;
                }
            }
            if (pick == null)
            {
                pick = WorldRooms.PickSpawnRoom(world, targetRoom, targetRoom.index, 2, true);
            }
            if (pick == null)
            {
                return;
            }
            int node = WorldRooms.PickNodeToward(pick, targetRoom.index, parent.creatureTemplate);
            if (node < 0)
            {
                return;
            }
            HuntedLog.Warn("The Pursuer had no path from " + (parent.Room != null ? parent.Room.name : "?") + " to " + targetRoom.name + "; moved it to " + pick.name + ".");
            parent.Move(new WorldCoordinate(pick.index, -1, -1, node));
        }

        // ------------------------------------------------------------------ saving

        /// <summary>Called right before the death-persistent data is serialized.</summary>
        public void PrepareSave(bool saveAsIfPlayerDied, bool saveAsIfPlayerQuit)
        {
            if (Resolved)
            {
                return;
            }
            if (SaveContext.Sleeping)
            {
                ResolveSleep(SaveContext.Malnourished);
            }
            else if (saveAsIfPlayerDied)
            {
                ResolveDeath();
            }
        }

        public void ResolveSleep(bool malnourished)
        {
            if (Resolved)
            {
                return;
            }
            Resolved = true;
            PursuerState next = PursuerTracker.OnCycleSurvived(graph, State, saveState.denPosition, malnourished, Snapshot(), Config, saveState.cycleNumber, log);
            next.ScavKills += ScavKillsThisCycle;
            State = next;
            LastCycleSummary = Summary(false);
            FlushLog(malnourished ? "starved sleep" : "sleep");
        }

        public void ResolveDeath()
        {
            if (Resolved)
            {
                return;
            }
            Resolved = true;
            State = PursuerTracker.OnPlayerDied(graph, State, saveState.denPosition, PlayerKilledByPursuer, Config, log);
            LastCycleSummary = Summary(true);
            FlushLog("death");
        }

        private string Summary(bool afterDeath)
        {
            int hops = PursuerTracker.HopsAway(graph, State, saveState.denPosition);
            switch (State.Status)
            {
                case PursuerStatus.Dead:
                    int cycles = Math.Max(0, State.RespawnCycle - saveState.cycleNumber);
                    return cycles == 0 ? "The Pursuer stirs again." : "The Pursuer is dead. It returns in " + cycles + (cycles == 1 ? " cycle." : " cycles.");
                case PursuerStatus.Arrived:
                    return "The Pursuer is here. " + Capitalize(PursuerTracker.Describe(hops)) + " from this shelter.";
                default:
                    return (afterDeath ? "The Pursuer withdraws. " : "The Pursuer draws closer. ") + Capitalize(PursuerTracker.Describe(hops)) + " away.";
            }
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private InWorldSnapshot Snapshot()
        {
            if (State.Status != PursuerStatus.Arrived)
            {
                return null;
            }
            if (frozenSnapshot != null)
            {
                return frozenSnapshot;
            }
            if (Creature != null)
            {
                return new InWorldSnapshot
                {
                    Alive = Creature.state.alive && !PursuerDiedThisCycle,
                    LastRoom = Creature.Room != null ? Creature.Room.name : lastKnownRoom,
                    Inventory = PursuerSpawner.ReadInventory(Creature),
                };
            }
            if (lastKnownRoom != null && lastKnownInventory != null)
            {
                return new InWorldSnapshot { Alive = lastKnownAlive && !PursuerDiedThisCycle, LastRoom = lastKnownRoom, Inventory = lastKnownInventory };
            }
            if (PursuerDiedThisCycle)
            {
                return new InWorldSnapshot { Alive = false, LastRoom = lastKnownRoom };
            }
            return null;
        }

        public void WriteToSaveData(DeathPersistentSaveData data)
        {
            if (data == null || data.unrecognizedSaveStrings == null)
            {
                return;
            }
            data.unrecognizedSaveStrings.RemoveAll(s => s != null && s.StartsWith(SavePrefix, StringComparison.Ordinal));
            data.unrecognizedSaveStrings.Add(SavePrefix + State.Serialize());
        }

        private static bool TryLoad(DeathPersistentSaveData data, out PursuerState state)
        {
            state = null;
            if (data == null || data.unrecognizedSaveStrings == null)
            {
                return false;
            }
            foreach (string entry in data.unrecognizedSaveStrings)
            {
                if (entry != null && entry.StartsWith(SavePrefix, StringComparison.Ordinal))
                {
                    return PursuerState.TryParse(entry.Substring(SavePrefix.Length), out state);
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ events

        public void OnPursuerDied(AbstractCreature creature)
        {
            if (PursuerDiedThisCycle)
            {
                return;
            }
            PursuerDiedThisCycle = true;
            lastKnownAlive = false;
            HuntedLog.Info("The Pursuer died in " + (creature.Room != null ? creature.Room.name : lastKnownRoom) + ".");
            if (!testKill)
            {
                Learner.Reward(-3f, "died");
            }
        }

        public void OnPlayerDied(Player player)
        {
            if (player != null && player.killTag != null && IsPursuer(player.killTag))
            {
                PlayerKilledByPursuer = true;
                HuntedLog.Info("The Pursuer killed the player.");
                Learner.Reward(3f, "killed the player");
            }
        }

        public void OnScavengerKilledByPursuer(Creature victim)
        {
            ScavKillsThisCycle++;
            HuntedLog.Info("The Pursuer killed a scavenger (" + ScavKillsThisCycle + " this cycle).");
        }

        // ------------------------------------------------------------------ testing hotkeys

        private void Hotkeys()
        {
            Options o = Options.Instance;
            if (o == null || !o.DebugHotkeys.Value)
            {
                return;
            }
            if (Input.GetKeyDown(o.KeySummon.Value)) Safe("summon", TestSummon);
            if (Input.GetKeyDown(o.KeyBring.Value)) Safe("bring", TestBring);
            if (Input.GetKeyDown(o.KeyAdvance.Value)) Safe("advance", TestAdvance);
            if (Input.GetKeyDown(o.KeyGear.Value)) Safe("gear", TestCycleGear);
            if (Input.GetKeyDown(o.KeyKill.Value)) Safe("kill", TestKill);
            if (Input.GetKeyDown(o.KeyOverlay.Value)) OverlayVisible = !OverlayVisible;
            if (Input.GetKeyDown(o.KeyReset.Value)) Safe("reset", TestReset);
            if (Input.GetKeyDown(o.KeyForget.Value)) Safe("forget", () => Learner.Forget());
            if (Input.GetKeyDown(o.KeyForceMove.Value)) Safe("force move", TestCycleForcedMove);
        }

        /// <summary>none, Slide, Pounce, SlidePounce, Roll, Backflip, FlipThrow, none: the Pursuer does the forced move whenever it is armed, engaging and on the ground.</summary>
        private void TestCycleForcedMove()
        {
            ForcedTactic = ForcedTactic == null ? Tactic.Slide : ForcedTactic.Value >= Tactic.FlipThrow ? (Tactic?)null : ForcedTactic.Value + 1;
            HuntedLog.Info("[test] forced tactic: " + (ForcedTactic.HasValue ? ForcedTactic.Value.ToString() : "none (the learner chooses again)"));
        }

        private static void Safe(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                HuntedLog.Error("Test action '" + name + "' failed", e);
            }
        }

        private void MarkArrivedHere(World world)
        {
            AbstractCreature player = TargetPlayer();
            PursuerState s = State.Clone();
            s.Status = PursuerStatus.Arrived;
            s.Region = WorldRooms.RegionName(world);
            s.RespawnCycle = -1;
            s.LastRoom = null;
            ShelterNode near = player != null && player.Room != null ? graph.NearestShelter(player.Room.name) : null;
            if (near != null)
            {
                s.Shelter = near.Name;
            }
            State = s;
        }

        /// <summary>F5: the Pursuer is in this region, two rooms away, hunting now.</summary>
        public void TestSummon()
        {
            World world = game.world;
            if (world == null || TargetPlayer() == null)
            {
                return;
            }
            MarkArrivedHere(world);
            HuntedLog.Info("[test] Summoning the Pursuer.");
            SpawnAt(world, null, 2, false);
        }

        /// <summary>F6: the Pursuer enters the player's room through a pipe.</summary>
        public void TestBring()
        {
            World world = game.world;
            AbstractCreature player = TargetPlayer();
            AbstractRoom room = player != null ? world.GetAbstractRoom(player.pos.room) : null;
            if (room == null || room.realizedRoom == null || !room.realizedRoom.shortCutsReady)
            {
                HuntedLog.Warn("[test] Cannot bring the Pursuer: the player's room is not ready.");
                return;
            }
            List<string> inventory = Creature != null ? PursuerSpawner.ReadInventory(Creature) : new List<string>(State.Inventory);
            MarkArrivedHere(world);
            PursuerState s = State.Clone();
            s.Inventory = inventory;
            State = s;
            Despawn();
            PursuerBody body = PursuerBodies.Selected();
            int node = WorldRooms.PickNode(room, PursuerSpawner.Template(body));
            if (node < 0)
            {
                HuntedLog.Warn("[test] The player's room has no pipe the Pursuer can use.");
                return;
            }
            Creature = PursuerSpawner.Spawn(world, room, node, inventory, body);
            frozenSnapshot = null;
            graceTicks = 0;
            stuckTicks = 0;
            lastKnownRoom = room.name;
            lastKnownInventory = null;
            lastKnownAlive = true;
            PursuerDiedThisCycle = false;
            HuntedLog.Info("[test] The Pursuer enters " + room.name + " holding " + GearTier.Describe(inventory) + ".");
        }

        /// <summary>F7: one cycle of offscreen tracking, right now.</summary>
        public void TestAdvance()
        {
            World world = game.world;
            AbstractCreature player = TargetPlayer();
            string shelter = null;
            if (player != null && player.Room != null)
            {
                ShelterNode near = graph.NearestShelter(player.Room.name);
                shelter = near != null ? near.Name : null;
            }
            shelter = shelter ?? saveState.denPosition;
            virtualCycles++;
            PursuerState next = PursuerTracker.OnCycleSurvived(graph, State, shelter, false, Snapshot(), Config, saveState.cycleNumber + virtualCycles, log);
            next.ScavKills += ScavKillsThisCycle;
            ScavKillsThisCycle = 0;
            State = next;
            FlushLog("test advance (player shelter " + shelter + ")");
            Despawn();
            lastKnownRoom = null;
            lastKnownInventory = null;
            lastKnownAlive = true;
            PursuerDiedThisCycle = false;
            if (State.Status == PursuerStatus.Arrived && WorldRooms.IsRegion(world, State.Region))
            {
                SpawnFromState(world, 2, false);
            }
        }

        /// <summary>F8: nothing, rock, spear, explosive spear, spear + bomb, the full Hunter loadout (explosive spear, bomb, spear on the back), nothing...</summary>
        public void TestCycleGear()
        {
            PursuerBody body = PursuerBodies.Selected();
            List<string> current = Creature != null ? PursuerSpawner.ReadInventory(Creature) : new List<string>(State.Inventory);
            List<string> next;
            if (current.Count == 0)
            {
                next = new List<string> { GearTier.Rock };
            }
            else if (current.Contains(GearTier.ScavengerBomb) && current.Contains(GearTier.ExplosiveSpear))
            {
                next = new List<string>();
            }
            else if (current.Contains(GearTier.ScavengerBomb))
            {
                next = new List<string> { GearTier.ExplosiveSpear, GearTier.ScavengerBomb, GearTier.Spear };
            }
            else if (current.Contains(GearTier.ExplosiveSpear))
            {
                next = new List<string> { GearTier.Spear, GearTier.ScavengerBomb };
            }
            else if (current.Contains(GearTier.Spear))
            {
                next = new List<string> { GearTier.ExplosiveSpear };
            }
            else
            {
                next = new List<string> { GearTier.Spear };
            }
            PursuerState s = State.Clone();
            s.Inventory = next;
            State = s;
            if (Creature != null)
            {
                World world = Creature.world;
                AbstractRoom room = Creature.Room;
                int node = WorldRooms.PickNode(room, PursuerSpawner.Template(body));
                Despawn();
                if (room != null && node > -1)
                {
                    Creature = PursuerSpawner.Spawn(world, room, node, next, body);
                    frozenSnapshot = null;
                    graceTicks = 0;
                }
            }
            HuntedLog.Info("[test] Pursuer gear: " + GearTier.Describe(next) + ".");
        }

        /// <summary>F9: kill it where it stands.</summary>
        public void TestKill()
        {
            if (Creature == null)
            {
                HuntedLog.Info("[test] No Pursuer creature to kill.");
                return;
            }
            HuntedLog.Info("[test] Killing the Pursuer.");
            testKill = true;   // a debug kill must not teach the policy anything
            try
            {
                if (Creature.realizedCreature != null)
                {
                    Creature.realizedCreature.Die();
                }
                else
                {
                    Creature.Die();
                }
            }
            finally
            {
                testKill = false;
            }
        }

        /// <summary>F11: a fresh Pursuer, far away.</summary>
        public void TestReset()
        {
            Despawn();
            lastKnownRoom = null;
            lastKnownInventory = null;
            lastKnownAlive = true;
            PursuerDiedThisCycle = false;
            PlayerKilledByPursuer = false;
            ScavKillsThisCycle = 0;
            State = PursuerTracker.Initialize(graph, saveState.denPosition, State.Seed + 1, Config, saveState.cycleNumber, log);
            FlushLog("test reset");
        }

        // ------------------------------------------------------------------ HUD

        public string StatusLine()
        {
            var sb = new StringBuilder("HUNTED   ");
            switch (State.Status)
            {
                case PursuerStatus.Dead:
                    sb.Append("dead   respawns at cycle ").Append(State.RespawnCycle).Append(" (now ").Append(saveState.cycleNumber).Append(')');
                    break;
                case PursuerStatus.Traveling:
                    sb.Append("traveling   ").Append(State.Region).Append('/').Append(State.Shelter).Append("   ").Append(HopsText()).Append(" away   gear: ").Append(GearTier.Describe(State.Inventory));
                    break;
                default:
                    if (Creature != null && Creature.state.alive && !PursuerDiedThisCycle)
                    {
                        string room = Creature.Room != null ? Creature.Room.name : "?";
                        bool visible = Creature.realizedCreature != null;
                        string behavior = graceTicks > 0 ? "waiting (" + (graceTicks / TicksPerSecond + 1) + "s grace)" : BehaviorName();
                        sb.Append("HUNTING   in ").Append(room).Append(visible ? " (visible)" : " (offscreen)");
                        if (cachedRoomsAway >= 0)
                        {
                            sb.Append("   ").Append(cachedRoomsAway).Append(cachedRoomsAway == 1 ? " room" : " rooms").Append(" from you");
                        }
                        sb.Append("   ").Append(behavior).Append("   gear: ").Append(GearTier.Describe(PursuerSpawner.ReadInventory(Creature)));
                    }
                    else if (PursuerDiedThisCycle)
                    {
                        sb.Append("killed this cycle in ").Append(lastKnownRoom ?? "?").Append("   (sleep to confirm)");
                    }
                    else
                    {
                        sb.Append("arrived   ").Append(State.Region).Append('/').Append(State.Shelter).Append(" (not spawned)   ").Append(HopsText()).Append(" away");
                    }
                    break;
            }
            return sb.ToString();
        }

        private string BehaviorName()
        {
            if (Creature.abstractAI != null && Creature.abstractAI.RealAI is PursuerAI pursuer)
            {
                return pursuer.DebugState;
            }
            if (Creature.abstractAI != null && Creature.abstractAI.RealAI is ScavengerAI ai && ai.behavior != null)
            {
                return "behavior: " + ai.behavior.value;
            }
            return "migrating";
        }

        private string HopsText()
        {
            AbstractCreature player = TargetPlayer();
            string shelter = saveState.denPosition;
            if (player != null && player.Room != null)
            {
                ShelterNode near = graph.NearestShelter(player.Room.name);
                if (near != null)
                {
                    shelter = near.Name;
                }
            }
            return PursuerTracker.Describe(PursuerTracker.HopsAway(graph, State, shelter));
        }

        private void FlushLog(string context)
        {
            foreach (string line in log)
            {
                HuntedLog.Info("[" + context + "] " + line);
            }
            log.Clear();
        }
    }

    /// <summary>Set by the PlayerProgression hook while the game saves a survived cycle.</summary>
    internal static class SaveContext
    {
        public static bool Sleeping;
        public static bool Malnourished;
    }
}
