using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The standing mood penalty for being inside a dungeon that is still
    /// alive, staged by its rank.
    ///
    /// This is the counterweight that makes the clear thought land. A pure
    /// reward would be a free +15 for going somewhere; with this, the crawl
    /// costs mood the whole time you are down there and clearing it both lifts
    /// the penalty and pays the spike. Relief plus elation, which is what
    /// finishing a dungeon actually feels like.
    ///
    /// TWO GUARDS THAT ARE NOT OPTIONAL:
    ///
    ///  - PEACEFUL FLOORS. TowerFloorArchetypeDef's own validator states that a
    ///    peaceful archetype can never be completed ("with no boss the floor
    ///    can never be completed"), so `cleared` stays false there forever.
    ///    Without this check, a colonist browsing the tower village would carry
    ///    permanent dread with no way to ever remove it.
    ///
    ///  - CLEARED. Once the dungeon is settled the map is just a room with
    ///    loot in it, and standing in it should cost nothing.
    ///
    /// Performance: situational thoughts are re-polled per colonist on a timer,
    /// so this does no scanning — a type check, a field read and a component
    /// fetch off the map's own short component list. Nothing here walks pawns
    /// or cells, which is why it needs none of the caching the base mod's
    /// identity worker uses.
    /// </summary>
    public class ThoughtWorker_DungeonDread : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (p == null || !p.Spawned || !IsekaiDungeonsSettings.Current.dungeonDreadEnabled)
                return ThoughtState.Inactive;

            Map map = p.Map;
            if (map == null)
                return ThoughtState.Inactive;

            DungeonMapParent parent = map.Parent as DungeonMapParent;
            if (parent == null || parent.peaceful)
                return ThoughtState.Inactive;

            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp == null || comp.Cleared)
                return ThoughtState.Inactive;

            return ThoughtState.ActiveAtStage(DungeonMoods.ThreatStage(comp.Rank));
        }
    }
}
