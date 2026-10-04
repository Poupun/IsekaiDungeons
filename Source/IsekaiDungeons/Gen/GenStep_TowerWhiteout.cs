using System.Collections.Generic;
using Verse;
using Verse.Noise;

namespace IsekaiDungeons
{
    /// <summary>
    /// Makes the tower white from the inside out.
    ///
    /// A carved floor's walls are natural rock left behind when the layout
    /// subtracts rooms from a solid map, so the tower's identity comes down to
    /// ONE question: which rock did this map generate with? That is answered by
    /// RockNoises.Init, which reads Find.World.NaturalRockTypesIn(map.Tile) —
    /// the geology of the tile the tower happens to stand on. Left alone, the
    /// inside of the tower would be whatever granite or sandstone the local
    /// biome uses, and it would change depending on where the tower was raised.
    ///
    /// So this replaces the whole noise list with a single entry at constant
    /// weight: every rock cell resolves to pale stone, everywhere, always.
    ///
    /// ORDER MATTERS ABSOLUTELY. RockNoises.Init runs once before the gen steps
    /// begin, and Underground_RocksFromGrid consumes the list at order 200.
    /// This step is order 190 — after Init has built the list, before anything
    /// reads it. Later is too late, and there is no earlier hook that survives
    /// Init overwriting the list.
    ///
    /// OPEN FLOORS ARE DELIBERATELY EXEMPT. A field of grass under a strange
    /// sky, a market, a stand of trees — those are the floors that make the
    /// climb feel like a journey rather than a corridor, and bleaching them
    /// would flatten the whole tower into a single texture.
    /// </summary>
    public class GenStep_TowerWhiteout : GenStep
    {
        /// <summary>Unique across the mod's gen steps; MapGenerator re-seeds each step from it.</summary>
        public override int SeedPart => 823471231;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!(map.Parent is TowerFloorMapParent))
                return;

            // Only carved floors. isUnderground is the same discriminator the
            // environment defs use, and it decides whether there is a rock
            // shell to recolour at all.
            if (map.generatorDef == null || !map.generatorDef.isUnderground)
                return;

            ThingDef pale = IsekaiDungeonsDefOf.IsekaiDungeons_PaleStone;
            if (pale == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Pale stone def is missing; this tower floor will use local geology.");
                return;
            }

            // Const(1.0) rather than a Perlin: there is nothing to blend
            // between, and any variation would reintroduce the local rock.
            RockNoises.rockNoises = new List<RockNoises.RockNoise>
            {
                new RockNoises.RockNoise
                {
                    rockDef = pale,
                    noise = new Const(1.0),
                },
            };
        }
    }
}
