using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Narrative flavour attached to an ENVIRONMENT (a MapGeneratorDef).
    ///
    /// A DefModExtension rather than fields on DungeonKindDef, for two reasons.
    /// First, flavour belongs to the place, not to the monster roster: Slime
    /// Ranch and Kobold Den are different dungeons but the player walks into the
    /// same cavern, and writing the cave description 53 times would guarantee it
    /// drifts. Second, MapGeneratorDef is a vanilla def we do not own — an
    /// extension is the idiomatic way to hang data off it, and it means a modder
    /// attaches this to THEIR generator and their environment narrates itself.
    ///
    /// Every field is optional. Missing keys fall back to layout-mode defaults,
    /// so an environment with no extension still produces a sensible letter.
    /// </summary>
    public class DungeonEnvironmentExtension : DefModExtension
    {
        /// <summary>
        /// Translation key for the sentence describing what the party is
        /// standing in ("cut from the living rock", "an ice plain under a sky
        /// the wrong colour"). This is the line that makes a cave read
        /// differently from a snowfield.
        /// </summary>
        [MustTranslate]
        public string entryFlavorKey;

        /// <summary>
        /// Short line under the dungeon name on the title card. Two or three
        /// words — it is read in about a second.
        /// </summary>
        [MustTranslate]
        public string titleSubtitleKey;

        /// <summary>
        /// Optional word for what a "room" is called here, so the scale line
        /// reads naturally: chambers in a cave, but reaches or hunting grounds
        /// on an open field.
        /// </summary>
        [MustTranslate]
        public string roomNounKey;
    }
}
