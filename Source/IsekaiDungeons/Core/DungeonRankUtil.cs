using IsekaiLeveling.MobRanking;
using IsekaiLeveling.Quests;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Rank plumbing shared by the incident, gensteps and loot code. One rank
    /// currency everywhere: IsekaiLeveling.MobRanking.MobRankTier (F…Nation).
    /// QuestRank (which has no Nation member) only appears at the base-mod API
    /// boundary, collapsed via ToQuestRank — the same convention the Creatures
    /// add-on uses for the guild board.
    /// </summary>
    public static class DungeonRankUtil
    {
        /// <summary>
        /// Rolls the rank of a LOCAL gate: one that tears itself open on the
        /// colony's own map, unasked.
        ///
        /// DELIBERATELY BELOW THE COLONY'S CEILING, by a gap that widens as the
        /// colony climbs (DungeonTunables.LocalRankTopByColonyRank), weighted
        /// toward the bottom of that window. This used to run cap-3 to
        /// cap with the weight ON the cap, which meant a colony with an SSS
        /// colonist saw an SSS gate roughly two times in five and a Nation gate
        /// one time in twenty, every few days, on its doorstep. Each one was a
        /// full expedition's worth of obligation that the player never asked
        /// for, and the reasonable response to that pressure was to stop
        /// engaging with gates at all.
        ///
        /// So the two sources of dungeons now have different jobs. A local gate
        /// is BACKGROUND TEXTURE: something a standing colony can put down
        /// without planning a trip, worth clearing for the cores. Anything at
        /// or above the colony's own rank arrives through the guild instead, as
        /// a quest the player chooses to accept, at a place they travel to.
        /// That is also why no red gate can spawn here any more: a rank above
        /// the cap is the definition of an expedition, and expeditions are
        /// opt-in now.
        ///
        /// Cap still comes from RaidRankSystem.GetColonyBestRank, the same
        /// public API raids use, so gates track the colony's actual power
        /// rather than its wealth.
        /// </summary>
        public static MobRankTier RollDungeonRank(Map colonyMap, DungeonKindDef def)
        {
            MobRankTier cap = RaidRankSystem.GetColonyBestRank(colonyMap);

            int capIdx = Mathf.Clamp((int)cap, 0, DungeonTunables.LocalRankTopByColonyRank.Length - 1);
            // The rank shift setting moves the window, but never past SSS. A
            // Nation gate is an expedition by definition, so it only ever
            // arrives as a guild contract (DungeonContractFactory), never on
            // the colony's doorstep.
            int maxIdx = Mathf.Clamp(DungeonTunables.LocalRankTopByColonyRank[capIdx] + DungeonTunables.LocalRankShift,
                                     0, (int)MobRankTier.SSS);
            int minIdx = Mathf.Max(0, maxIdx - DungeonTunables.LocalRankWindowSpan);

            // Weighted toward minIdx: within an already-lowered window, the
            // quieter end should still be the common case.
            int totalWeight = 0;
            for (int i = minIdx; i <= maxIdx; i++)
                totalWeight += WeightFor(i - minIdx);

            int pick = Rand.RangeInclusive(1, totalWeight);
            for (int i = minIdx; i <= maxIdx; i++)
            {
                pick -= WeightFor(i - minIdx);
                if (pick <= 0)
                    return (MobRankTier)i;
            }
            return (MobRankTier)minIdx;   // unreachable, defensive
        }

        /// <summary>
        /// Weight by distance ABOVE the window floor.
        ///
        /// NOW LEANS UP, mildly. It used to be 40/30/20 toward the floor, which
        /// on top of an already-lowered ceiling meant the single most likely
        /// local gate was two ranks under the cap - the quiet end of a quiet
        /// window, twice. 30/34/36 makes the top of the window the common case
        /// without turning the window itself into a threat: the ceiling is
        /// still below the colony's own rank, so the worst outcome here is
        /// still something a standing colony can put down.
        /// </summary>
        private static int WeightFor(int distanceAboveFloor)
        {
            switch (distanceAboveFloor)
            {
                case 0: return 30;
                case 1: return 34;
                case 2: return 36;
                default: return 20;
            }
        }

        /// <summary>
        /// Base-mod API boundary: QuestRank has no Nation member, so Nation
        /// collapses to SSS (display/selection only — power scaling keeps
        /// using the MobRankTier).
        /// </summary>
        public static QuestRank ToQuestRank(MobRankTier tier)
        {
            if (tier >= MobRankTier.SSS)
                return QuestRank.SSS;
            return (QuestRank)(int)tier;
        }

        /// <summary>Rank tier clamped stepping — used by the room-tier ramp.</summary>
        public static MobRankTier ClampTier(int tierIndex)
        {
            return (MobRankTier)Mathf.Clamp(tierIndex, (int)MobRankTier.F, (int)MobRankTier.Nation);
        }

        /// <summary>Colored rank string for letters/inspect ("S", gold), via the base-mod palette.</summary>
        public static string ColoredRankString(MobRankTier tier)
        {
            string label = MobRankUtility.GetRankString(tier);
            Color c = MobRankUtility.GetRankColor(tier);
            return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{label}</color>";
        }
    }
}
