using System.Collections.Generic;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// How dressed a place is allowed to be. One step per two ranks, so the
    /// dungeon's rank reads from a screenshot: an F gate is a hole somebody
    /// dug, a Nation gate is something no person built.
    /// </summary>
    public enum DecorRegister : byte
    {
        Hole = 0,
        Lair = 1,
        Stronghold = 2,
        Temple = 3,
        Wonder = 4,
    }

    /// <summary>Where in a room a piece goes. Rooms on an Open map have no walls, so the wall rules find no cells there.</summary>
    public enum DecorPlacement : byte
    {
        /// <summary>A wall attachment: stands on the floor cell, rotated to face the wall it hangs on (vanilla Placeworker_AttachedToWall).</summary>
        WallAttached,
        /// <summary>A floor cell touching a wall, rotated to face into the room (vanilla RoomGenUtility.FillAroundEdges).</summary>
        AgainstWall,
        /// <summary>A floor cell touching two perpendicular walls.</summary>
        Corner,
        /// <summary>Anywhere free in the room.</summary>
        Scatter,
        /// <summary>The free cell nearest the room's centre.</summary>
        Centerpiece,
        /// <summary>Mirrored pairs along the room's long axis, inset from the walls. Colonnades.</summary>
        ColumnPairs,
        /// <summary>Either side of each doorway, on the room side.</summary>
        DoorFlank,
        /// <summary>Evenly spaced round the room's centre. The Open-map colonnade.</summary>
        Ring,
    }

    /// <summary>
    /// Which kind of light a piece gives.
    ///
    /// Kept apart per room because vanilla cave plants die in any light that is
    /// not another cave plant: Plant.DyingBecauseExposedToLight reads
    /// GroundGlowAt(ignoreCavePlants: true). A torch beside a bryolux kills it
    /// within days, so a room is lit by one family or the other, never both.
    /// </summary>
    public enum DecorLight : byte
    {
        None,
        Fire,
        Plant,
    }

    /// <summary>One thing a decor set can put down.</summary>
    public class DecorEntry
    {
        // Exactly one of these. Strings, resolved at load, so an entry can name a
        // DLC def without breaking the set for players who do not own the DLC.
        public string thing;
        public string plant;
        public string filth;

        /// <summary>Terrain painted under the piece first — moss under a glowing mushroom.</summary>
        public string patch;

        public DecorPlacement placement = DecorPlacement.Scatter;

        /// <summary>The register range this entry belongs to, inclusive.</summary>
        public DecorRegister from = DecorRegister.Hole;
        public DecorRegister upTo = DecorRegister.Wonder;

        public float weight = 1f;

        /// <summary>Decor points one placement spends. A cluster is one placement.</summary>
        public float cost = 1f;

        /// <summary>Pieces laid per placement, round the first one.</summary>
        public IntRange cluster = new IntRange(1, 1);

        public int maxPerRoom = 99;

        /// <summary>Rooms smaller than this (cells) never get it. Keeps colossi out of closets.</summary>
        public int minRoomArea;

        /// <summary>Room roles allowed. Empty means every role.</summary>
        public List<DungeonRoomRole> roles = new List<DungeonRoomRole>();

        /// <summary>Only placed in kinds marked dirty (DungeonKindDef.dirty). Loose ground and the like.</summary>
        public bool dirtyOnly;

        /// <summary>The expansion this entry needs. Unresolved names are an error only when this is empty or the expansion is active.</summary>
        public string mayRequire;

        [Unsaved] public ThingDef thingDef;
        [Unsaved] public ThingDef plantDef;
        [Unsaved] public ThingDef filthDef;
        [Unsaved] public TerrainDef patchDef;

        /// <summary>The def this entry places, whichever kind it is.</summary>
        public ThingDef PlacedDef => thingDef ?? plantDef ?? filthDef;

        public bool Resolved => PlacedDef != null;

        public DecorLight Light
        {
            get
            {
                ThingDef d = thingDef ?? plantDef;
                if (d == null)
                    return DecorLight.None;
                if (d.plant != null)
                    return d.plant.cavePlant && d.GetCompProperties<CompProperties_Glower>() != null
                        ? DecorLight.Plant : DecorLight.None;
                return d.GetCompProperties<CompProperties_Glower>() != null ? DecorLight.Fire : DecorLight.None;
            }
        }

        public bool AllowsRole(DungeonRoomRole role) => roles == null || roles.Count == 0 || roles.Contains(role);

        public bool AllowsRegister(DecorRegister r) => r >= from && r <= upTo;

        public void Resolve()
        {
            thingDef = thing.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(thing);
            plantDef = plant.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(plant);
            filthDef = filth.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(filth);
            patchDef = patch.NullOrEmpty() ? null : DefDatabase<TerrainDef>.GetNamedSilentFail(patch);
        }

        public string Name => thing ?? plant ?? filth ?? "(empty)";
    }

    /// <summary>
    /// The decoration kit for one environment: what its rooms may contain at
    /// each register, and how its surfaces age or refine with rank.
    ///
    /// A new look is a new def and nothing else, the same contract the dungeon
    /// kinds keep. GenStep_DungeonDecor picks the set by the map's generator, or
    /// by the dungeon kind when a set names kinds, and a kind always wins.
    /// </summary>
    public class DungeonDecorSetDef : Def
    {
        /// <summary>MapGeneratorDef defNames this set dresses.</summary>
        public List<string> generators = new List<string>();

        /// <summary>DungeonKindDef defNames this set dresses, overriding the generator's set.</summary>
        public List<string> kinds = new List<string>();

        public List<DecorEntry> entries = new List<DecorEntry>();

        /// <summary>Scales every room's decor budget. The one knob for "this place is sparse" or "this place is cluttered".</summary>
        public float density = 1f;

        /// <summary>
        /// Rooms at or below this register are lit by cave plants, rooms above it
        /// by fire. Entry and boss rooms are never plant-lit: the exit and the
        /// stairs both glow, and their light would kill the plants.
        /// </summary>
        public DecorRegister plantLightUpTo = DecorRegister.Hole;
        public bool plantLight = true;
        public bool fireLight = true;

        /// <summary>Wall attachment lining the corridors from <see cref="corridorLightFrom"/> up. Carved maps only.</summary>
        public string corridorLight;
        public DecorRegister corridorLightFrom = DecorRegister.Stronghold;

        /// <summary>Filth scattered along corridors, one roll per corridor stretch.</summary>
        public List<string> corridorFilth = new List<string>();

        /// <summary>Extra corridor filth, rolled only in kinds marked dirty.</summary>
        public List<string> corridorFilthDirty = new List<string>();

        /// <summary>Refine carved rock floors with rank: rough, rough-hewn, smooth. Only the rock's own floors are touched.</summary>
        public bool refineFloors;

        /// <summary>Smooth the rock shell round rooms with rank. The smoothed walls still hold the roof.</summary>
        public bool smoothWalls;

        [Unsaved] public ThingDef corridorLightDef;
        [Unsaved] public List<ThingDef> corridorFilthDefs = new List<ThingDef>();
        [Unsaved] public List<ThingDef> corridorFilthDirtyDefs = new List<ThingDef>();

        public override void ResolveReferences()
        {
            base.ResolveReferences();
            for (int i = 0; i < entries.Count; i++)
                entries[i].Resolve();
            corridorLightDef = corridorLight.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(corridorLight);
            ResolveFilth(corridorFilth, corridorFilthDefs);
            ResolveFilth(corridorFilthDirty, corridorFilthDirtyDefs);
        }

        private static void ResolveFilth(List<string> names, List<ThingDef> defs)
        {
            defs.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                ThingDef f = DefDatabase<ThingDef>.GetNamedSilentFail(names[i]);
                if (f != null)
                    defs.Add(f);
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;

            if (generators.NullOrEmpty() && kinds.NullOrEmpty())
                yield return "names no generator and no kind, so nothing will ever use it";

            for (int i = 0; i < entries.Count; i++)
            {
                DecorEntry e = entries[i];
                int named = (e.thing.NullOrEmpty() ? 0 : 1) + (e.plant.NullOrEmpty() ? 0 : 1) + (e.filth.NullOrEmpty() ? 0 : 1);
                if (named != 1)
                {
                    yield return $"entry {i} must name exactly one of thing, plant or filth (names {named})";
                    continue;
                }
                if (e.from > e.upTo)
                    yield return $"entry {e.Name}: from {e.from} is above upTo {e.upTo}";

                if (!e.Resolved)
                {
                    // A missing DLC def is expected. A missing Core def is a typo.
                    if (e.mayRequire.NullOrEmpty() || ModsConfig.IsActive(e.mayRequire))
                        yield return $"entry {e.Name} does not resolve to a def";
                    continue;
                }
                if (!e.patch.NullOrEmpty() && e.patchDef == null)
                    yield return $"entry {e.Name}: patch terrain {e.patch} does not resolve";

                string unsafeWhy = DecorSafety.WhyUnsafe(e.PlacedDef);
                if (unsafeWhy != null)
                    yield return $"entry {e.Name} is not safe dungeon decor: {unsafeWhy}";
            }

            if (!corridorLight.NullOrEmpty() && corridorLightDef != null)
            {
                if (corridorLightDef.building == null || !corridorLightDef.building.isAttachment)
                    yield return $"corridorLight {corridorLight} must be a wall attachment";
                string why = DecorSafety.WhyUnsafe(corridorLightDef);
                if (why != null)
                    yield return $"corridorLight {corridorLight} is not safe dungeon decor: {why}";
            }
        }
    }

    /// <summary>
    /// The contract every piece of dungeon decor keeps, checked at load.
    ///
    /// Tower and multi-floor maps rebuild on every visit, so anything a player can
    /// turn into resources is back the next time they walk in. An ownerless
    /// building deconstructs without a claim (Building.DeconstructibleBy falls
    /// through to ClaimableBy) and refunds half its cost; a minifiable one can be
    /// carried out and sold; killedLeavings pays out when it is shot apart; and a
    /// refuelable light burns out between visits. DUNGEON_DECOR_DESIGN.md §2 has
    /// the decompiled detail.
    /// </summary>
    public static class DecorSafety
    {
        /// <summary>Null when the def is safe to generate in a dungeon, otherwise the reason it is not.</summary>
        public static string WhyUnsafe(ThingDef def)
        {
            if (def == null)
                return null;
            if (def.category == ThingCategory.Filth || def.plant != null)
                return null;   // filth pays nothing; plants are what the biome step spawns anyway
            if (!def.costList.NullOrEmpty())
                return "it has a costList, so deconstructing it refunds resources";
            if (def.MadeFromStuff)
                return "it is made from stuff, so deconstructing it refunds resources";
            if (def.Minifiable)
                return "it is minifiable, so it can be uninstalled and sold";
            if (!def.killedLeavings.NullOrEmpty())
                return "it has killedLeavings, so shooting it apart pays out";
            if (def.GetCompProperties<CompProperties_Refuelable>() != null)
                return "it is refuelable, so its light burns out between visits";
            return null;
        }
    }
}
