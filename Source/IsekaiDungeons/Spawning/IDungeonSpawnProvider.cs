using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Seam between the dungeon generator and whatever bestiary is installed.
    ///
    /// Providers are probed in priority order by DungeonSpawnResolver:
    ///   1. SpawnProvider_IsekaiCreatures — reflection-only soft dependency on
    ///      the Creatures add-on's rank table (fails open when absent).
    ///   2. SpawnProvider_BaseMod — the base mod's own creature selection,
    ///      always available (hard reference).
    ///
    /// Implementations must be cheap after their first call (build indexes
    /// lazily, cache them for the process lifetime — def identity is stable
    /// across saves) and must NEVER throw for a tier with no candidates:
    /// return false / null and let the resolver fall through.
    /// </summary>
    public interface IDungeonSpawnProvider
    {
        /// <summary>False disables the provider for the whole session (logged once).</summary>
        bool Available { get; }

        /// <summary>
        /// Picks one creature kind fighting at roughly the given tier.
        /// budgetLeft is the remaining room budget in combatPower points —
        /// providers should prefer kinds that fit inside it.
        /// </summary>
        bool TryPickCreature(MobRankTier tier, float budgetLeft, out PawnKindDef kind);

        /// <summary>Picks a single boss-worthy kind for a dungeon of the given rank. Null = no candidate.</summary>
        PawnKindDef PickBoss(MobRankTier rank);
    }
}
