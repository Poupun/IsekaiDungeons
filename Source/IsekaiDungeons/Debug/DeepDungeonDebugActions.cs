using System;
using System.Collections.Generic;
using System.Text;
using IsekaiLeveling.MobRanking;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Dev tools for the multi-floor ("deep") dungeons, under their own
    /// "Isekai Deep Dungeons" category next to the tower's.
    ///
    /// The real path is slow to reach by playing: a survey only posts when the
    /// guild scheduler rolls one, the colony rank clears the plan's gate, and a
    /// caravan walks there. These skip each wait in turn — post a survey now,
    /// post a SPECIFIC one, drop straight onto any floor — while still going
    /// through the same entry points (MultiFloorReports, MultiFloorFloors.Open
    /// and Retire), so a dev-opened floor is indistinguishable from a walked one.
    /// </summary>
    public static class DeepDungeonDebugActions
    {
        private const string Category = "Isekai Deep Dungeons";

        // ── Posting ─────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the scheduler's survey path with only the timer skipped. Every
        /// gate still applies, so when it refuses it says which one did.
        /// </summary>
        [DebugAction(Category, "Post deep-dungeon survey now",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PostSurveyNow()
        {
            Map map = HomeMapOrWarn();
            if (map == null)
                return;

            if (MultiFloorReports.TryPost(map))
                return;

            Log.Warning("[IsekaiDungeons] Deep-dungeon survey refused: " + WhyNoSurvey(map)
                      + " Use \"Post specific deep dungeon...\" to bypass the gates.");
        }

        /// <summary>
        /// Pick the exact dungeon to report. Bypasses the setting, the planet
        /// cap, the colony-rank gate and the one-of-each rule, because testing
        /// the Nation-rank descent should not need a Nation-rank colony.
        /// </summary>
        [DebugAction(Category, "Post specific deep dungeon...",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PostSpecificSurvey()
        {
            Map map = HomeMapOrWarn();
            if (map == null)
                return;

            var plans = new List<MultiFloorDungeonDef>(DefDatabase<MultiFloorDungeonDef>.AllDefsListForReading);
            if (plans.Count == 0)
            {
                Log.Error("[IsekaiDungeons] No MultiFloorDungeonDefs loaded.");
                return;
            }
            plans.SortBy(d => (int)d.minRank, d => d.label ?? d.defName);

            var options = new List<DebugMenuOption>();
            foreach (MultiFloorDungeonDef d in plans)
            {
                MultiFloorDungeonDef plan = d;   // fresh local for the closure
                string entry = $"{MobRankUtility.GetRankString(plan.minRank)}-{MobRankUtility.GetRankString(plan.maxRank)}"
                             + $" | {plan.LabelCap} ({plan.floorCount} floors, colony {MobRankUtility.GetRankString(plan.minColonyRank)}+)";
                options.Add(new DebugMenuOption(entry, DebugMenuOptionMode.Action, delegate
                {
                    WorldObject_MultiFloorDungeon dungeon = MultiFloorReports.Place(map, plan);
                    if (dungeon == null)
                        Messages.Message($"[IsekaiDungeons] No legal tile for {plan.defName} near this colony.",
                                         MessageTypeDefOf.RejectInput, historical: false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        // ── Travel ──────────────────────────────────────────────────────────

        /// <summary>
        /// GOD MODE TRAVEL: pick a surveyed dungeon, then any floor, and go
        /// there, skipping the caravan and every floor above it. A floor
        /// already open in that dungeon is retired INTO the new one, so a party
        /// inside comes along rather than being deleted with the map.
        ///
        /// Records are left alone: floors skipped this way are not marked
        /// cleared, so the stair and NextOpenFloor still behave as if you had
        /// walked in normally.
        /// </summary>
        [DebugAction(Category, "Open floor... (skip travel)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OpenFloorPicker()
        {
            PickDungeon(dungeon =>
            {
                var options = new List<DebugMenuOption>();
                for (int f = 1; f <= dungeon.Def.floorCount; f++)
                {
                    int floor = f;
                    MultiFloorRecord rec = dungeon.RecordIfSeen(floor);
                    string tag = rec == null ? "" : rec.cleared ? " [cleared]" : rec.discovered ? " [seen]" : "";
                    string label = $"Floor {floor}: {MobRankUtility.GetRankString(dungeon.Def.RankForFloor(floor))}"
                                 + $" {dungeon.Def.KindForFloor(floor)?.label ?? "?"}{tag}";
                    options.Add(new DebugMenuOption(label, DebugMenuOptionMode.Action,
                                                    delegate { JumpToFloor(dungeon, floor); }));
                }
                Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
            });
        }

        private static void JumpToFloor(WorldObject_MultiFloorDungeon dungeon, int floor)
        {
            Map live = MultiFloorFloors.LiveFloorOf(dungeon);
            if (live != null)
                MultiFloorFloors.NotifyFloorLeft(dungeon, MultiFloorFloors.FloorOf(live), live);

            Map map = MultiFloorFloors.Open(dungeon, floor);
            if (map == null)
            {
                Messages.Message("Floor generation failed — see the log.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (live != null)
                MultiFloorFloors.Retire(live, map);

            MultiFloorFloors.NotifyFloorEntered(dungeon, floor, map);
            Current.Game.CurrentMap = map;
            CameraJumper.TryHideWorld();
            Find.CameraDriver.JumpToCurrentMapLoc(TowerFloors.ArrivalCell(map));
        }

        /// <summary>Moves every free colonist on the current map onto the open deep floor.</summary>
        [DebugAction(Category, "Send colonists to open floor",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SendColonists()
        {
            Map floorMap = AnyLiveFloor();
            if (floorMap == null)
            {
                Messages.Message("No deep-dungeon floor is open.", MessageTypeDefOf.RejectInput, historical: false);
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

        // ── Winning and leaving ─────────────────────────────────────────────

        /// <summary>
        /// Kills every hostile on the floor being viewed. The stair unseals on
        /// the map component's next poll, exactly as after a real fight.
        /// </summary>
        [DebugAction(Category, "Clear the viewed floor",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearViewedFloor()
        {
            Map floorMap = ViewedFloorOrWarn();
            if (floorMap == null)
                return;

            var pawns = new List<Pawn>(floorMap.mapPawns.AllPawnsSpawned);
            int killed = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.Dead || p.Faction == Faction.OfPlayer)
                    continue;
                if (p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer))
                    continue;
                p.Kill(null);
                killed++;
            }
            Messages.Message($"Killed {killed} on the floor — the stair unseals on the next poll "
                           + $"(≤{DungeonTunables.MapStateInterval} ticks).",
                             MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Takes the stair on the viewed floor right now, skipping the walk.
        /// Runs the stair's own Descend, so the party move, the letter and the
        /// spine hooks are the real ones. Refuses while the floor is unbeaten,
        /// as the stair would.
        /// </summary>
        [DebugAction(Category, "Take the stair now",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TakeStairNow()
        {
            Map floorMap = ViewedFloorOrWarn();
            if (floorMap == null)
                return;

            List<Building> buildings = floorMap.listerBuildings.allBuildingsNonColonist;
            Building_MultiFloorStair stair = null;
            for (int i = 0; i < buildings.Count && stair == null; i++)
                stair = buildings[i] as Building_MultiFloorStair;
            if (stair == null)
            {
                Messages.Message("No stair on this floor (it is the bottom, or placement failed — see the log).",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (!stair.DescendAvailable)
            {
                Messages.Message("The stair is sealed: clear the floor first.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            stair.Descend();
        }

        /// <summary>Leaves the viewed floor through the real exit path (records harvested, caravan formed).</summary>
        [DebugAction(Category, "Leave the dungeon (form caravan)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LeaveDungeon()
        {
            Map floorMap = ViewedFloorOrWarn();
            if (floorMap == null)
                return;
            MultiFloorFloors.Retire(floorMap, null);
        }

        // ── Inspection ──────────────────────────────────────────────────────

        /// <summary>
        /// Every surveyed dungeon and its floor records, plus why the scheduler
        /// would or would not post another one right now.
        /// </summary>
        [DebugAction(Category, "Deep dungeon status",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void Status()
        {
            var sb = new StringBuilder();
            sb.AppendLine("── Deep dungeons ──");
            IsekaiDungeonsSettings s = IsekaiDungeonsSettings.Current;
            sb.AppendLine($"enabled           : {s.deepDungeonsEnabled}");
            sb.AppendLine($"on planet / cap   : {MultiFloorReports.CountOnPlanet()} / {MultiFloorReports.MaxOnPlanet}");
            sb.AppendLine($"survey chance     : {DungeonQuestTuning.ChancePostedDeepReport:P0} of guild cycles"
                        + (s.guildContractsEnabled ? "" : " (contracts off: every cycle)"));

            Map home = Find.AnyPlayerHomeMap;
            if (home != null)
            {
                sb.AppendLine($"colony rank       : {MobRankUtility.GetRankString(RaidRankSystem.GetColonyBestRank(home))}");
                sb.AppendLine($"would post now    : {(MultiFloorReports.AnythingToReport(home) ? "yes" : "no - " + WhyNoSurvey(home))}");
            }

            foreach (WorldObject_MultiFloorDungeon d in AllDungeons())
            {
                sb.AppendLine();
                sb.AppendLine($"{d.Label} [{d.Def?.defName ?? "NO PLAN"}] id {d.ID} tile {d.Tile}");
                if (d.Def == null)
                    continue;
                Map live = MultiFloorFloors.LiveFloorOf(d);
                sb.AppendLine($"  final boss {d.Def.FinalBoss?.defName ?? "none (kind list)"}, bounty {MultiFloorRun.BountyFor(d.Def)}, "
                            + $"quest {(d.quest == null ? "none" : d.quest.State.ToString())}, spine {d.Def.spineWorkerClass?.Name ?? "none"} "
                            + $"(counter {d.spineCounter})");
                sb.AppendLine($"  deepest {d.deepestReached}/{d.Def.floorCount}, next open {d.NextOpenFloor}, "
                            + $"completed {d.completed}, live floor {(live == null ? "none" : MultiFloorFloors.FloorOf(live).ToString())}");
                for (int f = 1; f <= d.Def.floorCount; f++)
                {
                    MultiFloorRecord r = d.RecordIfSeen(f);
                    string state = r == null ? "unseen" : r.cleared ? "cleared" : r.discovered ? "seen" : "recorded";
                    sb.AppendLine($"  {f,3}  {MobRankUtility.GetRankString(d.Def.RankForFloor(f)),-6} "
                                + $"{d.Def.KindForFloor(f)?.label ?? "?",-26} {d.Def.MapSizeForFloor(f)}  {state}");
                }
            }
            Log.Message(sb.ToString());
        }

        /// <summary>Selects a surveyed dungeon on the planet view.</summary>
        [DebugAction(Category, "Show deep dungeon on world map...",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void ShowOnWorld() => PickDungeon(d => CameraJumper.TryJumpAndSelect(d));

        /// <summary>Removes one surveyed dungeon. Its floors close with it (PostRemove).</summary>
        [DebugAction(Category, "Remove deep dungeon...",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void RemoveOne() => PickDungeon(d =>
        {
            Find.WorldObjects.Remove(d);
            Messages.Message($"Removed {d.Label}.", MessageTypeDefOf.NeutralEvent, historical: false);
        });

        // ── Helpers ─────────────────────────────────────────────────────────

        private static Map HomeMapOrWarn()
        {
            Map map = Find.CurrentMap;
            if (map == null || !map.IsPlayerHome)
            {
                Messages.Message("Stand on a player home map: the survey is placed relative to it.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            return map;
        }

        private static Map ViewedFloorOrWarn()
        {
            Map map = Find.CurrentMap;
            if (MultiFloorFloors.OwnerOf(map) == null)
            {
                Messages.Message("The viewed map is not a deep-dungeon floor.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            return map;
        }

        private static Map AnyLiveFloor()
        {
            if (MultiFloorFloors.OwnerOf(Find.CurrentMap) != null)
                return Find.CurrentMap;
            foreach (WorldObject_MultiFloorDungeon d in AllDungeons())
            {
                Map live = MultiFloorFloors.LiveFloorOf(d);
                if (live != null)
                    return live;
            }
            return null;
        }

        private static List<WorldObject_MultiFloorDungeon> AllDungeons()
        {
            var list = new List<WorldObject_MultiFloorDungeon>();
            List<WorldObject> all = Find.WorldObjects?.AllWorldObjects;
            if (all == null)
                return list;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is WorldObject_MultiFloorDungeon d)
                    list.Add(d);
            }
            return list;
        }

        /// <summary>Picker over the surveyed dungeons; skips the dialog when there is exactly one.</summary>
        private static void PickDungeon(Action<WorldObject_MultiFloorDungeon> then)
        {
            List<WorldObject_MultiFloorDungeon> dungeons = AllDungeons().FindAll(d => d.Def != null);
            if (dungeons.Count == 0)
            {
                Messages.Message("No deep dungeon on the planet. Post one first.",
                                 MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (dungeons.Count == 1)
            {
                then(dungeons[0]);
                return;
            }
            var options = new List<DebugMenuOption>();
            foreach (WorldObject_MultiFloorDungeon d in dungeons)
            {
                WorldObject_MultiFloorDungeon dungeon = d;
                options.Add(new DebugMenuOption($"{dungeon.Label} (id {dungeon.ID}, deepest {dungeon.deepestReached}/{dungeon.Def.floorCount})",
                                                DebugMenuOptionMode.Action, delegate { then(dungeon); }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        /// <summary>Names the TryPost gate that refused, in the order TryPost checks them.</summary>
        private static string WhyNoSurvey(Map colony)
        {
            if (!IsekaiDungeonsSettings.Current.deepDungeonsEnabled)
                return "deep dungeons are switched off in the settings.";
            if (MultiFloorReports.CountOnPlanet() >= MultiFloorReports.MaxOnPlanet)
                return $"the planet already holds its cap of {MultiFloorReports.MaxOnPlanet}.";
            MobRankTier rank = RaidRankSystem.GetColonyBestRank(colony);
            if (MultiFloorReports.PickPlan(rank) == null)
                return $"no unsurveyed plan accepts a {MobRankUtility.GetRankString(rank)}-rank colony (minColonyRank).";
            return "no legal tile near the colony at the plan's opening rank.";
        }
    }
}
