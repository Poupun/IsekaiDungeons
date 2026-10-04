using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What a tower floor IS when nobody is standing on it.
    ///
    /// There is no third state in RimWorld between "live and ticking" and
    /// "gone": Map.Dispose frees native memory and TickManager.DoSingleTick
    /// walks Find.Maps with no per-map gate. A hundred floors therefore cannot
    /// coexist, and "travel back to floor 23" cannot mean "the map is still
    /// there". So a dormant floor is a RECIPE plus a DELTA — about a hundred
    /// bytes of XML — and re-entering rebuilds it.
    ///
    /// The recipe reproduces EXACTLY, which is the part that makes this
    /// honest rather than a re-roll. Two verified properties do the work:
    ///
    ///  1. DungeonLayoutPlan.Generate is a pure function of
    ///     (mapSize, seed, kindDef, rank) — it brackets itself in
    ///     Rand.PushState/PopState and touches no Map, Find or Current.
    ///  2. Vanilla seeds a pocket map from HashCombineInt(worldSeed, parent.ID)
    ///     and re-seeds EVERY gen step from that, including Plants, Fog and the
    ///     scatter steps. So pinning the parent's ID pins the whole map, not
    ///     just the parts this mod generates.
    ///
    /// Together: the same floor comes back, down to where the grass grew.
    /// </summary>
    public class TowerFloorRecord : IExposable
    {
        /// <summary>1-based. Floor 1 is the first one above the entrance hall.</summary>
        public int floor;

        // ── Identity (rolled once, then frozen) ─────────────────────────────

        public TowerFloorArchetypeDef archetype;
        /// <summary>The dungeon this floor borrowed. Carries roster, bosses, loot and objective.</summary>
        public DungeonKindDef kindDef;
        /// <summary>Discrete rank — roster minDepth gating and creature normalisation need a real enum.</summary>
        public MobRankTier rank = MobRankTier.F;
        /// <summary>Continuous ladder position, so 100 floors get 100 difficulty steps out of 10 curve anchors.</summary>
        public float rankIndex;
        /// <summary>Edge length of the floor's map.</summary>
        public int mapSize = 160;

        // ── Recipe ──────────────────────────────────────────────────────────

        /// <summary>Pins DungeonLayoutPlan: rooms, corridors, wings, shortcuts, roles, depths.</summary>
        public int layoutSeed;
        /// <summary>
        /// Pins the map parent's ID, and through it vanilla's own seed for
        /// every gen step. Allocated from a reserved descending range so it can
        /// never collide with UniqueIDsManager, which counts up from zero.
        /// </summary>
        public int parentId;

        // ── Delta: what the player changed ──────────────────────────────────

        /// <summary>Named for what it generated: "Floor 23 - Village".</summary>
        public string name;

        /// <summary>Seen from below (the picker lists it) — set when the floor is first entered.</summary>
        public bool discovered;

        /// <summary>
        /// Objective completed. A cleared floor regenerates EMPTY: no boss, no
        /// roster, no treasure. That is the anti-farm property and it is
        /// structural rather than a timer — the mod pays rewards per KILL, so
        /// any repopulation at all would be an income stream.
        /// </summary>
        public bool cleared;

        /// <summary>Bit per DungeonRoom.index: that treasure room has been emptied and stays empty.</summary>
        public ulong treasureTakenMask;

        /// <summary>Bit per DungeonRoom.index: that combat room was fought out or its event used. Stays empty.</summary>
        public ulong roomSpentMask;

        /// <summary>Ore and scatter resources were generated once already; later visits get none.</summary>
        public bool resourcesGenerated;

        /// <summary>Layout the room masks belong to. See DungeonMapParent.layoutSignature.</summary>
        public int layoutSignature;

        /// <summary>
        /// Whether this floor's village still stands. One enum survives the
        /// destroy-and-rebuild cycle where a list of pawns could not — so a
        /// village the player massacred comes back as ruins, permanently,
        /// rather than quietly repopulating as though nothing happened.
        /// </summary>
        public TowerVillageState villageState = TowerVillageState.None;

        public string Label => name.NullOrEmpty() ? $"Floor {floor}" : name;

        public void ExposeData()
        {
            Scribe_Values.Look(ref floor, "floor", 0);
            Scribe_Defs.Look(ref archetype, "archetype");
            Scribe_Defs.Look(ref kindDef, "kindDef");
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Values.Look(ref rankIndex, "rankIndex", 0f);
            Scribe_Values.Look(ref mapSize, "mapSize", 160);
            Scribe_Values.Look(ref layoutSeed, "layoutSeed", 0);
            Scribe_Values.Look(ref parentId, "parentId", 0);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref discovered, "discovered", defaultValue: false);
            Scribe_Values.Look(ref cleared, "cleared", defaultValue: false);
            Scribe_Values.Look(ref treasureTakenMask, "treasureTakenMask", 0UL);
            Scribe_Values.Look(ref roomSpentMask, "roomSpentMask", 0UL);
            Scribe_Values.Look(ref resourcesGenerated, "resourcesGenerated", defaultValue: false);
            Scribe_Values.Look(ref layoutSignature, "layoutSignature", 0);
            Scribe_Values.Look(ref villageState, "villageState", TowerVillageState.None);
        }
    }
}
