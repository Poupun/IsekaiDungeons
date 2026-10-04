using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Dev tools for the tower, under their own "Isekai Tower" category so they
    /// do not bury the dungeon ones.
    ///
    /// The tower is much harder to test by playing than a dungeon is: it is a
    /// hundred floors behind a multi-day caravan journey. So these cover the
    /// things that would otherwise cost an hour of walking — raise it, drop
    /// straight onto any floor, win the current one — plus a dump of the record
    /// state, which is where the interesting bugs actually live.
    ///
    /// Every tool routes through the SAME public entry points the real feature
    /// uses (TowerFloors.Open / Retire, the real incident worker), so a
    /// dev-opened floor is indistinguishable from a climbed one.
    /// </summary>
    public static class TowerDebugActions
    {
        private const string Category = "Isekai Tower";

        private static TowerComponent Comp => TowerComponent.Get;

        /// <summary>Opens a floor, bringing any party already inside along with it.</summary>
        private static void JumpToFloor(int floor)
        {
            Map live = TowerFloors.LiveFloor;
            Map map = TowerFloors.Open(floor);
            if (map == null)
            {
                Messages.Message("Floor generation failed — see the log.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            // Retire the old floor INTO the new one, so a party already inside
            // comes along instead of being deleted with the map.
            if (live != null)
                TowerFloors.Retire(live, map);
            Comp?.NotifyReached(floor);
            Current.Game.CurrentMap = map;
            CameraJumper.TryHideWorld();
            Find.CameraDriver.JumpToCurrentMapLoc(TowerFloors.ArrivalCell(map));
        }

        // ── Raising ─────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the real incident worker rather than placing the tower by hand,
        /// so this exercises the rank gate, the tile finder and the reachability
        /// check — the parts most likely to be wrong on an awkward planet.
        /// </summary>
        [DebugAction(Category, "Raise the tower (run incident)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RaiseTowerViaIncident()
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;
            var parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("IsekaiDungeons_TowerRises");
            if (def == null)
            {
                Log.Error("[IsekaiDungeons] IsekaiDungeons_TowerRises incident def is missing.");
                return;
            }
            if (!def.Worker.TryExecute(parms))
                Messages.Message("Tower incident refused to fire (already raised, or no tile).",
                                 MessageTypeDefOf.RejectInput, historical: false);
        }

        /// <summary>Selects the tower on the planet view, so its tile is easy to find.</summary>
        [DebugAction(Category, "Show tower on world map",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ShowTowerOnWorld()
        {
            WorldObject_Tower tower = TowerFloors.Tower;
            if (tower == null)
            {
                Messages.Message("No tower has been raised.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            CameraJumper.TryJumpAndSelect(tower);
        }

        // ── Travel ──────────────────────────────────────────────────────────

        /// <summary>
        /// GOD MODE TRAVEL: open any floor, discovered or not, and go there —
        /// skipping both the caravan journey and the climb.
        ///
        /// Deliberately bypasses the discovery gate. That gate exists to stop a
        /// PLAYER skipping a boss; testing floor 40 should not require beating
        /// thirty-nine floors first.
        /// </summary>
        [DebugAction(Category, "Open floor... (skip travel)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OpenFloorPicker()
        {
            if (Comp == null || !Comp.Exists)
            {
                Messages.Message("No tower. Raise one first.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            var options = new List<DebugMenuOption>();
            for (int f = 1; f <= TowerTunables.FloorCount; f++)
            {
                int floor = f;   // fresh local for the closure
                TowerFloorRecord preview = Comp.Preview(floor);
                string tag = preview.cleared ? " [cleared]" : preview.discovered ? " [seen]" : "";
                options.Add(new DebugMenuOption(preview.Label + tag, DebugMenuOptionMode.Action,
                                                delegate { JumpToFloor(floor); }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        /// <summary>Moves every colonist on the current map onto the live floor — the party you forgot to bring.</summary>
        [DebugAction(Category, "Send colonists to open floor",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SendColonists()
        {
            Map floorMap = TowerFloors.LiveFloor;
            if (floorMap == null)
            {
                Messages.Message("No floor is open.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Map from = Find.CurrentMap;
            if (from == null || from == floorMap)
                return;

            IntVec3 arrival = TowerFloors.ArrivalCell(floorMap);
            var colonists = new List<Pawn>(from.mapPawns.FreeColonistsSpawned);
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                p.DeSpawn();
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(arrival, floorMap, 6,
                    c => c.Standable(floorMap));
                GenSpawn.Spawn(p, cell.IsValid ? cell : arrival, floorMap);
            }
            Current.Game.CurrentMap = floorMap;
            Find.CameraDriver.JumpToCurrentMapLoc(arrival);
        }

        // ── The survey contract ─────────────────────────────────────────────

        /// <summary>
        /// Drives the contract to its end through the REAL path: latch the
        /// summit, and let the component's own tick notice and bring the tower
        /// down. Deliberately does not call
        /// TrySinkTheTower itself — the wait for the party to leave is the part
        /// most worth testing, and a tool that skipped it would test nothing.
        /// </summary>
        [DebugAction(Category, "Finish the tower (collapse it)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void FinishTower()
        {
            TowerComponent comp = Comp;
            if (comp == null || !comp.Exists)
            {
                Messages.Message("No tower stands.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            if (!comp.summitClaimed)
            {
                // Straight to the latch rather than through the reward path:
                // CheckSummitReward needs a claimant and a damage record, and
                // this tool is about the ENDING, not about who gets godhood.
                comp.summitClaimed = true;
            }

            Messages.Message(
                TowerFloors.LiveFloor != null
                    ? "Summit marked claimed. The tower collapses once everyone is out of it."
                    : "Summit marked claimed. The tower collapses within a quarter second.",
                MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Why the contract is or is not where you expect. "Nothing happened"
        /// has to be answerable without adding logging after the fact.
        /// </summary>
        [DebugAction(Category, "Survey contract status",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void SurveyStatus()
        {
            TowerComponent comp = Comp;
            var sb = new StringBuilder();
            sb.AppendLine("── Tower survey contract ──");
            if (comp == null)
            {
                sb.AppendLine("no TowerComponent");
                Log.Message(sb.ToString());
                return;
            }
            sb.AppendLine($"tower exists      : {comp.Exists} (seed {comp.towerSeed})");
            sb.AppendLine($"highest reached   : {comp.highestReached}");
            sb.AppendLine($"summit claimed    : {comp.summitClaimed}");
            sb.AppendLine($"tower sank        : {comp.towerSank}"
                        + (comp.towerSank ? "   (it will never rise again)" : ""));

            sb.AppendLine($"live floor map    : {(TowerFloors.LiveFloor == null ? "none" : "OPEN - collapse is held")}");
            sb.AppendLine($"safe to collapse  : {TowerQuest.SafeToSink()}");

            sb.AppendLine(comp.towerQuest == null
                ? "quest entry       : none (cosmetic; the tower still works)"
                : $"quest entry       : '{comp.towerQuest.name}' state {comp.towerQuest.State}");
            sb.AppendLine($"world object      : {(TowerFloors.Tower == null ? "gone" : "on the map")}");
            Log.Message(sb.ToString());
        }

        // ── Winning ─────────────────────────────────────────────────────────

        /// <summary>Kills everything hostile on the live floor, which is what clearing it means.</summary>
        [DebugAction(Category, "Clear the open floor",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearOpenFloor()
        {
            Map floorMap = TowerFloors.LiveFloor;
            if (floorMap == null)
            {
                Messages.Message("No floor is open.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            var pawns = new List<Pawn>(floorMap.mapPawns.AllPawnsSpawned);
            int killed = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.Dead || p.Faction == Faction.OfPlayer)
                    continue;
                // Leave the villagers alone — killing a market floor is a
                // permanent consequence, and nobody wants that from "clear".
                if (p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer))
                    continue;
                p.Kill(null);
                killed++;
            }
            Messages.Message($"Killed {killed} on the floor.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>Leaves the tower from wherever the party is, exercising the real exit path.</summary>
        [DebugAction(Category, "Leave the tower (form caravan)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LeaveTower()
        {
            Map floorMap = TowerFloors.LiveFloor;
            if (floorMap == null)
            {
                Messages.Message("No floor is open.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            TowerFloors.Retire(floorMap, null);
        }

        // ── Villages ────────────────────────────────────────────────────────

        /// <summary>Opens the nearest floor whose archetype builds a village.</summary>
        [DebugAction(Category, "Open nearest village floor",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GoToVillage()
        {
            if (Comp == null || !Comp.Exists)
            {
                Messages.Message("No tower. Raise one first.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            for (int f = 1; f <= TowerTunables.FloorCount; f++)
            {
                // Preview, not Ensure: scanning for the next village must not
                // freeze every floor it walks past on the way there.
                TowerFloorRecord rec = Comp.Preview(f);
                if (rec.archetype != null && rec.archetype.village)
                {
                    JumpToFloor(f);
                    return;
                }
            }
            Messages.Message("This ladder rolled no village floors.", MessageTypeDefOf.RejectInput, historical: false);
        }

        // ── Inspection ──────────────────────────────────────────────────────

        /// <summary>
        /// Dumps the whole ladder without visiting any of it. The fastest way to
        /// check the archetype schedule, the rank curve and which dungeon backs
        /// each floor — all of which are pure functions of the tower seed.
        /// </summary>
        [DebugAction(Category, "Log the whole ladder",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogLadder()
        {
            int seed = Comp != null && Comp.Exists ? Comp.towerSeed : Rand.Int;
            var sb = new StringBuilder();
            sb.AppendLine($"[IsekaiDungeons] TOWER LADDER  seed={seed}");
            for (int f = 1; f <= TowerTunables.FloorCount; f++)
            {
                TowerFloorRecord r = TowerLadder.Roll(seed, f);
                TowerFloorRecord live = Comp?.RecordIfSeen(f);
                string state = live == null ? ""
                    : (live.cleared ? " [cleared]" : live.discovered ? " [seen]" : "");
                string arch = r.archetype?.defName?.Replace("IsekaiDungeons_Floor_", "") ?? "?";
                sb.AppendLine($"  {f,3}  {r.rank,-6} {arch,-11} {r.kindDef?.label,-26} {r.mapSize}x{r.mapSize}{state}");
            }
            Log.Message(sb.ToString());
        }

        /// <summary>Lists which floors are villages, and what state each is in.</summary>
        [DebugAction(Category, "List village floors",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ListVillages()
        {
            int seed = Comp != null && Comp.Exists ? Comp.towerSeed : Rand.Int;
            var sb = new StringBuilder();
            sb.AppendLine($"[IsekaiDungeons] VILLAGE FLOORS  seed={seed}");
            int n = 0;
            for (int f = 1; f <= TowerTunables.FloorCount; f++)
            {
                TowerFloorRecord r = TowerLadder.Roll(seed, f);
                if (r.archetype == null || !r.archetype.village)
                    continue;
                n++;
                TowerFloorRecord live = Comp?.RecordIfSeen(f);
                sb.AppendLine($"  {r.Label,-34} rank {r.rank,-6} state {(live != null ? live.villageState.ToString() : "unvisited")}");
            }
            sb.AppendLine($"  {n} of {TowerTunables.FloorCount} floors are markets.");
            Log.Message(sb.ToString());
        }

        // ── Teardown ────────────────────────────────────────────────────────

        /// <summary>Wipes the tower entirely, so the incident can raise a fresh one.</summary>
        [DebugAction(Category, "Remove the tower",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveTower()
        {
            Map floorMap = TowerFloors.LiveFloor;
            if (floorMap != null)
                TowerFloors.Retire(floorMap, null);

            WorldObject_Tower tower = TowerFloors.Tower;
            if (tower != null)
                Find.WorldObjects.Remove(tower);

            Comp?.EndTower();
            Messages.Message("Tower removed.", MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
