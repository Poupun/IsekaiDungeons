using System.Collections.Generic;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// One carved room of a dungeon. Created by GenStep_DungeonLayout, populated
    /// by GenStep_DungeonPopulation, owned and deep-scribed by DungeonMapComponent.
    ///
    /// The CellRect is scribed as four ints rather than relying on CellRect
    /// string-parsing support — bulletproof across game versions.
    ///
    /// creatures are saved as REFERENCES: the pawns themselves are saved by the
    /// pocket map like any other spawned pawn; the room only remembers which of
    /// them belong to its aggro group. Dead/despawned entries are pruned lazily
    /// by the map component, never during ExposeData.
    /// </summary>
    public class DungeonRoom : IExposable
    {
        public CellRect rect;
        public int index;
        public DungeonRoomRole role = DungeonRoomRole.Combat;
        public List<Pawn> creatures = new List<Pawn>();
        public bool aggroed;

        /// <summary>
        /// Where this zone's group physically stands. Chosen at population time
        /// and reused for every member so the encounter clusters instead of
        /// smearing across a large Open zone. Also the leash's home point.
        /// </summary>
        public IntVec3 groupAnchor = IntVec3.Invalid;

        /// <summary>
        /// Room event assigned at generation, if any. Scribed by def so the
        /// runtime hook (traps, ambushes) still works after a save/load.
        /// </summary>
        public DungeonRoomEventDef eventDef;

        /// <summary>Set once the party has physically entered — drives the one-shot event hook.</summary>
        public bool entered;

        /// <summary>Encounter budget this room was built with; replayed to runtime events.</summary>
        public float eventBudget;

        /// <summary>
        /// The room's ramped tier when its event was assigned, as an int so
        /// -1 can mean "unknown" (saves from before it was recorded fall back
        /// to the gate rank, the old behaviour).
        /// </summary>
        public int eventTier = -1;

        /// <summary>
        /// 0 = the way in, 1 = the boss. Progress along the dungeon, NOT a list
        /// position — once a layout branches, list position stops meaning depth.
        /// Written by DungeonLayoutPlan, which is the only party that knows the
        /// geometry.
        /// </summary>
        public float depthFrac;

        /// <summary>
        /// True when depthFrac was written by the layout planner.
        ///
        /// THIS EXISTS FOR OLD SAVES. Before branching, depthFrac was filled in
        /// by DungeonRoomEventSelector inside its event-capped walk, so only the
        /// first floor(n * 0.34) rooms ever got a value and every other room
        /// scribed 0. Anything that reads depthFrac after a load must therefore
        /// ask whether it can be trusted, or it will treat most of a
        /// pre-existing dungeon as depth zero.
        /// </summary>
        public bool depthAuthored;

        /// <summary>
        /// False for rooms on an optional branch — a wing the party can walk
        /// past. Optionality is a BOOLEAN, deliberately not smuggled into
        /// depthFrac as a smaller number: a wing off a deep room is still deep,
        /// it is just skippable.
        /// </summary>
        public bool onCriticalPath = true;

        /// <summary>
        /// Nudges this room's creature tier above the depth ramp. Used for a
        /// wing's terminal guard. Applied INSIDE the existing clamp, so it can
        /// never push a creature past the gate's advertised rank.
        /// </summary>
        public int tierOffset;

        /// <summary>
        /// Multiplier on this room's treasure value. Sub-linear in wing length
        /// on purpose — see DungeonTunables.WingLootBonusPerRoom.
        /// </summary>
        public float lootScale = 1f;

        public DungeonRoom()
        {
        }

        public DungeonRoom(CellRect rect, int index, DungeonRoomRole role)
        {
            this.rect = rect;
            this.index = index;
            this.role = role;
        }

        public void ExposeData()
        {
            int minX = rect.minX, minZ = rect.minZ, width = rect.Width, height = rect.Height;
            Scribe_Values.Look(ref minX, "minX");
            Scribe_Values.Look(ref minZ, "minZ");
            Scribe_Values.Look(ref width, "width");
            Scribe_Values.Look(ref height, "height");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                rect = new CellRect(minX, minZ, width, height);

            Scribe_Values.Look(ref index, "index");
            Scribe_Values.Look(ref role, "role", DungeonRoomRole.Combat);
            Scribe_Values.Look(ref aggroed, "aggroed", defaultValue: false);
            Scribe_Values.Look(ref groupAnchor, "groupAnchor", IntVec3.Invalid);
            Scribe_Defs.Look(ref eventDef, "eventDef");
            Scribe_Values.Look(ref entered, "entered", defaultValue: false);
            Scribe_Values.Look(ref eventBudget, "eventBudget", 0f);
            Scribe_Values.Look(ref eventTier, "eventTier", -1);
            Scribe_Values.Look(ref depthFrac, "depthFrac", 0f);
            // Defaults chosen so a pre-branching save loads as exactly what it
            // was: a fully-critical-path linear dungeon with untrusted depth.
            Scribe_Values.Look(ref depthAuthored, "depthAuthored", defaultValue: false);
            Scribe_Values.Look(ref onCriticalPath, "onCriticalPath", defaultValue: true);
            Scribe_Values.Look(ref tierOffset, "tierOffset", 0);
            Scribe_Values.Look(ref lootScale, "lootScale", 1f);
            Scribe_Collections.Look(ref creatures, "creatures", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && creatures == null)
                creatures = new List<Pawn>();
        }
    }
}
