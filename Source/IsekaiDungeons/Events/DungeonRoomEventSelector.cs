using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Decides which combat rooms become events.
    ///
    /// Runs once per dungeon at generation. The rule set is intentionally
    /// conservative: most rooms stay ordinary fights, because an event only
    /// reads as an event when it is the exception. A dungeon where every room
    /// is a surprise has no baseline for the surprise to stand against.
    ///
    /// Eligibility is all data (rank band, layout mode, depth window, per-dungeon
    /// cap) so a modder's event competes on equal terms with the shipped ones.
    /// </summary>
    public static class DungeonRoomEventSelector
    {
        /// <summary>
        /// Share of a dungeon's combat rooms that may become events, before the
        /// per-event caps apply. A third feels varied without diluting the
        /// ordinary fights that make the events land. A setting (Dungeons tab).
        /// </summary>
        private static float EventRoomFraction => DungeonTunables.EventRoomFraction;

        /// <summary>Never convert this many rooms or fewer — a 1-room dungeon keeps its fight. A setting.</summary>
        private static int MinPlainRooms => DungeonTunables.MinPlainRooms;

        private static readonly List<DungeonRoomEventDef> tmpEligible = new List<DungeonRoomEventDef>();

        /// <summary>
        /// Assigns events to a subset of the given combat rooms, in place. Each
        /// room's <see cref="DungeonRoom.eventDef"/> is set (or left null).
        /// Depth/budget are recorded on the room so runtime hooks can replay them.
        /// </summary>
        public static void AssignEvents(List<DungeonRoom> combatRooms, DungeonKindDef kind, MobRankTier rank,
                                        List<float> budgets, List<MobRankTier> tiers)
        {
            if (combatRooms == null || combatRooms.Count == 0 || kind == null)
                return;

            List<DungeonRoomEventDef> all = DefDatabase<DungeonRoomEventDef>.AllDefsListForReading;
            if (all.Count == 0)
                return;

            int n = combatRooms.Count;

            // EVENT DENSITY IS KEYED TO THE CRITICAL PATH, not to the room count.
            // Wings add combat rooms to this list, so counting all of them would
            // have quietly doubled event density on a high-rank dungeon — the
            // 0.34 fraction below is tuned against how many fights the player
            // must walk through, and wings are optional.
            int spineRooms = 0;
            for (int i = 0; i < n; i++)
            {
                if (combatRooms[i].onCriticalPath)
                    spineRooms++;
            }
            if (spineRooms == 0)
                spineRooms = n;

            int maxEvents = Mathf.Min(
                Mathf.FloorToInt(spineRooms * EventRoomFraction),
                Mathf.Max(0, n - MinPlainRooms));
            if (maxEvents <= 0)
                return;

            // Per-def usage so maxPerDungeon is honoured.
            var used = new Dictionary<DungeonRoomEventDef, int>();

            // Walk rooms in a random order so events are not always front-loaded.
            var order = new List<int>();
            for (int i = 0; i < n; i++)
                order.Add(i);
            order.Shuffle();

            int placed = 0;
            for (int oi = 0; oi < order.Count && placed < maxEvents; oi++)
            {
                int i = order[oi];
                DungeonRoom room = combatRooms[i];
                // Depth is the LAYOUT's to author (DungeonLayoutPlan writes it
                // for every room); this used to overwrite it from the list index,
                // which is not depth once a dungeon branches. eventBudget is
                // still recorded here so runtime hooks can replay it.
                float depth = room.depthFrac;
                room.eventBudget = (budgets != null && i < budgets.Count) ? budgets[i] : 0f;

                tmpEligible.Clear();
                for (int e = 0; e < all.Count; e++)
                {
                    DungeonRoomEventDef def = all[e];
                    if (!def.AllowsRank(rank) || !def.AllowsMode(kind.layoutMode) || !def.AllowsDepth(depth))
                        continue;
                    if (used.TryGetValue(def, out int count) && count >= def.maxPerDungeon)
                        continue;
                    tmpEligible.Add(def);
                }
                if (tmpEligible.Count == 0)
                    continue;

                DungeonRoomEventDef picked = tmpEligible.RandomElementByWeightWithFallback(d => d.weight);
                tmpEligible.Clear();
                if (picked == null)
                    continue;

                room.eventDef = picked;
                // Recorded exactly as PopulateRoomEvent will see them, so a
                // runtime event (Ambush, Mana Spring) replays at the ROOM's
                // ramped tier and factored budget, not at the full gate rank.
                room.eventBudget *= picked.budgetFactor;
                room.eventTier = (tiers != null && i < tiers.Count) ? (int)tiers[i] : (int)rank;
                used[picked] = used.TryGetValue(picked, out int c) ? c + 1 : 1;
                placed++;
            }
        }

        /// <summary>
        /// Runs a room's event population, if it has one. Returns true when the
        /// event REPLACED the normal encounter (so the caller skips it).
        /// A worker that vetoes or throws leaves the room as an ordinary fight —
        /// an event must never be able to produce an empty room.
        /// </summary>
        public static bool PopulateRoomEvent(Map map, DungeonMapComponent comp, DungeonRoom room,
                                             DungeonKindDef kind, MobRankTier rank, MobRankTier tier,
                                             float depth, float budget)
        {
            if (room?.eventDef == null)
                return false;

            DungeonRoomEventDef def = room.eventDef;
            try
            {
                if (!def.Worker.CanPlace(map, room, kind, rank))
                {
                    room.eventDef = null;
                    return false;
                }

                var ctx = new DungeonRoomEventContext(map, comp, room, kind, rank, tier, depth,
                                                      budget * def.budgetFactor);
                def.Worker.Populate(ctx);
                return def.replacesEncounter;
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Room event {def.defName} threw during population; " +
                          $"the room falls back to an ordinary encounter. {ex}");
                room.eventDef = null;
                return false;
            }
        }
    }
}
