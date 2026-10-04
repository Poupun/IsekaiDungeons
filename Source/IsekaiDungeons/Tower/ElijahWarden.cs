using System.Collections.Generic;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.CharacterForge;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// ELIJAH, THE HUNDREDTH — the thing at the top of the tower.
    ///
    /// She climbed it. She won. Whatever ran the tower promoted her into it,
    /// and then that order died out (see the archangel weapons' "a verdict of
    /// an order that no longer exists to enforce it"). She has been holding a
    /// post whose purpose died with the people who assigned it, and she does
    /// not know what the tower is for either. The player kills her and walks
    /// out with the same Godhood she won; the horror is that she didn't.
    ///
    /// Named for the prophet taken up alive in a whirlwind — Hebrew Eliyahu,
    /// "my God is Yahweh", an oath of service worn by someone nobody came
    /// back to relieve.
    ///
    /// SHE IS ALMOST ENTIRELY DATA. The character (name, face, stats, gear,
    /// constellation) is an IsekaiCharacterDef in XML, generated through the
    /// base mod's own forge — the same path the mirror copies take, so it is
    /// already proven. This file only supplies the four things a spec cannot
    /// express:
    ///
    ///   1. the white aura (favoriteColor is not a spec field),
    ///   2. the ascendant hediff that keeps her conscious,
    ///   3. the VPE Conflagrator grant, when VPE is present,
    ///   4. the wing overlay, when its texture exists.
    ///
    /// Everything else is XML, and the def is editable without a rebuild.
    /// </summary>
    public static class ElijahWarden
    {
        /// <summary>The IsekaiCharacterDef that IS Elijah. Edit her in XML, not here.</summary>
        /// <summary>
        /// How much larger than an ordinary pawn she is drawn.
        ///
        /// Reads high because most of her canvas is WING. Measured on the art,
        /// her actual body is 0.54 of the texture width, so a multiplier of 3
        /// puts her body at roughly two and a half colonists with a wingspan
        /// filling about four and a half cells. A world boss sits at 2.5 on art
        /// that fills its own canvas, which is why she needs more than one to
        /// read as larger at all.
        ///
        /// THE one number for her size. bodyGraphicScale is not it.
        ///
        /// 3.5 since 2026-09-30 (was 3, user: "her body is still too small").
        /// Part of that report was a bug: the base mod's render-scale registry
        /// is runtime-only and she registered once, so after any load she drew
        /// at 1x. HediffComp_Ascendant now re-registers her.
        ///
        /// 3.2 since 2026-09-30 (user: "make it a bit smaller"), between the
        /// two answers above.
        ///
        /// 1.3 since 2026-10-01 (user asked for 1.3 instead of 3.2).
        /// </summary>
        internal const float RenderScale = 1.3f;

        public const string CharacterDefName = "IsekaiDungeons_Elijah";

        /// <summary>Psylink level when VPE is installed. 30 is far past vanilla's six.</summary>
        private const int PsylinkLevel = 30;

        /// <summary>Her school. Fire, from the prophet taken up in a chariot of it.</summary>
        private const string PathDefName = "VPE_Conflagrator";

        /// <summary>
        /// Builds her. Null if the character def is missing — the caller then
        /// falls back to an ordinary rolled boss rather than a bossless floor.
        /// </summary>
        public static Pawn Generate(Map map)
        {
            // THE FACTION MUST EXIST BEFORE SHE DOES. The forge's ResolveFaction
            // only LOOKS UP a faction (FirstFactionOfDef) — it never creates
            // one — and on a miss it silently falls back to a random hostile
            // faction, which is precisely the bug that gave the floor-50
            // mirrors somebody else's xenotype. A player who reached floor 100
            // without ever seeing floor 50 would have no mirror faction in
            // their world at all, so it is ensured here rather than assumed.
            MirrorForge.EnsureMirrorFaction();

            Pawn elijah = IsekaiCharacterForge.Generate(CharacterDefName, map?.Tile ?? -1, locked: true);
            if (elijah == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} No IsekaiCharacterDef '{CharacterDefName}' — the summit will use a rolled boss instead.");
                return null;
            }

            try
            {
                // ── The white aura ──────────────────────────────────────────
                // AuraSystem overrides the rank palette with story.favoriteColor
                // when the base mod's auraUseFavoriteColor setting is on (it is
                // by default), and ShouldShowAura already returns true for
                // anything hostile — so a white ColorDef is the whole feature.
                // A player who turned that setting off gets her rank colour
                // instead, which is their choice, not a failure.
                ColorDef white = DefDatabase<ColorDef>.GetNamedSilentFail("IsekaiDungeons_AngelicWhite");
                if (white != null && elijah.story != null)
                    elijah.story.favoriteColor = white;

                // ── The body that will not lie down ─────────────────────────
                HediffDef ascendant = DefDatabase<HediffDef>.GetNamedSilentFail("IsekaiDungeons_Ascendant");
                if (ascendant != null && !elijah.health.hediffSet.HasHediff(ascendant))
                    elijah.health.AddHediff(ascendant);

                // ── The godhood body, and it is the SAME one ────────────────
                //
                // Not an imitation of the rule the player is about to receive:
                // literally IsekaiDungeons_GodhoodBody, the hediff an ascended
                // colonist carries, with its preventsDeath and its brain watch.
                // She cannot die of damage, blood loss, a failed organ or
                // shock. Destroy the brain or she does not stop.
                //
                // That is the floor teaching its own reward. A player who has
                // had to take her head apart understands exactly what the
                // summit letter is handing them, without a word of tutorial.
                //
                // The reconciler that normally pairs trait and hediff only ever
                // walks COLONISTS, so it will neither add this for us nor strip
                // it from her; attaching it here is the whole of it.
                HediffDef godBody = DefDatabase<HediffDef>.GetNamedSilentFail("IsekaiDungeons_GodhoodBody");
                if (godBody != null && !elijah.health.hediffSet.HasHediff(godBody))
                    elijah.health.AddHediff(godBody);

                // ── The fire ────────────────────────────────────────────────
                // Silent no-op without VPE; the base mod owns the reflection.
                IsekaiLeveling.Compatibility.VPEPsycastRankBonus.GrantFullPath(
                    elijah, PathDefName, PsylinkLevel);

                // ── The radiant smite ───────────────────────────────────────
                EnsureArchangelSmite(elijah);

                // ── The boss health bar ────────────────────────────────────
                // The BAR only. A real RegisterWorldBoss would also impose the
                // 2.5x visual scale and the world-boss health multipliers, and
                // she is already a hand-tuned Nation character who is meant to
                // be only a little larger than a colonist.
                IsekaiLeveling.WorldBoss.WorldBossSizePatch.RegisterBossBar(elijah);

                // ── Her size ────────────────────────────────────────────────
                //
                // THIS is what makes a pawn bigger. BodyTypeDef.bodyGraphicScale
                // looks like the lever and is not one: the whole game reads it
                // in two places and both scale attachment OFFSETS, never the
                // body. Setting it did nothing at all, twice.
                //
                // RenderScale goes through the world-boss graphic patch, which
                // re-fetches the Graphic at a scaled drawSize, AND (since
                // 2026-09-30) scales the humanlike body/head meshes. The mesh
                // half is what actually resizes her: a humanlike's mesh comes
                // from HumanlikeMeshPoolUtility and ignores drawSize, so before
                // that patch she drew at colonist size whatever this said. Registered
                // without RegisterWorldBoss so she takes the size and none of
                // the health multipliers.
                IsekaiLeveling.WorldBoss.WorldBossSizePatch.RegisterRenderScale(elijah, RenderScale);

                // ── The celestial blade, when there is one to take ──────────
                // Her XML gives her a weapon that always exists; if Isekai
                // Creatures is installed she carries its Nation-rank archangel
                // sword instead. Done in code rather than XML because a
                // GearSpec has no fallback: naming a missing def there would
                // leave her empty-handed on a bare install.
                UpgradeWeapon(elijah);
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Elijah post-generation setup failed: {ex.Message}");
            }

            return elijah;
        }

        /// <summary>The archangel whose weapon she is borrowing. Absent without Isekai Creatures, and then she simply does not smite.</summary>
        private const string ArchangelDefName = "IsekaiArchangel_Common";

        /// <summary>Her blade when Isekai Creatures is present — the same Nation-rank sword the archangels carry.</summary>
        private const string CelestialBladeDefName = "IsekaiWeapon_ArchangelsWrath";

        /// <summary>
        /// Swaps her XML weapon for the archangel blade when that def exists.
        /// Silent no-op otherwise, so a bare install keeps the vanilla weapon
        /// the spec already equipped rather than losing both.
        /// </summary>
        private static void UpgradeWeapon(Pawn pawn)
        {
            ThingDef blade = DefDatabase<ThingDef>.GetNamedSilentFail(CelestialBladeDefName);
            if (blade == null || pawn.equipment == null)
                return;
            if (pawn.equipment.Primary?.def == blade)
                return;

            var weapon = ThingMaker.MakeThing(blade, blade.MadeFromStuff ? GenStuff.DefaultStuffFor(blade) : null) as ThingWithComps;
            if (weapon == null)
                return;
            weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Legendary, ArtGenerationContext.Outsider);
            pawn.equipment.DestroyAllEquipment();
            pawn.equipment.AddEquipment(weapon);
        }

        /// <summary>
        /// Gives her the archangel's radiant smite — the real one, not a copy.
        ///
        /// The AOE is `IsekaiCreatures.Combat.CompProperties_AoeStrike`, a
        /// ThingComp on the archangel's RACE def, and Elijah is a human. So
        /// rather than re-implementing it (two tuning tables to keep in step,
        /// forever) the archangel's own CompProperties INSTANCE is lifted off
        /// its def and handed to a fresh comp on her. Sharing one props object
        /// between things is exactly what vanilla already does — every
        /// archangel on the map shares that instance — so she is just one more
        /// reader of it, and any retune of the archangel retunes her too.
        ///
        /// NO REFLECTION INTO ISEKAI CREATURES. This only touches Verse's own
        /// CompProperties/ThingComp and a def lookup, so the add-on's
        /// reflection-only soft-dependency rule is kept without any
        /// AccessTools at all: no Creatures, no def, no smite, no error.
        ///
        /// IDEMPOTENT, because it has to be. A comp added at runtime does NOT
        /// survive a save: ThingWithComps.ExposeData calls InitializeComps() on
        /// LoadingVars, which rebuilds the list from the DEF and silently drops
        /// anything we attached. HediffComp_Ascendant therefore calls this on
        /// its interval, and this method's own duplicate check is what makes
        /// that safe to do forever.
        /// </summary>
        public static void EnsureArchangelSmite(Pawn pawn)
        {
            if (pawn?.AllComps == null)
                return;

            ThingDef archangel = DefDatabase<ThingDef>.GetNamedSilentFail(ArchangelDefName);
            if (archangel?.comps == null)
                return;

            CompProperties aoeProps = null;
            for (int i = 0; i < archangel.comps.Count; i++)
            {
                // By TYPE NAME rather than by index or position: the archangel
                // also carries aggro and rank comps, and their order is the
                // Creatures add-on's business to change.
                if (archangel.comps[i]?.GetType().Name == "CompProperties_AoeStrike")
                {
                    aoeProps = archangel.comps[i];
                    break;
                }
            }
            if (aoeProps?.compClass == null)
                return;

            for (int i = 0; i < pawn.AllComps.Count; i++)
            {
                if (pawn.AllComps[i]?.GetType() == aoeProps.compClass)
                    return;   // already smiting
            }

            try
            {
                var comp = (ThingComp)System.Activator.CreateInstance(aoeProps.compClass);
                comp.parent = pawn;
                pawn.AllComps.Add(comp);
                // Initialize AFTER the list add, matching InitializeComps' own
                // order, so anything the comp does on init sees a consistent
                // parent whose comp list already contains it.
                comp.Initialize(aoeProps);
            }
            catch (System.Exception ex)
            {
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Could not attach the archangel smite to {pawn.LabelShort}: {ex.Message}", 0x60D81);
            }
        }
    }

    /// <summary>
    /// The hediff that makes her the last thing standing.
    ///
    /// TWO SEPARATE PROBLEMS, TWO MECHANISMS, because vanilla splits them:
    ///
    ///   PAIN — Pawn_HealthTracker.InPainShock is skipped entirely when the
    ///          pawn's KIND sets ignoresPainShock, so that half is pure XML on
    ///          her PawnKindDef and needs nothing here.
    ///   CONSCIOUSNESS — everything else routes through CanBeAwake, which is
    ///          Consciousness >= 0.3 unless RaceProps.alwaysAwake. That flag is
    ///          on the RACE, and she is an ordinary human, so it is not
    ///          available without giving her a bespoke race — a heavy price for
    ///          one boolean, and one that would drag in backstory, body and
    ///          apparel compatibility. Hence the patch below.
    ///
    /// PSYCHIC COMA is the one that cannot be out-statted: its stage uses
    /// setMax 0.1 on Consciousness, and a setMax beats any offset a hediff of
    /// ours could add. So it is REMOVED rather than resisted. That also covers
    /// VPE's own neural-heat coma, which uses the same vanilla def.
    /// </summary>
    public class HediffCompProperties_Ascendant : HediffCompProperties
    {
        public HediffCompProperties_Ascendant()
        {
            compClass = typeof(HediffComp_Ascendant);
        }
    }

    public class HediffComp_Ascendant : HediffComp
    {
        private const int Interval = 60;
        private int ticks;

        /// <summary>
        /// Hediffs that would put her under. Removed on sight rather than
        /// resisted — see the class comment on setMax.
        /// </summary>
        private static readonly HashSet<string> Unconsciousness = new HashSet<string>
        {
            "PsychicComa",
            "Anesthetic",
            "CatatonicBreakdown",
        };

        public override void CompPostTickInterval(ref float severityAdjustment, int delta)
        {
            base.CompPostTickInterval(ref severityAdjustment, delta);
            ticks += delta;
            if (ticks < Interval)
                return;
            ticks = 0;

            Pawn pawn = Pawn;
            if (pawn == null || pawn.Dead || pawn.health?.hediffSet?.hediffs == null)
                return;

            // THE SMITE IS RE-ATTACHED FROM HERE, and this is the reason the
            // hediff owns it rather than the spawn path owning it alone. A
            // ThingComp added at runtime is destroyed by a save/load cycle
            // (ThingWithComps.ExposeData rebuilds the comp list from the def),
            // but a HEDIFF and its comps are scribed properly — so the durable
            // thing re-establishes the fragile one. EnsureArchangelSmite is
            // idempotent, so this costs one type comparison per interval once
            // she is already smiting.
            ElijahWarden.EnsureArchangelSmite(pawn);

            // THE BOSS BAR, for the same reason. WorldBossSizePatch keeps its
            // bar list in a runtime-only HashSet, and Generate registers her
            // exactly once. Save and load, and she had silently fallen off the
            // list: no bar for the rest of the fight. Re-adding an id to a
            // HashSet is idempotent, so doing it every interval costs nothing
            // and puts the bar back within a second of any load.
            IsekaiLeveling.WorldBoss.WorldBossSizePatch.RegisterBossBar(pawn);

            // HER SIZE, same registry problem. The render scale is applied when
            // the render tree fetches her graphics, and after a load that
            // happened at 1x with nothing registered, so re-registering alone
            // is not enough: the cached graphics must be rebuilt. Only when the
            // scale was actually missing, so this is not a per-second rebuild.
            if (IsekaiLeveling.WorldBoss.WorldBossSizePatch.RenderScaleFor(pawn) < ElijahWarden.RenderScale - 0.001f)
            {
                IsekaiLeveling.WorldBoss.WorldBossSizePatch.RegisterRenderScale(pawn, ElijahWarden.RenderScale);
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            }

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                Hediff h = hediffs[i];
                if (h?.def != null && Unconsciousness.Contains(h.def.defName))
                    pawn.health.RemoveHediff(h);
            }
        }
    }

    /// <summary>
    /// She does not fall down. She dies standing, or she wins.
    ///
    /// A prefix rather than a pile of stat offsets because downing has many
    /// causes and only one exit: ShouldBeDowned. Blocking the exit covers
    /// every cause at once — including causes from mods this code has never
    /// heard of — in a way no amount of capacity arithmetic could.
    ///
    /// SHE NEVER FALLS, at any health (user decision, 2026-09-30). This used to
    /// yield below a health threshold (the elijahYieldBelowHealth setting, now
    /// removed) so she would kneel when spent. Now the only way the fight ends
    /// is the godhood body's own rule: destroy the brain and she dies standing.
    /// The known cost: a standing target spreads hits across every body part,
    /// so the head coming apart can take a long time. If that proves too long
    /// in play, the fix is making the brain easier to reach, not letting her
    /// fall again.
    ///
    /// Gated on the hediff, so it costs one hediff lookup on pawns that have
    /// no chance of carrying it and touches nothing else in the game.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDowned))]
    public static class Pawn_HealthTracker_ShouldBeDowned_AscendantPatch
    {
        private static HediffDef ascendantCached;
        private static bool resolved;

        private static HediffDef Ascendant
        {
            get
            {
                if (!resolved)
                {
                    ascendantCached = DefDatabase<HediffDef>.GetNamedSilentFail("IsekaiDungeons_Ascendant");
                    resolved = true;
                }
                return ascendantCached;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(Pawn_HealthTracker __instance, ref bool __result, Pawn ___pawn)
        {
            try
            {
                HediffDef def = Ascendant;
                if (def == null || ___pawn?.health?.hediffSet == null)
                    return true;
                if (!___pawn.health.hediffSet.HasHediff(def))
                    return true;

                __result = false;
                return false;   // skip the original entirely
            }
            catch (System.Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Ascendant downing patch failed: {ex}", 0x60D80);
                return true;
            }
        }
    }
}
