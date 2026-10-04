using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A thing that can happen in ONE room (Carve) or ONE encounter zone (Open),
    /// instead of — or on top of — the ordinary creature encounter.
    ///
    /// This is deliberately a Def rather than another entry in DungeonRoomRole.
    /// The role enum is closed, so every new room type would have meant a code
    /// change and a save-breaking enum edit; a def means a modder adds a rival
    /// party, a shrine or a puzzle room from XML alone, and ours are just the
    /// ones that ship in the box.
    ///
    /// Selection happens once per dungeon at generation time: eligible events
    /// are weighted-rolled against the plain combat rooms, respecting rank band,
    /// layout mode, depth window and per-dungeon cap. An event that cannot place
    /// (CanPlace false) is skipped and the room stays an ordinary fight, so a
    /// failed event never leaves a hole in the dungeon.
    /// </summary>
    public class DungeonRoomEventDef : Def
    {
        /// <summary>Behaviour. Must be DungeonRoomEventWorker or a subclass.</summary>
        public Type workerClass = typeof(DungeonRoomEventWorker);

        /// <summary>Relative weight among all events eligible for a room.</summary>
        public float weight = 1f;

        /// <summary>Gate rank band this event may appear in.</summary>
        public MobRankTier minRank = MobRankTier.F;
        public MobRankTier maxRank = MobRankTier.Nation;

        /// <summary>
        /// Layout modes this event works in. Empty = all. Some events need real
        /// enclosure (a trap that seals you in is meaningless on an open plain),
        /// so they restrict themselves to Carve here rather than being disabled
        /// by a hardcoded check somewhere in the worker.
        /// </summary>
        public List<DungeonLayoutMode> layoutModes = new List<DungeonLayoutMode>();

        /// <summary>How many of this event a single dungeon may contain.</summary>
        public int maxPerDungeon = 1;

        /// <summary>
        /// Depth window along the room chain, 0 = first combat room, 1 = last.
        /// Keeps a nasty surprise out of the opening room and a trivial one out
        /// of the approach to the boss.
        /// </summary>
        public float minDepth;
        public float maxDepth = 1f;

        /// <summary>
        /// When true this event REPLACES the room's normal creature encounter
        /// (a treasure vault, a rival camp). When false the event is layered on
        /// top of the usual fight (a trap in an occupied room).
        /// </summary>
        public bool replacesEncounter = true;

        /// <summary>
        /// Fraction of the room's normal encounter budget the event may spend on
        /// its own spawns. 1 = as dangerous as the fight it replaced.
        /// </summary>
        public float budgetFactor = 1f;

        /// <summary>Optional one-shot message when the party walks in. Translation key.</summary>
        [MustTranslate]
        public string enteredMessageKey;

        private DungeonRoomEventWorker workerInt;

        /// <summary>Cached, stateless. Per-room state belongs on DungeonRoom, which is scribed.</summary>
        public DungeonRoomEventWorker Worker
        {
            get
            {
                if (workerInt == null)
                    workerInt = (DungeonRoomEventWorker)Activator.CreateInstance(workerClass ?? typeof(DungeonRoomEventWorker));
                return workerInt;
            }
        }

        public bool AllowsRank(MobRankTier rank) => rank >= minRank && rank <= maxRank;

        public bool AllowsMode(DungeonLayoutMode mode) =>
            layoutModes == null || layoutModes.Count == 0 || layoutModes.Contains(mode);

        public bool AllowsDepth(float depth) => depth >= minDepth && depth <= maxDepth;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (workerClass != null && !typeof(DungeonRoomEventWorker).IsAssignableFrom(workerClass))
                yield return $"workerClass {workerClass} does not extend DungeonRoomEventWorker";
            if (minRank > maxRank)
                yield return "minRank is above maxRank";
            if (minDepth > maxDepth)
                yield return "minDepth is above maxDepth";
            if (maxPerDungeon < 1)
                yield return "maxPerDungeon must be >= 1";
        }
    }
}
