using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Behaviour for one <see cref="DungeonRoomEventDef"/>.
    ///
    /// Two lifecycle points, deliberately separated:
    ///   - <see cref="Populate"/> runs at MAP GENERATION. Spawn things, place
    ///     loot, build scenery. Free to allocate; runs inside vanilla's seeded
    ///     GenStep bracket, so plain Rand is deterministic per map.
    ///   - <see cref="OnEntered"/> runs the first time a player pawn steps into
    ///     the room, at play time. This is where a trap fires and an ambush
    ///     springs. It fires exactly once per room.
    ///
    /// Workers are stateless and cached on the def. Anything that must persist
    /// belongs on <see cref="DungeonRoom"/> (scribed) — never in a field here.
    /// </summary>
    public class DungeonRoomEventWorker
    {
        /// <summary>
        /// Veto hook. Return false when this specific room cannot host the
        /// event (too small, wrong shape, a needed def is missing). The room
        /// then falls back to an ordinary encounter rather than being left
        /// empty, so a veto is always safe.
        /// </summary>
        public virtual bool CanPlace(Map map, DungeonRoom room, DungeonKindDef kind, MobRankTier rank)
        {
            return true;
        }

        /// <summary>Generation-time setup. Register any pawns you spawn with the component.</summary>
        public virtual void Populate(DungeonRoomEventContext ctx)
        {
        }

        /// <summary>
        /// Fires once, when the party first enters the room. Default does
        /// nothing — most events are fully built at generation time.
        /// </summary>
        public virtual void OnEntered(DungeonRoomEventContext ctx)
        {
        }

        // ── Shared helpers for concrete events ──────────────────────────────

        /// <summary>Random standable cell inside the room, or the centre as a last resort.</summary>
        protected static IntVec3 RandomCellIn(Map map, DungeonRoom room)
        {
            if (CellFinder.TryFindRandomCellInsideWith(room.rect, c => c.Standable(map), out IntVec3 cell))
                return cell;
            return room.rect.CenterCell;
        }

        /// <summary>
        /// Spawns a creature group worth roughly <paramref name="budget"/> combat
        /// power at the given tier, clustered near an anchor, and registers it
        /// with the room so it participates in aggro and (Open) the leash.
        /// </summary>
        protected static int SpawnGroup(DungeonRoomEventContext ctx, MobRankTier tier, float budget, IntVec3 anchor, int maxCount = 20)
        {
            int spawned = 0;
            float left = budget;
            while (spawned < maxCount && left > 0f)
            {
                if (!DungeonRoster.TryPickFromRoster(ctx.kind, ctx.depth, left, out PawnKindDef pick, ctx.rank)
                    && !DungeonSpawnResolver.TryPickCreature(tier, left, out pick))
                    break;
                if (pick == null)
                    break;

                float cost = DungeonSpawnResolver.CombatPowerOf(pick);
                if (spawned > 0 && cost > left)
                    break;

                if (!CellFinder.TryFindRandomCellNear(anchor, ctx.map, 6,
                        c => c.Standable(ctx.map) && ctx.room.rect.Contains(c), out IntVec3 cell))
                    cell = RandomCellIn(ctx.map, ctx.room);

                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    pick, DungeonHostility.FactionFor(pick), PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true));
                if (pawn == null)
                    break;

                GenSpawn.Spawn(pawn, cell, ctx.map, Rot4.Random);
                DungeonSpawnResolver.NormalizeRank(pawn, tier);
                ctx.component.RegisterCreature(ctx.room, pawn);
                spawned++;
                left -= cost;
            }
            return spawned;
        }

        /// <summary>Drops a list of things around a point, never losing any to a blocked cell.</summary>
        protected static void ScatterLoot(Map map, DungeonRoom room, IntVec3 center, List<Thing> loot)
        {
            if (loot == null)
                return;
            for (int i = 0; i < loot.Count; i++)
            {
                if (!GenPlace.TryPlaceThing(loot[i], center, map, ThingPlaceMode.Near))
                    GenPlace.TryPlaceThing(loot[i], room.rect.CenterCell, map, ThingPlaceMode.Near);
            }
        }

        /// <summary>One-shot player message anchored on the room.</summary>
        protected static void Message(DungeonRoomEventContext ctx, string key, MessageTypeDef type = null)
        {
            if (key.NullOrEmpty())
                return;
            Messages.Message(key.Translate(),
                new TargetInfo(ctx.room.rect.CenterCell, ctx.map),
                type ?? MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }

    /// <summary>
    /// Everything an event worker needs, bundled so the signatures stay stable
    /// as the framework grows — adding a field here never breaks a modder's
    /// override.
    /// </summary>
    public class DungeonRoomEventContext
    {
        public Map map;
        public DungeonMapComponent component;
        public DungeonRoom room;
        public DungeonKindDef kind;
        public MobRankTier rank;

        /// <summary>Room tier after the difficulty ramp (may be below the gate rank).</summary>
        public MobRankTier tier;

        /// <summary>0 = first combat room, 1 = the last before the boss.</summary>
        public float depth;

        /// <summary>Encounter budget this room would have spent on a normal fight.</summary>
        public float budget;

        public DungeonRoomEventContext() { }

        public DungeonRoomEventContext(Map map, DungeonMapComponent component, DungeonRoom room,
                                       DungeonKindDef kind, MobRankTier rank, MobRankTier tier,
                                       float depth, float budget)
        {
            this.map = map;
            this.component = component;
            this.room = room;
            this.kind = kind;
            this.rank = rank;
            this.tier = tier;
            this.depth = depth;
            this.budget = budget;
        }

        /// <summary>
        /// Rebuilds a context at play time (OnEntered) from what AssignEvents
        /// recorded on the room: its ramped tier and its factored budget.
        /// </summary>
        public static DungeonRoomEventContext ForRuntime(DungeonMapComponent comp, DungeonRoom room)
        {
            MobRankTier rank = comp.Rank;
            MobRankTier tier = room.eventTier >= 0 && room.eventTier <= (int)rank
                ? (MobRankTier)room.eventTier
                : rank;
            return new DungeonRoomEventContext(comp.map, comp, room, comp.Kind, rank, tier,
                                               Mathf.Clamp01(room.depthFrac), room.eventBudget);
        }
    }
}
