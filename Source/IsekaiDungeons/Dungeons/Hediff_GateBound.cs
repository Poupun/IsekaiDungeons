using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Carried by a dungeon boss that walked out with the final breach wave.
    /// Killing it on the colony map settles the dungeon: the same clear loot
    /// and the same clear XP the player would have got for beating it in its
    /// own hall.
    ///
    /// WHY A HEDIFF AND NOT A TRACKER. The reward has to outlive the thing that
    /// owed it — by the time the boss is out, the pocket map is gone, the map
    /// component with it, and the gate destroys itself moments later. A hediff
    /// rides the pawn instead: it scribes with the pawn for free, survives any
    /// number of saves, is cleaned up when the pawn is, and hooks death through
    /// Pawn.Kill -> HediffSet.Notify_PawnDied -> here. No polling, no registry,
    /// nothing to migrate.
    ///
    /// It is also deliberately VISIBLE in the health tab. A player who has just
    /// been handed a rank-S monster on their doorstep should be able to see
    /// that killing this specific creature is worth something.
    ///
    /// Known limit: a boss REMOVED rather than killed (destroyed outright,
    /// despawned by another mod) never fires Notify_PawnDied and pays nothing.
    /// Sealing it into a Sealing Cube is the interesting case, and that is
    /// handled where the cube handles everything else — it counts as a kill for
    /// the base mod's own quest targets, so the same call can settle this.
    /// </summary>
    public class Hediff_GateBound : HediffWithComps
    {
        public MobRankTier rank = MobRankTier.F;
        public DungeonKindDef kindDef;

        /// <summary>Latched so a double death notification cannot pay twice.</summary>
        private bool settled;

        // Plain rank string, not the coloured one: the health tab does not
        // reliably parse rich text in a hediff label, and a raw <color> tag on
        // screen is worse than no colour at all.
        public override string LabelBase =>
            "IsekaiDungeons.Hediff.GateBound.Label".Translate(
                MobRankUtility.GetRankString(rank).Named("RANK"));

        public override bool ShouldRemove => false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Defs.Look(ref kindDef, "kindDef");
            Scribe_Values.Look(ref settled, "settled", defaultValue: false);
        }

        public override void Notify_PawnDied(DamageInfo? dinfo, Hediff culprit = null)
        {
            base.Notify_PawnDied(dinfo, culprit);
            Settle();
        }

        /// <summary>
        /// Pays out. Public so the same reward can be granted by anything else
        /// that legitimately "beats" the boss without killing it.
        /// </summary>
        public void Settle()
        {
            if (settled || kindDef == null || pawn == null)
                return;
            settled = true;

            // MapHeld rather than Map: the boss may already be a corpse in
            // somebody's arms by the time this runs.
            Map map = pawn.MapHeld;
            if (map == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} {pawn.LabelShortCap} was gate-bound but died off-map; " +
                            $"the dungeon reward could not be placed.");
                return;
            }

            int xpEach = DungeonTunables.ScaleXp(kindDef.LootWorker.ClearXpPerColonist(rank, kindDef), map);
            IntVec3 center = pawn.PositionHeld.IsValid ? pawn.PositionHeld : map.Center;

            // Same rule as an in-dungeon clear: full XP, no headcount split —
            // but only to the colonists who were actually THERE. An in-dungeon
            // clear pays the handful who went in; paying every colonist on the
            // map would make killing the boss on your doorstep worth several
            // times more than beating it in its own hall.
            List<Pawn> onMap = map.mapPawns.FreeColonistsSpawned;
            var credited = new List<Pawn>();
            for (int i = 0; i < onMap.Count; i++)
            {
                if (onMap[i].Position.InHorDistOf(center, DungeonTunables.BreachXpRadius))
                    credited.Add(onMap[i]);
            }
            // Killed by a trap, a turret, or something else with nobody stood
            // nearby: pay the colony rather than silently dropping the reward.
            if (credited.Count == 0)
                credited.AddRange(onMap);

            for (int i = 0; i < credited.Count; i++)
                IsekaiComponent.GetCached(credited[i])?.GainXP(xpEach, "dungeon boss slain");

            // Mood goes to the whole map, not just the credited fighters. XP is
            // deliberately local so killing the boss at home cannot out-earn
            // diving for it, but the relief of watching the thing die on your
            // own ground is felt by everyone who was living under it.
            DungeonMoods.GainBeastPutDown(map);

            // If this boss walked out of a guild contract's gate, killing it IS
            // completing the contract. The gate is already gone by now, so the
            // contract's own Cleared check can never fire.
            QuestPart_DungeonContract.NotifyDungeonSettled(map);

            // The hoard falls where the boss did. Note what is NOT here:
            // treasure rooms and room-event caches only ever existed inside the
            // dungeon, so beating the boss on your own ground pays the headline
            // reward but never the full haul. Diving still pays more.
            List<Thing> loot = DungeonLootGenerator.WithValueScale(DungeonTunables.LootMultiplierFor(map),
                                                                   () => kindDef.LootWorker.GenerateClearLoot(rank, kindDef))
                               ?? new List<Thing>();
            Thing lookTarget = null;
            for (int i = 0; i < loot.Count; i++)
            {
                if (GenPlace.TryPlaceThing(loot[i], center, map, ThingPlaceMode.Near, out Thing placed))
                {
                    if (lookTarget == null)
                        lookTarget = placed;
                }
                else
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not place breach reward {loot[i].LabelCap}; " +
                                $"retrying at the map centre.");
                    GenPlace.TryPlaceThing(loot[i], map.Center, map, ThingPlaceMode.Near);
                }
            }

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Letter.BossSlain.Label".Translate(
                    MobRankUtility.GetRankString(rank).Named("RANK")),
                "IsekaiDungeons.Letter.BossSlain.Text".Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    (kindDef.label ?? "dungeon").Named("DUNGEON"),
                    xpEach.ToString("N0").Named("XP")),
                LetterDefOf.PositiveEvent,
                lookTarget != null ? new TargetInfo(lookTarget.Position, map) : new TargetInfo(center, map));
        }

        /// <summary>
        /// Attaches the bounty to a boss on its way out of a breaching gate.
        /// Idempotent: a boss can only be bound once.
        /// </summary>
        public static void Bind(Pawn boss, DungeonKindDef kind, MobRankTier rank)
        {
            if (boss == null || boss.Dead || kind == null || boss.health == null)
                return;
            if (boss.health.hediffSet.GetFirstHediffOfDef(IsekaiDungeonsDefOf.IsekaiDungeons_GateBound) != null)
                return;

            var hediff = HediffMaker.MakeHediff(IsekaiDungeonsDefOf.IsekaiDungeons_GateBound, boss) as Hediff_GateBound;
            if (hediff == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} IsekaiDungeons_GateBound is not a Hediff_GateBound; " +
                            $"the escaped boss will pay no reward.");
                return;
            }
            hediff.rank = rank;
            hediff.kindDef = kind;
            boss.health.AddHediff(hediff);
        }

        /// <summary>Settles the bounty on a boss that was beaten without being killed.</summary>
        public static void SettleOn(Pawn boss)
        {
            if (boss?.health == null)
                return;
            (boss.health.hediffSet.GetFirstHediffOfDef(IsekaiDungeonsDefOf.IsekaiDungeons_GateBound)
                as Hediff_GateBound)?.Settle();
        }
    }
}
