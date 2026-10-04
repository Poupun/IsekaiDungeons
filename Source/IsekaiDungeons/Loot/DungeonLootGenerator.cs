using System;
using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.Forge;
using IsekaiLeveling.MobRanking;
using IsekaiLeveling.Quests;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Builds the item piles a dungeon pays out: the big clear pile in the boss
    /// room and the small treasure-side-room piles.
    ///
    /// THE PILE IS TREASURE, NOT A SUPPLY DROP. This ladder used to mirror the
    /// base mod's quest reward generator so a clear "felt like a guild contract
    /// payout, just bigger" — food always, medicine D+, components C+, raw
    /// materials D+, and gear only as a chance roll from C/B upward. That was
    /// the wrong target. A guild contract is someone paying you in supplies; a
    /// dungeon is a hoard you take off a dead god. Opening the boss room to
    /// pemmican, herbal medicine, and four hundred steel reads as the former.
    ///
    /// So the categories are now the ones an RPG dungeon actually pays:
    ///
    ///   GEAR    — weapons and armour, GUARANTEED at every rank (never a
    ///             chance roll), quality-rolled and forge-enhanced, as many
    ///             pieces as GearShare of the budget buys up to a rank cap.
    ///             This is the headline; everything else is trim.
    ///   RUNES   — every rank now, not B+. The socketable progression drop.
    ///   COIN    — a purse at every rank, reserved so the gear above cannot
    ///             crowd it out of the early ranks.
    ///   RELICS  — the curated vanilla/DLC treasure tables (DungeonRelicTables):
    ///             archotech cores, psylink, persona weapons, eltex, subcores,
    ///             genepacks, Anomaly relics. Which of them a given rank may
    ///             pay is decided BY THEIR MARKET VALUE against this dungeon's
    ///             own budget, not by a hand-written rank column.
    ///   ELIXIRS — A+ only, and only the real ones (go-juice, luciferium).
    ///             The old ladder's low tier handed out beer.
    ///   HOARD   — mana cores, gold, silver, in descending denominations,
    ///             spending whatever the categories above did not.
    ///   CORES   — the guaranteed rank payout, on top of the budget.
    ///
    /// Food, medicine, industrial/spacer components and raw materials are
    /// GONE from both piles, deliberately. They are not a reward, they are
    /// logistics, and the colony already has a supply chain for them.
    ///
    /// The gear pools, quality windows and forge enhancement still follow the
    /// base mod's own tables, reimplemented STANDALONE (that generator is
    /// private and we do not reflect into privates) — so a dungeon weapon is
    /// built like a quest weapon even though the pile around it is not.
    ///
    /// Other differences from the base generator, all deliberate:
    ///  - The value target comes from DungeonKindDef.lootValueByRank. That XML
    ///    curve ALREADY includes the 1.5x dungeon premium documented by
    ///    DungeonTunables.LootValueMultiplier — do NOT multiply again here.
    ///    (The tunable is only consulted by the hardcoded fallback ladder used
    ///    when the curve is missing.)
    ///  - EVERY generated item's market value is subtracted from the running
    ///    budget (the base mod forgets valuables/drugs/runes), and whatever is
    ///    left over is topped up with silver — so the pile reliably lands near
    ///    the target instead of undershooting when the chance-gated categories
    ///    miss their rolls.
    ///  - Guaranteed mana cores are appended ON TOP of the target value, never
    ///    counted against it: cores are the progression currency the dungeon
    ///    promises (ARCHITECTURE §8), not part of the market-value budget.
    ///  - The base mod's chance-rolled S+ reinforcement cores and A+ trait
    ///    artifacts are intentionally NOT mirrored: the guaranteed core table
    ///    below supersedes the former, and trait artifacts stay exclusive to
    ///    the quest board so dungeons don't power-creep trait acquisition.
    ///
    /// Randomness: plain Rand is used throughout WITHOUT Rand.PushState.
    ///  - GenerateTreasureLoot is called from GenStep_DungeonPopulation, which
    ///    runs inside GenStep.Generate — vanilla's MapGenerator already brackets
    ///    the whole GenStep pipeline in a seeded Rand state.
    ///  - GenerateClearLoot is called at play time (DungeonMapComponent clear
    ///    detection), where determinism is not required.
    ///
    /// Allocation: this is generation-time code (one call per dungeon clear /
    /// per treasure room at map gen) — allocating freely and scanning
    /// DefDatabase is fine here; nothing in this class runs per tick.
    ///
    /// Minification: the RELICS category is the one that can produce buildings
    /// (an infinite chemreactor, an animus stone), and DungeonRelicTables wraps
    /// those with MinifyUtility before handing them over, so everything this
    /// class returns is a plain haulable Thing. Every other category is
    /// items/equipment, where ThingMaker.MakeThing plus CompQuality.SetQuality
    /// covers all comp setup.
    /// </summary>
    public static class DungeonLootGenerator
    {
        /// <summary>Remaining-budget floor below which we stop adding filler silver.</summary>
        private const float FillerCutoff = 60f;

        /// <summary>
        /// Share of the clear target spent on weapons and armour.
        ///
        /// Over half, because gear IS the reward — everything under this line
        /// is what the hoard looks like once the interesting part is decided.
        /// It is a stopping condition rather than a cap: the two guaranteed
        /// pieces are minted before it is consulted, so a rank-F gate still
        /// pays a weapon and a set of armour even though 55% of 270 silver
        /// does not strictly buy them.
        ///
        /// Raising the quality windows does NOT inflate the payout, because
        /// this is measured in market value: better pieces simply mean fewer
        /// of them. Count trades against quality automatically. A setting.
        /// </summary>
        private static float GearShare => IsekaiDungeonsSettings.Current.gearShare;

        /// <summary>Chance a treasure side-room holds a piece of gear rather than only coin. A setting.</summary>
        private static float TreasureGearChance => IsekaiDungeonsSettings.Current.treasureGearChance;

        /// <summary>Chance an A-rank-or-better clear adds elixirs. A setting.</summary>
        private static float ElixirChance => IsekaiDungeonsSettings.Current.elixirChance;

        // ── Value scale ─────────────────────────────────────────────────────

        /// <summary>The loot multiplier for the call in progress, or negative when no caller set one.</summary>
        private static float valueScale = -1f;

        /// <summary>
        /// Runs a loot call with a value multiplier in force: the global loot
        /// setting, times the tower or deep-dungeon one for maps that belong to
        /// them (DungeonTunables.LootMultiplierFor).
        ///
        /// A SCOPE RATHER THAN A PARAMETER because DungeonLootWorker is a public
        /// extension point with a frozen signature. A third-party worker that
        /// delegates here gets the player's multiplier for free, and one that
        /// pays in its own currency is left alone, which is the right answer for
        /// a number this code cannot interpret. Scopes nest and restore.
        /// Guaranteed mana cores are never scaled.
        /// </summary>
        public static List<Thing> WithValueScale(float scale, Func<List<Thing>> generate)
        {
            float previous = valueScale;
            valueScale = Mathf.Max(0f, scale);
            try
            {
                return generate();
            }
            finally
            {
                valueScale = previous;
            }
        }

        private static float ValueScale =>
            valueScale >= 0f ? valueScale : IsekaiDungeonsSettings.Current.globalLootMultiplier;

        /// <summary>
        /// Share of the clear target paid as loose coin, at every rank.
        ///
        /// Separate from the leftover hoard on purpose. The hoard only exists
        /// when the categories above underspend, which at F-D they never do —
        /// two guaranteed pieces of gear and a rune already exhaust a 270
        /// budget. Without this slice the early ranks would pay no money at
        /// all, and "you opened the boss room and there was coin in it" is not
        /// a detail a dungeon can skip. A setting.
        /// </summary>
        private static float CoinShare => IsekaiDungeonsSettings.Current.coinShare;

        /// <summary>
        /// Rune defs bucketed by rune rank (index 0 = rank I … 4 = rank V),
        /// discovered once by defName prefix scan. Def identity is stable for
        /// the process lifetime, so a static cache is safe (same convention as
        /// the spawn providers). The base mod ships 17 rune families x 5 ranks
        /// under the "Isekai_Rune_" prefix; scanning instead of hardcoding
        /// names keeps us correct when families are added or removed.
        /// </summary>
        private static List<ThingDef>[] runeBuckets;

        // ────────────────────────────────────────────────────────────────────
        //  Public API (frozen contract)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The boss-room payout for a cleared dungeon of the given rank.
        /// Target market value = def.lootValueByRank.Evaluate((int)rank);
        /// guaranteed mana cores are appended on top of that value.
        /// </summary>
        /// <param name="rankIndex">
        /// The continuous ladder position; -1 uses the enum, which is what
        /// every ordinary dungeon does. A tower floor passes a fraction so its
        /// payout climbs smoothly instead of jumping once every ten floors.
        /// </param>
        public static List<Thing> GenerateClearLoot(MobRankTier rank, DungeonKindDef def, float rankIndex = -1f)
        {
            List<Thing> loot = new List<Thing>();

            float target = TargetClearValue(rank, def, rankIndex);
            float remaining = target;

            // Gear pools, quality windows and forge tables live in QuestRank
            // space because they are the base mod's own tables. Nation
            // collapses to SSS there — but the value target above, the gear
            // piece cap and the rune ladder all read the full MobRankTier, so
            // a Nation gate still out-pays an SSS one.
            QuestRank q = DungeonRankUtil.ToQuestRank(rank);
            TechLevel maxTech = MaxTechLevel();

            // === GEAR (every rank, guaranteed, the headline of the pile) ===
            remaining -= AddGearHaul(loot, rank, q, maxTech, target * GearShare);

            // === RUNES (every rank) ===
            remaining -= AddRunes(loot, rank);

            // === COIN (every rank, never crowded out by the gear above) ===
            remaining -= AddCoinPurse(loot, target * CoinShare, q);

            // === RELICS (rank-gated by their own market value) ===
            remaining -= AddRelics(loot, rank, target);

            // === ELIXIRS (A+, 35%) ===
            if ((int)q >= (int)QuestRank.A && Rand.Chance(ElixirChance))
                remaining -= AddElixirs(loot, q, maxTech);

            // === THE HOARD (mana cores, gold, silver) ===
            // Whatever the categories above did not spend becomes coin and
            // cores rather than being quietly dropped, so the pile reliably
            // lands near its target instead of undershooting.
            if (remaining > FillerCutoff)
                remaining = AddFillerHoard(loot, remaining);

            // === GUARANTEED MANA CORES (on top, never against the target) ===
            AddGuaranteedCores(loot, rank);

            // === GATE SCROLL (on top, chance) — the way back to this rank ===
            GateScrolls.TryAdd(loot, rank, IsekaiDungeonsSettings.Current.gateScrollClearChance);

            return loot;
        }

        /// <summary>
        /// The small pile a treasure side-room holds: TreasureLootFraction of
        /// the clear value. A CHEST, not a warehouse — the odd piece of gear,
        /// a rune, and coin, with one small mana core sweetener so every
        /// treasure room pays at least a token of progression currency.
        ///
        /// It deliberately reads as a lesser version of the boss room rather
        /// than a different kind of reward: the same gear pools and the same
        /// rune buckets, one rank band shallower, so finding a side room feels
        /// like finding a piece of the hoard early. The raw-materials half of
        /// the old split is gone for the reason the clear pile lost it — a
        /// chest full of steel is not treasure.
        /// </summary>
        public static List<Thing> GenerateTreasureLoot(MobRankTier rank, DungeonKindDef def, float rankIndex = -1f)
        {
            List<Thing> loot = new List<Thing>();

            float budget = TargetClearValue(rank, def, rankIndex) * DungeonTunables.TreasureLootFraction;
            QuestRank q = DungeonRankUtil.ToQuestRank(rank);
            TechLevel maxTech = MaxTechLevel();

            // A single piece of gear, sometimes — the "you opened the chest and
            // there was a sword in it" moment. Chance-gated on purpose: a
            // guaranteed piece in every side room would out-supply the boss.
            if (Rand.Chance(TreasureGearChance))
            {
                if (Rand.Bool)
                    budget -= MakeWeapon(loot, BuildWeaponPool(q, maxTech), q, maxTech);
                else
                    budget -= MakeArmor(loot, BuildArmorPool(q, maxTech), q);
            }

            // One rune, from C up, drawn a band below the clear ladder.
            if ((int)rank >= (int)MobRankTier.C)
                budget -= AddRuneDraw(loot, Mathf.Max(1, RuneMaxRankFor(rank) - 1));

            // Whatever is left becomes coin — the stash identity of the room.
            ThingDef valuableDef = TreasureValuableFor(q);
            if (budget > 0f && valuableDef != null)
            {
                int count = Mathf.Max(1, Mathf.RoundToInt(
                    budget / Mathf.Max(0.01f, valuableDef.BaseMarketValue)));
                AddStacks(loot, valuableDef, Mathf.Min(count, FillerCap(valuableDef)));
            }

            // Sweetener: exactly one small core regardless of rank.
            AddCoreStack(loot, "Isekai_SmallManaCore", 1);

            // Rarely, a gate scroll of the dungeon's own rank.
            GateScrolls.TryAdd(loot, rank, IsekaiDungeonsSettings.Current.gateScrollTreasureChance);

            return loot;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Value target
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the clear-loot market-value target. Normal path is the
        /// def's lootValueByRank curve (which already bakes in the 1.5x
        /// premium). Null def / null curve fall back loudly (G5 — never a
        /// silent no-op) to the BreakDungeon def, then to a hardcoded ladder.
        /// </summary>
        /// <summary>Fraction of the remaining budget one core denomination may take, so the hoard stays mixed.</summary>
        private const float CoreFillerShare = 0.55f;

        /// <summary>Share of the leftover budget paid in mana cores; the rest becomes gold.</summary>
        private const float FillerCoreFraction = 0.5f;

        /// <summary>
        /// Hard cap on any single filler item, in FULL STACKS, so a
        /// pathological curve cannot bury the room. It used to be a flat 400
        /// units, which is 400 silver or 4,000 silver of gold: at SSS and
        /// Nation the gold and silver steps hit it and the rest of the value
        /// target was silently dropped.
        /// </summary>
        private const int MaxFillerStacks = 8;

        private static int FillerCap(ThingDef def) => MaxFillerStacks * Mathf.Max(1, def.stackLimit);

        private static float TargetClearValue(MobRankTier rank, DungeonKindDef def, float rankIndex = -1f)
        {
            DungeonKindDef resolved = def;
            if (resolved == null)
            {
                Log.Warning("[IsekaiDungeons] DungeonLootGenerator called with null DungeonKindDef — falling back to IsekaiDungeons_BreakDungeon.");
                resolved = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            }

            SimpleCurve curve = resolved?.lootValueByRank;
            if (curve != null)
                return Mathf.Max(0f, curve.Evaluate(rankIndex >= 0f ? rankIndex : (int)rank)) * ValueScale;

            Log.Warning($"[IsekaiDungeons] DungeonKindDef '{resolved?.defName ?? "null"}' has no lootValueByRank curve — using hardcoded fallback loot values.");
            return FallbackClearValue(rank) * ValueScale;
        }

        /// <summary>
        /// Emergency ladder if the XML curve is missing: the base mod's quest
        /// loot values times LootValueMultiplier. This is the ONLY place the
        /// multiplier is applied in code — the real curve carries the premium
        /// in its XML values (the tunable documents it; applying both would
        /// double-dip).
        /// </summary>
        private static float FallbackClearValue(MobRankTier rank)
        {
            float baseValue;
            switch (rank)
            {
                case MobRankTier.F: baseValue = 100f; break;
                case MobRankTier.E: baseValue = 200f; break;
                case MobRankTier.D: baseValue = 400f; break;
                case MobRankTier.C: baseValue = 800f; break;
                case MobRankTier.B: baseValue = 1600f; break;
                case MobRankTier.A: baseValue = 3200f; break;
                case MobRankTier.S: baseValue = 6400f; break;
                case MobRankTier.SS: baseValue = 12800f; break;
                case MobRankTier.SSS: baseValue = 25600f; break;
                case MobRankTier.Nation: baseValue = 51200f; break; // no quest analogue; keep doubling
                default: baseValue = 100f; break;
            }
            return baseValue * DungeonTunables.LootValueMultiplier;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Category generators (base-mod ladder mirrors)
        //  Each returns the market value it added so the caller's budget
        //  bookkeeping stays exact.
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The armoury a cleared dungeon pays out.
        ///
        /// A WEAPON AND A SET OF ARMOUR ARE MINTED UNCONDITIONALLY, before the
        /// budget is even consulted. That is the whole point of the rework: the
        /// old ladder rolled 70%/60% chances gated at C and B, so most gates
        /// below B paid no gear at all and a bad pair of rolls at A paid none
        /// either. A dungeon that can hand you nothing to equip is a supply
        /// crate with a boss in front of it.
        ///
        /// Past those two, pieces alternate weapon/armour until the budget runs
        /// out or the rank's piece cap does. The cap is what stops a Nation
        /// gate — 138k of budget — from carpeting the boss room in swords; the
        /// leftover falls through to the hoard as cores and gold instead, which
        /// is both denser on the floor and more on-theme.
        ///
        /// Pools are built ONCE here and passed down. The per-piece builders
        /// used to scan the whole DefDatabase each call, which was fine for the
        /// old one-piece-maximum ladder and is not fine now that a high-rank
        /// clear mints a dozen.
        /// </summary>
        private static float AddGearHaul(List<Thing> loot, MobRankTier rank, QuestRank q, TechLevel maxTech, float budget)
        {
            List<ThingDef> weaponPool = BuildWeaponPool(q, maxTech);
            List<ThingDef> armorPool = BuildArmorPool(q, maxTech);
            if (weaponPool.Count == 0 && armorPool.Count == 0)
                return 0f;

            // One piece per clear is the TROPHY: guaranteed refinement and
            // guaranteed rune sockets, so every dungeon reliably pays at least
            // one forged item rather than leaving it to a 5-15% per-slot roll
            // that the early game would almost never win. The weapon takes the
            // role unless there is no weapon to mint.
            bool weaponIsTrophy = weaponPool.Count > 0;
            float spent = MakeWeapon(loot, weaponPool, q, maxTech, trophy: weaponIsTrophy)
                        + MakeArmor(loot, armorPool, q, trophy: !weaponIsTrophy);

            // The cap bounds this loop even when a pool is empty and its turn
            // adds nothing, so an unlucky modlist cannot spin here.
            int cap = GearPieceCap(rank);
            for (int piece = 2; piece < cap && spent < budget; piece++)
            {
                spent += (piece % 2 == 0)
                    ? MakeWeapon(loot, weaponPool, q, maxTech)
                    : MakeArmor(loot, armorPool, q);
            }

            return spent;
        }

        /// <summary>
        /// How many pieces of gear a rank may drop at most.
        ///
        /// MobRankTier rather than QuestRank so Nation reads as its own tier
        /// instead of collapsing onto SSS — it is the only band above the
        /// quest ladder and should feel like it. The numbers are chosen so the
        /// cap binds at the top (where budget is enormous and the excess is
        /// better spent as hoard) and the budget binds at the bottom.
        /// </summary>
        private static int GearPieceCap(MobRankTier rank)
        {
            switch (rank)
            {
                case MobRankTier.F: return 2;
                case MobRankTier.E: return 2;
                case MobRankTier.D: return 3;
                case MobRankTier.C: return 4;
                case MobRankTier.B: return 5;
                case MobRankTier.A: return 6;
                case MobRankTier.S: return 8;
                case MobRankTier.SS: return 10;
                case MobRankTier.SSS: return 12;
                default: return 14;   // Nation
            }
        }

        /// <summary>
        /// Mirror of GenerateWeapon's pool filter: rank-tiered def pool capped
        /// by player tech level. Built once per haul, not once per piece.
        /// </summary>
        private static List<ThingDef> BuildWeaponPool(QuestRank q, TechLevel maxTech)
        {
            List<ThingDef> pool = new List<ThingDef>();
            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;

            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (!d.IsWeapon || d.tradeability == Tradeability.None || d.destroyOnDrop)
                    continue;

                // Ranked artifacts (this mod's and Isekai Creatures') are
                // chest prizes, banded by grade. Here the only filter is tech
                // level, so an A-rank clear could pay an S or SS artifact.
                if (IsRankedArtifact(d))
                    continue;

                if ((int)q >= (int)QuestRank.SS)
                {
                    TechLevel cap = (TechLevel)Mathf.Min((int)TechLevel.Spacer, (int)maxTech);
                    if ((int)d.techLevel > (int)cap) continue;
                }
                else if ((int)q >= (int)QuestRank.A)
                {
                    TechLevel cap = (TechLevel)Mathf.Min((int)TechLevel.Spacer, (int)maxTech);
                    if (d.techLevel != TechLevel.Industrial && d.techLevel != TechLevel.Spacer) continue;
                    if ((int)d.techLevel > (int)cap) continue;
                }
                else
                {
                    TechLevel cap = (TechLevel)Mathf.Min((int)TechLevel.Industrial, (int)maxTech);
                    if ((int)d.techLevel > (int)cap || d.BaseMarketValue >= 500f) continue;
                }

                pool.Add(d);
            }

            return pool;
        }

        private static bool IsRankedArtifact(ThingDef d)
        {
            List<string> tags = d.weaponTags;
            if (tags == null)
                return false;
            for (int i = 0; i < tags.Count; i++)
            {
                if (tags[i] == "IsekaiDungeonArtifact" || tags[i] == "IsekaiCreatureArtifact")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mints one weapon from a prebuilt pool: rank-tiered stuff
        /// preference, quality roll, and the same forge-enhancement table
        /// quest rewards get (so a dungeon weapon is exactly as juiced as a
        /// same-rank contract one).
        /// </summary>
        private static float MakeWeapon(List<Thing> loot, List<ThingDef> pool, QuestRank q, TechLevel maxTech, bool trophy = false)
        {
            if (pool == null || pool.Count == 0)
                return 0f;

            ThingDef weaponDef = pool[Rand.Range(0, pool.Count)];
            ThingDef stuff = PickWeaponStuff(weaponDef, q, maxTech);

            Thing weapon = ThingMaker.MakeThing(weaponDef, stuff);
            ApplyQuality(weapon, q);
            ApplyForgeEnhancement(weapon, q, trophy);

            loot.Add(weapon);
            return weapon.MarketValue;
        }

        /// <summary>
        /// Mirror of GenerateArmor's pool filter: torso-covering apparel by
        /// rank (SS+ any tech-capped torso gear, A+ industrial gear with a
        /// real sharp-armor stat, else cheap torso gear). Built once per haul.
        /// </summary>
        private static List<ThingDef> BuildArmorPool(QuestRank q, TechLevel maxTech)
        {
            List<ThingDef> pool = new List<ThingDef>();
            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;

            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (!d.IsApparel || d.tradeability == Tradeability.None)
                    continue;

                if ((int)q >= (int)QuestRank.SS)
                {
                    TechLevel cap = (TechLevel)Mathf.Min((int)TechLevel.Spacer, (int)maxTech);
                    if ((int)d.techLevel > (int)cap || !CoversTorso(d)) continue;
                }
                else if ((int)q >= (int)QuestRank.A)
                {
                    if ((int)d.techLevel < (int)TechLevel.Industrial || (int)d.techLevel > (int)maxTech) continue;
                    if (!HasSharpArmorStat(d)) continue;
                }
                else
                {
                    TechLevel cap = (TechLevel)Mathf.Min((int)TechLevel.Industrial, (int)maxTech);
                    if ((int)d.techLevel > (int)cap || !CoversTorso(d) || d.BaseMarketValue >= 300f) continue;
                }

                pool.Add(d);
            }

            return pool;
        }

        /// <summary>Mints one piece of armour from a prebuilt pool: stuff preference, quality, forge enhancement.</summary>
        private static float MakeArmor(List<Thing> loot, List<ThingDef> pool, QuestRank q, bool trophy = false)
        {
            if (pool == null || pool.Count == 0)
                return 0f;

            ThingDef armorDef = pool[Rand.Range(0, pool.Count)];
            ThingDef stuff = PickArmorStuff(armorDef, q);

            Thing armor = ThingMaker.MakeThing(armorDef, stuff);
            ApplyQuality(armor, q);
            ApplyForgeEnhancement(armor, q, trophy);

            loot.Add(armor);
            return armor.MarketValue;
        }

        /// <summary>
        /// Combat elixirs — the only consumable that survived the rework.
        ///
        /// A vial of something that makes you fight better is dungeon loot;
        /// pemmican and herbal medicine are groceries. So this is A+ only and
        /// draws exclusively from the top of the base ladder (go-juice,
        /// luciferium), where the old GenerateDrugs mirror had a low tier that
        /// handed out penoxycyline and, failing that, BEER. Below A the pile
        /// simply has no consumable, which is correct — a rank-F warren does
        /// not stock pharmaceuticals.
        ///
        /// Both defs are optional (they are Ideology-adjacent in some
        /// modlists), so a null just skips, per the GetNamedSilentFail
        /// convention used everywhere we touch defs we do not own.
        /// </summary>
        private static float AddElixirs(List<Thing> loot, QuestRank q, TechLevel maxTech)
        {
            ThingDef goJuice = DefDatabase<ThingDef>.GetNamedSilentFail("GoJuice");
            ThingDef luciferium = DefDatabase<ThingDef>.GetNamedSilentFail("Luciferium");

            ThingDef drugDef;
            int count;

            if ((int)q >= (int)QuestRank.SS && (int)maxTech >= (int)TechLevel.Spacer && luciferium != null)
            {
                drugDef = luciferium;
                count = Rand.Range(3, 8);
            }
            else
            {
                drugDef = goJuice;
                count = Rand.Range(5, 12);
            }

            return AddStacks(loot, drugDef, count);
        }

        /// <summary>
        /// Loose coin sized to a budget: the rank's currency ladder, count
        /// fitted to value, capped so a Nation payout cannot bury the room in
        /// gold before the hoard has even run.
        /// </summary>
        private static float AddCoinPurse(List<Thing> loot, float budget, QuestRank q)
        {
            if (budget <= 0f)
                return 0f;

            ThingDef coinDef = TreasureValuableFor(q);
            if (coinDef == null || coinDef.BaseMarketValue <= 0f)
                return 0f;

            int count = Mathf.Clamp(
                Mathf.RoundToInt(budget / coinDef.BaseMarketValue), 1, FillerCap(coinDef));
            return AddStacks(loot, coinDef, count);
        }

        /// <summary>
        /// Share of the clear target reserved for relics.
        ///
        /// A fifth. Gear takes 55% and coin 12%; a fifth on top leaves the
        /// hoard thin at the ranks where relic draws actually land, which is
        /// correct — a pile with a persona core in it does not also need four
        /// hundred gold. Like GearShare this is a stopping condition, not a
        /// cap: one draw may exceed it and the NEXT draw is what stops. A setting.
        /// </summary>
        private static float RelicShare => IsekaiDungeonsSettings.Current.relicShare;

        /// <summary>
        /// The curated vanilla and DLC treasure: archotech cores, psylink,
        /// persona weapons, eltex, subcores, genepacks, Anomaly relics.
        ///
        /// WHY THIS IS NOT A RANK COLUMN. Every other category here is gated by
        /// an explicit ladder because the mod owns both sides of it — we wrote
        /// the runes, we wrote the cores, we chose what F should see. The relic
        /// tables are the opposite case: they are somebody else's items with
        /// somebody else's prices, and any rank column we wrote would be a
        /// second, silently diverging copy of the pricing Ludeon already did.
        /// So the gate is the price itself, measured against THIS dungeon's own
        /// budget. See DungeonRelicTables for the arithmetic and what it lands
        /// on per rank.
        ///
        /// The relic budget is carved from the target the way gear and coin
        /// are, rather than added on top: a relic is part of what the dungeon
        /// paid out, not a bonus, and letting it overflow would quietly hand a
        /// Nation clear tens of thousands the curve never promised.
        ///
        /// Whatever the draws do not spend stays in `remaining` and falls
        /// through to the hoard, so a rank with an empty table (F, or a bare
        /// install with no expansions) loses nothing — it pays out as cores and
        /// gold exactly as before.
        /// </summary>
        private static float AddRelics(List<Thing> loot, MobRankTier rank, float target)
        {
            int draws = DungeonRelicTables.DrawCountFor(rank);
            if (draws <= 0 || target <= 0f)
                return 0f;

            float budget = target * RelicShare;
            float spent = 0f;
            HashSet<string> used = new HashSet<string>();

            for (int i = 0; i < draws; i++)
            {
                // Checked BEFORE the pick so we never build a genepack only to
                // throw it away.
                if (spent >= budget)
                    break;

                // TryPick is given the FULL target, not the remaining slice:
                // the ceiling it applies is "how big may one relic be relative
                // to this dungeon", which does not shrink because an earlier
                // draw already spent part of the slice.
                DungeonRelicTables.RelicEntry entry = DungeonRelicTables.TryPick(target, used);
                if (entry == null)
                    break;

                Thing thing = DungeonRelicTables.Make(entry, rank);

                // Marking the entry used either way. A pick that could not be
                // built (an empty genepack, a def whose comps refused) burns
                // its draw rather than being handed back on the next loop.
                used.Add(entry.defName);
                if (thing == null)
                    continue;

                loot.Add(thing);

                // Charge the INNER thing. A minified building's own stat sheet
                // is the crate's, not the chemreactor's, and billing the crate
                // would let the relic slice never fill — the loop would keep
                // drawing because `spent` never climbed. GetInnerIfMinified is
                // a no-op for everything that was not wrapped.
                spent += MinifyUtility.GetInnerIfMinified(thing).MarketValue;
            }

            return spent;
        }

        /// <summary>
        /// Runes, at EVERY rank rather than B and up.
        ///
        /// The old ladder started runes at B, which meant the entire early
        /// game — the ranks a colony actually clears first — never saw the
        /// mod's own signature drop. Now F pays a rank-I rune and the ladder
        /// climbs from there, in MobRankTier space so Nation sits above SSS
        /// instead of collapsing onto it.
        /// </summary>
        private static float AddRunes(List<Thing> loot, MobRankTier rank)
        {
            int count = RuneCountFor(rank);
            if (count <= 0)
                return 0f;

            // F and E draw their single rune on a coin flip rather than always.
            // A rank-I rune is worth 150-350, against a rank-F value target of
            // 270 — guaranteeing one there would roughly double the bottom of
            // the ladder. As a coin flip the expected value stays on budget and
            // the rune reads as a find rather than a wage.
            if (rank <= MobRankTier.E && !Rand.Chance(0.5f))
                return 0f;

            int maxRuneRank = RuneMaxRankFor(rank);
            float added = 0f;
            for (int i = 0; i < count; i++)
                added += AddRuneDraw(loot, maxRuneRank);
            return added;
        }

        private static int RuneCountFor(MobRankTier rank)
        {
            switch (rank)
            {
                case MobRankTier.F:
                case MobRankTier.E:
                case MobRankTier.D: return 1;
                case MobRankTier.C:
                case MobRankTier.B: return 2;
                case MobRankTier.A:
                case MobRankTier.S: return 3;
                case MobRankTier.SS: return 4;
                case MobRankTier.SSS: return 5;
                default: return 6;   // Nation
            }
        }

        /// <summary>Highest rune tier (I-V) a rank may roll; the draw itself is uniform below it.</summary>
        private static int RuneMaxRankFor(MobRankTier rank)
        {
            switch (rank)
            {
                case MobRankTier.F:
                case MobRankTier.E: return 1;
                case MobRankTier.D:
                case MobRankTier.C: return 2;
                case MobRankTier.B:
                case MobRankTier.A: return 3;
                case MobRankTier.S:
                case MobRankTier.SS: return 4;
                default: return 5;   // SSS, Nation
            }
        }

        /// <summary>
        /// One rune: uniform roll of the tier up to <paramref name="maxRuneRank"/>,
        /// then a random family at that tier. An empty bucket (that rune tier
        /// not shipped by the installed base mod) just skips, same null-skip
        /// convention as everything else touching defs we do not own.
        /// </summary>
        private static float AddRuneDraw(List<Thing> loot, int maxRuneRank)
        {
            EnsureRuneCache();

            int chosenRank = Rand.RangeInclusive(1, Mathf.Clamp(maxRuneRank, 1, 5));
            List<ThingDef> bucket = runeBuckets[chosenRank - 1];
            if (bucket.Count == 0)
                return 0f;

            ThingDef runeDef = bucket[Rand.Range(0, bucket.Count)];
            Thing rune = ThingMaker.MakeThing(runeDef);
            rune.stackCount = 1;
            loot.Add(rune);
            return rune.MarketValue;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Guaranteed cores
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The rank-scaled core payout every clear is contractually owed
        /// (ARCHITECTURE §8). Grouped by MobRankTier pairs, NOT QuestRank, so
        /// Nation gets the full SSS-tier payout rather than colliding with
        /// SS. Missing defs (base mod rework) null-skip per convention.
        /// </summary>
        /// <summary>
        /// Spends the leftover budget in the LARGEST denominations available,
        /// biggest first: mana cores, then gold, then silver for the scraps.
        ///
        /// This exists because every category above is count-clamped (food 5-20,
        /// components 1-8, ...) while the dungeon value target is an order of
        /// magnitude larger than the quest generator's. The old flat silver
        /// top-up therefore absorbed 85-92% of a high-rank payout, so the boss
        /// room was a mountain of silver stacks instead of a hoard — and at
        /// Nation it physically would not fit in the room.
        ///
        /// Mana cores are the right sink for an Isekai dungeon on every axis:
        /// they are the base mod's own currency, a Huge core is worth 1500 (so
        /// 30 stacks of silver become one core), and they feed the XP economy
        /// the whole progression runs on. A cleared Nation gate now pays in a
        /// pile of cores and gold, which reads as treasure.
        ///
        /// Returns whatever value could not be spent (always small).
        /// </summary>
        private static float AddFillerHoard(List<Thing> loot, float remaining)
        {
            // Descending denominations. Cores first: they are the thematic
            // payout AND the densest, so they collapse the stack count hardest.
            // Only PART of the filler becomes cores. Cores are worth far more
            // in play than in silver — each one is an XP item — so paying the
            // whole overflow in cores would quietly multiply progression well
            // past the intended loot boost. Half in cores, the rest in gold,
            // keeps the hoard reading as treasure without turning every clear
            // into a level-up firehose.
            float coreBudget = remaining * FillerCoreFraction;
            remaining -= coreBudget;

            string[] coreDefNames = { "Isekai_HugeManaCore", "Isekai_BigManaCore", "Isekai_ManaCore", "Isekai_SmallManaCore" };
            for (int i = 0; i < coreDefNames.Length && coreBudget > FillerCutoff; i++)
            {
                ThingDef core = DefDatabase<ThingDef>.GetNamedSilentFail(coreDefNames[i]);
                if (core == null || core.BaseMarketValue <= 0f)
                    continue;

                // Leave a slice for the smaller denominations so the hoard is
                // mixed rather than one enormous stack of a single core.
                float spendable = (i == coreDefNames.Length - 1) ? coreBudget : coreBudget * CoreFillerShare;
                int count = Mathf.FloorToInt(spendable / core.BaseMarketValue);
                if (count <= 0)
                    continue;
                count = Mathf.Min(count, FillerCap(core));
                AddStacks(loot, core, count);
                coreBudget -= count * core.BaseMarketValue;
            }

            // Anything the core ladder could not spend rejoins the pot.
            remaining += Mathf.Max(0f, coreBudget);

            // Gold: 10x denser than silver, and it still reads as treasure.
            if (remaining > FillerCutoff && ThingDefOf.Gold != null && ThingDefOf.Gold.BaseMarketValue > 0f)
            {
                int gold = Mathf.Min(Mathf.FloorToInt(remaining / ThingDefOf.Gold.BaseMarketValue), FillerCap(ThingDefOf.Gold));
                if (gold > 0)
                {
                    AddStacks(loot, ThingDefOf.Gold, gold);
                    remaining -= gold * ThingDefOf.Gold.BaseMarketValue;
                }
            }

            // Silver only for the remainder — at most a stack or two now.
            if (remaining > FillerCutoff && ThingDefOf.Silver != null && ThingDefOf.Silver.BaseMarketValue > 0f)
            {
                int silver = Mathf.Min(Mathf.RoundToInt(remaining / ThingDefOf.Silver.BaseMarketValue), FillerCap(ThingDefOf.Silver));
                if (silver > 0)
                {
                    AddStacks(loot, ThingDefOf.Silver, silver);
                    remaining -= silver * ThingDefOf.Silver.BaseMarketValue;
                }
            }

            return Mathf.Max(0f, remaining);
        }

        private static void AddGuaranteedCores(List<Thing> loot, MobRankTier rank)
        {
            switch (rank)
            {
                case MobRankTier.F:
                case MobRankTier.E:
                    AddCoreStack(loot, "Isekai_SmallManaCore", Rand.RangeInclusive(4, 6));
                    break;

                case MobRankTier.D:
                case MobRankTier.C:
                    AddCoreStack(loot, "Isekai_ManaCore", Rand.RangeInclusive(4, 6));
                    break;

                case MobRankTier.B:
                case MobRankTier.A:
                    AddCoreStack(loot, "Isekai_BigManaCore", Rand.RangeInclusive(4, 6));
                    AddCoreStack(loot, "Isekai_ReinforcementCore", Rand.RangeInclusive(3, 4));
                    break;

                case MobRankTier.S:
                case MobRankTier.SS:
                    AddCoreStack(loot, "Isekai_HugeManaCore", Rand.RangeInclusive(3, 4));
                    AddCoreStack(loot, "Isekai_StarFragment", Rand.RangeInclusive(2, 3));
                    AddCoreStack(loot, "Isekai_ReinforcementCore", Rand.RangeInclusive(3, 5));
                    break;

                default: // SSS and Nation
                    AddCoreStack(loot, "Isekai_HugeManaCore", Rand.RangeInclusive(6, 8));
                    AddCoreStack(loot, "Isekai_StarFragment", Rand.RangeInclusive(5, 7));
                    AddCoreStack(loot, "Isekai_ReinforcementCore", Rand.RangeInclusive(5, 7));
                    break;
            }
        }

        /// <summary>Base-mod item by defName, null-skip if absent (GetNamedSilentFail convention).</summary>
        private static void AddCoreStack(List<Thing> loot, string defName, int count)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            AddStacks(loot, def, count);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Shared helpers
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Makes `totalCount` of a def as one or more stacks, each respecting
        /// def.stackLimit (the base generator can emit a 150-steel "stack"
        /// that vanilla then refuses to merge; we split up front so the loot
        /// pile spawns clean). Returns total market value added.
        /// </summary>
        private static float AddStacks(List<Thing> loot, ThingDef def, int totalCount)
        {
            if (def == null || totalCount <= 0)
                return 0f;

            int limit = Mathf.Max(1, def.stackLimit);
            float added = 0f;
            while (totalCount > 0)
            {
                int n = Mathf.Min(limit, totalCount);
                Thing thing = ThingMaker.MakeThing(def);
                thing.stackCount = n;
                loot.Add(thing);
                added += thing.MarketValue * n;
                totalCount -= n;
            }
            return added;
        }

        /// <summary>
        /// Player tech level caps every gear/material tier (base-mod
        /// convention, keeps us compatible with tech-limiting mods).
        /// </summary>
        private static TechLevel MaxTechLevel()
        {
            return Faction.OfPlayer?.def?.techLevel ?? TechLevel.Spacer;
        }

        /// <summary>Mirror of the base mod's weapon stuff preference: plasteel at S+ (spacer), uranium at A+ (industrial), else random legal stuff.</summary>
        private static ThingDef PickWeaponStuff(ThingDef weaponDef, QuestRank q, TechLevel maxTech)
        {
            if (!weaponDef.MadeFromStuff)
                return null;

            List<ThingDef> options = new List<ThingDef>();
            foreach (ThingDef s in GenStuff.AllowedStuffsFor(weaponDef))
                options.Add(s);
            if (options.Count == 0)
                return null;

            if ((int)q >= (int)QuestRank.S && (int)maxTech >= (int)TechLevel.Spacer && options.Contains(ThingDefOf.Plasteel))
                return ThingDefOf.Plasteel;
            if ((int)q >= (int)QuestRank.A && (int)maxTech >= (int)TechLevel.Industrial && options.Contains(ThingDefOf.Uranium))
                return ThingDefOf.Uranium;
            return options[Rand.Range(0, options.Count)];
        }

        /// <summary>Mirror of the base mod's armor stuff preference: devilstrand at SS+, hyperweave at A+, else random legal stuff.</summary>
        private static ThingDef PickArmorStuff(ThingDef armorDef, QuestRank q)
        {
            if (!armorDef.MadeFromStuff)
                return null;

            List<ThingDef> options = new List<ThingDef>();
            foreach (ThingDef s in GenStuff.AllowedStuffsFor(armorDef))
                options.Add(s);
            if (options.Count == 0)
                return null;

            ThingDef stuff = null;
            if ((int)q >= (int)QuestRank.SS)
                stuff = FindByDefName(options, "DevilstrandCloth");
            if (stuff == null && (int)q >= (int)QuestRank.A)
                stuff = FindByDefName(options, "Hyperweave");
            if (stuff == null)
                stuff = options[Rand.Range(0, options.Count)];
            return stuff;
        }

        private static ThingDef FindByDefName(List<ThingDef> defs, string defName)
        {
            for (int i = 0; i < defs.Count; i++)
                if (defs[i] != null && defs[i].defName == defName)
                    return defs[i];
            return null;
        }

        /// <summary>
        /// "Is this real body armor, not a hat" — the base mod's proxy is
        /// coverage of the Torso body part group, matched by defName because
        /// BodyPartGroupDefOf has no Torso entry.
        /// </summary>
        private static bool CoversTorso(ThingDef d)
        {
            List<BodyPartGroupDef> groups = d.apparel?.bodyPartGroups;
            if (groups == null)
                return false;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i] != null && groups[i].defName == "Torso")
                    return true;
            return false;
        }

        /// <summary>
        /// The base mod's A-tier armor filter: the def itself must carry a
        /// meaningful ArmorRating_Sharp stat base (>0.1), which excludes
        /// stuff-derived pseudo-armor like plain clothing.
        /// </summary>
        private static bool HasSharpArmorStat(ThingDef d)
        {
            List<StatModifier> stats = d.statBases;
            if (stats == null)
                return false;
            for (int i = 0; i < stats.Count; i++)
                if (stats[i] != null && stats[i].stat == StatDefOf.ArmorRating_Sharp && stats[i].value > 0.1f)
                    return true;
            return false;
        }

        /// <summary>
        /// Quality roll inside the window for the rank: Normal→Good at the
        /// bottom, Masterwork→Legendary at SSS.
        ///
        /// The windows sit a full tier above the base mod's quest table, whose
        /// floor was Poor. Poor gear is worse than what any colony with a
        /// smithy already makes, so as dungeon loot it reads as an insult
        /// rather than a reward — you fought a boss for a shoddy knife.
        /// Nothing below Normal drops from a dungeon now.
        ///
        /// This costs no balance: the gear haul spends a MARKET VALUE budget,
        /// and quality multiplies market value, so a richer window mints fewer
        /// pieces rather than a stronger pile.
        /// </summary>
        private static void ApplyQuality(Thing thing, QuestRank q)
        {
            CompQuality comp = thing.TryGetComp<CompQuality>();
            if (comp == null)
                return;
            QualityCategory quality = (QualityCategory)Rand.Range((int)MinQuality(q), (int)MaxQuality(q) + 1);
            comp.SetQuality(quality, ArtGenerationContext.Outsider);
        }

        private static QualityCategory MinQuality(QuestRank q)
        {
            switch (q)
            {
                case QuestRank.SSS: return QualityCategory.Masterwork;
                case QuestRank.SS: return QualityCategory.Excellent;
                case QuestRank.S: return QualityCategory.Excellent;
                case QuestRank.A: return QualityCategory.Good;
                case QuestRank.B: return QualityCategory.Good;
                default: return QualityCategory.Normal;
            }
        }

        private static QualityCategory MaxQuality(QuestRank q)
        {
            switch (q)
            {
                case QuestRank.SSS: return QualityCategory.Legendary;
                case QuestRank.SS: return QualityCategory.Masterwork;
                case QuestRank.S: return QualityCategory.Masterwork;
                case QuestRank.A: return QualityCategory.Excellent;
                case QuestRank.B: return QualityCategory.Excellent;
                default: return QualityCategory.Good;
            }
        }

        /// <summary>
        /// Same forge-enhancement table quest rewards use (refinement range +
        /// rune-socket chance by rank). ForgeItemGenerator is PUBLIC base-mod
        /// API (hard reference, no reflection) and internally no-ops when the
        /// forge system is disabled or the item has no enhancement comp, so
        /// no guards are needed here.
        /// </summary>
        private static void ApplyForgeEnhancement(Thing item, QuestRank q, bool trophy = false)
        {
            int minRef, maxRef;
            float runeChance;
            int maxRuneRank;

            switch (q)
            {
                // F/E/D ARE OURS, not the quest table's.
                //
                // The quest ladder starts at C and its default case pays
                // nothing, which was self-consistent there because the quest
                // generator never mints gear below C. Dungeons now guarantee a
                // weapon and armour at every rank, so copying that default
                // meant every rank-F/E/D drop came out plain — no refinement,
                // no sockets — which is exactly the gear the forge system
                // exists to replace.
                //
                // Chance-gated rather than flat so the ladder still climbs:
                // an enhanced blade out of a rank-F warren should read as a
                // find, not as the baseline.
                case QuestRank.F:
                    if (!trophy && !Rand.Chance(0.45f)) return;
                    minRef = 1; maxRef = 1; runeChance = 0.05f; maxRuneRank = 1; break;
                case QuestRank.E:
                    if (!trophy && !Rand.Chance(0.60f)) return;
                    minRef = 1; maxRef = 2; runeChance = 0.08f; maxRuneRank = 1; break;
                case QuestRank.D:
                    if (!trophy && !Rand.Chance(0.80f)) return;
                    minRef = 1; maxRef = 2; runeChance = 0.12f; maxRuneRank = 1; break;

                // C and up mirror the base mod's quest table exactly, so a
                // dungeon drop is as juiced as a same-rank contract one.
                case QuestRank.C: minRef = 1; maxRef = 2; runeChance = 0.15f; maxRuneRank = 1; break;
                case QuestRank.B: minRef = 2; maxRef = 3; runeChance = 0.30f; maxRuneRank = 2; break;
                case QuestRank.A: minRef = 3; maxRef = 5; runeChance = 0.50f; maxRuneRank = 2; break;
                case QuestRank.S: minRef = 4; maxRef = 6; runeChance = 0.70f; maxRuneRank = 3; break;
                case QuestRank.SS: minRef = 5; maxRef = 8; runeChance = 0.85f; maxRuneRank = 4; break;
                case QuestRank.SSS: minRef = 6; maxRef = 10; runeChance = 1.00f; maxRuneRank = 5; break;
                default: return;
            }

            ForgeItemGenerator.TryApplyRandomEnhancement(item, maxRef, runeChance, maxRuneRank, minRef);

            // The trophy always comes out socketed. Below B the per-slot rune
            // chance is 5-15%, so across a whole early game a player could
            // clear a dozen gates and never actually see a runed item — the
            // feature would be in the code and not in the game.
            //
            // A TOP-UP rather than forcing runeChance to 1: that would fill
            // every slot, which is three runes on a rank-F chestpiece and a
            // real power spike at the point the curve is flattest. This adds
            // exactly one, and only when the normal rolls produced none, so
            // the upper ranks (already rolling 70-100% per slot) are untouched.
            if (trophy)
                EnsureOneRune(item, maxRuneRank);
        }

        /// <summary>
        /// Sockets a single rune of the item's own category if it came out
        /// with none. Only the per-clear trophy uses this.
        ///
        /// Guarded on the forge toggle exactly like ForgeItemGenerator is:
        /// this reaches the comp directly, so without the check a player who
        /// had switched the forge system off would still get runed dungeon
        /// loot — the one path around their own setting.
        /// </summary>
        private static void EnsureOneRune(Thing item, int maxRuneRank)
        {
            if (!IsekaiLevelingSettings.EnableForgeSystem)
                return;

            CompForgeEnhancement comp = item.TryGetComp<CompForgeEnhancement>();
            if (comp == null || comp.UsedRuneSlots > 0 || !comp.CanAddRune())
                return;

            RuneCategory wanted = item.def.IsWeapon ? RuneCategory.Weapon : RuneCategory.Armor;

            List<RuneDef> pool = new List<RuneDef>();
            List<RuneDef> all = DefDatabase<RuneDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].category == wanted)
                    pool.Add(all[i]);

            if (pool.Count == 0)
                return;

            comp.TryAddRune(pool[Rand.Range(0, pool.Count)],
                            Rand.RangeInclusive(1, Mathf.Clamp(maxRuneRank, 1, 5)));
        }

        /// <summary>
        /// The currency ladder, shared by the clear pile's coin purse and the
        /// treasure rooms: gold/jade at SS+, gold at A+, gold-or-silver at C+,
        /// silver below. Low ranks pay in silver because a rank-F purse is
        /// worth ~30 — denominated in gold that would round to three coins.
        /// </summary>
        private static ThingDef TreasureValuableFor(QuestRank q)
        {
            if ((int)q >= (int)QuestRank.SS)
                return Rand.Bool ? ThingDefOf.Gold : ThingDefOf.Jade;
            if ((int)q >= (int)QuestRank.A)
                return ThingDefOf.Gold;
            if ((int)q >= (int)QuestRank.C)
                return Rand.Bool ? ThingDefOf.Gold : ThingDefOf.Silver;
            return ThingDefOf.Silver;
        }

        /// <summary>Rune-rank parse from the defName suffix, mirroring the base mod (no suffix = I).</summary>
        private static int RuneItemRank(string defName)
        {
            if (defName.EndsWith("_V")) return 5;
            if (defName.EndsWith("_IV")) return 4;
            if (defName.EndsWith("_III")) return 3;
            if (defName.EndsWith("_II")) return 2;
            return 1;
        }

        private static void EnsureRuneCache()
        {
            if (runeBuckets != null)
                return;

            runeBuckets = new List<ThingDef>[5];
            for (int i = 0; i < 5; i++)
                runeBuckets[i] = new List<ThingDef>();

            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (d?.defName == null || !d.defName.StartsWith("Isekai_Rune_"))
                    continue;
                runeBuckets[RuneItemRank(d.defName) - 1].Add(d);
            }
        }
    }
}
