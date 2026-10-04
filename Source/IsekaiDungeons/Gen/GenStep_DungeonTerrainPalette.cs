using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace IsekaiDungeons
{
    /// <summary>One terrain in a palette, with the share of the field it should cover.</summary>
    public class DungeonTerrainEntry
    {
        /// <summary>
        /// TerrainDef defName as a STRING, resolved at generation time.
        ///
        /// Deliberately not a def reference: a palette may want a terrain that
        /// only exists with a DLC, or one of the smoothed-stone terrains that
        /// RimWorld generates in code rather than declaring in XML. A missing
        /// name drops out of the palette with a warning instead of failing the
        /// whole def at load — the same convention the loot generator uses for
        /// base-mod items.
        /// </summary>
        public string terrain;

        /// <summary>Relative share of the field. Weights need not sum to anything.</summary>
        public float weight = 1f;

        /// <summary>
        /// Accepts both the compact form and the verbose one:
        ///     &lt;li&gt;TileMarble 0.55&lt;/li&gt;
        ///     &lt;li&gt;&lt;terrain&gt;TileMarble&lt;/terrain&gt;&lt;weight&gt;0.55&lt;/weight&gt;&lt;/li&gt;
        /// A palette is a dozen of these, and the verbose form buries the
        /// actual design under scaffolding.
        /// </summary>
        public void LoadDataFromXmlCustom(System.Xml.XmlNode xmlRoot)
        {
            if (xmlRoot.ChildNodes.Count == 1 && xmlRoot.FirstChild.NodeType == System.Xml.XmlNodeType.Text)
            {
                string[] parts = xmlRoot.FirstChild.Value.Trim()
                    .Split(new[] { ' ', '	' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                    terrain = parts[0];
                if (parts.Length > 1)
                    weight = ParseWeight(parts[parts.Length - 1], weight);
                return;
            }

            System.Xml.XmlNode t = xmlRoot["terrain"];
            if (t != null)
                terrain = t.InnerText.Trim();
            System.Xml.XmlNode w = xmlRoot["weight"];
            if (w != null)
                weight = ParseWeight(w.InnerText, weight);
        }

        // Invariant culture: these files use '.' as the decimal separator, and a
        // comma locale would read "0.55" as 55.
        private static float ParseWeight(string s, float fallback)
        {
            return float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float v)
                ? v : fallback;
        }
    }

    /// <summary>
    /// Repaints an OPEN dungeon's ground with its own terrain palette.
    ///
    /// WHY THIS EXISTS. An Open dungeon's ground comes from whatever vanilla
    /// biome its generator borrowed, and the biome was chosen for its RULES —
    /// plant density, temperature, whether rivers may appear — not for its
    /// looks. The Celestial environment borrows ExtremeDesert because it wants
    /// a bare, plantless field, and consequently a "pale marble reach" renders
    /// as ordinary sand. Six open environments all end up looking like the
    /// handful of vanilla biomes underneath them.
    ///
    /// So the biome keeps deciding the rules and this step decides the look.
    /// It runs straight after the vanilla Terrain step, before anything is
    /// scattered or carved, and paints the whole field from a weighted palette.
    ///
    /// PATCHES, NOT NOISE. Terrains are selected through a low-frequency Perlin
    /// field rather than per-cell randomness, so a palette reads as drifts and
    /// veins of one material through another instead of a uniform speckle. A
    /// second, finer field breaks up the boundaries so the patches do not look
    /// like contour lines.
    ///
    /// GAMEPLAY CAVEAT worth knowing when authoring a palette: constructed
    /// floors have zero fertility, so a field painted with them grows nothing
    /// when the Plants step runs afterwards. That is exactly right for a marble
    /// sanctum and exactly wrong for an overgrown forest — which is why the
    /// Verdant environment has no palette.
    /// </summary>
    public class GenStep_DungeonTerrainPalette : GenStep
    {
        public List<DungeonTerrainEntry> terrains = new List<DungeonTerrainEntry>();

        /// <summary>Size of the patches. Lower is broader; 0.018 gives drifts a few dozen cells across.</summary>
        public float frequency = 0.018f;

        /// <summary>Fine detail that roughens the seams between patches.</summary>
        public float detailFrequency = 0.09f;
        public float detailStrength = 0.18f;

        /// <summary>Leave water alone. A river painted over with marble reads as a bug, not a design.</summary>
        public bool preserveWater = true;

        public override int SeedPart => 1937450113;

        public override void Generate(Map map, GenStepParams parms)
        {
            // Belt and braces: no Carve generator lists a palette, but if one
            // ever did it would be painting over rock the carver has not cut
            // yet, and every carved room sets its own floorTerrain afterwards
            // anyway. Refuse rather than fight it.
            if (map?.Parent is DungeonMapParent parent
                && parent.mapGenerator != null
                && parent.mapGenerator.isUnderground)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Terrain palette skipped on {parent.mapGenerator.defName}: " +
                            $"palettes are for OPEN dungeons; a carved dungeon's floors come from its DungeonKindDef.");
                return;
            }

            var resolved = new List<TerrainDef>();
            var weights = new List<float>();
            float total = 0f;
            for (int i = 0; i < terrains.Count; i++)
            {
                DungeonTerrainEntry e = terrains[i];
                if (e == null || e.terrain.NullOrEmpty() || e.weight <= 0f)
                    continue;
                TerrainDef def = DefDatabase<TerrainDef>.GetNamedSilentFail(e.terrain);
                if (def == null)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Terrain palette: no TerrainDef named \"{e.terrain}\"; " +
                                $"dropping it from the palette.");
                    continue;
                }
                resolved.Add(def);
                weights.Add(e.weight);
                total += e.weight;
            }

            if (resolved.Count == 0 || total <= 0f)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Terrain palette on {map?.Parent?.def?.defName} resolved to nothing; ground left as the biome painted it.");
                return;
            }

            // Cumulative thresholds over the normalised weights: a noise value
            // of v picks the first terrain whose running total passes it.
            var thresholds = new List<float>(resolved.Count);
            float running = 0f;
            for (int i = 0; i < weights.Count; i++)
            {
                running += weights[i] / total;
                thresholds.Add(running);
            }
            thresholds[thresholds.Count - 1] = 1.001f;   // guard against float drift at the top

            ModuleBase broad = new Perlin(frequency, 2.0, 0.5, 4, Rand.Int, QualityMode.Medium);
            ModuleBase detail = new Perlin(detailFrequency, 2.0, 0.5, 2, Rand.Int, QualityMode.Low);

            TerrainGrid grid = map.terrainGrid;
            int painted = 0;
            foreach (IntVec3 cell in map.AllCells)
            {
                TerrainDef current = grid.TerrainAt(cell);
                if (current == null)
                    continue;
                if (preserveWater && current.IsWater)
                    continue;

                // Perlin is roughly -1..1; fold to 0..1 and roughen the seams.
                float v = (float)broad.GetValue(cell.x, 0.0, cell.z) * 0.5f + 0.5f;
                v += ((float)detail.GetValue(cell.x, 0.0, cell.z)) * detailStrength;
                v = Mathf.Clamp01(v);

                TerrainDef pick = resolved[resolved.Count - 1];
                for (int i = 0; i < thresholds.Count; i++)
                {
                    if (v <= thresholds[i])
                    {
                        pick = resolved[i];
                        break;
                    }
                }

                if (pick != current)
                {
                    grid.SetTerrain(cell, pick);
                    painted++;
                }
            }

            Log.Message($"{IsekaiDungeonsMod.LogPrefix} Terrain palette: repainted {painted} cells across {resolved.Count} terrains.");
        }
    }
}
