using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsekaiDungeons
{
    /// <summary>
    /// Per-dungeon-map brain. Installed ONLY on dungeon maps via
    /// MapGeneratorDef.customMapComponents (vanilla instantiates it with
    /// Activator.CreateInstance(type, map) — the (Map) ctor is load-bearing),
    /// so colony maps never pay a tick for it.
    ///
    /// Responsibilities:
    ///  - own the room registry the GenSteps build (layout → population),
    ///  - room-scoped aggro: a room's creatures charge only once a player pawn
    ///    steps inside its rect, so the crawl is paced room by room,
    ///  - clear detection (boss dead OR removed — a boss sealed into a Sealing
    ///    Cube despawns, and the house convention counts that as defeated),
    ///  - clear rewards: full clear-XP to every colonist inside + the loot
    ///    pile in the boss room, then telling the gate.
    /// </summary>
    public class DungeonMapComponent : CustomMapComponent
    {
        public List<DungeonRoom> rooms = new List<DungeonRoom>();
        public Pawn boss;

        /// <summary>
        /// Consecutive clear checks the boss has been despawned without being
        /// in flight. Not saved: a reload simply restarts the short grace.
        /// </summary>
        private int bossAbsentChecks;
        public int BossAbsentChecks => bossAbsentChecks;
        public Building_DungeonGate gate;
        public Building_DungeonExit exit;

        private bool cleared;

        /// <summary>
        /// Whether the dungeon has been settled. Read by
        /// ThoughtWorker_DungeonDread, which lifts the standing mood penalty
        /// the moment this turns true.
        /// </summary>
        public bool Cleared => cleared;

        /// <summary>
        /// This map was generated with its population deliberately suppressed —
        /// a tower floor the player already beat. Distinct from `cleared`:
        /// that field returns early ABOVE the aggro sweep, so reusing it here
        /// would disable aggro on maps that still have things standing on them.
        /// This one only ever means "there was never anything to fight".
        /// </summary>
        private bool generatedEmpty;

        /// <summary>True when the map was built as an already-beaten floor.</summary>
        public bool GeneratedEmpty => generatedEmpty;

        /// <summary>Called by the population step when it skips its whole job.</summary>
        public void Notify_GeneratedEmpty()
        {
            generatedEmpty = true;
        }



        /// <summary>
        /// Arrival title card. Presentation only — not scribed, because a card
        /// mid-fade is a sub-second visual state and replaying it on load would
        /// announce a dungeon the player is already standing in.
        /// </summary>
        private readonly DungeonTitleCard titleCard = new DungeonTitleCard();

        /// <summary>Whether this map was the viewed map last frame (edge detection for the card).</summary>
        private bool wasCurrentMap;

        /// <summary>A creature harmed within this many ticks counts as "in a fight".</summary>
        private const int RecentHarmTicks = 600;

        public DungeonMapParent DungeonParent => map.Parent as DungeonMapParent;

        /// <summary>Rank via the map parent first — it outlives the gate (see DungeonMapParent).</summary>
        public MobRankTier Rank => DungeonParent?.rank ?? gate?.rank ?? MobRankTier.F;

        public DungeonKindDef Kind =>
            DungeonParent?.kindDef ?? gate?.kindDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;

        public bool IsCleared => cleared;

        public DungeonMapComponent(Map map) : base(map)
        {
        }

        // ── GenStep entry points ────────────────────────────────────────────

        public void RegisterLayout(List<DungeonRoom> newRooms)
        {
            rooms = newRooms ?? new List<DungeonRoom>();
        }

        public void RegisterCreature(DungeonRoom room, Pawn pawn)
        {
            if (room != null && pawn != null)
                room.creatures.Add(pawn);
        }

        public void RegisterBoss(Pawn pawn)
        {
            boss = pawn;
        }

        // ── Lifecycle hooks ─────────────────────────────────────────────────

        public override void MapGenerated()
        {
            // The gate is only discoverable through this static during
            // generation (UndercaveMapComponent uses the source-map lister for
            // the same job; ours is cleaner because the portal IS the gate).
            // A TOWER FLOOR legitimately has neither. It was not opened by a
            // gate — the party caravanned to the tower — and its way out is a
            // Building_TowerExit that leads to the world rather than a
            // PocketMapExit that leads to a building. Reporting either as a
            // fault would mean an error every single time the player enters the
            // tower, which trains them to ignore the log.
            // A multi-floor dungeon floor is the same case (caravan in, world
            // exit out), and GenStep_DungeonEntrance already gives it the tower
            // exit, so the two checks must agree on what counts.
            bool towerFloor = map.Parent is TowerFloorMapParent
                              || map.Parent is MultiFloorMapParent;

            gate = PocketMapUtility.currentlyGeneratingPortal as Building_DungeonGate;
            if (gate == null && !towerFloor)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon map generated without a gate (dev tools?). Rank falls back to the map parent.");

            if (exit == null && !towerFloor)
            {
                // Fallback resolve, mirroring UndercaveMapComponent.MapGenerated.
                List<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Building_DungeonExit e)
                    {
                        exit = e;
                        break;
                    }
                }
                if (exit == null)
                    Log.Error($"{IsekaiDungeonsMod.LogPrefix} Dungeon map has no exit portal — entering pawns would be trapped. Check GenStep_DungeonEntrance.");
            }
            else if (towerFloor && !TowerFloors.HasWayOut(map))
            {
                // The tower's own way out is missing, which strands the party
                // just as surely. Different building, same fatal condition.
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Tower / multi-floor dungeon floor has no way out — the party would be trapped. Check GenStep_DungeonEntrance.");
            }

            // A deliberately empty floor has no boss BY DESIGN, so the missing
            // boss is not a bug there and must not be reported as one. A
            // mirror arena has no boss YET — its population walks in through
            // the door and gets copied.
            if (boss == null && !generatedEmpty && !(Kind?.mirrorArena ?? false))
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Dungeon map generated without a boss — the dungeon can never be cleared. Check GenStep_DungeonPopulation.");
        }

        public override void MapComponentOnGUI()
        {
            // Never draw over another map's view, and stay out of the way while
            // a full-screen window (menu, trade, architect tabs) is up.
            if (Find.CurrentMap != map || Find.WindowStack.WindowsForcePause)
                return;
            if (!IsekaiDungeonsSettings.Current.titleCardEnabled)
                return;
            titleCard.OnGUI();
        }

        public override void MapComponentTick()
        {
            // Arrival card. This lives in Tick and NOT in MapComponentUpdate
            // because Update only runs for the CURRENT map — it would see the
            // rising edge once and never the falling one, so the card would
            // play on the very first visit and never again. Tick runs for every
            // map, so both edges are observed and re-entering re-announces.
            // Above the `cleared` early-return on purpose: coming back into a
            // cleared dungeon to haul the hoard out should still name the place.
            bool isCurrent = Find.CurrentMap == map;
            if (isCurrent && !wasCurrentMap)
            {
                // A tower floor announces itself as a FLOOR. Same card, same
                // animation, same rank ribbon — only the hero line changes, and
                // ordinary dungeons take the identical path they always did.
                if (map.Parent is TowerFloorMapParent floorParent)
                {
                    titleCard.Trigger(DungeonNarrative.TitleFloorName(floorParent),
                                      DungeonNarrative.TitleRankLine(Rank),
                                      DungeonNarrative.TitleTagline(Kind),
                                      Rank);
                }
                else if (map.Parent is MultiFloorMapParent deepParent && deepParent.Plan != null)
                {
                    // A descent names the PLACE and the floor, not the
                    // environment kind every floor of it shares.
                    titleCard.Trigger("IsekaiDungeons.MultiFloor.FloorTileName".Translate(
                                          deepParent.Plan.LabelCap.Named("PLACE"),
                                          deepParent.floor.Named("FLOOR")),
                                      DungeonNarrative.TitleRankLine(Rank),
                                      DungeonNarrative.TitleTagline(Kind),
                                      Rank);
                }
                else
                {
                    titleCard.Trigger(Kind, Rank);
                }
            }
            wasCurrentMap = isCurrent;

            // Fuses run every tick — a 60-tick granularity would make a
            // 2-second telegraph land anywhere in a 1-second window.
            if (delayed.Count > 0)
                RunDelayed();

            // Before the cleared return: the copies' gear has to vanish after
            // the duel is won too, not only while it is being fought.
            if (mirrorGear.Count > 0 && Find.TickManager.TicksGame % DungeonTunables.MapStateInterval == 0)
                SweepMirrorGear();

            if (cleared)
                return;
            if (Find.TickManager.TicksGame % DungeonTunables.MapStateInterval != 0)
                return;

            // ClearCheck is two field reads — always cheap, and it must still
            // fire if the party kills the boss and leaves in the same window.
            ClearCheck();

            // A descent's spine may act mid-fight (the Cistern's slimes split).
            if (!cleared && map.Parent is MultiFloorMapParent)
                MultiFloorFloors.NotifyFloorTick(map, this);

            // The two sweeps only mean anything while someone is actually in
            // the dungeon. A player can enter, retreat, and leave the gate open
            // for days; without this the map would keep walking every creature
            // home and scanning every zone for intruders that are not there.
            // mapPawns keeps this list cached, so the check itself is free.
            if (map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count == 0)
                return;

            // ── The mirror arena's population arrives with the player ───────
            // Spawned HERE and not at generation because generation runs while
            // the party still stands on the previous floor: cloning at first
            // presence is what makes the roster mirror who ACTUALLY came
            // through, including a pawn added to the party at the last moment.
            // generatedEmpty: a beaten summit regenerates with no mirrors, the
            // same rule as every other cleared floor. Without it the arena
            // re-cloned the party (and re-paid the clear) on every visit.
            if (!arenaMirrorsSpawned && (Kind?.mirrorArena ?? false) && !cleared && !generatedEmpty)
                SpawnArenaMirrors();

            AggroSweep();
            LeashSweep();
        }

        // ── Aggro ───────────────────────────────────────────────────────────

        private void AggroSweep()
        {
            List<Pawn> players = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            if (players.Count == 0)
                return;

            for (int r = 0; r < rooms.Count; r++)
            {
                DungeonRoom room = rooms[r];
                if (room.aggroed || room.role == DungeonRoomRole.Entry)
                    continue;

                // Proximity: stepping into the zone wakes it.
                bool woke = false;
                for (int p = 0; p < players.Count; p++)
                {
                    if (room.rect.Contains(players[p].Position))
                    {
                        FireRoomEvent(room);   // trap springs / ambush lands BEFORE the wake
                        AggroRoom(room);
                        woke = true;
                        break;
                    }
                }
                if (woke)
                    continue;

                // Being SHOT wakes it too. On an unwalled field the whole map is
                // visible from arrival, so shooting into a zone from 30 cells
                // away is the obvious play - and without this the zone would
                // stay "dormant", leaving the leash to drag wounded creatures
                // back home mid-fight while the party plinks them apart.
                for (int c = 0; c < room.creatures.Count; c++)
                {
                    Pawn cr = room.creatures[c];
                    if (cr == null || cr.Dead || !cr.Spawned)
                        continue;
                    if (cr.mindState != null
                        && Find.TickManager.TicksGame - cr.mindState.lastHarmTick < RecentHarmTicks)
                    {
                        AggroRoom(room);
                        break;
                    }
                }
            }
        }

        // ── Group anchoring (Open dungeons) ─────────────────────────────────

        /// <summary>
        /// Keeps un-aggroed groups standing on their own patch of ground.
        ///
        /// In a Carve/Build dungeon, walls do this for free: a group physically
        /// cannot leave its room. An OPEN dungeon has no walls at all, and wild
        /// creatures wander — so within an hour the carefully ordered F → boss
        /// progression would smear itself across the field and the "walk deeper,
        /// meet stronger things" premise would be gone. This is the open-field
        /// replacement for a wall.
        ///
        /// Deliberately a soft leash, not a lord: giving these pawns a lord
        /// means giving them a FACTION, and a hostile faction makes them charge
        /// the moment the party lands, destroying the approach-at-your-own-pace
        /// pacing that the whole design rests on. A forced Goto also works on
        /// the Anomaly entities whose think trees ignore mental states.
        ///
        /// Aggroed groups are never leashed — once woken, a creature chases as
        /// far as it likes.
        /// </summary>
        private void LeashSweep()
        {
            if (Kind == null || Kind.layoutMode != DungeonLayoutMode.Open)
                return;

            for (int r = 0; r < rooms.Count; r++)
            {
                DungeonRoom room = rooms[r];
                if (room.aggroed || room.role == DungeonRoomRole.Entry)
                    continue;

                IntVec3 anchor = room.rect.CenterCell;
                // Half the zone plus a margin: creatures may roam their whole
                // zone freely, and are only pulled back once clearly outside it.
                float leash = (room.rect.Width * 0.5f) + DungeonTunables.LeashSlackCells;

                for (int i = room.creatures.Count - 1; i >= 0; i--)
                {
                    Pawn c = room.creatures[i];
                    if (c == null || c.Destroyed || c.Dead || !c.Spawned)
                    {
                        room.creatures.RemoveAt(i);
                        continue;
                    }
                    // Never yank a creature that is already fighting, fleeing or
                    // otherwise reacting: an InterruptForced Goto every 60 ticks
                    // would cancel its flee/approach and make it ping-pong.
                    if (c.InMentalState || c.Downed)
                        continue;
                    if (c.mindState != null
                        && (c.mindState.enemyTarget != null
                            || Find.TickManager.TicksGame - c.mindState.lastHarmTick < RecentHarmTicks))
                        continue;
                    if (c.CurJob != null && c.CurJob.def.alwaysShowWeapon)
                        continue; // mid-attack job
                    if (c.CurJobDef == JobDefOf.Goto)
                        continue; // already walking home
                    if (c.Position.DistanceTo(anchor) <= leash)
                        continue;

                    if (!CellFinder.TryFindRandomCellNear(anchor, map, Mathf.Max(2, room.rect.Width / 3),
                            cell => cell.Standable(map) && c.CanReach(cell, PathEndMode.OnCell, Danger.Deadly),
                            out IntVec3 home))
                        continue;

                    c.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Goto, home), JobCondition.InterruptForced);
                }
            }
        }

        /// <summary>
        /// Fires a room's event the first time the party walks in. Runs BEFORE
        /// AggroRoom so an ambush's spawns are included in the wake that follows.
        /// Wrapped: a third-party event worker that throws must not take the
        /// dungeon's aggro system down with it.
        /// </summary>
        private void FireRoomEvent(DungeonRoom room)
        {
            if (room.entered)
                return;
            room.entered = true;

            if (room.eventDef == null)
                return;
            try
            {
                room.eventDef.Worker.OnEntered(DungeonRoomEventContext.ForRuntime(this, room));
                if (!room.eventDef.enteredMessageKey.NullOrEmpty())
                {
                    Messages.Message(room.eventDef.enteredMessageKey.Translate(),
                        new TargetInfo(room.rect.CenterCell, map), MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Room event {room.eventDef.defName} threw on entry: {ex}");
            }
        }

        // ── Delayed actions (event fuses) ───────────────────────────────────

        private struct DelayedAction
        {
            public int fireTick;
            public System.Action action;
        }

        private readonly List<DelayedAction> delayed = new List<DelayedAction>();

        /// <summary>
        /// Runs an action in N ticks on this map. Exists so an event can
        /// TELEGRAPH before it hits (a trap that detonates the instant you cross
        /// the threshold is a coin flip, not a fight). Deliberately not scribed:
        /// a fuse is sub-second, and a save landing mid-fuse should simply not
        /// fire rather than resurrect an explosion on load.
        /// </summary>
        public void QueueDelayed(int delayTicks, System.Action action)
        {
            if (action == null)
                return;
            delayed.Add(new DelayedAction { fireTick = Find.TickManager.TicksGame + delayTicks, action = action });
        }

        private void RunDelayed()
        {
            for (int i = delayed.Count - 1; i >= 0; i--)
            {
                if (Find.TickManager.TicksGame < delayed[i].fireTick)
                    continue;
                System.Action a = delayed[i].action;
                delayed.RemoveAt(i);
                try { a(); }
                catch (System.Exception ex) { Log.Error($"{IsekaiDungeonsMod.LogPrefix} Delayed room-event action threw: {ex}"); }
            }
        }

        private void AggroRoom(DungeonRoom room)
        {
            room.aggroed = true;

            // Prune the dead first, then hand the survivors to DungeonHostility
            // as ONE group. It splits them by race: animals go manhunter exactly
            // as before, humanlikes get a shared assault lord instead — forcing
            // an aggro mental state on a soldier makes it hostile to every other
            // pawn on the map, including its own squad, which is what turned the
            // Gilded Garrison into a free-for-all.
            for (int i = room.creatures.Count - 1; i >= 0; i--)
            {
                Pawn c = room.creatures[i];
                if (c == null || c.Destroyed || c.Dead || !c.Spawned)
                    room.creatures.RemoveAt(i);
            }

            int woken = DungeonHostility.Wake(room.creatures, map);

            if (woken > 0)
            {
                Messages.Message(
                    "IsekaiDungeons.Message.RoomAggro".Translate(),
                    new TargetInfo(room.rect.CenterCell, map),
                    MessageTypeDefOf.ThreatSmall);
            }
        }

        // ── Mirror arena ───────────────────────────────────────────────────────────

        /// <summary>The duel has been set. Latched forever - one copy per visitor, once.</summary>
        private bool arenaMirrorsSpawned;

        /// <summary>The copies. The objective worker reads this list, so it is the save-authoritative record of the duel.</summary>
        private List<Pawn> arenaMirrors = new List<Pawn>();

        /// <summary>Drops a creature from every room's registry (it left the dungeon through a breach).</summary>
        public void ForgetCreature(Pawn pawn)
        {
            for (int r = 0; r < rooms.Count; r++)
                rooms[r].creatures.Remove(pawn);
        }

        public bool ArenaMirrorsSpawned => arenaMirrorsSpawned;
        public List<Pawn> ArenaMirrors => arenaMirrors;

        /// <summary>
        /// Every weapon, apparel piece and inventory item a copy was generated
        /// with, keyed to its copy. A copy's gear is a REFLECTION: it vanishes
        /// once its wearer is dead or downed. Without this the floor was a
        /// gear duplicator - each copy dropped your legendary, refined, runed
        /// weapon on death, and an uncleared summit re-cloned the party on
        /// every visit.
        /// </summary>
        private List<Thing> mirrorGear = new List<Thing>();

        /// <summary>Parallel to <see cref="mirrorGear"/>: the copy each item belongs to.</summary>
        private List<Pawn> mirrorGearOwners = new List<Pawn>();

        private void RecordMirrorGear(Pawn mirror)
        {
            if (mirror.equipment != null)
                foreach (ThingWithComps eq in mirror.equipment.AllEquipmentListForReading)
                    AddMirrorGear(eq, mirror);
            if (mirror.apparel != null)
                foreach (Apparel ap in mirror.apparel.WornApparel)
                    AddMirrorGear(ap, mirror);
            if (mirror.inventory != null)
                foreach (Thing t in mirror.inventory.innerContainer)
                    AddMirrorGear(t, mirror);
        }

        private void AddMirrorGear(Thing gear, Pawn owner)
        {
            mirrorGear.Add(gear);
            mirrorGearOwners.Add(owner);
        }

        /// <summary>
        /// Vanishes the gear of every copy that is dead or downed, wherever the
        /// item now is: on the ground, on the corpse, or already in a
        /// colonist's hands. Backwards so removal keeps the lists aligned.
        /// </summary>
        private void SweepMirrorGear(bool pruneOnly = false)
        {
            for (int i = mirrorGear.Count - 1; i >= 0; i--)
            {
                Thing gear = mirrorGear[i];
                Pawn owner = i < mirrorGearOwners.Count ? mirrorGearOwners[i] : null;
                bool defeated = owner == null || owner.Dead || owner.Destroyed || owner.Downed;
                if (gear != null && !gear.Destroyed && (!defeated || pruneOnly))
                    continue;

                if (gear != null && !gear.Destroyed)
                    gear.Destroy(DestroyMode.Vanish);
                mirrorGear.RemoveAt(i);
                if (i < mirrorGearOwners.Count)
                    mirrorGearOwners.RemoveAt(i);
            }
        }

        /// <summary>
        /// Clones every player humanlike standing on the map into the middle
        /// of the hall, hostile, and wakes them as one warband.
        ///
        /// The latch is set BEFORE any cloning so a mid-spawn exception can
        /// never re-run the copy next interval and field a second party. If
        /// every clone fails, the objective worker's empty-list rule opens the
        /// stair rather than sealing the floor forever - a broken forge should
        /// cost the fight, not the run.
        /// </summary>
        private void SpawnArenaMirrors()
        {
            arenaMirrorsSpawned = true;

            DungeonRoom hall = null;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].role == DungeonRoomRole.Boss)
                {
                    hall = rooms[i];
                    break;
                }
            }
            if (hall == null && rooms.Count > 0)
                hall = rooms[rooms.Count - 1];
            if (hall == null)
                return;

            List<Pawn> players = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            var toCopy = new List<Pawn>();
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i]?.RaceProps != null && players[i].RaceProps.Humanlike)
                    toCopy.Add(players[i]);
            }
            if (toCopy.Count == 0)
            {
                arenaMirrorsSpawned = false;   // animals scouting ahead do not start the duel
                return;
            }

            IntVec3 centre = hall.rect.CenterCell;
            Pawn strongest = null;
            int strongestLevel = -1;

            for (int i = 0; i < toCopy.Count; i++)
            {
                Pawn mirror = null;
                try
                {
                    mirror = MirrorForge.Clone(toCopy[i], map);
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Mirror clone failed for {toCopy[i].LabelShort}: {ex.Message}");
                }
                if (mirror == null)
                    continue;

                // AT the centre, not merely near it: the radial pattern walks
                // outward from the exact centre cell, so the first copy stands
                // on it and the rest close ranks around it, shoulder to
                // shoulder in the plaza light.
                IntVec3 cell = centre;
                for (int r = 0; r < GenRadial.NumCellsInRadius(8f); r++)
                {
                    IntVec3 candidate = centre + GenRadial.RadialPattern[r];
                    if (candidate.InBounds(map) && hall.rect.Contains(candidate)
                        && candidate.Standable(map) && candidate.GetFirstPawn(map) == null)
                    {
                        cell = candidate;
                        break;
                    }
                }

                GenSpawn.Spawn(mirror, cell, map, Rot4.South);
                RegisterCreature(hall, mirror);
                arenaMirrors.Add(mirror);
                RecordMirrorGear(mirror);

                int level = IsekaiComponent.GetCached(mirror)?.currentLevel ?? 0;
                if (level > strongestLevel)
                {
                    strongestLevel = level;
                    strongest = mirror;
                }
            }

            if (arenaMirrors.Count == 0)
                return;

            // The strongest copy is the floor's nominal boss: letters, loot
            // centring and every system that expects a boss to exist key off
            // it, even though the OBJECTIVE is the whole list.
            if (strongest != null)
                RegisterBoss(strongest);

            // Wake routes humanlikes into a hostile assault lord - the same
            // path a woken soldier room takes, so the copies fight exactly
            // like any authored warband.
            //
            // THE ROOM IS LATCHED AGGROED FIRST. The wake is ours, and without
            // the latch the ordinary AggroSweep re-fired on the hall the
            // moment a player pawn stood in its rect - re-lording pawns that
            // already had a lord, every sweep, which is both the "[Isekai
            // Raid] processed 1 pawns" log loop and a reliable way to corrupt
            // lord state into per-tick NullReferenceExceptions.
            hall.aggroed = true;
            hall.entered = true;
            DungeonHostility.Wake(arenaMirrors, map);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MirrorArena.Letter.Label".Translate(),
                "IsekaiDungeons.MirrorArena.Letter.Text".Translate(arenaMirrors.Count.Named("COUNT")),
                LetterDefOf.ThreatBig,
                strongest != null ? (LookTargets)strongest : new TargetInfo(centre, map));
        }

        // ── Clear ───────────────────────────────────────────────────────────

        private void ClearCheck()
        {
            // The WIN CONDITION is the archetype's, not ours. The default worker
            // is the old boss-death rule (including "removed from play", which
            // covers a boss sealed into a Sealing Cube); a custom dungeon can
            // demand something else entirely.
            DungeonKindDef kind = Kind;
            if (kind == null)
                return;

            // Consecutive checks the boss has been off the map in a form that
            // is not a leap. Read by the default objective worker; kept here
            // because workers must stay stateless.
            bool bossAbsent = boss != null && !boss.Dead && !boss.Destroyed
                              && !boss.Spawned && !(boss.ParentHolder is PawnFlyer);
            bossAbsentChecks = bossAbsent ? bossAbsentChecks + 1 : 0;

            if (kind.ObjectiveWorker.IsComplete(this))
                HandleCleared();
        }

        private void HandleCleared()
        {
            if (cleared)
                return;
            cleared = true;

            // The latch above is set before anything that can throw. If a
            // reward step fails (a third-party loot or objective worker), the
            // gate must still learn the dungeon is beaten, or a cleared map
            // sits behind a gate that keeps counting down and breaks later.
            // Multi-floor floors are excluded: clearing one floor is not
            // clearing the dungeon. Notify_Cleared is idempotent.
            try
            {
                GrantClearRewards();
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Dungeon clear rewards failed; sealing the gate anyway. {ex}");
                if (!(map.Parent is MultiFloorMapParent))
                    gate?.Notify_Cleared();
            }
        }

        private void GrantClearRewards()
        {
            MobRankTier rank = Rank;
            DungeonKindDef kind = Kind;
            kind.ObjectiveWorker.OnCompleted(this);
            int xpEach = DungeonTunables.ScaleXp(kind.LootWorker.ClearXpPerColonist(rank, kind), map);

            // Full XP to EVERY colonist standing in the dungeon — no headcount
            // split (house convention from the quest reward rescale).
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                IsekaiComponent.GetCached(colonists[i])?.GainXP(xpEach, "dungeon clear");

            // A floor of a multi-floor descent: its spine gets a say before
            // anything is placed (the Dragon Nest withholds), and the letter and
            // mood below speak about a floor rather than a finished dungeon.
            MultiFloorRun.Clear deep = MultiFloorRun.Begin(map);
            bool withhold = deep != null && deep.withholdLoot;

            // Loot pile in the boss room (fallback: wherever the boss fell,
            // then the exit — never silently skip the reward, G5).
            IntVec3 lootCenter = LootCenter();
            List<Thing> loot = withhold ? new List<Thing>()
                             : DungeonLootGenerator.WithValueScale(DungeonTunables.LootMultiplierFor(map),
                                                                   () => kind.LootWorker.GenerateClearLoot(rank, kind))
                               ?? new List<Thing>();
            Thing lookTarget = null;
            for (int i = 0; i < loot.Count; i++)
            {
                if (GenPlace.TryPlaceThing(loot[i], lootCenter, map, ThingPlaceMode.Near, out Thing placed))
                {
                    if (lookTarget == null)
                        lookTarget = placed;
                }
                else
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not place clear loot {loot[i].LabelCap} — dropping at exit instead.");
                    GenPlace.TryPlaceThing(loot[i], exit?.Position ?? lootCenter, map, ThingPlaceMode.Near);
                }
            }

            if (deep != null)
            {
                MultiFloorRun.Announce(deep, colonists, rank, xpEach,
                    lookTarget != null ? new TargetInfo(lookTarget.Position, map) : new TargetInfo(lootCenter, map));
                if (!withhold)
                    SpawnTreasureChest(rank, lootCenter);
                MultiFloorRun.Finish(deep, this, lootCenter, rank);
                return;
            }

            // A tower floor is not a dungeon and should not announce itself as
            // one: the player is 40 floors up, mid-climb, and "dungeon cleared"
            // reads as though the whole expedition just ended.
            int towerFloor = (map.Parent as TowerFloorMapParent)?.floor ?? 0;
            bool inTower = towerFloor > 0;

            // Mood, to the pawns who were actually standing in it — the same
            // list the XP went to. A tower floor is one step of a hundred and
            // pays the small stacking climb thought instead of the full clear
            // spike, for the same reason the letter above reads differently.
            if (inTower)
                DungeonMoods.GainFloorCleared(colonists, towerFloor, TowerTunables.FloorCount);
            else
                DungeonMoods.GainClear(colonists, rank);

            Find.LetterStack.ReceiveLetter(
                inTower
                    ? "IsekaiDungeons.Letter.FloorCleared.Label".Translate(towerFloor.Named("FLOOR"))
                    : "IsekaiDungeons.Letter.Cleared.Label".Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                inTower
                    ? "IsekaiDungeons.Letter.FloorCleared.Text".Translate(
                        towerFloor.Named("FLOOR"),
                        DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                        xpEach.ToString("N0").Named("XP"))
                    : "IsekaiDungeons.Letter.Cleared.Text".Translate(
                        DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                        xpEach.ToString("N0").Named("XP")),
                LetterDefOf.PositiveEvent,
                lookTarget != null ? new TargetInfo(lookTarget.Position, map) : new TargetInfo(lootCenter, map));

            gate?.Notify_Cleared();

            SpawnTreasureChest(rank, lootCenter);
        }

        /// <summary>
        /// Extra chests BELOW the headline, as an independent chance per step
        /// down the grade ladder. The inner array is walked one grade at a
        /// time, so jade's { .65, .40, .20 } means a gold at 65%, then a
        /// silver at 40%, then a bronze at 20% - each rolled on its own, so
        /// "jade + gold + silver" and "jade alone" are both ordinary results.
        ///
        /// THE HEADLINE IS NEVER ONE OF THESE ROLLS. An SSS clear always pays
        /// its jade chest and the tail only ever adds. Folding the top chest
        /// into the lottery would turn the grade the player actually earned
        /// into a coin flip, which is the opposite of what the rank band is
        /// for - and it is the guarantee that lets the tail be generous.
        ///
        /// Bronze has nothing beneath it, so its single roll repeats bronze:
        /// an F-rank clear that sometimes pays two, rather than the one tier
        /// on the ladder that can never vary.
        ///
        /// Expected chests per clear: bronze 1.20, silver 1.45, gold 1.85,
        /// jade 2.25.
        ///
        /// The four tables and a multiplier over all of them are settings
        /// (Rewards and Advanced tabs); the numbers above are their defaults.
        /// </summary>
        private static float[] ExtraChestChance(int tier)
        {
            IsekaiDungeonsSettings s = IsekaiDungeonsSettings.Current;
            switch (tier)
            {
                case 1: return s.extraChestChancesBronze;
                case 2: return s.extraChestChancesSilver;
                case 3: return s.extraChestChancesGold;
                default: return s.extraChestChancesJade;
            }
        }

        /// <summary>
        /// Grade words rather than "_T" + tier: RimWorld refuses any ThingDef
        /// defName that ends in a digit (it collides with the random-graphic
        /// suffix convention) with a startup config error.
        /// </summary>
        private static readonly string[] ChestDefGrades = { "Plain", "Fine", "Grand", "Mythic" };

        /// <summary>
        /// The clear's headline reward: a treasure chest at the rank's own
        /// grade, every time (T1 F-C, T2 B-A, T3 S-SS, T4 SSS-Nation), plus a
        /// tail of lower grades rolled from <see cref="ExtraChestChance"/>.
        ///
        /// Placed in the LOOT PILE, not on the corpse - see the note at the
        /// cell assignment for why that changed. Escalates through the exit,
        /// any standable cell and finally Direct placement rather than giving
        /// up, because the chest must never be the one reward that silently
        /// fails to arrive (G5).
        /// </summary>
        /// <summary>The chest grade a clear at this rank earns: 1 bronze .. 4 jade.</summary>
        public static int ChestTierFor(MobRankTier rank) =>
            rank <= MobRankTier.C ? 1
            : rank <= MobRankTier.A ? 2
            : rank <= MobRankTier.SS ? 3
            : 4;

        /// <summary>One chest of a grade in the loot pile, for rewards beyond the clear's own.</summary>
        public ThingDef PlaceBonusChest(int tier, IntVec3 cell)
        {
            var placed = new List<ThingDef>();
            PlaceChest(tier, cell, placed);
            return placed.Count > 0 ? placed[0] : null;
        }

        /// <summary>
        /// A whole clear's worth of loot and chests at a given rank, dropped at
        /// a cell - what a floor would have paid, paid somewhere else. The
        /// Dragon Nest's hoard is made of these.
        /// </summary>
        public void PlaceClearReward(MobRankTier rank, IntVec3 cell)
        {
            DungeonKindDef kind = Kind;
            if (kind == null)
                return;
            List<Thing> loot = DungeonLootGenerator.WithValueScale(DungeonTunables.LootMultiplierFor(map),
                                                                   () => kind.LootWorker.GenerateClearLoot(rank, kind))
                               ?? new List<Thing>();
            for (int i = 0; i < loot.Count; i++)
                GenPlace.TryPlaceThing(loot[i], cell, map, ThingPlaceMode.Near);
            SpawnTreasureChest(rank, cell);
        }

        /// <summary>Where the clear's rewards go: the boss room, else the boss, else the exit.</summary>
        public IntVec3 RewardCell => LootCenter();

        private void SpawnTreasureChest(MobRankTier rank, IntVec3 fallback)
        {
            try
            {
                int tier = ChestTierFor(rank);

                // WITH THE LOOT, NOT ON THE BOSS. The chest used to be placed
                // at the corpse's cell, which scattered the reward across the
                // floor: a pile here, a chest wherever the fight happened to
                // end. One place to walk to is worth more than the flourish of
                // the body marking the prize.
                IntVec3 cell = fallback;
                if (!cell.IsValid || !cell.InBounds(map))
                    cell = map.Center;

                // Counted rather than listed flat: a bronze double is a fifth
                // of all F-rank clears, and "a bronze chest and a bronze chest"
                // is not a sentence anyone wrote on purpose.
                var placed = new List<ThingDef>();
                PlaceChest(tier, cell, placed);

                float[] extras = ExtraChestChance(tier);
                float extraScale = IsekaiDungeonsSettings.Current.extraChestChanceMultiplier;
                for (int i = 0; i < extras.Length; i++)
                {
                    if (Rand.Value < Mathf.Clamp01(extras[i] * extraScale))
                        PlaceChest(Mathf.Max(1, tier - (i + 1)), cell, placed);
                }

                // Only worth saying when there is more than one - a lone chest
                // is already announced by being a chest.
                if (placed.Count > 1 && IsekaiDungeonsSettings.Current.chestHaulMessage)
                {
                    var counts = new Dictionary<ThingDef, int>();
                    for (int i = 0; i < placed.Count; i++)
                    {
                        counts.TryGetValue(placed[i], out int n);
                        counts[placed[i]] = n + 1;
                    }

                    LanguageWorker lang = Find.ActiveLanguageWorker;
                    var parts = new List<string>(counts.Count);
                    foreach (KeyValuePair<ThingDef, int> kv in counts)
                    {
                        parts.Add(kv.Value == 1
                            ? lang.WithIndefiniteArticle(kv.Key.label)
                            : kv.Value + " " + lang.Pluralize(kv.Key.label, kv.Value));
                    }

                    Messages.Message(
                        "IsekaiDungeons.Treasure.Haul".Translate(
                            parts.ToCommaList(useAnd: true).Named("CHESTS")),
                        new TargetInfo(cell, map), MessageTypeDefOf.PositiveEvent);
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not spawn the treasure chests: "
                          + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// One chest of a grade, next to the boss. A grade whose def is missing
        /// is skipped rather than fatal: the rest of the haul still arrives.
        /// </summary>
        /// <summary>
        /// One chest of a grade, in the loot pile.
        ///
        /// IT MUST NOT SILENTLY FAIL. Players reported clears that paid no
        /// chest at all, and every way that could happen was a quiet one:
        /// TryPlaceThing returns false when the cells around the target are
        /// full or unstandable and the old code simply moved on, so a boss room
        /// packed with rubble, corpses and the rest of the loot could eat the
        /// headline reward with nothing in the log. A dungeon clear owes a
        /// chest, so this escalates instead of giving up - the pile, then the
        /// exit, then any standable cell on the map, then Direct placement
        /// which cannot refuse - and only an unregistered def can stop it,
        /// which is a startup error rather than a silent one.
        /// </summary>
        private bool PlaceChest(int tier, IntVec3 cell, List<ThingDef> placed)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(
                "IsekaiDungeons_TreasureChest" + ChestDefGrades[Mathf.Clamp(tier - 1, 0, 3)]);
            if (def == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Treasure chest def for tier {tier} is "
                        + "missing; the clear cannot pay its chest.");
                return false;
            }

            Thing chest = ThingMaker.MakeThing(def);
            if (GenPlace.TryPlaceThing(chest, cell, map, ThingPlaceMode.Near))
            {
                placed.Add(def);
                return true;
            }

            IntVec3 alt = exit?.Position ?? IntVec3.Invalid;
            if (alt.IsValid && alt.InBounds(map)
                && GenPlace.TryPlaceThing(chest, alt, map, ThingPlaceMode.Near))
            {
                placed.Add(def);
                return true;
            }

            if (CellFinderLoose.TryGetRandomCellWith(
                    c => c.Standable(map) && !c.Fogged(map), map, 200, out IntVec3 any)
                && GenPlace.TryPlaceThing(chest, any, map, ThingPlaceMode.Near))
            {
                placed.Add(def);
                return true;
            }

            // Direct ignores what is already in the cell. Ugly, and still far
            // better than owing the player a chest and delivering nothing.
            if (GenPlace.TryPlaceThing(chest, cell, map, ThingPlaceMode.Direct))
            {
                placed.Add(def);
                return true;
            }

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not place a {def.label} anywhere on "
                      + "this map; the clear paid no chest.");
            return false;
        }

        private IntVec3 LootCenter()
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].role == DungeonRoomRole.Boss)
                    return rooms[i].rect.CenterCell;
            }
            if (boss != null && boss.Corpse != null && boss.Corpse.Spawned)
                return boss.Corpse.Position;
            if (exit != null)
                return exit.Position;
            return map.Center;
        }

        public void DebugForceClear()
        {
            if (cleared)
                return;
            if (boss != null && !boss.Dead && boss.Spawned)
                boss.Kill(null);
            else
                HandleCleared();
        }

        // ── Save/load ───────────────────────────────────────────────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref rooms, "rooms", LookMode.Deep);
            Scribe_Values.Look(ref arenaMirrorsSpawned, "arenaMirrorsSpawned", false);
            Scribe_Collections.Look(ref arenaMirrors, "arenaMirrors", LookMode.Reference);
            // Destroyed gear cannot be saved by reference; drop those entries
            // first. Prune only - never destroy things while the save runs.
            if (Scribe.mode == LoadSaveMode.Saving)
                SweepMirrorGear(pruneOnly: true);
            Scribe_Collections.Look(ref mirrorGear, "mirrorGear", LookMode.Reference);
            Scribe_Collections.Look(ref mirrorGearOwners, "mirrorGearOwners", LookMode.Reference);
            Scribe_References.Look(ref boss, "boss");
            Scribe_References.Look(ref gate, "gate");
            Scribe_References.Look(ref exit, "exit");
            Scribe_Values.Look(ref cleared, "cleared", defaultValue: false);
            Scribe_Values.Look(ref generatedEmpty, "generatedEmpty", defaultValue: false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && rooms == null)
                rooms = new List<DungeonRoom>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && arenaMirrors == null)
                arenaMirrors = new List<Pawn>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && (mirrorGear == null || mirrorGearOwners == null
                                                             || mirrorGear.Count != mirrorGearOwners.Count))
            {
                mirrorGear = new List<Thing>();
                mirrorGearOwners = new List<Pawn>();
            }
        }
    }
}
