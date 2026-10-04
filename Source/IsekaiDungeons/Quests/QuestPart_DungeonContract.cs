using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The bookkeeping half of a guild dungeon contract: it watches the site,
    /// decides when the contract is done, and above all it owns the ONE rule
    /// this feature cannot get wrong.
    ///
    /// THE RULE: FINISHING THE CONTRACT NEVER REMOVES THE SITE.
    ///
    /// Completion and teardown are decoupled in time, on purpose. The quest
    /// ends the moment the dungeon is cleared, because that is when the player
    /// earned it, but the site stays on the world map until its map is gone,
    /// however many days the player spends hauling loot out of it. This is not
    /// a nicety: the add-on ecosystem already shipped the other version once,
    /// in July, and it removed sites out from under parties and was reported as
    /// lost caravans and missing crew.
    ///
    /// PRESENCE IS MAP EXISTENCE, NEVER A PAWN COUNT. That bug was caused by
    /// asking "are there colonists on this map" with a spawned-pawn scan.
    /// Colonists riding a shuttle, an Odyssey gravship or a Vehicle Framework
    /// vehicle sit inside an IThingHolder and are invisible to those scans, so
    /// a site read as empty on the exact tick the party landed on it. RimWorld
    /// already keeps a site map loaded for precisely as long as player pawns
    /// remain on it in ANY form (Map.ShouldRemoveMapNow via
    /// MapPawns.AnyPawnBlockingMapRemoval), so Site.HasMap is the authoritative
    /// answer and the engine judges it better than we can.
    ///
    /// Note that this mod's own presence checks
    /// (Building_DungeonGate.AnyPlayerPawnInside and the evacuation loops in
    /// Collapse / Retire) are the broken kind. They are safe only because they
    /// guard POCKET maps, where the mod owns both ends of the portal and can
    /// evacuate to a known destination. A site a caravan walked onto has no
    /// portal and no defensible destination, so waiting is the only correct
    /// policy. Do not reuse those checks here.
    /// </summary>
    public class QuestPart_DungeonContract : QuestPart
    {
        public Site site;
        public MobRankTier rank = MobRankTier.F;
        public DungeonQuestKind kind = DungeonQuestKind.Located;
        public Faction rescueFaction;

        /// <summary>Latched so the outcome is decided exactly once.</summary>
        private bool resolved;

        /// <summary>Set when the contract ended but the site could not be removed yet.</summary>
        private bool siteCleanupPending;

        /// <summary>Tick the contract was posted, for the break clock.</summary>
        public int postedTick = -1;

        public bool HasPendingSiteCleanup => siteCleanupPending;
        public bool Resolved => resolved;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Values.Look(ref kind, "kind", DungeonQuestKind.Located);
            Scribe_References.Look(ref rescueFaction, "rescueFaction");
            Scribe_Values.Look(ref resolved, "resolved", defaultValue: false);
            Scribe_Values.Look(ref siteCleanupPending, "siteCleanupPending", defaultValue: false);
            Scribe_Values.Look(ref postedTick, "postedTick", -1);
        }

        public override IEnumerable<GlobalTargetInfo> QuestLookTargets
        {
            get
            {
                if (site != null)
                    yield return site;
            }
        }

        /// <summary>
        /// Driven once a second by DungeonQuestTracker. QuestPart has no tick of
        /// its own, which is why every quest family in this ecosystem is polled
        /// from a GameComponent instead of ticking itself.
        /// </summary>
        public void PolledTick()
        {
            if (siteCleanupPending)
            {
                TryDeferredCleanup();
                return;
            }
            if (resolved || site == null)
                return;

            // The site is gone. That should not happen while a contract is live
            // (DungeonContractSite refuses to be destroyed with its map until
            // the contract resolves), but it CAN: the orphan backstop fires, a
            // dev tool removes it, another mod tidies world objects. Without
            // this branch the contract silently zombifies and holds a slot
            // against MaxActiveQuests forever, which stops the guild posting.
            if (site.Destroyed || !site.Spawned)
            {
                site = null;
                Resolve(QuestEndOutcome.Fail);
                return;
            }

            if (site.HasMap)
            {
                if (IsDungeonCleared(site.Map))
                    Resolve(QuestEndOutcome.Success);
                return;
            }

            // Nobody came, and the clock ran out. The contract does not fail:
            // the gate opens on its own terms and the job becomes containment.
            // Escalation rather than expiry is what makes ignoring guild work
            // feel like a decision instead of a missed notification, and it
            // reuses the same site, so the player who skipped the first letter
            // recognises the second one.
            if (kind == DungeonQuestKind.Located && TryConvertToBreak())
                return;

            // No per-poll grace counter here, deliberately. The only two things
            // absence can trigger are the break conversion and the write-off,
            // and both are gated on MULTIPLE DAYS having passed, so a map that
            // flickers for a frame during a shuttle or gravship landing cannot
            // possibly reach either. A debounce would be dead code pretending
            // to be a safety feature.
            TryWriteOff();
        }

        /// <summary>
        /// Pays the fee the letter promised.
        ///
        /// The letter and the quest description both quote a silver figure, and
        /// until this existed the mod never paid it. An advertised reward that
        /// does not arrive is worse than no reward at all: it reads as a bug the
        /// first time and as dishonesty the second.
        ///
        /// Drop pods rather than a silent stack in a stockpile, because the
        /// arrival is the moment the payment registers as payment.
        /// </summary>
        private void PayFinderFee()
        {
            Map drop = Find.AnyPlayerHomeMap;
            if (drop == null)
                return;

            int amount = DungeonQuestTuning.FeeFor(kind, (int)rank);
            if (amount <= 0)
                return;

            try
            {
                var stacks = new List<Thing>();
                int left = amount;
                while (left > 0)
                {
                    Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                    silver.stackCount = Mathf.Min(left, ThingDefOf.Silver.stackLimit);
                    left -= silver.stackCount;
                    stacks.Add(silver);
                }

                IntVec3 cell = DropCellFinder.TradeDropSpot(drop);
                DropPodUtility.DropThingsNear(cell, drop, stacks, canRoofPunch: false, forbid: false);

                Find.LetterStack.ReceiveLetter(
                    "IsekaiDungeons.Contract.Fee.Label".Translate(),
                    "IsekaiDungeons.Contract.Fee.Text".Translate(
                        amount.ToString("N0").Named("SILVER"),
                        MobRankUtility.GetRankString(rank).Named("RANK")),
                    LetterDefOf.PositiveEvent,
                    new LookTargets(cell, drop));
            }
            catch (System.Exception e)
            {
                Log.Warning(IsekaiDungeonsMod.LogPrefix + " Could not deliver the contract fee: " + e.Message);
            }
        }

        /// <summary>
        /// The guild posted the work, so the guild remembers it was done.
        ///
        /// GuildFactionAddon already pays goodwill for base-mod hunts from its
        /// own Quest.End postfix, but that patch only recognises the base mod's
        /// quest parts and cannot see this one, so the payout lives here. Soft
        /// dependency by defName: without the add-on the faction is absent and
        /// this does nothing. A hostile guild gets nothing, matching the add-on.
        /// </summary>
        private void GrantGuildGoodwill()
        {
            int amount = DungeonQuestTuning.GuildGoodwillFor((int)rank);
            if (amount <= 0)
                return;

            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(DungeonQuestTuning.GuildFactionDefName);
            if (def == null)
                return;
            Faction guild = Find.FactionManager?.FirstFactionOfDef(def);
            if (guild == null || guild.defeated || guild.HostileTo(Faction.OfPlayer))
                return;

            guild.TryAffectGoodwillWith(Faction.OfPlayer, amount,
                canSendMessage: true, canSendHostilityLetter: false, reason: null, lookTarget: site);
        }

        /// <summary>
        /// Gives up on a break nobody ever dealt with.
        ///
        /// Without this a site the player simply never visits stays unresolved
        /// forever, and because DungeonQuestTracker counts unresolved contracts
        /// against MaxActiveQuests, two of them would permanently stop the guild
        /// posting any more dungeon work. The write-off is what keeps the board
        /// turning over.
        /// </summary>
        private void TryWriteOff()
        {
            if (kind == DungeonQuestKind.Located || postedTick < 0)
                return;
            if (Find.TickManager.TicksGame - postedTick
                < (int)(DungeonQuestTuning.BrokenSiteDays * GenDate.TicksPerDay))
                return;

            Resolve(QuestEndOutcome.Fail);
        }

        /// <summary>
        /// Turns an untouched, expired contract into a break contract in place.
        ///
        /// Only ever runs while the site has NO map. If a map exists the player
        /// is standing on it, or has been and left things there, and rewriting
        /// the site under them would change what they are fighting mid-fight.
        /// </summary>
        private bool TryConvertToBreak()
        {
            if (postedTick < 0)
            {
                postedTick = Find.TickManager.TicksGame;   // legacy save; start the clock now
                return false;
            }
            if (Find.TickManager.TicksGame - postedTick
                < (int)(DungeonQuestTuning.DaysBeforeBreak * GenDate.TicksPerDay))
                return false;

            WorldObjectComp_DungeonContract comp = site.GetComponent<WorldObjectComp_DungeonContract>();
            if (comp == null)
                return false;

            kind = DungeonQuestKind.Broken;
            comp.questKind = DungeonQuestKind.Broken;
            postedTick = Find.TickManager.TicksGame;   // restart the clock for the write-off

            if (quest != null)
            {
                quest.name = DungeonContractNarrative.Title(kind, rank, comp.kindDef);
                // The Quests tab otherwise kept the Located text ("intact") on
                // a broken gate, quoting the old, smaller fee.
                quest.description = DungeonContractNarrative.Description(
                    kind, rank, comp.kindDef, site, Find.AnyPlayerHomeMap);
            }
            DungeonContractNarrative.SendBreakConversionLetter(rank, comp.kindDef, site, quest);
            return true;
        }

        /// <summary>
        /// The contract is satisfied when the dungeon behind the gate is beaten.
        ///
        /// Read off the gate's own state rather than tracked separately, so the
        /// mod has exactly one definition of "cleared" and a contract can never
        /// disagree with the letter the dungeon already sent. The gate lingers
        /// about two in-game hours after clearing before it collapses, and this
        /// polls once a second, so the window cannot be missed.
        /// </summary>
        private static bool IsDungeonCleared(Map map)
        {
            if (map == null)
                return false;
            List<Building_DungeonGate> gates = DungeonTracker.GatesOn(map);
            if (gates == null || gates.Count == 0)
                return false;
            for (int i = 0; i < gates.Count; i++)
            {
                Building_DungeonGate g = gates[i];
                if (g != null && g.state == DungeonState.Cleared)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Called when a dungeon settles somewhere OTHER than by its gate
        /// reaching the Cleared state.
        ///
        /// A breach that runs its full course never reaches Cleared: the last
        /// wave walks the boss out and the gate ends in Broken and destroys
        /// itself. The player who then kills that boss has beaten the dungeon
        /// by every measure the mod uses to pay loot and XP, so a contract that
        /// only watched for Cleared would have failed them for winning the
        /// harder way. Hediff_GateBound.Settle calls this at the moment the
        /// escaped boss dies.
        /// </summary>
        public static void NotifyDungeonSettled(Map map)
        {
            if (map == null)
                return;
            List<Quest> quests = Find.QuestManager?.QuestsListForReading;
            if (quests == null)
                return;

            for (int i = 0; i < quests.Count; i++)
            {
                List<QuestPart> parts = quests[i]?.PartsListForReading;
                if (parts == null)
                    continue;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is QuestPart_DungeonContract p
                        && !p.Resolved
                        && p.site != null
                        && p.site.HasMap
                        && p.site.Map == map)
                    {
                        p.Resolve(QuestEndOutcome.Success);
                        return;
                    }
                }
            }
        }

        /// <summary>Ends the contract and STARTS, but does not necessarily finish, site teardown.</summary>
        public void Resolve(QuestEndOutcome outcome)
        {
            if (resolved)
                return;
            resolved = true;

            // Tell the SITE the contract is over. Until this flips,
            // DungeonContractSite refuses to let the engine destroy the site
            // alongside its map, which is what lets a player walk away from an
            // unfinished gate and come back to it.
            WorldObjectComp_DungeonContract rc = site?.GetComponent<WorldObjectComp_DungeonContract>();
            if (rc != null)
                rc.contractResolved = true;

            if (kind == DungeonQuestKind.FactionRescue && rescueFaction != null && !rescueFaction.IsPlayer)
            {
                int shift = outcome == QuestEndOutcome.Success
                    ? DungeonQuestTuning.RescueGoodwill
                    : DungeonQuestTuning.RescueFailureGoodwill;
                rescueFaction.TryAffectGoodwillWith(Faction.OfPlayer, shift,
                    canSendMessage: true, canSendHostilityLetter: true, reason: null, lookTarget: site);
            }

            if (outcome == QuestEndOutcome.Success)
            {
                PayFinderFee();
                GrantGuildGoodwill();
            }

            // End can throw on a malformed quest, and if it did the site would
            // never be cleaned up. Cleanup belongs in a finally for the same
            // reason teardown is deferred at all: losing track of a site is the
            // failure mode that strands people.
            try
            {
                if (quest != null && !quest.Historical)
                    quest.End(outcome, sendLetter: true, playSound: true);
            }
            catch (System.Exception e)
            {
                Log.Warning(IsekaiDungeonsMod.LogPrefix + " Contract quest failed to end cleanly: " + e.Message);
            }
            finally
            {
                CleanupWorldSite();
            }
        }

        /// <summary>
        /// Removes the site if that is safe RIGHT NOW, otherwise arms the
        /// deferred path. The gate belongs INSIDE this method rather than at the
        /// call sites, because quest signals fire from places nobody enumerates.
        /// </summary>
        private void CleanupWorldSite()
        {
            if (site == null)
                return;
            if (IsSiteSafeToRemove())
            {
                RemoveSite();
                return;
            }
            siteCleanupPending = true;
        }

        private void TryDeferredCleanup()
        {
            if (site == null)
            {
                siteCleanupPending = false;
                return;
            }
            if (IsSiteSafeToRemove())
            {
                RemoveSite();
                siteCleanupPending = false;
            }
        }

        /// <summary>Safe only when the site map is GONE. A pawn count is the wrong question.</summary>
        private bool IsSiteSafeToRemove() => site == null || !site.HasMap;

        /// <summary>
        /// Remove, never Destroy, and ONLY behind the HasMap guard.
        ///
        /// A correction worth stating plainly, because the comment that used to
        /// live here said the opposite and would have misled the next edit:
        /// Find.WorldObjects.Remove does NOT spare a loaded map.
        /// WorldObjectsHolder.Remove calls WorldObject.PostRemove, and
        /// MapParent overrides PostRemove precisely to run
        /// Game.DeinitAndRemoveMap. So Remove on a site that still has a map
        /// deletes that map and everything standing on it, exactly like Destroy.
        ///
        /// Which means the guard is not a nicety layered on top of a safe call.
        /// IsSiteSafeToRemove IS the safety; Remove versus Destroy is only the
        /// difference between a graceful despawn and additionally tearing down
        /// attached objects and firing the caravan-lost path. Never reach this
        /// method without the guard, and never move the guard to a call site.
        /// </summary>
        private void RemoveSite()
        {
            if (site == null)
                return;
            if (site.Spawned)
                Find.WorldObjects.Remove(site);
            site = null;
        }

        public override void Cleanup()
        {
            base.Cleanup();
            // Arms teardown; it deliberately does not force it. Cleanup runs
            // when the quest object is discarded, which can happen while a party
            // is still on the map, and the tracker keeps polling pending
            // cleanups precisely so teardown can outlive the quest.
            CleanupWorldSite();
        }
    }
}
