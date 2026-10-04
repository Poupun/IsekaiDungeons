using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The body that comes back down from the hundredth floor.
    ///
    /// Godhood is two things stitched together. The TRAIT (Isekai_Godhood) is
    /// the identity: the mark the halo draws over, the gold label, the thing
    /// other pawns react to. It grants no XP multiplier — a pawn who has
    /// already finished the hundredth floor has nothing left to grind toward.
    /// This HEDIFF is what godhood pays instead, and everything physical about
    /// a god routes through it:
    ///
    ///   AGELESS   — biological age is pinned to the moment of ascension. Not
    ///               slowed: pinned. The pin also means biological birthdays
    ///               never fire again, which is the mechanism vanilla uses to
    ///               hand out bad backs and dementia — so age immunity falls
    ///               out of the same fact that stops the clock.
    ///   UNDISEASED— nothing pathological can keep hold of the body. The def's
    ///               makeImmuneTo list blocks vanilla's contagion pipeline the
    ///               way penoxycyline does (the disease incident will not even
    ///               target them); the comp's strip pass below catches
    ///               everything else, including modded diseases this mod has
    ///               never heard of, via predicate rather than name list.
    ///   DEATHLESS — the def sets preventsDeath, the same flag Anomaly's rapid
    ///               regeneration uses, and Pawn_HealthTracker.ShouldBeDead
    ///               checks it before ANY cause of death: lethal damage totals,
    ///               blood loss, failed organs, zero consciousness. A god at
    ///               the bottom of any of those lies in a coma and heals.
    ///               THE BRAIN IS THE EXCEPTION, and it is ours to enforce —
    ///               preventsDeath by itself would shrug off decapitation too.
    ///               The comp watches for a missing brain after every hit and
    ///               kills the pawn through the ordinary pipeline when it
    ///               finds one, so a destroyed brain still means a real death,
    ///               with the real cause attributed.
    ///   WHITE HAIR— story.HairColor, re-asserted every upkeep so a dye or a
    ///               hair gene cannot win it back. Restored faithfully if
    ///               godhood is ever removed. SKIN IS NEVER TOUCHED: every
    ///               skin tone stays the pawn's own. (It used to go marble;
    ///               that was dropped for inclusivity, and old gods get their
    ///               own skin back on the next upkeep.)
    ///
    /// Direct Pawn.Kill calls — execution, ritual sacrifice — bypass
    /// preventsDeath by design, and that is left alone deliberately: those are
    /// the player's own hand, not the world's. What cannot kill a god is
    /// everything that arrives as damage, disease or failure.
    ///
    /// The trait remains the single source of truth. GodhoodBodyUtility's
    /// reconciler keeps trait and hediff paired in both directions, which is
    /// what makes this work on a save where someone ascended before this
    /// system existed — and what makes removing the trait (however a player
    /// manages that) unwind the body cleanly, hair included.
    /// </summary>
    public class HediffCompProperties_GodhoodBody : HediffCompProperties
    {
        public HediffCompProperties_GodhoodBody()
        {
            compClass = typeof(HediffComp_GodhoodBody);
        }
    }

    public class HediffComp_GodhoodBody : HediffComp
    {
        /// <summary>
        /// AgeBiologicalTicks at the moment of ascension; the value the body is
        /// pinned to forever after. -1 = not yet captured (pre-save-format
        /// hediffs and the moment before PostAdd runs).
        /// </summary>
        private long frozenBioTicks = -1;

        /// <summary>What skinColorOverride held before the marble, so removal restores rather than resets.</summary>
        private bool hadPreviousSkinOverride;
        private Color previousSkinOverride;

        /// <summary>And the hair, captured the same way and for the same reason.</summary>
        private Color previousHairColor;

        /// <summary>
        /// Whether the originals above have been recorded yet.
        ///
        /// Capture happens on ascension REGARDLESS of the appearance setting,
        /// which is what lets the setting be toggled freely afterwards: turn it
        /// on later and there is still a real "before" to go back to. Without
        /// this flag, a pawn who ascended while the option was off would have
        /// nothing recorded, and turning it on then off again would restore
        /// them to default(Color) - black hair on white skin.
        /// </summary>
        private bool appearanceCaptured;

        /// <summary>Upkeep cadence accumulator. Deliberately not scribed — a load simply re-runs upkeep within 4 in-game seconds.</summary>
        private int ticksSinceUpkeep;

        private const int UpkeepInterval = 250;

        public override void CompPostPostAdd(DamageInfo? dinfo)
        {
            base.CompPostPostAdd(dinfo);
            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead)
                return;

            frozenBioTicks = pawn.ageTracker.AgeBiologicalTicks;

            // The authored-NPC carve-out lives in SyncAppearance.
            CaptureAppearance(pawn);
            SyncAppearance(pawn);

            // Ascension cures what the mortal body was already carrying: the
            // dementia, the artery blockage, the gut worms. Scars and missing
            // parts stay — those are history, not pathology, and healing them
            // is the healer serum's job, not godhood's.
            GodhoodBodyUtility.StripDiseases(pawn);

            if (Current.ProgramState == ProgramState.Playing
                && PawnUtility.ShouldSendNotificationAbout(pawn))
            {
                Messages.Message(
                    "IsekaiDungeons.Godhood.BodyAscended".Translate(pawn.LabelShortCap.Named("PAWN")),
                    pawn, MessageTypeDefOf.PositiveEvent);
            }
        }

        public override void CompPostTickInterval(ref float severityAdjustment, int delta)
        {
            base.CompPostTickInterval(ref severityAdjustment, delta);

            ticksSinceUpkeep += delta;
            if (ticksSinceUpkeep < UpkeepInterval)
                return;
            ticksSinceUpkeep = 0;

            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead)
                return;

            // ── The age pin ─────────────────────────────────────────────────
            // Set back, not slowed. BiologicalTicksPerTick has no hediff hook
            // (only genes feed it), so the tracker is allowed to drift up to
            // one upkeep interval — about four in-game seconds of aging — and
            // is then snapped to the ascension instant. A birthday can never
            // fire, because a birthday needs a whole year of drift.
            //
            // Drift DOWNWARD is adopted instead of corrected: something that
            // deliberately de-ages the pawn (a mod's rejuvenation serum) is a
            // gift, not an error, and the pin moves down to keep it.
            //
            // A CHILD GROWS UP FIRST. Pinning at the ascension instant froze a
            // Biotech child who won the summit as a child forever. Until the
            // pawn is an adult the pin simply follows their real age, and it
            // takes hold at adulthood.
            if (frozenBioTicks < 0 || !pawn.ageTracker.Adult)
                frozenBioTicks = pawn.ageTracker.AgeBiologicalTicks;
            else if (pawn.ageTracker.AgeBiologicalTicks > frozenBioTicks)
                pawn.ageTracker.AgeBiologicalTicks = frozenBioTicks;
            else if (pawn.ageTracker.AgeBiologicalTicks < frozenBioTicks)
                frozenBioTicks = pawn.ageTracker.AgeBiologicalTicks;

            // ── The brain exception (backstop) ──────────────────────────────
            // The event-driven check in Notify_PawnPostApplyDamage catches the
            // ordinary case the moment it happens; this catches a brain that
            // vanished by any path that is not damage (another mod's surgery,
            // dev tools).
            if (CheckBrainGone(null))
                return;

            // ── Diseases that slipped past makeImmuneTo ─────────────────────
            // Modded diseases, and anything vanilla applies without consulting
            // the immunity pipeline. A hediff that exists for at most four
            // seconds before being erased has no time to matter.
            GodhoodBodyUtility.StripDiseases(pawn);

            // ── The white hair holds ────────────────────────────────────────
            // Also what applies the setting to living gods when it is toggled.
            SyncAppearance(pawn);
        }

        /// <summary>
        /// Records what the pawn looked like before godhood touched anything.
        /// Runs once, on ascension, whatever the setting says - see the flag's
        /// comment for why it cannot be conditional.
        /// </summary>
        private void CaptureAppearance(Pawn pawn)
        {
            if (appearanceCaptured || pawn?.story == null)
                return;
            hadPreviousSkinOverride = pawn.story.skinColorOverride.HasValue;
            previousSkinOverride = pawn.story.skinColorOverride ?? default;
            previousHairColor = pawn.story.HairColor;
            appearanceCaptured = true;
        }

        /// <summary>
        /// Brings the pawn's colours in line with the setting, in whichever
        /// direction is needed. Called on ascension and on every upkeep, so the
        /// mod option takes effect on living colonists rather than only on the
        /// next one to ascend.
        ///
        /// AN AUTHORED NPC IS NEVER TOUCHED. The forge lock is the mod-wide
        /// statement that a hand-built character's appearance is finished and
        /// no automatic system may rewrite it, and this is exactly such a
        /// system. It matters concretely for the tower's warden: her body art
        /// is multiplied by skin colour and she is set to pure white so the
        /// sprite renders as drawn, and marble would quietly dim the whole
        /// character.
        /// </summary>
        private void SyncAppearance(Pawn pawn)
        {
            if (pawn?.story == null)
                return;

            // The forge lock guards AUTHORED NPCs (the warden). It is not a
            // player's opt-out: the forge window ticks "Lock as authored" by
            // default, so any colonist the player ever opened in it came back
            // locked, and godhood's hair silently never arrived on them.
            bool authored = pawn.Faction != Faction.OfPlayer
                && IsekaiLeveling.CharacterForge.IsekaiCharacterForge.IsLocked(pawn);
            if (authored)
                return;

            bool changed = false;

            // Skin is no longer touched, so every skin tone stays the pawn's
            // own. Gods who ascended while the marble shipped get their skin
            // back here, but only if the marble is still ours to take.
            if (appearanceCaptured && pawn.story.skinColorOverride == GodhoodBodyUtility.MarbleSkin)
            {
                pawn.story.skinColorOverride =
                    hadPreviousSkinOverride ? previousSkinOverride : (Color?)null;
                changed = true;
            }

            bool want = IsekaiDungeonsMod.Settings?.godhoodChangesAppearance ?? true;
            if (want)
            {
                if (pawn.story.HairColor != GodhoodBodyUtility.GodHair)
                {
                    pawn.story.HairColor = GodhoodBodyUtility.GodHair;
                    changed = true;
                }
            }
            else if (appearanceCaptured && pawn.story.HairColor == GodhoodBodyUtility.GodHair)
            {
                // Only take back what is still ours; a later dye owns the colour.
                pawn.story.HairColor = previousHairColor;
                changed = true;
            }

            if (changed)
            {
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
            }
        }

        public override void Notify_PawnPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.Notify_PawnPostApplyDamage(dinfo, totalDamageDealt);
            CheckBrainGone(dinfo);
        }

        /// <summary>
        /// A god with no brain dies, now, with the true cause attached.
        ///
        /// Pawn.Kill rather than removing the hediff first: Kill does not
        /// consult preventsDeath (verified against the decompiled source), so
        /// the flag needs no unwinding — and removing a hediff from inside the
        /// health tracker's own notify loop would mutate the list being
        /// iterated. The hediff rides to the corpse, which is also correct:
        /// resurrection restores the brain, and a resurrected god is still a
        /// god.
        /// </summary>
        private bool CheckBrainGone(DamageInfo? dinfo)
        {
            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead || pawn.health.isBeingKilled)
                return false;
            if (pawn.health.hediffSet.GetBrain() != null)
                return false;

            pawn.Kill(dinfo, parent);
            return true;
        }

        public override void CompPostPostRemoved()
        {
            base.CompPostPostRemoved();
            Pawn pawn = Pawn;
            if (pawn?.story == null)
                return;

            // Only take the marble off if it is still ours to take — if some
            // other system has painted over it since, that system owns the skin
            // now and clobbering it would trade one bug for another.
            bool changed = false;
            if (pawn.story.skinColorOverride == GodhoodBodyUtility.MarbleSkin)
            {
                pawn.story.skinColorOverride =
                    hadPreviousSkinOverride ? previousSkinOverride : (Color?)null;
                changed = true;
            }
            if (appearanceCaptured && pawn.story.HairColor == GodhoodBodyUtility.GodHair)
            {
                pawn.story.HairColor = previousHairColor;
                changed = true;
            }
            if (changed)
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
        }

        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Values.Look(ref frozenBioTicks, "frozenBioTicks", -1L);
            Scribe_Values.Look(ref hadPreviousSkinOverride, "hadPreviousSkinOverride", false);
            Scribe_Values.Look(ref previousSkinOverride, "previousSkinOverride");
            Scribe_Values.Look(ref previousHairColor, "previousHairColor");
            Scribe_Values.Look(ref appearanceCaptured, "appearanceCaptured", false);
        }
    }

    /// <summary>
    /// The pairing rules between the Godhood trait and the ascended-body
    /// hediff, plus the shared disease predicate.
    /// </summary>
    public static class GodhoodBodyUtility
    {
        /// <summary>
        /// LEGACY: no longer applied. Kept so gods who ascended while the
        /// marble shipped can be recognised and given their own skin back.
        ///
        /// Marble. High and slightly cool — white enough to read as changed at
        /// a glance, warm enough not to read as a corpse.
        ///
        /// The channels are exact two-decimal values ON PURPOSE: the upkeep
        /// pass and the removal path both compare skinColorOverride against
        /// this constant with float equality, and the override survives a save
        /// through Scribe's text form. Two-decimal floats reparse to identical
        /// bits, so the comparison stays exact across a save/load; a value
        /// that did not roundtrip would repaint (and rebuild the pawn atlas)
        /// every 250 ticks forever.
        /// </summary>
        public static readonly Color? MarbleSkin = new Color(0.94f, 0.93f, 0.97f);

        /// <summary>
        /// The hair godhood grows: pure white, a shade cooler than neutral so
        /// it reads as luminous rather than bleached.
        ///
        /// Two-decimal channels for exactly the reason MarbleSkin gives - the
        /// upkeep compares against this with float equality every 250 ticks and
        /// the value has to survive Scribe's text form untouched, or the pawn
        /// repaints (and rebuilds its atlas) forever.
        ///
        /// Color, not Color?, because HairColor is non-nullable: a pawn always
        /// has one, so there is nothing to clear - only a previous value to put
        /// back.
        /// </summary>
        public static readonly Color GodHair = new Color(0.98f, 0.98f, 1.00f);

        /// <summary>
        /// Diseases vanilla marks neither chronic nor immunizable, caught by
        /// name. defName strings rather than resolved defs so absent DLC costs
        /// nothing (BloodRot and Abasia are Royalty; the rest are Core).
        /// </summary>
        private static readonly HashSet<string> ExtraDiseaseDefNames = new HashSet<string>
        {
            "FibrousMechanites",
            "SensoryMechanites",
            "HeartAttack",
            "LungRot",
            "BloodRot",
            "Abasia",
        };

        private static TraitDef traitCached;
        private static bool traitResolved;

        /// <summary>Same lazy silent-fail resolve as GodhoodHalo: the trait ships in this mod, but a broken load order should degrade, not throw.</summary>
        public static TraitDef GodhoodTrait
        {
            get
            {
                if (!traitResolved)
                {
                    traitCached = DefDatabase<TraitDef>.GetNamedSilentFail("Isekai_Godhood");
                    traitResolved = true;
                }
                return traitCached;
            }
        }

        public static bool IsGod(Pawn pawn)
        {
            TraitDef def = GodhoodTrait;
            return def != null && pawn?.story?.traits != null && pawn.story.traits.HasTrait(def);
        }

        /// <summary>
        /// Trait implies hediff. Called at the summit grant so the
        /// transformation lands in the same beat as the letter, and by the
        /// reconciler for gods who ascended before the body system existed.
        /// </summary>
        public static void EnsureBody(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.health == null)
                return;
            HediffDef def = IsekaiDungeonsDefOf.IsekaiDungeons_GodhoodBody;
            if (def == null || pawn.health.hediffSet.HasHediff(def))
                return;
            pawn.health.AddHediff(def);
        }

        /// <summary>
        /// Both directions of the pairing, on the tower's own tick. Colonists
        /// (maps, caravans, transporters) are the whole scope on purpose:
        /// godhood is only ever granted to a colonist, and a god who has left
        /// the faction keeps the hediff they already carry — this loop only
        /// decides who GAINS a body or loses one with the trait.
        /// </summary>
        public static void ReconcileTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0)
                return;

            HediffDef bodyDef = IsekaiDungeonsDefOf.IsekaiDungeons_GodhoodBody;
            if (bodyDef == null || GodhoodTrait == null)
                return;

            List<Pawn> pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn?.health == null || !pawn.RaceProps.Humanlike)
                    continue;

                bool hasTrait = IsGod(pawn);
                bool hasBody = pawn.health.hediffSet.HasHediff(bodyDef);

                if (hasTrait && !hasBody)
                    EnsureBody(pawn);
                else if (!hasTrait && hasBody)
                {
                    Hediff body = pawn.health.hediffSet.GetFirstHediffOfDef(bodyDef);
                    if (body != null)
                        pawn.health.RemoveHediff(body);   // comp restores the hair
                }
            }
        }

        /// <summary>
        /// One pass of the cure. Predicate, not a name list, so diseases from
        /// mods this code has never seen are covered the day they are
        /// installed:
        ///
        ///   chronic                 — vanilla's own flag for the age-and-decay
        ///                             family (bad back through organ decay and
        ///                             carcinoma, all of Hediffs_Local_Chronic).
        ///   HediffComp_Immunizable  — vanilla's own marker for "your immune
        ///                             system fights this": flu, plague,
        ///                             malaria, infections, parasites — and
        ///                             every modded disease built the standard
        ///                             way, which is nearly all of them.
        ///   ExtraDiseaseDefNames    — the stragglers marked neither way.
        ///
        /// Deliberate exclusions: injuries, scars, missing and added parts
        /// (history and hardware, not pathology), and Blindness — which is
        /// chronic by flag but is also the deliberate outcome of an Ideology
        /// blinding ritual, and un-blinding a blindsight ideologue against
        /// their will is not a cure, it is an argument.
        /// </summary>
        public static void StripDiseases(Pawn pawn)
        {
            if (pawn?.health?.hediffSet?.hediffs == null)
                return;

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                if (IsDisease(hediffs[i]))
                    pawn.health.RemoveHediff(hediffs[i]);
            }
        }

        private static bool IsDisease(Hediff h)
        {
            if (h?.def == null)
                return false;
            if (h.def == IsekaiDungeonsDefOf.IsekaiDungeons_GodhoodBody)
                return false;
            if (h is Hediff_Injury || h is Hediff_MissingPart || h is Hediff_AddedPart)
                return false;
            if (h.def.defName == "Blindness")
                return false;

            if (h.def.chronic)
                return true;
            if (h.def.CompProps<HediffCompProperties_Immunizable>() != null)
                return true;
            return ExtraDiseaseDefNames.Contains(h.def.defName);
        }
    }

    /// <summary>
    /// The +50 everyone who can see them carries: "angelic".
    ///
    /// Same shape as vanilla's ThoughtWorker_Pretty — know them, be able to
    /// see, judge the observed pawn — with one deliberate omission: Pretty is
    /// nullified by disfigurement and by the Ascetic and Kind traits, and this
    /// is not. A scar does not dent divinity, and an ascetic is not above
    /// noticing it; what stands in the room is past the question of taste.
    /// </summary>
    public class ThoughtWorker_Angelic : ThoughtWorker
    {
        protected override ThoughtState CurrentSocialStateInternal(Pawn pawn, Pawn other)
        {
            if (other == null || !other.RaceProps.Humanlike)
                return false;
            if (!GodhoodBodyUtility.IsGod(other))
                return false;
            if (!RelationsUtility.PawnsKnowEachOther(pawn, other))
                return false;
            // The thought is about how they LOOK; a blind observer keeps their
            // own counsel, exactly as vanilla beauty thoughts behave.
            if (PawnUtility.IsBiologicallyOrArtificiallyBlind(pawn))
                return false;
            return true;
        }
    }
}
