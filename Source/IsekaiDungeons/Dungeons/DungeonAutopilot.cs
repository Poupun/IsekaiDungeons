using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsekaiDungeons
{
    /// <summary>
    /// The auto-grind toggle: point the party at a dungeon and let it clear.
    ///
    /// One brain, two places it runs. On a TOWER FLOOR it is the climb loop —
    /// clear the floor, walk to the stair, ascend, repeat, a hundred times.
    /// In an ORDINARY GATE DUNGEON it is the same loop with a different
    /// ending: when nothing registered in the dungeon is left alive, it
    /// switches itself off and stands the party down, and the hauling-out is
    /// the player's again. The toggle is shown only inside dungeon maps
    /// (tower floors included), because that is the only place it means
    /// anything.
    ///
    /// The floor loop, priority-ordered:
    ///
    ///   FIGHT    — any woken hostile gets engaged: melee pawns are ordered
    ///              onto the nearest one, ranged pawns are WALKED to a firing
    ///              position and then left alone — a drafted pawn's own
    ///              auto-targeting is better at picking targets frame-to-frame
    ///              than any order queue, so the autopilot moves the shooter
    ///              and lets vanilla shoot.
    ///   PUT DOWN — a visible dungeon creature that auto-fire will not touch
    ///              still needs killing, and two kinds qualify. NEUTRALS:
    ///              DungeonMapComponent stops its aggro sweep the moment the
    ///              CLEAR fires (MapComponentTick early-returns on `cleared`),
    ///              so every den the party had not opened when the boss died
    ///              holds creatures that will never wake again. DOWNED: an
    ///              unconscious creature is not a dead one — drafted
    ///              auto-targeting skips it, and the clear objective does not:
    ///              a boss that went DOWN instead of dying seals the stair
    ///              until someone finishes it. Both get EXPLICIT orders —
    ///              AttackMelee (killIncappedTarget on), or AttackStatic for
    ///              shooters.
    ///   EXPLORE  — nothing visible: walk AT the nearest still-fogged
    ///              creature itself, not at its room's centre. The first
    ///              version walked to the registration room's centre, and a
    ///              creature that had MOVED — chased and got leashed home down
    ///              an unwalked corridor, wandered an open field, stood behind
    ///              a side-room door — left the pawn parked on an empty centre
    ///              cell forever, re-issuing a Goto to the spot it already
    ///              stood on (and retoggling changed nothing, because the
    ///              stale destination was re-derived every time). Walking at
    ///              the creature's position closes the loop by construction:
    ///              approach unfogs it, and the moment it is visible the FIGHT
    ///              or PUT DOWN tier takes over. Aggro still works the same —
    ///              the room wakes when the pawn steps into its rect on the
    ///              way in.
    ///   FINISH   — nothing registered left alive anywhere:
    ///                tower floor  → one pawn walks to the stair and the whole
    ///                               party ascends (that is what the stair
    ///                               already does), and the loop starts over
    ///                               on the next floor;
    ///                gate dungeon → the toggle switches itself off for every
    ///                               pawn on this map, they stand down, and a
    ///                               message says so.
    ///
    /// IT DRIVES THE GAME'S OWN CONTROLS AND NOTHING ELSE. Every action is an
    /// ordered job on a drafted pawn — the same Goto, AttackMelee and
    /// AttackStatic the player would issue by right-click — plus the stair's
    /// own climb path. No think tree edits, no custom JobDefs, no duties.
    /// That is what makes it safe to interrupt at any moment: toggling off,
    /// or just giving a pawn a manual order, leaves a perfectly ordinary
    /// drafted colonist standing wherever they were, and a manual order is
    /// DETECTED and respected (the autopilot re-issues nothing until the
    /// player's job finishes).
    ///
    /// NEEDS ARE HANDLED BY LETTING GO. A drafted pawn will starve standing
    /// up, and a hundred floors take days. When food or rest runs low and no
    /// hostile is awake, the autopilot undrafts the pawn and stops issuing
    /// orders; their own AI then eats from inventory and sleeps on the
    /// ground, exactly as an unattended caravan pawn would. The moment
    /// something wakes — or the needs recover — the pawn is drafted back and
    /// the loop resumes. The party grinds at the pace of its packed lunches,
    /// which is the correct limiting resource for an expedition.
    ///
    /// WHAT IT NEVER TOUCHES: beast-vault animals. They are deliberately
    /// neither hostile nor room-registered (GenStep_DungeonPopulation), so
    /// every tier of target selection is blind to them — the autopilot walks
    /// past the caged wyvern instead of picking that fight, and a live vault
    /// beast does not block the finish.
    ///
    /// Toggle state lives in TowerComponent (scribed, per-pawn — that
    /// component exists for every game and already owns the tick that drives
    /// this); everything here is runtime scratch that rebuilds after a load.
    /// </summary>
    public static class DungeonAutopilot
    {
        /// <summary>Decision cadence. Half a second: snappy enough to read as intent, far above per-tick cost.</summary>
        private const int IntervalTicks = 30;

        private static IsekaiDungeonsSettings S => IsekaiDungeonsSettings.Current;

        /// <summary>Food/rest levels that pause the grind (only while nothing is awake). Settings, Tower tab.</summary>
        private static float FoodPauseBelow => S.autopilotFoodPauseBelow;
        private static float RestPauseBelow => S.autopilotRestPauseBelow;

        /// <summary>
        /// Per-need resume points. THE RESUME GATE IS PER NEED, and that is the
        /// fix for a real stall: the first version demanded BOTH needs above
        /// one bar, which left a pawn who paused hungry — ate, and now stood at
        /// 95% food but 60% rest — in a dead band where they neither resumed
        /// (rest under the bar) nor slept (their own AI only beds down near the
        /// pause floor). They idled for in-game hours while the finished floor
        /// waited; fighting still resumed, because threat overrides a pause.
        /// That is exactly the "fights but never climbs" shape players saw.
        /// Now only the needs that actually caused (or joined) the pause must
        /// recover. Settings keep each resume point above its pause point.
        /// </summary>
        private static float FoodResumeAt => S.autopilotFoodResumeAt;
        private static float RestResumeAt => S.autopilotRestResumeAt;

        /// <summary>
        /// A paused pawn who is AWAKE and NOT EATING for this long resumes
        /// anyway — the failsafe for "the packs are empty": a pawn who cannot
        /// reach their resume point because there is nothing left to eat should
        /// grind on hungry rather than stand still forever. One in-game hour;
        /// the clock resets whenever they are actively recovering. A setting.
        /// </summary>
        private static int PauseIdleTimeoutTicks => Mathf.RoundToInt(S.autopilotIdleTimeoutHours * GenDate.TicksPerHour);

        /// <summary>Stand-and-shoot band: inside this fraction of verb range (with LoS), a ranged pawn is left to auto-fire. A setting.</summary>
        private static float RangedHoldFraction => S.autopilotRangedHoldFraction;

        /// <summary>The job we most recently issued per pawn — reference compare distinguishes OUR orders from the player's.</summary>
        private static readonly Dictionary<Pawn, Job> issued = new Dictionary<Pawn, Job>();

        /// <summary>What a paused pawn is recovering from. Runtime scratch: after a load the needs check re-derives it.</summary>
        private sealed class PauseState
        {
            public bool food;
            public bool rest;
            /// <summary>TicksGame when last seen actively recovering (asleep or mid-meal).</summary>
            public int idleSince;
        }

        /// <summary>Pawns currently released to feed/sleep, and why.</summary>
        private static readonly Dictionary<Pawn, PauseState> paused = new Dictionary<Pawn, PauseState>();

        /// <summary>
        /// No new pause for this pawn before this tick. Set by the idle
        /// failsafe, and it is what makes the failsafe actually work: a
        /// timeout resume leaves the need still under its floor, so without a
        /// snooze the very next interval re-paused and undrafted the pawn —
        /// forever, in a loop. The pawn lived on their schedule (wandering,
        /// meditation slots) and only fought what walked up to them, which is
        /// exactly the "autopilot stopped doing anything" it looked like from
        /// outside. Six in-game hours: long enough to grind on hungry, short
        /// enough to try the packs again the same day.
        /// </summary>
        private static readonly Dictionary<Pawn, int> pauseSnoozeUntil = new Dictionary<Pawn, int>();

        private static int PauseSnoozeTicks => Mathf.RoundToInt(S.autopilotSnoozeHours * GenDate.TicksPerHour);

        /// <summary>
        /// How long the autopilot will stand aside for a forced job it did not
        /// issue before taking the wheel back.
        ///
        /// THIS BOUND EXISTS BECAUSE DEFERENCE WAS UNBOUNDED AND SOMETHING ELSE
        /// WAS DRIVING. "A playerForced job that is not mine" was read as "the
        /// player is giving orders", which is true for a right-click and FALSE
        /// for any other mod that orders drafted pawns around — Search and
        /// Destroy being the obvious one, since it exists precisely to make
        /// drafted colonists go hunt on their own. Against a mod like that the
        /// old check went quiet forever and never logged a thing, which is
        /// exactly the reported symptom: the pawn kills whatever it walks into
        /// (that mod's doing) and otherwise wanders (its schedule), while the
        /// autopilot politely waits for a turn that never comes.
        ///
        /// Three seconds is long enough that a real right-click still feels
        /// respected, and short enough that nothing can hold the wheel. The
        /// player's own escape hatch is the toggle, which is instant. A setting.
        /// </summary>
        private static int DeferMaxTicks => Mathf.RoundToInt(S.autopilotDeferSeconds * 60f);

        /// <summary>When each pawn started deferring. Cleared whenever we act.</summary>
        private static readonly Dictionary<Pawn, int> deferSince = new Dictionary<Pawn, int>();

        /// <summary>What each pawn's loop decided last, so the dev log prints only on CHANGE.</summary>
        private static readonly Dictionary<Pawn, string> lastDecision = new Dictionary<Pawn, string>();

        /// <summary>
        /// One line per pawn per change of state, dev mode only. After three
        /// rounds of guessing at which predicate was lying, the loop now simply
        /// says what it is doing.
        /// </summary>
        private static void Decide(Pawn pawn, string decision)
        {
            if (!Prefs.DevMode)
                return;
            if (lastDecision.TryGetValue(pawn, out string prev) && prev == decision)
                return;
            lastDecision[pawn] = decision;
            Log.Message($"{IsekaiDungeonsMod.LogPrefix} autopilot [{pawn.LabelShort}] -> {decision}");
        }

        private static readonly List<Pawn> tmpPawns = new List<Pawn>();

        // ────────────────────────────────────────────────────────────────────
        //  Toggle surface
        // ────────────────────────────────────────────────────────────────────

        public static bool IsOn(Pawn pawn) =>
            TowerComponent.Get?.AutopilotOn(pawn) ?? false;

        /// <summary>
        /// Drops all per-pawn runtime state. Called from
        /// TowerComponent.FinalizeInit: the dictionaries are static and keyed
        /// by Pawn, so without this a loaded game kept the previous game's
        /// pawns alive in memory and their stale entries forever.
        /// </summary>
        public static void ResetRuntimeState()
        {
            issued.Clear();
            paused.Clear();
            pauseSnoozeUntil.Clear();
            deferSince.Clear();
            lastDecision.Clear();
        }

        public static void SetOn(Pawn pawn, bool on)
        {
            TowerComponent.Get?.SetAutopilot(pawn, on);
            if (!on)
            {
                issued.Remove(pawn);
                paused.Remove(pawn);
                pauseSnoozeUntil.Remove(pawn);
                deferSince.Remove(pawn);
                lastDecision.Remove(pawn);
                // Draft state is deliberately left as-is: toggling off mid-fight
                // must not undraft someone standing next to a manhunter.
            }
        }

        /// <summary>
        /// Same toggle, dressed for where it stands: the tower reads as a
        /// climb, a gate dungeon reads as a sweep. One store behind both, so a
        /// pawn toggled in the tower is toggled in the next gate too.
        /// </summary>
        public static Gizmo GizmoFor(Pawn pawn, bool towerFloor)
        {
            return new Command_Toggle
            {
                defaultLabel = (towerFloor
                    ? "IsekaiDungeons.Tower.Autopilot.Label"
                    : "IsekaiDungeons.Gate.Autopilot.Label").Translate(),
                defaultDesc = (towerFloor
                    ? "IsekaiDungeons.Tower.Autopilot.Desc"
                    : "IsekaiDungeons.Gate.Autopilot.Desc").Translate(),
                icon = towerFloor ? DungeonAutopilotTex.TowerIcon : DungeonAutopilotTex.GateIcon,
                isActive = () => IsOn(pawn),
                toggleAction = () => SetOn(pawn, !IsOn(pawn)),
            };
        }

        // ────────────────────────────────────────────────────────────────────
        //  The loop — driven from TowerComponent.GameComponentTick
        // ────────────────────────────────────────────────────────────────────

        public static void Tick(TowerComponent tower)
        {
            List<Pawn> pawns = tower.AutopilotPawns;
            if (pawns == null || pawns.Count == 0)
                return;
            if (Find.TickManager.TicksGame % IntervalTicks != 0)
                return;

            // Snapshot: SetOn, death pruning and the gate-dungeon FINISH state
            // all mutate the source list while we walk it.
            tmpPawns.Clear();
            tmpPawns.AddRange(pawns);

            for (int i = 0; i < tmpPawns.Count; i++)
            {
                Pawn pawn = tmpPawns[i];
                if (pawn == null || pawn.Dead)
                {
                    // SetOn, not tower.SetAutopilot: the toggle surface is
                    // what also clears this pawn's runtime entries.
                    if (pawn == null)
                        tower.SetAutopilot(null, false);
                    else
                        SetOn(pawn, false);
                    continue;
                }
                // A FINISH earlier in this same pass may have switched this
                // pawn off already — the snapshot does not know that.
                if (!tower.AutopilotOn(pawn))
                    continue;
                // Off the dungeons the toggle stays set but does nothing — the
                // pawn that steps into the next gate resumes without
                // re-toggling.
                if (!pawn.Spawned || !(pawn.Map.Parent is DungeonMapParent))
                    continue;

                try
                {
                    DrivePawn(pawn, pawn.Map);
                }
                catch (System.Exception ex)
                {
                    // One confused pawn must not kill the whole party's loop.
                    Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Dungeon autopilot failed for {pawn.LabelShort}: {ex}", 0x60D50);
                }
            }
        }

        private static void DrivePawn(Pawn pawn, Map map)
        {
            if (pawn.Downed || pawn.InMentalState || pawn.drafter == null)
                return;

            // A manual order outranks the autopilot, but only for a while:
            // see DeferMaxTicks for why unbounded deference was the bug.
            if (PlayerHasTheWheel(pawn))
            {
                int since = deferSince.TryGetValue(pawn, out int t0) ? t0 : -1;
                if (since < 0)
                {
                    deferSince[pawn] = Find.TickManager.TicksGame;
                    Decide(pawn, "deferring to an order I did not issue");
                    return;
                }
                if (Find.TickManager.TicksGame - since < DeferMaxTicks)
                    return;

                // Long enough. Whatever is issuing these is not a person
                // clicking, so take the wheel back.
                Decide(pawn, "taking the wheel back (deference timed out)");
            }
            deferSince.Remove(pawn);

            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            Pawn target = NearestHostile(map, pawn);

            // ── Needs: release, feed, resume ────────────────────────────────
            if (paused.TryGetValue(pawn, out PauseState ps))
            {
                // Threat interrupts a meal; a finished recovery ends it.
                if (target != null || TryResume(pawn, ps))
                {
                    paused.Remove(pawn);
                    // SNOOZE ON EVERY EXIT, not only on the timeout path. A
                    // threat-interrupted pause used to re-arm the instant the
                    // fight ended, so a pawn with an unfixable need (no food in
                    // the packs, no bed in a dungeon) ping-ponged: pause,
                    // undraft, wander, enemy appears, fight, pause again. That
                    // reads exactly like "kills what it sees, then wanders".
                    // A guaranteed grinding window between pauses ends it.
                    pauseSnoozeUntil[pawn] = Find.TickManager.TicksGame + PauseSnoozeTicks;
                }
                else
                {
                    Decide(pawn, $"paused (food={ps.food} rest={ps.rest})");
                    return;
                }
            }
            else if (target == null && (FoodLow(pawn) || RestLow(pawn))
                     && (!pauseSnoozeUntil.TryGetValue(pawn, out int snooze)
                         || Find.TickManager.TicksGame >= snooze))
            {
                paused.Add(pawn, new PauseState
                {
                    food = FoodLow(pawn),
                    rest = RestLow(pawn),
                    idleSince = Find.TickManager.TicksGame,
                });
                issued.Remove(pawn);
                if (pawn.Drafted)
                    pawn.drafter.Drafted = false;   // their own AI takes it from here
                Decide(pawn, "pausing for needs");
                return;
            }

            // ── FIGHT ───────────────────────────────────────────────────────
            if (target != null)
            {
                EnsureDrafted(pawn);
                Decide(pawn, $"fighting {target.LabelShort}");
                Engage(pawn, target, map, autoFire: true);
                return;
            }

            // ── PUT DOWN — visible creatures auto-fire will not touch ───────
            if (comp != null)
            {
                Pawn quarry = NearestPutDownTarget(comp, pawn, map);
                if (quarry != null)
                {
                    EnsureDrafted(pawn);
                    Decide(pawn, $"putting down {quarry.LabelShort}");
                    Engage(pawn, quarry, map, autoFire: false);
                    return;
                }

                // ── EXPLORE — walk at the hidden creature itself ────────────
                Pawn hidden = NearestHiddenCreature(comp, pawn, map);
                if (hidden != null)
                {
                    EnsureDrafted(pawn);
                    Decide(pawn, $"exploring toward {hidden.LabelShort} at {hidden.Position}");
                    // The destination is re-derived every interval, so a
                    // creature on the move drags the Goto along with it.
                    IssueGoto(pawn, hidden.Position);
                    return;
                }
            }

            // ── FINISH ──────────────────────────────────────────────────────
            if (map.Parent is TowerFloorMapParent)
            {
                // THE STUCK DETECTOR. Reaching FINISH with the stair sealed
                // should be impossible: sealed means the objective is not met,
                // which means something is still alive, which means a tier
                // above should have claimed this interval. Repeated field
                // reports say it happens anyway and every code-reading of the
                // predicates has come up clean — so when the contradiction
                // occurs, dump the predicates themselves. One yellow line per
                // floor per session, built to be pasted into a bug report.
                Building_TowerStair stairProbe = FindStair(map);
                if (stairProbe != null && !stairProbe.SummitStair && !stairProbe.ClimbAvailable)
                    StuckReport(pawn, map, comp);

                FinishTowerFloor(pawn, map);
            }
            else if (map.Parent is MultiFloorMapParent)
                FinishMultiFloor(pawn, map);
            else
                FinishGateDungeon(pawn, map);
        }

        /// <summary>
        /// Why is the autopilot idle while the floor is unbeaten? This prints
        /// the actual state of every living registered creature — the exact
        /// predicates the three finder tiers test — so the failing one is
        /// named by the log instead of guessed at from symptoms.
        /// </summary>
        private static void StuckReport(Pawn pawn, Map map, DungeonMapComponent comp)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(IsekaiDungeonsMod.LogPrefix).Append(" Autopilot stuck-report (floor unbeaten, no target found) — ");

            if (comp == null)
            {
                sb.Append("DungeonMapComponent is NULL on this map.");
                Log.WarningOnce(sb.ToString(), 0x60D70 ^ map.uniqueID);
                return;
            }

            int living = 0, fogged = 0, downed = 0, hostile = 0, inCache = 0;
            var cacheSet = new HashSet<Pawn>();
            foreach (IAttackTarget tgt in map.attackTargetsCache.TargetsHostileToColony)
                if (tgt.Thing is Pawn hp)
                    cacheSet.Add(hp);

            var samples = new List<string>();
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                List<Pawn> creatures = comp.rooms[r].creatures;
                for (int c = 0; c < creatures.Count; c++)
                {
                    Pawn cr = creatures[c];
                    if (cr == null || cr.Dead || !cr.Spawned)
                        continue;
                    living++;
                    bool fog = cr.Position.Fogged(map);
                    bool host = cr.HostileTo(Faction.OfPlayer);
                    bool cache = cacheSet.Contains(cr);
                    if (fog) fogged++;
                    if (cr.Downed) downed++;
                    if (host) hostile++;
                    if (cache) inCache++;
                    if (samples.Count < 3)
                        samples.Add($"{cr.LabelShort}@{cr.Position} fogged={fog} downed={cr.Downed} hostile={host} inCache={cache} reach={pawn.CanReach(cr, PathEndMode.Touch, Danger.Deadly)} mental={cr.MentalStateDef?.defName ?? "none"} faction={cr.Faction?.Name ?? "wild"}");
                }
            }

            sb.Append($"rooms={comp.rooms.Count} livingRegistered={living} (fogged={fogged} downed={downed} hostile={hostile} inAttackCache={inCache}) cacheHostilesTotal={cacheSet.Count} cleared={comp.IsCleared} generatedEmpty={comp.GeneratedEmpty} pawnDrafted={pawn.Drafted} paused={paused.ContainsKey(pawn)}");
            for (int i = 0; i < samples.Count; i++)
                sb.Append("\n    creature ").Append(i + 1).Append(": ").Append(samples[i]);
            if (living == 0)
                sb.Append("\n    (no living registered creatures — the objective worker is what holds the stair, not a missed target)");

            Log.WarningOnce(sb.ToString(), 0x60D70 ^ map.uniqueID);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Finish states
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A multi-floor dungeon wants BOTH of the endings this class already
        /// has, chosen by position: every floor but the last behaves like a
        /// tower floor, and the last behaves like a gate dungeon.
        ///
        /// So the question stops being "am I in the tower?" and becomes "is
        /// there another floor?", which is the question the tower was really
        /// asking all along — the absence of a stair is what ends a run in
        /// both places.
        /// </summary>
        private static void FinishMultiFloor(Pawn pawn, Map map)
        {
            Building_MultiFloorStair stair = FindMultiFloorStair(map);

            // No stair at all is the bottom floor: the run is over, and the
            // party has to be stood down and walked out by hand.
            if (stair == null)
            {
                SwitchOffMap(map);
                Messages.Message("IsekaiDungeons.MultiFloor.Autopilot.BottomOff".Translate(),
                    pawn, MessageTypeDefOf.NeutralEvent);
                return;
            }

            if (stair.HasPendingMover || !stair.DescendAvailable)
                return;   // objective not met yet, or somebody is already walking

            // A SPINE CAN VETO AN UNATTENDED DESCENT, and this is the whole
            // reason the hook exists. A heat or cold ramp does not care that
            // nobody is watching, so a dungeon whose next floor would kill the
            // party as equipped switches the grind off and says why, instead
            // of grinding them to death while the player is elsewhere.
            WorldObject_MultiFloorDungeon dungeon = MultiFloorFloors.OwnerOf(map);
            MultiFloorSpineWorker spine = dungeon?.Def?.Spine;
            if (spine != null)
            {
                string reason = null;
                bool allowed = true;
                try
                {
                    allowed = spine.AllowsUnattendedDescent(
                        new MultiFloorContext(dungeon, MultiFloorFloors.FloorOf(map), map), out reason);
                }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Spine veto check threw: {ex}", 0x60D52);
                }
                if (!allowed)
                {
                    SwitchOffMap(map);
                    Messages.Message(
                        reason.NullOrEmpty()
                            ? "IsekaiDungeons.MultiFloor.Autopilot.Refused".Translate()
                            : reason,
                        pawn, MessageTypeDefOf.CautionInput);
                    return;
                }
            }

            stair.OrderDescend(pawn);
        }

        private static Building_MultiFloorStair FindMultiFloorStair(Map map)
        {
            List<Thing> stairs = map.listerThings.ThingsOfDef(
                IsekaiDungeonsDefOf.IsekaiDungeons_MultiFloorStair);
            return stairs.Count > 0 ? stairs[0] as Building_MultiFloorStair : null;
        }

        private static void FinishTowerFloor(Pawn pawn, Map map)
        {
            Building_TowerStair stair = FindStair(map);
            if (stair == null || stair.HasPendingClimber)
                return;

            if (stair.SummitStair)
            {
                // The grind has nowhere left to go. Switch off THIS MAP's
                // pawns, once — scoped to the map because a second party may
                // be grinding a gate dungeon somewhere else right now, and the
                // summit is not their news. The summit reward machinery
                // (TowerComponent.CheckSummitReward) does its own announcing.
                SwitchOffMap(map);
                Messages.Message("IsekaiDungeons.Tower.Autopilot.SummitOff".Translate(),
                    pawn, MessageTypeDefOf.NeutralEvent);
                return;
            }

            if (!stair.ClimbAvailable)
                return;   // boss objective not met yet (event fight mid-run, etc.) — wait

            // The stair moves EVERYBODY, so the climb waits for the party:
            // ripping a sleeping pawn upstairs mid-recovery would re-pause them
            // on arrival, forever — they would never accumulate a full night
            // across floors. A pause always ends (per-need resume + the idle
            // failsafe), so this wait is finite by construction.
            if (AnyPausedOnMap(map))
                return;

            // ONE pawn walks; the stair moves everybody. Lowest ID is an
            // arbitrary but stable choice, so the same pawn leads every floor
            // instead of the party trading the job around every interval.
            if (pawn != DesignatedLeader(map))
                return;

            EnsureDrafted(pawn);
            stair.OrderClimb(pawn);
            if (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Goto)
                issued[pawn] = pawn.CurJob;
        }

        /// <summary>
        /// A gate dungeon has no next floor: when nothing registered is left
        /// alive, the job is DONE, and done means giving the party back.
        /// Toggles off and undrafts every autopilot pawn on this map — safe,
        /// because reaching this line proves there is nothing left to fight —
        /// so the colonists stand ready to be sent hauling the hoard out.
        /// </summary>
        private static void FinishGateDungeon(Pawn pawn, Map map)
        {
            // Only the leader announces; every pawn it switches off is skipped
            // by the IsOn re-check in the Tick loop.
            if (pawn != DesignatedLeader(map))
                return;

            SwitchOffMap(map);
            Messages.Message("IsekaiDungeons.Gate.Autopilot.Done".Translate(),
                pawn, MessageTypeDefOf.PositiveEvent);
        }

        private static void SwitchOffMap(Map map)
        {
            TowerComponent tower = TowerComponent.Get;
            if (tower == null)
                return;
            for (int i = tower.AutopilotPawns.Count - 1; i >= 0; i--)
            {
                Pawn p = tower.AutopilotPawns[i];
                if (p == null || p.MapHeld != map)
                    continue;
                tower.SetAutopilot(p, false);
                issued.Remove(p);
                paused.Remove(p);
                pauseSnoozeUntil.Remove(p);
                deferSince.Remove(p);
                lastDecision.Remove(p);
                if (p.Spawned && p.Drafted)
                    p.drafter.Drafted = false;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Combat
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts the pawn on a target. When the target is something drafted
        /// auto-targeting will engage on its own (an awake hostile), a ranged
        /// pawn is only ever positioned — vanilla does the shooting. When it is
        /// not (a neutral, or anything DOWNED — auto-fire skips the
        /// unconscious), shooters get an explicit AttackStatic once they can
        /// actually hit, and melee orders carry killIncappedTarget so the
        /// swing that downs a boss is followed by the one that finishes it.
        /// </summary>
        private static void Engage(Pawn pawn, Pawn target, Map map, bool autoFire)
        {
            Verb verb = pawn.TryGetAttackVerb(target);
            if (verb == null)
                return;

            if (verb.verbProps.IsMeleeAttack)
            {
                // Renew only when the order actually changes: TryTakeOrderedJob
                // on an identical job every half-second would reset pathing.
                if (issued.TryGetValue(pawn, out Job mine) && pawn.CurJob == mine
                    && mine.def == JobDefOf.AttackMelee && mine.targetA.Thing == target)
                    return;

                Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                job.playerForced = true;
                // ON, everywhere. It used to be off under a "downed counts as
                // defeated" doctrine, which read as mercy and played as a
                // deadlock: the clear objective needs the boss DEAD, and a boss
                // that went down instead of dying sealed the stair while every
                // pawn politely declined to finish it.
                job.killIncappedTarget = true;
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder))
                    issued[pawn] = job;
                return;
            }

            float range = verb.verbProps.range;
            bool inPosition = pawn.Position.DistanceTo(target.Position) <= range * RangedHoldFraction
                && GenSight.LineOfSight(pawn.Position, target.Position, map, skipFirstCell: true);

            if (inPosition)
            {
                if (autoFire)
                {
                    // Still walking on OUR old order? Stop — standing here
                    // works, and vanilla targeting takes it from there.
                    if (issued.TryGetValue(pawn, out Job mine) && pawn.CurJob == mine
                        && mine.def == JobDefOf.Goto)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    return;
                }

                // Neutral target: the shot must be ORDERED.
                if (issued.TryGetValue(pawn, out Job mineStatic) && pawn.CurJob == mineStatic
                    && mineStatic.def == JobDefOf.AttackStatic && mineStatic.targetA.Thing == target)
                    return;

                Job shoot = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                shoot.playerForced = true;
                // If the target shuffles behind cover the job ends instead of
                // idling forever; the next interval repositions us.
                shoot.endIfCantShootTargetFromCurPos = true;
                if (pawn.jobs.TryTakeOrderedJob(shoot, JobTag.DraftedOrder))
                    issued[pawn] = shoot;
                return;
            }

            CastPositionRequest req = default;
            req.caster = pawn;
            req.target = target;
            req.verb = verb;
            req.maxRangeFromTarget = Mathf.Max(1.42f, range * RangedHoldFraction);
            req.wantCoverFromTarget = false;

            if (CastPositionFinder.TryFindCastPosition(req, out IntVec3 dest))
            {
                if (dest != pawn.Position)
                    IssueGoto(pawn, dest);
            }
            else
            {
                // No firing solution (fog, doorway) — close distance toward the
                // target instead; proximity will open one.
                IssueGoto(pawn, target.Position);
            }
        }

        /// <summary>
        /// Nearest live, awake, STANDING enemy this pawn can actually reach —
        /// the targets drafted auto-fire would engage on its own. Downed
        /// enemies are excluded HERE only because auto-fire ignores them; they
        /// are the put-down tier's quarry, not spared. Fogged targets are
        /// invisible to the player and so invisible to the autopilot — walking
        /// into their room is what reveals them, and that is the explore
        /// state's job.
        /// </summary>
        private static Pawn NearestHostile(Map map, Pawn pawn)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;

            foreach (IAttackTarget t in map.attackTargetsCache.TargetsHostileToColony)
            {
                Pawn enemy = t.Thing as Pawn;
                if (enemy == null || enemy.Dead || enemy.Downed || !enemy.Spawned)
                    continue;
                if (enemy.Position.Fogged(map))
                    continue;

                float dist = pawn.Position.DistanceToSquared(enemy.Position);
                if (dist >= bestDist)
                    continue;
                if (!pawn.CanReach(enemy, PathEndMode.Touch, Danger.Deadly))
                    continue;

                best = enemy;
                bestDist = dist;
            }
            return best;
        }

        /// <summary>
        /// Nearest visible creature that needs an EXPLICIT order: any
        /// room-registered creature that is neutral or downed (the dens the
        /// aggro system will never wake, and the bodies it left breathing),
        /// plus any DOWNED hostile from the attack-target cache — room-event
        /// ambushers and breach stragglers are never room-registered, and a
        /// gate dungeon is not finished while one of them still breathes.
        /// Registration-or-hostility is the filter that keeps beast-vault
        /// animals out of the crosshairs: a vault beast is neither.
        /// </summary>
        private static Pawn NearestPutDownTarget(DungeonMapComponent comp, Pawn pawn, Map map)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;

            Pawn bestUnreachable = null;
            float bestUnreachableDist = float.MaxValue;

            void Consider(Pawn cr)
            {
                if (cr == null || cr.Dead || !cr.Spawned)
                    return;
                // Standing hostiles are tier one's; everything else here.
                if (!cr.Downed && cr.HostileTo(Faction.OfPlayer))
                    return;
                if (cr.Position.Fogged(map))
                    return;

                float dist = pawn.Position.DistanceToSquared(cr.Position);
                if (dist < bestUnreachableDist)
                {
                    bestUnreachable = cr;
                    bestUnreachableDist = dist;
                }
                if (dist >= bestDist)
                    return;
                if (!pawn.CanReach(cr, PathEndMode.Touch, Danger.Deadly))
                    return;

                best = cr;
                bestDist = dist;
            }

            for (int r = 0; r < comp.rooms.Count; r++)
            {
                List<Pawn> creatures = comp.rooms[r].creatures;
                for (int c = 0; c < creatures.Count; c++)
                    Consider(creatures[c]);
            }

            foreach (IAttackTarget tgt in map.attackTargetsCache.TargetsHostileToColony)
            {
                if (tgt.Thing is Pawn enemy && enemy.Downed)
                    Consider(enemy);
            }

            // Same reachability-as-preference rule as the hidden finder.
            return best ?? bestUnreachable;
        }

        /// <summary>
        /// Nearest living registered creature the party cannot see yet.
        /// Visible ones are the fight/put-down tiers'; this tier exists to get
        /// hidden ones REVEALED. The target is the creature, wherever it
        /// actually stands — registration tells us it exists, its Position
        /// tells us where to walk (fog hides things from the player, not from
        /// the code that put them there, and the autopilot is the player's
        /// proxy walking in to look). Downed included: a creature that
        /// collapsed inside its still-fogged den must still be walked to and
        /// finished. PathEndMode.Touch, because the destination cell is
        /// occupied by the creature itself.
        /// </summary>
        private static Pawn NearestHiddenCreature(DungeonMapComponent comp, Pawn pawn, Map map)
        {
            Pawn best = null;
            Pawn bestUnreachable = null;
            float bestDist = float.MaxValue;
            float bestUnreachableDist = float.MaxValue;

            for (int r = 0; r < comp.rooms.Count; r++)
            {
                List<Pawn> creatures = comp.rooms[r].creatures;
                for (int c = 0; c < creatures.Count; c++)
                {
                    Pawn cr = creatures[c];
                    if (cr == null || cr.Dead || !cr.Spawned)
                        continue;
                    if (!cr.Position.Fogged(map))
                        continue;

                    float dist = pawn.Position.DistanceToSquared(cr.Position);
                    if (dist < bestUnreachableDist)
                    {
                        bestUnreachable = cr;
                        bestUnreachableDist = dist;
                    }
                    if (dist >= bestDist)
                        continue;
                    if (!pawn.CanReach(cr, PathEndMode.Touch, Danger.Deadly))
                        continue;

                    best = cr;
                    bestDist = dist;
                }
            }

            // REACHABILITY IS A PREFERENCE, NOT A GATE. When every remaining
            // creature fails CanReach (a door state the reachability cache
            // disagrees with, a mod's impassable terrain, anything), refusing
            // to act left the pawn to its schedule - the "just wanders,
            // ignores the far rooms" report. Walking AT the nearest one anyway
            // either gets there (the pather is the real authority) or fails
            // visibly next to the obstruction, where the player can see what
            // the problem actually is. One dev-mode line says it happened.
            if (best == null && bestUnreachable != null)
            {
                if (Prefs.DevMode)
                    Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Autopilot: no REACHABLE hidden creature (nearest is {bestUnreachable.LabelShort}); walking at it anyway.",
                        0x60D60 ^ map.uniqueID);
                return bestUnreachable;
            }
            return best;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Plumbing
        // ────────────────────────────────────────────────────────────────────

        private static bool FoodLow(Pawn pawn) =>
            pawn.needs?.food != null && pawn.needs.food.CurLevelPercentage < FoodPauseBelow;

        private static bool RestLow(Pawn pawn) =>
            pawn.needs?.rest != null && pawn.needs.rest.CurLevelPercentage < RestPauseBelow;

        /// <summary>
        /// True when the pause is over. Per-need: only the needs recorded at
        /// entry (plus any that fell under their floor DURING the pause —
        /// sleeping makes a pawn hungry, and they should eat before resuming)
        /// must reach their resume point. Rest additionally waits for the pawn
        /// to be awake, so an 85%-rested sleeper is left to wake on their own
        /// rather than being drafted out of bed. The idle failsafe resumes a
        /// pawn who is neither eating nor sleeping for an hour — whatever they
        /// were waiting for is not coming.
        /// </summary>
        private static bool TryResume(Pawn pawn, PauseState ps)
        {
            if (FoodLow(pawn))
                ps.food = true;
            if (RestLow(pawn))
                ps.rest = true;

            if (ps.food && (pawn.needs?.food == null
                    || pawn.needs.food.CurLevelPercentage >= FoodResumeAt))
                ps.food = false;
            if (ps.rest && (pawn.needs?.rest == null
                    || (pawn.needs.rest.CurLevelPercentage >= RestResumeAt && pawn.Awake())))
                ps.rest = false;

            if (!ps.food && !ps.rest)
                return true;

            bool recovering = !pawn.Awake() || pawn.CurJobDef == JobDefOf.Ingest;
            if (recovering)
            {
                ps.idleSince = Find.TickManager.TicksGame;
                return false;
            }
            if (Find.TickManager.TicksGame - ps.idleSince <= PauseIdleTimeoutTicks)
                return false;

            // Timing out means the need CANNOT recover here (no food left, no
            // safe sleep) — so the resume must also bar the door behind
            // itself, or the still-low need re-enters the pause on the very
            // next interval and the pawn never grinds again.
            pauseSnoozeUntil[pawn] = Find.TickManager.TicksGame + PauseSnoozeTicks;
            return true;
        }

        /// <summary>
        /// Our own orders are playerForced too, so the tell is reference
        /// identity: a forced job we did not issue is the player's hand on the
        /// wheel.
        /// </summary>
        private static bool PlayerHasTheWheel(Pawn pawn)
        {
            Job cur = pawn.CurJob;
            return cur != null && cur.playerForced
                && (!issued.TryGetValue(pawn, out Job mine) || cur != mine);
        }

        private static bool AnyPausedOnMap(Map map)
        {
            foreach (KeyValuePair<Pawn, PauseState> kv in paused)
            {
                Pawn p = kv.Key;
                if (p != null && !p.Dead && p.MapHeld == map && IsOn(p))
                    return true;
            }
            return false;
        }

        private static void EnsureDrafted(Pawn pawn)
        {
            if (!pawn.Drafted)
                pawn.drafter.Drafted = true;
        }

        private static void IssueGoto(Pawn pawn, IntVec3 dest)
        {
            // Same-destination renewal guard, same reason as the melee one.
            if (issued.TryGetValue(pawn, out Job mine) && pawn.CurJob == mine
                && mine.def == JobDefOf.Goto && mine.targetA.Cell == dest)
                return;

            Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
            job.playerForced = true;
            if (pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder))
                issued[pawn] = job;
        }

        private static Building_TowerStair FindStair(Map map)
        {
            List<Thing> stairs = map.listerThings.ThingsOfDef(IsekaiDungeonsDefOf.IsekaiDungeons_TowerStair);
            return stairs.Count > 0 ? stairs[0] as Building_TowerStair : null;
        }

        /// <summary>
        /// The one toggled pawn on this map who leads climbs and announces
        /// finishes — lowest ID, stable across intervals. A pawn who is paused
        /// for needs or under a manual player order CANNOT lead: the finish
        /// states early-return for everyone who is not the leader, so a leader
        /// who never reaches them (their DrivePawn bails at the pause or the
        /// override check) would silently block the whole party. That was the
        /// second half of the "fights but never climbs" stall: the hungry pawn
        /// happened to be the lowest ID.
        /// </summary>
        private static Pawn DesignatedLeader(Map map)
        {
            List<Pawn> pawns = TowerComponent.Get?.AutopilotPawns;
            if (pawns == null)
                return null;

            Pawn best = null;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || !p.Spawned || p.Map != map || p.Downed || p.InMentalState)
                    continue;
                if (paused.ContainsKey(p) || PlayerHasTheWheel(p))
                    continue;
                if (best == null || p.thingIDNumber < best.thingIDNumber)
                    best = p;
            }
            return best;
        }
    }

    /// <summary>
    /// Icon holders. [StaticConstructorOnStartup] because Texture2D fields must
    /// resolve on the main thread — the exact bug IsekaiGizmoProvider documents.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DungeonAutopilotTex
    {
        public static readonly Texture2D TowerIcon =
            ContentFinder<Texture2D>.Get("UI/TowerAutopilot", false) ?? TexCommand.Attack;

        public static readonly Texture2D GateIcon =
            ContentFinder<Texture2D>.Get("UI/DungeonAutopilot", false) ?? TexCommand.Attack;
    }

    /// <summary>
    /// Shows the toggle — on colonists, inside any dungeon map (tower floors
    /// and gate dungeons alike), nowhere else. Same separate-patch pattern as
    /// the Godhood halo: neither this nor the base mod's gizmo patch can break
    /// the other, and removing the add-on removes the button and nothing else.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Pawn_GetGizmos_DungeonAutopilotPatch
    {
        [HarmonyPostfix]
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result)
                yield return g;

            if (__instance == null
                || !__instance.RaceProps.Humanlike
                || __instance.Faction != Faction.OfPlayer
                || !__instance.Spawned
                || !(__instance.MapHeld?.Parent is DungeonMapParent))
                yield break;

            Gizmo gizmo = null;
            try
            {
                gizmo = DungeonAutopilot.GizmoFor(__instance,
                    __instance.MapHeld.Parent is TowerFloorMapParent
                    || __instance.MapHeld.Parent is MultiFloorMapParent);
            }
            catch (System.Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Dungeon autopilot gizmo failed: {ex}", 0x60D51);
            }
            if (gizmo != null)
                yield return gizmo;
        }
    }
}
