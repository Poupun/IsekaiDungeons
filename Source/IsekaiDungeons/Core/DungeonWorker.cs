using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Stateless per-archetype strategy. The base implementation IS the Dungeon
    /// Break behavior; future archetypes override the hooks they need and are
    /// wired in via DungeonKindDef.workerClass. Instances are cached on the def
    /// and must remain stateless — persistent state belongs to the gate
    /// building, the DungeonMapParent, or the DungeonMapComponent, all of which
    /// are properly scribed.
    /// </summary>
    public class DungeonWorker
    {
        /// <summary>Rolls the rank of a freshly spawned gate for the given colony map.</summary>
        public virtual MobRankTier RollRank(Map colonyMap, DungeonKindDef def)
        {
            return DungeonRankUtil.RollDungeonRank(colonyMap, def);
        }

        /// <summary>Called once when the boss is defeated (after loot + XP are handed out).</summary>
        public virtual void OnCleared(Building_DungeonGate gate)
        {
        }

        /// <summary>Called once when the countdown expires and the break fires.</summary>
        public virtual void OnBreak(Building_DungeonGate gate)
        {
        }
    }
}
