using System;
using System.Collections.Generic;
using HarmonyLib;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What rank a creature ACTUALLY IS, as authored — not what a dungeon would
    /// like it to be.
    ///
    /// Isekai Creatures ships an explicit rank per creature in
    /// IsekaiCreatureRanks.ForcedRanks, and that table is the truth about the
    /// bestiary: a valkyrie is SSS, an archangel is Nation, a blue slime is F.
    /// Nothing else in this add-on is entitled to disagree with it.
    ///
    /// THIS EXISTS BECAUSE THE OLD ANSWER WAS WRONG IN BOTH DIRECTIONS.
    /// Selection used to derive a creature's rank from combatPower via
    /// MobRankUtility.CalculateBaseRankFromThreatScore, which TOPS OUT AT S —
    /// every creature above S therefore measured as "S", so a valkyrie (SSS)
    /// and an archangel (Nation) were indistinguishable from a goblin king, and
    /// the band filter could not tell them apart. The dungeon then "fixed" the
    /// mismatch at the other end by raising whatever it spawned to the floor's
    /// rank, which is how the tower ended up serving Nation-rank valkyries.
    ///
    /// Placement, not promotion: a creature goes where its rank says it belongs.
    ///
    /// Deliberately separate from SpawnProvider_IsekaiCreatures even though that
    /// class reflects the same field. The provider's index is built lazily on
    /// its own lifecycle and is keyed by tier for picking; this is keyed by kind
    /// for asking, is needed by callers that never touch a provider (the roster
    /// filter, the rank normalizer), and must answer correctly no matter which
    /// of them runs first. A dozen lines of duplicated reflection is a smaller
    /// cost than an ordering dependency between them.
    /// </summary>
    public static class CreatureRankRegistry
    {
        private static Dictionary<PawnKindDef, MobRankTier> byKind;
        private static bool resolved;

        /// <summary>True once the add-on's table has been read (or found absent).</summary>
        public static bool Available
        {
            get
            {
                Resolve();
                return byKind != null && byKind.Count > 0;
            }
        }

        private static void Resolve()
        {
            if (resolved)
                return;
            resolved = true;

            try
            {
                Type ranksType = AccessTools.TypeByName("IsekaiCreatures.IsekaiCreatureRanks");
                if (ranksType == null)
                    return;   // add-on absent — the normal case, and not a problem

                var table = AccessTools.Field(ranksType, "ForcedRanks")?.GetValue(null)
                            as Dictionary<string, MobRankTier>;
                if (table == null)
                    return;

                var map = new Dictionary<PawnKindDef, MobRankTier>();
                foreach (KeyValuePair<string, MobRankTier> e in table)
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(e.Key);
                    if (kind != null)
                        map[kind] = e.Value;
                }
                byKind = map;
            }
            catch (Exception ex)
            {
                // Fail open exactly like every other spawn-side lookup: without
                // the table, callers fall back to the combatPower estimate.
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not read the Isekai Creatures rank table; "
                          + $"creature placement will fall back to combat-power estimates. {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The rank this kind was AUTHORED at, when someone authored one.
        ///
        /// False means nobody ever declared a rank for this creature — a vanilla
        /// animal, or a kind from a bestiary that has no rank table. Those have
        /// no natural rank to respect, only a rolled one, which is why they are
        /// the only creatures this add-on will still normalize upward.
        /// </summary>
        public static bool TryGetAuthored(PawnKindDef kind, out MobRankTier tier)
        {
            tier = MobRankTier.F;
            if (kind == null)
                return false;
            Resolve();
            return byKind != null && byKind.TryGetValue(kind, out tier);
        }

        /// <summary>Convenience for the pawn side of the same question.</summary>
        public static bool TryGetAuthored(Pawn pawn, out MobRankTier tier)
        {
            tier = MobRankTier.F;
            return pawn != null && TryGetAuthored(pawn.kindDef, out tier);
        }
    }
}
