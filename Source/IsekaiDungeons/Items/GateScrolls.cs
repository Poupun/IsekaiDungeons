using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The gate summoning scrolls as loot. One ThingDef per wild-gate rank,
    /// named IsekaiDungeons_GateScroll_{RANK}; Nation has none (a Nation gate
    /// only ever comes as a guild contract), so a Nation source pays an SSS scroll.
    ///
    /// Three faucets, each a setting on the Rewards tab:
    ///   - a dungeon clear (DungeonLootGenerator.GenerateClearLoot)
    ///   - a treasure side room (GenerateTreasureLoot)
    ///   - a quest reward: base-mod hunts and bounties, and Isekai Creatures
    ///     quests when installed (Patches/Patch_GateScrollQuestLoot.cs)
    /// </summary>
    public static class GateScrolls
    {
        private static readonly ThingDef[] byRank = new ThingDef[(int)MobRankTier.SSS + 1];
        private static bool resolved;

        public static ThingDef DefFor(MobRankTier rank)
        {
            if (!resolved)
            {
                for (int i = 0; i < byRank.Length; i++)
                    byRank[i] = DefDatabase<ThingDef>.GetNamedSilentFail(
                        "IsekaiDungeons_GateScroll_" + ((MobRankTier)i).ToString());
                resolved = true;
            }
            int idx = Math.Max(0, Math.Min((int)rank, (int)MobRankTier.SSS));
            return byRank[idx];
        }

        /// <summary>Rolls <paramref name="chance"/>; on success adds one scroll of that rank to the pile.</summary>
        public static bool TryAdd(List<Thing> loot, MobRankTier rank, float chance)
        {
            if (loot == null || chance <= 0f || !Rand.Chance(chance))
                return false;
            ThingDef def = DefFor(rank);
            if (def == null)
                return false;
            loot.Add(ThingMaker.MakeThing(def));
            return true;
        }
    }
}
