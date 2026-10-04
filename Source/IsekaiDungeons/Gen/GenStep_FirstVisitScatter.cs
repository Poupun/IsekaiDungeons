using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Vanilla scatter steps that only run on a floor's FIRST generation.
    ///
    /// Tower and multi-floor maps are destroyed when the party leaves and
    /// regenerated from a pinned seed on the next visit, so anything a scatter
    /// step places comes back every time. For mana ore (a core per cell, plus
    /// a full core on a roll) and steel slag that turned a beaten floor into a
    /// repeatable mine. DungeonMapParent.suppressResources is set from the
    /// floor record once the floor has been generated once; ordinary gate
    /// dungeons are single-visit and never set it.
    /// </summary>
    public class GenStep_ScatterLumpsMineableFirstVisit : GenStep_ScatterLumpsMineable
    {
        public override void Generate(Map map, GenStepParams parms)
        {
            if (map.Parent is DungeonMapParent p && p.suppressResources)
                return;
            base.Generate(map, parms);
        }
    }

    /// <inheritdoc cref="GenStep_ScatterLumpsMineableFirstVisit"/>
    public class GenStep_ScatterThingsFirstVisit : GenStep_ScatterThings
    {
        public override void Generate(Map map, GenStepParams parms)
        {
            if (map.Parent is DungeonMapParent p && p.suppressResources)
                return;
            base.Generate(map, parms);
        }
    }
}
