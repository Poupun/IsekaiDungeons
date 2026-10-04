using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>How a dungeon's rooms are cut into (or built onto) the map.</summary>
    public enum DungeonLayoutMode : byte
    {
        /// <summary>
        /// Subtractive. The generator pre-fills the map with rock (isUnderground
        /// generators do this), and we carve rooms and corridors out of it —
        /// the leftover rock IS the wall shell. Caverns, hives, crypts.
        /// </summary>
        Carve = 0,

        /// <summary>
        /// Additive. The map is open ground (a surface biome), so we floor the
        /// rooms and corridors and BUILD a wall shell around the complex.
        /// Snowfields, ruins, foundries — the Solo-Leveling style dungeons that
        /// are a whole alternative world rather than a hole in the ground.
        ///
        /// NOTE: no shipped dungeon uses this. It is kept for a future walled
        /// compound / fortress archetype.
        /// </summary>
        Build = 1,

        /// <summary>
        /// Untouched. The map is the dungeon: an open biome with NO walls, NO
        /// corridors and NO doors. Difficulty comes purely from where the
        /// creature groups stand — escalating encounter ZONES laid out along a
        /// path from the entry portal to the boss, so walking deeper is walking
        /// into stronger things.
        ///
        /// This is what a Solo-Leveling surface gate actually looks like: you
        /// step through and you are standing in a snowfield, not in a building.
        /// Containment is free — ExitMapGrid.MapUsesExitGridNow returns false
        /// for pocket maps, so the map edge already bounds the field and no
        /// wall is needed to keep pawns in.
        /// </summary>
        Open = 2,
    }

    /// <summary>
    /// Data-only description of one dungeon ARCHETYPE — a single entry in the
    /// dungeon catalogue ("Slime Ranch", "Mechanoid Foundry", ...).
    ///
    /// The design contract: a new dungeon is a new DungeonKindDef and NOTHING
    /// else. Environment comes from mapGenerator (a handful of shared
    /// MapGeneratorDefs), look comes from the theme fields, and identity comes
    /// from the roster. Code never hard-codes a dungeon.
    ///
    /// All SimpleCurves are keyed by (float)(int)MobRankTier: 0 = F … 8 = SSS,
    /// 9 = Nation. Curves may define fewer points; SimpleCurve clamps outside
    /// the defined range, which is the behavior we want.
    /// </summary>
    public class DungeonKindDef : Def
    {
        /// <summary>Strategy hook for archetype behaviour. Must be DungeonWorker or a subclass.</summary>
        public Type workerClass = typeof(DungeonWorker);

        /// <summary>
        /// What "cleared" means for this dungeon. Defaults to "the boss is
        /// defeated"; override for survive/reach/wipe-all objectives. Must be
        /// DungeonObjectiveWorker or a subclass.
        /// </summary>
        public Type objectiveWorkerClass = typeof(DungeonObjectiveWorker);

        /// <summary>
        /// What this dungeon pays out. Defaults to the built-in Isekai loot
        /// ladder; override to drop your own themed rewards. Must be
        /// DungeonLootWorker or a subclass.
        /// </summary>
        public Type lootWorkerClass = typeof(DungeonLootWorker);

        /// <summary>The gate building spawned on the colony map. thingClass Building_DungeonGate.</summary>
        public ThingDef gateDef;

        /// <summary>
        /// Pocket-map generator = the ENVIRONMENT. Must include
        /// DungeonMapComponent in customMapComponents and the IsekaiDungeons
        /// gen steps — the lifecycle code assumes both.
        /// </summary>
        public MapGeneratorDef mapGenerator;

        // ── Selection ───────────────────────────────────────────────────────

        /// <summary>Lowest gate rank this dungeon can be rolled for.</summary>
        /// <summary>
        /// Belongs to the TOWER, not to gates.
        ///
        /// The two pools are mutually exclusive: DungeonKindSelector skips
        /// anything with this set, TowerLadder accepts only things with it. A
        /// hundred-floor climb and a colony-side gate want completely different
        /// balance, and a shared pool means every tower tweak silently retunes
        /// the storyteller's dungeons too.
        ///
        /// Defaults false, so all 67 existing defs keep working with no edits.
        /// </summary>
        public bool towerOnly;

        /// <summary>
        /// This dungeon's population is MIRRORS of the player pawns who enter.
        ///
        /// Generation spawns nothing (the map is built while the party still
        /// stands on the previous floor, so there is nobody to copy yet);
        /// DungeonMapComponent clones every player humanlike present on the
        /// first tick one exists, registers them as the boss-room encounter,
        /// and the missing-boss generation error stands down for this def.
        /// Pair with objectiveWorkerClass DungeonObjectiveWorker_MirrorArena,
        /// or the floor completes on the default boss rule instead of
        /// "every copy defeated".
        /// </summary>
        public bool mirrorArena;

        /// <summary>
        /// This dungeon's boss is an AUTHORED CHARACTER, not a rolled creature:
        /// the defName of an IsekaiCharacterDef (the base mod's character spec
        /// system), generated through IsekaiCharacterForge and locked, so every
        /// automatic NPC mutator stands down and the boss is exactly what the
        /// XML says.
        ///
        /// Null (the default) is the ordinary path: pick from the roster's
        /// bosses list, then the generic at-rank pool. When set and generation
        /// FAILS the ordinary path still runs, because a bossless dungeon can
        /// never be cleared and that is worse than the wrong boss.
        ///
        /// The named character supplies its own faction, gear and stats; the
        /// dungeon's rank is not forced onto it (NormalizeRank only raises, and
        /// an authored Nation-rank character is already where it should be).
        /// </summary>
        public string authoredBoss;

        /// <summary>
        /// The boss stands here ALONE. No combat-room population, no room
        /// events, no boss entourage — and the authored boss is placed at the
        /// centre of the map rather than inside a room rect.
        ///
        /// For a floor whose whole content is one confrontation. The summit is
        /// the case it was written for: Elijah has held that roof by herself for
        /// five centuries, and filling the approach with a war party of her own
        /// order both contradicted the story and buried the one thing the floor
        /// exists for. Treasure is untouched — this suppresses who is standing
        /// there, not what is lying there.
        ///
        /// Requires <see cref="authoredBoss"/> in practice: without one, the
        /// floor falls back to a rolled boss standing alone, which works but is
        /// not what anyone means by this flag.
        /// </summary>
        public bool bossOnly;

        /// <summary>
        /// This dungeon may ONLY appear where an archetype names it in
        /// dungeonKinds — never from a weighted roll.
        ///
        /// selectionWeight 0 does NOT achieve this and never did: every
        /// selection site floors the weight at 0.01
        /// (RandomElementByWeight(d => Mathf.Max(0.01f, d.selectionWeight))),
        /// so a "weight 0" def is merely rare, not excluded. Set-piece floors
        /// — the mirror hall, the summit — must be exactly where they were
        /// authored to be and nowhere else, so they need a real flag rather
        /// than a number that reads like one.
        /// </summary>
        public bool pinnedOnly;


        public MobRankTier minRank = MobRankTier.F;

        /// <summary>Highest gate rank this dungeon can be rolled for.</summary>
        public MobRankTier maxRank = MobRankTier.Nation;

        /// <summary>Relative weight among all dungeons valid for the rolled rank.</summary>
        public float selectionWeight = 1f;

        // ── Rank-keyed tuning curves ────────────────────────────────────────
        public SimpleCurve mapSizeByRank;
        public SimpleCurve roomCountByRank;
        public SimpleCurve encounterPointsByRank;
        public SimpleCurve breakTimerDaysByRank;
        public SimpleCurve lootValueByRank;

        // ── Layout tuning ───────────────────────────────────────────────────
        public DungeonLayoutMode layoutMode = DungeonLayoutMode.Carve;

        /// <summary>
        /// Edge length (cells) rooms are rolled from. Rooms are rectangular.
        /// Max 13 is a ROOF-SUPPORT constraint for roofed dungeons: carved
        /// rooms get RoofRockThin, and at 13 cells every roof cell stays within
        /// support range of the walls; 14+ would rain collapses on room centers.
        /// Unroofed (open-sky) dungeons are not bound by this.
        /// </summary>
        public IntRange roomSizeRange = new IntRange(8, 12);

        /// <summary>
        /// OPEN mode only: radius of an encounter zone, in cells. Zones are the
        /// open-field replacement for rooms — invisible circles of ground that
        /// a creature group holds. They are deliberately far larger than a room
        /// (a group should feel spread across a field, not packed in a box) and
        /// they never touch, so the progression stays readable as you advance.
        /// </summary>
        public IntRange openZoneRadiusRange = new IntRange(10, 16);
        public IntRange treasureRoomCountRange = new IntRange(0, 2);
        public int corridorWidth = 2;

        // ── Theme ───────────────────────────────────────────────────────────

        /// <summary>Floor for carved/built cells. Null keeps whatever the generator laid down.</summary>
        public TerrainDef floorTerrain;

        /// <summary>
        /// Earthy, lived-in, dug-out places (burrows, warrens, mud, nests). Only
        /// these get the decor entries and corridor filth marked dirtyOnly, which
        /// is where loose ground lives. A clean or built dungeon, and every tower
        /// floor, leaves this false.
        /// </summary>
        public bool dirty;

        /// <summary>Build mode only: the wall shell. Defaults to a plain vanilla wall. IGNORED in Open mode.</summary>
        public ThingDef wallDef;

        /// <summary>Build mode only: stuff for wallDef (Granite, Steel, WoodLog...). Null = first allowed.</summary>
        public ThingDef wallStuff;

        /// <summary>
        /// Roof rooms and corridors? Carve dungeons are always roofed (they are
        /// holes in rock). Surface dungeons default to OPEN SKY, which is what
        /// makes a snowfield read as a world rather than a building.
        /// </summary>
        public bool roofed = true;

        // ── Doors ───────────────────────────────────────────────────────────

        /// <summary>
        /// Place doors where corridors meet rooms. This is what keeps unexplored
        /// rooms fogged (see Building_DungeonDoor), so it is on by default and
        /// should only be disabled for deliberately open-plan dungeons.
        /// </summary>
        public bool placeDoors = true;

        /// <summary>Door building. Defaults to IsekaiDungeons_DungeonDoor.</summary>
        public ThingDef doorDef;

        /// <summary>Stuff for doorDef when it is stuffable. Null = first allowed.</summary>
        public ThingDef doorStuff;

        // ── Roster ──────────────────────────────────────────────────────────

        /// <summary>
        /// The dungeon's monster roster. Empty = fall back to the generic
        /// rank-based providers (any rank-appropriate creature in the game).
        /// </summary>
        public List<DungeonSpawnEntry> roster = new List<DungeonSpawnEntry>();

        /// <summary>Boss candidates (PawnKindDef defNames). Empty = generic boss selection.</summary>
        public List<string> bosses = new List<string>();

        /// <summary>
        /// When true, this dungeon is only offered if at least one roster entry
        /// AND one boss resolve. Set on dungeons whose identity depends on a
        /// soft-dependency bestiary — a "Slime Ranch" with no slimes in it is
        /// not a Slime Ranch, so it should simply not appear. Vanilla-rostered
        /// dungeons leave this false and always work.
        /// </summary>
        public bool requiresFullRoster = true;

        /// <summary>
        /// Never fall back to the generic creature pool while the roster has
        /// anything that is not over-rank: when a floor's band is above what
        /// the family reaches, field the family's strongest instead. Set on
        /// the multi-floor kinds, where the theme IS the dungeon.
        /// </summary>
        public bool stayOnTheme;

        // ── Behavior tuning ─────────────────────────────────────────────────

        // clearedLingerDays REMOVED. A cleared gate used to sit for a flat 2-3
        // days before it was even eligible to collapse; it now closes two hours
        // after the last player pawn leaves (DungeonTunables.ClearedEmptyCollapseTicks),
        // which is departure-driven and needs no per-kind knob. The field is
        // deleted rather than deprecated on purpose: a vestigial XML field that
        // silently does nothing is worse than one that errors, because the next
        // person to set it gets no feedback at all.

        public bool bossIsElite = true;

        /// <summary>
        /// Optional theme for this dungeon. Null plays the shipped rank-band
        /// tracks. A third-party dungeon can name its own SongDef here; it is
        /// played and kept through fights exactly like the shipped tracks.
        /// </summary>
        public SongDef music;

        // redGateChance was removed: local gates no longer roll above the
        // colony cap (DungeonRankUtil), and nothing read the field. Deleted
        // rather than kept, for the reason given above.

        private DungeonWorker workerInt;
        private DungeonObjectiveWorker objectiveWorkerInt;
        private DungeonLootWorker lootWorkerInt;
        private List<PawnKindDef> resolvedBossesInt;
        private List<DungeonSpawnEntry> resolvedRosterInt;
        private bool rosterResolved;

        /// <summary>
        /// Cached, stateless strategy instance. Never saved — all persistent
        /// state lives on the gate building / map parent / map component.
        /// </summary>
        public DungeonWorker Worker
        {
            get
            {
                if (workerInt == null)
                    workerInt = (DungeonWorker)Activator.CreateInstance(workerClass ?? typeof(DungeonWorker));
                return workerInt;
            }
        }

        /// <summary>Cached objective strategy. Stateless — never scribed.</summary>
        public DungeonObjectiveWorker ObjectiveWorker
        {
            get
            {
                if (objectiveWorkerInt == null)
                    objectiveWorkerInt = (DungeonObjectiveWorker)Activator.CreateInstance(objectiveWorkerClass ?? typeof(DungeonObjectiveWorker));
                return objectiveWorkerInt;
            }
        }

        /// <summary>Cached loot strategy. Stateless — never scribed.</summary>
        public DungeonLootWorker LootWorker
        {
            get
            {
                if (lootWorkerInt == null)
                    lootWorkerInt = (DungeonLootWorker)Activator.CreateInstance(lootWorkerClass ?? typeof(DungeonLootWorker));
                return lootWorkerInt;
            }
        }

        /// <summary>Roster entries whose PawnKindDef actually exists in this game.</summary>
        public List<DungeonSpawnEntry> ResolvedRoster
        {
            get
            {
                EnsureRosterResolved();
                return resolvedRosterInt;
            }
        }

        /// <summary>Boss kinds that actually exist in this game.</summary>
        public List<PawnKindDef> ResolvedBosses
        {
            get
            {
                EnsureRosterResolved();
                return resolvedBossesInt;
            }
        }

        /// <summary>
        /// Available AND its authored roster actually resolves. Available alone
        /// is true for any requiresFullRoster=false kind, even one whose whole
        /// bestiary is missing; this is the stricter question for content that
        /// is only worth offering as authored (the deep dungeons).
        /// </summary>
        public bool AvailableAsAuthored =>
            Available && (roster == null || roster.Count == 0 || (ResolvedRoster?.Count ?? 0) > 0);

        /// <summary>
        /// Can this dungeon be offered at all? A themed dungeon whose bestiary
        /// is missing (add-on not installed, DLC absent) drops out silently
        /// instead of generating an empty or off-theme crawl.
        /// </summary>
        public bool Available
        {
            get
            {
                if (!requiresFullRoster)
                    return true;
                EnsureRosterResolved();
                return resolvedRosterInt.Count > 0 && resolvedBossesInt.Count > 0;
            }
        }

        /// <summary>
        /// This dungeon exists so the mod still works with no add-ons installed.
        ///
        /// The catalogue calls these "the backbone that keeps the mod playable
        /// with no add-on installed at all", and that is exactly the problem
        /// they caused: a backbone was competing at full selectionWeight with
        /// the themed content it was meant to stand in for. At F and E they were
        /// forty-odd percent of the pool, so a player with Isekai Creatures
        /// installed still met megaspiders and wolves about half the time.
        ///
        /// Marking them lets DungeonKindSelector step them down WHENEVER
        /// themed content is actually available at that rank, and leave them at
        /// full strength when it is not. A player with no add-ons notices no
        /// difference; a player with the add-ons gets the dungeons they
        /// installed.
        ///
        /// NOT the same question as requiresFullRoster. The Nation dungeons and
        /// the DLC ones also set that false or true for their own reasons; this
        /// is specifically "I am a stand-in for missing content".
        /// </summary>
        public bool vanillaBackbone;

        public bool AllowsRank(MobRankTier rank) => rank >= minRank && rank <= maxRank;

        /// <summary>
        /// Resolves roster/boss defName strings once, on first use (after all
        /// defs are loaded — never in the constructor). Missing defs are
        /// dropped silently; a fully-dropped roster is reported once so a
        /// content author can tell "intentionally absent" from "typo".
        /// </summary>
        private void EnsureRosterResolved()
        {
            if (rosterResolved)
                return;
            rosterResolved = true;

            resolvedRosterInt = new List<DungeonSpawnEntry>();
            if (roster != null)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    DungeonSpawnEntry e = roster[i];
                    if (e?.kind == null)
                        continue;
                    if (DefDatabase<PawnKindDef>.GetNamedSilentFail(e.kind) != null)
                        resolvedRosterInt.Add(e);
                }
            }

            resolvedBossesInt = new List<PawnKindDef>();
            if (bosses != null)
            {
                for (int i = 0; i < bosses.Count; i++)
                {
                    PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(bosses[i]);
                    if (k != null)
                        resolvedBossesInt.Add(k);
                }
            }

            bool declaredRoster = (roster != null && roster.Count > 0) || (bosses != null && bosses.Count > 0);
            if (declaredRoster && resolvedRosterInt.Count == 0 && resolvedBossesInt.Count == 0)
            {
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Dungeon '{defName}' resolved no roster creatures " +
                            $"(its bestiary mod is probably not installed); it will not be offered.");
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors())
                yield return error;
            if (gateDef == null) yield return "gateDef is null";
            if (mapGenerator == null) yield return "mapGenerator is null";
            if (mapSizeByRank == null) yield return "mapSizeByRank curve missing";
            if (roomCountByRank == null) yield return "roomCountByRank curve missing";
            if (encounterPointsByRank == null) yield return "encounterPointsByRank curve missing";
            if (breakTimerDaysByRank == null) yield return "breakTimerDaysByRank curve missing";
            if (lootValueByRank == null) yield return "lootValueByRank curve missing";
            if (workerClass != null && !typeof(DungeonWorker).IsAssignableFrom(workerClass))
                yield return $"workerClass {workerClass} does not extend DungeonWorker";
            if (objectiveWorkerClass != null && !typeof(DungeonObjectiveWorker).IsAssignableFrom(objectiveWorkerClass))
                yield return $"objectiveWorkerClass {objectiveWorkerClass} does not extend DungeonObjectiveWorker";
            if (lootWorkerClass != null && !typeof(DungeonLootWorker).IsAssignableFrom(lootWorkerClass))
                yield return $"lootWorkerClass {lootWorkerClass} does not extend DungeonLootWorker";
            // Width 1 can seal the corridor off: a one-lane leg running flush
            // along a room ring has no mouth cell the door logic recognises,
            // so the ring is resealed and everything beyond it (the boss
            // included) is unreachable. Width 2 survives because the second
            // lane overlaps the room.
            if (corridorWidth < 2) yield return "corridorWidth must be >= 2 (width 1 can cut the boss off)";
            if (minRank > maxRank) yield return "minRank is above maxRank";
            // 12, not 13. The wall ring sits OUTSIDE the room rect, so the
            // centre cell of a 13x13 room is 7 cells from every wall, past
            // vanilla's 6.9 roof-support radius: destroy the decor columns near
            // it (a boss-fight explosion) and that roof cell falls.
            if (layoutMode != DungeonLayoutMode.Open && roofed && roomSizeRange.max > 12)
                yield return "roofed carved/built dungeons must keep roomSizeRange.max <= 12 (roof support)";

            // Open-mode invariants. These caught real drift once already: an Open
            // dungeon carrying wallDef/floorTerrain describes architecture that
            // never generates, and an Open kind pointed at an underground
            // generator would produce a solid-rock map with zero standable cells
            // — an instantly unplayable dungeon with no error anywhere.
            if (layoutMode == DungeonLayoutMode.Open)
            {
                if (mapGenerator != null && mapGenerator.isUnderground)
                    yield return $"layoutMode Open but mapGenerator {mapGenerator.defName} is underground (the map would be solid rock)";
                if (wallDef != null || wallStuff != null)
                    yield return "layoutMode Open ignores wallDef/wallStuff — remove them (Open builds no walls)";
                if (floorTerrain != null)
                    yield return "layoutMode Open ignores floorTerrain — remove it (Open leaves the biome's ground alone)";
                if (openZoneRadiusRange.min < 5)
                    yield return "openZoneRadiusRange.min must be >= 5 — a smaller zone cannot hold a group";
                if (openZoneRadiusRange.max < openZoneRadiusRange.min)
                    yield return "openZoneRadiusRange max is below min";
            }
            else if (layoutMode == DungeonLayoutMode.Carve && mapGenerator != null && !mapGenerator.isUnderground)
            {
                yield return $"layoutMode Carve but mapGenerator {mapGenerator.defName} is a surface generator (there is no rock to carve)";
            }
        }
    }
}
