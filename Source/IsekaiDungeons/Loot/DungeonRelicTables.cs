using System.Collections.Generic;
using System.Text;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What a relic READS AS when you find it on the floor of the last room.
    ///
    /// The grouping is deliberately not "which DLC did this come from". A
    /// player opening a boss room does not think "ah, Royalty content"; they
    /// think "there is a staff and a set of robes here, someone died in this
    /// room". So the tables are cut by fiction, and the DLC each entry needs
    /// is a resolution detail handled by GetNamedSilentFail — a missing
    /// expansion simply thins the table.
    ///
    /// The groups exist as an enum rather than as separate lists because a
    /// future DungeonKindDef.lootTags field (LOOT_POOL.md Part 4) wants to
    /// say "this dungeon prefers MageKit and Occult" without the tables
    /// being restructured for it.
    /// </summary>
    public enum RelicGroup
    {
        /// <summary>Sealed vault: archotech and ancient-tech centrepieces.</summary>
        Relic,

        /// <summary>Shrine: things you install in a body.</summary>
        Blessing,

        /// <summary>It has a name: persona weapons that title themselves.</summary>
        NamedBlade,

        /// <summary>Robed corpse: eltex and shield gear.</summary>
        MageKit,

        /// <summary>Foundry: mechanitor tooling and mech guns.</summary>
        Mechtech,

        /// <summary>Specimen shelf: genepacks, archites, catalysts.</summary>
        Genetics,

        /// <summary>Cursed vault: Anomaly relics.</summary>
        Occult,
    }

    /// <summary>
    /// The curated vanilla and DLC treasure tables, and the rank-by-value rule
    /// that decides which of them a given dungeon may pay out.
    ///
    /// ── THE RULE ───────────────────────────────────────────────────────────
    ///
    /// There is no hardcoded minimum rank on any entry. A relic is eligible
    /// when its market value fits the dungeon's own loot budget, and that is
    /// the whole gate:
    ///
    ///     eligible  ⇔  BaseMarketValue ≤ target × MaxRelicShare
    ///
    /// This is what "correctly corresponding to their value" means in code. It
    /// is also self-maintaining in three ways a hand-written rank column is
    /// not. It tracks DungeonKindDef.lootValueByRank, so retuning the payout
    /// curve retunes the relic ladder with it. It tracks the DEF, so a mod
    /// that repricess an item moves it to the right rank without us noticing.
    /// And it works for the tower's fractional floors, which sit between ranks
    /// and have no enum value to compare against.
    ///
    /// Worked through the shipped curve (270 at F … 138 240 at Nation) with
    /// MaxRelicShare = 0.40, the ceiling per rank is:
    ///
    ///     F 108  E 216  D 432  C 864  B 1728  A 3456
    ///     S 6912  SS 13824  SSS 27648  Nation 55296
    ///
    /// So F and E pay no relic (see RelicDrawsByRank for why E is excluded
    /// deliberately rather than incidentally). D opens on eltex shirts, shards,
    /// tomes, subcores and catalysts; C adds the rest of the eltex set, archite
    /// capsules and genepacks; B adds the bionics, the chemreactor and the mech
    /// serums; A adds psylink, the archotech limbs and the persona weapons; S
    /// adds the persona core, the animus stone and the cerebrex node — which is
    /// the whole table, because 5000 silver is the top of what vanilla and its
    /// expansions actually sell. SS and above simply draw more of them, and the
    /// unspent remainder of the slice falls through to the hoard.
    ///
    /// That ceiling is a real limit, not a tuning miss: a Nation clear can
    /// afford 55 296 of relic and the most expensive thing Ludeon ships is an
    /// animus stone. Filling that top end is what the Isekai Creatures named
    /// weapons (3 800–15 000) and the sealing cube (20 000) are for.
    ///
    /// ── THE OTHER HALF OF THE RULE ─────────────────────────────────────────
    ///
    /// A ceiling alone would leave a Nation dungeon paying out shards, because
    /// a 400-silver shard fits under 55 296 forever. So value ALSO sets the
    /// weight: an entry worth less than RelicSoftFloorShare of the target is
    /// down-weighted in proportion to how far under it sits. Cheap trinkets
    /// fade out as the ladder climbs instead of being cut off, which keeps the
    /// tail of the table alive at the top ranks without letting it dominate.
    ///
    /// ── WHAT IS DELIBERATELY ABSENT ────────────────────────────────────────
    ///
    /// GoldenCube. It is Anomaly's best-designed trap: picking it up starts a
    /// colony-wide obsession that is expensive to unwind. That makes it superb
    /// as a choice the player knowingly takes and terrible as something that
    /// falls out of a chest they opened for a sword. It belongs to the cursed
    /// chest mechanic (LOOT_POOL.md Part 4), which does not exist yet, and
    /// until it does the cube stays out of the random pool.
    /// </summary>
    public static class DungeonRelicTables
    {
        /// <summary>Non-plain construction paths. Everything else is ThingMaker plus a stuff pick.</summary>
        public enum RelicSpecial
        {
            None,

            /// <summary>
            /// Genepacks carry their value in their genes, not their def: the
            /// ThingDef is worth 100 silver empty. So the entry declares a
            /// nominal value for the eligibility test and the built pack's real
            /// MarketValue is what gets charged to the budget.
            /// </summary>
            Genepack,
        }

        /// <summary>One line of a treasure table.</summary>
        public class RelicEntry
        {
            public readonly string defName;
            public readonly RelicGroup group;

            /// <summary>
            /// Rarity within the group, BEFORE the value weighting above. Use
            /// it for "how special should this feel", not for "how expensive
            /// is it" — value already handles the second question.
            /// </summary>
            public readonly float weight;

            /// <summary>Overrides BaseMarketValue for the eligibility test only. -1 = use the def.</summary>
            public readonly float nominalValue;

            public readonly RelicSpecial special;

            /// <summary>Resolved once at first use. Null means the expansion or mod is absent.</summary>
            public ThingDef def;

            public RelicEntry(string defName, RelicGroup group, float weight,
                float nominalValue = -1f, RelicSpecial special = RelicSpecial.None)
            {
                this.defName = defName;
                this.group = group;
                this.weight = weight;
                this.nominalValue = nominalValue;
                this.special = special;
            }

            /// <summary>The number the rank-by-value rule is applied to.</summary>
            public float Value => nominalValue >= 0f ? nominalValue : (def?.BaseMarketValue ?? 0f);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Tuning
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Largest fraction of a clear's value target one relic may be.
        ///
        /// Two fifths. Gear is the headline of a haul (GearShare is 55%), and a
        /// single object eating more than this crowds the pile down to "one
        /// expensive thing and some coins", which is a worse read than a pile
        /// with an expensive thing IN it. A setting (Rewards tab).
        /// </summary>
        public static float MaxRelicShare => IsekaiDungeonsSettings.Current.maxRelicShare;

        /// <summary>
        /// Below this fraction of the target an entry starts losing weight,
        /// linearly, hitting zero at zero value. See the class comment: this is
        /// what makes a Nation dungeon stop paying out 400-silver shards
        /// without ever hard-banning them. A setting (Advanced tab).
        /// </summary>
        public static float RelicSoftFloorShare => IsekaiDungeonsSettings.Current.relicSoftFloorShare;

        /// <summary>
        /// Expected relic count per clear, indexed by (int)MobRankTier.
        /// Fractional: the integer part is guaranteed, the remainder is a
        /// chance.
        ///
        ///   F  E  D   C   B   A   S   SS  SSS  Nation
        ///   0  0  0.5 0.8 1.1 1.4 1.8 2.3 2.9  3.6
        ///
        /// F AND E ARE ZERO, and E is the interesting one. Its 540-silver
        /// target puts the ceiling at 216, and exactly one line of the table
        /// costs less than that — so an E dungeon with relics enabled paid out
        /// a mechanoid transponder every single time it paid out anything, a
        /// mechanitor consumable that is useless without a mechlink the colony
        /// almost certainly does not have yet. A category that can only ever
        /// produce one wrong answer should not run, so the ladder starts at D,
        /// where eltex, shards, subcores and catalysts all come into range at
        /// once. (Measured, not assumed: the E case was 100% transponder over
        /// 20 000 simulated clears.) A setting (Advanced tab); raising F or E
        /// above zero brings that transponder problem back.
        /// </summary>
        public static float[] RelicDrawsByRank => IsekaiDungeonsSettings.Current.relicDrawsByRank;

        /// <summary>Genes packed into a dungeon genepack, indexed by (int)MobRankTier. A setting (Advanced tab).</summary>
        private static int[] GeneCountByRank => IsekaiDungeonsSettings.Current.geneCountByRank;

        /// <summary>Rank at and above which a genepack may roll archite genes.</summary>
        private const MobRankTier ArchiteGeneMinRank = MobRankTier.SS;

        // ────────────────────────────────────────────────────────────────────
        //  The tables
        //
        //  Values in the comments are the shipped BaseMarketValue and are
        //  documentation only — nothing reads them. The rank each line lands at
        //  is derived, not declared.
        // ────────────────────────────────────────────────────────────────────

        private static readonly RelicEntry[] Table =
        {
            // ── RELIC — the sealed vault ────────────────────────────────────
            // Ancient-tech centrepieces. Several are minifiable BUILDINGS; they
            // are wrapped by MinifyUtility at construction so they behave like
            // any other item in the pile, and a non-minifiable one is dropped
            // at resolve time rather than producing an unhaulable thing.
            new RelicEntry("AIPersonaCore",          RelicGroup.Relic, 0.50f),  // 4000, Core
            new RelicEntry("TechprofSubpersonaCore", RelicGroup.Relic, 0.80f),  // 1000, Core
            new RelicEntry("InfiniteChemreactor",    RelicGroup.Relic, 0.60f),  // 1500, Core, minified
            new RelicEntry("VanometricPowerCell",    RelicGroup.Relic, 0.70f),  // 1200, Core, minified
            new RelicEntry("PsychicEmanator",        RelicGroup.Relic, 0.70f),  // 1300, Core, minified
            new RelicEntry("AnimusStone",            RelicGroup.Relic, 0.40f),  // 5000, Royalty, minified
            new RelicEntry("GravEngine",             RelicGroup.Relic, 0.40f),  // 2000, Odyssey, minified
            new RelicEntry("AncientGravEngine",      RelicGroup.Relic, 0.35f),  // 2000, Odyssey
            new RelicEntry("Gravcore",               RelicGroup.Relic, 0.70f),  // 1250, Odyssey
            new RelicEntry("ShuttleEngine",          RelicGroup.Relic, 0.50f),  // 2500, Odyssey
            new RelicEntry("AncientTransportPod",    RelicGroup.Relic, 0.40f),  // 2000, Odyssey

            // ── BLESSING — the shrine ───────────────────────────────────────
            // Things you install in a body. The most immediately USEFUL group,
            // which is why the plain bionics carry the highest weights: an
            // archotech arm is a story, a bionic arm is a colonist who can
            // work again.
            new RelicEntry("PsychicAmplifier",     RelicGroup.Blessing, 0.50f), // 2600, Core
            new RelicEntry("ArchotechArm",         RelicGroup.Blessing, 0.55f), // 2800, Core
            new RelicEntry("ArchotechLeg",         RelicGroup.Blessing, 0.55f), // 2800, Core
            new RelicEntry("ArchotechEye",         RelicGroup.Blessing, 0.55f), // 2800, Core
            new RelicEntry("MechSerumHealer",      RelicGroup.Blessing, 0.90f), // 1300, Core
            new RelicEntry("MechSerumResurrector", RelicGroup.Blessing, 0.50f), // 1700, Core
            new RelicEntry("BionicArm",            RelicGroup.Blessing, 1.00f), // cost-derived, Core
            new RelicEntry("BionicLeg",            RelicGroup.Blessing, 1.00f),
            new RelicEntry("BionicEye",            RelicGroup.Blessing, 1.00f),
            new RelicEntry("BionicHeart",          RelicGroup.Blessing, 0.90f),
            new RelicEntry("BionicStomach",        RelicGroup.Blessing, 0.80f),
            new RelicEntry("BionicSpine",          RelicGroup.Blessing, 0.80f),
            new RelicEntry("PsychicSensitizer",    RelicGroup.Blessing, 0.70f), // 1000, Royalty
            new RelicEntry("PsychicReader",        RelicGroup.Blessing, 0.70f), // 1000, Royalty
            new RelicEntry("PsychicHarmonizer",    RelicGroup.Blessing, 0.70f), //  800, Royalty
            new RelicEntry("DetoxifierKidney",     RelicGroup.Blessing, 0.80f), // 1080, Biotech
            new RelicEntry("DetoxifierLung",       RelicGroup.Blessing, 0.80f), //  925, Biotech
            new RelicEntry("Apparel_CerebrexNode", RelicGroup.Blessing, 0.35f), // 5000, Odyssey

            // ── NAMED BLADE — it has a name ─────────────────────────────────
            // Persona weapons title themselves and roll their own traits, which
            // is the closest vanilla gets to an isekai artifact for zero
            // content cost. Capped at one per haul by the draw loop: they
            // biocode to the first pawn who equips them, so a second is dead
            // weight in the same pile.
            new RelicEntry("MeleeWeapon_MonoSwordBladelink",   RelicGroup.NamedBlade, 1.00f), // 3000, Royalty
            new RelicEntry("MeleeWeapon_PlasmaSwordBladelink", RelicGroup.NamedBlade, 0.80f), // 3000, Royalty
            new RelicEntry("MeleeWeapon_ZeusHammerBladelink",  RelicGroup.NamedBlade, 0.80f), // 3000, Royalty

            // ── MAGE KIT — the robed corpse ─────────────────────────────────
            // The cheapest group, which is the point: it is what a low-rank
            // dungeon can afford to pay, and a set of eltex reads as loot in a
            // way that a stack of steel does not.
            new RelicEntry("MeleeWeapon_PsyfocusStaff", RelicGroup.MageKit, 0.70f), // 2000, Royalty
            new RelicEntry("Apparel_PsyfocusRobe",      RelicGroup.MageKit, 1.20f), //  600, Royalty
            new RelicEntry("Apparel_PsyfocusVest",      RelicGroup.MageKit, 1.20f), //  500, Royalty
            new RelicEntry("Apparel_PsyfocusHelmet",    RelicGroup.MageKit, 1.20f), //  500, Royalty
            new RelicEntry("Apparel_PsyfocusShirt",     RelicGroup.MageKit, 1.00f), //  400, Royalty
            new RelicEntry("Apparel_EltexSkullcap",     RelicGroup.MageKit, 1.00f), //  500, Royalty
            new RelicEntry("UnstablePowerCell",         RelicGroup.MageKit, 0.60f), //  400, Royalty, minified
            new RelicEntry("BroadshieldCore",           RelicGroup.MageKit, 0.80f), //  350, Royalty
            new RelicEntry("ShieldGeneratorBullets",    RelicGroup.MageKit, 0.50f), // 1000, Royalty
            new RelicEntry("ShieldGeneratorMortar",     RelicGroup.MageKit, 0.50f), // 1000, Royalty

            // ── MECHTECH — the foundry ──────────────────────────────────────
            new RelicEntry("NanostructuringChip",   RelicGroup.Mechtech, 0.60f), // 1500, Biotech
            new RelicEntry("PowerfocusChip",        RelicGroup.Mechtech, 0.80f), // 1000, Biotech
            new RelicEntry("SubcoreHigh",           RelicGroup.Mechtech, 0.80f), // 1000, Biotech
            new RelicEntry("SubcoreRegular",        RelicGroup.Mechtech, 1.00f), //  300, Biotech
            new RelicEntry("Mechlink",              RelicGroup.Mechtech, 0.70f), //  500, Biotech
            new RelicEntry("MechanoidTransponder",  RelicGroup.Mechtech, 0.60f), //  200, Biotech
            new RelicEntry("Gun_HellsphereCannon",  RelicGroup.Mechtech, 0.50f), // 1400, Biotech
            new RelicEntry("Gun_ToxicNeedle",       RelicGroup.Mechtech, 0.50f), // 1400, Biotech

            // ── GENETICS — the specimen shelf ───────────────────────────────
            // The genepack's nominal value is a stand-in: the def is 100 silver
            // empty and its real worth is whatever genes get packed into it, so
            // eligibility uses 600 (about a mid-rank pack) and the budget is
            // charged the built thing's actual MarketValue.
            new RelicEntry("Genepack",         RelicGroup.Genetics, 1.20f, 600f, RelicSpecial.Genepack),
            new RelicEntry("ArchiteCapsule",   RelicGroup.Genetics, 0.90f), // 700, Biotech
            new RelicEntry("SentienceCatalyst",RelicGroup.Genetics, 0.80f), // 400, Odyssey

            // ── OCCULT — the cursed vault ───────────────────────────────────
            // Anomaly. MonolithFragment is the one entry here that is genuinely
            // inert without an activated monolith, so it carries its own gate
            // (see MonolithRequired); the rest work on their own terms whether
            // or not the player has engaged the questline.
            new RelicEntry("MonolithFragment",          RelicGroup.Occult, 0.50f), // 2000, Anomaly
            new RelicEntry("RevenantSpine",             RelicGroup.Occult, 0.70f), // 1000, Anomaly
            new RelicEntry("Apparel_BiomutationLance",  RelicGroup.Occult, 0.80f), //  850, Anomaly
            new RelicEntry("BiomutationPulser",         RelicGroup.Occult, 0.80f), //  800, Anomaly
            new RelicEntry("Shard",                     RelicGroup.Occult, 1.00f), //  400, Anomaly
            new RelicEntry("Tome",                      RelicGroup.Occult, 0.90f), //  250, Anomaly
        };

        /// <summary>Entries that only make sense once the monolith is up.</summary>
        private static readonly HashSet<string> MonolithRequired = new HashSet<string> { "MonolithFragment" };

        // ────────────────────────────────────────────────────────────────────
        //  Resolution
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Entries whose def resolved AND which can actually be handed to a
        /// player. Built once; def identity is stable for the process lifetime,
        /// which is the same assumption DungeonLootGenerator's rune cache and
        /// the spawn providers already make.
        /// </summary>
        private static List<RelicEntry> live;

        private static void EnsureResolved()
        {
            if (live != null)
                return;

            live = new List<RelicEntry>(Table.Length);
            List<string> dropped = null;

            for (int i = 0; i < Table.Length; i++)
            {
                RelicEntry e = Table[i];
                e.def = DefDatabase<ThingDef>.GetNamedSilentFail(e.defName);

                // Absent expansion or absent mod. Not an error — the table is
                // written for a full install and thins itself for anything
                // less, exactly like the dungeon rosters do.
                if (e.def == null)
                    continue;

                // A building that cannot be minified cannot be picked up, and a
                // relic the player cannot carry out is worse than no relic. We
                // cannot know this from the XML (minifiedDef is usually
                // inherited), so it is checked here rather than assumed above.
                if (e.def.category == ThingCategory.Building && !e.def.Minifiable)
                {
                    (dropped ?? (dropped = new List<string>())).Add(e.defName);
                    continue;
                }

                live.Add(e);
            }

            if (dropped != null && Prefs.DevMode)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(IsekaiDungeonsMod.LogPrefix)
                  .Append(" relic table dropped ").Append(dropped.Count)
                  .Append(" non-minifiable building entr").Append(dropped.Count == 1 ? "y: " : "ies: ");
                for (int i = 0; i < dropped.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(dropped[i]);
                }
                Log.Message(sb.ToString());
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Draw
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// How many relics a clear at this rank should attempt. Integer part
        /// guaranteed, fraction rolled.
        /// </summary>
        public static int DrawCountFor(MobRankTier rank)
        {
            int i = Mathf.Clamp((int)rank, 0, RelicDrawsByRank.Length - 1);
            float expected = RelicDrawsByRank[i];
            int whole = Mathf.FloorToInt(expected);
            return whole + (Rand.Chance(expected - whole) ? 1 : 0);
        }

        /// <summary>
        /// Picks one relic entry that fits the budget, or null when nothing
        /// does. <paramref name="usedDefNames"/> is the haul's running set:
        /// the same def never appears twice in one pile (three identical
        /// archotech eyes is a bug the player will read as one), and the first
        /// NamedBlade closes that whole group for the rest of the haul.
        /// </summary>
        public static RelicEntry TryPick(float target, HashSet<string> usedDefNames)
        {
            EnsureResolved();
            if (live.Count == 0 || target <= 0f)
                return null;

            float ceiling = target * MaxRelicShare;
            float softFloor = target * RelicSoftFloorShare;
            bool bladeUsed = false;

            if (usedDefNames != null)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i].group == RelicGroup.NamedBlade && usedDefNames.Contains(live[i].defName))
                    {
                        bladeUsed = true;
                        break;
                    }
                }
            }

            List<RelicEntry> candidates = new List<RelicEntry>();
            List<float> weights = new List<float>();
            float total = 0f;

            for (int i = 0; i < live.Count; i++)
            {
                RelicEntry e = live[i];

                if (usedDefNames != null && usedDefNames.Contains(e.defName))
                    continue;
                if (bladeUsed && e.group == RelicGroup.NamedBlade)
                    continue;
                if (!EntryAvailable(e))
                    continue;

                float value = e.Value;
                if (value <= 0f || value > ceiling)
                    continue;

                // The soft floor: full weight at or above it, falling off
                // below it so cheap entries thin out as the ladder climbs
                // instead of being cut. See the class comment.
                //
                // SQUARED, not linear. Linear was measurably too gentle: at
                // Nation the whole table sits under the floor, so every entry
                // is scaled and only the RATIO between them matters — and a
                // 400-silver shard against a 3000-silver persona sword is 7:1
                // linearly, which still left a fifth of Nation relic draws
                // paying out shards and tomes. Squaring makes it 56:1, which
                // is the intended read: the cheap tail stays possible and stops
                // being common. At mid ranks it barely differs, because there
                // the entries near the floor are the only ones scaled at all.
                float w = e.weight;
                if (softFloor > 0f && value < softFloor)
                {
                    float f = value / softFloor;
                    w *= f * f;
                }

                if (w <= 0f)
                    continue;

                candidates.Add(e);
                weights.Add(w);
                total += w;
            }

            if (candidates.Count == 0 || total <= 0f)
                return null;

            float roll = Rand.Range(0f, total);
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0f)
                    return candidates[i];
            }
            return candidates[candidates.Count - 1];
        }

        /// <summary>
        /// Per-entry availability that is not about value: expansion-specific
        /// world state. Def resolution already handled "is the DLC installed";
        /// this is "does the DLC's content mean anything yet".
        /// </summary>
        private static bool EntryAvailable(RelicEntry e)
        {
            if (!MonolithRequired.Contains(e.defName))
                return true;

            // Find.Anomaly is null outside a running game (and on a save made
            // before Anomaly was enabled), so the null check is the gate, not
            // a defensive flourish.
            return ModsConfig.AnomalyActive
                && Find.Anomaly != null
                && Find.Anomaly.Level > 0;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Construction
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the actual thing for an entry, or null if it could not be
        /// made. Minifiable buildings come back wrapped so they haul, stack in
        /// a caravan, and sell like any other item.
        /// </summary>
        public static Thing Make(RelicEntry entry, MobRankTier rank)
        {
            if (entry?.def == null)
                return null;

            if (entry.special == RelicSpecial.Genepack)
                return MakeGenepack(entry.def, rank);

            ThingDef stuff = entry.def.MadeFromStuff ? GenStuff.DefaultStuffFor(entry.def) : null;
            Thing thing = ThingMaker.MakeThing(entry.def, stuff);
            if (thing == null)
                return null;

            // Quality where the thing has it — a persona monosword and a set of
            // eltex robes both roll one, and a relic that always arrived Normal
            // would read as a placeholder. NOT forge-enhanced, unlike the gear
            // haul: the forge is the colony's own progression path and a relic
            // is something the dungeon had before they got there.
            CompQuality quality = thing.TryGetComp<CompQuality>();
            if (quality != null)
                quality.SetQuality(QualityForRank(rank), ArtGenerationContext.Outsider);

            return MinifyUtility.TryMakeMinified(thing);
        }

        /// <summary>
        /// Quality window for a found relic. Narrower and flatter than the gear
        /// ladder on purpose: a relic's interest is what it IS, and rolling
        /// Awful on a persona weapon would undercut the whole point of the
        /// group.
        /// </summary>
        private static QualityCategory QualityForRank(MobRankTier rank)
        {
            int r = (int)rank;
            QualityCategory min = r >= (int)MobRankTier.SS ? QualityCategory.Excellent
                : r >= (int)MobRankTier.A ? QualityCategory.Good
                : QualityCategory.Normal;
            QualityCategory max = r >= (int)MobRankTier.SSS ? QualityCategory.Legendary
                : r >= (int)MobRankTier.S ? QualityCategory.Masterwork
                : QualityCategory.Excellent;

            return (QualityCategory)Rand.RangeInclusive((int)min, (int)max);
        }

        /// <summary>
        /// A genepack with rank-scaled contents.
        ///
        /// The gene list is built here rather than left to the def's own
        /// PostMake so the contents are OURS to scale: gene count climbs with
        /// rank, and archite genes — the ones that need an archite capsule to
        /// implant and are the actual endgame of Biotech — only appear from SS.
        ///
        /// The filter is deliberately conservative. Genes with a prerequisite
        /// are skipped rather than resolved (a pack holding the second half of
        /// a pair is a puzzle, not a prize), and exclusion tags are tracked so
        /// the pack cannot contain two genes that cancel each other.
        /// </summary>
        private static Thing MakeGenepack(ThingDef packDef, MobRankTier rank)
        {
            if (!ModsConfig.BiotechActive)
                return null;

            Thing thing = ThingMaker.MakeThing(packDef);
            Genepack pack = thing as Genepack;
            if (pack == null)
                return thing;

            bool allowArchite = (int)rank >= (int)ArchiteGeneMinRank;
            int want = GeneCountByRank[Mathf.Clamp((int)rank, 0, GeneCountByRank.Length - 1)];

            List<GeneDef> all = DefDatabase<GeneDef>.AllDefsListForReading;
            List<GeneDef> pool = new List<GeneDef>();
            for (int i = 0; i < all.Count; i++)
            {
                GeneDef g = all[i];
                if (!g.canGenerateInGeneSet || g.prerequisite != null)
                    continue;
                if (g.biostatArc > 0 && !allowArchite)
                    continue;
                pool.Add(g);
            }

            if (pool.Count == 0)
                return thing;

            List<GeneDef> chosen = new List<GeneDef>();
            HashSet<string> exclusions = new HashSet<string>();

            for (int attempt = 0; attempt < want * 8 && chosen.Count < want; attempt++)
            {
                GeneDef g = pool[Rand.Range(0, pool.Count)];
                if (chosen.Contains(g))
                    continue;

                bool clash = false;
                if (g.exclusionTags != null)
                {
                    for (int i = 0; i < g.exclusionTags.Count; i++)
                    {
                        if (exclusions.Contains(g.exclusionTags[i]))
                        {
                            clash = true;
                            break;
                        }
                    }
                }
                if (clash)
                    continue;

                chosen.Add(g);
                if (g.exclusionTags != null)
                    for (int i = 0; i < g.exclusionTags.Count; i++)
                        exclusions.Add(g.exclusionTags[i]);
            }

            // An empty pack is worth nothing and reads as a bug. Better to pay
            // the slot out as something else, which the caller does when this
            // returns null.
            if (chosen.Count == 0)
                return null;

            pack.Initialize(chosen);
            return pack;
        }
    }
}
