using System;
using System.Collections.Generic;

namespace Hunted.Core
{
    /// <summary>Tunables for the offscreen tracking rules. Mirrors the Remix options.</summary>
    public sealed class TrackerConfig
    {
        public int HopsPerCycle = 2;
        public int StarvedBonusHops = 1;
        public int ArriveWithinHops = 2;
        public int SpawnMinHops = 6;
        public int RetreatHops = 4;
        public int RespawnCycles = 5;
        public bool OffscreenUpgrades = true;
        public bool KeepGearOnRespawn = false;

        /// <summary>Regions with dense scavenger presence: better odds of finding gear offscreen.</summary>
        public HashSet<string> ScavengerRichRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SU", "GW", "SB", "SL", "LM", "SH",
        };
    }

    /// <summary>What the game layer knows about the Pursuer creature at the end of a cycle.</summary>
    public sealed class InWorldSnapshot
    {
        public bool Alive = true;
        public string LastRoom;
        public List<string> Inventory;
    }

    /// <summary>
    /// The offscreen rules of the mod as pure functions over the shelter graph:
    /// where the Pursuer starts, how it closes in every cycle, and what happens
    /// when it or the player dies. Nothing in here touches the game.
    /// </summary>
    public static class PursuerTracker
    {
        public static PursuerState Initialize(ShelterGraph graph, string playerShelter, int seed, TrackerConfig cfg, int cycleNumber, List<string> log = null)
        {
            var state = new PursuerState { Seed = seed, Status = PursuerStatus.Traveling };
            Random rng = Rng(seed, cycleNumber, 17);
            ShelterNode playerNode = graph.NearestShelter(playerShelter);
            ShelterNode spawn = PickSpawnNode(graph, playerNode, cfg.SpawnMinHops, rng);
            if (spawn == null)
            {
                Log(log, "No shelters in the graph; the Pursuer cannot be placed.");
                state.Shelter = playerShelter;
                state.Region = graph.RegionOfRoom(playerShelter);
                return state;
            }
            state.Shelter = spawn.Name;
            state.Region = spawn.Region;
            Log(log, "Pursuer starts at " + spawn.Region + "/" + spawn.Name + ", " + Describe(graph.HopDistance(spawn, playerNode)) + " from " + playerShelter + ".");
            return state;
        }

        /// <summary>
        /// The player slept successfully. Applies the in-world snapshot (if the
        /// Pursuer existed as a creature this cycle), then advances it along the
        /// shelter graph if it is still travelling.
        /// </summary>
        public static PursuerState OnCycleSurvived(ShelterGraph graph, PursuerState previous, string playerShelter, bool malnourished, InWorldSnapshot snapshot, TrackerConfig cfg, int cycleNumber, List<string> log = null)
        {
            PursuerState s = previous.Clone();
            s.CyclesTracked++;
            ShelterNode playerNode = graph.NearestShelter(playerShelter);
            if (playerNode == null)
            {
                Log(log, "Player shelter " + playerShelter + " is not in the shelter graph; Pursuer holds position.");
                return s;
            }

            if (s.Status == PursuerStatus.Dead)
            {
                if (s.RespawnCycle >= 0 && cycleNumber >= s.RespawnCycle)
                {
                    PursuerState reborn = Initialize(graph, playerShelter, s.Seed, cfg, cycleNumber, log);
                    reborn.PlayerKills = s.PlayerKills;
                    reborn.ScavKills = s.ScavKills;
                    reborn.CyclesTracked = s.CyclesTracked;
                    if (cfg.KeepGearOnRespawn)
                    {
                        reborn.Inventory = new List<string>(s.Inventory);
                    }
                    Log(log, "The Pursuer has respawned.");
                    return reborn;
                }
                Log(log, "Pursuer is dead; respawns at cycle " + s.RespawnCycle + " (now " + cycleNumber + ").");
                return s;
            }

            if (s.Status == PursuerStatus.Arrived)
            {
                if (snapshot != null)
                {
                    if (!snapshot.Alive)
                    {
                        s.Status = PursuerStatus.Dead;
                        s.RespawnCycle = cycleNumber + cfg.RespawnCycles;
                        s.Inventory.Clear();
                        s.LastRoom = null;
                        Log(log, "The Pursuer died this cycle; it respawns at cycle " + s.RespawnCycle + ".");
                        return s;
                    }
                    if (snapshot.Inventory != null)
                    {
                        s.Inventory = new List<string>(snapshot.Inventory);
                    }
                    if (snapshot.LastRoom != null)
                    {
                        ShelterNode near = graph.NearestShelter(snapshot.LastRoom);
                        if (near != null)
                        {
                            s.Shelter = near.Name;
                            s.Region = near.Region;
                        }
                        s.LastRoom = snapshot.LastRoom;
                    }
                }
                if (string.Equals(s.Region, playerNode.Region, StringComparison.OrdinalIgnoreCase))
                {
                    Log(log, "Pursuer stays in " + s.Region + " at " + s.Shelter + ", " + Describe(HopsAway(graph, s, playerShelter)) + " from the player.");
                    return s;
                }
                Log(log, "Player left " + s.Region + "; the Pursuer goes back to tracking.");
                s.Status = PursuerStatus.Traveling;
                s.LastRoom = null;
            }

            ShelterNode from = graph.Get(s.Shelter);
            if (from == null)
            {
                Log(log, "Pursuer shelter " + s.Shelter + " is unknown; re-placing it.");
                PursuerState replaced = Initialize(graph, playerShelter, s.Seed, cfg, cycleNumber, log);
                replaced.Inventory = s.Inventory;
                replaced.PlayerKills = s.PlayerKills;
                replaced.ScavKills = s.ScavKills;
                replaced.CyclesTracked = s.CyclesTracked;
                return replaced;
            }
            List<ShelterNode> path = graph.ShortestPath(from, playerNode);
            if (path == null)
            {
                Log(log, "No shelter path from " + from.Name + " to " + playerNode.Name + "; Pursuer holds position.");
                return s;
            }
            int hops = cfg.HopsPerCycle + (malnourished ? cfg.StarvedBonusHops : 0);
            int idx = Math.Min(Math.Max(hops, 0), path.Count - 1);
            Random rng = Rng(s.Seed, cycleNumber, 3);
            for (int i = 1; i <= idx; i++)
            {
                RollOffscreenUpgrade(s, path[i].Region, rng, cfg, log);
            }
            ShelterNode node = path[idx];
            s.Shelter = node.Name;
            s.Region = node.Region;
            int remaining = path.Count - 1 - idx;
            if (remaining <= cfg.ArriveWithinHops)
            {
                s.Status = PursuerStatus.Arrived;
                Log(log, "The Pursuer has arrived: " + node.Region + "/" + node.Name + ", " + Describe(remaining) + " from the player.");
            }
            else
            {
                Log(log, "The Pursuer moved " + idx + " shelters to " + node.Region + "/" + node.Name + ", " + Describe(remaining) + " away.");
            }
            return s;
        }

        /// <summary>The player died (any cause). The Pursuer backs off so the player is not spawn-camped.</summary>
        public static PursuerState OnPlayerDied(ShelterGraph graph, PursuerState previous, string playerShelter, bool killedByPursuer, TrackerConfig cfg, List<string> log = null)
        {
            PursuerState s = previous.Clone();
            if (killedByPursuer)
            {
                s.PlayerKills++;
            }
            if (s.Status == PursuerStatus.Dead)
            {
                return s;
            }
            ShelterNode playerNode = graph.NearestShelter(playerShelter);
            ShelterNode current = graph.Get(s.Shelter);
            ShelterNode retreat = PickRetreatNode(graph, playerNode, current, cfg.RetreatHops);
            if (retreat != null)
            {
                s.Shelter = retreat.Name;
                s.Region = retreat.Region;
            }
            s.Status = PursuerStatus.Traveling;
            s.LastRoom = null;
            Log(log, "Player died" + (killedByPursuer ? " to the Pursuer" : "") + "; the Pursuer retreats to " + s.Region + "/" + s.Shelter + ", " + Describe(HopsAway(graph, s, playerShelter)) + " away.");
            return s;
        }

        /// <summary>Shelter hops between the Pursuer and the player's shelter, or -1 if unknown.</summary>
        public static int HopsAway(ShelterGraph graph, PursuerState state, string playerShelter)
        {
            ShelterNode from = graph.Get(state.Shelter);
            ShelterNode to = graph.NearestShelter(playerShelter);
            if (from == null || to == null)
            {
                return -1;
            }
            return graph.HopDistance(from, to);
        }

        public static string Describe(int hops)
        {
            if (hops < 0)
            {
                return "an unknown distance";
            }
            if (hops == 1)
            {
                return "1 shelter";
            }
            return hops + " shelters";
        }

        private static Random Rng(int seed, int cycle, int salt)
        {
            unchecked
            {
                return new Random(seed * 31 + cycle * 7919 + salt * 104729);
            }
        }

        private static ShelterNode PickSpawnNode(ShelterGraph graph, ShelterNode playerNode, int minHops, Random rng)
        {
            if (graph.Nodes.Count == 0)
            {
                return null;
            }
            if (playerNode == null)
            {
                return graph.Nodes[rng.Next(graph.Nodes.Count)];
            }
            Dictionary<ShelterNode, PathInfo> reach = graph.Dijkstra(playerNode);
            var candidates = new List<ShelterNode>();
            ShelterNode farthest = null;
            int farthestHops = -1;
            foreach (KeyValuePair<ShelterNode, PathInfo> pair in reach)
            {
                if (pair.Value.Hops >= minHops)
                {
                    candidates.Add(pair.Key);
                }
                if (pair.Value.Hops > farthestHops)
                {
                    farthestHops = pair.Value.Hops;
                    farthest = pair.Key;
                }
            }
            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return candidates[rng.Next(candidates.Count)];
            }
            return farthest ?? playerNode;
        }

        private static ShelterNode PickRetreatNode(ShelterGraph graph, ShelterNode playerNode, ShelterNode current, int minHops)
        {
            if (playerNode == null)
            {
                return current;
            }
            Dictionary<ShelterNode, PathInfo> fromPlayer = graph.Dijkstra(playerNode);
            Dictionary<ShelterNode, PathInfo> fromCurrent = current != null ? graph.Dijkstra(current) : new Dictionary<ShelterNode, PathInfo>();
            ShelterNode best = null;
            int bestCost = int.MaxValue;
            ShelterNode farthest = null;
            int farthestHops = -1;
            foreach (KeyValuePair<ShelterNode, PathInfo> pair in fromPlayer)
            {
                if (pair.Value.Hops > farthestHops)
                {
                    farthestHops = pair.Value.Hops;
                    farthest = pair.Key;
                }
                if (pair.Value.Hops < minHops)
                {
                    continue;
                }
                int cost = fromCurrent.TryGetValue(pair.Key, out PathInfo info) ? info.Rooms : int.MaxValue / 2;
                if (cost < bestCost || (cost == bestCost && best != null && string.CompareOrdinal(pair.Key.Name, best.Name) < 0))
                {
                    bestCost = cost;
                    best = pair.Key;
                }
            }
            return best ?? farthest ?? current;
        }

        private static void RollOffscreenUpgrade(PursuerState s, string region, Random rng, TrackerConfig cfg, List<string> log)
        {
            if (!cfg.OffscreenUpgrades)
            {
                return;
            }
            bool rich = region != null && cfg.ScavengerRichRegions.Contains(region);
            int best = GearTier.BestTier(s.Inventory);
            string found = null;
            if (best < 1)
            {
                found = Roll(rng, rich ? 0.35 : 0.2) ? GearTier.Rock : null;
            }
            else if (best < 2)
            {
                found = Roll(rng, rich ? 0.3 : 0.15) ? GearTier.Spear : null;
            }
            else if (best < 3)
            {
                found = Roll(rng, rich ? 0.12 : 0.05) ? GearTier.ExplosiveSpear : null;
            }
            else if (!s.Inventory.Contains(GearTier.ScavengerBomb))
            {
                found = Roll(rng, rich ? 0.1 : 0.03) ? GearTier.ScavengerBomb : null;
            }
            if (found == null)
            {
                return;
            }
            string dropped = GearTier.Add(s.Inventory, found);
            if (dropped != found)
            {
                Log(log, "Offscreen: the Pursuer picked up a " + found + " in " + region + (dropped != null ? " (left its " + dropped + " behind)" : "") + ".");
            }
        }

        private static bool Roll(Random rng, double chance)
        {
            return rng.NextDouble() < chance;
        }

        private static void Log(List<string> log, string message)
        {
            log?.Add(message);
        }
    }
}
