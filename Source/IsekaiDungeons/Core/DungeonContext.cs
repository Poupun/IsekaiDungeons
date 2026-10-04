using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// PUBLIC API: "which dungeon am I generating, and at what rank?"
    ///
    /// This is the entry point a third-party GenStep needs. Previously the only
    /// implementation was <c>internal</c> on GenStep_DungeonLayout (plus a
    /// private duplicate in GenStep_DungeonPopulation), so a modder writing a
    /// custom gen step for their own environment had no supported way to ask
    /// where it was running — they had to reverse-engineer the map parent and
    /// would silently miss the fallback chain.
    ///
    /// Resolution order, and why:
    ///   1. <see cref="DungeonMapParent"/> on the map — the gate assigns rank and
    ///      kind BEFORE MapGenerator.GenerateMap runs, and the parent outlives
    ///      the gate, so it is the canonical identity.
    ///   2. <see cref="PocketMapUtility.currentlyGeneratingPortal"/> as a gate —
    ///      set by vanilla MapPortal.GeneratePocketMap around generation. Covers
    ///      the window before the parent is readable.
    ///   3. Rank F + the always-present fallback archetype, with a warning. A
    ///      wrong-but-playable dungeon beats a silently empty map.
    /// </summary>
    public static class DungeonContext
    {
        /// <summary>
        /// Resolves the dungeon archetype and rank for a map being generated (or
        /// already generated). Never returns a null kind.
        /// </summary>
        public static void Resolve(Map map, out DungeonKindDef kind, out MobRankTier rank)
        {
            if (map?.Parent is DungeonMapParent parent && parent.kindDef != null)
            {
                kind = parent.kindDef;
                rank = parent.rank;
                return;
            }

            if (PocketMapUtility.currentlyGeneratingPortal is Building_DungeonGate gate && gate.kindDef != null)
            {
                kind = gate.kindDef;
                rank = gate.rank;
                return;
            }

            kind = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            rank = MobRankTier.F;
            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not resolve a dungeon kind/rank for {map} " +
                        $"(dev-generated map, or a GenStep running outside a dungeon) — falling back to rank F {kind.defName}.");
        }

        /// <summary>True when this map is a dungeon at all — cheap guard for custom gen steps and comps.</summary>
        public static bool IsDungeonMap(Map map) => map?.Parent is DungeonMapParent;

        /// <summary>The dungeon brain for a map, or null if it is not a dungeon map.</summary>
        public static DungeonMapComponent ComponentOf(Map map) =>
            IsDungeonMap(map) ? map.GetComponent<DungeonMapComponent>() : null;
    }
}
