using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Primary spawn provider: the Isekai Creatures add-on's bestiary, accessed
    /// via reflection ONLY (soft dependency, house convention — same pattern as
    /// IsekaiCreatures' own GuildQuestBoardPatchShared and CompForceMobRank).
    ///
    /// The Creatures add-on keeps its entire taxonomy in one static registry:
    /// <c>IsekaiCreatures.IsekaiCreatureRanks.ForcedRanks</c>, a
    /// <c>Dictionary&lt;string, MobRankTier&gt;</c> mapping ThingDef defName →
    /// forced rank. Two facts make this seam sound:
    ///
    ///   1. The VALUE type, MobRankTier, lives in IsekaiLeveling.dll which we
    ///      hard-reference — so once the dictionary object is obtained via a
    ///      reflected FieldInfo, a direct cast to
    ///      Dictionary&lt;string, MobRankTier&gt; is type-identical, no
    ///      per-entry boxing/unboxing dance needed.
    ///   2. In that add-on every creature's PawnKindDef defName is IDENTICAL
    ///      to its ThingDef defName (verified across all its Races XML), so
    ///      the ThingDef-keyed dictionary doubles as a PawnKindDef index via
    ///      DefDatabase&lt;PawnKindDef&gt;.GetNamedSilentFail.
    ///
    /// Fail-open contract: if the add-on is absent or its internal API changed
    /// (type gone, field renamed, unexpected runtime type), <see cref="Available"/>
    /// latches false for the whole session after a single warning log, and the
    /// resolver silently falls through to SpawnProvider_BaseMod. A broken soft
    /// dep must never cost the player their dungeon (G5).
    /// </summary>
    public class SpawnProvider_IsekaiCreatures : IDungeonSpawnProvider
    {
        /// <summary>Tier-indexed candidate lists, index = (int)MobRankTier (0=F … 9=Nation).
        /// Each list is sorted ascending by combatPower so "cheapest" is [0]. Built once.</summary>
        private List<PawnKindDef>[] byTier;

        /// <summary>Latched after the first resolution attempt — reflection runs exactly once.</summary>
        private bool resolved;
        private bool ready;

        /// <summary>Scratch list reused by TryPickCreature. Providers are only called from
        /// generation-time / break-time code on the main thread, so a shared buffer is safe.</summary>
        private readonly List<PawnKindDef> fitScratch = new List<PawnKindDef>();

        public bool Available
        {
            get
            {
                if (!resolved)
                    Resolve();
                return ready;
            }
        }

        /// <summary>
        /// One-shot reflection + index build. Every failure path logs once and
        /// leaves <see cref="ready"/> false forever — later calls are a single
        /// bool check.
        /// </summary>
        private void Resolve()
        {
            resolved = true;
            ready = false;

            try
            {
                Type ranksType = AccessTools.TypeByName("IsekaiCreatures.IsekaiCreatureRanks");
                if (ranksType == null)
                {
                    // Not installed — the normal case for most players. Message (not
                    // Warning): absence of an optional add-on is not a problem.
                    Log.Message($"{IsekaiDungeonsMod.LogPrefix} Isekai Creatures not detected; dungeon spawns will use the base-mod bestiary.");
                    return;
                }

                FieldInfo ranksField = AccessTools.Field(ranksType, "ForcedRanks");
                if (ranksField == null)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Isekai Creatures found but IsekaiCreatureRanks.ForcedRanks is missing (internal API changed?); falling back to the base-mod bestiary.");
                    return;
                }

                // Sound cast: MobRankTier is defined in the hard-referenced
                // IsekaiLeveling assembly, so the dictionary's generic instantiation
                // is the same runtime type on both sides of the reflection boundary.
                var forcedRanks = ranksField.GetValue(null) as Dictionary<string, MobRankTier>;
                if (forcedRanks == null)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} IsekaiCreatureRanks.ForcedRanks has an unexpected runtime type; falling back to the base-mod bestiary.");
                    return;
                }

                // Build the tier index. PawnKindDef defNames == ThingDef defNames in
                // that add-on; any entry whose PawnKindDef can't be resolved is
                // skipped silently (a future creature family might ship ThingDefs
                // without kinds — never let one odd entry poison the rest).
                var lists = new List<PawnKindDef>[(int)MobRankTier.Nation + 1];
                for (int i = 0; i < lists.Length; i++)
                    lists[i] = new List<PawnKindDef>();

                int indexed = 0;
                foreach (KeyValuePair<string, MobRankTier> entry in forcedRanks)
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(entry.Key);
                    if (kind == null)
                        continue;

                    int tierIdx = (int)entry.Value;
                    if (tierIdx < 0 || tierIdx >= lists.Length)
                        continue; // future enum member from a newer main mod — skip, don't crash

                    lists[tierIdx].Add(kind);
                    indexed++;
                }

                // Sort each tier ascending by combatPower: cheapest-fit fallback is
                // [0], boss preference scans from the tail. Comparison delegate
                // allocates once per list at build time only.
                for (int i = 0; i < lists.Length; i++)
                    lists[i].Sort((a, b) => a.combatPower.CompareTo(b.combatPower));

                byTier = lists;
                ready = indexed > 0;

                if (ready)
                    Log.Message($"{IsekaiDungeonsMod.LogPrefix} Isekai Creatures bestiary active for dungeon spawns ({indexed} creature kinds indexed across ranks).");
                else
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Isekai Creatures rank table resolved but no PawnKindDefs matched; falling back to the base-mod bestiary.");
            }
            catch (Exception ex)
            {
                // Absolute fail-open: any surprise here downgrades to the base-mod
                // provider instead of aborting dungeon generation.
                ready = false;
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Isekai Creatures spawn provider failed to initialize; falling back to the base-mod bestiary. {ex.GetType().Name}: {ex.Message}");
            }
        }

        public bool TryPickCreature(MobRankTier tier, float budgetLeft, out PawnKindDef kind)
        {
            kind = null;
            if (!Available)
                return false;

            List<PawnKindDef> candidates = byTier[(int)tier];
            if (candidates.Count == 0)
                return false; // this tier genuinely has no add-on creatures — let the resolver fall through

            // Prefer a random candidate that fits inside the remaining room budget
            // (variety over optimality — dungeons should not degenerate into N
            // copies of the single best-value creature).
            fitScratch.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (DungeonSpawnResolver.CombatPowerOf(candidates[i]) <= budgetLeft)
                    fitScratch.Add(candidates[i]);
            }

            if (fitScratch.Count > 0)
            {
                kind = fitScratch[Rand.Range(0, fitScratch.Count)];
                return true;
            }

            // Nothing fits the leftover budget: hand back the cheapest at-tier kind
            // anyway. The CALLER decides whether an over-budget pick still spawns
            // (it does for the guaranteed first creature of a room — G3) or ends
            // the room's fill loop.
            kind = candidates[0]; // lists are sorted ascending by combatPower
            return true;
        }

        public PawnKindDef PickBoss(MobRankTier rank)
        {
            if (!Available)
                return null;

            List<PawnKindDef> candidates = byTier[(int)rank];
            if (candidates.Count == 0)
                return null;

            // A boss should be ONE big thing, so prefer the highest combatPower at
            // exactly the gate rank. Several kinds often share the top value (e.g.
            // all six A-rank wyverns at 330), so pick randomly among everything
            // within a small tolerance of the max instead of always the same kind.
            float maxPower = DungeonSpawnResolver.CombatPowerOf(candidates[candidates.Count - 1]);
            float threshold = maxPower * 0.99f;

            int firstTopIdx = candidates.Count - 1;
            while (firstTopIdx > 0 && DungeonSpawnResolver.CombatPowerOf(candidates[firstTopIdx - 1]) >= threshold)
                firstTopIdx--;

            return candidates[Rand.RangeInclusive(firstTopIdx, candidates.Count - 1)];
        }
    }
}
