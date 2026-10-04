using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Entry point for the Isekai Dungeons Add-on.
    ///
    /// The dungeon layer itself rides vanilla extension points (MapPortal subclass,
    /// MapGeneratorDef.customMapComponents, GenSteps, an IncidentWorker). Harmony is
    /// used only where vanilla exposes no hook: wildlife suppression, quest-loot
    /// scrolls, the autopilot gizmo, music, and the tower/godhood patches. The
    /// per-type patch loop below applies each patch class individually, so a single
    /// broken patch does not abort all the others (house convention shared with the
    /// Creatures and Guild add-ons).
    /// </summary>
    public class IsekaiDungeonsMod : Mod
    {
        public const string HarmonyId = "Poupun.IsekaiDungeonsAddon";
        public const string LogPrefix = "[IsekaiDungeons]";

        /// <summary>Player-facing tuning. Resolved once, in the mod constructor.</summary>
        public static IsekaiDungeonsSettings Settings { get; private set; }

        public IsekaiDungeonsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<IsekaiDungeonsSettings>();

            var harmony = new Harmony(HarmonyId);
            int patched = 0;
            int skipped = 0;

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                try
                {
                    var patchMethods = HarmonyMethodExtensions.GetFromType(type);
                    if (patchMethods != null && patchMethods.Any())
                    {
                        harmony.CreateClassProcessor(type).Patch();
                        patched++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    Log.Warning($"{LogPrefix} Skipped Harmony patch {type.Name}: {ex.Message}");
                }
            }

            if (skipped == 0)
                Log.Message($"{LogPrefix} Isekai Dungeons Add-on loaded ({patched} patches applied).");
            else
                Log.Warning($"{LogPrefix} Isekai Dungeons Add-on loaded with {skipped} skipped patch(es) ({patched} applied).");
        }

        public override string SettingsCategory() => "Isekai Dungeons";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            SettingsWindow.Draw(inRect, Settings);
        }
    }
}
