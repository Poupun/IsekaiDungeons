using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsekaiDungeons
{
    /// <summary>
    /// The way out of the tower, standing where the party arrived.
    ///
    /// A floor is a pocket map with no world tile, so a caravan cannot be
    /// formed on it the ordinary way. This building is the answer: everyone on
    /// the floor leaves together and reappears as a caravan standing on the
    /// tower's tile — exactly where they would be if they had turned round at
    /// the door.
    ///
    /// LEAVING TAKES THE WHOLE PARTY, and that is deliberate rather than a
    /// simplification: the alternative is a colonist stranded forty floors up
    /// with no way home and no way for the player to reach them.
    /// </summary>
    public class Building_TowerExit : Building
    {
        private Pawn pendingLeaver;

        // No DrawAt override: this is a stair, not a portal, and it draws its
        // def's texture like any building - a stairwell cut into a stone
        // platform, sinking toward a tunnel lit in the Tower's pink. The glow
        // comp still casts that pink on every floor.

        private static int FloorOf(Map map) => (map?.Parent as TowerFloorMapParent)?.floor ?? 0;

        /// <summary>The order's wording. Virtual so a multi-floor dungeon's exit does not say "tower".</summary>
        protected virtual string LeaveLabel => "IsekaiDungeons.Tower.Leave".Translate();

        protected virtual string LeaveDesc => "IsekaiDungeons.Tower.LeaveDesc".Translate();

        private Texture2D LeaveIcon =>
            (Graphic?.MatSingle?.mainTexture as Texture2D) ?? def.uiIcon;

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = LeaveLabel,
                defaultDesc = LeaveDesc,
                // A command with no icon draws vanilla's red X. The building's
                // own picture is the clearest "go out THIS way" there is, and
                // through Graphic a multi-floor exit shows its own dungeon's art.
                icon = LeaveIcon,
                action = Leave,
            };
        }

        /// <summary>Right-click, because leaving is an order you give people.</summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
                yield return o;

            if (selPawn == null || selPawn.Faction != Faction.OfPlayer)
                yield break;

            string label = LeaveLabel;
            if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
            {
                yield return new FloatMenuOption(label + " (" + "NoPath".Translate() + ")", null);
                yield break;
            }

            yield return new FloatMenuOption(label, delegate
            {
                Job job = JobMaker.MakeJob(JobDefOf.Goto, this);
                job.playerForced = true;
                selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                pendingLeaver = selPawn;
            });
        }

        protected override void Tick()
        {
            base.Tick();
            if (pendingLeaver == null || !this.IsHashIntervalTick(15))
                return;
            if (pendingLeaver.Dead || pendingLeaver.Map != Map)
            {
                pendingLeaver = null;
                return;
            }
            if (pendingLeaver.Position.AdjacentTo8WayOrInside(this))
            {
                pendingLeaver = null;
                Leave();
            }
            else if (pendingLeaver.CurJobDef != JobDefOf.Goto)
            {
                pendingLeaver = null;
            }
        }

        private void Leave()
        {
            Map here = Map;
            if (here == null)
                return;
            // Retire with no destination: everyone rescued walks out onto the
            // planet as a caravan.
            TowerFloors.Retire(here, null);
        }

        public override string GetInspectString()
        {
            int floor = FloorOf(Map);
            return floor > 0
                ? "IsekaiDungeons.Tower.ExitDesc".Translate(floor.Named("FLOOR"))
                : base.GetInspectString();
        }
    }
}
