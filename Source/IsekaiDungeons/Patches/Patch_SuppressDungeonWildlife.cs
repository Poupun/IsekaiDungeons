using HarmonyLib;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Stops vanilla wildlife migrating into a dungeon.
    ///
    /// A dungeon pocket map uses a REAL surface biome (IceSheet, TemperateForest,
    /// …) so its terrain, plants and weather look right. That biome also carries
    /// an animalDensity, and Map.MapPostTick runs WildAnimalSpawnerTick on every
    /// map unconditionally — pocket maps included. Left alone, a dungeon slowly
    /// fills with muffalo and squirrels wandering the encounter field, which is
    /// wrong twice over: it is absurd next to a dark-elf warband, and every
    /// animal is another pawn ticking on a 150-240 cell map that can stay open
    /// for days while the player is elsewhere.
    ///
    /// The spawner itself is not the cost — it self-throttles to one attempt per
    /// 1213 ticks — the accumulated ANIMALS are. Suppressing the spawn is the
    /// cheapest possible fix: one type check, no per-animal work. The dungeon's
    /// own roster is unaffected, because our creatures are placed by
    /// GenStep_DungeonPopulation and never by this spawner.
    ///
    /// One of the mod's few Harmony patches; each exists only where vanilla
    /// has no extension point (see IsekaiDungeonsMod). There is no extension
    /// point for "this map has no wildlife", so this one is a patch by
    /// necessity rather than by choice. Notably the Godhood BODY
    /// (ageless/deathless/immune, GodhoodBody.cs) needed no patch at all —
    /// preventsDeath and makeImmuneTo are vanilla hediff fields.
    /// </summary>
    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    public static class Patch_SuppressDungeonWildlife
    {
        /// <summary>
        /// ___map is WildAnimalSpawner's private Map field, injected by Harmony.
        /// Returning false skips the original ONLY for our dungeon maps; every
        /// other map on the planet keeps its wildlife exactly as vanilla intends.
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix(Map ___map)
        {
            // Settings, Advanced tab: a player who wants the biome's wildlife
            // wandering the dungeon can have it, at the TPS cost described above.
            if (!IsekaiDungeonsSettings.Current.suppressDungeonWildlife)
                return true;
            return !(___map?.Parent is DungeonMapParent);
        }
    }
}
