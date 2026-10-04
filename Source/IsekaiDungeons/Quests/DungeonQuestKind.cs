namespace IsekaiDungeons
{
    /// <summary>What the guild is actually asking for. Drives narration, staging and pay.</summary>
    public enum DungeonQuestKind
    {
        /// <summary>Scouts found a gate. It is intact and counting down. Go clear it.</summary>
        Located = 0,

        /// <summary>Nobody went. It tore open and the countryside is full of what was inside.</summary>
        Broken = 1,

        /// <summary>It tore open on somebody's doorstep, and they are still alive in there.</summary>
        FactionRescue = 2,

        /// <summary>
        /// Scouts surveyed a deep place and marked the map. No gate, no
        /// countdown, no deadline - a multi-floor dungeon is somewhere that
        /// will still be there next season, so the guild reports it rather
        /// than contracting it. See MultiFloorReports for why that distinction
        /// is worth a separate kind.
        /// </summary>
        DeepReport = 3,
    }
}
