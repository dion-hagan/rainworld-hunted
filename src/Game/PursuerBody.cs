namespace Hunted.Game
{
    /// <summary>Which creature the Pursuer wears this cycle.</summary>
    public enum PursuerBody
    {
        /// <summary>Stage 1: an elite scavenger driven by the vanilla scavenger AI plus a few hooks.</summary>
        EliteScavenger,
        /// <summary>Stage 2: a real slugcat body (Downpour's NPC slugcat) driven by <see cref="PursuerAI"/>.</summary>
        Slugcat,
    }

    internal static class PursuerBodies
    {
        /// <summary>The body the options ask for, falling back to the scavenger when Downpour is not enabled.</summary>
        public static PursuerBody Selected()
        {
            bool wantSlugcat = Options.Instance == null || Options.Instance.SlugcatBody.Value;
            if (wantSlugcat && SlugcatAvailable())
            {
                return PursuerBody.Slugcat;
            }
            return PursuerBody.EliteScavenger;
        }

        public static bool SlugcatAvailable()
        {
            return ModManager.MSC && MoreSlugcats.MoreSlugcatsEnums.CreatureTemplateType.SlugNPC != null && StaticWorld.GetCreatureTemplate(MoreSlugcats.MoreSlugcatsEnums.CreatureTemplateType.SlugNPC) != null;
        }

        public static bool IsSlugcat(AbstractCreature creature)
        {
            return creature != null && ModManager.MSC && creature.creatureTemplate.TopAncestor().type == MoreSlugcats.MoreSlugcatsEnums.CreatureTemplateType.SlugNPC;
        }

        public static string Name(PursuerBody body)
        {
            return body == PursuerBody.Slugcat ? "slugcat" : "elite scavenger";
        }
    }
}
