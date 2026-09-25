using System.Runtime.CompilerServices;

namespace Hunted.Game
{
    /// <summary>
    /// Identity of the Pursuer creature, available from the moment the abstract
    /// creature is constructed (before it is realized or handed to the session),
    /// so hooks that run inside Realize/InitiateAI can recognize it.
    /// </summary>
    internal static class PursuerMark
    {
        private static readonly ConditionalWeakTable<AbstractCreature, object> marks = new ConditionalWeakTable<AbstractCreature, object>();
        private static readonly object token = new object();

        public static void Mark(AbstractCreature creature)
        {
            if (creature == null)
            {
                return;
            }
            marks.Remove(creature);
            marks.Add(creature, token);
        }

        public static void Unmark(AbstractCreature creature)
        {
            if (creature != null)
            {
                marks.Remove(creature);
            }
        }

        public static bool IsMarked(AbstractCreature creature)
        {
            return creature != null && marks.TryGetValue(creature, out _);
        }
    }
}
