using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A sealed dungeon door standing between a corridor and a room.
    ///
    /// WHY DOORS EXIST AT ALL: RimWorld's fog-of-war unfogs by flood fill, and
    /// FloodFillerFog.PassCheck stops at any edifice whose ThingDef.MakeFog is
    /// true — which is Fillage == Full, i.e. fillPercent > 0.99. Doors are
    /// fillPercent 1, so a closed door is a hard fog boundary exactly like a
    /// wall. Without doors, stepping into a corridor flood-unfogs every room
    /// the corridor touches and the whole dungeon is revealed at a glance;
    /// with them, each room stays black until someone actually opens it. The
    /// entire exploration feel of the mod rides on this one vanilla behavior.
    ///
    /// TWO DELIBERATE DEVIATIONS FROM A VANILLA DOOR:
    ///
    /// 1. Delvers can always open them. These doors are spawned FACTIONLESS,
    ///    and vanilla Building_Door.PawnCanOpen falls through to
    ///    RaceProps.canOpenFactionlessDoors for factionless doors — a flag
    ///    almost nothing has (in core only Anomaly's fleshbeasts). That would
    ///    lock the player out of their own dungeon, so player pawns and
    ///    humanlikes are allowed through explicitly. Dungeon creatures are NOT,
    ///    which is what keeps each room's monsters politely at home until their
    ///    door is opened — the same pacing the room-aggro system assumes.
    ///
    /// 2. Once opened, they stay open forever (holdOpen latch). A vanilla door
    ///    auto-closes after ~110 ticks, and since the creatures chasing you
    ///    cannot open it, a fleeing colonist would leave a closed door behind
    ///    and the entire room's worth of monsters would be stuck on the far
    ///    side — free kills through a chokepoint they can never cross. Latching
    ///    the door open on first use removes that exploit: once you crack a
    ///    room, what is inside can follow you anywhere.
    /// </summary>
    public class Building_DungeonDoor : Building_Door
    {
        /// <summary>
        /// Factionless by design (see class docs), so the vanilla faction check
        /// must not run — it would ask GenAI.MachinesLike(null, pawn).
        /// </summary>
        protected override bool CheckFaction => false;

        public override bool PawnCanOpen(Pawn p)
        {
            if (p == null || !p.CanOpenDoors)
                return false;

            // The delving party — and any humanlike, so a wandering visitor or
            // another mod's humanlike raider is never trapped behind a door.
            if (p.Faction == Faction.OfPlayer || p.RaceProps.Humanlike)
                return true;

            // Everything else follows the vanilla factionless-door rule, which
            // is false for essentially every creature: dungeon monsters wait
            // in their rooms.
            return base.PawnCanOpen(p);
        }

        protected override void DoorOpen(int ticksToClose = 110)
        {
            base.DoorOpen(ticksToClose);
            // Latch open permanently the first time this door is used. Vanilla
            // DoorTryClose() early-returns while holdOpenInt is set, so this is
            // all it takes.
            holdOpenInt = true;
        }

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string line = "IsekaiDungeons.Door.Inspect".Translate();
            return baseStr.NullOrEmpty() ? line : baseStr + "\n" + line;
        }
    }
}
