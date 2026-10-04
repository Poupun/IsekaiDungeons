using RimWorld;
using RimWorld.Planet;   // PlanetTile, on the trader hook's signature
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Floors a dungeon artifact's quality at Good.
    ///
    /// WHY THIS EXISTS. The artifacts are balanced so that GOOD quality is the
    /// baseline - every published DPS figure for them is the Good number - and
    /// quality climbs from there to Legendary. That only holds if nothing can
    /// hand out a worse one.
    ///
    /// Decompiled from 1.6, CompQuality has NO PostPostMake: its field simply
    /// starts at Normal and only ever changes when something calls SetQuality.
    /// So the paths that matter are:
    ///
    ///   ThingMaker.MakeThing        -> Normal (x1.00), one step under our floor
    ///   MakePrize (the reel)        -> Good..Legendary, already correct
    ///   PostPostGeneratedForTrader  -> GenerateQualityTraderItem(), can be awful
    ///
    /// so two of the three need catching. PostPostMake covers the first, and
    /// the trader override covers the third.
    ///
    /// ORDER MATTERS IN THE DEF. This comp must be listed AFTER CompQuality:
    /// comps are walked in def order and the trader hook fires on both, so ours
    /// has to be the one that runs last or the floor gets rolled straight back
    /// off again.
    /// </summary>
    public class CompArtifactQuality : ThingComp
    {
        private const QualityCategory Floor = QualityCategory.Good;

        private void Raise()
        {
            CompQuality q = parent.TryGetComp<CompQuality>();
            if (q != null && (int)q.Quality < (int)Floor)
                q.SetQuality(Floor, ArtGenerationContext.Outsider);
        }

        public override void PostPostMake()
        {
            base.PostPostMake();
            Raise();
        }

        public override void PostPostGeneratedForTrader(
            TraderKindDef trader, PlanetTile forTile, Faction forFaction)
        {
            base.PostPostGeneratedForTrader(trader, forTile, forFaction);
            Raise();
        }
    }
}
