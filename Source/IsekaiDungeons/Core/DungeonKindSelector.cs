using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Picks WHICH dungeon opens, once the rank is already rolled.
    ///
    /// Selection is deliberately rank-first: DungeonRankUtil decides how hard
    /// the gate is (from colony power), and only then do we ask which dungeons
    /// exist at that difficulty. That ordering keeps the threat curve honest no
    /// matter how lopsided the catalogue is — adding ten new F-rank dungeons
    /// must never make a strong colony start seeing easy gates.
    /// </summary>
    public static class DungeonKindSelector
    {
        private static readonly List<DungeonKindDef> tmpCandidates = new List<DungeonKindDef>();

        /// <summary>
        /// How far a vanilla-backbone dungeon is stepped down when themed
        /// content exists at the same rank.
        ///
        /// A third, not zero. These are good encounters and the variety is
        /// worth keeping; they simply should not be half of what a player with
        /// the add-ons installed ever sees. A setting (Advanced tab).
        /// </summary>
        private static float BackboneWeightFactor => IsekaiDungeonsSettings.Current.backboneWeightFactor;

        /// <summary>
        /// A dungeon valid at this rank, weighted by selectionWeight. Falls back
        /// to the nearest-rank available dungeon, then to any available dungeon
        /// at all — a rolled rank must never fail to produce a gate after the
        /// incident already committed to firing (G5).
        /// </summary>
        public static DungeonKindDef SelectForRank(MobRankTier rank)
        {
            List<DungeonKindDef> all = DefDatabase<DungeonKindDef>.AllDefsListForReading;

            tmpCandidates.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                DungeonKindDef d = all[i];
                if (!d.towerOnly && d.AllowsRank(rank) && d.Available)
                    tmpCandidates.Add(d);
            }

            if (tmpCandidates.Count > 0)
            {
                // Is there anything at this rank that is NOT a stand-in for
                // missing content? If so the stand-ins step aside; if not they
                // are all we have and they keep full weight. This is what makes
                // the same catalogue read correctly both for a player with every
                // add-on and for one with none.
                bool themedAvailable = false;
                for (int i = 0; i < tmpCandidates.Count; i++)
                {
                    if (!tmpCandidates[i].vanillaBackbone)
                    {
                        themedAvailable = true;
                        break;
                    }
                }

                DungeonKindDef picked = tmpCandidates.RandomElementByWeight(
                    d => themedAvailable && d.vanillaBackbone
                        ? d.selectionWeight * BackboneWeightFactor
                        : d.selectionWeight);
                tmpCandidates.Clear();
                return picked;
            }

            // Nothing authored (or nothing installed) at this exact rank: take
            // the available dungeon whose band is closest, so an odd rank still
            // opens something sensible instead of nothing.
            DungeonKindDef nearest = null;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                DungeonKindDef d = all[i];
                // The nearest-band fallback must not leak a tower floor onto
                // the colony map either.
                if (d.towerOnly || !d.Available)
                    continue;
                int distance = (int)rank < (int)d.minRank
                    ? (int)d.minRank - (int)rank
                    : (int)rank - (int)d.maxRank;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = d;
                }
            }

            if (nearest != null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No dungeon authored for rank {rank}; " +
                            $"falling back to '{nearest.defName}' ({nearest.minRank}-{nearest.maxRank}).");
                return nearest;
            }

            Log.Error($"{IsekaiDungeonsMod.LogPrefix} No available DungeonKindDef at all — check that the mod's Defs loaded.");
            return null;
        }
    }
}
