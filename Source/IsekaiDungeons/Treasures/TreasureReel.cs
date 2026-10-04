using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using IsekaiLeveling.Forge;
using IsekaiLeveling.UI;

namespace IsekaiDungeons
{
    /// <summary>Reel rarity. Order is severity order - the award tables index it.</summary>
    public enum TreasureRarity : byte { Common, Rare, Epic, Legendary }

    /// <summary>
    /// The loot pool and the award tables behind the treasure chests.
    ///
    /// AUTO-DISCOVERED, ZERO CURATION. The pool is every ThingDef the forge
    /// system itself claimed at startup - ForgeCompInjector stamps
    /// CompProperties_ForgeEnhancement onto every craftable weapon and apparel
    /// def, vanilla, DLC and modded alike - so "what can a chest contain" and
    /// "what can carry refinement and runes" are the same set by construction
    /// and can never drift apart.
    ///
    /// TWO FILTERS SIT ON TOP OF THAT, because what the forge should be able
    /// to enhance and what a chest should be able to CONTAIN are not the same
    /// question - see IsTreasureWorthy for the machinery that gets dropped,
    /// and TierTechLow/High for the tech band each grade draws from.
    ///
    /// RARITY IS MARKET VALUE, banded by percentile at p50 / p80 / p95 rather
    /// than by fixed thresholds. Fixed cuts rot the moment a mod ships 40
    /// cheap spears or three 9000-silver relics; percentiles re-derive the
    /// bands from whatever this playthrough's modlist actually contains.
    ///
    /// THE BANDS ARE PER TIER, not global, and that is what makes a grade mean
    /// something. Banded globally, a jade chest's "Common" was the bottom half
    /// of every item in the game - tribal gear out of the top of the tower -
    /// while a bronze chest's "Legendary" was a persona weapon on day three.
    /// Per tier, Legendary means "the best thing THIS chest could have given",
    /// which is the promise the reel's gold card was always making.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TreasurePool
    {
        public struct Entry
        {
            public ThingDef def;
            public TreasureRarity rarity;
        }

        /// <summary>[tier 0-3][rarity 0-3]. Built once, at first use.</summary>
        private static List<Entry>[][] byTier;
        private static List<Entry> all;

        public static readonly Color[] RarityColor =
        {
            new Color(0.75f, 0.75f, 0.78f),   // Common - the rank ladder's silver
            new Color(0.45f, 0.70f, 1.00f),   // Rare   - B-rank blue
            new Color(0.85f, 0.45f, 0.90f),   // Epic   - S-rank purple
            new Color(1.00f, 0.90f, 0.30f),   // Legendary - SSS brilliant gold
        };

        public static string RarityLabel(TreasureRarity r) =>
            ("IsekaiDungeons.Treasure.Rarity." + r).Translate();

        /// <summary>
        /// The player-facing name of a chest tier.
        ///
        /// NAMED ON THE RARITY LADDER, not on the tier number: "Legendary
        /// Chest" tells a player what it is worth using a word the cards in
        /// the reel already taught them, where "Tier 4" is an implementation
        /// detail wearing a label. The defNames stay Plain/Fine/Grand/Mythic
        /// because renaming those would strand chests in existing saves.
        /// </summary>
        /// <summary>
        /// Grade names for the four chests: Bronze, Silver, Gold, Jade.
        ///
        /// DELIBERATELY NOT the TreasureRarity words. A chest's grade and the
        /// rarity of what comes out of it are different ladders - a Silver
        /// chest shifts the odds, it does not guarantee a Rare item - and
        /// sharing vocabulary implied a promise the odds table never made.
        /// Indexing the rarity enum here is what welded them together, so the
        /// keys are their own array.
        /// </summary>
        private static readonly string[] ChestGrades = { "Bronze", "Silver", "Gold", "Jade" };

        public static string ChestName(int tier) =>
            ("IsekaiDungeons.Treasure.Chest" + ChestGrades[Mathf.Clamp(tier - 1, 0, 3)]).Translate();

        // ── What a chest may actually contain ───────────────────────────────

        /// <summary>Apparel under this much armour is clothing, not a prize.</summary>
        private const float MinApparelArmor = 0.10f;

        /// <summary>
        /// Minimum StuffEffectMultiplierArmor for stuffable apparel to count as
        /// armour rather than clothing.
        ///
        /// MEASURED, and the vanilla spread is unusually clean: everything the
        /// game treats as clothing sits at 0.30 or below (T-shirt and hats 0.20,
        /// duster, jacket and cape 0.30) and real protection starts at 0.50
        /// (simple helmet 0.50, flak helmet 0.70, plate armor 0.90). 0.45 is the
        /// gap between the two clusters, not a guess.
        ///
        /// Treating ANY positive multiplier as armour - the first attempt - let
        /// every T-shirt straight back into the bronze chest, which was the
        /// whole thing this filter existed to stop.
        /// </summary>
        private const float MinStuffArmorMult = 0.45f;

        /// <summary>A melee weapon under this much power is a prop or a drink.</summary>
        private const float MinMeleePower = 12f;

        /// <summary>
        /// The forge claims every weapon and every piece of apparel in the
        /// game. That is correct for the forge and wrong for a chest, because
        /// a good deal of what it claims is MACHINERY rather than loot.
        ///
        /// TURRET AND MECH HARDWARE IS THE REAL OFFENDER, and it fails in a
        /// nasty way. RimWorld's own BaseWeaponTurret sets equipmentType
        /// Primary, so a colonist will cheerfully equip an autocannon or a
        /// mortar pulled out of a chest - and the same base sets
        /// destroyOnDrop, so that weapon EVAPORATES the first time they
        /// unequip it, are downed, or die. tradeability None means it cannot
        /// even be sold in the meantime.
        ///
        /// Note the prize is NOT lost at the moment of the award: Award()
        /// places with GenPlace.TryPlaceThing, and destroyOnDrop is only
        /// enforced in GenDrop.TryDropSpawn. It is lost hours later, which is
        /// worse - by then nobody connects it to the chest it came from.
        ///
        /// 26 defs in a Core+Royalty+Ideology+Biotech+Anomaly+Odyssey install
        /// are caught by destroyOnDrop and tradeability alone.
        /// </summary>
        private static bool IsTreasureWorthy(ThingDef d)
        {
            if (d.destroyOnDrop) return false;
            if (d.tradeability == Tradeability.None) return false;

            // Apparel is worn rather than equipped, so only weapons need a slot.
            // A weapon nothing can hold is not a prize.
            if (!d.IsApparel && d.equipmentType != EquipmentType.Primary) return false;

            if (d.weaponTags != null)
            {
                for (int i = 0; i < d.weaponTags.Count; i++)
                {
                    string tag = d.weaponTags[i];
                    if (tag == null) continue;
                    if (tag.Contains("Turret") || tag.Contains("Artillery")
                        || tag.Contains("Mechanoid"))
                        return false;
                }
            }

            // ── It has to be BATTLE gear ────────────────────────────────────
            //
            // The pool is everything ForgeCompInjector claimed, and the forge
            // claims ALL apparel - which means every T-shirt, beret, bowler hat
            // and burka in the game was a legal chest prize. Measured before
            // this filter: 70% of the bronze band and 54% of the silver band
            // carried no armour and no damage at all. The chest ceremony
            // promises a weapon or a piece of armour; a beret with +6
            // refinement is a joke at the end of a six-second reel.
            //
            // Tech level was never going to catch this. It sorts by ERA, not by
            // whether a thing fights - a Spacer eltex robe and a Spacer
            // cataphract plate sit in the same band - so this is a separate
            // axis and both are needed.
            if (d.IsApparel)
            {
                // STUFFABLE ARMOUR SCORES ZERO ON THE DEF. Plate armor's
                // protection comes from its material, so ArmorRating_Sharp is 0
                // on the ThingDef and only resolves on a made Thing. Checking
                // the rating alone would throw out exactly the medieval armour
                // the bronze and silver chests exist to hand out, so the stuff
                // multiplier counts as armour in its own right.
                if (d.GetStatValueAbstract(StatDefOf.StuffEffectMultiplierArmor) < MinStuffArmorMult
                    && d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp) < MinApparelArmor
                    && d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt) < MinApparelArmor)
                    return false;
            }
            else if (!d.IsRangedWeapon)
            {
                // A ranged weapon's damage lives on its projectile, not in
                // tools, so only melee is measured here. Getting that backwards
                // once had assault rifles and charge rifles counted as junk.
                float best = 0f;
                if (d.tools != null)
                {
                    for (int i = 0; i < d.tools.Count; i++)
                        best = Mathf.Max(best, d.tools[i].power);
                }
                if (best < MinMeleePower)
                    return false;
            }
            return true;
        }

        // ── Which tech a grade draws from ───────────────────────────────────

        /// <summary>
        /// The tech band each chest grade rolls inside, inclusive on both ends
        /// and OVERLAPPING its neighbours by one step - a ladder rather than
        /// four sealed boxes, so gear keeps appearing across a grade boundary
        /// instead of the whole armoury changing at once.
        ///
        ///     bronze  F-C        Animal .. Medieval
        ///     silver  B-A        Medieval .. Industrial
        ///     gold    S-SS       Industrial .. Spacer
        ///     jade    SSS-Nation Spacer .. Archotech
        ///
        /// This is the "coherent with where it dropped" rule. Without it a
        /// bronze chest out of an F-rank gate on day three could hand over a
        /// persona monosword, and the nine rank-branded artifact weapons in
        /// IsekaiCreatures - every one of them Ultra, and labelled with the
        /// rank of the creature it belongs to - could fall out of the very
        /// first chest the player ever opened. Ultra sits in the jade band
        /// only, so those land where their own labels say they should, with no
        /// special case needed for them.
        /// </summary>
        private static readonly TechLevel[] TierTechLow =
            { TechLevel.Animal, TechLevel.Medieval, TechLevel.Industrial, TechLevel.Spacer };

        private static readonly TechLevel[] TierTechHigh =
            { TechLevel.Medieval, TechLevel.Industrial, TechLevel.Spacer, TechLevel.Archotech };

        /// <summary>
        /// Under this many eligible defs a tier widens its own band until it
        /// is viable. A player on Core alone, or one running a low-tech
        /// scenario, must not end up with a grade that cannot fill a reel.
        /// </summary>
        private const int MinTierPool = 12;

        /// <summary>Undefined tech is machinery in practice; treat it as Industrial.</summary>
        private static TechLevel TechOf(ThingDef d) =>
            d.techLevel == TechLevel.Undefined ? TechLevel.Industrial : d.techLevel;

        private static void EnsureBuilt()
        {
            if (byTier != null)
                return;

            var pool = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.comps == null)
                    continue;
                bool forgeable = false;
                for (int i = 0; i < d.comps.Count; i++)
                {
                    if (d.comps[i] is CompProperties_ForgeEnhancement) { forgeable = true; break; }
                }
                if (!forgeable)
                    continue;
                // Below this a thing is a wooden club: technically forgeable,
                // never a treasure.
                if (d.BaseMarketValue < 40f)
                    continue;
                if (!IsTreasureWorthy(d))
                    continue;
                pool.Add(d);
            }

            all = new List<Entry>(pool.Count);
            for (int i = 0; i < pool.Count; i++)
                all.Add(new Entry { def = pool[i], rarity = TreasureRarity.Common });

            byTier = new List<Entry>[4][];
            for (int t = 0; t < 4; t++)
                byTier[t] = BuildTier(pool, t);

            if (pool.Count == 0)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Treasure pool is empty - no forge-compatible items found.");
            else
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Treasure pool: {pool.Count} items -> "
                          + $"bronze {Count(0)}, silver {Count(1)}, gold {Count(2)}, jade {Count(3)}.");
        }

        private static int Count(int t)
        {
            int n = 0;
            for (int r = 0; r < 4; r++) n += byTier[t][r].Count;
            return n;
        }

        /// <summary>
        /// One grade's four rarity bands: take the tech window, widen it if it
        /// is too thin to work with, then cut p50 / p80 / p95 WITHIN it.
        /// </summary>
        private static List<Entry>[] BuildTier(List<ThingDef> pool, int t)
        {
            int lo = (int)TierTechLow[t], hi = (int)TierTechHigh[t];
            var subset = new List<ThingDef>();
            while (true)
            {
                subset.Clear();
                for (int i = 0; i < pool.Count; i++)
                {
                    int tl = (int)TechOf(pool[i]);
                    if (tl >= lo && tl <= hi) subset.Add(pool[i]);
                }
                if (subset.Count >= MinTierPool)
                    break;
                bool widened = false;
                if (lo > (int)TechLevel.Animal) { lo--; widened = true; }
                if (hi < (int)TechLevel.Archotech) { hi++; widened = true; }
                if (!widened)
                    break;                       // the whole pool is this small
            }

            var values = new List<float>(subset.Count);
            for (int i = 0; i < subset.Count; i++)
                values.Add(subset[i].BaseMarketValue);
            values.Sort();
            float P(float p) => values.Count == 0 ? 0f
                : values[Mathf.Clamp(Mathf.FloorToInt(values.Count * p), 0, values.Count - 1)];
            // WIDER AT THE TOP THAN IT LOOKS LIKE IT SHOULD BE, and this is
            // the half of the change that stops the other half backfiring.
            //
            // At p95 the Legendary band is the top TWENTIETH of a grade, and a
            // grade holds 21 to 67 defs - so Legendary meant one to four
            // items. Jade's was literally three: the Destroying, Harp and
            // Slashing Gods. Raising the Legendary weight against a band that
            // small does not make the game more interesting, it makes it more
            // repetitive, and the player learns the whole top of the table in
            // an evening.
            //
            // p45/p72/p90 roughly doubles what lives up there while keeping it
            // genuinely elite - Legendary is still the top tenth by market
            // value, which in jade is the Nation weapons and nothing else.
            float p50 = P(0.45f), p80 = P(0.72f), p95 = P(0.90f);

            var bands = new List<Entry>[4];
            for (int i = 0; i < 4; i++)
                bands[i] = new List<Entry>();
            for (int i = 0; i < subset.Count; i++)
            {
                float v = subset[i].BaseMarketValue;
                TreasureRarity r = v >= p95 ? TreasureRarity.Legendary
                                 : v >= p80 ? TreasureRarity.Epic
                                 : v >= p50 ? TreasureRarity.Rare
                                 : TreasureRarity.Common;
                bands[(int)r].Add(new Entry { def = subset[i], rarity = r });
            }

            // A band so sparse it came out empty: spill downward, so a roll can
            // always be honoured rather than handing the reel a null prize.
            for (int i = 3; i >= 1; i--)
            {
                if (bands[i].Count == 0)
                    bands[i] = bands[i - 1];
            }
            return bands;
        }

        // ── Odds and power ──────────────────────────────────────────────────

        /// <summary>
        /// Rarity weights per chest tier (Common/Rare/Epic/Legendary).
        ///
        /// A CHEST IS THE HEADLINE REWARD, and these used to argue otherwise.
        /// The old table opened 60% of bronze and 45% of silver chests on a
        /// Common, so the ceremony - a six-second reel, a colour, a name
        /// landing glyph by glyph - most often resolved to something the
        /// player would have walked past on the floor. The reel was writing a
        /// cheque the table would not cash.
        ///
        /// What the rarity actually buys, which is why moving it moves so
        /// much: MakePrize spends it FOUR times over - quality
        /// (Good/Excellent/Masterwork/Legendary), the refinement floor
        /// (2*tier-1+r), the rune chance (0.10 + 0.05*tier + 0.15*r) and the
        /// maximum rune rank (tier+r-1). One step of rarity is a better item,
        /// better made, better refined and more likely to be runed.
        ///
        ///             Epic or better      was      now
        ///             T1 bronze           12%      30%
        ///             T2 silver           23%      45%
        ///             T3 gold             35%      62%
        ///             T4 jade             55%      80%
        ///
        /// The shape still climbs - the modal outcome walks from Rare at
        /// bronze to Legendary at jade - so the grades stay distinguishable
        /// and clearing deeper still means something. Common is deliberately
        /// not gone at the low end: a band with no floor has no ceiling
        /// either, and bronze needs somewhere to disappoint from.
        ///
        /// Defaults, per grade (settings, Advanced tab), each weight then
        /// scaled by its rarity's multiplier (Rewards tab):
        ///   T1  (F-C)         33 36 23  8   modal Rare
        ///   T2  (B-A)         22 33 30 15   modal Rare
        ///   T3  (S-SS)        12 26 37 25   modal Epic
        ///   T4  (SSS-Nation)   4 16 38 42   modal Legendary
        /// </summary>
        private static float[] TierWeights(int tier)
        {
            IsekaiDungeonsSettings s = IsekaiDungeonsSettings.Current;
            switch (Mathf.Clamp(tier, 1, 4))
            {
                case 1: return s.rarityWeightsBronze;
                case 2: return s.rarityWeightsSilver;
                case 3: return s.rarityWeightsGold;
                default: return s.rarityWeightsJade;
            }
        }

        public static TreasureRarity RollRarity(int tier)
        {
            IsekaiDungeonsSettings s = IsekaiDungeonsSettings.Current;
            float[] table = TierWeights(tier);
            float[] scale = { s.chestCommonWeight, s.chestRareWeight, s.chestEpicWeight, s.chestLegendaryWeight };
            var w = new float[4];
            float total = 0f;
            for (int i = 0; i < 4; i++)
            {
                w[i] = Mathf.Max(0f, (i < table.Length ? table[i] : 0f) * scale[i]);
                total += w[i];
            }
            // Every weight zeroed by hand still has to open to something.
            if (total <= 0f)
                return TreasureRarity.Common;

            float roll = Rand.Value * total;
            for (int i = 0; i < 4; i++)
            {
                if (roll < w[i]) return (TreasureRarity)i;
                roll -= w[i];
            }
            return TreasureRarity.Common;
        }

        /// <summary>
        /// A prize from one grade's band. The rarity-only overload is gone on
        /// purpose: with per-tier bands a rarity no longer identifies a list,
        /// and an overload that silently picked bronze's would have been a
        /// quiet way to reintroduce exactly the bug this replaced.
        /// </summary>
        public static Entry RollEntry(int tier, TreasureRarity rarity)
        {
            EnsureBuilt();
            List<Entry> band = byTier[Mathf.Clamp(tier - 1, 0, 3)][(int)rarity];
            return band.Count > 0 ? band[Rand.Range(0, band.Count)] : default;
        }

        public static Entry RollEntry(int tier) => RollEntry(tier, RollRarity(tier));

        /// <summary>
        /// Builds the actual prize: the thing, its stuff, a quality that tracks
        /// the rarity, and the forge enhancement the chest exists to deliver.
        ///
        /// Refinement and rune power come from BOTH axes - the chest's tier
        /// sets the floor of the range, the rolled rarity climbs within it -
        /// so a T4 common still beats a T1 legendary on refinement floor,
        /// which is what makes climbing worth it.
        /// </summary>
        public static Thing MakePrize(Entry entry, int tier)
        {
            if (entry.def == null)
                return null;
            tier = Mathf.Clamp(tier, 1, 4);
            int r = (int)entry.rarity;

            ThingDef stuff = GenStuff.RandomStuffByCommonalityFor(entry.def);
            Thing item = ThingMaker.MakeThing(entry.def, stuff);

            var quality = item.TryGetComp<CompQuality>();
            if (quality != null)
            {
                QualityCategory q = r switch
                {
                    0 => QualityCategory.Good,
                    1 => QualityCategory.Excellent,
                    2 => QualityCategory.Masterwork,
                    _ => QualityCategory.Legendary,
                };
                quality.SetQuality(q, ArtGenerationContext.Colony);
            }

            int minRef = Mathf.Clamp(2 * tier - 1 + r, 1, 10);       // T1: 1-4, T4: 7-10
            int maxRef = Mathf.Clamp(minRef + 2, 1, 10);
            float runeChance = Mathf.Clamp01(0.10f + 0.05f * tier + 0.15f * r);
            int maxRuneRank = Mathf.Clamp(tier + r - 1, 1, 5);

            ForgeItemGenerator.TryApplyRandomEnhancement(item, maxRef, runeChance, maxRuneRank, minRef);
            return item;
        }
    }

    /// <summary>
    /// The opening ceremony: a horizontal reel of possible prizes that spins,
    /// decelerates and lands on the one the chest already decided to give.
    ///
    /// THE ANIMATION IS ANALYTIC - a pure function of elapsed real time via
    /// IsekaiAnimations.Now - because OnGUI runs several times per rendered
    /// frame and anything that integrated state forward would spin at a speed
    /// set by mouse movement (the house rule from the title card). Sounds are
    /// the one side effect, and they fire only on the Repaint pass, which runs
    /// once per frame.
    ///
    /// THE WINNER IS PRE-ROLLED IN THE CONSTRUCTOR. The reel is theatre over a
    /// decision already made, which is exactly how the crates it imitates
    /// work - and it means closing the window early loses nothing: OnClosed
    /// awards whatever was rolled if Take was never clicked.
    /// </summary>
    public class Window_TreasureReel : Window
    {
        private readonly TreasureChest chest;
        private readonly int tier;

        // The strip. entries[WinnerIndex] is the prize.
        private const int StripLength = 48;
        private const int WinnerIndex = 42;
        private readonly List<TreasurePool.Entry> strip = new List<TreasurePool.Entry>(StripLength);

        // Motion: the reel position in slots, eased from StartSlot to
        // WinnerIndex + jitter over SpinSeconds. Jitter keeps the landing off
        // dead centre the way a real wheel stops, without changing the winner.
        // Read once when the window opens: a settings change must not stretch
        // a reel that is already spinning.
        private readonly float SpinSeconds = Mathf.Max(1f, IsekaiDungeonsSettings.Current.reelSpinSeconds);
        private const float StartSlot = 2.5f;
        private readonly float jitter;
        private readonly float startTime;

        private Thing prize;          // materialised on first need, awarded once
        private bool awarded;
        private int lastTickedSlot = -1;

        /// <summary>
        /// When the dismissal started, or -1 while the window is still live.
        ///
        /// TAKE NO LONGER CLOSES THE WINDOW. It awards, starts this clock, and
        /// lets DoWindowContents keep drawing for ExitSeconds; the actual Close
        /// happens when the animation finishes. Closing on the click is what
        /// made a six-second ceremony vanish in a single frame.
        /// </summary>
        private float exitStart = -1f;

        private bool Exiting => exitStart >= 0f;

        /// <summary>0 as the dismissal begins, 1 when it is over.</summary>
        private float ExitT => Exiting
            ? Mathf.Clamp01((IsekaiAnimations.Now - exitStart) / TreasureReelArt.ExitSeconds)
            : 0f;

        /// <summary>
        /// Multiplies the alpha of everything this window draws.
        ///
        /// EVERY GUI.color WRITE IN THIS FILE GOES THROUGH Tint(), and that is
        /// not stylistic. The window resets to Color.white in four places, and
        /// any one of them lands after a global fade was set and wipes it - the
        /// exact trap DungeonTitleCard warns about. Routing them through one
        /// helper is what makes a single fade value reach the whole ceremony.
        /// </summary>
        private float ExitAlpha => Exiting ? 1f - Tween.EaseInCubic(ExitT) : 1f;

        private void Tint(float a = 1f) => GUI.color = new Color(1f, 1f, 1f, a * ExitAlpha);

        /// <summary>Same job for the colours passed straight to DrawBoxSolid.</summary>
        private Color Fade(Color c) => new Color(c.r, c.g, c.b, c.a * ExitAlpha);

        // Built once each, not per frame: Build measures cumulative prefixes to
        // keep kerning, which is real work.
        private AnimatedTextRun titleRun;
        private AnimatedTextRun resultRun;
        private string resultRunFor;

        /// <summary>Seconds since the reel settled, or -1 while it is still spinning.</summary>
        private float SinceSettled => Settled ? Elapsed - SpinSeconds : -1f;

        private static readonly TextReveal TitleReveal =
            TextReveal.Make(delay: 0.20f, stagger: 0.030f, rise: 12f, scaleFrom: 0.6f,
                            omega: 19f, zeta: 0.55f, fade: 0.16f);

        // Slower and heavier than the title: this is the payoff line, and it
        // should feel like it lands rather than flicks on.
        private static readonly TextReveal ResultReveal =
            TextReveal.Make(delay: 0.16f, stagger: 0.026f, rise: 18f, scaleFrom: 0.72f,
                            omega: 16f, zeta: 0.44f, fade: 0.20f);

        public override Vector2 InitialSize =>
            new Vector2(TreasureReelArt.WindowW, TreasureReelArt.WindowH);

        public Window_TreasureReel(TreasureChest chest, Pawn opener)
        {
            this.chest = chest;
            tier = chest?.Tier ?? 1;

            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            doCloseX = false;                 // the reel finishes; Take (or Esc) closes
            closeOnCancel = true;             // Esc still awards - see OnClosed
            layer = WindowLayer.Dialog;

            // WINDOWLESS ON PURPOSE. The ceremony floats over the game rather
            // than boxing it out - the art carries its own silhouettes (track
            // chevrons, ribbon, plate), so a grey dialog behind them was pure
            // occlusion. absorbInputAroundWindow above still eats stray clicks.
            doWindowBackground = false;
            drawShadow = false;

            // THE ROLL. Winner first, then fillers drawn from the same odds so
            // the strip is an honest picture of the distribution it came from.
            TreasurePool.Entry winner = TreasurePool.RollEntry(tier);
            for (int i = 0; i < StripLength; i++)
                strip.Add(i == WinnerIndex ? winner : TreasurePool.RollEntry(tier));

            jitter = Rand.Range(-0.34f, 0.34f);
            startTime = IsekaiAnimations.Now;

            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }

        private float Elapsed => IsekaiAnimations.Now - startTime;
        private bool Settled => Elapsed >= SpinSeconds;

        /// <summary>Reel position in slot units - cubic ease-out, analytic.</summary>
        private float PosAt(float t)
        {
            float u = Mathf.Clamp01(t / SpinSeconds);
            float ease = 1f - Mathf.Pow(1f - u, 3f);
            return Mathf.Lerp(StartSlot, WinnerIndex + jitter, ease);
        }

        /// <summary>
        /// Wraps the whole ceremony in the dismissal transform, then restores
        /// the matrix and colour on EVERY path - including the early returns
        /// below - or the skew and the tint leak into unrelated UI drawn later
        /// in the same frame.
        /// </summary>
        public override void DoWindowContents(Rect inRect)
        {
            if (Exiting && ExitT >= 1f)
            {
                Close(false);
                return;
            }

            Matrix4x4 baseMatrix = GUI.matrix;
            Color baseColor = GUI.color;
            if (Exiting)
            {
                float s = Mathf.Lerp(1f, TreasureReelArt.ExitScaleTo, Tween.EaseInCubic(ExitT));
                GUIUtility.ScaleAroundPivot(new Vector2(s, s),
                    new Vector2(inRect.width / 2f, inRect.height / 2f));
            }
            Tint();

            try
            {
                DrawCeremony(inRect);
            }
            finally
            {
                GUI.matrix = baseMatrix;
                GUI.color = baseColor;
            }
        }

        private void DrawCeremony(Rect inRect)
        {
            DrawTitle(inRect);

            var trackRect = new Rect((inRect.width - TreasureReelArt.TrackW) / 2f,
                TreasureReelArt.TrackTop, TreasureReelArt.TrackW, TreasureReelArt.TrackH);
            DrawReel(trackRect);

            // ── After the landing ───────────────────────────────────────────
            if (!Settled)
                return;

            DrawResult(inRect);

            // The button waits for the plate to stop moving. Offering the exit
            // mid-flourish invites the player to click through the payoff they
            // are here for, and a button that fades in under the cursor is a
            // misclick waiting to happen - so it is not drawn AT ALL until it
            // is fully solid.
            float btnFade = Mathf.Clamp01(
                (SinceSettled - TreasureReelArt.ButtonDelay) / TreasureReelArt.ButtonFadeSeconds);
            if (btnFade <= 0.999f)
                return;

            var btn = new Rect((inRect.width - TreasureReelArt.ButtonW) / 2f,
                inRect.height - TreasureReelArt.ButtonH - 4f,
                TreasureReelArt.ButtonW, TreasureReelArt.ButtonH);
            // Tinted like everything else so it recedes with the window rather
            // than sitting there at full strength while the ceremony shrinks
            // behind it. The click is ignored once the dismissal has started -
            // DoAward guards itself too, but a button that still responds while
            // visibly leaving is its own small lie.
            Tint();
            bool take = Widgets.ButtonText(btn, "IsekaiDungeons.Treasure.Take".Translate());
            Tint();
            if (take && !Exiting)
            {
                DoAward();
                exitStart = IsekaiAnimations.Now;
            }
        }

        /// <summary>
        /// The ribbon drops in on a spring and the name arrives glyph by glyph,
        /// the same reveal the dungeon title cards use.
        /// </summary>
        private void DrawTitle(Rect inRect)
        {
            float t = Elapsed;
            float land = Tween.Spring01(t, TreasureReelArt.BannerOmega, TreasureReelArt.BannerZeta);

            Rect banner = TreasureReelArt.BannerRect(inRect.width);
            banner.y -= (1f - land) * TreasureReelArt.BannerDropPx;

            if (TreasureReelArt.TitleBanner != null)
            {
                Tint(Mathf.Clamp01(land * 1.6f));
                GUI.DrawTexture(banner, TreasureReelArt.TitleBanner);
                Tint();
            }

            string title = TreasurePool.ChestName(tier);
            if (titleRun == null)
            {
                titleRun = AnimatedTextRun.Build(title, DungeonFonts.Display,
                    TreasureReelArt.TitlePx, TreasureReelArt.TitlePx / 2,
                    TextGranularity.Character, TreasureReelArt.BannerW * 0.82f, 1, 1.1f);
            }

            float baseline = TreasureReelArt.TitleBaseline(
                banner, DungeonFonts.Display.AscentPx(TreasureReelArt.TitlePx));

            // Ribbon text is white and stays white - the house rule from the
            // title card; the rarity signal belongs to the result plate below.
            if (titleRun != null && titleRun.Valid)
                titleRun.Draw(inRect.width / 2f, baseline, t, TitleReveal,
                              Color.white, ExitAlpha, Color.clear, Vector2.zero);
        }

        /// <summary>
        /// The landing, on its plate: the item's name in Americaine, in the
        /// colour of the rarity it rolled. The pixel size steps down until the
        /// name fits between the plate's rune ends - a Legendary persona
        /// longsword's full label is a paragraph, and clipping it would cut
        /// exactly the words the player is here to read.
        /// </summary>
        /// <summary>
        /// The payoff: the plate springs open from a squash, and the item's
        /// name lands glyph by glyph in the rolled rarity's colour.
        /// </summary>
        private void DrawResult(Rect inRect)
        {
            float t = SinceSettled;
            float pop = Tween.Spring01(t - TreasureReelArt.ResultDelay,
                                       TreasureReelArt.ResultOmega, TreasureReelArt.ResultZeta);
            float scale = Mathf.Lerp(TreasureReelArt.ResultScaleFrom, 1f, pop);

            var plate = new Rect((inRect.width - TreasureReelArt.ResultW) / 2f,
                TreasureReelArt.ResultTop, TreasureReelArt.ResultW, TreasureReelArt.ResultH);
            Rect drawn = TreasureReelArt.ScaledAboutCentre(plate, scale);

            if (TreasureReelArt.ResultBG != null)
            {
                Tint(Mathf.Clamp01(pop * 2f));
                GUI.DrawTexture(drawn, TreasureReelArt.ResultBG);
                Tint();
            }
            else
            {
                Widgets.DrawBoxSolid(drawn, Fade(new Color(0.10f, 0.10f, 0.12f, 0.85f)));
            }

            TreasurePool.Entry win = strip[WinnerIndex];
            string label = (Prize?.LabelCap ?? win.def.LabelCap).ToString();

            var face = DungeonFonts.Display;
            int px = TreasureReelArt.ResultPx;
            while (px > TreasureReelArt.ResultMinPx
                   && face.Measure(label, px) > TreasureReelArt.ResultTextMaxW)
                px--;

            // Rebuilt only when the label itself changes.
            if (resultRun == null || resultRunFor != label)
            {
                resultRunFor = label;
                resultRun = AnimatedTextRun.Build(label, face, px, TreasureReelArt.ResultMinPx,
                    TextGranularity.Character, TreasureReelArt.ResultTextMaxW, 1, 1.1f);
            }

            // The text rides the settled plate, not the springing one: glyphs
            // animating on a surface that is itself still moving reads as a
            // wobble rather than as two things arriving.
            float baseline = TreasureReelArt.ResultBaseline(plate, face.AscentPx(px));
            if (resultRun != null && resultRun.Valid)
            {
                resultRun.Draw(inRect.width / 2f, baseline, t, ResultReveal,
                               TreasurePool.RarityColor[(int)win.rarity], ExitAlpha,
                               new Color(0f, 0f, 0f, 0.5f), new Vector2(0f, 2f));
            }
        }

        private void DrawReel(Rect rect)
        {
            float rise = Tween.Spring01(Elapsed - TreasureReelArt.TrackDelay,
                                        TreasureReelArt.TrackOmega, TreasureReelArt.TrackZeta);
            rect.y += (1f - rise) * TreasureReelArt.TrackRisePx;

            if (TreasureReelArt.Track != null)
                GUI.DrawTexture(rect, TreasureReelArt.Track);
            else
                Widgets.DrawBoxSolid(rect, Fade(new Color(0.10f, 0.10f, 0.12f, 0.9f)));

            float pos = PosAt(Elapsed);

            // One pass-tick per slot crossed, Repaint-gated so the multipass
            // OnGUI cannot double-fire it.
            if (Event.current.type == EventType.Repaint)
            {
                int slot = Mathf.FloorToInt(pos);
                if (slot != lastTickedSlot)
                {
                    lastTickedSlot = slot;
                    if (!Settled)
                        SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    else
                        SoundDefOf.Quest_Succeded.PlayOneShotOnCamera();
                }
            }

            // CARDS CLIP TO THE TRACK'S INNER SPAN, not its full rect: the
            // chevron ends are transparent, and a card sliding "past" them onto
            // the game world below broke the illusion that the track contains
            // the reel.
            var inner = new Rect(rect.x + TreasureReelArt.TrackInnerInset, rect.y,
                rect.width - 2f * TreasureReelArt.TrackInnerInset, rect.height);
            float cx = rect.width / 2f;   // pointer centre, in TRACK space

            // Cards fade up with the rail rather than sliding on their own:
            // they are already in motion horizontally, and a second travel axis
            // on top of the spin reads as a glitch.
            float cardAlpha = Mathf.Clamp01(
                (Elapsed - TreasureReelArt.CardsFadeDelay) / TreasureReelArt.CardsFadeSeconds);

            GUI.BeginGroup(inner);
            float groupCx = cx - TreasureReelArt.TrackInnerInset;
            float cardTop = TreasureReelArt.CardTop;
            for (int i = 0; i < StripLength; i++)
            {
                float x = groupCx + (i - pos) * TreasureReelArt.SlotPitch - TreasureReelArt.CardW / 2f;
                if (x + TreasureReelArt.CardW < -TreasureReelArt.CardW
                    || x > inner.width + TreasureReelArt.CardW)
                    continue;

                var card = new Rect(x, cardTop, TreasureReelArt.CardW, TreasureReelArt.CardH);
                int r = (int)strip[i].rarity;

                // The winner swells once when it lands - one spring pulse,
                // about its own centre, so the eye is pulled to the card the
                // pointer stopped on before the plate below has finished.
                if (Settled && i == WinnerIndex)
                {
                    float pulse = 1f + TreasureReelArt.WinPulseAmount
                        * (1f - Tween.Spring01(SinceSettled,
                               TreasureReelArt.WinPulseOmega, TreasureReelArt.WinPulseZeta));
                    card = TreasureReelArt.ScaledAboutCentre(card, pulse);
                }

                Tint(cardAlpha);
                Texture2D art = TreasureReelArt.Cards[r];
                if (art != null)
                {
                    GUI.DrawTexture(card, art);
                }
                else
                {
                    Widgets.DrawBoxSolid(card, Fade(new Color(0.17f, 0.17f, 0.20f)));
                    Widgets.DrawBoxSolid(new Rect(card.x, card.yMax - 7f, card.width, 7f),
                        Fade(TreasurePool.RarityColor[r]));
                }
                Tint();

                if (strip[i].def != null)
                {
                    var icon = new Rect(card.x + TreasureReelArt.CardIconSide,
                        card.y + TreasureReelArt.CardIconTop,
                        card.width - 2f * TreasureReelArt.CardIconSide,
                        card.height - TreasureReelArt.CardIconTop - TreasureReelArt.CardIconBottom);
                    Widgets.DefIcon(icon, strip[i].def);
                }
            }
            GUI.EndGroup();

            // The pointer, over the cards, under the track's own spark cluster.
            var ptr = new Rect(rect.x + cx - TreasureReelArt.PointerW / 2f,
                rect.y + TreasureReelArt.PointerTop,
                TreasureReelArt.PointerW, TreasureReelArt.PointerH);
            if (TreasureReelArt.Pointer != null)
            {
                Tint(Settled ? 0.4f : 1f);
                GUI.DrawTexture(ptr, TreasureReelArt.Pointer);
                Tint();
            }
            else
            {
                Widgets.DrawBoxSolid(ptr, Fade(new Color(1f, 0.9f, 0.3f, Settled ? 0.35f : 0.9f)));
            }
        }

        // ── The award ───────────────────────────────────────────────────────

        private Thing Prize => prize ?? (prize = TreasurePool.MakePrize(strip[WinnerIndex], tier));

        private void DoAward()
        {
            if (awarded)
                return;
            awarded = true;
            chest?.Award(Prize);
        }

        /// <summary>
        /// A closed reel still pays out. The roll happened in the constructor;
        /// letting Esc discard it would punish a misclick with a prize.
        /// </summary>
        public override void PostClose()
        {
            base.PostClose();
            DoAward();
        }
    }
}
