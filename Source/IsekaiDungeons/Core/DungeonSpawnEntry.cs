using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// One line of a dungeon's monster roster.
    ///
    /// The kind is stored as a STRING, not a PawnKindDef, on purpose: a dungeon
    /// may list creatures from the Isekai Creatures add-on (a soft dependency)
    /// or from a DLC. A direct &lt;PawnKindDef&gt; reference would make RimWorld's
    /// def resolver throw a red error for every missing entry at load; a string
    /// resolves lazily via GetNamedSilentFail and simply drops out of the roster
    /// when its mod is absent. A dungeon whose whole roster drops out marks
    /// itself unavailable and is never offered (see DungeonKindDef.Available).
    /// </summary>
    public class DungeonSpawnEntry
    {
        /// <summary>PawnKindDef defName. Missing defs are skipped, not errors.</summary>
        public string kind;

        /// <summary>Relative pick weight inside the roster.</summary>
        public float weight = 1f;

        /// <summary>
        /// Earliest room-depth fraction (0 = first combat room, 1 = last) this
        /// entry may appear at. Lets one roster ramp itself: chaff at 0, the
        /// nastier kinds gated to the back half with e.g. 0.5.
        /// </summary>
        public float minDepth;

        public override string ToString() => $"{kind} (w{weight}, depth>={minDepth})";
    }
}
