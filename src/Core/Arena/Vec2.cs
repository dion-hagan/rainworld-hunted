using System;

namespace Hunted.Core.Arena
{
    /// <summary>A pixel position or velocity. The arena does not reference Unity, so it has its own.</summary>
    public struct Vec2
    {
        public float X;
        public float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float Length => (float)Math.Sqrt(X * X + Y * Y);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);

        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        /// <summary>Unit vector from <paramref name="from"/> to <paramref name="to"/> (zero when they coincide).</summary>
        public static Vec2 Direction(Vec2 from, Vec2 to)
        {
            Vec2 d = to - from;
            float len = d.Length;
            return len > 0.0001f ? d * (1f / len) : new Vec2(0f, 0f);
        }

        public override string ToString() => "(" + X.ToString("0") + ", " + Y.ToString("0") + ")";
    }
}
