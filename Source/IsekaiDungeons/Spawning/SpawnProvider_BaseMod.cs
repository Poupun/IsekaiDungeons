using IsekaiLeveling.MobRanking;
using IsekaiLeveling.Quests;
using System.Collections.Generic;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Fallback spawn provider: the base mod's own rank-banded creature
    /// selection, <c>IncidentWorker_IsekaiHunt.SelectCreatureForRank</c>
    /// (public static, hard reference — always available).
    ///
    /// Semantics of that call, verified against the base-mod source:
    ///   - Selection is driven purely by RANK — each QuestRank maps to a fixed
    ///     combatPower band (F/E ≤35 … SSS 200+) over the whole huntable
    ///     DefDatabase (animals/mechs/insects, minus vehicles, Anomaly
    ///     entities, RoM creatures, blacklisted mods…).
    ///   - The <c>points</c> parameter is declared (<c>float points = 0f</c>)
    ///     but NOT read by the current implementation. We still pass our
    ///     budget/points through so that if a future base-mod version starts
    ///     honoring it, dungeons pick it up for free — but we must NOT rely on
    ///     it for budget fitting. Budget discipline therefore lives entirely
    ///     in the resolver/GenStep loop (they check CombatPowerOf after the
    ///     pick and stop when the room budget is spent).
    ///   - Returns null only when the whole filtered DefDatabase pool is
    ///     empty (pathological mod lists); it internally relaxes its band
    ///     before giving up, and logs its own selection message.
    ///
    /// MobRankTier → QuestRank goes through DungeonRankUtil.ToQuestRank
    /// (Nation collapses to SSS — QuestRank has no Nation member; the pawn's
    /// actual power is restored later by NormalizeRank at the real tier).
    /// </summary>
    public class SpawnProvider_BaseMod : IDungeonSpawnProvider
    {
        /// <summary>Hard-referenced base-mod API — always usable.</summary>
        public bool Available => true;

        /// <summary>
        /// Generous points value for boss picks. SelectCreatureForRank ignores
        /// points today (see class remarks); if a future version uses it as an
        /// upper bound, this keeps the boss pool wide open at the rank's band.
        /// </summary>
        private const float BossPoints = 10000f;

        /// <summary>
        /// Sampled pool per rank. IncidentWorker_IsekaiHunt.SelectCreatureForRank
        /// re-runs the base mod's whole huntable filter on every call — a LINQ
        /// Where over the entire PawnKindDef database with ~11 predicates, then
        /// an OrderBy and a ToList. The population GenStep calls it once per
        /// spawned creature, so a big dungeon paid for ~100 full database scans
        /// in a single map generation (a visible generation stall, though not a
        /// per-tick cost). Sampling the band a few times and drawing from the
        /// result keeps the same selection behaviour for a fraction of the work.
        /// Def identity is stable for the process lifetime, so this never needs
        /// invalidating.
        /// </summary>
        private readonly Dictionary<QuestRank, List<PawnKindDef>> poolByRank = new Dictionary<QuestRank, List<PawnKindDef>>();

        private const int PoolSamples = 12;

        private List<PawnKindDef> PoolFor(QuestRank rank)
        {
            if (poolByRank.TryGetValue(rank, out List<PawnKindDef> pool))
                return pool;

            pool = new List<PawnKindDef>();
            for (int i = 0; i < PoolSamples; i++)
            {
                PawnKindDef k = IncidentWorker_IsekaiHunt.SelectCreatureForRank(rank, 0f);
                if (k != null && !pool.Contains(k))
                    pool.Add(k);
            }
            poolByRank[rank] = pool;
            return pool;
        }

        public bool TryPickCreature(MobRankTier tier, float budgetLeft, out PawnKindDef kind)
        {
            List<PawnKindDef> pool = PoolFor(DungeonRankUtil.ToQuestRank(tier));
            kind = null;
            if (pool.Count == 0)
                // The base mod returns null when no creature pool exists at all.
                // Report false so the resolver's caller can decide what a truly
                // empty bestiary means (it logs — never a silent no-op, G5).
                return false;

            // Prefer something the remaining budget can actually afford; fall
            // back to the cheapest in the band, matching the provider contract
            // (the CALLER decides whether to overspend on the first creature).
            PawnKindDef cheapest = null;
            for (int i = 0; i < pool.Count; i++)
            {
                float cost = DungeonSpawnResolver.CombatPowerOf(pool[i]);
                if (cheapest == null || cost < DungeonSpawnResolver.CombatPowerOf(cheapest))
                    cheapest = pool[i];
                if (cost <= budgetLeft && (kind == null || Rand.Bool))
                    kind = pool[i];
            }
            if (kind == null)
                kind = cheapest;
            return kind != null;
        }

        public PawnKindDef PickBoss(MobRankTier rank)
        {
            // The STRONGEST kind of the rank's band, not a random one. A random
            // draw from the F/E band (combat power 35 and under) crowned F-rank
            // dungeons with a rat or a squirrel. NormalizeRank + the elite flag
            // (and ThreatParity when Isekai Creatures is absent) then push it
            // to proper boss power.
            List<PawnKindDef> pool = PoolFor(DungeonRankUtil.ToQuestRank(rank));
            PawnKindDef best = null;
            for (int i = 0; i < pool.Count; i++)
            {
                if (best == null || DungeonSpawnResolver.CombatPowerOf(pool[i]) > DungeonSpawnResolver.CombatPowerOf(best))
                    best = pool[i];
            }
            return best ?? IncidentWorker_IsekaiHunt.SelectCreatureForRank(
                DungeonRankUtil.ToQuestRank(rank), BossPoints);
        }
    }
}
