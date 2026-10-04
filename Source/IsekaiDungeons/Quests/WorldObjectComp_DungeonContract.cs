using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    public class WorldObjectCompProperties_DungeonContract : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_DungeonContract()
        {
            compClass = typeof(WorldObjectComp_DungeonContract);
        }
    }

    /// <summary>
    /// The per-site memory of a guild dungeon contract: which dungeon is behind
    /// the gate, at what rank, and in what state the player will find it.
    ///
    /// WHY A COMP AND NOT THE QUEST PART. The quest is bookkeeping; the SITE is
    /// the thing the player travels to, and it has to be able to answer "what
    /// is here" on its own. A map can be generated from a save in which the
    /// quest was already completed, abandoned, or lost to a mod change, and the
    /// site still has to produce a coherent dungeon rather than an empty field.
    /// Hanging the data on the world object means the site is self-sufficient
    /// and the quest part is free to be about the contract instead of about
    /// content.
    /// </summary>
    public class WorldObjectComp_DungeonContract : WorldObjectComp
    {
        public MobRankTier rank = MobRankTier.F;
        public DungeonKindDef kindDef;
        public DungeonQuestKind questKind = DungeonQuestKind.Located;

        /// <summary>Faction whose settlement this broke open on, for the rescue variant.</summary>
        public Faction rescueFaction;

        /// <summary>
        /// Set once a gate has been placed on this site at least once. Kept for
        /// save compatibility and as history only: staging checks the current
        /// map for a gate instead, because the site map (and its gate) is
        /// removed when the party leaves and regenerated on the next visit.
        /// </summary>
        public bool gatePlaced;

        /// <summary>
        /// True once the contract is over, however it ended.
        ///
        /// Read by DungeonContractSite.ShouldRemoveMapNow: while this is false
        /// the site refuses to be destroyed alongside its map, so a party can
        /// walk away from a gate and come back to it. Kept on the COMP rather
        /// than only on the quest part because the site has to be able to
        /// answer the question with no quest present at all.
        /// </summary>
        public bool contractResolved;

        /// <summary>Tick the contract was posted, for the orphan backstop.</summary>
        public int postedTick = -1;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Defs.Look(ref kindDef, "kindDef");
            Scribe_Values.Look(ref questKind, "questKind", DungeonQuestKind.Located);
            Scribe_References.Look(ref rescueFaction, "rescueFaction");
            Scribe_Values.Look(ref gatePlaced, "gatePlaced", defaultValue: false);
            Scribe_Values.Look(ref contractResolved, "contractResolved", defaultValue: false);
            Scribe_Values.Look(ref postedTick, "postedTick", -1);

            // Same defensive re-default every other kindDef consumer in this mod
            // performs: Scribe_Defs resolves through GetNamedSilentFail, so a
            // renamed def or an uninstalled content pack loads as null and every
            // downstream dereference throws outside its guard.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && kindDef == null)
            {
                kindDef = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon contract site loaded with an unresolvable kindDef; "
                          + $"falling back to {kindDef.defName} at rank {rank}.");
            }
        }

        public override string CompInspectStringExtra()
        {
            string rankStr = MobRankUtility.GetRankString(rank);
            switch (questKind)
            {
                case DungeonQuestKind.Broken:
                    return "IsekaiDungeons.Site.Inspect.Broken".Translate(rankStr.Named("RANK"));
                case DungeonQuestKind.FactionRescue:
                    return "IsekaiDungeons.Site.Inspect.Rescue".Translate(
                        rankStr.Named("RANK"),
                        (rescueFaction?.Name ?? "?").Named("FACTION"));
                default:
                    return "IsekaiDungeons.Site.Inspect.Located".Translate(rankStr.Named("RANK"));
            }
        }
    }
}
