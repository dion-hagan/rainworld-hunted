using Hunted.Core;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace Hunted
{
    /// <summary>
    /// The mod's Remix options screen: a "Hunted" tab for the gameplay rules and
    /// a "Testing" tab with the hotkeys and overlay used to test the Pursuer
    /// without playing whole cycles.
    /// </summary>
    public class Options : OptionInterface
    {
        public static Options Instance;

        public readonly Configurable<int> HopsPerCycle;
        public readonly Configurable<int> StartDistance;
        public readonly Configurable<int> ArriveWithin;
        public readonly Configurable<int> RetreatHops;
        public readonly Configurable<int> RespawnCycles;
        public readonly Configurable<int> GraceSeconds;
        public readonly Configurable<bool> OffscreenUpgrades;
        public readonly Configurable<bool> HardMode;
        public readonly Configurable<bool> SlugcatBody;
        public readonly Configurable<bool> AdaptiveTactics;
        public readonly Configurable<KeyCode> KeyForget;

        public readonly Configurable<bool> ShowOverlay;
        public readonly Configurable<bool> DebugHotkeys;
        public readonly Configurable<KeyCode> KeySummon;
        public readonly Configurable<KeyCode> KeyBring;
        public readonly Configurable<KeyCode> KeyAdvance;
        public readonly Configurable<KeyCode> KeyGear;
        public readonly Configurable<KeyCode> KeyKill;
        public readonly Configurable<KeyCode> KeyOverlay;
        public readonly Configurable<KeyCode> KeyReset;
        public readonly Configurable<KeyCode> KeyForceMove;

        public Options()
        {
            HopsPerCycle = config.Bind("hopsPerCycle", 2, new ConfigurableInfo("How many shelters the Pursuer closes in by each cycle you survive.", new ConfigAcceptableRange<int>(1, 8)));
            StartDistance = config.Bind("startDistance", 6, new ConfigurableInfo("Minimum shelters between you and the Pursuer when it (re)spawns.", new ConfigAcceptableRange<int>(2, 20)));
            ArriveWithin = config.Bind("arriveWithin", 2, new ConfigurableInfo("Once the Pursuer is this many shelters away it enters your region as a real creature at the next cycle start.", new ConfigAcceptableRange<int>(0, 5)));
            RetreatHops = config.Bind("retreatHops", 4, new ConfigurableInfo("Shelters the Pursuer backs off when you die, so it never camps your spawn.", new ConfigAcceptableRange<int>(1, 12)));
            RespawnCycles = config.Bind("respawnCycles", 5, new ConfigurableInfo("Cycles after the Pursuer is killed before it respawns far away.", new ConfigAcceptableRange<int>(0, 30)));
            GraceSeconds = config.Bind("graceSeconds", 20, new ConfigurableInfo("Seconds after a cycle starts before the Pursuer starts moving toward you.", new ConfigAcceptableRange<int>(0, 180)));
            OffscreenUpgrades = config.Bind("offscreenUpgrades", true, new ConfigurableInfo("Let the Pursuer find rocks, spears and bombs while it travels between regions offscreen."));
            HardMode = config.Bind("hardMode", false, new ConfigurableInfo("Hard mode: the Pursuer respawns with the gear it died with."));
            SlugcatBody = config.Bind("slugcatBody", true, new ConfigurableInfo("Slugcat body (needs Downpour): the Pursuer is an AI-driven slugcat with Hunter's stats. Off: an elite scavenger."));
            AdaptiveTactics = config.Bind("adaptiveTactics", true, new ConfigurableInfo("Adaptive tactics (slugcat body): the Pursuer learns which fighting tactic works against you and remembers it per save slot and slugcat."));
            KeyForget = config.Bind("keyForget", KeyCode.F12, new ConfigurableInfo("Forget: throw away everything the Pursuer has learned about you on this save slot and slugcat."));

            ShowOverlay = config.Bind("showOverlay", true, new ConfigurableInfo("Show the Pursuer tracker line at the top of the screen."));
            DebugHotkeys = config.Bind("debugHotkeys", true, new ConfigurableInfo("Enable the testing hotkeys below."));
            KeySummon = config.Bind("keySummon", KeyCode.F5, new ConfigurableInfo("Summon: put the Pursuer in this region two rooms away from you, hunting immediately."));
            KeyBring = config.Bind("keyBring", KeyCode.F6, new ConfigurableInfo("Bring: make the Pursuer enter your current room through a pipe."));
            KeyAdvance = config.Bind("keyAdvance", KeyCode.F7, new ConfigurableInfo("Advance: apply one cycle of offscreen tracking right now (as if you had slept)."));
            KeyGear = config.Bind("keyGear", KeyCode.F8, new ConfigurableInfo("Gear: cycle the Pursuer's loadout (nothing, rock, spear, explosive spear, spear + bomb)."));
            KeyKill = config.Bind("keyKill", KeyCode.F9, new ConfigurableInfo("Kill: kill the Pursuer where it stands (drops its gear)."));
            KeyOverlay = config.Bind("keyOverlay", KeyCode.F10, new ConfigurableInfo("Toggle the tracker overlay."));
            KeyReset = config.Bind("keyReset", KeyCode.F11, new ConfigurableInfo("Reset: despawn the Pursuer and place it far away again, as at a new campaign."));
            KeyForceMove = config.Bind("keyForceMove", KeyCode.F4, new ConfigurableInfo("Force move: cycle a move the slugcat Pursuer must use while armed and engaging (slide, pounce, slide pounce, roll, backflip, flip throw, then off). Bypasses the learner; nothing is recorded."));
        }

        public TrackerConfig ToTrackerConfig()
        {
            return new TrackerConfig
            {
                HopsPerCycle = HopsPerCycle.Value,
                StarvedBonusHops = 1,
                ArriveWithinHops = ArriveWithin.Value,
                SpawnMinHops = StartDistance.Value,
                RetreatHops = RetreatHops.Value,
                RespawnCycles = RespawnCycles.Value,
                OffscreenUpgrades = OffscreenUpgrades.Value,
                KeepGearOnRespawn = HardMode.Value,
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            var gameplay = new OpTab(this, "Hunted");
            var testing = new OpTab(this, "Testing");
            Tabs = new[] { gameplay, testing };

            float y = 560f;
            gameplay.AddItems(new OpLabel(new Vector2(20f, y), new Vector2(560f, 30f), "Hunted", FLabelAlignment.Left, true));
            y -= 40f;
            gameplay.AddItems(new OpLabel(new Vector2(20f, y), new Vector2(560f, 20f), "A second slugcat is hunting you. Every cycle you survive it moves closer along the world's shelters.", FLabelAlignment.Left));
            y -= 40f;
            AddNumber(gameplay, ref y, HopsPerCycle, "Shelters per cycle");
            AddNumber(gameplay, ref y, StartDistance, "Spawn distance (shelters)");
            AddNumber(gameplay, ref y, ArriveWithin, "Arrives when within (shelters)");
            AddNumber(gameplay, ref y, RetreatHops, "Retreat after your death (shelters)");
            AddNumber(gameplay, ref y, RespawnCycles, "Respawn delay (cycles)");
            AddNumber(gameplay, ref y, GraceSeconds, "Grace period at cycle start (seconds)");
            AddToggle(gameplay, ref y, SlugcatBody, "Slugcat body (Stage 2, needs Downpour; off = elite scavenger)");
            AddToggle(gameplay, ref y, OffscreenUpgrades, "Offscreen gear upgrades");
            AddToggle(gameplay, ref y, HardMode, "Hard mode: keeps gear on respawn");
            AddToggle(gameplay, ref y, AdaptiveTactics, "Adaptive tactics: learns what works against you");
            var forget = new OpSimpleButton(new Vector2(20f, y), new Vector2(200f, 28f), "Forget learned tactics") { description = "Deletes what the Pursuer has learned about you, for every save slot and slugcat. An installed arena baseline stays." };
            var forgetLabel = new OpLabel(new Vector2(235f, y + 4f), new Vector2(320f, 24f), "", FLabelAlignment.Left);
            forget.OnClick += _ => forgetLabel.text = "Deleted " + Game.PursuerLearner.ForgetAll() + " file(s).";
            gameplay.AddItems(forget, forgetLabel);
            y -= 40f;

            y = 560f;
            testing.AddItems(new OpLabel(new Vector2(20f, y), new Vector2(560f, 30f), "Testing", FLabelAlignment.Left, true));
            y -= 40f;
            testing.AddItems(new OpLabel(new Vector2(20f, y), new Vector2(560f, 20f), "Hotkeys work in any story game. Watch the overlay and BepInEx/LogOutput.log for what happens.", FLabelAlignment.Left));
            y -= 40f;
            AddToggle(testing, ref y, ShowOverlay, "Show tracker overlay");
            AddToggle(testing, ref y, DebugHotkeys, "Enable testing hotkeys");
            AddKey(testing, ref y, KeySummon, "Summon into this region (2 rooms away)");
            AddKey(testing, ref y, KeyBring, "Bring it into my room");
            AddKey(testing, ref y, KeyAdvance, "Advance one cycle of tracking");
            AddKey(testing, ref y, KeyGear, "Cycle its loadout");
            AddKey(testing, ref y, KeyKill, "Kill it");
            AddKey(testing, ref y, KeyOverlay, "Toggle overlay");
            AddKey(testing, ref y, KeyReset, "Reset it far away");
            AddKey(testing, ref y, KeyForget, "Forget learned tactics");
            AddKey(testing, ref y, KeyForceMove, "Force a move (cycles)");
        }

        private static void AddNumber(OpTab tab, ref float y, Configurable<int> cfg, string label)
        {
            var updown = new OpUpdown(cfg, new Vector2(20f, y), 80f) { description = cfg.info.description };
            tab.AddItems(updown, new OpLabel(new Vector2(115f, y + 3f), new Vector2(440f, 24f), label, FLabelAlignment.Left) { description = cfg.info.description });
            y -= 36f;
        }

        private static void AddToggle(OpTab tab, ref float y, Configurable<bool> cfg, string label)
        {
            var box = new OpCheckBox(cfg, new Vector2(20f, y)) { description = cfg.info.description };
            tab.AddItems(box, new OpLabel(new Vector2(55f, y + 2f), new Vector2(500f, 24f), label, FLabelAlignment.Left) { description = cfg.info.description });
            y -= 36f;
        }

        private static void AddKey(OpTab tab, ref float y, Configurable<KeyCode> cfg, string label)
        {
            var binder = new OpKeyBinder(cfg, new Vector2(20f, y), new Vector2(130f, 30f), false) { description = cfg.info.description };
            tab.AddItems(binder, new OpLabel(new Vector2(165f, y + 5f), new Vector2(400f, 24f), label, FLabelAlignment.Left) { description = cfg.info.description });
            y -= 40f;
        }
    }
}
