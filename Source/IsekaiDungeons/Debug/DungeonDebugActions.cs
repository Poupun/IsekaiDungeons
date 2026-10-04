using System;
using System.Collections.Generic;
using System.Reflection;
using IsekaiLeveling.MobRanking;
using LudeonTK;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Dev-mode tools for the dungeon loop, all under the "Isekai Dungeons"
    /// debug category. House pattern (see IsekaiCreatures.SlimeDebugActions):
    /// LudeonTK [DebugAction] attributes, rank pickers via
    /// Dialog_DebugOptionListLister with DebugMenuOptionMode.Tool so the click
    /// AFTER choosing a rank places the thing — a picker cannot be a plain
    /// ToolMap action because the rank must be chosen before tool mode starts.
    ///
    /// Everything here goes through the same public entry points the real
    /// features use (incident-style Initialize flow, boss-death clear
    /// detection), so a dev-spawned gate is indistinguishable from a
    /// storyteller one — if the dev tool works, the incident path works.
    /// </summary>
    public static class DungeonDebugActions
    {
        private const string Category = "Isekai Dungeons";

        /// <summary>
        /// What the soundtrack thinks it should be doing. "The music did not
        /// change" is otherwise unanswerable without adding logging after the
        /// fact.
        /// </summary>
        /// <summary>
        /// "No contracts are arriving" has five silent causes; this names which.
        /// </summary>
        [DebugAction(Category, "Contract scheduler status",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void ContractStatus()
        {
            DungeonQuestTracker t = Current.Game?.GetComponent<DungeonQuestTracker>();
            if (t == null)
            {
                Log.Error("No DungeonQuestTracker on this game - contracts cannot post at all.");
                return;
            }
            Log.Message(t.StatusReport());
        }

        /// <summary>
        /// Runs the real posting path, ignoring only the timer - the cap, the
        /// colony check and the tile search all still apply, so a failure here
        /// is the same failure the scheduler would have hit silently.
        /// </summary>
        [DebugAction(Category, "Post dungeon contract now",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PostContractNow()
        {
            Map map = Find.CurrentMap;
            if (map == null || !map.IsPlayerHome)
            {
                Log.Warning("Stand on a player home map to post a contract.");
                return;
            }
            Quest q = DungeonContractFactory.TryPost(map, DungeonQuestKind.Located);
            Log.Message(q == null
                ? "Contract post FAILED - no legal tile, or no dungeon kind at the rolled rank. "
                + "See any warning above this line."
                : $"Posted: {q.name}");
        }

        [DebugAction(Category, "OST status",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.Playing)]
        private static void OSTStatus()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("── Dungeon OST ──");

            Map cur = Find.CurrentMap;
            sb.AppendLine($"viewed map        : {(cur == null ? "none" : cur.Parent?.GetType().Name ?? "plain map")}");
            sb.AppendLine($"main OST enabled  : {IsekaiLeveling.IsekaiLevelingSettings.EnableIsekaiOST}"
                        + "   (this feature follows that switch)");
            sb.AppendLine($"OST volume        : {IsekaiLeveling.IsekaiLevelingSettings.IsekaiOSTVolume:P0}");

            SongDef want = DungeonOST.Wanted;
            sb.AppendLine($"wanted track      : {(want == null ? "none - ordinary rotation" : want.defName)}");
            if (want != null)
                sb.AppendLine($"clip loaded       : {(want.clip != null ? "yes" : "NO - the mp3 did not resolve")}");

            MusicManagerPlay m = Find.MusicManagerPlay;
            if (m == null)
            {
                sb.AppendLine("music manager     : none");
            }
            else
            {
                sb.AppendLine($"playing           : {(m.IsPlaying ? m.CurrentSong?.defName ?? "?" : "silent")}");
                sb.AppendLine($"is ours           : {DungeonOST.IsOurs(m.CurrentSong)}");
                sb.AppendLine($"danger mode       : {m.DangerMusicMode}");
                sb.AppendLine($"transitions       : {(DungeonOST.TransitionsAllowed ? "allowed (vanilla)" : "HELD (dungeon on screen)")}");
                sb.AppendLine($"triggered trans.  : {m.TriggeredTransition?.def?.defName ?? "none"}"
                            + "   (anything here means a DLC sequence has the music)");
                sb.AppendLine($"manager state     : {m.State}");
                sb.AppendLine($"next song in      : {m.NextSongTimer:0.0}s");
            }
            Log.Message(sb.ToString());
        }


        // ── (a) SPAWN GATE ──────────────────────────────────────────────

        /// <summary>
        /// Rank picker → map-click tool → gate at the clicked cell. Replicates
        /// the incident worker's flow exactly: MakeThing from the kind's
        /// gateDef, Initialize BEFORE spawn (so breakTick is set when Tick
        /// first runs), then GenSpawn.Spawn. No letter — dev spawns are
        /// silent by design; the incident owns player-facing messaging.
        /// </summary>
        [DebugAction(Category, "Spawn dungeon gate (rank)...",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnDungeonGateDialog()
        {
            List<DebugMenuOption> options = new List<DebugMenuOption>();
            for (int i = (int)MobRankTier.F; i <= (int)MobRankTier.Nation; i++)
            {
                MobRankTier tier = (MobRankTier)i; // fresh local per iteration for the closure
                options.Add(new DebugMenuOption(
                    MobRankUtility.GetRankString(tier) + "-rank gate (random dungeon)",
                    DebugMenuOptionMode.Tool,
                    () => SpawnGateAtCursor(DungeonKindSelector.SelectForRank(tier), tier, redGate: false)));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        /// <summary>
        /// Pick the EXACT dungeon to spawn, then its rank.
        ///
        /// This exists because the rank-only tool used to hard-code the
        /// rosterless fallback archetype, so every dev-spawned gate generated
        /// as the same cavern and none of the other environments could be
        /// reached from the dev palette at all. Testing a specific dungeon must
        /// not depend on winning a weighted roll.
        ///
        /// Unavailable dungeons (their bestiary mod is not installed) are still
        /// listed, marked, and still spawnable - populating them falls back to
        /// the generic rank providers, which is exactly what you want when
        /// checking a layout or an environment in isolation.
        /// </summary>
        [DebugAction(Category, "Spawn dungeon gate (pick dungeon)...",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnSpecificDungeonDialog()
        {
            List<DungeonKindDef> kinds = new List<DungeonKindDef>(DefDatabase<DungeonKindDef>.AllDefsListForReading);
            kinds.SortBy(k => (int)k.minRank, k => k.label ?? k.defName);

            List<DebugMenuOption> options = new List<DebugMenuOption>();
            foreach (DungeonKindDef k in kinds)
            {
                DungeonKindDef kind = k; // closure capture
                string env = kind.mapGenerator?.defName?.Replace("IsekaiDungeons_", "") ?? "?";
                string mark = kind.Available ? "" : " [roster missing]";
                string entry = $"{MobRankUtility.GetRankString(kind.minRank)}-{MobRankUtility.GetRankString(kind.maxRank)} | {kind.label ?? kind.defName} ({env}, {kind.layoutMode}){mark}";

                options.Add(new DebugMenuOption(entry, DebugMenuOptionMode.Action, delegate
                {
                    List<DebugMenuOption> rankOptions = new List<DebugMenuOption>();
                    for (int i = (int)kind.minRank; i <= (int)kind.maxRank; i++)
                    {
                        MobRankTier tier = (MobRankTier)i;
                        rankOptions.Add(new DebugMenuOption(
                            MobRankUtility.GetRankString(tier) + "-rank",
                            DebugMenuOptionMode.Tool,
                            () => SpawnGateAtCursor(kind, tier, redGate: false)));
                    }
                    Find.WindowStack.Add(new Dialog_DebugOptionListLister(rankOptions));
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        private static void SpawnGateAtCursor(DungeonKindDef kind, MobRankTier rank, bool redGate)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;

            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                Messages.Message("[IsekaiDungeons] Cell out of bounds.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            if (kind == null)
                kind = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            if (kind?.gateDef == null)
            {
                Messages.Message($"[IsekaiDungeons] {kind?.defName ?? "(null dungeon)"} has no gateDef — check the DungeonKindDef XML.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Building_DungeonGate gate = ThingMaker.MakeThing(kind.gateDef) as Building_DungeonGate;
            if (gate == null)
            {
                Messages.Message($"[IsekaiDungeons] {kind.gateDef.defName}.thingClass is not Building_DungeonGate.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            // Same order as the incident: configure first, then spawn, so the
            // gate's first Tick already sees a valid rank + countdown.
            gate.Initialize(kind, rank, redGate);
            // FullRefund: a dev tool should place exactly where it is pointed,
            // but testing on a real save should not cost the player a wall.
            GenSpawn.Spawn(gate, cell, map, Rot4.North, WipeMode.FullRefund);

            float breakDays = gate.TicksUntilBreak / 60000f;
            Messages.Message(
                $"[IsekaiDungeons] Spawned {MobRankUtility.GetRankString(rank)}-rank {kind.label ?? kind.defName} " +
                $"[{kind.mapGenerator?.defName}] (breaks in {breakDays:F1} days).",
                new LookTargets(gate), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ── (b) FORCE CLEAR ─────────────────────────────────────────────

        /// <summary>
        /// Kills the registered boss of the CURRENT map (must be a dungeon
        /// pocket map). Deliberately goes through Pawn.Kill rather than any
        /// direct clear call: the map component's poll-based clear detection
        /// (G1/G9 — state polling, not death events) is exactly what we want
        /// to exercise; rewards/letters fire on its next poll (≤60 ticks).
        /// </summary>
        [DebugAction(Category, "Clear dungeon (boss dies)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearDungeonBossDies()
        {
            Map map = Find.CurrentMap;
            DungeonMapComponent comp = map?.GetComponent<DungeonMapComponent>();
            if (comp == null)
            {
                Messages.Message("[IsekaiDungeons] Current map is not a dungeon (no DungeonMapComponent). Switch to the pocket map first.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Pawn boss = comp.boss;
            if (boss == null)
            {
                Messages.Message("[IsekaiDungeons] No boss registered on this dungeon map (population genstep may not have run).",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (boss.Dead)
            {
                Messages.Message("[IsekaiDungeons] Boss is already dead — clear detection should fire on its next poll.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            boss.Kill(null);
            Messages.Message($"[IsekaiDungeons] Killed boss {boss.LabelShortCap} — clear fires on the next map-component poll (≤{DungeonTunables.MapStateInterval} ticks).",
                MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ── (c)/(d) FORCE BREAK / COLLAPSE ──────────────────────────────

        /// <summary>Drives the first gate's break flow (countdown expiry path) immediately.</summary>
        [DebugAction(Category, "Force dungeon break",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceDungeonBreak() => DriveFirstGate("DoBreak", "break");

        /// <summary>Drives the first gate's collapse flow (post-clear teardown path) immediately.</summary>
        [DebugAction(Category, "Collapse gate now",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CollapseGateNow() => DriveFirstGate("Collapse", "collapse");

        /// <summary>
        /// Finds a gate for the current map and invokes the named lifecycle
        /// hook on it. The hook names ("DoBreak", "Collapse") are normative
        /// per ARCHITECTURE §5.2, but their ACCESSIBILITY is not part of the
        /// frozen cross-file contract — so we look the method up at runtime
        /// restricted to PUBLIC instance methods and fail with a helpful
        /// message if the gate implementation kept it non-public (in which
        /// case the gate's own dev gizmos, also mandated by §5.2, cover the
        /// same ground). This keeps us inside the "public members only" rule
        /// without a compile-time dependency on an unfrozen signature.
        /// </summary>
        private static void DriveFirstGate(string methodName, string verb)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;

            Building_DungeonGate gate = FindGateFor(map);
            if (gate == null)
            {
                Messages.Message($"[IsekaiDungeons] No dungeon gate found for the current map — cannot {verb}.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            MethodInfo hook = typeof(Building_DungeonGate).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (hook == null)
            {
                Messages.Message($"[IsekaiDungeons] Building_DungeonGate.{methodName}() is not public — use the gate's own dev gizmos to {verb}.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            try
            {
                hook.Invoke(gate, null);
                Messages.Message($"[IsekaiDungeons] Gate {verb} triggered ({methodName} on {gate.LabelCap}).",
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
            catch (Exception ex)
            {
                Log.Error($"[IsekaiDungeons] Dev {verb} failed invoking {methodName}: {ex}");
            }
        }

        /// <summary>
        /// The gate for the current map: if we are standing ON the pocket map,
        /// the map component's back-reference is authoritative; on a colony
        /// map, scan the building listers (gates spawn faction-null, which
        /// vanilla files under allBuildingsNonColonist — the colonist list is
        /// checked too as a claim-edge-case fallback).
        /// </summary>
        private static Building_DungeonGate FindGateFor(Map map)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp?.gate != null)
                return comp.gate;

            List<Building> buildings = map.listerBuildings.allBuildingsNonColonist;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i] is Building_DungeonGate gate)
                    return gate;

            buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i] is Building_DungeonGate gate)
                    return gate;

            return null;
        }
    }
}
