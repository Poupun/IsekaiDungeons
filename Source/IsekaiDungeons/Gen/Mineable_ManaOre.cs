using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Mana ore. Vanilla Mineable already drops the small mana cores named in
    /// the def (mineableThing); this adds a rank-scaled chance of one full mana
    /// core on top.
    ///
    /// The hook is DeSpawn, not Destroy: Mineable.DestroyMined calls
    /// base.Destroy directly, so a Destroy override never sees a mined cell,
    /// while DeSpawn is virtual and runs on both paths. KillFinalize is the
    /// mined-or-destroyed mode; the layout carve and map teardown use Vanish,
    /// and those must drop nothing.
    /// </summary>
    public class Mineable_ManaOre : Mineable
    {
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            IntVec3 cell = Position;
            base.DeSpawn(mode);

            if (mode != DestroyMode.KillFinalize || map == null)
                return;
            if (!Rand.Chance(DungeonTunables.ManaOreCoreChanceFor(map)))
                return;

            ThingDef coreDef = DefDatabase<ThingDef>.GetNamedSilentFail(DungeonTunables.ManaOreBonusCoreDefName);
            if (coreDef != null)
                GenPlace.TryPlaceThing(ThingMaker.MakeThing(coreDef), cell, map, ThingPlaceMode.Near);
        }
    }
}
