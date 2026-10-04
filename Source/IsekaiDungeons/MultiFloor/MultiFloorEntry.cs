using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsekaiDungeons
{
    /// <summary>
    /// The way onward, standing in the boss room at the far end of every floor
    /// but the last.
    ///
    /// Not a MapPortal, for the same reason the tower's stair is not: vanilla
    /// portals cannot nest — GeneratePocketMap nulls currentlyGeneratingPortal
    /// instead of restoring it — so a stair that generated its own map would
    /// break the binding for the floor's own exit. It asks the owning dungeon
    /// to open the next floor instead.
    /// </summary>
    public class Building_MultiFloorStair : Building
    {
        /// <summary>The floor this stair stands on. It leads to floor + 1.</summary>
        public int floor;

        /// <summary>
        /// The pawn ordered onto the stair, if any.
        ///
        /// The order WALKS somebody over and the arrival triggers the party
        /// move, rather than teleporting everyone the instant it is issued.
        /// Not scribed: an order in flight across a save is not worth the
        /// state, and the worst case is the player re-issuing it.
        /// </summary>
        private Pawn pendingMover;

        // ── Autopilot surface ───────────────────────────────────────────────
        // The autopilot drives the SAME walk-then-move path the float menu
        // does, through these, so an automated descent is indistinguishable
        // from an ordered one.

        /// <summary>This stair can be taken right now: not the bottom, floor beaten.</summary>
        public bool DescendAvailable => Plan != null && !Plan.IsLastFloor(floor) && FloorBeaten;

        /// <summary>The bottom floor has no stair at all; the run ends there.</summary>
        public bool BottomStair => Plan == null || Plan.IsLastFloor(floor);

        /// <summary>An order is already in flight; do not stack another.</summary>
        public bool HasPendingMover => pendingMover != null;

        /// <summary>Send this pawn to the stair; arrival moves the party. One path for menu and autopilot.</summary>
        public void OrderDescend(Pawn pawn)
        {
            if (pawn?.jobs == null)
                return;
            var job = JobMaker.MakeJob(JobDefOf.Goto, this);
            job.playerForced = true;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            pendingMover = pawn;
        }

        protected override void Tick()
        {
            base.Tick();
            if (pendingMover == null || !this.IsHashIntervalTick(15))
                return;
            if (pendingMover.Dead || pendingMover.Map != Map)
            {
                pendingMover = null;
                return;
            }
            if (pendingMover.Position.AdjacentTo8WayOrInside(this))
            {
                pendingMover = null;
                if (FloorBeaten)
                    Descend();
            }
            else if (pendingMover.CurJobDef != JobDefOf.Goto)
            {
                pendingMover = null;   // they were given something else to do
            }
        }

        private MultiFloorMapParent Parent => Map?.Parent as MultiFloorMapParent;
        private WorldObject_MultiFloorDungeon Dungeon => Parent?.Owner;
        private MultiFloorDungeonDef Plan => Dungeon?.Def;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floor, "floor", 0);
        }

        /// <summary>
        /// The RECORD is the authority, not the map. A floor already beaten
        /// regenerates deliberately empty, so asking the map alone would seal
        /// the stair on a floor that was cleared days ago.
        /// </summary>
        private bool FloorBeaten
        {
            get
            {
                MultiFloorRecord rec = Dungeon?.RecordIfSeen(floor);
                if (rec != null && rec.cleared)
                    return true;
                DungeonMapComponent comp = Map?.GetComponent<DungeonMapComponent>();
                if (comp == null)
                    return true;
                return comp.IsCleared || comp.GeneratedEmpty;
            }
        }

        // ── Look ────────────────────────────────────────────────────────────

        private bool sealedCached;
        private int sealedCheckFrame = -1;

        /// <summary>
        /// Re-read every thirty frames, not every frame: this runs on every draw,
        /// and a seal that lifts half a second late is invisible. Same as the
        /// tower stair.
        /// </summary>
        private bool LooksSealed
        {
            get
            {
                if (UnityEngine.Time.frameCount >= sealedCheckFrame)
                {
                    sealedCached = !FloorBeaten;
                    sealedCheckFrame = UnityEngine.Time.frameCount + 30;
                }
                return sealedCached;
            }
        }

        /// <summary>
        /// The dungeon's own stair, "Onward" or "Onward_Sealed" in its
        /// structureArt folder. The def draws in realtime so the swap lands the
        /// moment the floor is beaten, with no map mesh to rebuild.
        /// </summary>
        public override Graphic Graphic =>
            Plan?.StructureGraphic(LooksSealed ? "Onward_Sealed" : "Onward", def.graphicData.drawSize)
            ?? base.Graphic;

        // The def says "descending"; the Choir Spire's stair climbs.
        public override string LabelNoCount =>
            (Plan?.descends ?? true) ? base.LabelNoCount : "IsekaiDungeons.MultiFloor.StairUpLabel".Translate().ToString();

        public override string DescriptionFlavor =>
            (Plan?.descends ?? true) ? base.DescriptionFlavor : "IsekaiDungeons.MultiFloor.StairUpDescription".Translate().ToString();

        private string OnwardLabel =>
            (Plan?.descends ?? true)
                ? "IsekaiDungeons.MultiFloor.Descend".Translate((floor + 1).Named("FLOOR"))
                : "IsekaiDungeons.MultiFloor.Ascend".Translate((floor + 1).Named("FLOOR"));

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            var go = new Command_Action
            {
                defaultLabel = OnwardLabel,
                defaultDesc = "IsekaiDungeons.MultiFloor.OnwardDesc".Translate(),
                // This dungeon's own open stair (the button already greys out
                // while sealed); the def icon when the dungeon has no art.
                icon = (Plan?.StructureGraphic("Onward", def.graphicData.drawSize)?.MatSingle?.mainTexture as UnityEngine.Texture2D)
                       ?? def.uiIcon,
                action = Descend,
            };
            if (Plan == null || Plan.IsLastFloor(floor))
                go.Disable("IsekaiDungeons.MultiFloor.Bottom".Translate());
            else if (!FloorBeaten)
                go.Disable("IsekaiDungeons.MultiFloor.StairSealed".Translate());
            yield return go;
        }

        /// <summary>
        /// Right-click as well as a gizmo. Going deeper reads as an order you
        /// give people, and a float menu option is a property of the selected
        /// PAWN, which is how every other "go and do that" interaction works.
        /// The gizmo stays because it is the only place the sealed reason shows.
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
                yield return o;

            if (selPawn == null || selPawn.Faction != Faction.OfPlayer)
                yield break;

            string label = OnwardLabel;
            if (Plan == null || Plan.IsLastFloor(floor))
            {
                yield return new FloatMenuOption(
                    label + " (" + "IsekaiDungeons.MultiFloor.Bottom".Translate() + ")", null);
                yield break;
            }
            if (!FloorBeaten)
            {
                yield return new FloatMenuOption(
                    label + " (" + "IsekaiDungeons.MultiFloor.StairSealed".Translate() + ")", null);
                yield break;
            }
            if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
            {
                yield return new FloatMenuOption(label + " (" + "NoPath".Translate() + ")", null);
                yield break;
            }

            // Walk there, THEN move the party. Descending takes everybody, so
            // ordering one pawn to do it is really ordering the expedition to
            // move, and walking over is what makes that read as a decision
            // rather than a teleport.
            yield return new FloatMenuOption(label, () => OrderDescend(selPawn));
        }

        /// <summary>
        /// Move the whole party one floor on.
        ///
        /// ORDER MATTERS. The next floor is generated FIRST, while the party is
        /// still standing safely on this one; only once a real map exists does
        /// anybody move. Generating after despawning would leave the party
        /// nowhere at all if generation threw.
        /// </summary>
        public void Descend()
        {
            Map here = Map;
            WorldObject_MultiFloorDungeon dungeon = Dungeon;
            MultiFloorDungeonDef plan = Plan;
            if (here == null || dungeon == null || plan == null || plan.IsLastFloor(floor))
                return;

            bool watching = Find.CurrentMap == here;
            int next = floor + 1;

            // The spine sees the floor being left BEFORE the next one is built,
            // so anything it records is available to the next floor's generation.
            MultiFloorFloors.NotifyFloorLeft(dungeon, floor, here);

            Map nextMap = MultiFloorFloors.Open(dungeon, next);
            if (nextMap == null)
            {
                Messages.Message("IsekaiDungeons.MultiFloor.OnwardFailed".Translate(),
                                 this, MessageTypeDefOf.RejectInput);
                return;
            }

            // Retire moves every player pawn AND every body to the destination,
            // so a downed colonist or a corpse travels with the party rather
            // than being deleted with the floor.
            MultiFloorFloors.Retire(here, nextMap);

            IntVec3 arrival = TowerFloors.ArrivalCell(nextMap);
            MultiFloorFloors.NotifyFloorEntered(dungeon, next, nextMap);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.Arrived.Label".Translate(next.Named("FLOOR")),
                "IsekaiDungeons.MultiFloor.Arrived.Text".Translate(
                    next.Named("FLOOR"),
                    plan.floorCount.Named("TOTAL"),
                    plan.LabelCap.Named("PLACE")),
                LetterDefOf.NeutralEvent,
                new TargetInfo(arrival, nextMap));

            // The camera only follows a move the player was actually watching:
            // the whole point of auto-clear is that the party grinds while you
            // do something else.
            if (watching)
                CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(arrival, nextMap));
        }
    }

    /// <summary>
    /// Walking a caravan into a multi-floor dungeon.
    ///
    /// There is no building at the mouth and no ground floor to cross: the
    /// dungeon IS its floors, so arriving puts you on one. Which one depends on
    /// how far you have already got — clearing floors banks progress, the same
    /// way the tower's does.
    /// </summary>
    public class CaravanArrivalAction_EnterMultiFloor : CaravanArrivalAction
    {
        private WorldObject_MultiFloorDungeon dungeon;

        public CaravanArrivalAction_EnterMultiFloor() { }

        public CaravanArrivalAction_EnterMultiFloor(WorldObject_MultiFloorDungeon dungeon)
        {
            this.dungeon = dungeon;
        }

        public override string Label =>
            "IsekaiDungeons.MultiFloor.Enter".Translate(
                (dungeon?.Def?.LabelCap ?? string.Empty).Named("PLACE"));

        public override string ReportString =>
            "IsekaiDungeons.MultiFloor.EnterReport".Translate(
                (dungeon?.Def?.LabelCap ?? string.Empty).Named("PLACE"));

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport rep = base.StillValid(caravan, destinationTile);
            if (!rep)
                return rep;
            if (dungeon == null || !dungeon.Spawned || dungeon.Def == null)
                return false;
            return CanEnter(caravan, dungeon);
        }

        public static FloatMenuAcceptanceReport CanEnter(Caravan caravan, WorldObject_MultiFloorDungeon dungeon)
        {
            if (dungeon?.Def == null)
                return false;
            // A floor already open means somebody is inside. Two parties on
            // different floors of one dungeon is a state nothing else in the
            // mod supports, so it is refused rather than half-handled.
            if (MultiFloorFloors.LiveFloorOf(dungeon) != null)
                return FloatMenuAcceptanceReport.WithFailReason(
                    "IsekaiDungeons.MultiFloor.AlreadyInside".Translate());
            return true;
        }

        public override void Arrived(Caravan caravan)
        {
            int floor = dungeon.NextOpenFloor;
            Map map = MultiFloorFloors.Open(dungeon, floor);
            if (map == null)
            {
                Messages.Message("IsekaiDungeons.MultiFloor.OnwardFailed".Translate(),
                                 MessageTypeDefOf.RejectInput);
                return;
            }

            IntVec3 cell = TowerFloors.ArrivalCell(map);
            CaravanEnterMapUtility.Enter(caravan, map, x => cell, draftColonists: true);
            MultiFloorFloors.NotifyFloorEntered(dungeon, floor, map);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.Arrived.Label".Translate(floor.Named("FLOOR")),
                "IsekaiDungeons.MultiFloor.Arrived.Text".Translate(
                    floor.Named("FLOOR"),
                    dungeon.Def.floorCount.Named("TOTAL"),
                    dungeon.Def.LabelCap.Named("PLACE")),
                LetterDefOf.NeutralEvent,
                new TargetInfo(cell, map));
        }

        public static IEnumerable<FloatMenuOption> GetFloatMenuOptions(
            Caravan caravan, WorldObject_MultiFloorDungeon dungeon)
        {
            return CaravanArrivalActionUtility.GetFloatMenuOptions(
                () => CanEnter(caravan, dungeon),
                () => new CaravanArrivalAction_EnterMultiFloor(dungeon),
                "IsekaiDungeons.MultiFloor.Enter".Translate(dungeon.Def.LabelCap.Named("PLACE")),
                caravan, dungeon.Tile, dungeon);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref dungeon, "dungeon");
        }
    }
}
