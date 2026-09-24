using System;
using System.Collections.Generic;

namespace Hunted.Core
{
    public static class ShelterGraphBuilder
    {
        private sealed class MergedRoom
        {
            public string Name;
            public string Region;
            public bool IsShelter;
            public bool IsGate;
            public List<string> Connections = new List<string>();
        }

        /// <summary>
        /// Merges the parsed regions into one room graph (gate rooms appear in
        /// two region files and are unified by name), then derives the shelter
        /// graph from it.
        /// </summary>
        public static ShelterGraph Build(IEnumerable<ParsedRegion> regions)
        {
            var rooms = new Dictionary<string, MergedRoom>(StringComparer.OrdinalIgnoreCase);
            foreach (ParsedRegion region in regions)
            {
                foreach (WorldRoom room in region.Rooms)
                {
                    if (!rooms.TryGetValue(room.Name, out MergedRoom merged))
                    {
                        merged = new MergedRoom { Name = room.Name, Region = region.Acronym };
                        rooms[room.Name] = merged;
                    }
                    merged.IsShelter |= room.IsShelter;
                    merged.IsGate |= room.IsGate;
                    foreach (string conn in room.Connections)
                    {
                        if (!merged.Connections.Contains(conn))
                        {
                            merged.Connections.Add(conn);
                        }
                    }
                }
            }

            // Connections must point at known rooms and be symmetric.
            foreach (MergedRoom room in rooms.Values)
            {
                room.Connections.RemoveAll(c => !rooms.ContainsKey(c));
            }
            foreach (MergedRoom room in rooms.Values)
            {
                foreach (string conn in room.Connections)
                {
                    MergedRoom other = rooms[conn];
                    if (!other.Connections.Contains(room.Name))
                    {
                        other.Connections.Add(room.Name);
                    }
                }
            }

            var graph = new ShelterGraph();
            foreach (MergedRoom room in rooms.Values)
            {
                graph.AddRoom(room.Name, room.Region, room.Connections.ToArray());
            }

            var shelterRooms = new List<MergedRoom>();
            foreach (MergedRoom room in rooms.Values)
            {
                if (room.IsShelter)
                {
                    shelterRooms.Add(room);
                }
            }
            shelterRooms.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            var nodeByName = new Dictionary<string, ShelterNode>(StringComparer.OrdinalIgnoreCase);
            foreach (MergedRoom room in shelterRooms)
            {
                string entrance = null;
                foreach (string conn in room.Connections)
                {
                    if (!rooms[conn].IsShelter)
                    {
                        entrance = conn;
                        break;
                    }
                }
                nodeByName[room.Name] = graph.AddNode(room.Name, room.Region, entrance ?? (room.Connections.Count > 0 ? room.Connections[0] : null));
            }

            // Edges: shelters are dead-end rooms, so "a path that passes through no
            // other shelter" would connect every shelter to every other one. Instead
            // two shelters are neighbours when no third shelter sits between them
            // (relative-neighbourhood graph over room distances). That graph still
            // contains the minimum spanning tree, so every reachable shelter stays
            // reachable, hop by hop through nearby shelters.
            int n = shelterRooms.Count;
            var dist = new int[n][];
            var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                indexOf[shelterRooms[i].Name] = i;
            }
            for (int i = 0; i < n; i++)
            {
                dist[i] = new int[n];
                for (int j = 0; j < n; j++)
                {
                    dist[i][j] = -1;
                }
                dist[i][i] = 0;
                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [shelterRooms[i].Name] = 0 };
                var queue = new Queue<string>();
                queue.Enqueue(shelterRooms[i].Name);
                while (queue.Count > 0)
                {
                    string cur = queue.Dequeue();
                    int d = seen[cur];
                    foreach (string next in rooms[cur].Connections)
                    {
                        if (seen.ContainsKey(next))
                        {
                            continue;
                        }
                        seen[next] = d + 1;
                        if (indexOf.TryGetValue(next, out int j))
                        {
                            dist[i][j] = d + 1;
                        }
                        queue.Enqueue(next);
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                ShelterNode node = nodeByName[shelterRooms[i].Name];
                for (int j = 0; j < n; j++)
                {
                    int d = dist[i][j];
                    if (j == i || d <= 0)
                    {
                        continue;
                    }
                    bool dominated = false;
                    for (int k = 0; k < n && !dominated; k++)
                    {
                        if (k == i || k == j)
                        {
                            continue;
                        }
                        int dik = dist[i][k];
                        int dkj = dist[k][j];
                        dominated = dik > 0 && dkj > 0 && dik < d && dkj < d;
                    }
                    if (!dominated)
                    {
                        node.Edges.Add(new ShelterEdge { To = nodeByName[shelterRooms[j].Name], Rooms = d });
                    }
                }
            }

            // Nearest shelter for every room: multi-source BFS.
            var nearest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new Queue<string>();
            foreach (MergedRoom shelter in shelterRooms)
            {
                nearest[shelter.Name] = shelter.Name;
                frontier.Enqueue(shelter.Name);
            }
            while (frontier.Count > 0)
            {
                string cur = frontier.Dequeue();
                foreach (string next in rooms[cur].Connections)
                {
                    if (nearest.ContainsKey(next))
                    {
                        continue;
                    }
                    nearest[next] = nearest[cur];
                    frontier.Enqueue(next);
                }
            }
            foreach (KeyValuePair<string, string> pair in nearest)
            {
                graph.SetNearestShelter(pair.Key, pair.Value);
            }

            return graph;
        }
    }
}
