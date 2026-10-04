using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse.AI;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The way up, standing in the boss room at the far end of every floor.
    ///
    /// NOT A MapPortal, and that is deliberate. Vanilla portals cannot nest —
    /// GeneratePocketMap nulls currentlyGeneratingPortal instead of restoring
    /// it — so a stair that generated its own map would break the binding for
    /// the floor's exit. Instead the stair asks the gate to open the next floor
    /// and moves the party across itself: about forty lines, fully under our
    /// control, and it keeps the gate as the single owner of every floor map.
    ///
    /// It is also where DISCOVERY happens. That is load-bearing: if merely
    /// arriving on a floor unlocked the next one in the picker, a player could
    /// step onto floor N, walk back out, and travel straight to N+1 — skipping
    /// N's boss entirely. Only this building advances the frontier, and it
    /// refuses to work until the floor's objective is met.
    /// </summary>
    public class Building_TowerStair : Building
    {
        /// <summary>Which floor this stair stands on. It leads to floor + 1.</summary>
        public int floor;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floor, "floor", 0);
        }

        /// <summary>
        /// Has this floor been beaten? The RECORD is the authority, not the map:
        /// a floor the player already cleared regenerates deliberately empty,
        /// with no boss to kill, so asking the map alone would seal the stair on
        /// a floor that was beaten days ago.
        /// </summary>
        private bool FloorBeaten
        {
            get
            {
                TowerFloorRecord rec = TowerComponent.Get?.RecordIfSeen(floor);
                if (rec != null && rec.cleared)
                    return true;
                // Some archetypes are landings, not fights — their stair is
                // open the moment you arrive.
                if (rec?.archetype != null && !rec.archetype.sealStairUntilCleared)
                    return true;
                DungeonMapComponent comp = Map?.GetComponent<DungeonMapComponent>();
                if (comp == null)
                    return true;
                return comp.IsCleared || comp.GeneratedEmpty;
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            var climb = new Command_Action
            {
                defaultLabel = "IsekaiDungeons.Tower.Climb".Translate((floor + 1).Named("FLOOR")),
                defaultDesc = "IsekaiDungeons.Tower.ClimbDesc".Translate(),
                // The open stair, not the live Graphic: the sealed art on a
                // disabled button would read as a second "locked" signal.
                icon = def.uiIcon,
                action = Climb,
            };
            if (floor >= TowerTunables.FloorCount)
                climb.Disable("IsekaiDungeons.Tower.Summit".Translate());
            else if (!FloorBeaten)
                climb.Disable("IsekaiDungeons.Tower.StairSealed".Translate());
            yield return climb;
        }

        /// <summary>
        /// Right-click to climb, not just a gizmo.
        ///
        /// A gizmo is a property of the SELECTED THING; a float menu option is
        /// a property of the SELECTED PAWN and the thing under the cursor,
        /// which is how every other "go and do something at that" interaction
        /// in RimWorld works. Climbing reads as an order you give people, so it
        /// belongs here — the gizmo stays as well, because it is the only place
        /// the SEALED reason can be shown.
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
                yield return o;

            if (selPawn == null || selPawn.Faction != Faction.OfPlayer)
                yield break;

            string label = "IsekaiDungeons.Tower.Climb".Translate((floor + 1).Named("FLOOR"));

            if (floor >= TowerTunables.FloorCount)
            {
                yield return new FloatMenuOption(
                    label + " (" + "IsekaiDungeons.Tower.Summit".Translate() + ")", null);
                yield break;
            }
            if (!FloorBeaten)
            {
                yield return new FloatMenuOption(
                    label + " (" + "IsekaiDungeons.Tower.StairSealed".Translate() + ")", null);
                yield break;
            }
            if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
            {
                yield return new FloatMenuOption(
                    label + " (" + "NoPath".Translate() + ")", null);
                yield break;
            }

            // Climbing takes the WHOLE party, so ordering one pawn to do it is
            // really ordering the expedition to move. Walking there first is
            // what makes that read as a decision rather than a teleport.
            yield return new FloatMenuOption(label, delegate { OrderClimb(selPawn); });

            // GOD MODE: any floor, immediately, from anywhere in the tower.
            // Skips the walk as well as the ladder — when the thing under test
            // is floor 88's generation, walking to the stair is not part of it.
            if (DebugSettings.godMode)
            {
                yield return new FloatMenuOption(
                    "IsekaiDungeons.Tower.ClimbAnyDev".Translate(), delegate
                    {
                        Find.WindowStack.Add(new Dialog_TowerFloorPicker(JumpTo, allFloors: true));
                    });
            }
        }

        /// <summary>
        /// The pawn who was ordered to climb. Climbing fires when they arrive,
        /// so the order behaves like every other "go there and do it" job
        /// rather than teleporting the party the instant it is issued.
        /// </summary>
        private Pawn pendingClimber;

        // ── Autopilot surface ───────────────────────────────────────────────
        // TowerAutopilot drives the SAME walk-then-climb path the float menu
        // does, through these, so an automated ascent is indistinguishable from
        // an ordered one — same pending-climber handshake, same seal checks in
        // Tick, same Climb.

        /// <summary>This stair can be climbed right now (not the summit, floor beaten).</summary>
        public bool ClimbAvailable => floor < TowerTunables.FloorCount && FloorBeaten;

        /// <summary>The summit has no next floor; the grind ends here.</summary>
        public bool SummitStair => floor >= TowerTunables.FloorCount;

        /// <summary>A climb order is already in flight; don't stack another.</summary>
        public bool HasPendingClimber => pendingClimber != null;

        /// <summary>
        /// Send this pawn to the stair; arrival triggers the party ascent.
        /// The single path both the float menu and the autopilot use.
        /// </summary>
        public void OrderClimb(Pawn pawn)
        {
            if (pawn == null || pawn.jobs == null)
                return;
            var job = JobMaker.MakeJob(JobDefOf.Goto, this);
            job.playerForced = true;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            pendingClimber = pawn;
        }

        // ── Look ────────────────────────────────────────────────────────────

        private Graphic sealedGraphicInt;
        private bool sealedCached;
        private int sealedCheckFrame = -1;

        /// <summary>
        /// The "_Sealed" texture, resolved against this def's own path the way
        /// the treasure chests find their "_Open" art, so the pair cannot drift.
        /// </summary>
        private Graphic SealedGraphic
        {
            get
            {
                if (sealedGraphicInt == null)
                {
                    sealedGraphicInt = GraphicDatabase.Get<Graphic_Single>(
                        def.graphicData.texPath + "_Sealed",
                        def.graphicData.Graphic.Shader,
                        def.graphicData.drawSize,
                        DrawColor);
                }
                return sealedGraphicInt ?? base.Graphic;
            }
        }

        /// <summary>
        /// Sealed while the floor still objects, and on the summit, where there
        /// is nowhere to go. Re-read every thirty frames rather than every
        /// frame: the record lookup is cheap, but this runs on every draw, and a
        /// seal that lifts half a second late is invisible.
        /// </summary>
        private bool LooksSealed
        {
            get
            {
                if (UnityEngine.Time.frameCount >= sealedCheckFrame)
                {
                    sealedCached = floor >= TowerTunables.FloorCount || !FloorBeaten;
                    sealedCheckFrame = UnityEngine.Time.frameCount + 30;
                }
                return sealedCached;
            }
        }

        /// <summary>
        /// A stair, not a portal. The open art shows the Tower's light pouring
        /// out of the door at the top; the sealed art closes that door behind the
        /// Tower's star. The def draws in realtime so the swap lands the moment
        /// the floor is beaten, with no map mesh to rebuild.
        /// </summary>
        public override Graphic Graphic => LooksSealed ? SealedGraphic : base.Graphic;


        protected override void Tick()
        {
            base.Tick();
            if (pendingClimber == null || !this.IsHashIntervalTick(15))
                return;
            if (pendingClimber.Dead || pendingClimber.Map != Map)
            {
                pendingClimber = null;
                return;
            }
            if (pendingClimber.Position.AdjacentTo8WayOrInside(this))
            {
                pendingClimber = null;
                if (FloorBeaten)
                    Climb();
            }
            else if (pendingClimber.CurJobDef != JobDefOf.Goto)
            {
                pendingClimber = null;   // they were given something else to do
            }
        }

        /// <summary>
        /// Moves everyone standing on this floor up one.
        ///
        /// ORDER MATTERS. The next floor is generated FIRST, while the party is
        /// still safely standing on this one; only once a real map exists does
        /// anybody move; and the old floor is retired last. Generating after
        /// despawning would leave the party nowhere if generation threw.
        /// </summary>
        /// <summary>
        /// Dev jump: open any floor and take the whole party there, bypassing
        /// both the seal and the ladder.
        ///
        /// Routed through the SAME TowerFloors.Open / Retire pair the real climb
        /// uses, so a jumped-to floor is generated, populated and recorded
        /// exactly as a climbed-to one — which is what makes it worth testing on.
        /// </summary>
        private void JumpTo(int floor)
        {
            Map here = Map;
            if (here == null || floor < 1 || floor > TowerTunables.FloorCount)
                return;

            Map target = TowerFloors.Open(floor);
            if (target == null)
            {
                Messages.Message("IsekaiDungeons.Tower.ClimbFailed".Translate(),
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            TowerFloors.Retire(here, target);
            TowerComponent.Get?.NotifyReached(floor);

            IntVec3 arrival = TowerFloors.ArrivalCell(target);
            Current.Game.CurrentMap = target;
            Find.CameraDriver.JumpToCurrentMapLoc(arrival);
        }

        private void Climb()
        {
            Map here = Map;
            int next = floor + 1;
            if (here == null || next > TowerTunables.FloorCount)
                return;

            // Decided BEFORE the move, because Retire is about to destroy
            // `here` and the answer changes under our feet after that.
            bool watching = Find.CurrentMap == here;

            // ORDER MATTERS. The next floor is generated FIRST, while the party
            // is still standing safely on this one. Only once a real map exists
            // does anybody move — generating after despawning would leave the
            // party nowhere if generation threw.
            Map nextMap = TowerFloors.Open(next);
            if (nextMap == null)
            {
                Messages.Message("IsekaiDungeons.Tower.ClimbFailed".Translate(),
                                 this, MessageTypeDefOf.RejectInput);
                return;
            }

            // Retire moves EVERY player pawn and every body to the destination,
            // so a downed colonist or a corpse comes up with the party rather
            // than being deleted with the floor.
            TowerFloors.Retire(here, nextMap);
            TowerComponent.Get?.NotifyReached(next);

            IntVec3 arrival = TowerFloors.ArrivalCell(nextMap);
            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Tower.Ascended.Label".Translate(next.Named("FLOOR")),
                "IsekaiDungeons.Tower.Ascended.Text".Translate(
                    next.Named("FLOOR"),
                    (TowerComponent.Get?.EnsureRecord(next)?.Label ?? string.Empty).Named("NAME")),
                LetterDefOf.NeutralEvent,
                new TargetInfo(arrival, nextMap));

            // THE CAMERA ONLY FOLLOWS A CLIMB THE PLAYER WAS WATCHING.
            //
            // The whole point of the auto-grind is that the party farms the
            // tower while the player runs the colony — and this pair of lines,
            // applied unconditionally, yanked them back to the tower on every
            // single floor. The rule is about attention, not about who ordered
            // the climb: if the retiring floor was the current map, the player
            // was watching and the view must move (it is about to be destroyed
            // under them anyway); if they were somewhere else, the ascended
            // letter above carries the jump target and the colony keeps the
            // screen. Manual climbs get the same treatment for free, and are
            // almost always in the "watching" case.
            if (watching)
            {
                Current.Game.CurrentMap = nextMap;
                Find.CameraDriver.JumpToCurrentMapLoc(arrival);
            }
        }

        public override string GetInspectString()
        {
            return FloorBeaten
                ? "IsekaiDungeons.Tower.StairOpen".Translate((floor + 1).Named("FLOOR"))
                : "IsekaiDungeons.Tower.StairSealed".Translate();
        }
    }
}
