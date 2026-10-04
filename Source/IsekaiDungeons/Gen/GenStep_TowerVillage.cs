using System.Collections.Generic;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Builds an actual settlement on a village floor — houses with walls,
    /// doors, roofs, beds, lighting and stockpiles — using the same BaseGen
    /// machinery that generates every faction base in the game.
    ///
    /// WHY A GEN STEP AND NOT A POST-GENERATION CALL. BaseGen technically runs
    /// on a live map (vanilla ships a debug action that does exactly that), but
    /// four things break if the village is built after generation finishes:
    ///
    ///   1. addRoomCenterToRootsToUnfog is a silent no-op outside
    ///      ProgramState.MapInitializing, and GenStep_Fog is its only consumer
    ///      — so the village would generate fogged.
    ///   2. Plants (900), Animals (1200) and Fog (1500) would already have run,
    ///      leaving trees inside the houses and animals sealed in the walls.
    ///   3. After FinalizeInit the region updater is live, so a thousand wall
    ///      spawns each trigger an incremental rebuild.
    ///   4. MapGenerator's UsedRects var would leak between maps.
    ///
    /// Vanilla runs its settlement step at order 400 for precisely these
    /// reasons, and this one sits at 405 — after the layout has decided where
    /// the open ground is, still well before anything grows.
    /// </summary>
    public class GenStep_TowerVillage : GenStep
    {
        /// <summary>
        /// Vanilla's settlement symbol still fills its storage rooms with
        /// ownerless goods even with pawns disabled. The floor is rebuilt on
        /// every visit, so those piles came back each time: free goods on
        /// demand. The shopkeeper's stock (TowerVillage.Build) lives in the
        /// trader's inventory, not on the ground, and is untouched.
        /// </summary>
        private static void ClearLooseGoods(Map map, CellRect rect)
        {
            var doomed = new List<Thing>();
            foreach (IntVec3 c in rect)
            {
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].def.category == ThingCategory.Item)
                        doomed.Add(things[i]);
                }
            }
            for (int i = 0; i < doomed.Count; i++)
                doomed[i].Destroy(DestroyMode.Vanish);
        }

        /// <summary>Unique across the mod's gen steps.</summary>
        public override int SeedPart => 823471243;

        /// <summary>
        /// Edge length of the settlement footprint. The "settlement" RuleDef
        /// declares minRectSize (12,12); below that BaseGen refuses to divide
        /// and you get one shed. This is sized to read as a hamlet on a
        /// 150–250 cell floor without swallowing it.
        /// </summary>
        private const int VillageSize = 46;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!(map.Parent is TowerFloorMapParent floorParent) || !floorParent.village)
                return;

            Faction faction = TowerVillage.EnsureFaction();
            if (faction == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No tower-folk faction; floor {floorParent.floor} gets no settlement.");
                return;
            }

            CellRect rect = CellRect.CenteredOn(map.Center, VillageSize / 2).ClipInsideMap(map);
            if (rect.Width < 12 || rect.Height < 12)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Floor {floorParent.floor} is too small for a settlement.");
                return;
            }

            // Hand the footprint to the parent so TowerVillage.Build knows
            // where the houses went. It runs after generation and has no other
            // way to find them.
            floorParent.villageRect = rect;

            var rp = default(ResolveParams);
            rp.rect = rect;
            rp.faction = faction;

            // NO VANILLA PAWNS. TowerVillage.Build makes its own villagers with
            // the exact trader wiring the shop needs — a second, differently
            // configured population would be a crowd of strangers who cannot
            // trade standing next to the one who can.
            rp.settlementDontGeneratePawns = true;

            // No turrets, no mortars, no sandbag line. These people are not a
            // faction base with a grudge; they live here.
            rp.edgeDefenseWidth = 0;

            try
            {
                BaseGen.globalSettings.map = map;
                BaseGen.globalSettings.minBuildings = 4;
                BaseGen.globalSettings.minBarracks = 1;
                // mainRect is deliberately NOT set: BaseGen.Generate assigns it
                // from the bottom of the stack, and every coverage cap divides
                // by it.
                BaseGen.symbolStack.Push("settlement", rp);
                BaseGen.Generate();
                ClearLooseGoods(map, rect);
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Settlement generation failed on floor {floorParent.floor}: {ex}");
            }
        }
    }
}
