using System.Collections.Generic;

namespace Hunted.Game
{
    /// <summary>Small helpers over the loaded world's abstract rooms.</summary>
    internal static class WorldRooms
    {
        public static string RegionName(World world)
        {
            if (world == null)
            {
                return null;
            }
            return world.region != null ? world.region.name : world.name;
        }

        public static bool IsRegion(World world, string acronym)
        {
            string name = RegionName(world);
            return name != null && acronym != null && string.Equals(name, acronym, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>BFS room distances from a room over abstract room connections.</summary>
        public static Dictionary<int, int> Distances(World world, int startRoom, int maxDepth = int.MaxValue)
        {
            var dist = new Dictionary<int, int>();
            if (world == null || world.GetAbstractRoom(startRoom) == null)
            {
                return dist;
            }
            dist[startRoom] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(startRoom);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int d = dist[cur];
                if (d >= maxDepth)
                {
                    continue;
                }
                AbstractRoom room = world.GetAbstractRoom(cur);
                if (room == null)
                {
                    continue;
                }
                foreach (int next in room.connections)
                {
                    if (next < 0 || dist.ContainsKey(next) || world.GetAbstractRoom(next) == null)
                    {
                        continue;
                    }
                    dist[next] = d + 1;
                    queue.Enqueue(next);
                }
            }
            return dist;
        }

        public static int Distance(World world, int from, int to)
        {
            Dictionary<int, int> d = Distances(world, from);
            return d.TryGetValue(to, out int v) ? v : -1;
        }

        public static bool IsOrdinaryRoom(AbstractRoom room)
        {
            return room != null && !room.shelter && !room.gate && !room.offScreenDen;
        }

        /// <summary>
        /// A room for the Pursuer to appear in: as close as possible to
        /// <paramref name="preferred"/> while at least <paramref name="minFromPlayer"/>
        /// rooms away from the player, and never a shelter, gate or the offscreen den.
        /// </summary>
        public static AbstractRoom PickSpawnRoom(World world, AbstractRoom preferred, int playerRoom, int minFromPlayer, bool mustBeUnrealized)
        {
            Dictionary<int, int> fromPlayer = Distances(world, playerRoom);
            bool Ok(AbstractRoom r)
            {
                if (!IsOrdinaryRoom(r) || (mustBeUnrealized && r.realizedRoom != null))
                {
                    return false;
                }
                return !fromPlayer.TryGetValue(r.index, out int d) || d >= minFromPlayer;
            }
            if (preferred != null)
            {
                if (Ok(preferred))
                {
                    return preferred;
                }
                Dictionary<int, int> fromPreferred = Distances(world, preferred.index);
                AbstractRoom best = null;
                int bestD = int.MaxValue;
                foreach (KeyValuePair<int, int> pair in fromPreferred)
                {
                    AbstractRoom r = world.GetAbstractRoom(pair.Key);
                    if (pair.Value < bestD && Ok(r))
                    {
                        best = r;
                        bestD = pair.Value;
                    }
                }
                if (best != null)
                {
                    return best;
                }
            }
            // Nothing near the preferred room: take the closest acceptable room to the player instead.
            AbstractRoom fallback = null;
            int fallbackD = int.MaxValue;
            foreach (KeyValuePair<int, int> pair in fromPlayer)
            {
                AbstractRoom r = world.GetAbstractRoom(pair.Key);
                if (pair.Value >= minFromPlayer && pair.Value < fallbackD && Ok(r))
                {
                    fallback = r;
                    fallbackD = pair.Value;
                }
            }
            return fallback;
        }

        /// <summary>A node in the room the creature can use, preferring exits. -1 if none.</summary>
        public static int PickNode(AbstractRoom room, CreatureTemplate template, bool preferExit = true)
        {
            if (room == null || room.nodes == null)
            {
                return -1;
            }
            int any = -1;
            for (int i = 0; i < room.nodes.Length; i++)
            {
                AbstractRoomNode node = room.nodes[i];
                if (node.type.Index == -1 || template.mappedNodeTypes == null || node.type.Index >= template.mappedNodeTypes.Length || !template.mappedNodeTypes[node.type.Index])
                {
                    continue;
                }
                if (preferExit && node.type == AbstractRoomNode.Type.Exit && i < room.connections.Length && room.connections[i] > -1)
                {
                    return i;
                }
                if (any == -1)
                {
                    any = i;
                }
            }
            return any;
        }

        /// <summary>The exit node of <paramref name="room"/> that leads toward <paramref name="towardRoom"/>, or any usable node.</summary>
        public static int PickNodeToward(AbstractRoom room, int towardRoom, CreatureTemplate template)
        {
            if (room != null)
            {
                int exit = room.ExitIndex(towardRoom);
                if (exit > -1 && exit < room.nodes.Length && room.nodes[exit].type.Index != -1 && template.mappedNodeTypes[room.nodes[exit].type.Index])
                {
                    return exit;
                }
            }
            return PickNode(room, template);
        }
    }
}
