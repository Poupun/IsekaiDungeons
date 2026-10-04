using IsekaiLeveling.MobRanking;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// World object owning a dungeon pocket map. Subclasses vanilla
    /// PocketMapParent (which already scribes sourceMap + mapGenerator and
    /// plugs into Find.World.pocketMaps) and adds the two facts a dungeon map
    /// must never lose: its rank and its archetype def.
    ///
    /// These are duplicated from the gate ON PURPOSE: the parent is the map's
    /// canonical identity. If the gate is ever destroyed while the map still
    /// exists (mods, dev tools, future archetypes), every GenStep/MapComponent
    /// consumer still resolves rank/kind through map.Parent instead of
    /// silently defaulting (G5/G9).
    /// </summary>
    public class DungeonMapParent : PocketMapParent
    {
        public MobRankTier rank = MobRankTier.F;
        public DungeonKindDef kindDef;

        // ── The recipe ──────────────────────────────────────────────────────
        //
        // Both of these are OPT-IN SENTINELS. A gate that never sets them
        // produces exactly the dungeon it always did: the seed is rolled fresh
        // and the rank index is the enum's own integer value. They exist so a
        // caller that needs a map to be REPRODUCIBLE (the tower, which destroys
        // a floor when the player leaves and rebuilds it on return) can pin
        // both without changing anything for anyone else.

        /// <summary>
        /// Layout seed, or -1 to roll one. DungeonLayoutPlan.Generate is a pure
        /// function of (mapSize, seed, kindDef, rank) — verified: it brackets
        /// itself in Rand.PushState/PopState and touches no Map, Find or
        /// Current — so pinning this reproduces rooms, corridors, wings,
        /// shortcuts, room roles, depthFrac and lootScale exactly.
        /// </summary>
        public int layoutSeed = -1;

        /// <summary>
        /// Difficulty as a CONTINUOUS position on the F..Nation ladder, or -1
        /// to use the enum.
        ///
        /// Every rank curve on DungeonKindDef is a SimpleCurve, and
        /// SimpleCurve.Evaluate takes a float and interpolates — but all six
        /// call sites passed (int)rank, which threw that away. A tower with a
        /// hundred floors would otherwise collapse into ten difficulty steps.
        /// Carrying a float here buys a hundred smooth steps out of the ten
        /// anchors the curves already have, with no new tuning data anywhere.
        ///
        /// The discrete MobRankTier is still carried alongside it: roster
        /// minDepth gating, TierFor and NormalizeRank all need a real enum.
        /// </summary>
        public float rankIndex = -1f;

        /// <summary>Continuous difficulty, falling back to the enum when unset.</summary>
        public float RankIndex => rankIndex >= 0f ? rankIndex : (int)rank;

        // ── Per-instance bends ──────────────────────────────────────────────
        // A tower floor is one of the shipped dungeons seen through an
        // archetype's lens: same def, different proportions. All three are
        // identity values, so an ordinary dungeon is untouched.

        /// <summary>Scales the rolled room count. 1 = the def's own curve.</summary>
        public float roomCountFactor = 1f;

        /// <summary>Scales the encounter purse. 1 = the def's own curve.</summary>
        public float encounterFactor = 1f;

        /// <summary>Forces the treasure-room count. -1 = the def's own range.</summary>
        public int treasureRoomsOverride = -1;

        /// <summary>
        /// Generate the place but not its population: no creatures, no boss, no
        /// treasure. This is what a CLEARED tower floor comes back as.
        ///
        /// It is a separate flag from DungeonMapComponent.cleared on purpose.
        /// That field returns early ABOVE the aggro sweep, so reusing it as the
        /// quiet-mode marker would disable aggro on a floor that still had
        /// things standing on it. This one only ever means "skip population".
        /// </summary>
        public bool suppressPopulation;

        /// <summary>
        /// Generate the place and its treasure, but nothing hostile.
        ///
        /// Separate from suppressPopulation on purpose: that one means "already
        /// beaten and already paid out", and so skips loot too. This one means
        /// "never was a fight", and the floor still owns everything it holds.
        /// </summary>
        public bool peaceful;

        /// <summary>Bit per DungeonRoom.index: that treasure room has already been emptied.</summary>
        public ulong treasureTakenMask;

        /// <summary>
        /// Bit per DungeonRoom.index: that combat room was fought out (or its
        /// room event used) on an earlier visit. It regenerates empty, with no
        /// event. Without it, leaving a floor before the boss died refilled
        /// every room, a repeatable source of kill XP, corpses and event loot.
        /// </summary>
        public ulong roomSpentMask;

        /// <summary>
        /// This floor's one-off resources (mana ore, debris and boulder
        /// scatters) were already generated on an earlier visit. The scatter
        /// steps skip themselves, so mining a floor out is final.
        /// </summary>
        public bool suppressResources;

        /// <summary>
        /// Signature of the room layout the masks above were recorded against
        /// (0 = unknown). The masks are keyed by room INDEX, and the layout is
        /// not a pure function of the seed: room-count and map-size settings,
        /// or a def change, renumber the rooms. When the regenerated layout's
        /// signature differs, the masks are ignored instead of being applied to
        /// the wrong rooms.
        /// </summary>
        public int layoutSignature;

        public override string Label
        {
            get
            {
                // "F-rank dungeon" — the kind label already reads "dungeon break".
                string kindLabel = kindDef?.label ?? "dungeon";
                return $"{MobRankUtility.GetRankString(rank)}-rank {kindLabel}";
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Defs.Look(ref kindDef, "kindDef");
            Scribe_Values.Look(ref layoutSeed, "layoutSeed", -1);
            Scribe_Values.Look(ref rankIndex, "rankIndex", -1f);
            Scribe_Values.Look(ref roomCountFactor, "roomCountFactor", 1f);
            Scribe_Values.Look(ref encounterFactor, "encounterFactor", 1f);
            Scribe_Values.Look(ref treasureRoomsOverride, "treasureRoomsOverride", -1);
            Scribe_Values.Look(ref suppressPopulation, "suppressPopulation", defaultValue: false);
            Scribe_Values.Look(ref peaceful, "peaceful", defaultValue: false);
            Scribe_Values.Look(ref treasureTakenMask, "treasureTakenMask", 0UL);
            Scribe_Values.Look(ref roomSpentMask, "roomSpentMask", 0UL);
            Scribe_Values.Look(ref suppressResources, "suppressResources", defaultValue: false);
            Scribe_Values.Look(ref layoutSignature, "layoutSignature", 0);
        }
    }
}
