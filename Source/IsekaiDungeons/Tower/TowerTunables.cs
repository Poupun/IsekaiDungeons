using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Tower tuning. Kept apart from DungeonTunables because the two features
    /// scale on different axes: a dungeon is one instance with a countdown, a
    /// tower is a hundred instances with none.
    /// </summary>
    public static class TowerTunables
    {
        private static IsekaiDungeonsSettings S => IsekaiDungeonsSettings.Current;

        /// <summary>
        /// How tall the tower is.
        ///
        /// A hundred. Everything else was already written for it — the rank
        /// curve, the archetype scheduler, the record model — so this was the
        /// only number that had to change once the climb was proven at three.
        /// The debug category has a "Go to floor..." tool precisely so nobody
        /// has to walk to floor 60 to test floor 60.
        ///
        /// NOT A SETTING. Floor 50's mirror hall, floor 100's summit, the music
        /// split, mood staging and the cell all hang off this number.
        /// </summary>
        public const int FloorCount = 100;

        /// <summary>Edge distance a raised tower keeps from the map border.</summary>
        public const int TowerEdgeMargin = 12;

        /// <summary>
        /// Game ticks of unpaused play, counted from when the game was started
        /// OR LOADED, before the tower appears.
        ///
        /// A SCHEDULE rather than a storyteller roll. The tower is a
        /// hundred-floor landmark that exists once per playthrough — it is the
        /// shape of the run, not an event inside it, so leaving it to a
        /// 0.6-weight ThreatBig lottery meant a colony could go a year without
        /// ever learning the feature existed.
        ///
        /// Ten seconds rather than the old one day, so a colony can make the
        /// climb its whole run from the landing. Counted per session rather than
        /// from TicksGame zero so that a save which adds the mod mid-playthrough
        /// gets the same short beat after loading instead of a letter on the
        /// very first frame. Unpaused time only: a player reading the scenario
        /// screen paused does not get the letter under it.
        ///
        /// The setting is a NEW field name (towerRaiseAfterSeconds, not the old
        /// towerRaiseAfterDays) so a saved "1 day" cannot be read back as one
        /// second.
        /// </summary>
        public static int RaiseAfterTicks =>
            UnityEngine.Mathf.RoundToInt(S.towerRaiseAfterSeconds * GenTicks.TicksPerRealSecond);

        /// <summary>Whether the tower may rise at all. Off never removes a tower that already stands.</summary>
        public static bool Enabled => S.towerEnabled;

        /// <summary>
        /// Colony rank the storyteller-driven fallback still checks.
        ///
        /// Zero: the timer is the real trigger now, and a rank gate on top of
        /// it would silently delay the tower on a weak colony — which is the
        /// colony that most needs somewhere to grow into.
        /// </summary>
        public const int MinColonyRankIndex = 0;

        private static SimpleCurve rankCurve;
        private static SimpleCurve sizeCurve;
        private static IsekaiDungeonsSettings curveOwner;
        private static int curveRevision = -1;

        /// <summary>Floor to continuous rank, from the settings table (see TowerLadder for the shape's reasoning).</summary>
        public static SimpleCurve RankCurve
        {
            get
            {
                RefreshCurves();
                return rankCurve;
            }
        }

        /// <summary>Map edge length by floor, from the settings table.</summary>
        public static SimpleCurve SizeCurve
        {
            get
            {
                RefreshCurves();
                return sizeCurve;
            }
        }

        private static void RefreshCurves()
        {
            IsekaiDungeonsSettings s = S;
            if (rankCurve != null && curveOwner == s && curveRevision == s.Revision)
                return;
            rankCurve = DungeonTunables.CurveFrom(SettingsRegistry.TowerRankFloors, s.towerRankByFloor);
            sizeCurve = DungeonTunables.CurveFrom(SettingsRegistry.TowerSizeFloors, s.towerMapSizeByFloor);
            curveOwner = s;
            curveRevision = s.Revision;
        }
    }
}
