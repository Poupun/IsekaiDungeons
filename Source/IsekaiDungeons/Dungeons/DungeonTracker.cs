using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Safety net + the per-map gate registry.
    ///
    /// The registry exists for FRAMEWORK correctness. This used to look gates up
    /// with <c>listerThings.ThingsOfDef(IsekaiDungeons_DungeonGate)</c>, which
    /// silently only ever saw OUR gate def — so a third-party dungeon shipping
    /// its own gate ThingDef (exactly what <see cref="DungeonKindDef.gateDef"/>
    /// invites) got two broken behaviours: it never counted toward the
    /// concurrency cap, so gates could pile up without limit, and it never
    /// received SafetySweep, so a stuck state was never repaired. Tracking
    /// gates by TYPE instead of by def fixes both for every dungeon, ours or
    /// anyone else's.
    ///
    /// The registry is rebuilt from the world itself: every gate registers in
    /// SpawnSetup (which also runs on load, with respawningAfterLoad true) and
    /// deregisters in DeSpawn. There is therefore nothing to scribe and nothing
    /// to migrate — the failure mode the other add-ons' ClearStaticState pattern
    /// guards against cannot occur here.
    /// </summary>
    public class DungeonTracker : GameComponent
    {
        private const int SweepInterval = 2500;

        /// <summary>Live gates per map, maintained by Building_DungeonGate's spawn/despawn.</summary>
        private static readonly Dictionary<Map, List<Building_DungeonGate>> gatesByMap =
            new Dictionary<Map, List<Building_DungeonGate>>();

        /// <summary>Scratch list so the sweep never mutates the registry while walking it.</summary>
        private static readonly List<Building_DungeonGate> tmpSweep = new List<Building_DungeonGate>();

        public DungeonTracker(Game game)
        {
            // THE REGISTRY IS STATIC AND THE COMPONENT IS PER-GAME.
            //
            // Loading a save builds a new DungeonTracker but leaves the static
            // dictionary exactly as the previous session left it: full of gates
            // belonging to Map objects that were torn down when that game
            // ended. The sweep then calls SafetySweep on them, which despawns
            // into a dead ListerThings and NREs — with no clue in the trace
            // that the offending gate is from a game that no longer exists.
            //
            // Clearing on construction is the fix, and it is safe because every
            // live gate re-registers itself in SpawnSetup, which runs for the
            // whole map on load.
            gatesByMap.Clear();
        }

        // ── Scheduling ──────────────────────────────────────────────────────

        /// <summary>
        /// Game tick the next scheduled gate is due. 0 means "not scheduled yet".
        /// </summary>
        private int nextGateTick;

        /// <summary>
        /// The mean interval nextGateTick was drawn with. When the gates-per-
        /// season setting changes, the pending gate is rescheduled at once;
        /// before this, a new rate only applied after the gate already queued
        /// at the OLD rate had fired, which read as "the slider does nothing".
        /// </summary>
        private float scheduledMean = -1f;

        /// <summary>
        /// The first scheduled gate has opened. Until it has, the gate is due
        /// ON firstGateDay itself. It used to be scheduled a whole interval
        /// AFTER firstGateDay (about a season at the default rate), so the
        /// first-gate setting appeared to do nothing.
        /// </summary>
        private bool firstGateDone;

        /// <summary>
        /// Earliest day a scheduled gate may open.
        ///
        /// THREE, not twenty. Vanilla's twenty-day floor was never ours: it is
        /// Cassandra's forceRaidEnemyBeforeDaysPassed, which turns EVERY big
        /// threat into a raid for the first twenty days. Driving gates from our
        /// own clock instead of the storyteller's threat lottery sidesteps that
        /// entirely — and it is the only way a rate slider could mean anything,
        /// because a weight in a shared lottery cannot be expressed in gates
        /// per season.
        ///
        /// Three days of grace so a colony has walls before the first one.
        /// A setting (Local gates tab).
        /// </summary>
        private static float FirstGateDay => IsekaiDungeonsSettings.Current.firstGateDay;

        /// <summary>Spread around the mean interval, so gates are not metronomic. A setting.</summary>
        private static float IntervalJitter => IsekaiDungeonsSettings.Current.gateIntervalJitter;

        /// <summary>When a spawn is refused (cap reached, no colonists), retry after this.</summary>
        private const float RetryDays = 0.5f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextGateTick, "nextGateTick", 0);
            Scribe_Values.Look(ref scheduledMean, "scheduledMean", -1f);
            Scribe_Values.Look(ref firstGateDone, "firstGateDone", defaultValue: false);

            // A save from before these fields: a scheduled gate means the
            // schedule was already running, so the first gate is behind it.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && scheduledMean < 0f && nextGateTick > 0)
                firstGateDone = true;
        }

        /// <summary>
        /// Opens gates on OUR clock rather than the storyteller's.
        ///
        /// It still fires the real IncidentDef through its own worker, so every
        /// existing check — colonist present, concurrency cap, rank roll, cell
        /// finder, letter — runs exactly as it always did. Only the decision of
        /// WHEN moved.
        /// </summary>
        private void TickGateSchedule()
        {
            IsekaiDungeonsSettings settings = IsekaiDungeonsSettings.Current;
            if (!settings.localGatesEnabled)
                return;                      // feature switched off in the settings
            float mean = settings.MeanDaysBetweenGates;
            if (mean <= 0f)
                return;                      // slider at zero: scheduled gates off

            if (GenDate.DaysPassedFloat < FirstGateDay)
                return;

            // After the first gate: draw the next one, and redraw it whenever
            // the rate setting moved since it was drawn.
            if (firstGateDone && (nextGateTick <= 0 || !Mathf.Approximately(scheduledMean, mean)))
            {
                ScheduleNext(mean);
                return;
            }

            // Before the first gate nextGateTick is 0 (due now: it is
            // firstGateDay) or a retry tick after a refused attempt.
            if (Find.TickManager.TicksGame < nextGateTick)
                return;

            Map map = TargetMap();
            if (map == null)
            {
                // Nowhere to put one yet. Come back shortly rather than losing
                // the slot — otherwise a colony in transit silently skips gates.
                Retry(mean);
                return;
            }

            IncidentDef def = IsekaiDungeonsDefOf.IsekaiDungeons_DungeonBreak;
            if (def == null)
                return;

            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            // The player's gate settings ARE the schedule. Without the bypass,
            // vanilla's storyteller gates still applied: the def's
            // earliestDay (3) overrode a lower firstGateDay setting, and a
            // difficulty with big threats off silently never fired a gate.
            // Our own checks (CanFireNowSub: cap, colonists, home map) still run.
            parms.bypassStorytellerSettings = true;
            bool fired = false;
            try
            {
                // CanFireNow FIRST. IncidentWorker.TryExecute does NOT consult
                // CanFireNowSub — the storyteller calls it separately — so
                // firing straight into TryExecute silently bypassed the
                // concurrency cap, the colonist check and the home-map check.
                // That is how gates started appearing past the four-gate limit.
                if (def.Worker.CanFireNow(parms))
                    fired = def.Worker.TryExecute(parms);
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Scheduled gate failed: {ex}");
            }

            if (fired)
            {
                firstGateDone = true;
                ScheduleNext(mean);
            }
            else
            {
                Retry(mean);
            }
        }

        private void Retry(float mean)
        {
            nextGateTick = Find.TickManager.TicksGame + Mathf.RoundToInt(RetryDays * GenDate.TicksPerDay);
            // Recorded so a retry is not mistaken for a rate change (or, on
            // load, for a pre-update save).
            scheduledMean = mean;
        }

        /// <summary>
        /// Drops registry entries for maps the game has removed.
        ///
        /// Deregister only fires when a GATE despawns. Nothing fires when a
        /// MAP is removed out from under its gates — an abandoned settlement,
        /// a destroyed pocket map — so those entries sit in the dictionary
        /// forever and the sweep keeps poking things that live on a torn-down
        /// lister.
        /// </summary>
        private static void PruneDeadMaps()
        {
            List<Map> live = Find.Maps;
            tmpDeadMaps.Clear();
            foreach (KeyValuePair<Map, List<Building_DungeonGate>> pair in gatesByMap)
            {
                if (pair.Key == null || !live.Contains(pair.Key))
                    tmpDeadMaps.Add(pair.Key);
            }
            for (int i = 0; i < tmpDeadMaps.Count; i++)
                gatesByMap.Remove(tmpDeadMaps[i]);
            tmpDeadMaps.Clear();
        }

        /// <summary>Scratch list; removing while enumerating a dictionary throws.</summary>
        private static readonly List<Map> tmpDeadMaps = new List<Map>();

        private void ScheduleNext(float meanDays)
        {
            float days = meanDays * Rand.Range(1f - IntervalJitter, 1f + IntervalJitter);
            nextGateTick = Find.TickManager.TicksGame + Mathf.Max(1, Mathf.RoundToInt(days * GenDate.TicksPerDay));
            scheduledMean = meanDays;
        }

        /// <summary>A home map with someone on it, preferring one with room for another gate.</summary>
        private static Map TargetMap()
        {
            List<Map> maps = Find.Maps;
            Map fallback = null;
            for (int i = 0; i < maps.Count; i++)
            {
                Map m = maps[i];
                if (!m.IsPlayerHome || m.mapPawns.FreeColonistsCount == 0)
                    continue;
                if (ActiveGateCount(m) < DungeonTunables.MaxActiveGatesPerMap)
                    return m;
                fallback = m;
            }
            return fallback;
        }

        // ── Registry ────────────────────────────────────────────────────────

        public static void Register(Building_DungeonGate gate)
        {
            if (gate?.Map == null)
                return;
            if (!gatesByMap.TryGetValue(gate.Map, out List<Building_DungeonGate> list))
            {
                list = new List<Building_DungeonGate>();
                gatesByMap[gate.Map] = list;
            }
            if (!list.Contains(gate))
                list.Add(gate);
        }

        public static void Deregister(Building_DungeonGate gate, Map map)
        {
            if (gate == null || map == null)
                return;
            if (gatesByMap.TryGetValue(map, out List<Building_DungeonGate> list))
            {
                list.Remove(gate);
                if (list.Count == 0)
                    gatesByMap.Remove(map);
            }
        }

        /// <summary>Every live gate on a map — ANY dungeon's gate def, not just ours.</summary>
        public static List<Building_DungeonGate> GatesOn(Map map)
        {
            if (map != null && gatesByMap.TryGetValue(map, out List<Building_DungeonGate> list))
                return list;
            return null;
        }

        // ── Safety net ──────────────────────────────────────────────────────

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 == 0)
                TickGateSchedule();

            if (Find.TickManager.TicksGame % SweepInterval != 0)
                return;

            // SafetySweep can destroy a gate (and therefore deregister it), so
            // snapshot before iterating.
            PruneDeadMaps();

            tmpSweep.Clear();
            foreach (KeyValuePair<Map, List<Building_DungeonGate>> pair in gatesByMap)
                tmpSweep.AddRange(pair.Value);

            for (int i = 0; i < tmpSweep.Count; i++)
                tmpSweep[i]?.SafetySweep();

            tmpSweep.Clear();
        }

        /// <summary>
        /// Gates the colony still owes something to — the incident's
        /// concurrency cap.
        ///
        /// A BREACHING gate counts. It is draining a wave a day onto the map
        /// and can still be stormed, so it is very much live content; letting
        /// the storyteller open a fresh gate on top of one would stack two
        /// multi-day sieges nobody designed together.
        /// </summary>
        public static int ActiveGateCount(Map map)
        {
            List<Building_DungeonGate> gates = GatesOn(map);
            if (gates == null)
                return 0;
            int count = 0;
            for (int i = 0; i < gates.Count; i++)
            {
                if (gates[i] == null)
                    continue;
                if (gates[i].state == DungeonState.Active || gates[i].state == DungeonState.Breaching)
                    count++;
            }
            return count;
        }
    }
}
