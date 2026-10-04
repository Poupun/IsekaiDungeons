using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Raises the tower.
    ///
    /// Fires ONCE per playthrough: TowerComponent.Exists gates it, and the
    /// tower only stops existing when the player finishes or abandons it. A
    /// second tower standing next to the first would make nonsense of a
    /// hundred-floor climb.
    ///
    /// Gated on colony rank rather than on days survived. A tower whose first
    /// floor already outclasses the colony is not a challenge, it is a wall,
    /// and unlike a dungeon gate there is no countdown pressuring the player to
    /// enter before they are ready.
    ///
    /// It raises a PLACE ON THE PLANET, not a building in the base. The tower
    /// gets its own world tile and the player has to travel to it — which is
    /// what makes climbing it an expedition you outfit and commit to, rather
    /// than a door in the yard.
    /// </summary>
    public class IncidentWorker_TowerRises : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms))
                return false;
            if (!TowerTunables.Enabled)
                return false;

            var map = parms.target as Map;
            if (map == null || !map.IsPlayerHome || map.mapPawns.FreeColonistsCount == 0)
                return false;

            TowerComponent comp = TowerComponent.Get;
            if (comp == null || comp.Exists)
                return false;

            MobRankTier best = RaidRankSystem.GetColonyBestRank(map);
            return (int)best >= TowerTunables.MinColonyRankIndex;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            TowerComponent comp = TowerComponent.Get;
            if (comp == null || comp.Exists)
                return false;

            WorldObject_Tower tower = WorldObject_Tower.TryRaise(Rand.Int);
            if (tower == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No world tile available for the tower; incident skipped.");
                return false;
            }

            SendStandardLetter(
                "IsekaiDungeons.Letter.TowerRises.Label".Translate(),
                "IsekaiDungeons.Letter.TowerRises.Text".Translate(TowerTunables.FloorCount.Named("FLOORS")),
                IsekaiDungeonsDefOf.IsekaiDungeons_TowerRises, parms, tower);
            return true;
        }
    }
}
