using System;

namespace Hunted.Core.Arena
{
    /// <summary>The scripted moves a fighter can run, the arena's stand-ins for the slugcat's movement tech.</summary>
    public enum MoveKind
    {
        None,
        /// <summary>Crouch, then a belly slide: 15 ticks flat on the ground, about six tiles.</summary>
        Slide,
        /// <summary>Crawl a few ticks, hold the jump for 20 while flat, then leap: eight tiles or so in the air.</summary>
        Pounce,
        /// <summary>Crouch, a 12-tick slide, then the leap out of it: another eight tiles or so in the air.</summary>
        SlidePounce,
        /// <summary>A pounce that rolls on landing: 20 more ticks low and fast.</summary>
        Roll,
        /// <summary>A 12-tick run-up, then up and a little back; airborne for about 20 ticks.</summary>
        Backflip,
        /// <summary>A backflip during which the brain throws at <see cref="Fighter.FlipThrowTick"/>.</summary>
        FlipThrow,
    }

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

        // A scripted move in progress (see StartMove): the body runs it to the end and ignores steering meanwhile.
        public MoveKind Move;
        /// <summary>Ticks since the move started.</summary>
        public int MoveTick;
        /// <summary>The move's direction along x.</summary>
        public int MoveDir;
        /// <summary>Ticks into the flip part of a backflip (0 before it leaves the ground); the brain throws at <see cref="FlipThrowTick"/>.</summary>
        public int FlipTick;
        /// <summary>True while the body is flat on the ground (sliding, rolling): a level throw at chest height passes over it.</summary>
        public bool Low;
        /// <summary>Ticks of slowed walking after a belly slide (the game's slowMovementStun).</summary>
        public int Recover;
        private int phase;
        private int phaseTick;
        private int runTicks;

        public Fighter(string name)
        {
            Name = name;
        }

        public bool Dead => Health <= 0f;
        public Vec2 Eye => MainChunk;
        /// <summary>Flat on the ground both chunks lie at the lower chunk's height, the head a little ahead.</summary>
        public Vec2 MainChunk => Low ? new Vec2(Pos.X + Facing * 12f, Pos.Y + LowerChunkHeight) : new Vec2(Pos.X, Pos.Y + MainChunkHeight);
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
            Move = MoveKind.None;
            MoveTick = 0;
            FlipTick = 0;
            Low = false;
            Recover = 0;
            runTicks = 0;
            ClearInputs();
        }

        // ------------------------------------------------------------------ scripted moves

        /// <summary>Ticks a belly slide's crouch takes before the body launches (face the direction, hold down until it is on all fours).</summary>
        public const int CrouchTicks = 8;
        /// <summary>A belly slide lasts this long (the game ends it at rollCounter 15).</summary>
        public const int SlideTicks = 15;
        /// <summary>The slide tick a pounce leaves from (the game's window is rollCounter 12 to 15).</summary>
        public const int PounceTick = 12;
        /// <summary>A landing roll lasts this long (the game's roll ends after rollCounter 15 once the diagonal is released, 30 at most).</summary>
        public const int RollTicks = 20;
        /// <summary>A backflip needs this much running first (the game's initSlideCounter must pass 10).</summary>
        public const int RunUpTicks = 12;
        /// <summary>The tick of the flip at which a flip throw leaves the hand (near the top of the arc).</summary>
        public const int FlipThrowTick = 5;
        /// <summary>Speed the pounce leaves the slide with (the game's RocketJump from a belly slide: 9 along, 8.5 up).</summary>
        public const float PounceSpeedX = 9f;
        public const float PounceSpeedY = 8.5f;
        /// <summary>The charged pounce: a few ticks of crawling toward the target, then the jump button held this long (the game's superLaunchJump reaching 20).</summary>
        public const int CrawlTicks = 4;
        public const int ChargeTicks = 20;
        public const float CrawlSpeed = 2f;
        /// <summary>The charged pounce's launch (the game adds 9 along and 3 to 4 up plus a held jump boost: about eight tiles).</summary>
        public const float ChargedSpeedX = 9f;
        public const float ChargedSpeedY = 8f;
        /// <summary>The flip's launch: 9 up, and the reversal leaves about 2 px per tick backward.</summary>
        public const float FlipSpeedX = 2f;
        public const float FlipSpeedY = 9f;
        public const float RollSpeed = 7f;
        public const int RecoverTicks = 20;

        /// <summary>True when a move can start this tick: on the ground, standing, nothing else in progress.</summary>
        public bool CanStartMove => Ground != null && OnPole == null && Move == MoveKind.None && Stun == 0 && !Dead;

        /// <summary>
        /// Starts a scripted move in <paramref name="direction"/> (-1 or 1). The body then runs
        /// the sequence on its own: crouch and slide (and pounce, and roll), or run up and flip.
        /// Returns false when it cannot start now.
        /// </summary>
        public bool StartMove(MoveKind kind, int direction)
        {
            if (kind == MoveKind.None || !CanStartMove)
            {
                return false;
            }
            Move = kind;
            MoveDir = direction >= 0 ? 1 : -1;
            Facing = MoveDir;
            MoveTick = 0;
            FlipTick = 0;
            phase = 0;
            phaseTick = 0;
            // A body already running this way skips the run-up, as the game does (initSlideCounter carries over).
            if ((kind == MoveKind.Backflip || kind == MoveKind.FlipThrow) && runTicks > 10 && Facing == MoveDir)
            {
                phaseTick = RunUpTicks - 1;
            }
            ClearInputs();
            return true;
        }

        private void EndMove()
        {
            Move = MoveKind.None;
            Low = false;
            FlipTick = 0;
        }

        /// <summary>One tick of the move in progress. Steering is ignored while it runs.</summary>
        private void StepMove(ArenaRoom room)
        {
            MoveTick++;
            phaseTick++;
            switch (Move)
            {
                case MoveKind.Slide:
                case MoveKind.SlidePounce:
                case MoveKind.Roll:
                    StepSlideFamily(room);
                    break;
                case MoveKind.Pounce:
                    StepChargedPounce(room);
                    break;
                case MoveKind.Backflip:
                case MoveKind.FlipThrow:
                    StepFlipFamily(room);
                    break;
            }
        }

        private void StepSlideFamily(ArenaRoom room)
        {
            if (phase == 0)
            {
                // Crouching: still, on the ground.
                if (Ground == null)
                {
                    EndMove();
                    StepAirborne(room);
                    return;
                }
                if (phaseTick >= CrouchTicks)
                {
                    phase = 1;
                    phaseTick = 0;
                    Low = true;
                }
                return;
            }
            if (phase == 1)
            {
                // Sliding: the game adds 18 px per tick along a half sine over 15 ticks, minus friction.
                if (Ground == null)
                {
                    EndMove();
                    StepAirborne(room);
                    return;
                }
                int length = Move == MoveKind.Slide ? SlideTicks : PounceTick;
                float speed = 12f * (float)Math.Sin(Math.PI * (phaseTick - 0.5) / SlideTicks);
                Vel = new Vec2(MoveDir * speed, 0f);
                SlideAlongGround(room);
                if (Ground == null)
                {
                    EndMove(); // slid off an edge: just fall
                    return;
                }
                if (phaseTick >= length)
                {
                    if (Move == MoveKind.Slide)
                    {
                        Recover = RecoverTicks;
                        EndMove();
                        return;
                    }
                    // Pounce: the RocketJump out of the slide.
                    phase = 2;
                    phaseTick = 0;
                    Low = false;
                    Ground = null;
                    Vel = new Vec2(MoveDir * PounceSpeedX, PounceSpeedY);
                    Pos.Y += 0.5f;
                }
                return;
            }
            if (phase == 2)
            {
                // Airborne out of the slide: no air control, the game keeps the launch speed.
                StepAirborne(room);
                if (Ground != null)
                {
                    if (Move == MoveKind.Roll)
                    {
                        phase = 3;
                        phaseTick = 0;
                        Low = true;
                    }
                    else
                    {
                        EndMove();
                    }
                }
                return;
            }
            // Rolling after the landing, low, until the roll runs out.
            Vel = new Vec2(MoveDir * RollSpeed, 0f);
            SlideAlongGround(room);
            if (Ground == null || phaseTick >= RollTicks)
            {
                EndMove();
            }
        }

        private void StepChargedPounce(ArenaRoom room)
        {
            if (phase == 0)
            {
                // Crawling toward the target so the head leads, flat.
                if (Ground == null)
                {
                    EndMove();
                    StepAirborne(room);
                    return;
                }
                Low = true;
                Vel = new Vec2(MoveDir * CrawlSpeed, 0f);
                SlideAlongGround(room);
                if (Ground == null)
                {
                    EndMove();
                    return;
                }
                if (phaseTick >= CrawlTicks)
                {
                    phase = 1;
                    phaseTick = 0;
                }
                return;
            }
            if (phase == 1)
            {
                // Charging: still, flat, the jump held.
                if (phaseTick >= ChargeTicks)
                {
                    phase = 2;
                    phaseTick = 0;
                    Low = false;
                    Ground = null;
                    Vel = new Vec2(MoveDir * ChargedSpeedX, ChargedSpeedY);
                    Pos.Y += 0.5f;
                }
                return;
            }
            StepAirborne(room);
            if (Ground != null)
            {
                EndMove();
            }
        }

        private void StepFlipFamily(ArenaRoom room)
        {
            if (phase == 0)
            {
                // The run-up: walking at full speed toward the target.
                if (Ground == null)
                {
                    EndMove();
                    StepAirborne(room);
                    return;
                }
                Vel = new Vec2(MoveDir * WalkSpeed, 0f);
                SlideAlongGround(room);
                if (Ground == null)
                {
                    EndMove(); // ran off an edge
                    return;
                }
                if (phaseTick >= RunUpTicks)
                {
                    // The reversal and the jump: up, and a little back the way it came.
                    phase = 1;
                    phaseTick = 0;
                    Ground = null;
                    Vel = new Vec2(-MoveDir * FlipSpeedX, FlipSpeedY);
                    Pos.Y += 0.5f;
                }
                return;
            }
            FlipTick++;
            StepAirborne(room);
            if (Ground != null)
            {
                EndMove();
            }
        }

        /// <summary>Moves along the ground by Vel.X, stopping at crates and the room's edges, and drops off the surface's end.</summary>
        private void SlideAlongGround(ArenaRoom room)
        {
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
            if (!Ground.Spans(Pos.X))
            {
                Ground = null;
            }
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
                if (Move != MoveKind.None)
                {
                    EndMove(); // a hit ends whatever the body was doing
                }
            }
            if (ThrowCooldown > 0)
            {
                ThrowCooldown--;
            }
            if (Recover > 0)
            {
                Recover--;
            }
            if (Move != MoveKind.None)
            {
                StepMove(room);
                ClearInputs();
                return;
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
            // Consecutive ticks of running one way, for the backflip's run-up.
            runTicks = MoveX != 0 && MoveX == Facing ? runTicks + 1 : 0;
            Vel = new Vec2(MoveX * (Recover > 0 ? WalkSpeed * 0.5f : WalkSpeed), 0f);
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
            if (Move == MoveKind.None)
            {
                Vel.X = MoveX != 0 ? MoveX * WalkSpeed : Vel.X * 0.9f;
            }
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
            var p = new Projectile(Held, this, new Vec2(Pos.X + direction * (Radius + 4f), MainChunk.Y), new Vec2(direction * Projectile.Speed, 0f));
            Held = WeaponKind.None;
            ThrowCooldown = 10;
            Facing = direction;
            return p;
        }

        /// <summary>Lets go of the held weapon straight up or down (<paramref name="directionY"/> 1 or -1), as a flip throw does; a down throw goes through platforms.</summary>
        public Projectile ThrowVertical(int directionY)
        {
            int dy = directionY >= 0 ? 1 : -1;
            var p = new Projectile(Held, this, new Vec2(Pos.X, MainChunk.Y + dy * (Radius + 4f)), new Vec2(0f, dy * Projectile.Speed), throughPlatforms: true);
            Held = WeaponKind.None;
            ThrowCooldown = 10;
            return p;
        }

        public void Hurt(float damage, int stun)
        {
            Health -= damage;
            Stun = Math.Max(Stun, stun);
        }
    }
}
