using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace IsekaiDungeons
{
    /// <summary>
    /// How a dungeon dweller is made hostile — which is NOT the same answer for
    /// a slime and for a soldier.
    ///
    /// THE BUG THIS EXISTS TO FIX. The dungeon lifecycle was built on
    /// animal-race rosters and says so in its own comments ("v1 rosters are
    /// animal-race creatures"). Every dweller spawned wild (faction null) and
    /// was woken by forcing MentalStateDefOf.ManhunterPermanent on it. For
    /// animals that is exactly right and stays exactly right.
    ///
    /// For HUMANLIKES it is a disaster. ManhunterPermanent is an aggro mental
    /// state, and an aggro pawn is hostile to everything on the map — including
    /// every other aggro pawn. A room of factionless manhunter soldiers is
    /// therefore a room of pawns who are all mutually hostile, and they tear
    /// each other apart instead of defending the dungeon. That is what the
    /// Gilded Garrison, the mod's first all-humanlike roster, actually did.
    ///
    /// So dwellers are split by race:
    ///
    ///   ANIMALS    — faction null, woken with ManhunterPermanent. Unchanged.
    ///   HUMANLIKES — spawned into AncientsHostile and woken with a Lord.
    ///
    /// AncientsHostile is vanilla's own permanentEnemy hidden faction for
    /// soldiers still guarding something long after anyone remembers why, which
    /// is the Gilded Garrison's premise word for word. Being a real faction is
    /// what makes them allies of each other; being permanentEnemy is what makes
    /// them enemies of the player without touching diplomacy.
    ///
    /// The faction is passed at GENERATION rather than stamped on afterwards,
    /// so gear, ideology and relations are all built consistently the first
    /// time instead of being patched up post-spawn.
    /// </summary>
    public static class DungeonHostility
    {
        /// <summary>
        /// The faction a dweller of this kind should be generated into.
        ///
        /// Null for animals — that is the existing wild-spawn behaviour and the
        /// leash/aggro system depends on it. Non-null only for humanlikes.
        /// </summary>
        public static Faction FactionFor(PawnKindDef kind)
        {
            if (kind?.RaceProps == null || !kind.RaceProps.Humanlike)
                return null;

            // Pirates as the fallback: AncientsHostile is a hidden faction and
            // a world could in principle be generated without one, and a
            // humanlike dweller with NO faction is the broken case this class
            // exists to prevent. Better a pirate garrison than a brawl.
            return Faction.OfAncientsHostile ?? Faction.OfPirates;
        }

        /// <summary>
        /// Wakes a group: animals go manhunter, humanlikes get a single shared
        /// Lord that assaults the player. Returns how many actually woke, for
        /// the caller's "should I announce this?" check.
        ///
        /// One lord for the whole group on purpose — a garrison that responds
        /// as a unit reads as a garrison. Per-pawn lords would each pick their
        /// own target and look like the brawl this replaced.
        /// </summary>
        public static int Wake(List<Pawn> pawns, Map map)
        {
            if (pawns == null || pawns.Count == 0)
                return 0;

            int woken = 0;
            List<Pawn> soldiers = null;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || p.Destroyed || !p.Spawned || p.mindState == null)
                    continue;

                if (p.RaceProps != null && p.RaceProps.Humanlike)
                {
                    // Already fighting under a lord = already awake. Handing a
                    // lorded pawn to MakeNewLord again does not transfer it
                    // cleanly - the old lord keeps a stale reference and its
                    // toils tick into nulls - so a second Wake on the same
                    // group must be a no-op, whoever calls it.
                    if (p.GetLord() != null)
                    {
                        woken++;
                        continue;
                    }
                    (soldiers ?? (soldiers = new List<Pawn>())).Add(p);
                    woken++;
                    continue;
                }

                if (p.mindState.mentalStateHandler.TryStartMentalState(
                        MentalStateDefOf.ManhunterPermanent, null, forced: true, forceWake: true,
                        causedByMood: false, otherPawn: null, transitionSilently: true))
                    woken++;
            }

            if (soldiers != null)
                LordUp(soldiers, map);

            return woken;
        }

        /// <summary>
        /// Puts humanlike dwellers under assault lords, ONE PER FACTION.
        ///
        /// Per faction rather than one for everyone because a lord may only
        /// hold pawns of its own faction — and a group genuinely can be mixed
        /// (a themed dungeon whose room event also rolled a raider party, or a
        /// breach wave carrying both). Pawns with no faction, the player's
        /// faction, or a faction that is not hostile are skipped: a rescued
        /// survivor caught in the same sweep must not be handed an assault lord
        /// pointed at their own rescuers.
        ///
        /// canTimeoutOrFlee is FALSE deliberately: a pocket map has no exit
        /// grid, so a fleeing garrison cannot leave and would mill on the edge
        /// forever, which looks broken and leaves the player sweeping corners.
        /// </summary>
        private static void LordUp(List<Pawn> soldiers, Map map)
        {
            if (map == null || soldiers.Count == 0)
                return;

            var byFaction = new Dictionary<Faction, List<Pawn>>();
            for (int i = 0; i < soldiers.Count; i++)
            {
                Pawn p = soldiers[i];
                Faction f = p.Faction;
                if (f == null || f == Faction.OfPlayer || !f.HostileTo(Faction.OfPlayer))
                    continue;
                if (!byFaction.TryGetValue(f, out List<Pawn> group))
                {
                    group = new List<Pawn>();
                    byFaction[f] = group;
                }
                group.Add(p);
            }

            foreach (KeyValuePair<Faction, List<Pawn>> pair in byFaction)
            {
                try
                {
                    LordMaker.MakeNewLord(pair.Key,
                        new LordJob_AssaultColony(pair.Key,
                            canKidnap: false, canTimeoutOrFlee: false, canSteal: false),
                        map, pair.Value);
                }
                catch (System.Exception e)
                {
                    // A failed lord must not abort the wake — the pawns are
                    // still a hostile faction standing next to the player, so
                    // the fight happens either way, just less coordinated.
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not raise an assault lord for " +
                                $"{pair.Value.Count} dungeon soldier(s) of {pair.Key.Name}: {e.Message}");
                }
            }
        }
    }
}
