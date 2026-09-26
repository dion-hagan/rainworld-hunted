using Hunted.Core;
using MoreSlugcats;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>
    /// The slugcat Pursuer's movement tech that the tactic policy chooses: the input sequences
    /// a player uses for a belly slide, a charged pounce, a slide pounce, a landing roll, a
    /// backflip and a flip throw, run on the real <see cref="Player"/> body. (Wall jumps, pole
    /// hops and ledge climbs are not chosen; the movement layer in <c>PursuerAI.Move</c> uses
    /// them whenever the path goes up.) Each move is a small state machine that feeds the body one
    /// <see cref="Player.InputPackage"/> per tick, watches the body's animation to see that
    /// the game took the step, and gives up (with a log line) when it did not. The tactic
    /// policy decides when a move is worth it; this class only knows how.
    ///
    /// The sequences follow <c>Player.Jump</c>, <c>Player.UpdateAnimation</c> and
    /// <c>Player.TerrainImpact</c>: a belly slide starts from the crouch (DownOnFours) with
    /// jump plus a down-diagonal in the facing direction; a jump in the slide's last ticks
    /// (rollCounter 12 to 15) is the pounce (a RocketJump); landing a pounce with the
    /// diagonal held rolls; a run of over ten ticks, one tick of the opposite direction (the
    /// skid) and a jump is the backflip; a throw during the flip with no x input and y held
    /// goes straight down (or up with the Remix "upwards spear throw" option); crawling with
    /// the jump held and no direction charges a pounce (superLaunchJump) that flies on release.
    /// </summary>
    internal sealed class PursuerMoves
    {
        /// <summary>The move running now, or null.</summary>
        public Tactic? Running { get; private set; }

        private int dir;
        private int throwY;
        private int phase;
        private int ticks;
        private int phaseTicks;
        /// <summary>The game was seen doing the move (sliding, flipping, airborne): an end after this is a finish, before it a failure.</summary>
        private bool pressed;
        private bool jumped;

        public bool Active => Running.HasValue;

        /// <summary>True when the body can start a move this tick: standing or crouched on the ground, nothing else going on.</summary>
        public static bool CanStart(Player cat)
        {
            if (cat == null || cat.room == null || !cat.Consious || cat.enteringShortCut.HasValue || cat.dead)
            {
                return false;
            }
            if (cat.bodyMode != Player.BodyModeIndex.Default && cat.bodyMode != Player.BodyModeIndex.Stand && cat.bodyMode != Player.BodyModeIndex.Crawl)
            {
                return false;
            }
            if (cat.animation != Player.AnimationIndex.None && cat.animation != Player.AnimationIndex.DownOnFours)
            {
                return false;
            }
            if (cat.bodyChunks[1].ContactPoint.y >= 0 || cat.room.PointSubmerged(cat.bodyChunks[1].pos))
            {
                return false;
            }
            return true;
        }

        /// <summary>Whether the game lets a flip throw go upward (Remix "upwards spear throw"); otherwise an up throw comes out level.</summary>
        public static bool UpwardThrowsAllowed()
        {
            try
            {
                return ModManager.MMF && MMF.cfgUpwardsSpearThrow != null && MMF.cfgUpwardsSpearThrow.Value;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Starts <paramref name="move"/> toward <paramref name="direction"/> (-1 or 1). For a flip
        /// throw, <paramref name="throwDirectionY"/> is 1 (up), -1 (down) or 0 (level at the top
        /// of the flip). Returns false when the body cannot start it now.
        /// </summary>
        public bool Start(Player cat, Tactic move, int direction, int throwDirectionY = 0)
        {
            if (!Tactics.IsMove(move) || !CanStart(cat))
            {
                return false;
            }
            Running = move;
            dir = direction >= 0 ? 1 : -1;
            throwY = throwDirectionY;
            phase = 0;
            ticks = 0;
            phaseTicks = 0;
            pressed = false;
            HuntedLog.Info("[move] " + move + (move == Tactic.FlipThrow ? (throwY > 0 ? " up" : throwY < 0 ? " down" : " level") : "") + " toward " + (dir > 0 ? "right" : "left") + " from " + cat.animation + "/" + cat.bodyMode);
            return true;
        }

        public void Cancel(string reason)
        {
            if (Running.HasValue)
            {
                HuntedLog.Info("[move] " + Running.Value + " cancelled: " + reason);
                End();
            }
        }

        /// <summary>
        /// Fills <paramref name="input"/> for this tick of the running move and returns true; returns
        /// false (input untouched) the tick the move ends, so the caller can move normally again.
        /// </summary>
        public bool Drive(Player cat, ref Player.InputPackage input)
        {
            if (!Running.HasValue)
            {
                return false;
            }
            ticks++;
            phaseTicks++;
            if (ticks > 160)
            {
                return Abort(cat, "gave up after 160 ticks");
            }
            bool running;
            switch (Running.Value)
            {
                case Tactic.Slide:
                case Tactic.SlidePounce:
                case Tactic.Roll:
                    running = SlideFamily(cat, ref input);
                    break;
                case Tactic.Pounce:
                    running = ChargedPounce(cat, ref input);
                    break;
                default:
                    running = FlipFamily(cat, ref input);
                    break;
            }
            if (running && cat.input[1].jmp)
            {
                // The game reads a jump press one update late and acts on it with this tick's
                // direction (Player.Update reads the edge before checkInput; Jump runs after): the
                // tick after every press repeats the press tick's direction, whatever the phase
                // wanted. inside AI.Update, input[1] is our previous output.
                input.x = cat.input[1].x;
                input.y = cat.input[1].y;
                input.downDiagonal = cat.input[1].downDiagonal;
            }
            return running;
        }

        // ------------------------------------------------------------------ charged pounce

        /// <summary>
        /// Crawl toward the target so the head leads, then hold the jump with no direction until
        /// the charge is full (superLaunchJump 20), release, and fly. Holding on past full kills
        /// the charge, so the release comes the tick the count is seen at 20.
        /// </summary>
        private bool ChargedPounce(Player cat, ref Player.InputPackage input)
        {
            switch (phase)
            {
                case 0:
                    input.x = dir;
                    input.y = -1;
                    input.downDiagonal = dir;
                    if (phaseTicks >= 4 && cat.bodyMode == Player.BodyModeIndex.Crawl)
                    {
                        Next();
                    }
                    else if (phaseTicks > 20)
                    {
                        return Abort(cat, "never got crawling");
                    }
                    return true;
                case 1:
                    if (cat.bodyMode != Player.BodyModeIndex.Crawl)
                    {
                        return Abort(cat, "left the crawl while charging");
                    }
                    if (cat.superLaunchJump >= 20)
                    {
                        Next(); // release this tick: the edge is the launch
                        return true;
                    }
                    input.jmp = true;
                    if (phaseTicks > 40)
                    {
                        return Abort(cat, "the charge never filled");
                    }
                    return true;
                default:
                    input.x = dir;
                    if (phaseTicks <= 3)
                    {
                        return true;
                    }
                    if (cat.bodyChunks[1].ContactPoint.y < 0 && cat.bodyMode != Player.BodyModeIndex.Default)
                    {
                        return pressed ? Finish("pounced") : Abort(cat, "the pounce did not leave the ground");
                    }
                    pressed = true; // airborne
                    if (phaseTicks > 80)
                    {
                        return Abort(cat, "never came down");
                    }
                    return true;
            }
        }

        // ------------------------------------------------------------------ slide, pounce, roll

        private bool SlideFamily(Player cat, ref Player.InputPackage input)
        {
            switch (phase)
            {
                case 0:
                    // Face the way we will slide (flipDirection follows x while standing).
                    input.x = dir;
                    if (phaseTicks >= 2)
                    {
                        Next();
                    }
                    return true;
                case 1:
                    // Crouch with the diagonal held. The body is on all fours the tick after down is
                    // pressed and only for three or four ticks before it is crawling (from which a
                    // jump is just a hop), so the launch is pressed the first tick all fours is seen;
                    // the game acts on it the update after, still on all fours, with the same inputs.
                    input.x = dir;
                    input.y = -1;
                    input.downDiagonal = dir;
                    if (cat.animation == Player.AnimationIndex.DownOnFours && cat.bodyChunks[1].ContactPoint.y < 0)
                    {
                        input.jmp = true;
                        Next();
                    }
                    else if (phaseTicks > 20)
                    {
                        return Abort(cat, "never got down on all fours");
                    }
                    return true;
                case 2:
                    // Sliding. Keep the diagonal so the slide is not cut short; a Slide finishes standing
                    // (up held at the end halves the recovery), a pounce jumps at rollCounter 12.
                    if (cat.animation != Player.AnimationIndex.BellySlide)
                    {
                        if (phaseTicks <= 4)
                        {
                            input.x = dir;
                            input.y = -1;
                            input.downDiagonal = dir;
                            return true; // the slide takes a tick or two to begin
                        }
                        if (Running == Tactic.Slide && pressed)
                        {
                            return Finish("slid");
                        }
                        if (Running != Tactic.Slide && pressed)
                        {
                            Next(); // the pounce left the ground
                            return AirFamily(cat, ref input);
                        }
                        return Abort(cat, "the slide did not start");
                    }
                    pressed = true;
                    input.x = dir;
                    if (Running == Tactic.Slide)
                    {
                        if (cat.rollCounter >= 13)
                        {
                            input.y = 1; // finish on our feet
                        }
                        else
                        {
                            input.y = -1;
                            input.downDiagonal = dir;
                        }
                        return true;
                    }
                    input.y = -1;
                    input.downDiagonal = dir;
                    if (cat.rollCounter >= 12 && !jumped)
                    {
                        input.jmp = true; // the pounce window: rollCounter 12 to 15
                        jumped = true;
                    }
                    return true;
                case 3:
                    return AirFamily(cat, ref input);
                default:
                    // Rolling: hold the diagonal for the first part, then let the roll run out.
                    if (cat.animation != Player.AnimationIndex.Roll)
                    {
                        if (phaseTicks <= 3)
                        {
                            input.x = dir;
                            input.y = -1;
                            input.downDiagonal = dir;
                            return true;
                        }
                        return Finish("rolled");
                    }
                    input.x = dir;
                    if (phaseTicks <= 16)
                    {
                        input.y = -1;
                        input.downDiagonal = dir;
                    }
                    return true;
            }
        }

        /// <summary>In the air out of the slide (the game's RocketJump): a pounce lands, a roll keeps the diagonal so the landing rolls.</summary>
        private bool AirFamily(Player cat, ref Player.InputPackage input)
        {
            input.x = dir;
            if (Running == Tactic.Roll)
            {
                // The diagonal must be held seven ticks before the landing for it to roll.
                input.y = -1;
                input.downDiagonal = dir;
                if (cat.animation == Player.AnimationIndex.Roll)
                {
                    phase = 4;
                    phaseTicks = 0;
                    return true;
                }
            }
            bool landed = cat.animation != Player.AnimationIndex.RocketJump && cat.bodyChunks[1].ContactPoint.y < 0 && phaseTicks > 3;
            if (landed)
            {
                if (Running == Tactic.Roll && phaseTicks <= 6)
                {
                    return true; // give the roll a moment to begin
                }
                return Finish(Running == Tactic.Roll ? "landed without rolling" : "pounced");
            }
            if (phaseTicks > 80)
            {
                return Abort(cat, "never came down");
            }
            return true;
        }

        // ------------------------------------------------------------------ backflip, flip throw

        private bool FlipFamily(Player cat, ref Player.InputPackage input)
        {
            switch (phase)
            {
                case 0:
                    // The run-up: the skid that a backflip starts from needs over ten ticks of running one way.
                    input.x = dir;
                    if (cat.initSlideCounter > 10 && Mathf.Abs(cat.mainBodyChunk.vel.x) > 1f && (cat.mainBodyChunk.vel.x > 0f) == (dir > 0) && cat.bodyMode == Player.BodyModeIndex.Stand)
                    {
                        Next();
                    }
                    else if (phaseTicks > 30)
                    {
                        return Abort(cat, "could not get a run-up");
                    }
                    return true;
                case 1:
                    // One tick the other way starts the skid.
                    input.x = -dir;
                    Next();
                    return true;
                case 2:
                    // Jump during the skid: the flip.
                    if (cat.slideCounter <= 0)
                    {
                        return Abort(cat, "no skid to flip from");
                    }
                    input.x = -dir;
                    input.jmp = true;
                    Next();
                    return true;
                default:
                    if (cat.animation != Player.AnimationIndex.Flip)
                    {
                        if (phaseTicks <= 3 && !pressed)
                        {
                            return true; // the flip takes a tick to begin
                        }
                        return pressed ? Finish(Running == Tactic.FlipThrow ? "flip threw" : "flipped") : Abort(cat, "the flip did not start");
                    }
                    pressed = true;
                    if (Running == Tactic.FlipThrow && phaseTicks == 5)
                    {
                        // While still rising: no x input makes a held y a vertical throw.
                        input.x = throwY == 0 ? dir : 0;
                        input.y = throwY;
                        input.thrw = true;
                        HuntedSession.Current?.Learner.NoteThrow();
                        HuntedLog.Info("[move] flip throw released " + (throwY > 0 ? "up" : throwY < 0 ? "down" : "level"));
                    }
                    return true;
            }
        }

        // ------------------------------------------------------------------ bookkeeping

        private void Next()
        {
            phase++;
            phaseTicks = 0;
        }

        private bool Finish(string what)
        {
            HuntedLog.Info("[move] " + Running.Value + " done: " + what + " in " + ticks + " ticks");
            End();
            return false;
        }

        private bool Abort(Player cat, string why)
        {
            HuntedLog.Info("[move] " + Running.Value + " given up: " + why + " (" + cat.animation + "/" + cat.bodyMode + ", tick " + ticks + ")");
            End();
            return false;
        }

        private void End()
        {
            Running = null;
            jumped = false;
            pressed = false;
        }
    }
}
