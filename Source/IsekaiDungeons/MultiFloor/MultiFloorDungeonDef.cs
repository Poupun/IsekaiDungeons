using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// An authored multi-floor dungeon: a structure on the planet that you
    /// caravan to and walk down into.
    ///
    /// THE THIRD DUNGEON FORM, and it is defined by what it borrows from each
    /// of the other two. From the TOWER it takes the shape: a world object
    /// rather than a portal, floors as pocket maps, a stair at the far end of
    /// each one. From the GATE DUNGEONS it takes the ending: a finite run with
    /// a bottom floor that finishes it. What it takes from neither is the
    /// tower's per-floor re-roll — a hell hole is a hell hole all the way
    /// down, which is the entire point of the form.
    ///
    /// So the def is small on purpose. Floor count, which dungeon kind backs
    /// the floors, how rank ramps with depth, and one worker for whatever
    /// carries between floors. Everything else — layout, population, loot,
    /// room events, treasure — already keys off DungeonKindDef and rank, and
    /// works here untouched.
    /// </summary>
    public class MultiFloorDungeonDef : Def
    {
        /// <summary>How deep it goes. 3 to 10 in the shipped set.</summary>
        public int floorCount = 5;

        /// <summary>
        /// The dungeon kind backing every floor. ONE kind is what makes the
        /// theme strict: TowerLadder re-rolls a kind per floor and that is
        /// precisely why the tower reads as varied and these must not.
        /// </summary>
        public DungeonKindDef kindDef;

        /// <summary>
        /// Optional per-floor override, indexed by floor (clamped at the end).
        /// Left empty, every floor uses <see cref="kindDef"/>.
        ///
        /// This exists for descents that change character without becoming the
        /// tower — an outer crypt giving way to an inner sanctum. It is a
        /// SEQUENCE, authored floor by floor, not a roll, so the dungeon is
        /// still the same place every time you enter it.
        /// </summary>
        public List<DungeonKindDef> floorKinds = new List<DungeonKindDef>();

        /// <summary>Rank on the first floor and on the last; floors between interpolate.</summary>
        public MobRankTier minRank = MobRankTier.F;
        public MobRankTier maxRank = MobRankTier.A;

        /// <summary>
        /// False for the one that goes up. Purely presentational — the stair,
        /// the labels and the letters read "down" or "up" from this — but a
        /// spire that talked about descending would undo its own premise.
        /// </summary>
        public bool descends = true;

        /// <summary>Map edge length by floor. Flat unless a dungeon wants to open out as it deepens.</summary>
        public SimpleCurve mapSizeByFloor = new SimpleCurve
        {
            new CurvePoint(1f, 160f),
            new CurvePoint(10f, 190f),
        };

        /// <summary>
        /// The thing that carries between floors — this dungeon's spine.
        ///
        /// Without one, floor 3 of a crypt is floor 2 of a crypt again and the
        /// extra floors are padding. Optional all the same: a dungeon with no
        /// spine is still a legal themed descent, just a plainer one.
        /// </summary>
        public Type spineWorkerClass;

        /// <summary>World-map icon. Falls back to the gate icons if unset.</summary>
        public string worldIconPath;

        /// <summary>Relative frequency when the world places one of these.</summary>
        public float selectionWeight = 1f;

        /// <summary>Colony rank needed before this may appear at all.</summary>
        public MobRankTier minColonyRank = MobRankTier.F;

        private MultiFloorSpineWorker workerInt;

        /// <summary>Cached and stateless. Per-run state lives on the world object, which is scribed.</summary>
        public MultiFloorSpineWorker Spine
        {
            get
            {
                if (spineWorkerClass == null)
                    return null;
                return workerInt ?? (workerInt = (MultiFloorSpineWorker)Activator.CreateInstance(spineWorkerClass));
            }
        }

        /// <summary>Which dungeon backs this floor. 1-based, like every floor number the player sees.</summary>
        public DungeonKindDef KindForFloor(int floor)
        {
            if (floorKinds != null && floorKinds.Count > 0)
                return floorKinds[Mathf.Clamp(floor - 1, 0, floorKinds.Count - 1)];
            return kindDef;
        }

        /// <summary>
        /// Continuous rank for a floor, so the ramp is smooth rather than
        /// stepping in whole tiers. A one-floor dungeon sits at minRank.
        /// </summary>
        public float RankIndexForFloor(int floor)
        {
            if (floorCount <= 1)
                return (int)minRank;
            float t = Mathf.Clamp01((floor - 1) / (float)(floorCount - 1));
            return Mathf.Lerp((int)minRank, (int)maxRank, t);
        }

        public MobRankTier RankForFloor(int floor) =>
            (MobRankTier)Mathf.Clamp(Mathf.FloorToInt(RankIndexForFloor(floor)), 0, 9);

        public int MapSizeForFloor(int floor) =>
            Mathf.RoundToInt(mapSizeByFloor.Evaluate(floor));

        public bool IsLastFloor(int floor) => floor >= floorCount;

        // ── The bottom floor ────────────────────────────────────────────────

        /// <summary>
        /// The boss of the last floor, first available wins (defNames, so a
        /// missing DLC or bestiary drops an entry instead of erroring). Never
        /// fielded on any floor above: the floors before draw lieutenants
        /// from the kind's own bosses list, minus these.
        ///
        /// Exempt from the rank band on purpose. The crown of a descent is
        /// allowed to sit above the bottom floor (the Kobold Queen is S at the
        /// foot of a B undercity) because it is the one fight the whole run
        /// has been walking toward.
        /// </summary>
        public List<string> finalBosses = new List<string>();

        [Unsaved] private List<PawnKindDef> finalBossKinds;

        /// <summary>The finalBosses that exist in this game, in authored order.</summary>
        public List<PawnKindDef> FinalBossKinds
        {
            get
            {
                if (finalBossKinds == null)
                {
                    finalBossKinds = new List<PawnKindDef>();
                    if (finalBosses != null)
                    {
                        for (int i = 0; i < finalBosses.Count; i++)
                        {
                            PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(finalBosses[i]);
                            if (k != null)
                                finalBossKinds.Add(k);
                        }
                    }
                }
                return finalBossKinds;
            }
        }

        /// <summary>The bottom floor's boss, or null to let the ordinary pick decide.</summary>
        public PawnKindDef FinalBoss => FinalBossKinds.Count > 0 ? FinalBossKinds[0] : null;

        /// <summary>
        /// Whether this descent exists as authored on this install: every
        /// floor's dungeon resolves its bestiary, and a declared final boss
        /// resolves. Without it (Isekai Creatures or a DLC missing) the survey
        /// used to be posted anyway and the player got a "Slime Cistern" with
        /// no slimes, a generic boss at the bottom and its spine doing nothing.
        /// </summary>
        public bool Playable
        {
            get
            {
                if (kindDef != null && !kindDef.AvailableAsAuthored)
                    return false;
                if (floorKinds != null)
                {
                    for (int i = 0; i < floorKinds.Count; i++)
                    {
                        if (floorKinds[i] != null && !floorKinds[i].AvailableAsAuthored)
                            return false;
                    }
                }
                return finalBosses == null || finalBosses.Count == 0 || FinalBoss != null;
            }
        }

        // ── Structure art ───────────────────────────────────────────────────

        /// <summary>
        /// Texture folder holding this dungeon's own way in and way on:
        /// Exit, Onward and Onward_Sealed. Rendered by
        /// Tools/gen_multifloor_stairs.py. Unset or missing art falls back to
        /// the building def's own texture, so a new dungeon works before its
        /// art does.
        /// </summary>
        public string structureArt;

        [Unsaved] private Dictionary<string, Graphic> structureGraphics;

        /// <summary>One of this dungeon's structure textures, or null when it has none.</summary>
        public Graphic StructureGraphic(string name, Vector2 drawSize)
        {
            if (structureArt.NullOrEmpty())
                return null;
            if (structureGraphics == null)
                structureGraphics = new Dictionary<string, Graphic>();
            string path = structureArt + "/" + name;
            if (!structureGraphics.TryGetValue(path, out Graphic g))
            {
                g = ContentFinder<Texture2D>.Get(path, reportFailure: false) != null
                    ? GraphicDatabase.Get<Graphic_Single>(path, ShaderDatabase.Cutout, drawSize, Color.white)
                    : null;
                structureGraphics[path] = g;
            }
            return g;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (floorCount < 1)
                yield return "floorCount must be at least 1";
            if (kindDef == null && (floorKinds == null || floorKinds.Count == 0))
                yield return "needs either a kindDef or a floorKinds list";
            if (minRank > maxRank)
                yield return "minRank is above maxRank";
            if (spineWorkerClass != null && !typeof(MultiFloorSpineWorker).IsAssignableFrom(spineWorkerClass))
                yield return $"spineWorkerClass {spineWorkerClass} does not extend MultiFloorSpineWorker";
            // A kind meant for a gate carries a break timer it will never use
            // here, which is harmless — but one with no map generator cannot
            // build a floor at all, and that failure is silent until entry.
            for (int i = 1; i <= floorCount; i++)
            {
                if (KindForFloor(i)?.mapGenerator == null)
                {
                    yield return $"floor {i} resolves to a dungeon kind with no map generator";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// What carries between floors.
    ///
    /// Every hook is optional and every one is wrapped by the caller, because a
    /// spine is flavour on top of a working dungeon: a spine that throws should
    /// cost its own effect, never the floor. Same contract the room event
    /// workers run under, for the same reason.
    /// </summary>
    public abstract class MultiFloorSpineWorker
    {
        /// <summary>
        /// Called as a floor finishes generating, before the party arrives.
        /// The place to add population, change conditions, or read what the
        /// run has accumulated so far.
        /// </summary>
        public virtual void OnFloorGenerated(MultiFloorContext ctx) { }

        /// <summary>Called when the party actually sets foot on the floor.</summary>
        public virtual void OnFloorEntered(MultiFloorContext ctx) { }

        /// <summary>
        /// Called when the party leaves a floor by the stair, BEFORE the next
        /// floor is built — so anything this records is available to the next
        /// floor's OnFloorGenerated.
        /// </summary>
        public virtual void OnFloorLeft(MultiFloorContext ctx) { }

        /// <summary>Called once when the bottom (or top) floor is cleared and the run ends.</summary>
        public virtual void OnRunCompleted(MultiFloorContext ctx) { }

        /// <summary>
        /// Called the moment a floor's objective is met, before its rewards
        /// are placed. Any floor, including the last.
        /// </summary>
        public virtual void OnFloorCleared(MultiFloorContext ctx) { }

        /// <summary>
        /// Whether this floor's clear loot and chests are held back. The XP and
        /// the mood are still paid; only the things on the floor are not. A
        /// spine that withholds is expected to pay it back some other way
        /// (the Dragon Nest's hoard does, on the bottom floor).
        /// </summary>
        public virtual bool WithholdsFloorLoot(MultiFloorContext ctx) => false;

        /// <summary>
        /// Every map-state poll (once a second) while the floor is live and
        /// not yet cleared. For mechanics that happen DURING the fight rather
        /// than at a floor boundary - slimes splitting as they die.
        /// </summary>
        public virtual void OnFloorTick(MultiFloorContext ctx, DungeonMapComponent comp) { }

        /// <summary>
        /// Whether the autopilot may take the party deeper unattended.
        ///
        /// Some spines can kill an idle party outright — a heat or cold ramp
        /// does not care that nobody is watching. A spine that says no here
        /// gets the auto-clear switched off with a message rather than a wipe,
        /// and the player decides.
        /// </summary>
        public virtual bool AllowsUnattendedDescent(MultiFloorContext ctx, out string reason)
        {
            reason = null;
            return true;
        }
    }

    /// <summary>Everything a spine hook needs, so the hooks stay one-argument.</summary>
    public class MultiFloorContext
    {
        public readonly WorldObject_MultiFloorDungeon dungeon;
        public readonly MultiFloorDungeonDef def;
        public readonly int floor;
        public readonly MobRankTier rank;
        public readonly Map map;

        public MultiFloorContext(WorldObject_MultiFloorDungeon dungeon, int floor, Map map)
        {
            this.dungeon = dungeon;
            this.def = dungeon?.Def;
            this.floor = floor;
            this.map = map;
            this.rank = def?.RankForFloor(floor) ?? MobRankTier.F;
        }

        /// <summary>Depth through the dungeon, 0 on the first floor and 1 on the last.</summary>
        public float DepthFrac =>
            def == null || def.floorCount <= 1 ? 1f : (floor - 1) / (float)(def.floorCount - 1);
    }
}
