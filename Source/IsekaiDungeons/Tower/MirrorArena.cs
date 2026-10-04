using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.CharacterForge;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// FLOOR 50: THE MIRROR HALL. Halfway up the tower, the fight is you.
    ///
    /// One giant closed hall, and in the middle of it a copy of every colonist
    /// who walked in — same name, same face, same stats, same constellation
    /// build node for node, same psycasts, same gear down to the runes in it.
    /// Send one pawn and duel yourself; send six and fight your own party.
    ///
    /// The copies are built on the base mod's character spec system
    /// (IsekaiCharacterForge.Capture → Generate), which is what makes "same"
    /// a strong word here: the spec round-trips identity, appearance, traits,
    /// skills, the whole Isekai layer including the exact constellation node
    /// list, and gear defs with stuff and quality — and Generate LOCKS the
    /// result, so every automatic system that rewrites NPCs (raid ranking,
    /// role redistribution, stat regeneration) stands down and the copy stays
    /// a copy. MirrorForge then fills in what a spec does not carry: implants
    /// and bionics, genes, vanilla abilities, VPE psycasts (via the main
    /// mod's own reflection bridge), and forge refinement + runes on the gear.
    ///
    /// The floor itself is three pieces of existing machinery pointed at one
    /// place: a TowerFloorArchetypeDef pinned to exactly floor 50 (a
    /// single-floor band outranks every roll), a DungeonKindDef flagged
    /// mirrorArena, and a dedicated map generator whose layout step carves
    /// one pillared hall instead of a room chain. The copies cannot spawn at
    /// map generation — the map is built while the party still stands on
    /// floor 49 — so DungeonMapComponent spawns them on the first tick a
    /// player pawn is present, which also means the roster mirrors whoever
    /// ACTUALLY came, not whoever was around when the stair was clicked.
    /// </summary>
    public static class MirrorForge
    {
        /// <summary>
        /// Hediffs the mirror must never inherit. GodhoodBody carries
        /// preventsDeath — a copy with it could not lose. The VPE implant is
        /// excluded from the GENERIC pass only, because CopyPsycasts rebuilds
        /// it properly wired (psylink first, then implant, then link).
        /// </summary>
        private static readonly HashSet<string> ExcludedHediffs = new HashSet<string>
        {
            "IsekaiDungeons_GodhoodBody",
            "VPE_PsycastAbilityImplant",
        };

        /// <summary>
        /// A pure copy of a colonist, hostile, standing on nothing the
        /// original could claim: no relations (suppressed at generation), no
        /// player faction (spec.faction null resolves to the first permanently
        /// hostile humanlike faction), and locked so it stays authored.
        /// Null when the forge fails; the caller just fields fewer mirrors.
        /// </summary>
        public static Pawn Clone(Pawn source, Map map)
        {
            if (source == null)
                return null;

            IsekaiCharacterSpec spec = IsekaiCharacterForge.Capture(source);
            if (spec == null)
                return null;

            // Generate rolls a real pawn first and overlays the spec on it, so
            // anything the overlay does not CLEAR survives the roll. A mirror
            // must not keep a stranger's traits under your name.
            //
            // THE FACTION IS OURS, NOT "whichever hostiles resolve first". The
            // first version left spec.faction null, and the forge's fallback
            // handed the copy to a random hostile faction - whose xenotypeSet
            // then drove generation, which is how a baseliner colonist met a
            // mirror carrying another faction's Yttakin germline. The tower's
            // own blank enemy faction generates baseliners only, carries no
            // apparel/ideo identity of its own, is permanently hostile, and is
            // hidden from the Factions tab exactly like the tower folk.
            EnsureMirrorFaction();
            spec.faction = MirrorFactionDefName;
            spec.clearExistingTraits = true;
            spec.clearExistingApparel = true;
            spec.rollPsycastBonus = false;   // psycasts are COPIED, never rolled

            Pawn mirror = IsekaiCharacterForge.Generate(spec, map?.Tile ?? -1, locked: true);
            if (mirror == null)
                return null;

            // ── What the spec does not carry ────────────────────────────────
            CopyGenesExact(source, mirror);
            CopyHediffs(source, mirror);
            CopyAbilities(source, mirror);
            VPEPsycastRankBonusProxy(source, mirror);
            CopyForgeEnhancements(source, mirror);

            mirror.Drawer?.renderer?.SetAllGraphicsDirty();
            return mirror;
        }

        /// <summary>The tower's blank enemy faction: permanently hostile, hidden, baseliner-only generation.</summary>
        public const string MirrorFactionDefName = "IsekaiDungeons_MirrorFaction";

        /// <summary>
        /// Same runtime-creation pattern as TowerVillage.EnsureFaction: the
        /// faction exists the first time something needs it, so mid-save
        /// installs work and worlds that never reach floor 50 never carry it.
        /// </summary>
        public static Faction EnsureMirrorFaction()
        {
            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(MirrorFactionDefName);
            if (def == null)
                return null;
            Faction existing = Find.FactionManager?.FirstFactionOfDef(def);
            if (existing != null)
                return existing;
            try
            {
                Faction faction = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(def));
                Find.FactionManager.Add(faction);
                return faction;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not create the mirror faction: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The mirror's genes become EXACTLY the source's - not a merge.
        ///
        /// The first version only ADDED missing genes, which left everything
        /// generation had rolled underneath: SetXenotype clears xenogenes but
        /// not ENDOGENES, and inheritable xenotypes (Yttakin among them) put
        /// their genes in the endogene list - so a mirror rolled under the
        /// wrong faction kept a full germline your colonist never had. Now
        /// every rolled gene is removed first, and what remains is a
        /// def-for-def copy of the source's two lists.
        /// </summary>
        private static void CopyGenesExact(Pawn source, Pawn mirror)
        {
            if (!ModsConfig.BiotechActive || source.genes == null || mirror.genes == null)
                return;
            try
            {
                var rolled = new List<Gene>();
                rolled.AddRange(mirror.genes.Endogenes);
                rolled.AddRange(mirror.genes.Xenogenes);
                for (int i = 0; i < rolled.Count; i++)
                    if (rolled[i] != null)
                        mirror.genes.RemoveGene(rolled[i]);

                foreach (Gene g in source.genes.Endogenes)
                    if (g?.def != null && mirror.genes.GetGene(g.def) == null)
                        mirror.genes.AddGene(g.def, xenogene: false);
                foreach (Gene g in source.genes.Xenogenes)
                    if (g?.def != null && mirror.genes.GetGene(g.def) == null)
                        mirror.genes.AddGene(g.def, xenogene: true);

                // Direct: sets the label without re-adding the xenotype's
                // gene list - the lists above are already the verbatim copy.
                mirror.genes.SetXenotypeDirect(source.genes.Xenotype);
                mirror.genes.xenotypeName = source.genes.xenotypeName;
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Mirror gene copy failed for {source.LabelShort}: {ex.Message}");
            }
        }

        /// <summary>
        /// The INSTALLED body: bionics, archotech parts, implants, psylink.
        /// Whitelisted by nature rather than by name — added parts, implants,
        /// and levelled hediffs — so injuries, scars and diseases stay where
        /// they belong: a mirror is the pawn as built, not as bruised. Parts
        /// map by body index, which is exact because both bodies are the same
        /// race's body def.
        /// </summary>
        private static void CopyHediffs(Pawn source, Pawn mirror)
        {
            List<Hediff> hediffs = source.health?.hediffSet?.hediffs;
            if (hediffs == null || mirror.health == null)
                return;

            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h?.def == null || ExcludedHediffs.Contains(h.def.defName))
                    continue;

                bool installed = h is Hediff_AddedPart || h is Hediff_Implant
                    || h is Hediff_Level || h.def.countsAsAddedPartOrImplant;
                if (!installed)
                    continue;

                try
                {
                    BodyPartRecord part = null;
                    if (h.Part != null)
                    {
                        List<BodyPartRecord> parts = mirror.RaceProps.body.AllParts;
                        if (h.Part.Index >= 0 && h.Part.Index < parts.Count)
                            part = parts[h.Part.Index];
                    }
                    if (mirror.health.hediffSet.HasHediff(h.def, part))
                        continue;

                    Hediff copy = HediffMaker.MakeHediff(h.def, mirror, part);
                    if (h is Hediff_Level levelled && copy is Hediff_Level levelledCopy)
                        levelledCopy.level = levelled.level;
                    copy.Severity = h.Severity;
                    mirror.health.AddHediff(copy, part);
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Mirror hediff copy ({h.def.defName}) failed: {ex.Message}");
                }
            }
        }

        /// <summary>Vanilla ability tracker — Royalty psycasts and anything modded that grants through it.</summary>
        private static void CopyAbilities(Pawn source, Pawn mirror)
        {
            List<Ability> abilities = source.abilities?.abilities;
            if (abilities == null || mirror.abilities == null)
                return;
            for (int i = 0; i < abilities.Count; i++)
            {
                AbilityDef def = abilities[i]?.def;
                if (def != null && mirror.abilities.GetAbility(def) == null)
                    mirror.abilities.GainAbility(def);
            }
        }

        /// <summary>
        /// VPE psycasts through the main mod's own reflection bridge — exact
        /// copy, never a roll, silent no-op without VPE. Named as a proxy so a
        /// reader looking for VPE handling finds ONE place: the base mod owns
        /// the reflection cache, this add-on only asks.
        /// </summary>
        private static void VPEPsycastRankBonusProxy(Pawn source, Pawn mirror)
        {
            IsekaiLeveling.Compatibility.VPEPsycastRankBonus.CopyPsycasts(source, mirror);
        }

        /// <summary>
        /// Refinement and runes. GearSpec carries def, stuff and quality; the
        /// forge layer lives in CompForgeEnhancement and has to be carried
        /// across by hand. Items match by def+stuff, first unclaimed wins —
        /// exact enough for a wardrobe, and a colonist wearing two identical
        /// enchanted dusters gets a mirror wearing the same two.
        /// </summary>
        private static void CopyForgeEnhancements(Pawn source, Pawn mirror)
        {
            var claimed = new HashSet<Thing>();

            void CopyOnto(Thing from, IEnumerable<Thing> candidates)
            {
                var fromComp = from?.TryGetComp<IsekaiLeveling.Forge.CompForgeEnhancement>();
                if (fromComp == null)
                    return;
                foreach (Thing to in candidates)
                {
                    if (to == null || claimed.Contains(to) || to.def != from.def || to.Stuff != from.Stuff)
                        continue;
                    var toComp = to.TryGetComp<IsekaiLeveling.Forge.CompForgeEnhancement>();
                    if (toComp == null)
                        return;
                    toComp.refinementLevel = fromComp.refinementLevel;
                    toComp.appliedRuneDefNames = new List<string>(fromComp.appliedRuneDefNames ?? new List<string>());
                    toComp.appliedRuneRanks = new List<int>(fromComp.appliedRuneRanks ?? new List<int>());
                    // The mirror match must be a fair fight: copying the refinement raises
                    // this copy's MaxHitPoints without touching its stored HitPoints, so
                    // the duplicate would enter the arena pre-damaged against the original.
                    IsekaiLeveling.Forge.ForgeUtility.FillToMaxCondition(to);
                    claimed.Add(to);
                    return;
                }
            }

            try
            {
                Thing srcWeapon = source.equipment?.Primary;
                Thing dstWeapon = mirror.equipment?.Primary;
                if (srcWeapon != null && dstWeapon != null)
                    CopyOnto(srcWeapon, new[] { dstWeapon });

                List<Apparel> srcWorn = source.apparel?.WornApparel;
                List<Apparel> dstWorn = mirror.apparel?.WornApparel;
                if (srcWorn != null && dstWorn != null)
                    for (int i = 0; i < srcWorn.Count; i++)
                        CopyOnto(srcWorn[i], dstWorn);
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Mirror forge-enhancement copy failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Carves the mirror hall: one giant pillared chamber and the portal
    /// alcove that leads into it. Replaces GenStep_DungeonLayout on the
    /// arena's generator — a room-chain planner has nothing to plan here.
    ///
    /// The pillars are not decor. Every roofed cell must sit within 6 cells of
    /// support or vanilla collapses it, which is exactly why ordinary carved
    /// rooms cap at 13 wide (see IsekaiDungeonBase's roomSizeRange comment). A
    /// 35-cell hall is only possible as a COLONNADE: 2x2 columns of the native
    /// rock left standing on a 9-cell grid, which reads as architecture and
    /// halves as cover in the duel.
    /// </summary>
    public class GenStep_MirrorArenaLayout : GenStep
    {
        public override int SeedPart => 736251908;

        /// <summary>Hall edge length — a true arena (2.7x the first draft's floor area). Needs the 150 map the arena def sets.</summary>
        private const int HallSize = 57;

        /// <summary>Column grid pitch; with 2-wide columns, no cell is farther than ~4 from support.</summary>
        private const int PillarPitch = 9;

        /// <summary>
        /// The centre plaza: no pillars, no roof. The mirrors stand HERE, dead
        /// centre under a shaft of open sky — and the missing roof is not just
        /// staging: an unroofed cell needs no support, so the plaza can be
        /// clear of columns without recreating the collapse bug the first
        /// draft had (clearing a spawn zone under a ROOFED span put its middle
        /// past vanilla's 6.9-cell support radius). Pillar removal and roof
        /// removal share one radius so the coupling can never drift.
        /// </summary>
        private const int PlazaRadius = 6;

        private const int AlcoveSize = 9;
        private const int MouthWidth = 5;
        /// <summary>Wall rows between alcove and hall — the doorway's depth.</summary>
        private const int MouthDepth = 2;

        public override void Generate(Map map, GenStepParams parms)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            GenStep_DungeonLayout.ResolveContext(map, out DungeonKindDef kind, out _);

            IntVec3 c = map.Center;
            CellRect hall = CellRect.CenteredOn(c, HallSize / 2);

            // Alcove hangs off the hall's south face; the party arrives here
            // and walks up through the mouth into the duel.
            CellRect alcove = new CellRect(
                c.x - AlcoveSize / 2, hall.minZ - MouthDepth - AlcoveSize, AlcoveSize, AlcoveSize);
            CellRect mouth = new CellRect(
                c.x - MouthWidth / 2, alcove.maxZ + 1, MouthWidth, MouthDepth);

            TerrainDef floor = kind?.floorTerrain;

            foreach (IntVec3 cell in hall)
            {
                bool plaza = cell.DistanceToSquared(c) <= PlazaRadius * PlazaRadius;
                if (plaza || !IsPillarCell(cell, hall))
                    CarveCell(map, cell, floor, roofed: !plaza);
            }
            foreach (IntVec3 cell in alcove)
                CarveCell(map, cell, floor, roofed: true);
            foreach (IntVec3 cell in mouth)
                CarveCell(map, cell, floor, roofed: true);

            if (comp != null)
            {
                var rooms = new List<DungeonRoom>
                {
                    new DungeonRoom(alcove, 0, DungeonRoomRole.Entry) { depthFrac = 0f, depthAuthored = true },
                    new DungeonRoom(hall, 1, DungeonRoomRole.Boss) { depthFrac = 1f, depthAuthored = true },
                };
                comp.RegisterLayout(rooms);

                // The arena has no population step, so it must honour the
                // floor's history itself: a beaten summit comes back as an
                // empty hall with an open stair, never a second duel.
                if (map.Parent is DungeonMapParent parent && parent.suppressPopulation)
                    comp.Notify_GeneratedEmpty();
            }
        }

        /// <summary>
        /// 2x2 native-rock columns at the pitch-grid intersections, skipped
        /// along the rim where the wall itself is the support.
        ///
        /// THE CENTRE COLUMN STAYS, deliberately. The first draft cleared a
        /// spawn zone around the middle, which quietly removed the one support
        /// the middle of the roof depends on — vanilla holds a roof up to 6.9
        /// cells from an edifice, and without the centre intersection the
        /// central cells sit ~7.1 from the next column: collapse on
        /// generation, rocks on the mirrors' heads. The spawn finder only
        /// picks standable cells anyway, so the copies simply ring the
        /// column — which reads better than an empty centre did.
        /// </summary>
        private static bool IsPillarCell(IntVec3 cell, CellRect hall)
        {
            int lx = cell.x - hall.minX;
            int lz = cell.z - hall.minZ;
            if (lx < 3 || lz < 3 || lx >= hall.Width - 3 || lz >= hall.Height - 3)
                return false;

            return lx % PillarPitch < 2 && lz % PillarPitch < 2;
        }

        /// <summary>
        /// Same recipe as GenStep_DungeonLayout.CarveCell: drop the rock, lay
        /// the themed floor, swap the collapse-bait thick roof for the thin
        /// rock vanilla uses over open cave space.
        /// </summary>
        private static void CarveCell(Map map, IntVec3 cell, TerrainDef floor, bool roofed)
        {
            if (!cell.InBounds(map))
                return;
            cell.GetEdifice(map)?.Destroy();
            if (floor != null)
                map.terrainGrid.SetTerrain(cell, floor);
            map.roofGrid.SetRoof(cell, roofed ? RoofDefOf.RoofRockThin : null);
        }
    }

    /// <summary>
    /// The mirror hall's win condition: every copy defeated — dead, downed, or
    /// removed from play. Downed counts on purpose: these wear your
    /// colonists' faces, and the floor should not demand executions the
    /// player may not want to watch. (The autopilot WILL finish downed
    /// enemies; a player who wants to spare their own reflection plays this
    /// floor by hand.)
    /// </summary>
    public class DungeonObjectiveWorker_MirrorArena : DungeonObjectiveWorker
    {
        public override bool IsComplete(DungeonMapComponent dungeon)
        {
            if (dungeon == null || !dungeon.ArenaMirrorsSpawned)
                return false;   // the duel has not begun; the stair stays sealed

            List<Pawn> mirrors = dungeon.ArenaMirrors;
            if (mirrors == null || mirrors.Count == 0)
                return true;    // spawn produced nothing to fight — never seal forever

            for (int i = 0; i < mirrors.Count; i++)
            {
                Pawn m = mirrors[i];
                if (m != null && !m.Dead && !m.Destroyed && (m.Spawned || m.ParentHolder is PawnFlyer) && !m.Downed)
                    return false;
            }
            return true;
        }

        public override string ObjectiveDescription(DungeonKindDef kind, MobRankTier rank) =>
            "IsekaiDungeons.MirrorArena.Objective".Translate();
    }
}
