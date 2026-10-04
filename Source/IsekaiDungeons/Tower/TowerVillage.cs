using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace IsekaiDungeons
{
    /// <summary>How a floor's village stands, across regenerations.</summary>
    public enum TowerVillageState : byte
    {
        /// <summary>Not a village floor, or not built yet.</summary>
        None = 0,
        /// <summary>Standing and willing to trade. Rebuilt as itself on every revisit.</summary>
        Friendly = 1,
        /// <summary>The player killed them. Rebuilt permanently as empty ruins.</summary>
        Razed = 2,
    }

    /// <summary>
    /// Builds a living settlement on a tower floor: a market square, a handful
    /// of villagers, and a shopkeeper who will actually trade.
    ///
    /// EVERY STEP HERE IS LOAD-BEARING AND THE ORDER IS NOT NEGOTIABLE. The
    /// vanilla trade path has a dense set of preconditions and each one was
    /// verified against decompiled source:
    ///
    ///  - Pawn already implements ITrader, so no custom trader type is needed,
    ///    and nothing in the trade path checks the map type. A pocket map is
    ///    fine.
    ///  - A trader with NO LORD crashes the right-click menu:
    ///    FloatMenuOptionProvider_Trade dereferences clickedPawn.GetLord()
    ///    unguarded. Every villager gets a lord.
    ///  - The lord must be LordJob_DefendPoint. Anything with an exit toil
    ///    (TradeWithColony, VisitColony) sends villagers to the map edge, and
    ///    JobDriver_Goto despawns them into WorldPawns on arrival even though
    ///    pocket maps have no exit grid.
    ///  - SetFaction AFTER building the trader silently destroys it and evicts
    ///    the pawn from its lord. So: generate WITH faction, set the flag, add
    ///    components, assign traderKind, spawn, then lord. Never reorder.
    ///  - wantsToTradeWithColony and traderKind must be set together;
    ///    CanTradeNow dereferences traderKind unguarded.
    ///  - Nothing on a fresh pocket map is sellable: ColonyThingsWillingToBuy
    ///    demands Home area or storage. So a small market rect is painted into
    ///    map.areaManager.Home. Small and empty on purpose — Home area makes
    ///    ANY loose item in it sellable, including village dressing.
    ///  - CheckGiveTraderQuest is deliberately NOT called. It would hand out
    ///    world quests scaled off the pocket map's wealth.
    /// </summary>
    public static class TowerVillage
    {
        /// <summary>Runtime-created, hidden, mod-owned.</summary>
        private const string FactionDefName = "IsekaiDungeons_TowerFolk";

        /// <summary>How many villagers a market floor gets (inclusive roll). Settings, Tower tab.</summary>
        private static int MinVillagers => IsekaiDungeonsSettings.Current.towerVillagersMin;
        private static int MaxVillagers => UnityEngine.Mathf.Max(MinVillagers, IsekaiDungeonsSettings.Current.towerVillagersMax);
        private const int MarketRadius = 6;

        /// <summary>
        /// The villagers' faction: hidden, so trading grants no goodwill and
        /// killing them costs none either.
        ///
        /// Explicitly NOT the Adventurers' Guild faction. That one is visible
        /// with real goodwill, so a massacre inside the tower would leak into
        /// the player's actual world relations — a consequence nobody asked
        /// for and one the player cannot see coming.
        /// </summary>
        public static Faction EnsureFaction()
        {
            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(FactionDefName);
            if (def == null)
                return null;
            Faction existing = Find.FactionManager?.FirstFactionOfDef(def);
            if (existing != null)
                return existing;
            try
            {
                Faction faction = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(def));
                Find.FactionManager.Add(faction);
                return faction;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not create the tower-folk faction: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Populates a village floor. Returns the state to record.
        ///
        /// A RAZED village rebuilds as empty ruins, permanently. One enum
        /// survives the destroy-and-regenerate cycle where a list of pawns
        /// could not, and it means the consequence of a massacre outlives the
        /// map it happened on.
        /// </summary>
        public static TowerVillageState Build(Map map, TowerFloorRecord rec, TowerVillageState previous)
        {
            if (map == null || rec == null)
                return TowerVillageState.None;

            IntVec3 centre = MarketCentre(map);
            PaintMarket(map, centre);
            ScatterDressing(map, centre, previous == TowerVillageState.Razed);

            if (previous == TowerVillageState.Razed)
            {
                // They do not come back. The ruins are the memory.
                return TowerVillageState.Razed;
            }

            Faction faction = EnsureFaction();
            if (faction == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No tower-folk faction; floor {rec.floor} village will be empty.");
                return TowerVillageState.None;
            }

            var villagers = new List<Pawn>();
            int count = Rand.RangeInclusive(MinVillagers, MaxVillagers);
            for (int i = 0; i < count; i++)
            {
                Pawn p = MakeVillager(map, faction, centre, isTrader: i == 0, rec: rec);
                if (p != null)
                    villagers.Add(p);
            }

            if (villagers.Count == 0)
                return TowerVillageState.None;

            // DEFEND POINT and nothing else. Every other lord job this could
            // plausibly use ends in an exit toil, and an exiting villager walks
            // to the map edge and is swallowed by WorldPawns.
            try
            {
                int wander = VillagerScatterRadius(map) + 4;
                LordMaker.MakeNewLord(faction,
                    new LordJob_DefendPoint(centre, wanderRadius: wander, defendRadius: wander + 6),
                    map, villagers);
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Village lord failed on floor {rec.floor}: {ex.Message}");
            }

            return TowerVillageState.Friendly;
        }

        /// <summary>
        /// Fixed ordering, and it is the part most likely to be broken by a
        /// well-meaning refactor. See the class comment.
        /// </summary>
        private static Pawn MakeVillager(Map map, Faction faction, IntVec3 centre, bool isTrader, TowerFloorRecord rec)
        {
            // Everyone is a villager, shopkeeper included. The trader is not a
            // different KIND of person — they are the one holding the stock.
            PawnKindDef kind = PawnKindDefOf.Villager;

            Pawn pawn;
            try
            {
                // Generated WITH the faction. Calling SetFaction later would
                // destroy the trader tracker and evict the pawn from its lord.
                pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind, faction, PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true, mustBeCapableOfViolence: false));
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not generate a villager: {ex.Message}");
                return null;
            }
            if (pawn == null)
                return null;

            if (isTrader)
            {
                TraderKindDef traderKind = PickTraderKind(rec);
                if (traderKind != null)
                {
                    // These three, in this order, are what vanilla's
                    // PawnGroupKindWorker_Trader.GenerateTrader does — minus
                    // CheckGiveTraderQuest, which would hand out world quests
                    // priced off the pocket map's wealth.
                    pawn.mindState.wantsToTradeWithColony = true;
                    PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, actAsIfSpawned: true);
                    pawn.trader.traderKind = traderKind;

                    // WITHOUT THIS THE SHOP DOES NOT EXIST.
                    //
                    // Pawn_TraderTracker.CanTradeNow ends with a check that the
                    // trader has at least one good its traderKind will trade,
                    // and Goods reads the trader's own inventory (plus lord
                    // members acting as carriers). A shopkeeper with an empty
                    // inventory fails that check, so the trade option never
                    // appears and the whole village reads as decorative — with
                    // no error anywhere to say why.
                    GiveWares(pawn, traderKind, map, faction);
                }
            }

            // SPREAD ACROSS THE WHOLE SETTLEMENT. A village is people at home
            // and in the street, not a knot of strangers standing on one tile —
            // so the search radius is the town, and only the shopkeeper is
            // pinned to the market square where the player will look for them.
            int radius = isTrader ? MarketRadius : VillagerScatterRadius(map);

            // RandomClosewalkCellNear returns ROOT on failure, not Invalid — so
            // an IsValid check after it can never fire. Ask the Try- form, which
            // actually reports failure.
            if (!CellFinder.TryRandomClosewalkCellNear(centre, map, radius, out IntVec3 cell,
                                                       c => c.Standable(map)))
            {
                cell = CellFinder.StandableCellNear(centre, map, radius * 2);
                if (!cell.IsValid)
                {
                    pawn.Destroy();
                    return null;
                }
            }
            GenSpawn.Spawn(pawn, cell, map);

            // A pocket map ticks like any other, so villagers get hungry while
            // the player is off exploring. They are not a survival simulation:
            // top them up so a shopkeeper is never found dead of starvation.
            if (pawn.needs?.food != null)
                pawn.needs.food.CurLevel = pawn.needs.food.MaxLevel;
            if (pawn.needs?.rest != null)
                pawn.needs.rest.CurLevel = pawn.needs.rest.MaxLevel;

            return pawn;
        }

        /// <summary>
        /// Stocks the shop.
        ///
        /// Vanilla builds trader wares with ThingSetMakerDefOf.TraderStock,
        /// parameterised by the trader kind — that is what decides a bulk-goods
        /// caravan carries food and a war merchant carries weapons. Generating
        /// them by hand would mean reimplementing every trader kind's stock
        /// table.
        ///
        /// Goods go into the shopkeeper's INVENTORY rather than onto the ground:
        /// Pawn_TraderTracker.Goods reads the inventory, and anything left on
        /// the floor of a market whose Home area is painted would also become
        /// sellable back to its own owner.
        /// </summary>
        private static void GiveWares(Pawn trader, TraderKindDef traderKind, Map map, Faction faction)
        {
            try
            {
                ThingSetMakerParams parms = default;
                parms.traderDef = traderKind;
                parms.tile = map.Tile;
                parms.makingFaction = faction;

                List<Thing> wares = ThingSetMakerDefOf.TraderStock.root.Generate(parms);
                for (int i = 0; i < wares.Count; i++)
                {
                    Thing t = wares[i];
                    // Live pawns in the wares (slaves, pack animals) cannot go
                    // in an inventory. A tower market is not a slave market;
                    // discard them rather than spawn something unintended.
                    if (t is Pawn p)
                    {
                        p.Destroy();
                        continue;
                    }
                    if (!trader.inventory.innerContainer.TryAdd(t))
                        t.Destroy();
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not stock the tower shop: {ex.Message}");
            }
        }

        /// <summary>
        /// How far from the centre ordinary villagers may be placed: the whole
        /// settlement when there is one, otherwise the old market disc.
        /// </summary>
        private static int VillagerScatterRadius(Map map)
        {
            if (map.Parent is TowerFloorMapParent floorParent && floorParent.villageRect.Area > 0)
                return Mathf.Max(MarketRadius, Mathf.Min(floorParent.villageRect.Width, floorParent.villageRect.Height) / 2);
            return MarketRadius;
        }

        /// <summary>Neolithic low down, outlander higher up — the tower gets richer as it climbs.</summary>
        private static TraderKindDef PickTraderKind(TowerFloorRecord rec)
        {
            string name = rec.rankIndex >= 4f ? "Caravan_Outlander_Exotic"
                        : rec.rankIndex >= 2f ? "Caravan_Outlander_BulkGoods"
                                              : "Caravan_Neolithic_BulkGoods";
            TraderKindDef def = DefDatabase<TraderKindDef>.GetNamedSilentFail(name)
                                ?? DefDatabase<TraderKindDef>.GetNamedSilentFail("Caravan_Neolithic_BulkGoods");
            if (def == null)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No usable TraderKindDef for the tower shop.");
            return def;
        }

        private static IntVec3 MarketCentre(Map map)
        {
            // THE VILLAGE, not the door.
            //
            // This used to anchor on the arrival cell — the way OUT — which is
            // where a party lands, not where anybody lives. The houses are laid
            // down separately by GenStep_TowerVillage during map generation, so
            // the villagers were being placed at one end of the floor and their
            // homes at the other: a market square at the exit and an empty
            // hamlet in the middle.
            //
            // The settlement footprint is recorded on the map parent precisely
            // so this half can find it.
            if (map.Parent is TowerFloorMapParent floorParent
                && floorParent.villageRect.Area > 0)
            {
                IntVec3 inTown = CellFinder.StandableCellNear(floorParent.villageRect.CenterCell, map, 12);
                if (inTown.IsValid)
                    return inTown;
            }

            IntVec3 cell = CellFinder.StandableCellNear(map.Center, map, 25);
            return cell.IsValid ? cell : map.Center;
        }

        /// <summary>
        /// Paints the market into the Home area, which is the ONLY reason the
        /// player can sell anything here: ColonyThingsWillingToBuy requires
        /// Home area or storage, and a fresh pocket map has neither.
        ///
        /// Kept small and left empty on purpose. Home area makes every loose
        /// item inside it sellable, so a generous rect would quietly put the
        /// village's own scenery on the shop counter.
        /// </summary>
        private static void PaintMarket(Map map, IntVec3 centre)
        {
            Area_Home home = map.areaManager?.Home;
            if (home == null)
                return;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(centre, MarketRadius, true))
            {
                if (c.InBounds(map) && c.Standable(map))
                    home[c] = true;
            }
        }

        /// <summary>
        /// Dressing: a few campfires and stools so the market reads as a place
        /// people live, plus torches for light. Deliberately no walls — a
        /// village floor is Open, and building a compound on it would make it
        /// a dungeon with friendlier occupants.
        /// </summary>
        private static void ScatterDressing(Map map, IntVec3 centre, bool ruined)
        {
            ThingDef fire = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
            ThingDef torch = DefDatabase<ThingDef>.GetNamedSilentFail("TorchLamp");
            ThingDef debris = DefDatabase<ThingDef>.GetNamedSilentFail("ChunkSlagSteel");

            int props = ruined ? 6 : 4;
            for (int i = 0; i < props; i++)
            {
                ThingDef def = ruined ? debris : (i == 0 ? fire : torch);
                if (def == null)
                    continue;
                if (!CellFinder.TryRandomClosewalkCellNear(centre, map, MarketRadius + 3, out IntVec3 cell,
                        c => c.Standable(map) && c.GetEdifice(map) == null))
                    continue;
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                if (t.def.CanHaveFaction && !ruined)
                    t.SetFactionDirect(null);
                GenSpawn.Spawn(t, cell, map);
            }
        }

        /// <summary>
        /// Reads a live village floor and decides what to remember about it.
        /// Called from the gate as the floor is retired — the one moment the
        /// outcome can still be observed.
        /// </summary>
        public static TowerVillageState Observe(Map map, TowerVillageState current)
        {
            if (current != TowerVillageState.Friendly || map == null)
                return current;

            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(FactionDefName);
            Faction faction = def != null ? Find.FactionManager?.FirstFactionOfDef(def) : null;
            if (faction == null)
                return current;

            // ONE death or capture is enough. The village used to count as
            // razed only once EVERYONE was dead, and it is rebuilt with fresh
            // villagers and a freshly stocked shopkeeper on every visit - so
            // killing just the trader, looting the stock and re-entering was
            // an endless trade-goods farm. The tower folk are hidden and never
            // turn hostile, so their memory is the only consequence there is.
            List<Thing> corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            for (int i = 0; i < corpses.Count; i++)
            {
                if (corpses[i] is Corpse corpse && corpse.InnerPawn?.Faction == faction)
                    return TowerVillageState.Razed;
            }

            bool anyAlive = false;
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p.Faction != faction || p.Dead)
                    continue;
                if (p.IsPrisonerOfColony)
                    return TowerVillageState.Razed;
                anyAlive = true;
            }
            // Everyone who lived here is gone. That is permanent.
            return anyAlive ? TowerVillageState.Friendly : TowerVillageState.Razed;
        }
    }
}
