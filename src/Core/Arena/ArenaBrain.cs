using System;
using System.Collections.Generic;

namespace Hunted.Core.Arena
{
    /// <summary>
    /// The decision layer of a fighter, mirroring <c>PursuerAI</c>'s Engage, Search, Scavenge
    /// and Travel rules as closely as the arena allows. With <see cref="Policy"/> null it is
    /// the Stage 2 Pursuer: it takes a throwing position and throws whenever it is lined up.
    /// With a policy it is the Stage 3 Pursuer: every twenty ticks while armed and engaging
    /// it builds the same twelve features the game builds and lets the policy pick the
    /// tactic, and the tactics mean what they mean in the game (Throw takes a position and
    /// throws, Reposition gives the position up for a fresh one without throwing, CloseIn
    /// walks at the target, Wait holds still and throws only if the target walks into line).
    /// </summary>
    public sealed class ArenaBrain
    {
        public enum Mode
        {
            /// <summary>Target not seen for a long while: walk to where it actually is (the game knows the player's room and tile).</summary>
            Travel,
            /// <summary>Target recently seen, now lost: go to the last known position.</summary>
            Search,
            /// <summary>Better weapon in the room: go and take it.</summary>
            Scavenge,
            /// <summary>Target visible or very recently seen: fight.</summary>
            Engage,
        }

        public const int TacticHoldTicks = 20;
        public const float ThrowRangePx = 520f;
        public const float ScavengeRangePx = 600f;
        /// <summary>The game's tracker (SlugNPCAI's, seeAroundCorners = 100) keeps visual contact for this long after the last sighting and only then looks again.</summary>
        public const int SeeAroundCornersTicks = 100;
        private const int EngageMemoryTicks = 200;
        private const int RememberTargetTicks = 40 * 30;
        private const int SenseInterval = 40 * 8;
        private const int SenseAfterLostTicks = 400;
        private const int AttackPositionMaxAge = 300;

        public readonly Fighter Me;
        public readonly Fighter Them;
        /// <summary>The learner's policy, or null for the fixed Stage 2 rules.</summary>
        public readonly TacticPolicy Policy;
        /// <summary>True for the opponent's seat, which may dodge when the config says so.</summary>
        public readonly bool IsOpponent;

        public Mode CurrentMode { get; private set; } = Mode.Travel;
        public Tactic Tactic { get; private set; } = Tactic.Throw;
        /// <summary>The tracker's VisualContact: true from a sighting (or a sense fix) until 100 ticks later, when it looks again.</summary>
        public bool Seen { get; private set; }
        /// <summary>The tracker's TicksSinceSeen: ticks since the last sighting minus the 100 it keeps contact for, never below zero.</summary>
        public int TicksSinceSeen => Math.Max(0, contactTicks - SeeAroundCornersTicks);

        private readonly Random rng;
        private readonly float[] situation = new float[TacticFeatures.Count];
        private readonly List<Vec2> previousAttackPositions = new List<Vec2>();
        private Vec2 lastSeen;
        private int contactTicks;
        private int senseCooldown;
        private int tacticTicks;
        private int throwAtTarget;
        private int turnDelay;
        private Vec2 attackPos;
        private bool hasAttackPos;
        private int attackPosAge;
        private readonly bool[] allowed = new bool[TacticPolicy.TacticCount];
        private bool incomingLastTick;
        private int flipThrowY;

        public ArenaBrain(Fighter me, Fighter them, TacticPolicy policy, bool isOpponent, Random rng)
        {
            Me = me;
            Them = them;
            Policy = policy;
            IsOpponent = isOpponent;
            this.rng = rng;
        }

        /// <summary>The situation vector of the last decision, for tests and traces.</summary>
        public float[] LastSituation => situation;

        public void ResetEpisode()
        {
            CurrentMode = Mode.Travel;
            Tactic = Tactic.Throw;
            incomingLastTick = false;
            flipThrowY = 0;
            // In the game the Pursuer arrives with a fix on the player in its room (Sense gives one
            // when the tracker has no representation yet), so the encounter opens with contact.
            Seen = true;
            contactTicks = 0;
            lastSeen = Them.Pos;
            senseCooldown = SenseInterval;
            tacticTicks = 0;
            throwAtTarget = 0;
            turnDelay = 0;
            hasAttackPos = false;
            attackPosAge = 0;
            previousAttackPositions.Clear();
        }

        /// <summary>One tick of thinking: sets the body's inputs and returns the weapon it threw, if any.</summary>
        public Projectile Think(ArenaMatch match)
        {
            if (Me.Dead)
            {
                Me.ClearInputs();
                return null;
            }
            ArenaRoom room = match.Room;
            turnDelay = Math.Max(turnDelay - 1, 0);
            if (senseCooldown > 0)
            {
                senseCooldown--;
            }

            // Perception, as the game's Tracker.CreatureRepresentation does it: contact holds for
            // 100 ticks after a sighting without looking again; after that it looks every tick
            // and a sighting resets the clock. The Pursuer's Sense() adds a fix (contact set even
            // through walls) once the tracker reports the target lost for over 400 ticks.
            contactTicks++;
            if (contactTicks > SeeAroundCornersTicks)
            {
                Seen = false;
                if (!Them.Dead && room.LineOfSight(Me.Eye, Them.Eye))
                {
                    contactTicks = 0;
                    Seen = true;
                }
            }
            if (!Them.Dead && senseCooldown == 0 && TicksSinceSeen > SenseAfterLostTicks)
            {
                contactTicks = 0;
                Seen = true;
                senseCooldown = SenseInterval;
            }
            if (Seen)
            {
                lastSeen = Them.Pos;
            }

            if (Me.Stun > 0)
            {
                // Unconscious: the game does not run the AI's input step at all.
                Me.ClearInputs();
                return null;
            }

            float held = ArenaConfig.WeaponValue(Me.Held);
            Vec2 target;
            if (!Them.Dead && (Seen || TicksSinceSeen < EngageMemoryTicks))
            {
                CurrentMode = Mode.Engage;
                target = EngageTarget(match, held);
            }
            else if (held < 1f && match.NearestBetterItem(Me.Pos, held, float.MaxValue) != null)
            {
                CurrentMode = Mode.Scavenge;
                target = match.NearestBetterItem(Me.Pos, held, float.MaxValue).Pos;
            }
            else if (TicksSinceSeen < RememberTargetTicks)
            {
                CurrentMode = Mode.Search;
                target = lastSeen;
            }
            else
            {
                CurrentMode = Mode.Travel;
                target = Them.Pos;
            }

            // A person does not climb a pole or jump a crate up toward an armed Pursuer standing
            // above within throwing distance; the pure Stage 2 rules would, and hand a waiting
            // learner free throws on the way up. No sight test: the learner's own platform hides
            // the foot of the pole, and the climb would be seen halfway up regardless.
            Me.HoldClimbs = IsOpponent && match.Config.OpponentAvoidsExposure && Them.Held != WeaponKind.None && Them.Stun == 0
                && Math.Abs(Them.Pos.X - Me.Pos.X) <= ThrowRangePx && Them.Pos.Y > Me.Pos.Y + 12f;
            if (Me.Move.HasValue)
            {
                // The body is running a move: no steering, and no throw except the flip throw's own.
                Me.ClearInputs();
                if (Me.Move == Tactic.FlipThrow && Me.FlipTick == Fighter.FlipThrowTick && Me.CanThrow)
                {
                    Policy?.NoteThrow();
                    return flipThrowY != 0 ? Me.ThrowVertical(flipThrowY) : Me.Throw(Them.Pos.X >= Me.Pos.X ? 1 : -1);
                }
                return null;
            }
            Me.Steer(room, target);

            Projectile thrown = null;
            if (throwAtTarget != 0)
            {
                if (Me.Facing == throwAtTarget)
                {
                    if (Me.CanThrow)
                    {
                        thrown = Me.Throw(throwAtTarget);
                    }
                    turnDelay = 5;
                    throwAtTarget = 0;
                }
                else
                {
                    Me.MoveX = throwAtTarget; // turn to face the target first
                }
            }
            if (turnDelay > 0)
            {
                Me.MoveX = Me.Facing;
            }
            float dodge = IsOpponent ? match.Config.OpponentDodgeChance : 0f;
            if (dodge > 0f && Me.Ground != null && match.WeaponFlyingAt(Me) && rng.NextDouble() < dodge)
            {
                Me.Jump = true; // an optional person-like reflex the Stage 2 rules do not have
            }
            return thrown;
        }

        private Vec2 EngageTarget(ArenaMatch match, float held)
        {
            if (held < 0.5f)
            {
                // Unarmed: arm up if anything usable is close, otherwise keep the pressure on.
                GroundItem item = match.NearestBetterItem(Me.Pos, held, ScavengeRangePx);
                return item != null ? item.Pos : lastSeen;
            }
            ChooseTactic(match, held);
            Vec2 coord;
            switch (Tactic)
            {
                case Tactic.CloseIn:
                    coord = lastSeen;
                    break;
                case Tactic.Wait:
                    coord = Me.Pos;
                    break;
                default:
                    FindAttackPosition(match);
                    coord = attackPos;
                    break;
            }
            if (!Tactics.IsMove(Tactic) && Tactic != Tactic.Reposition && Tactic != Tactic.CloseIn && throwAtTarget == 0 && turnDelay == 0 && GoodAttackPos(match.Room))
            {
                throwAtTarget = Them.Pos.X >= Me.Pos.X ? 1 : -1;
                Policy?.NoteThrow();
            }
            return coord;
        }

        /// <summary>Level with the target (slugcat throws are horizontal), in range and in sight: the game's rule.</summary>
        public bool GoodAttackPos(ArenaRoom room)
        {
            return Lined(room, Me.Eye, Them.Eye);
        }

        private static bool Lined(ArenaRoom room, Vec2 from, Vec2 to)
        {
            Vec2 dir = Vec2.Direction(from, to);
            if (dir.Y > 0.05f || dir.Y < -0.2f)
            {
                return false;
            }
            if (Vec2.Distance(from, to) > ThrowRangePx)
            {
                return false;
            }
            return room.LineOfSight(from, to);
        }

        /// <summary>
        /// Every <see cref="TacticHoldTicks"/>, asks the policy which tactic fits the situation;
        /// also the tick a weapon starts flying at the body (a throw is a new situation), and
        /// the tick after a move ends. The features are built exactly as
        /// <c>PursuerAI.ChooseTactic</c> builds them; threat and bomb are always zero here
        /// because the arena has no predators and no bombs. Move tactics are only offered when
        /// the body can start one this tick (on the ground, nothing in progress), so a move arm
        /// is never trained on what another tactic did in its place; the game masks the same way.
        /// </summary>
        private void ChooseTactic(ArenaMatch match, float held)
        {
            if (Me.Move.HasValue)
            {
                return; // mid-move: the decision stands until the body is free again
            }
            bool incoming = match.WeaponFlyingAt(Me);
            // A weapon starting to fly at the body is a new situation, and so is the end of a move
            // (the tactic is still the move but the body is free): decide now, not at the next hold.
            bool interrupt = (incoming && !incomingLastTick) || Tactics.IsMove(Tactic);
            incomingLastTick = incoming;
            if (!interrupt && tacticTicks-- > 0)
            {
                return;
            }
            tacticTicks = TacticHoldTicks;
            if (Policy == null)
            {
                Tactic = Tactic.Throw;
                return;
            }
            Vec2 offset = Them.Pos - Me.Pos;
            int i = 0;
            situation[i++] = Clamp(offset.X / 400f, -1f, 1f);
            situation[i++] = Clamp(offset.Y / 400f, -1f, 1f);
            situation[i++] = Clamp(offset.Length / 400f, 0f, 1f);
            situation[i++] = Seen ? 1f : 0f;
            situation[i++] = Clamp(TicksSinceSeen / 400f, 0f, 1f);
            situation[i++] = offset.Y > 20f ? 1f : 0f;
            situation[i++] = Them.Held != WeaponKind.None ? 1f : 0f;
            situation[i++] = Clamp(Them.Vel.Length / 10f, 0f, 1f);
            situation[i++] = Clamp(held / 3f, 0f, 1f);
            situation[i++] = 0f;
            situation[i++] = GoodAttackPos(match.Room) ? 1f : 0f;
            situation[i++] = 0f;
            situation[i++] = Me.CanStartMove ? 1f : 0f;
            situation[i++] = incoming ? 1f : 0f;
            situation[i++] = offset.Y < -20f ? 1f : 0f;
            bool canStart = Me.CanStartMove;
            for (int a = 0; a < allowed.Length; a++)
            {
                allowed[a] = !Tactics.IsMove((Tactic)a) || canStart;
            }
            Tactic = Policy.Choose(situation, match.Tick, allowed);
            match.CountDecision();
            if (Tactic == Tactic.Reposition)
            {
                // Give up the current spot: it goes on the penalty list and a fresh one is picked at once.
                if (hasAttackPos)
                {
                    previousAttackPositions.Insert(Math.Min(1, previousAttackPositions.Count), attackPos);
                }
                attackPosAge = AttackPositionMaxAge;
            }
            else if (Tactics.IsMove(Tactic))
            {
                // The body's stand-in for the move, toward the target. The arena allows upward flip
                // throws: the Remix option that gates them in the game is on by default.
                flipThrowY = Tactic == Tactic.FlipThrow ? Tactics.FlipThrowY(offset.X, offset.Y, true) : 0;
                Me.StartMove(Tactic, offset.X >= 0f ? 1 : -1);
                throwAtTarget = 0;
                turnDelay = 0;
            }
        }

        /// <summary>
        /// Keeps the current attack position while it is still lined up on the target and not
        /// too old; otherwise samples spots in sight of the target and scores them the way the
        /// game's <c>SpearThrowPositionScore</c> does: a spot level with the target (a horizontal
        /// throw can reach it) is worth a hundred times an off-level one, near spots are worth
        /// double, very far and very close spots are marked down, off-level spots are only
        /// acceptable from a distance, and spots used before are avoided.
        /// </summary>
        private void FindAttackPosition(ArenaMatch match)
        {
            ArenaRoom room = match.Room;
            attackPosAge++;
            if (hasAttackPos && attackPosAge < AttackPositionMaxAge && Lined(room, Eye(attackPos), Them.Eye))
            {
                return;
            }
            Vec2? best = null;
            float bestScore = float.MinValue;
            List<Surface> level = match.LevelSurfaces;
            level.Clear();
            foreach (Surface s in room.Surfaces)
            {
                if (Math.Abs(s.Y - Them.Pos.Y) <= 12f)
                {
                    level.Add(s);
                }
            }
            for (int k = 0; k < 12; k++)
            {
                Surface s = level.Count > 0 && rng.NextDouble() < 0.7 ? level[rng.Next(level.Count)] : room.Surfaces[rng.Next(room.Surfaces.Count)];
                float d = 120f + (float)rng.NextDouble() * 360f;
                float side = rng.NextDouble() < 0.5 ? -1f : 1f;
                float x = Math.Max(s.X0 + 10f, Math.Min(s.X1 - 10f, Them.Pos.X + side * d));
                if (s.X1 - s.X0 < 20f)
                {
                    continue;
                }
                var candidate = new Vec2(x, s.Y);
                if (!room.LineOfSight(Eye(candidate), Them.Eye))
                {
                    continue;
                }
                float dist = Vec2.Distance(candidate, Them.Pos);
                float dy = Math.Abs(candidate.Y - Them.Pos.Y);
                float score = dy <= 12f ? 100f : 1f;
                if (Vec2.Distance(candidate, Me.Pos) < 60f)
                {
                    score *= 2f;
                }
                score *= LerpMap(dist, 600f, 1200f, 1f, 0f);
                score *= LerpMap(dist, 100f, 0f, 1f, 0.1f);
                if (dy >= 60f)
                {
                    score *= Clamp((dist - 100f) / 100f, 0f, 1f);
                }
                for (int n = 1; n < previousAttackPositions.Count; n++)
                {
                    float away = Vec2.Distance(candidate, previousAttackPositions[n]);
                    if (away < 100f)
                    {
                        score -= 50f * (1f - away / 100f);
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            if (best.HasValue)
            {
                attackPos = best.Value;
                hasAttackPos = true;
                attackPosAge = 0;
                previousAttackPositions.Insert(0, attackPos);
                if (previousAttackPositions.Count > 20)
                {
                    previousAttackPositions.RemoveAt(20);
                }
            }
            else
            {
                // Nowhere in sight of the target: move toward it and look again shortly.
                attackPos = lastSeen;
                hasAttackPos = true;
                attackPosAge = AttackPositionMaxAge - 20;
            }
        }

        private static Vec2 Eye(Vec2 feet) => new Vec2(feet.X, feet.Y + Fighter.EyeHeight);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The game's Custom.LerpMap: maps <paramref name="v"/> from [a, b] onto [x, y], clamped.</summary>
        private static float LerpMap(float v, float a, float b, float x, float y)
        {
            float t = Clamp((v - a) / (b - a), 0f, 1f);
            return x + (y - x) * t;
        }
    }
}
