using System;
using System.Collections.Generic;

namespace Hunted.Core.Arena
{
    public enum EpisodeOutcome
    {
        Timeout,
        LearnerWon,
        LearnerDied,
        /// <summary>Both died in the same tick: they lined up on each other and threw together.</summary>
        BothDied,
    }

    /// <summary>What happened in one encounter, from the learner's side.</summary>
    public sealed class EpisodeResult
    {
        public int Instance;
        public int Episode;
        public int Ticks;
        public EpisodeOutcome Outcome;
        public float LearnerReward;
        public int LearnerThrows;
        public int LearnerHits;
        public int OpponentThrows;
        public int OpponentHits;
        public int Decisions;
        public float Epsilon;

        public bool Won => Outcome == EpisodeOutcome.LearnerWon;
    }

    /// <summary>
    /// One arena: a room, the learner (the Stage 3 Pursuer, or the Stage 2 rules when it
    /// has no policy), the scripted opponent, weapons in flight and on the ground, and a
    /// tick clock that the policy's reward windows run on. Encounters run back to back;
    /// each one starts with both fighters placed apart in a (possibly fresh) room and ends
    /// when one dies or the time runs out. The learner's rewards are the game's.
    /// </summary>
    public sealed class ArenaMatch
    {
        /// <summary>The setup for the next encounters; a trainer may change it between rounds (a curriculum).</summary>
        public ArenaConfig Config;
        public readonly Random Rng;
        public readonly Fighter Learner = new Fighter("learner");
        public readonly Fighter Opponent = new Fighter("opponent");
        public readonly ArenaBrain LearnerBrain;
        public readonly ArenaBrain OpponentBrain;
        public readonly List<Projectile> Projectiles = new List<Projectile>();
        public readonly List<GroundItem> Items = new List<GroundItem>();
        internal readonly List<Surface> LevelSurfaces = new List<Surface>();

        public ArenaRoom Room { get; private set; }
        /// <summary>
        /// Ticks since the current encounter began: the policy's clock. It restarts at every
        /// encounter, which is safe because every pending decision is flushed at the end of
        /// one, and it keeps a run of millions of encounters from overflowing the counter.
        /// </summary>
        public int Tick { get; private set; }
        public int Episode { get; private set; }
        public TacticPolicy Policy => LearnerBrain.Policy;

        private EpisodeResult current;
        private ArenaRoom defaultRoom;

        public ArenaMatch(ArenaConfig config, int seed, TacticPolicy learnerPolicy)
        {
            Config = config ?? new ArenaConfig();
            Rng = new Random(seed);
            LearnerBrain = new ArenaBrain(Learner, Opponent, learnerPolicy, false, Rng);
            OpponentBrain = new ArenaBrain(Opponent, Learner, null, true, Rng);
            Room = ArenaRoom.Default();
        }

        /// <summary>Runs one encounter to its end and returns what happened.</summary>
        public EpisodeResult RunEpisode()
        {
            StartEpisode();
            current = new EpisodeResult { Episode = Episode, Outcome = EpisodeOutcome.Timeout };
            while (Tick < Config.MaxTicks)
            {
                Step();
                if (Learner.Dead || Opponent.Dead)
                {
                    current.Outcome = Learner.Dead && Opponent.Dead ? EpisodeOutcome.BothDied : Learner.Dead ? EpisodeOutcome.LearnerDied : EpisodeOutcome.LearnerWon;
                    break;
                }
            }
            current.Ticks = Tick;
            current.Epsilon = Policy != null ? Policy.Epsilon : 0f;
            // Decisions still open when the encounter ends learn from what they got; nothing
            // from the next encounter can reach back to them (the game flushes at session end).
            Policy?.Flush();
            Episode++;
            EpisodeResult result = current;
            current = null;
            return result;
        }

        private void StartEpisode()
        {
            Tick = 0;
            if (Config.RandomRooms)
            {
                Room = ArenaRoom.Generate(Rng);
            }
            else if (!ReferenceEquals(Room, defaultRoom))
            {
                Room = defaultRoom = ArenaRoom.Default();
            }
            Projectiles.Clear();
            Items.Clear();
            Vec2 a = RandomSpot();
            Vec2 b = RandomSpot();
            for (int attempt = 0; attempt < 30 && Vec2.Distance(a, b) < Config.StartSeparation; attempt++)
            {
                b = RandomSpot();
            }
            Learner.Reset(a, Room.SurfaceAt(a.X, a.Y), Config.LearnerGear);
            Opponent.Reset(b, Room.SurfaceAt(b.X, b.Y), Config.OpponentGear);
            Learner.Facing = b.X >= a.X ? 1 : -1;
            Opponent.Facing = a.X >= b.X ? 1 : -1;
            for (int i = 0; i < Config.SpareSpears; i++)
            {
                Items.Add(new GroundItem(WeaponKind.Spear, RandomSpot()));
            }
            LearnerBrain.ResetEpisode();
            OpponentBrain.ResetEpisode();
        }

        private Vec2 RandomSpot()
        {
            Surface s = Room.Surfaces[Rng.Next(Room.Surfaces.Count)];
            float x = s.X0 + 20f + (float)Rng.NextDouble() * Math.Max(1f, s.X1 - s.X0 - 40f);
            return new Vec2(x, s.Y);
        }

        private void Step()
        {
            Tick++;
            Projectile a = LearnerBrain.Think(this);
            Projectile b = OpponentBrain.Think(this);
            if (a != null)
            {
                Projectiles.Add(a);
                current.LearnerThrows++;
            }
            if (b != null)
            {
                Projectiles.Add(b);
                current.OpponentThrows++;
            }
            Learner.Step(Room);
            Opponent.Step(Room);
            PickUp(Learner);
            PickUp(Opponent);
            for (int i = Projectiles.Count - 1; i >= 0; i--)
            {
                Projectile p = Projectiles[i];
                Vec2 prev = p.Advance();
                Fighter victim = ReferenceEquals(p.Thrower, Learner) ? Opponent : Learner;
                if (!victim.Dead && SegmentHitsCircle(prev, p.Pos, victim.Eye, Fighter.Radius))
                {
                    Projectiles.RemoveAt(i);
                    OnHit(p, victim);
                    continue;
                }
                Vec2? wall = Room.FirstSolidHit(prev, p.Pos, out _);
                if (wall.HasValue)
                {
                    Projectiles.RemoveAt(i);
                    OnWall(p, wall.Value);
                    continue;
                }
                if (p.Age > 400)
                {
                    Projectiles.RemoveAt(i);
                }
            }
        }

        private void PickUp(Fighter f)
        {
            if (f.Dead || f.Stun > 0)
            {
                return;
            }
            float held = ArenaConfig.WeaponValue(f.Held);
            GroundItem item = NearestBetterItem(f.Pos, held, 30f);
            if (item == null || Math.Abs(item.Pos.Y - f.Pos.Y) > 30f)
            {
                return;
            }
            Items.Remove(item);
            if (f.Held != WeaponKind.None)
            {
                Items.Add(new GroundItem(f.Held, new Vec2(f.Pos.X, Room.SurfaceBelow(f.Pos.X, f.Pos.Y).Y)));
            }
            f.Held = item.Kind;
        }

        private void OnHit(Projectile p, Fighter victim)
        {
            float damage = ArenaRewards.Damage(p.Kind, Rng);
            victim.Hurt(damage, ArenaRewards.StunTicks(p.Kind));
            if (ReferenceEquals(p.Thrower, Learner))
            {
                current.LearnerHits++;
                Reward(ArenaRewards.ForHit(damage), true);
                if (victim.Dead)
                {
                    Reward(ArenaRewards.Kill, false);
                }
            }
            else
            {
                current.OpponentHits++;
                Reward(ArenaRewards.ForHurt(damage), false);
                if (victim.Dead)
                {
                    Reward(ArenaRewards.Death, false);
                }
            }
            if (ArenaRewards.SurvivesHit(p.Kind))
            {
                Items.Add(new GroundItem(p.Kind, new Vec2(victim.Pos.X, Room.SurfaceBelow(victim.Pos.X, victim.Pos.Y).Y)));
            }
        }

        private void OnWall(Projectile p, Vec2 at)
        {
            if (ReferenceEquals(p.Thrower, Learner))
            {
                Reward(ArenaRewards.WallHit, true);
            }
            if (p.Kind == WeaponKind.Rock)
            {
                // Rocks bounce off and can be thrown again; a spear that hits terrain sticks
                // where it lands and is worth nothing to the AI, as in the game.
                float x = Math.Max(Fighter.Radius, Math.Min(Room.Width - Fighter.Radius, at.X));
                Items.Add(new GroundItem(p.Kind, new Vec2(x, Room.SurfaceBelow(x, at.Y).Y)));
            }
        }

        private void Reward(float value, bool throwOutcome)
        {
            if (Policy == null)
            {
                return;
            }
            Policy.Reward(value, Tick, throwOutcome);
            current.LearnerReward += value;
        }

        internal void CountDecision()
        {
            if (current != null)
            {
                current.Decisions++;
            }
        }

        /// <summary>The closest item worth more than <paramref name="held"/> within <paramref name="range"/>, or null.</summary>
        public GroundItem NearestBetterItem(Vec2 from, float held, float range)
        {
            GroundItem best = null;
            float bestScore = 0f;
            foreach (GroundItem item in Items)
            {
                float value = ArenaConfig.WeaponValue(item.Kind);
                if (value <= held)
                {
                    continue;
                }
                float distance = Vec2.Distance(item.Pos, from);
                if (distance > range)
                {
                    continue;
                }
                // The game's scoring: value scaled down with distance.
                float score = value * Math.Max(0f, 1f - distance / 2500f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>True when a weapon thrown by the other fighter is heading toward <paramref name="f"/> and within <paramref name="within"/> px.</summary>
        public bool WeaponFlyingAt(Fighter f, float within)
        {
            foreach (Projectile p in Projectiles)
            {
                if (ReferenceEquals(p.Thrower, f))
                {
                    continue;
                }
                float dx = f.Pos.X - p.Pos.X;
                if (Math.Sign(dx) != Math.Sign(p.Vel.X) || Math.Abs(dx) > within || Math.Abs(p.Pos.Y - f.Eye.Y) > 40f)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static bool SegmentHitsCircle(Vec2 a, Vec2 b, Vec2 center, float radius)
        {
            Vec2 ab = b - a;
            float len2 = ab.X * ab.X + ab.Y * ab.Y;
            float t = 0f;
            if (len2 > 0.000001f)
            {
                t = ((center.X - a.X) * ab.X + (center.Y - a.Y) * ab.Y) / len2;
                t = t < 0f ? 0f : t > 1f ? 1f : t;
            }
            Vec2 closest = a + ab * t;
            return Vec2.Distance(closest, center) <= radius;
        }
    }
}
