using System;
using System.Collections.Generic;

namespace Hunted.Core.Arena
{
    /// <summary>A horizontal stretch a fighter can stand on. The floor is one; platforms and crate tops are others.</summary>
    public sealed class Surface
    {
        public float X0;
        public float X1;
        public float Y;
        /// <summary>Thin platforms can be jumped through from below; the floor and crate tops cannot.</summary>
        public bool OneWay;

        public Surface(float x0, float x1, float y, bool oneWay)
        {
            X0 = x0;
            X1 = x1;
            Y = y;
            OneWay = oneWay;
        }

        public bool Spans(float x) => x >= X0 && x <= X1;

        public override string ToString() => "surface " + X0.ToString("0") + ".." + X1.ToString("0") + " at " + Y.ToString("0");
    }

    /// <summary>A vertical pole from a lower height to an upper one; the upper end touches a surface.</summary>
    public sealed class Pole
    {
        public float X;
        public float Y0;
        public float Y1;

        public Pole(float x, float y0, float y1)
        {
            X = x;
            Y0 = y0;
            Y1 = y1;
        }
    }

    /// <summary>A solid block standing on a surface: blocks sight and weapons, and has to be jumped over.</summary>
    public sealed class Crate
    {
        public float X0;
        public float X1;
        public float Y0;
        public float Y1;

        public Crate(float x0, float x1, float y0, float y1)
        {
            X0 = x0;
            X1 = x1;
            Y0 = y0;
            Y1 = y1;
        }
    }

    /// <summary>
    /// The geometry of one arena room: a floor, thin platforms reached by poles or jumps,
    /// and a few crates that break the line of sight. Sizes are pixels (20 per tile), y up.
    /// Rain World rooms are far richer; this keeps the parts that matter to a throwing
    /// duel: height differences, cover, and distance.
    /// </summary>
    public sealed class ArenaRoom
    {
        public readonly float Width;
        public readonly float Height;
        public readonly List<Surface> Surfaces = new List<Surface>();
        public readonly List<Pole> Poles = new List<Pole>();
        public readonly List<Crate> Crates = new List<Crate>();

        public Surface Floor => Surfaces[0];

        public ArenaRoom(float width, float height)
        {
            Width = width;
            Height = height;
            Surfaces.Add(new Surface(0f, width, 0f, false));
        }

        public void AddPlatform(float x0, float x1, float y, float poleX)
        {
            Surfaces.Add(new Surface(x0, x1, y, true));
            if (poleX >= x0 && poleX <= x1)
            {
                Poles.Add(new Pole(poleX, 0f, y));
            }
        }

        public void AddCrate(float x0, float x1, float height)
        {
            Crates.Add(new Crate(x0, x1, 0f, height));
            Surfaces.Add(new Surface(x0, x1, height, false));
        }

        /// <summary>A fixed layout for tests: two low platforms, one high one, two crates on the floor.</summary>
        public static ArenaRoom Default()
        {
            var room = new ArenaRoom(1200f, 500f);
            room.AddPlatform(180f, 480f, 140f, 220f);
            room.AddPlatform(720f, 1020f, 140f, 980f);
            room.AddPlatform(450f, 750f, 260f, 600f);
            room.AddCrate(340f, 380f, 60f);
            room.AddCrate(820f, 860f, 60f);
            return room;
        }

        /// <summary>A layout drawn from <paramref name="rng"/>: one to four platforms, each with a pole, and up to three crates.</summary>
        public static ArenaRoom Generate(Random rng)
        {
            float width = 900f + rng.Next(0, 5) * 100f;
            var room = new ArenaRoom(width, 500f);
            int platforms = rng.Next(1, 5);
            for (int i = 0; i < platforms; i++)
            {
                float span = 200f + rng.Next(0, 4) * 60f;
                float x0 = 60f + (float)rng.NextDouble() * (width - span - 120f);
                float y = 120f + rng.Next(0, 3) * 100f;
                bool overlaps = false;
                foreach (Surface s in room.Surfaces)
                {
                    if (s.OneWay && Math.Abs(s.Y - y) < 60f && x0 < s.X1 + 80f && x0 + span > s.X0 - 80f)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                {
                    continue;
                }
                float poleX = x0 + 20f + (float)rng.NextDouble() * (span - 40f);
                room.AddPlatform(x0, x0 + span, y, poleX);
            }
            int crates = rng.Next(0, 4);
            for (int i = 0; i < crates; i++)
            {
                float x0 = 100f + (float)rng.NextDouble() * (width - 240f);
                float x1 = x0 + 40f;
                bool blocked = false;
                foreach (Pole p in room.Poles)
                {
                    if (p.X > x0 - 30f && p.X < x1 + 30f)
                    {
                        blocked = true;
                    }
                }
                foreach (Crate c in room.Crates)
                {
                    if (x0 < c.X1 + 80f && x1 > c.X0 - 80f)
                    {
                        blocked = true;
                    }
                }
                if (!blocked)
                {
                    room.AddCrate(x0, x1, 60f);
                }
            }
            return room;
        }

        /// <summary>The surface a body standing at (x, y) is on, if any: same height within a tolerance and inside the span.</summary>
        public Surface SurfaceAt(float x, float y, float tolerance = 4f)
        {
            Surface best = null;
            foreach (Surface s in Surfaces)
            {
                if (s.Spans(x) && Math.Abs(s.Y - y) <= tolerance && (best == null || Math.Abs(s.Y - y) < Math.Abs(best.Y - y)))
                {
                    best = s;
                }
            }
            return best;
        }

        /// <summary>The highest surface at or below (x, y): where something dropped there comes to rest.</summary>
        public Surface SurfaceBelow(float x, float y)
        {
            Surface best = Floor;
            foreach (Surface s in Surfaces)
            {
                if (s.Spans(x) && s.Y <= y + 0.01f && s.Y > best.Y)
                {
                    best = s;
                }
            }
            return best;
        }

        /// <summary>The crate a walker at foot height <paramref name="y"/> would bump into moving from <paramref name="x"/> by <paramref name="dx"/>.</summary>
        public Crate CrateAhead(float x, float y, float dx)
        {
            foreach (Crate c in Crates)
            {
                if (c.Y1 <= y + 1f || c.Y0 > y + 1f)
                {
                    continue; // standing on top of it, or it is above us
                }
                if (dx > 0f && x <= c.X0 && x + dx >= c.X0)
                {
                    return c;
                }
                if (dx < 0f && x >= c.X1 && x + dx <= c.X1)
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>The pole nearest to <paramref name="x"/> that starts at or below <paramref name="fromY"/> and reaches <paramref name="toY"/>.</summary>
        public Pole PoleTo(float x, float fromY, float toY)
        {
            Pole best = null;
            foreach (Pole p in Poles)
            {
                if (p.Y0 <= fromY + 4f && p.Y1 >= toY - 4f && (best == null || Math.Abs(p.X - x) < Math.Abs(best.X - x)))
                {
                    best = p;
                }
            }
            return best;
        }

        /// <summary>True when nothing solid lies on the segment: no crate and no platform between the two points.</summary>
        public bool LineOfSight(Vec2 a, Vec2 b)
        {
            foreach (Crate c in Crates)
            {
                if (BoxEntry(a, b, c.X0, c.Y0, c.X1, c.Y1, out _))
                {
                    return false;
                }
            }
            foreach (Surface s in Surfaces)
            {
                if (s.OneWay && SegmentCrossesHorizontal(a, b, s.X0, s.X1, s.Y))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Where a thrown weapon moving from <paramref name="a"/> to <paramref name="b"/> first meets
        /// something solid, or null. <paramref name="landed"/> is true when that something is a
        /// surface it came down on rather than a wall or crate it flew into.
        /// </summary>
        public Vec2? FirstSolidHit(Vec2 a, Vec2 b, out bool landed)
        {
            landed = false;
            Vec2? best = null;
            float bestT = float.MaxValue;
            if (b.X <= 0f || b.X >= Width)
            {
                float t = b.X <= 0f ? (0f - a.X) / (b.X - a.X) : (Width - a.X) / (b.X - a.X);
                if (t >= 0f && t < bestT)
                {
                    bestT = t;
                    best = a + (b - a) * t;
                }
            }
            if (b.Y >= Height)
            {
                float t = (Height - a.Y) / (b.Y - a.Y);
                if (t >= 0f && t < bestT)
                {
                    bestT = t;
                    best = a + (b - a) * t;
                }
            }
            foreach (Crate c in Crates)
            {
                if (BoxEntry(a, b, c.X0, c.Y0, c.X1, c.Y1, out float t) && t < bestT)
                {
                    bestT = t;
                    best = a + (b - a) * t;
                }
            }
            if (b.Y < a.Y)
            {
                foreach (Surface s in Surfaces)
                {
                    if (a.Y >= s.Y && b.Y <= s.Y)
                    {
                        float t = (a.Y - s.Y) / (a.Y - b.Y);
                        float x = a.X + (b.X - a.X) * t;
                        if (s.Spans(x) && t < bestT)
                        {
                            bestT = t;
                            best = new Vec2(x, s.Y);
                            landed = true;
                        }
                    }
                }
            }
            return best;
        }

        private static bool SegmentCrossesHorizontal(Vec2 a, Vec2 b, float x0, float x1, float y)
        {
            if ((a.Y - y) * (b.Y - y) > 0f || Math.Abs(a.Y - b.Y) < 0.0001f)
            {
                return false;
            }
            float t = (a.Y - y) / (a.Y - b.Y);
            float x = a.X + (b.X - a.X) * t;
            return x >= x0 && x <= x1;
        }

        /// <summary>Liang-Barsky clip: whether the segment a-b enters the box, and the parameter where it does.</summary>
        private static bool BoxEntry(Vec2 a, Vec2 b, float x0, float y0, float x1, float y1, out float tEntry)
        {
            tEntry = 0f;
            float t0 = 0f, t1 = 1f;
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float[] p = { -dx, dx, -dy, dy };
            float[] q = { a.X - x0, x1 - a.X, a.Y - y0, y1 - a.Y };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 0.000001f)
                {
                    if (q[i] < 0f)
                    {
                        return false;
                    }
                    continue;
                }
                float t = q[i] / p[i];
                if (p[i] < 0f)
                {
                    if (t > t1) return false;
                    if (t > t0) t0 = t;
                }
                else
                {
                    if (t < t0) return false;
                    if (t < t1) t1 = t;
                }
            }
            tEntry = t0;
            return true;
        }
    }
}
