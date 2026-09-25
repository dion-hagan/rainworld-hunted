using System;

namespace Hunted.Core.Arena
{
    /// <summary>
    /// A slugcat-shaped body in the arena: a point with a radius that walks, jumps,
    /// climbs poles, takes damage and holds one weapon. The numbers are close to the
    /// game's (a tile is 20 px, a run is about four tiles a second, a jump clears
    /// three to four tiles) so distances and timings the learner sees are in the
    /// right range. The brain sets the inputs each tick; <see cref="Step"/> applies them.
    /// </summary>
    public sealed class Fighter
    {
        /// <summary>Half the body's width; also the crate push-out margin.</summary>
        public const float Radius = 14f;
        /// <summary>A slugcat is two body chunks: the lower one just off the ground and the main one above it (Player chunk radius is about 9).</summary>
        public const float ChunkRadius = 10f;
        public const float LowerChunkHeight = 9f;
        public const float MainChunkHeight = 27f;
        /// <summary>The main chunk is where the game looks from, throws from and aims at.</summary>
        public const float EyeHeight = MainChunkHeight;
        public const float WalkSpeed = 4.4f;
        public const float ClimbSpeed = 3f;
        public const float JumpVelocity = 12f;
        public const float Gravity = 0.9f;
        /// <summary>How much higher than its feet a jump can land.</summary>
        public const float JumpReach = 75f;

        public readonly string Name;

        public Vec2 Pos;
        public Vec2 Vel;
        /// <summary>The surface under its feet; null while airborne or on a pole.</summary>
        public Surface Ground;
        public Pole OnPole;
        public int Facing = 1;
        public float Health = 1f;
        public int Stun;
        public WeaponKind Held;
        public int ThrowCooldown;

        // Inputs for this tick, set by the brain and consumed by Step.
        public int MoveX;
        public bool Jump;
        public int Climb;
        public Pole WantPole;
        /// <summary>Set by the brain: do not start a climb or a jump this tick (someone armed is watching); hold at the foot instead.</summary>
        public bool HoldClimbs;

        public Fighter(string name)
        {
            Name = name;
        }

        public bool Dead => Health <= 0f;
        public Vec2 Eye => new Vec2(Pos.X, Pos.Y + EyeHeight);
        public Vec2 MainChunk => new Vec2(Pos.X, Pos.Y + MainChunkHeight);
        public Vec2 LowerChunk => new Vec2(Pos.X, Pos.Y + LowerChunkHeight);

        public void Reset(Vec2 pos, Surface ground, WeaponKind held)
        {
            Pos = pos;
            Vel = new Vec2(0f, 0f);
            Ground = ground;
            OnPole = null;
            Health = 1f;
            Stun = 0;
            Held = held;
            ThrowCooldown = 0;
            HoldClimbs = false;
            ClearInputs();
        }

        /// <summary>Clears the movement inputs (not <see cref="HoldClimbs"/>, which the brain sets before steering).</summary>
        public void ClearInputs()
        {
            MoveX = 0;
            Jump = false;
            Climb = 0;
            WantPole = null;
        }

        // ------------------------------------------------------------------ steering

        /// <summary>Sets this tick's inputs to move toward <paramref name="target"/>: walk, jump onto low platforms, use poles for high ones, drop off edges.</summary>
        public void Steer(ArenaRoom room, Vec2 target)
        {
            ClearInputs();
            float dx = target.X - Pos.X;
            float dy = target.Y - Pos.Y;
            if (OnPole != null)
            {
                // Heading for the surface at either end: climb all the way there, then step off.
                bool wantTop = target.Y >= OnPole.Y1 - 12f;
                bool wantBottom = target.Y <= OnPole.Y0 + 12f;
                if ((wantTop || dy > 6f) && !wantBottom && Pos.Y < OnPole.Y1 - 0.5f)
                {
                    Climb = 1;
                }
                else if ((wantBottom || dy < -6f) && Pos.Y > OnPole.Y0 + 0.5f)
                {
                    Climb = -1;
                }
                else
                {
                    MoveX = dx >= 0f ? 1 : -1; // step off (or let go, mid-pole)
                }
                return;
            }
            if (Ground == null)
            {
                MoveX = Math.Abs(dx) > 4f ? Math.Sign(dx) : 0; // air control
                return;
            }
            if (Math.Abs(dy) <= 12f)
            {
                WalkToward(dx);
                JumpOverCrate(room);
                return;
            }
            if (dy > 12f)
            {
                Surface above = room.SurfaceAt(target.X, target.Y, 12f);
                if (above != null && dy <= JumpReach)
                {
                    float aimX = Math.Max(above.X0 + 15f, Math.Min(above.X1 - 15f, target.X));
                    WalkToward(aimX - Pos.X);
                    if (Pos.X >= above.X0 - 30f && Pos.X <= above.X1 + 30f)
                    {
                        if (HoldClimbs)
                        {
                            MoveX = 0; // wait under it until it is safe to go up
                            return;
                        }
                        Jump = true;
                    }
                    JumpOverCrate(room);
                    return;
                }
                Pole pole = room.PoleTo(Pos.X, Pos.Y, target.Y);
                if (pole != null)
                {
                    if (Math.Abs(pole.X - Pos.X) <= 5f)
                    {
                        if (!HoldClimbs)
                        {
                            WantPole = pole;
                        }
                    }
                    else
                    {
                        WalkToward(pole.X - Pos.X);
                        JumpOverCrate(room);
                    }
                    return;
                }
                WalkToward(dx); // no way up: best effort
                JumpOverCrate(room);
                return;
            }
            // Target below.
            if (Ground.Y <= target.Y + 12f || ReferenceEquals(Ground, room.Floor))
            {
                WalkToward(dx);
                JumpOverCrate(room);
            }
            else if (Ground.Spans(target.X))
            {
                MoveX = Pos.X - Ground.X0 < Ground.X1 - Pos.X ? -1 : 1; // to the nearer edge, then drop
            }
            else
            {
                WalkToward(dx); // walking off the edge gets us down
            }
        }

        private void WalkToward(float dx)
        {
            MoveX = Math.Abs(dx) > 3f ? Math.Sign(dx) : 0;
        }

        private void JumpOverCrate(ArenaRoom room)
        {
            if (MoveX == 0)
            {
                return;
            }
            Crate crate = room.CrateAhead(Pos.X, Pos.Y, MoveX * 30f);
            if (crate != null && crate.Y1 - Pos.Y <= JumpReach)
            {
                if (HoldClimbs)
                {
                    MoveX = 0; // stay behind the crate rather than jump into view
                    return;
                }
                Jump = true;
            }
        }

        // ------------------------------------------------------------------ physics

        /// <summary>Applies this tick's inputs and gravity, then clears the inputs.</summary>
        public void Step(ArenaRoom room)
        {
            if (Stun > 0)
            {
                Stun--;
                ClearInputs();
            }
            if (ThrowCooldown > 0)
            {
                ThrowCooldown--;
            }
            if (MoveX != 0)
            {
                Facing = MoveX;
            }
            if (WantPole != null && Ground != null)
            {
                OnPole = WantPole;
                Ground = null;
                Pos.X = WantPole.X;
                Climb = 1;
            }
            if (OnPole != null)
            {
                StepOnPole(room);
            }
            else if (Ground != null)
            {
                StepOnGround(room);
            }
            else
            {
                StepAirborne(room);
            }
            ClearInputs();
        }

        private void StepOnPole(ArenaRoom room)
        {
            Vel = new Vec2(0f, 0f);
            Pos.X = OnPole.X;
            if (Jump)
            {
                OnPole = null;
                Vel = new Vec2(MoveX * WalkSpeed, JumpVelocity * 0.7f);
                Pos += Vel;
                return;
            }
            if (Climb != 0)
            {
                Pos.Y = Math.Max(OnPole.Y0, Math.Min(OnPole.Y1, Pos.Y + Climb * ClimbSpeed));
                return;
            }
            if (MoveX != 0)
            {
                if (Pos.Y >= OnPole.Y1 - 0.5f)
                {
                    Surface top = room.SurfaceAt(OnPole.X, OnPole.Y1, 4f);
                    OnPole = null;
                    if (top != null)
                    {
                        Ground = top;
                        Pos.Y = top.Y;
                        Pos.X += MoveX * WalkSpeed;
                    }
                }
                else if (Pos.Y <= OnPole.Y0 + 0.5f)
                {
                    OnPole = null;
                    Ground = room.SurfaceAt(Pos.X, Pos.Y, 4f) ?? room.Floor;
                    Pos.Y = Ground.Y;
                    Pos.X += MoveX * WalkSpeed;
                }
                else
                {
                    OnPole = null; // let go mid-pole and drop
                    Vel = new Vec2(MoveX * WalkSpeed, 0f);
                }
            }
        }

        private void StepOnGround(ArenaRoom room)
        {
            Vel = new Vec2(MoveX * WalkSpeed, 0f);
            Crate crate = room.CrateAhead(Pos.X, Pos.Y, Vel.X);
            if (crate != null)
            {
                Pos.X = Vel.X > 0f ? crate.X0 - 0.5f : crate.X1 + 0.5f;
                Vel.X = 0f;
            }
            else
            {
                Pos.X += Vel.X;
            }
            ClampX(room);
            if (Jump)
            {
                Vel.Y = JumpVelocity;
                Ground = null;
                Pos.Y += 0.5f;
            }
            else if (!Ground.Spans(Pos.X))
            {
                Ground = null; // walked off the edge
            }
        }

        private void StepAirborne(ArenaRoom room)
        {
            Vel.X = MoveX != 0 ? MoveX * WalkSpeed : Vel.X * 0.9f;
            Vel.Y -= Gravity;
            Vec2 prev = Pos;
            Pos += Vel;
            ClampX(room);
            if (Vel.Y <= 0f)
            {
                // Coming down onto a surface (the floor, a platform, a crate top) lands on it.
                Surface landing = null;
                foreach (Surface s in room.Surfaces)
                {
                    if (s.Spans(Pos.X) && prev.Y >= s.Y && Pos.Y <= s.Y && (landing == null || s.Y > landing.Y))
                    {
                        landing = s;
                    }
                }
                if (landing != null)
                {
                    Ground = landing;
                    Pos.Y = landing.Y;
                    Vel = new Vec2(0f, 0f);
                }
            }
            if (Ground == null)
            {
                // Still in the air and overlapping a crate from the side: push out.
                foreach (Crate c in room.Crates)
                {
                    if (Pos.Y < c.Y1 - 1f && Pos.Y + 2f * Radius > c.Y0 && Pos.X > c.X0 - 6f && Pos.X < c.X1 + 6f)
                    {
                        Pos.X = Pos.X - c.X0 < c.X1 - Pos.X ? c.X0 - 6f : c.X1 + 6f;
                        Vel.X = 0f;
                    }
                }
            }
            if (Pos.Y < 0f)
            {
                Ground = room.Floor;
                Pos.Y = 0f;
                Vel = new Vec2(0f, 0f);
            }
            if (Pos.Y > room.Height - 2f * Radius)
            {
                Pos.Y = room.Height - 2f * Radius;
                Vel.Y = 0f;
            }
        }

        private void ClampX(ArenaRoom room)
        {
            if (Pos.X < Radius)
            {
                Pos.X = Radius;
                Vel.X = 0f;
            }
            else if (Pos.X > room.Width - Radius)
            {
                Pos.X = room.Width - Radius;
                Vel.X = 0f;
            }
        }

        // ------------------------------------------------------------------ combat

        /// <summary>True when a throw can leave the hand this tick: something held, not stunned, not just thrown.</summary>
        public bool CanThrow => Held != WeaponKind.None && Stun == 0 && ThrowCooldown == 0;

        /// <summary>Lets go of the held weapon as a projectile flying level from the main chunk in <paramref name="direction"/> (-1 or 1).</summary>
        public Projectile Throw(int direction)
        {
            var p = new Projectile(Held, this, new Vec2(Pos.X + direction * (Radius + 4f), Pos.Y + MainChunkHeight), new Vec2(direction * Projectile.Speed, 0f));
            Held = WeaponKind.None;
            ThrowCooldown = 10;
            Facing = direction;
            return p;
        }

        public void Hurt(float damage, int stun)
        {
            Health -= damage;
            Stun = Math.Max(Stun, stun);
        }
    }
}
