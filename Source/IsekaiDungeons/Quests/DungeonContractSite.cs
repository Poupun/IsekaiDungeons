using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A contract site that outlives its own map.
    ///
    /// THE BUG THIS EXISTS TO FIX, because a plain vanilla Site cannot do this
    /// and the failure is silent. Site.ShouldRemoveMapNow computes
    ///
    ///     alsoRemoveWorldObject = !parts.Any(worker is SitePartWorker_ConditionCauser
    ///                                     || worker is SitePartWorker_RaidSource ...)
    ///
    /// and SitePartWorker_DungeonContract is neither of those, so the flag came
    /// back TRUE unconditionally. MapParent.CheckRemoveMapNow then deinits the
    /// map and calls Destroy() on the world object. In practice: a party
    /// caravans to a rank-A contract, looks at the boss, decides it is too
    /// strong and walks home. The instant they leave, the engine deletes the
    /// site. The gate they were told about no longer exists, the quest hangs in
    /// the Quests tab pointing at a destroyed object forever, and because
    /// DungeonQuestTracker counts unresolved contracts against MaxActiveQuests,
    /// two such abandonments permanently stop the guild posting dungeon work
    /// for the rest of the save.
    ///
    /// Note the shape of the mistake. The quest part is written entirely around
    /// never calling Destroy on a site, and it never does; the ENGINE does,
    /// through a policy hook nobody thought to look at. Owning a world object
    /// means owning its removal policy too, not only your own calls.
    ///
    /// What vanilla already gets right, and is kept: the base implementation
    /// refuses to remove the map while any pawn blocks removal, AND while any
    /// pocket map lists this map as its sourceMap. That second clause is what
    /// protects a party who are INSIDE the dungeon rather than standing on the
    /// grass outside it, because Building_DungeonGate sets sourceMap on the
    /// DungeonMapParent it generates. So this override only ever relaxes the
    /// world-object half of the decision, never the map-safety half.
    /// </summary>
    public class DungeonContractSite : Site
    {
        /// <summary>
        /// Absolute ceiling on how long an unresolved site may keep itself
        /// alive, as a backstop against orphaning.
        ///
        /// The quest part normally writes a contract off on its own schedule,
        /// but a quest can disappear in ways the quest part never sees: a mod
        /// removed mid-save, a corrupted quest, a player deleting it through dev
        /// tools. Without a ceiling those sites would sit on the world map for
        /// the rest of the game with nothing able to clear them. Generous on
        /// purpose, since expiring one early is the worse failure. Read live,
        /// because both lifetimes are settings.
        /// </summary>
        private static float OrphanLifetimeDays =>
            DungeonQuestTuning.DaysBeforeBreak + DungeonQuestTuning.BrokenSiteDays + 20f;

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            bool removeMap = base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
            if (!removeMap || !alsoRemoveWorldObject)
                return removeMap;

            // The map may go. The SITE may not, while there is still a contract
            // on it: the whole point of a located contract is that the player
            // can leave, prepare, and come back to the same gate.
            if (StillUnderContract())
                alsoRemoveWorldObject = false;

            return removeMap;
        }

        private bool StillUnderContract()
        {
            WorldObjectComp_DungeonContract comp = GetComponent<WorldObjectComp_DungeonContract>();
            if (comp == null)
                return false;
            if (comp.contractResolved)
                return false;

            if (comp.postedTick >= 0
                && Find.TickManager.TicksGame - comp.postedTick
                   > (int)(OrphanLifetimeDays * GenDate.TicksPerDay))
            {
                return false;   // orphaned; let it go
            }
            return true;
        }

        // ── The pin on the planet ───────────────────────────────────────────
        //
        // A contract used to draw as a vanilla ancient complex, which said
        // "generic ruin" for something that is a dimensional gate, and said the
        // same thing whether it was rank F or rank Nation. Both icons here come
        // from GateVisuals.ColorFor, so the pin is the colour of the gate the
        // party will actually walk through when they arrive.
        //
        // Two members, because RimWorld uses two: Material is the small quad
        // drawn zoomed IN, ExpandingIcon is what replaces it once the camera
        // pulls back far enough for the planet to go schematic. Overriding only
        // one leaves the other vanilla, and the change appears to come and go
        // with the zoom level.
        private MobRankTier IconRank =>
            GetComponent<WorldObjectComp_DungeonContract>()?.rank ?? MobRankTier.F;

        public override Texture2D ExpandingIcon =>
            GateVisuals.WorldIcon(IconRank) ?? base.ExpandingIcon;

        public override Material Material
        {
            get
            {
                Texture2D tex = GateVisuals.WorldIcon(IconRank);
                if (tex == null)
                    return base.Material;
                var req = new MaterialRequest(tex, ShaderDatabase.WorldOverlayTransparentLit,
                                              Color.white)
                {
                    renderQueue = WorldMaterials.WorldObjectRenderQueue,
                };
                return MaterialPool.MatFrom(req);
            }
        }
    }
}
