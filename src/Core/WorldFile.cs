using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hunted.Core
{
    /// <summary>
    /// One room as described by a region's world_xx.txt, after the current
    /// timeline's conditional links have been applied.
    /// </summary>
    public sealed class WorldRoom
    {
        public string Name;
        public List<string> Connections = new List<string>();
        public List<string> Tags = new List<string>();

        public bool IsShelter => Tags.Contains("SHELTER") || Tags.Contains("ANCIENTSHELTER");
        public bool IsGate => Tags.Contains("GATE");

        public override string ToString() => Name;
    }

    public sealed class ParsedRegion
    {
        public string Acronym;
        public List<WorldRoom> Rooms = new List<WorldRoom>();
    }

    /// <summary>
    /// Reads the ROOMS and CONDITIONAL LINKS sections of a world_xx.txt the
    /// same way the game's WorldLoader does, for one timeline (slugcat):
    ///
    ///  - "//" comment lines are skipped
    ///  - "(A,B)" prefixes keep the line only for those timelines, "(X-A)" excludes them
    ///  - "{cond}" prefixes are evaluated by the optional callback (default: kept)
    ///  - EXCLUSIVEROOM / HIDEROOM disable rooms, REPLACEROOM renames them, and
    ///    "Timeline : ROOM : TARGET : REPLACEMENT" rewires one connection
    ///    (TARGET may be the n-th DISCONNECTED slot when it is a number)
    /// </summary>
    public static class WorldFileParser
    {
        private sealed class RawRoom
        {
            public string Name;
            public List<string> Connections = new List<string>(); // still holds DISCONNECTED placeholders
            public List<string> Tags = new List<string>();
        }

        private sealed class Link
        {
            public string Room;
            public string Target;      // null when Index is used
            public int Index;          // 1-based count of DISCONNECTED slots
            public string Replacement;
        }

        public static ParsedRegion Parse(string acronym, IEnumerable<string> lines, string timeline, Func<string, bool> customCondition = null)
        {
            var rooms = new List<RawRoom>();
            var exclusive = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var links = new List<Link>();

            string section = null;
            foreach (string raw in lines)
            {
                string line = PreprocessLine(raw, timeline, customCondition);
                if (line == null)
                {
                    continue;
                }
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                if (section == null)
                {
                    if (trimmed == "ROOMS")
                    {
                        section = "ROOMS";
                    }
                    else if (trimmed == "CONDITIONAL LINKS")
                    {
                        section = "LINKS";
                    }
                    else if (!trimmed.StartsWith("END ", StringComparison.Ordinal))
                    {
                        section = "OTHER";
                    }
                    continue;
                }
                if (trimmed.StartsWith("END ", StringComparison.Ordinal))
                {
                    section = null;
                    continue;
                }
                if (section == "ROOMS")
                {
                    RawRoom room = ParseRoomLine(trimmed);
                    if (room != null)
                    {
                        rooms.Add(room);
                    }
                }
                else if (section == "LINKS")
                {
                    ParseLinkLine(trimmed, timeline, exclusive, hidden, renames, links);
                }
            }

            var disabled = new HashSet<string>(hidden, StringComparer.Ordinal);
            foreach (KeyValuePair<string, HashSet<string>> pair in exclusive)
            {
                if (!pair.Value.Contains(timeline))
                {
                    disabled.Add(pair.Key);
                }
            }

            ApplyLinks(rooms, links);
            ApplyRenames(rooms, renames);

            var region = new ParsedRegion { Acronym = acronym };
            foreach (RawRoom raw in rooms)
            {
                if (disabled.Contains(raw.Name))
                {
                    continue;
                }
                var room = new WorldRoom { Name = raw.Name };
                room.Tags.AddRange(raw.Tags);
                foreach (string conn in raw.Connections)
                {
                    if (conn == "DISCONNECTED" || conn.Length == 0 || disabled.Contains(conn) || room.Connections.Contains(conn))
                    {
                        continue;
                    }
                    room.Connections.Add(conn);
                }
                region.Rooms.Add(room);
            }
            return region;
        }

        /// <summary>Mirrors WorldLoader.Preprocessing.PreprocessLine. Returns null when the line is dropped.</summary>
        public static string PreprocessLine(string line, string timeline, Func<string, bool> customCondition)
        {
            if (line == null || line.Length < 2 || line.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }
            if (line[0] == '(' && line.Contains(")"))
            {
                int close = line.IndexOf(')');
                if (!TimelineMatch(line.Substring(1, close - 1), timeline))
                {
                    return null;
                }
                line = line.Substring(close + 1);
            }
            if (line.Length > 0 && line[0] == '{' && line.Contains("}"))
            {
                int close = line.IndexOf('}');
                string cond = line.Substring(1, close - 1);
                if (customCondition != null && !customCondition(cond))
                {
                    return null;
                }
                line = line.Substring(close + 1);
            }
            return line;
        }

        /// <summary>Mirrors WorldLoader.Preprocessing.TimelineMatch ("A,B" or "X-A,B").</summary>
        public static bool TimelineMatch(string text, string timeline)
        {
            bool negate = false;
            if (text.StartsWith("X-", StringComparison.Ordinal))
            {
                text = text.Substring(2);
                negate = true;
            }
            if (timeline == null)
            {
                return negate;
            }
            bool match = false;
            foreach (string token in text.Split(','))
            {
                string t = token.Trim();
                if (int.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out int number))
                {
                    if (LegacyTimelineForNumber(number) == timeline)
                    {
                        match = true;
                        break;
                    }
                }
                else if (t == timeline)
                {
                    match = true;
                    break;
                }
            }
            return match != negate;
        }

        private static string LegacyTimelineForNumber(int number)
        {
            switch (number)
            {
                case 0: return "White";
                case 1: return "Yellow";
                case 2: return "Red";
                case 3: return "Gourmand";
                case 4: return "Artificer";
                case 5: return "Rivulet";
                case 6: return "Spear";
                case 7: return "Saint";
                case 8: return "Inv";
                default: return null;
            }
        }

        private static string[] SplitParts(string line)
        {
            string[] parts = line.Split(':');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }
            return parts;
        }

        private static RawRoom ParseRoomLine(string line)
        {
            if (!line.Contains(":"))
            {
                return null;
            }
            string[] parts = SplitParts(line);
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                return null;
            }
            var room = new RawRoom { Name = parts[0] };
            if (parts[1].Length > 0)
            {
                foreach (string conn in parts[1].Split(','))
                {
                    room.Connections.Add(conn.Trim());
                }
            }
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    room.Tags.Add(parts[i]);
                }
            }
            return room;
        }

        private static void ParseLinkLine(string line, string timeline, Dictionary<string, HashSet<string>> exclusive, HashSet<string> hidden, Dictionary<string, string> renames, List<Link> links)
        {
            string[] parts = SplitParts(line);
            if (parts.Length < 3)
            {
                return;
            }
            var timelines = new List<string>();
            foreach (string t in parts[0].Split(','))
            {
                timelines.Add(t.Trim());
            }
            bool applies = timelines.Contains(timeline);

            if (parts[1] == "EXCLUSIVEROOM")
            {
                if (!exclusive.TryGetValue(parts[2], out HashSet<string> set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    exclusive[parts[2]] = set;
                }
                foreach (string t in timelines)
                {
                    set.Add(t);
                }
                return;
            }
            if (parts[1] == "HIDEROOM")
            {
                if (applies)
                {
                    hidden.Add(parts[2]);
                }
                return;
            }
            if (parts[1] == "REPLACEROOM")
            {
                if (applies && parts.Length >= 4)
                {
                    renames[parts[2]] = parts[3];
                }
                return;
            }
            if (parts.Length < 4 || !applies)
            {
                return;
            }
            var link = new Link { Room = parts[1], Replacement = parts[3] };
            if (int.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out int index))
            {
                link.Index = index;
            }
            else
            {
                link.Target = parts[2];
            }
            links.Add(link);
        }

        private static void ApplyLinks(List<RawRoom> rooms, List<Link> links)
        {
            foreach (RawRoom room in rooms)
            {
                var deferred = new List<KeyValuePair<int, string>>();
                foreach (Link link in links)
                {
                    if (link.Room != room.Name)
                    {
                        continue;
                    }
                    if (link.Target != null)
                    {
                        int at = room.Connections.IndexOf(link.Target);
                        if (at >= 0)
                        {
                            room.Connections[at] = link.Replacement;
                        }
                        continue;
                    }
                    int seen = 0;
                    for (int i = 0; i < room.Connections.Count; i++)
                    {
                        if (room.Connections[i] != "DISCONNECTED")
                        {
                            continue;
                        }
                        seen++;
                        if (seen == link.Index)
                        {
                            deferred.Add(new KeyValuePair<int, string>(i, link.Replacement));
                            break;
                        }
                    }
                }
                foreach (KeyValuePair<int, string> change in deferred)
                {
                    room.Connections[change.Key] = change.Value;
                }
            }
        }

        private static void ApplyRenames(List<RawRoom> rooms, Dictionary<string, string> renames)
        {
            if (renames.Count == 0)
            {
                return;
            }
            foreach (RawRoom room in rooms)
            {
                if (renames.TryGetValue(room.Name, out string newName))
                {
                    room.Name = newName;
                }
                for (int i = 0; i < room.Connections.Count; i++)
                {
                    if (renames.TryGetValue(room.Connections[i], out string renamed))
                    {
                        room.Connections[i] = renamed;
                    }
                }
            }
        }
    }
}
