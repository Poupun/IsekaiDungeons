using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The spines: the thing each multi-floor dungeon carries between its own
    /// floors, and the reason the form is a dungeon rather than several
    /// dungeons in a row.
    ///
    /// THE RULE THEY ARE ALL HELD TO is the one the Titan Pit's cut tithe
    /// established: a spine must be something the player can PLAY AGAINST.
    /// Regrowth is answered by moving fast, the climate by packing right, the
    /// retreat by hunting stragglers. A tax that admits no counterplay is
    /// subtraction, not difficulty, and does not belong here.
    ///
    /// Every hook is wrapped by MultiFloorFloors, so a spine that throws costs
    /// its own effect and never the floor.
    /// </summary>
    internal static class SpineUtil
    {
        /// <summary>
        /// The room the party walks in through. Entry is the one role the
        /// aggro sweep deliberately skips, so anything put here has to be woken
        /// by hand — which is exactly right for a spine whose premise is that
        /// these things came in behind you.
        /// </summary>
        public static DungeonRoom EntryRoom(DungeonMapComponent comp)
        {
            if (comp?.rooms == null || comp.rooms.Count == 0)
                return null;
            for (int i = 0; i < comp.rooms.Count; i++)
            {
                if (comp.rooms[i].role == DungeonRoomRole.Entry)
                    return comp.rooms[i];
            }
            return comp.rooms[0];
        }

        /// <summary>Combat rooms, in layout order. Empty on a floor that has none.</summary>
        public static List<DungeonRoom> CombatRooms(DungeonMapComponent comp)
        {
            var list = new List<DungeonRoom>();
            if (comp?.rooms == null)
                return list;
            for (int i = 0; i < comp.rooms.Count; i++)
            {
                if (comp.rooms[i].role == DungeonRoomRole.Combat)
                    list.Add(comp.rooms[i]);
            }
            return list;
        }

        /// <summary>
        /// Puts creatures that FOLLOWED the party into the entry room, awake
        /// and hostile.
        ///
        /// They arrive at the entrance because that is where something chasing
        /// you would come from. Scattered through the level they would read as
        /// ordinary population, which is the one thing a spine spawn must not
        /// read as.
        ///
        /// Generated at the rank of the floor ABOVE, not this one. What
        /// followed you is what you already fought, so it should not have
        /// gained a tier on the stairs — and it keeps the arrival survivable on
        /// a floor whose own population is already a step harder.
        /// </summary>
        public static int SpawnFollowers(Map map, DungeonKindDef kind, MobRankTier rank,
                                         int count, float depth)
        {
            DungeonMapComponent comp = map?.GetComponent<DungeonMapComponent>();
            DungeonRoom room = EntryRoom(comp);
            if (room == null || count <= 0)
                return 0;

            var made = new List<Pawn>();
            for (int i = 0; i < count; i++)
            {
                Pawn p = MakeOne(map, comp, room, kind, rank, depth);
                if (p == null)
                    break;
                made.Add(p);
            }

            if (made.Count > 0)
                DungeonHostility.Wake(made, map);
            return made.Count;
        }

        /// <summary>
        /// Spends a combat-power budget across the given rooms, registering each
        /// spawn into the room it lands in so the ordinary aggro sweep wakes it.
        /// Used by the spines that make a floor DENSER rather than putting
        /// something at your back.
        /// </summary>
        public static int SpawnByBudget(Map map, List<DungeonRoom> rooms, DungeonKindDef kind,
                                        MobRankTier rank, float budget, float depth,
                                        int maxPerRoom = 8)
        {
            DungeonMapComponent comp = map?.GetComponent<DungeonMapComponent>();
            if (comp == null || rooms == null || rooms.Count == 0 || budget <= 0f)
                return 0;

            float each = budget / rooms.Count;
            int total = 0;

            for (int r = 0; r < rooms.Count; r++)
            {
                float left = each;
                int inRoom = 0;
                while (inRoom < maxPerRoom && left > 0f)
                {
                    if (!DungeonRoster.TryPickFromRoster(kind, depth, left, out PawnKindDef pick, rank)
                        && !DungeonSpawnResolver.TryPickCreature(rank, left, out pick))
                        break;
                    if (pick == null)
                        break;

                    float cost = DungeonSpawnResolver.CombatPowerOf(pick);
                    if (inRoom > 0 && cost > left)
                        break;   // budget genuinely spent; only the first may overshoot

                    if (MakeOne(map, comp, rooms[r], kind, rank, depth, pick) == null)
                        break;
                    inRoom++;
                    total++;
                    left -= cost;
                }
            }
            return total;
        }

        private static Pawn MakeOne(Map map, DungeonMapComponent comp, DungeonRoom room,
                                    DungeonKindDef kind, MobRankTier rank, float depth,
                                    PawnKindDef forced = null)
        {
            PawnKindDef pick = forced;
            if (pick == null
                && !DungeonRoster.TryPickFromRoster(kind, depth, 99999f, out pick, rank)
                && !DungeonSpawnResolver.TryPickCreature(rank, 99999f, out pick))
                return null;
            if (pick == null)
                return null;

            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                pick, DungeonHostility.FactionFor(pick), PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true));
            if (p == null)
                return null;

            if (!CellFinder.TryFindRandomCellNear(room.rect.CenterCell, map, 8,
                    c => c.Standable(map) && room.rect.Contains(c), out IntVec3 cell))
                cell = CellFinder.StandableCellNear(room.rect.CenterCell, map, 12);
            if (!cell.IsValid)
                cell = map.Center;

            GenSpawn.Spawn(p, cell, map, Rot4.Random);
            DungeonSpawnResolver.NormalizeRank(p, rank);
            comp.RegisterCreature(room, p);
            return p;
        }

        /// <summary>Registered creatures still standing.</summary>
        public static int CountLiving(DungeonMapComponent comp)
        {
            if (comp?.rooms == null)
                return 0;
            int n = 0;
            for (int i = 0; i < comp.rooms.Count; i++)
            {
                List<Pawn> cs = comp.rooms[i].creatures;
                if (cs == null)
                    continue;
                for (int j = 0; j < cs.Count; j++)
                {
                    Pawn p = cs[j];
                    if (p != null && !p.Dead && !p.Destroyed && p.Spawned)
                        n++;
                }
            }
            return n;
        }

        /// <summary>The rank of the floor above (or below, in the spire).</summary>
        public static MobRankTier PreviousRank(MultiFloorContext ctx) =>
            ctx.def == null ? ctx.rank : ctx.def.RankForFloor(Mathf.Max(1, ctx.floor - 1));

        /// <summary>One room's worth of encounter points at this floor's rank, with the encounter settings applied.</summary>
        public static float RoomBudget(MultiFloorContext ctx)
        {
            DungeonKindDef kind = ctx.def?.KindForFloor(ctx.floor);
            if (kind?.encounterPointsByRank == null)
                return 0f;
            return kind.encounterPointsByRank.Evaluate(ctx.def.RankIndexForFloor(ctx.floor))
                   * DungeonTunables.EncounterMultiplierFor(ctx.map);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE SUNKEN OSSUARY — your dead rise
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// What you killed on the last floor is waiting on the next one.
    ///
    /// Counted as the party takes the stair and spawned as the next floor
    /// builds, which is the natural fit for the hook pair: OnFloorLeft runs
    /// while the old map still stands, OnFloorGenerated before the party
    /// arrives.
    ///
    /// ENEMY CORPSES ONLY, and that narrows the original pitch on purpose.
    /// "Including yours" was the line, but TowerFloors.Retire evacuates every
    /// player-faction corpse to the destination deliberately — it is the pass
    /// that stops a colonist who died on an abandoned floor vanishing with no
    /// body and no message. Trading a real data-loss guard for a horror beat is
    /// a bad trade, and the beat survives without it: what you killed gets up
    /// and follows you down, and burning it costs the time you wanted to spend
    /// descending.
    ///
    /// CAPPED, because the risen leave corpses of their own. Uncapped, a slow
    /// party compounds itself into an unwinnable floor 5, which is the failure
    /// this dungeon already sits one bad fight away from.
    /// </summary>
    public class SpineWorker_RisingDead : MultiFloorSpineWorker
    {
        /// <summary>A setting (Deep dungeons tab).</summary>
        private static int MaxRisen => IsekaiDungeonsSettings.Current.risingDeadMax;

        public override void OnFloorLeft(MultiFloorContext ctx)
        {
            if (ctx.map == null)
                return;
            int left = 0;
            List<Thing> corpses = ctx.map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            for (int i = 0; i < corpses.Count; i++)
            {
                if (corpses[i] is Corpse c && c.InnerPawn?.Faction != Faction.OfPlayer)
                    left++;
            }
            ctx.dungeon.spineCounter = Mathf.Min(left, MaxRisen);
        }

        public override void OnFloorGenerated(MultiFloorContext ctx)
        {
            int owed = ctx.dungeon.spineCounter;
            ctx.dungeon.spineCounter = 0;
            if (owed <= 0)
                return;

            int made = SpineUtil.SpawnFollowers(ctx.map, ctx.def.KindForFloor(ctx.floor),
                                                SpineUtil.PreviousRank(ctx), owed, ctx.DepthFrac);
            if (made <= 0)
                return;

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Spine.RisingDead.Label".Translate(),
                "IsekaiDungeons.Spine.RisingDead.Text".Translate(made.Named("COUNT")),
                LetterDefOf.ThreatBig, new TargetInfo(ctx.map.Center, ctx.map));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE KOBOLD UNDERCITY — the retreat
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Anything still breathing when you take the stair does not stay behind.
    /// It runs ahead of you and is waiting with the next floor's garrison.
    ///
    /// The mirror of the ossuary, and the two sit a full rank band apart on
    /// purpose: this one rewards THOROUGHNESS and that one rewards SPEED, so a
    /// player should not be taught both at once.
    ///
    /// The counter-play is legible and physical — hunt the runners, or pay the
    /// bill at the queen's door.
    /// </summary>
    public class SpineWorker_Retreat : MultiFloorSpineWorker
    {
        /// <summary>A setting (Deep dungeons tab).</summary>
        private static int MaxFled => IsekaiDungeonsSettings.Current.retreatMax;

        public override void OnFloorLeft(MultiFloorContext ctx)
        {
            DungeonMapComponent comp = ctx.map?.GetComponent<DungeonMapComponent>();
            ctx.dungeon.spineCounter =
                Mathf.Min(ctx.dungeon.spineCounter + SpineUtil.CountLiving(comp), MaxFled);
        }

        public override void OnFloorGenerated(MultiFloorContext ctx)
        {
            int fled = ctx.dungeon.spineCounter;
            ctx.dungeon.spineCounter = 0;
            if (fled <= 0)
                return;

            // What flees keeps fleeing: leave these alive too and OnFloorLeft
            // counts them again, which is how a warren-wide bill accumulates
            // across four floors out of one lazy first clear.
            int made = SpineUtil.SpawnFollowers(ctx.map, ctx.def.KindForFloor(ctx.floor),
                                                SpineUtil.PreviousRank(ctx), fled, ctx.DepthFrac);
            if (made <= 0)
                return;

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Spine.Retreat.Label".Translate(),
                "IsekaiDungeons.Spine.Retreat.Text".Translate(made.Named("COUNT")),
                LetterDefOf.ThreatSmall, new TargetInfo(ctx.map.Center, ctx.map));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GORE HELL — regrowth
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A floor you cleared does not stay cleared. The hive grows back behind
    /// you, and the way out is back up through all of it.
    ///
    /// ONCE PER FLOOR, and that latch is the whole reason this is shippable.
    /// GenStep_DungeonPopulation states the rule outright: the mod pays per
    /// KILL, so a cleared floor that repopulates on every visit is an income
    /// stream a player can run forever. One regrowth per floor per dungeon is
    /// bounded — six floors, six bills, and the place is spent — which is the
    /// mechanic as designed rather than a farm wearing its clothes.
    ///
    /// How MUCH grows back is how far past it you got. Turn around on the next
    /// landing and the hive has barely noticed; dive to the bottom and every
    /// floor above you is worth crossing again.
    /// </summary>
    public class SpineWorker_Regrowth : MultiFloorSpineWorker
    {
        /// <summary>Share of a normal floor's population at full regrowth, and at none. Settings.</summary>
        private static float MaxShare => Mathf.Max(MinShare, IsekaiDungeonsSettings.Current.regrowthMaxShare);
        private static float MinShare => IsekaiDungeonsSettings.Current.regrowthMinShare;

        /// <summary>Floors of depth past this one that count as fully grown back. A setting.</summary>
        private static float FullAtDepth => Mathf.Max(1f, IsekaiDungeonsSettings.Current.regrowthFullAtDepth);

        public override void OnFloorGenerated(MultiFloorContext ctx)
        {
            MultiFloorRecord rec = ctx.dungeon?.RecordIfSeen(ctx.floor);
            if (rec == null || !rec.cleared || rec.regrown)
                return;

            // Latched BEFORE the spawn, not after: a throw halfway through must
            // not leave a floor able to regrow a second time.
            rec.regrown = true;

            float since = Mathf.Clamp01((ctx.dungeon.deepestReached - ctx.floor) / FullAtDepth);
            float share = Mathf.Lerp(MinShare, MaxShare, since);

            List<DungeonRoom> rooms = SpineUtil.CombatRooms(
                ctx.map.GetComponent<DungeonMapComponent>());
            if (rooms.Count == 0)
                return;

            float budget = SpineUtil.RoomBudget(ctx) * rooms.Count * share;
            int made = SpineUtil.SpawnByBudget(ctx.map, rooms, ctx.def.KindForFloor(ctx.floor),
                                               ctx.rank, budget, ctx.DepthFrac);
            if (made <= 0)
                return;

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Spine.Regrowth.Label".Translate(),
                "IsekaiDungeons.Spine.Regrowth.Text".Translate(
                    ctx.floor.Named("FLOOR"), made.Named("COUNT")),
                LetterDefOf.ThreatBig, new TargetInfo(ctx.map.Center, ctx.map));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE FOUNDRY STACK — alert level
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The plant notices you. A counter that rises with every floor and with
    /// every hour spent on one, and each floor below is reinforced by it.
    ///
    /// SPEED PRESSURE WITH NO CLOCK, which matters because this whole dungeon
    /// form is defined by having no break timer. The alert is pressure the
    /// player can see and manage rather than a countdown they can only lose to,
    /// and it is the only spine here that a careful party can finish at zero
    /// cost.
    ///
    /// TIME IS THE ONLY INPUT, and the original design also wanted noise. Noise
    /// is not measurable without hooking weapon fire, which this mod does not
    /// do; the per-floor step stands in for "you were seen getting here", and
    /// dwelling is what actually costs you. Honest, and legible in one number.
    ///
    /// Rides on both scratch fields, and the hook ORDER is what keeps them from
    /// colliding: OnFloorLeft(n) reads the entry tick, OnFloorGenerated(n+1)
    /// reads the alert, OnFloorEntered(n+1) writes the next entry tick.
    /// </summary>
    public class SpineWorker_Alert : MultiFloorSpineWorker
    {
        /// <summary>Alert added simply for arriving on a new floor. A setting.</summary>
        private static float PerFloor => IsekaiDungeonsSettings.Current.alertPerFloor;

        /// <summary>
        /// Alert added per in-game day spent on a floor.
        ///
        /// TUNED AGAINST THE SIX-FLOOR PLAN, because a spine that saturates is
        /// not a spine. At these values a party moving at a quarter-day a floor
        /// arrives at the bottom around 38%, a day a floor around 75%, and one
        /// that camps pegs at 100% — a gradient the player's own pace decides.
        /// The first pass used 0.55 and hit the ceiling by floor 3 at any
        /// ordinary pace, which is the same as having no mechanic. A setting.
        /// </summary>
        private static float PerDay => IsekaiDungeonsSettings.Current.alertPerDay;

        /// <summary>Share of a normal floor's population added at full alert. A setting.</summary>
        private static float MaxShare => IsekaiDungeonsSettings.Current.alertMaxShare;

        public override void OnFloorEntered(MultiFloorContext ctx)
        {
            ctx.dungeon.spineCounter = Find.TickManager.TicksGame;

            if (ctx.dungeon.spineValue > 0.01f)
            {
                Messages.Message(
                    "IsekaiDungeons.Spine.Alert.Level".Translate(
                        Mathf.RoundToInt(ctx.dungeon.spineValue * 100f).Named("PCT")),
                    new TargetInfo(ctx.map.Center, ctx.map),
                    MessageTypeDefOf.CautionInput, false);
            }
        }

        public override void OnFloorLeft(MultiFloorContext ctx)
        {
            float days = 0f;
            if (ctx.dungeon.spineCounter > 0)
                days = (Find.TickManager.TicksGame - ctx.dungeon.spineCounter)
                       / (float)GenDate.TicksPerDay;

            ctx.dungeon.spineValue =
                Mathf.Clamp01(ctx.dungeon.spineValue + PerFloor + Mathf.Max(0f, days) * PerDay);
        }

        public override void OnFloorGenerated(MultiFloorContext ctx)
        {
            float alert = ctx.dungeon?.spineValue ?? 0f;
            if (alert <= 0.01f)
                return;

            List<DungeonRoom> rooms = SpineUtil.CombatRooms(
                ctx.map.GetComponent<DungeonMapComponent>());
            if (rooms.Count == 0)
                return;

            float budget = SpineUtil.RoomBudget(ctx) * rooms.Count * MaxShare * alert;
            int made = SpineUtil.SpawnByBudget(ctx.map, rooms, ctx.def.KindForFloor(ctx.floor),
                                               ctx.rank, budget, ctx.DepthFrac);
            if (made <= 0)
                return;

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Spine.Alert.Label".Translate(),
                "IsekaiDungeons.Spine.Alert.Text".Translate(
                    Mathf.RoundToInt(alert * 100f).Named("PCT"), made.Named("COUNT")),
                LetterDefOf.ThreatBig, new TargetInfo(ctx.map.Center, ctx.map));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COLDREACH DEEP and THE HELL HOLE — the climate pair
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A temperature ramp that deepens with the descent.
    ///
    /// These two are the preparation dungeons: their difficulty is answered in
    /// the caravan loadout rather than in the fighting, which nothing else in
    /// the mod currently asks for. Cold and heat are one worker with the sign
    /// flipped, deliberately, so the pair teaches one lesson from both ends of
    /// the ladder.
    ///
    /// EACH FLOOR TARGETS AN ABSOLUTE TEMPERATURE and the offset is derived
    /// from whatever the map actually reads at generation. A fixed offset would
    /// have made the dungeon mean different things in different biomes — a hell
    /// hole in a desert forty degrees worse than the same hell hole in tundra,
    /// with the tundra one not really hot at all. The floor plan says what the
    /// bottom of the shaft IS; the tile only says how far it had to travel.
    ///
    /// Built on the vanilla temperature-offset condition, whose offset field is
    /// public and scribed — so no condition class of our own is needed and the
    /// per-floor value survives a save by itself. It must be written AFTER
    /// RegisterCondition, which calls Init() and would otherwise stamp the
    /// def's own flat offset back over it.
    ///
    /// ROOM TEMPERATURES ARE PUSHED TOO. An offset only moves the OUTDOOR
    /// figure; a sealed room drifts toward it over hours, and the Hell Hole is
    /// carved and roofed throughout. Waiting out the drift would mean the
    /// bottom of hell is room temperature for the first hour you stand in it.
    ///
    /// THIS IS WHAT THE UNATTENDED-DESCENT VETO EXISTS FOR. Heat and cold do
    /// not care that nobody is watching, so auto-clear stops rather than take a
    /// party somewhere their gear cannot survive.
    /// </summary>
    public abstract class SpineWorker_Climate : MultiFloorSpineWorker
    {
        /// <summary>Ambient the first floor sits at, and the last. Absolute, in Celsius.</summary>
        protected abstract float FirstFloorTemp { get; }
        protected abstract float LastFloorTemp { get; }
        protected abstract string EnteredKey { get; }

        /// <summary>What this floor should read, whatever the tile outside says.</summary>
        protected float TargetFor(MultiFloorDungeonDef plan, int floor)
        {
            if (plan == null || plan.floorCount <= 1)
                return LastFloorTemp;
            float t = Mathf.Clamp01((floor - 1) / (float)(plan.floorCount - 1));
            return Mathf.Lerp(FirstFloorTemp, LastFloorTemp, t);
        }

        public override void OnFloorGenerated(MultiFloorContext ctx)
        {
            if (ctx.map?.gameConditionManager == null)
                return;

            float target = TargetFor(ctx.def, ctx.floor);
            float offset = target - ctx.map.mapTemperature.OutdoorTemp;
            if (Mathf.Abs(offset) < 0.5f)
                return;

            GameConditionDef def = offset < 0f
                ? IsekaiDungeonsDefOf.IsekaiDungeons_DeepCold
                : IsekaiDungeonsDefOf.IsekaiDungeons_DeepHeat;
            if (def == null)
                return;

            GameCondition cond = GameConditionMaker.MakeConditionPermanent(def);
            ctx.map.gameConditionManager.RegisterCondition(cond);   // this calls Init()
            if (cond is GameCondition_TemperatureOffset t)
                t.tempOffset = offset;                              // so the override lands after it

            // Sealed rooms hold whatever the outdoor figure was BEFORE the
            // condition and would equalize toward it over hours. Setting them
            // now is what makes the bottom of the shaft cold the moment you
            // open the door rather than an hour later.
            IReadOnlyList<Room> rooms = ctx.map.regionGrid.AllRooms;
            for (int i = 0; i < rooms.Count; i++)
                rooms[i].Temperature = target;
        }

        public override void OnFloorEntered(MultiFloorContext ctx)
        {
            if (ctx.map == null)
                return;
            Messages.Message(
                EnteredKey.Translate(
                    Mathf.RoundToInt(ctx.map.mapTemperature.OutdoorTemp).Named("TEMP")),
                new TargetInfo(ctx.map.Center, ctx.map), MessageTypeDefOf.CautionInput, false);
        }

        /// <summary>
        /// Whether the party AS EQUIPPED can survive the next floor.
        ///
        /// The line is the pawn's own SAFE range, which is where RimWorld itself
        /// begins accruing hypothermia and heatstroke — not a threshold invented
        /// here. It moves with a parka, which is the whole point: the answer
        /// should change when the player changes the loadout.
        ///
        /// Neither hediff is fast. A party a dozen degrees past the line has
        /// most of a day before it kills them. But unattended IS most of a day
        /// and nobody is reading the health tab, so the place to stop is the
        /// place where the damage starts.
        /// </summary>
        public override bool AllowsUnattendedDescent(MultiFloorContext ctx, out string reason)
        {
            reason = null;
            Map map = ctx.map;
            if (map == null || ctx.def == null)
                return true;

            int next = ctx.floor + 1;
            if (next > ctx.def.floorCount)
                return true;

            // Absolute targets mean the next floor's reading needs no deriving:
            // it is simply what that floor is authored to be.
            float projected = TargetFor(ctx.def, next);

            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null)
                    continue;
                FloatRange safe = p.SafeTemperatureRange();
                if (projected < safe.min || projected > safe.max)
                {
                    reason = "IsekaiDungeons.Spine.Climate.Refused".Translate(
                        p.LabelShortCap.Named("PAWN"),
                        Mathf.RoundToInt(projected).Named("TEMP"));
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Coldreach Deep. Seven floors from freezing to forty-five below.
    ///
    /// The bottom sits just inside what the best cold gear in the game can
    /// answer, so floors 6 and 7 are the ones that ask a question — while a
    /// party that packed nothing is stopped at floor 2.
    /// </summary>
    public class SpineWorker_Cold : SpineWorker_Climate
    {
        // Defaults 0 and -45; settings, Deep dungeons tab.
        protected override float FirstFloorTemp => IsekaiDungeonsSettings.Current.coldFirstFloorTemp;
        protected override float LastFloorTemp => IsekaiDungeonsSettings.Current.coldDeepestTemp;
        protected override string EnteredKey => "IsekaiDungeons.Spine.Cold.Entered";
    }

    /// <summary>
    /// The Hell Hole. Ten floors, and the mirror of the above.
    ///
    /// Tops out nearer to comfortable than Coldreach reaches downward, and that
    /// asymmetry is not sloppiness: RimWorld gives a player far more insulation
    /// against cold than against heat, so the same nominal distance is a much
    /// harsher ask upward. Fifty-eight at the bottom is past everyone, which is
    /// the intent — the last floors are a survival problem on top of a combat
    /// problem, and heatstroke is slow enough to make that a race rather than a
    /// wall.
    /// </summary>
    public class SpineWorker_Heat : SpineWorker_Climate
    {
        // Defaults 28 and 58; settings, Deep dungeons tab.
        protected override float FirstFloorTemp => IsekaiDungeonsSettings.Current.heatFirstFloorTemp;
        protected override float LastFloorTemp => IsekaiDungeonsSettings.Current.heatDeepestTemp;
        protected override string EnteredKey => "IsekaiDungeons.Spine.Heat.Entered";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE SLIME CISTERN — splitting
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A medium or large slime that dies bursts into two of the next size
    /// down. Population grows as you fight, so a slow clear leaves you facing
    /// a crowd you made yourself; the counter-play is killing the small ones
    /// before they pile up, and not leaving a large one half-dead.
    ///
    /// Polled, not hooked: the mod is zero-Harmony, and the map-state poll
    /// already runs once a second. The CORPSE is the record - it is consumed
    /// by the split (the body is what becomes the two), so a corpse still
    /// lying there is a split that has not happened yet, and a save/load
    /// cannot split anything twice.
    ///
    /// Small slimes never split, which bounds it: a large is at most two
    /// mediums and four smalls. The boss never splits; its death ends the floor.
    /// </summary>
    public class SpineWorker_Split : MultiFloorSpineWorker
    {
        private static readonly List<Pawn> tmpDead = new List<Pawn>();

        public override void OnFloorTick(MultiFloorContext ctx, DungeonMapComponent comp)
        {
            if (comp?.rooms == null)
                return;
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                DungeonRoom room = comp.rooms[r];
                if (room.creatures == null)
                    continue;
                tmpDead.Clear();
                for (int i = 0; i < room.creatures.Count; i++)
                {
                    Pawn p = room.creatures[i];
                    if (p != null && p.Dead && p != comp.boss && p.Corpse != null && p.Corpse.Spawned
                        && p.Corpse.Map == ctx.map && SmallerOf(p.kindDef) != null)
                        tmpDead.Add(p);
                }
                for (int i = 0; i < tmpDead.Count; i++)
                    Split(ctx, comp, room, tmpDead[i]);
            }
            tmpDead.Clear();
        }

        /// <summary>IsekaiSlime_Color_Large -> _Medium, _Medium -> _Small, else null.</summary>
        private static PawnKindDef SmallerOf(PawnKindDef kind)
        {
            string n = kind?.defName;
            if (n == null || !n.StartsWith("IsekaiSlime_"))
                return null;
            string smaller = n.EndsWith("_Large") ? n.Substring(0, n.Length - 6) + "_Medium"
                           : n.EndsWith("_Medium") ? n.Substring(0, n.Length - 7) + "_Small"
                           : null;
            return smaller == null ? null : DefDatabase<PawnKindDef>.GetNamedSilentFail(smaller);
        }

        private static void Split(MultiFloorContext ctx, DungeonMapComponent comp, DungeonRoom room, Pawn dead)
        {
            PawnKindDef into = SmallerOf(dead.kindDef);
            Corpse corpse = dead.Corpse;
            IntVec3 at = corpse.Position;
            Map map = ctx.map;

            corpse.Destroy(DestroyMode.Vanish);
            FilthMaker.TryMakeFilth(at, map, ThingDefOf.Filth_Slime, 3);

            var made = new List<Pawn>(2);
            for (int i = 0; i < 2; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    into, DungeonHostility.FactionFor(into), PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true));
                if (p == null)
                    continue;
                IntVec3 cell = CellFinder.StandableCellNear(at, map, 2);
                GenSpawn.Spawn(p, cell.IsValid ? cell : at, map, Rot4.Random);
                DungeonSpawnResolver.NormalizeRank(p, ctx.rank);
                comp.RegisterCreature(room, p);
                made.Add(p);
            }
            if (made.Count > 0)
                DungeonHostility.Wake(made, map);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // THE DRAGON NEST — the hoard
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Floors pay nothing on the way down. Everything they would have paid
    /// waits in one hoard on the bottom shelf, and it opens when the nest's
    /// master dies.
    ///
    /// A commitment mechanic: every other dungeon lets you bank progress by
    /// leaving. Here the XP and the mood are still paid floor by floor - the
    /// fights happened - but the things are not, so turning back means leaving
    /// the hoard where it is. Nothing is lost by leaving, though: the tally is
    /// on the world object, and the hoard is still there when you come back.
    ///
    /// The tally is a bitmask of withheld floors in spineCounter (floors 1..31,
    /// far past the eight this dungeon has). On completion each withheld floor
    /// is paid at ITS OWN rank, so the hoard is exactly what the descent would
    /// have paid, gathered in one place, rather than a multiplier on the last
    /// floor that would overpay the shallow ones.
    /// </summary>
    public class SpineWorker_Hoard : MultiFloorSpineWorker
    {
        public override bool WithholdsFloorLoot(MultiFloorContext ctx) =>
            ctx.def != null && !ctx.def.IsLastFloor(ctx.floor);

        public override void OnFloorCleared(MultiFloorContext ctx)
        {
            if (ctx.def == null || ctx.def.IsLastFloor(ctx.floor) || ctx.floor < 1 || ctx.floor > 31)
                return;
            ctx.dungeon.spineCounter |= 1 << (ctx.floor - 1);
        }

        public override void OnRunCompleted(MultiFloorContext ctx)
        {
            DungeonMapComponent comp = ctx.map?.GetComponent<DungeonMapComponent>();
            if (comp == null || ctx.def == null)
                return;

            IntVec3 cell = comp.RewardCell;
            int paid = 0;
            for (int f = 1; f < ctx.def.floorCount && f <= 31; f++)
            {
                if ((ctx.dungeon.spineCounter & (1 << (f - 1))) == 0)
                    continue;
                comp.PlaceClearReward(ctx.def.RankForFloor(f), cell);
                paid++;
            }
            ctx.dungeon.spineCounter = 0;
            if (paid == 0)
                return;

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.Hoard.Label".Translate(),
                "IsekaiDungeons.MultiFloor.Hoard.Text".Translate(paid.Named("FLOORS"), ctx.def.LabelCap.Named("PLACE")),
                LetterDefOf.PositiveEvent, new TargetInfo(cell, ctx.map));
        }
    }
}
