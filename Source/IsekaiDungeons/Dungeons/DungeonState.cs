namespace IsekaiDungeons
{
    /// <summary>
    /// Lifecycle of one dungeon gate. Owned and scribed by Building_DungeonGate.
    ///
    ///   Active     — countdown running; dungeon enterable (map may not be
    ///                generated yet: generation is lazy on first entry).
    ///   Cleared    — boss defeated; countdown cancelled; loot window open.
    ///                Gate collapses once the linger period passed AND the
    ///                pocket map has been empty of player pawns long enough.
    ///   Breaching  — countdown expired and the dungeon is draining itself onto
    ///                the colony map, one wave a day. The gate STAYS OPEN and
    ///                the pocket map stays alive for the whole breach, so a
    ///                player who ran out of time can still storm in and race
    ///                the dungeon to its own boss. Killing the boss inside
    ///                during a breach is a normal clear and cancels the
    ///                remaining waves; otherwise the boss leaves with the last
    ///                one and has to be beaten on the colony map.
    ///   Collapsing — final teardown queued this very state pass (transient;
    ///                exists so a save mid-teardown restores into a state the
    ///                safety net recognizes and finishes).
    ///   Broken     — the breach ran to completion: everything is out, the
    ///                pocket map is gone. Terminal; the gate destroys itself
    ///                immediately after entering this state, so it should only
    ///                ever be observed in the same tick.
    /// </summary>
    public enum DungeonState : byte
    {
        Active = 0,
        Cleared = 1,
        Collapsing = 2,
        Broken = 3,
        Breaching = 4,
    }

    /// <summary>Role of one carved room inside a dungeon layout.</summary>
    public enum DungeonRoomRole : byte
    {
        Entry = 0,
        Combat = 1,
        Treasure = 2,
        Boss = 3,
    }
}
