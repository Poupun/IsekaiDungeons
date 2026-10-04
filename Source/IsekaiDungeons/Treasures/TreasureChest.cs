using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace IsekaiDungeons
{
    /// <summary>
    /// The treasure chest: a haulable item that drops where the boss died when
    /// a dungeon or tower floor is cleared, and opens - anywhere, including the
    /// colony - into the reel ceremony in Window_TreasureReel.
    ///
    /// FOUR TIERS, one per band of the rank ladder (F-C, B-A, S-SS,
    /// SSS+Nation). The tier lives on CompProperties_TreasureChest so the four
    /// defs share this one class, and everything downstream (rarity odds,
    /// refinement, runes) keys off it.
    ///
    /// THE ROLL IS COMMITTED WHEN THE LID OPENS, not when the player clicks
    /// Take. Window_TreasureReel pre-rolls its winner in its constructor and
    /// the reel animation merely lands on it - the CS:GO model, and also the
    /// crash-safe one: closing the window early, or the window dying with the
    /// game, can never eat a better roll than the one already made. An early
    /// close still awards (see the window's OnClosed).
    /// </summary>
    public class TreasureChest : ThingWithComps
    {
        /// <summary>Visual state only: the lid. The chest is consumed by Award, not by opening.</summary>
        private bool opened;

        private Graphic openGraphicInt;

        public int Tier => GetComp<CompTreasureChest>()?.Props.tier ?? 1;

        public bool Opened => opened;

        /// <summary>
        /// The "_Open" texture, resolved lazily against this def's own path so
        /// every tier finds its matching art by convention rather than by a
        /// second field that could drift.
        /// </summary>
        private Graphic OpenGraphic
        {
            get
            {
                if (openGraphicInt == null)
                {
                    openGraphicInt = GraphicDatabase.Get<Graphic_Single>(
                        def.graphicData.texPath + "_Open",
                        def.graphicData.Graphic.Shader,
                        def.graphicData.drawSize,
                        DrawColor);
                }
                return openGraphicInt ?? base.Graphic;
            }
        }

        public override Graphic Graphic => opened ? OpenGraphic : base.Graphic;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref opened, "opened", defaultValue: false);
        }

        // ── Opening ─────────────────────────────────────────────────────────

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
                yield return o;

            // 1.6 routes this through FloatMenuOptionProvider_FromThing, so an
            // override here is still the whole feature - no provider needed.
            if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
            {
                yield return new FloatMenuOption(
                    "IsekaiDungeons.Treasure.OpenNoPath".Translate(LabelShort.Named("CHEST")), null);
                yield break;
            }

            yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(
                "IsekaiDungeons.Treasure.Open".Translate(LabelShort.Named("CHEST")),
                () =>
                {
                    var job = JobMaker.MakeJob(
                        DefDatabase<JobDef>.GetNamed("IsekaiDungeons_OpenTreasureChest"), this);
                    selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }), selPawn, this);
        }

        /// <summary>
        /// Called by the job when the pawn has stood at the chest long enough:
        /// the lid comes off and the ceremony starts.
        ///
        /// `opened` is DELIBERATELY not a gate. If the game died mid-reel the
        /// chest survives looking open but was never consumed - the roll it had
        /// made is gone with the window, so opening it again is the only way
        /// the player ever gets what they are owed.
        /// </summary>
        public void BeginOpening(Pawn opener)
        {
            if (Destroyed)
                return;

            opened = true;
            if (Spawned)
            {
                // Items are printed into the map mesh, so a Graphic change is
                // invisible until the section is rebuilt.
                Map.mapDrawer.MapMeshDirty(Position, MapMeshFlagDefOf.Things);
                SoundDefOf.CryptosleepCasket_Eject.PlayOneShot(new TargetInfo(Position, Map));
            }

            Find.WindowStack.Add(new Window_TreasureReel(this, opener));
        }

        /// <summary>
        /// Delivers the rolled item beside the chest and consumes the chest.
        /// One call per chest by construction: the window guards its own
        /// awarded flag, and Destroy makes a second call impossible anyway.
        /// </summary>
        public void Award(Thing item)
        {
            if (item == null || Destroyed)
                return;

            IntVec3 cell = PositionHeld;
            Map map = MapHeld;
            if (map != null && cell.IsValid)
            {
                GenPlace.TryPlaceThing(item, cell, map, ThingPlaceMode.Near, out Thing placed);
                Messages.Message(
                    "IsekaiDungeons.Treasure.Awarded".Translate(item.LabelCap.Named("ITEM")),
                    placed ?? item, MessageTypeDefOf.PositiveEvent);
            }
            Destroy();
        }
    }

    public class CompProperties_TreasureChest : CompProperties
    {
        /// <summary>1..4. Drives rarity odds, refinement range and rune power.</summary>
        public int tier = 1;

        public CompProperties_TreasureChest()
        {
            compClass = typeof(CompTreasureChest);
        }
    }

    public class CompTreasureChest : ThingComp
    {
        public CompProperties_TreasureChest Props => (CompProperties_TreasureChest)props;

        public override string CompInspectStringExtra() =>
            "IsekaiDungeons.Treasure.Inspect".Translate(Props.tier.Named("TIER"));
    }

    /// <summary>
    /// Walk to the chest, stand at it for a moment, lift the lid. The wait is
    /// the anticipation beat - a lid that popped the instant the pawn arrived
    /// would waste the whole ceremony the reel exists for.
    /// </summary>
    public class JobDriver_OpenTreasureChest : JobDriver
    {
        private const int OpenTicks = 90;

        private TreasureChest Chest => job.targetA.Thing as TreasureChest;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnForbidden(TargetIndex.A);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil open = Toils_General.Wait(OpenTicks, TargetIndex.A);
            open.WithProgressBarToilDelay(TargetIndex.A);
            open.handlingFacing = true;
            open.tickAction = () => pawn.rotationTracker.FaceTarget(job.targetA);
            yield return open;

            yield return Toils_General.Do(() => Chest?.BeginOpening(pawn));
        }
    }
}
