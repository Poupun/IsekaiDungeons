using IsekaiDungeons;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace ExampleMod
{
    /// <summary>Cleared when every registered creature in every room is dead or downed, boss included.</summary>
    public class ObjectiveWorker_Purge : DungeonObjectiveWorker
    {
        public override bool IsComplete(DungeonMapComponent dungeon)
        {
            if (dungeon?.rooms == null || dungeon.boss == null)
                return false;
            if (!dungeon.boss.Dead && !dungeon.boss.Downed)
                return false;
            foreach (DungeonRoom room in dungeon.rooms)
            {
                foreach (Pawn p in room.creatures)
                {
                    if (p != null && !p.Dead && !p.Downed && p.Spawned)
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>On entry, a herd charges in from the room's far side two seconds later.</summary>
    public class RoomEventWorker_Stampede : DungeonRoomEventWorker
    {
        public override bool CanPlace(Map map, DungeonRoom room, DungeonKindDef kind, MobRankTier rank)
            => room.rect.Width >= 8 && room.rect.Height >= 8;

        public override void OnEntered(DungeonRoomEventContext ctx)
        {
            Message(ctx, "ExampleMod.Event.Stampede.Entered", MessageTypeDefOf.ThreatSmall);
            ctx.component.QueueDelayed(120, () =>
                SpawnGroup(ctx, ctx.tier, ctx.budget * 0.5f, RandomCellIn(ctx.map, ctx.room), maxCount: 6));
        }
    }
}
