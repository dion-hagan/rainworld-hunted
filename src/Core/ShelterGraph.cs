using System;
using System.Collections.Generic;

namespace Hunted.Core
{
    public sealed class ShelterNode
    {
        public string Name;
        public string Region;
        /// <summary>The non-shelter room the shelter opens onto (first connection), or null.</summary>
        public string EntranceRoom;
        public List<ShelterEdge> Edges = new List<ShelterEdge>();

        public override string ToString() => Name;
    }

    public sealed class ShelterEdge
    {
        public ShelterNode To;
        /// <summary>Room-to-room steps between the two shelters (always at least 1).</summary>
        public int Rooms;
    }

    public sealed class PathInfo
    {
        public int Rooms;
        public int Hops;
        public ShelterNode Prev;
    }

    /// <summary>
    /// The global shelter graph: every shelter of every region the campaign can
    /// reach, connected where a room path exists between two shelters that
    /// passes through no other shelter. Gate rooms belong to both of their
    /// regions, so region borders are just ordinary edges.
    /// </summary>
    public sealed class ShelterGraph
    {
        private readonly Dictionary<string, ShelterNode> nodes = new Dictionary<string, ShelterNode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> roomNeighbors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> roomRegion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> nearestShelter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ShelterNode> nodeList = new List<ShelterNode>();

        public IReadOnlyList<ShelterNode> Nodes => nodeList;
        public int RoomCount => roomNeighbors.Count;

        internal void AddRoom(string room, string region, string[] neighbors)
        {
            roomNeighbors[room] = neighbors;
            roomRegion[room] = region;
        }

        internal ShelterNode AddNode(string name, string region, string entrance)
        {
            var node = new ShelterNode { Name = name, Region = region, EntranceRoom = entrance };
            nodes[name] = node;
            nodeList.Add(node);
            return node;
        }

        internal void SetNearestShelter(string room, string shelter)
        {
            nearestShelter[room] = shelter;
        }

        public ShelterNode Get(string name)
        {
            if (name == null)
            {
                return null;
            }
            return nodes.TryGetValue(name, out ShelterNode node) ? node : null;
        }

        public bool HasRoom(string room)
        {
            return room != null && roomNeighbors.ContainsKey(room);
        }

        public string RegionOfRoom(string room)
        {
            return room != null && roomRegion.TryGetValue(room, out string region) ? region : null;
        }

        public IReadOnlyList<string> RoomNeighbors(string room)
        {
            return room != null && roomNeighbors.TryGetValue(room, out string[] n) ? n : Array.Empty<string>();
        }

        /// <summary>The shelter closest to a room by room steps (the room itself if it is a shelter), or null.</summary>
        public ShelterNode NearestShelter(string room)
        {
            if (room == null)
            {
                return null;
            }
            ShelterNode direct = Get(room);
            if (direct != null)
            {
                return direct;
            }
            return nearestShelter.TryGetValue(room, out string name) ? Get(name) : null;
        }

        /// <summary>Room steps between two rooms through the room graph, or -1.</summary>
        public int RoomDistance(string from, string to)
        {
            if (!HasRoom(from) || !HasRoom(to))
            {
                return -1;
            }
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [from] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                int d = dist[cur];
                foreach (string next in RoomNeighbors(cur))
                {
                    if (dist.ContainsKey(next))
                    {
                        continue;
                    }
                    if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase))
                    {
                        return d + 1;
                    }
                    dist[next] = d + 1;
                    queue.Enqueue(next);
                }
            }
            return -1;
        }

        /// <summary>
        /// Shortest paths (by rooms traversed, then by hops) from one shelter to
        /// every reachable shelter.
        /// </summary>
        public Dictionary<ShelterNode, PathInfo> Dijkstra(ShelterNode from)
        {
            var result = new Dictionary<ShelterNode, PathInfo>();
            if (from == null)
            {
                return result;
            }
            var open = new List<ShelterNode>();
            result[from] = new PathInfo { Rooms = 0, Hops = 0, Prev = null };
            open.Add(from);
            var closed = new HashSet<ShelterNode>();
            while (open.Count > 0)
            {
                int bestIdx = 0;
                for (int i = 1; i < open.Count; i++)
                {
                    PathInfo a = result[open[i]];
                    PathInfo b = result[open[bestIdx]];
                    if (a.Rooms < b.Rooms || (a.Rooms == b.Rooms && a.Hops < b.Hops))
                    {
                        bestIdx = i;
                    }
                }
                ShelterNode cur = open[bestIdx];
                open.RemoveAt(bestIdx);
                if (!closed.Add(cur))
                {
                    continue;
                }
                PathInfo curInfo = result[cur];
                foreach (ShelterEdge edge in cur.Edges)
                {
                    if (closed.Contains(edge.To))
                    {
                        continue;
                    }
                    int rooms = curInfo.Rooms + edge.Rooms;
                    int hops = curInfo.Hops + 1;
                    if (!result.TryGetValue(edge.To, out PathInfo known) || rooms < known.Rooms || (rooms == known.Rooms && hops < known.Hops))
                    {
                        result[edge.To] = new PathInfo { Rooms = rooms, Hops = hops, Prev = cur };
                        open.Add(edge.To);
                    }
                }
            }
            return result;
        }

        /// <summary>Shelters along the shortest path, including both ends; null when unreachable.</summary>
        public List<ShelterNode> ShortestPath(ShelterNode from, ShelterNode to)
        {
            if (from == null || to == null)
            {
                return null;
            }
            if (from == to)
            {
                return new List<ShelterNode> { from };
            }
            Dictionary<ShelterNode, PathInfo> all = Dijkstra(from);
            if (!all.ContainsKey(to))
            {
                return null;
            }
            var path = new List<ShelterNode>();
            ShelterNode cur = to;
            while (cur != null)
            {
                path.Add(cur);
                cur = all[cur].Prev;
            }
            path.Reverse();
            return path;
        }

        /// <summary>Shelter hops along the shortest path, or -1 when unreachable.</summary>
        public int HopDistance(ShelterNode from, ShelterNode to)
        {
            List<ShelterNode> path = ShortestPath(from, to);
            return path == null ? -1 : path.Count - 1;
        }
    }
}
