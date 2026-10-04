using System;
using System.Collections.Generic;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Music that follows the player into a dungeon and back out.
    ///
    /// WHAT THIS IS SCORING: the map the player is LOOKING AT, not where their
    /// pawns are. Switching to a dungeon starts its theme; switching away ends
    /// it. A caravan left fighting on floor 40 while the player watches the
    /// colony gets colony music, which is the right answer — the score is for
    /// the person at the keyboard.
    ///
    /// HOW THE TRANSITION WORKS, and what it cannot be. RimWorld's music
    /// manager owns exactly one AudioSource, so a true crossfade with both
    /// tracks audible at once is not available at all. What is available is
    /// ForceFadeoutAndSilenceFor, which rides the manager's own fade-out
    /// envelope and schedules the next pick — so the shape is fade the old one
    /// down, a beat of quiet, fade the new one up. Using vanilla's own fade
    /// rather than fighting it also means pausing, volume prefs and the
    /// application-focus duck all keep working.
    ///
    /// The pick itself lands through a ChooseNextSong postfix, the same funnel
    /// the main mod already uses. Ours runs LAST on purpose — see the patch.
    ///
    /// DEFERS TO THE MAIN MOD'S SETTINGS but overrides its choice. If the
    /// player has the Isekai OST switched off, this is off too, and the volume
    /// slider governs both. What it does NOT honour is "replaces vanilla": in a
    /// dungeon the dungeon theme always wins, because a location score that
    /// only plays sometimes reads as a fault rather than as variety.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DungeonOST
    {
        // ── Tracks ──────────────────────────────────────────────────────────

        private const string LowBand    = "IsekaiDungeons_OST_LowBand";     // F–D
        private const string MidBand    = "IsekaiDungeons_OST_MidBand";     // C–A
        private const string HighBand   = "IsekaiDungeons_OST_HighBand";    // S–SSS and Nation
        private const string TowerLower = "IsekaiDungeons_OST_TowerLower";  // floors 1–50
        private const string TowerUpper = "IsekaiDungeons_OST_TowerUpper";  // floors 51–100

        /// <summary>The last floor on the lower tower theme. A setting (Interface tab).</summary>
        private static int TowerBandSplit => IsekaiDungeonsSettings.Current.towerMusicSplitFloor;

        // ── Transition shape ────────────────────────────────────────────────
        // All three are settings (Interface tab); the defaults are the values
        // described here.

        /// <summary>
        /// Seconds to fade the outgoing track down. Long enough to read as a
        /// deliberate transition rather than a cut, short enough that walking
        /// through a gate does not spend a quarter of a minute in limbo. 2.5.
        /// </summary>
        private static float FadeOutSeconds => Mathf.Max(0.1f, IsekaiDungeonsSettings.Current.musicFadeOutSeconds);

        /// <summary>Quiet between the two tracks, on top of the fade. 1.2.</summary>
        private static float GapSeconds => Mathf.Max(0f, IsekaiDungeonsSettings.Current.musicGapSeconds);

        /// <summary>
        /// Seconds of fade-in on the incoming track. MUST stay above zero:
        /// EnvelopeAt divides by it, so it is floored here as well as clamped
        /// by the settings. 3.
        /// </summary>
        private static float FadeInSeconds => Mathf.Max(0.1f, IsekaiDungeonsSettings.Current.musicFadeInSeconds);

        /// <summary>
        /// A dungeon theme is a LOOP, not an entry in a playlist. Vanilla waits
        /// 85 to 105 seconds between songs in its relaxed mode, which is right
        /// for a colony and is dead air in a dungeon, so when a dungeon track
        /// ends the next one is pulled forward to about this.
        /// </summary>
        private const float LoopGapSeconds = 2f;

        // ── Resolution ──────────────────────────────────────────────────────

        private static readonly Dictionary<string, SongDef> cache = new Dictionary<string, SongDef>();
        private static List<SongDef> allOurs;

        private static SongDef Song(string defName)
        {
            if (cache.TryGetValue(defName, out SongDef s) && s != null)
                return s;
            s = DefDatabase<SongDef>.GetNamedSilentFail(defName);
            if (s != null)
                cache[defName] = s;
            return s;
        }

        /// <summary>Every track this class owns, for the tense sync.</summary>
        private static List<SongDef> AllOurs
        {
            get
            {
                if (allOurs == null || allOurs.Count == 0)
                {
                    allOurs = new List<SongDef>();
                    foreach (string n in new[] { LowBand, MidBand, HighBand, TowerLower, TowerUpper })
                    {
                        SongDef s = Song(n);
                        if (s != null)
                            allOurs.Add(s);
                    }
                    // Per-dungeon themes, including other mods' (DungeonKindDef.music).
                    foreach (DungeonKindDef k in DefDatabase<DungeonKindDef>.AllDefsListForReading)
                    {
                        if (k.music != null && !allOurs.Contains(k.music))
                            allOurs.Add(k.music);
                    }
                }
                return allOurs;
            }
        }

        public static bool IsOurs(SongDef song) =>
            song != null && song.defName != null
            && (song.defName.StartsWith("IsekaiDungeons_OST_", StringComparison.Ordinal) || AllOurs.Contains(song));

        // ── What should be playing ──────────────────────────────────────────

        /// <summary>
        /// The track this map calls for, or null when the map is not ours and
        /// the ordinary rotation should have it back.
        ///
        /// Returns the DEF rather than playing anything, so the same function
        /// answers "what should be playing" and "has that changed" without a
        /// second source of truth.
        /// </summary>
        public static SongDef TrackFor(Map map)
        {
            if (map == null)
                return null;

            // The tower first: its floors are dungeons too, and they have their
            // own music, so asking about rank first would answer wrongly.
            if (map.Parent is TowerFloorMapParent floor)
            {
                return Song(floor.floor <= TowerBandSplit ? TowerLower : TowerUpper);
            }

            if (!(map.Parent is DungeonMapParent dungeon))
                return null;

            if (dungeon.kindDef?.music != null)
                return dungeon.kindDef.music;

            if (dungeon.rank <= MobRankTier.D)
                return Song(LowBand);
            if (dungeon.rank <= MobRankTier.A)
                return Song(MidBand);
            return Song(HighBand);
        }

        /// <summary>The track the map on screen calls for.</summary>
        public static SongDef Wanted =>
            IsekaiLevelingSettings.EnableIsekaiOST && IsekaiDungeonsSettings.Current.dungeonMusicEnabled
                ? TrackFor(Find.CurrentMap)
                : null;

        // ── Danger ──────────────────────────────────────────────────────────

        /// <summary>
        /// Keeps every dungeon track's tense flag equal to the current danger
        /// state.
        ///
        /// MusicManagerPlay fades the playing song out the moment its tense
        /// flag disagrees with DangerMusicMode. A dungeon has exactly one theme
        /// and it is meant to carry the whole visit, so leaving the flag false
        /// would mean: boss wakes up, danger mode flips, the dungeon's own
        /// music fades out, the replacement is the same track and fades out
        /// too. Rewriting the flag to match the moment is what lets one track
        /// hold the room through a fight. The main mod does the same for its
        /// pool; the field is runtime-only and never saved.
        /// </summary>
        public static void SyncTense(bool tense)
        {
            List<SongDef> all = AllOurs;
            for (int i = 0; i < all.Count; i++)
                all[i].tense = tense;
        }

        // ── The transition ──────────────────────────────────────────────────

        private static SongDef lastWanted;
        private static bool initialised;

        /// <summary>
        /// Runs BEFORE vanilla's own per-frame music logic, and the ordering is
        /// the entire point.
        ///
        /// MusicUpdate re-checks the playing song's tense flag against
        /// DangerMusicMode and calls StartFadeout(10f) the moment they
        /// disagree. Syncing the flag from the postfix was one frame too late:
        /// on the frame a fight started, vanilla had already begun a ten second
        /// fade of the dungeon's own theme, and by the time the flag was
        /// corrected the fadeout was running and could not be called off. From
        /// a prefix the flag is already right when vanilla looks at it.
        /// </summary>
        public static void PreUpdate(MusicManagerPlay manager)
        {
            try
            {
                if (manager == null || manager.disabled)
                    return;
                if (Wanted != null)
                    SyncTense(manager.DangerMusicMode);
            }
            catch
            {
                // Never throw out of the per-frame audio update.
            }
        }

        /// <summary>
        /// Per-frame watch. Notices the viewed map changing what it wants and
        /// starts the handover.
        ///
        /// Never throws: this runs from the audio update, and an exception on a
        /// per-frame path repeats forever.
        /// </summary>
        public static void Update(MusicManagerPlay manager)
        {
            try
            {
                if (manager == null || manager.disabled)
                    return;

                SongDef wanted = Wanted;

                if (!initialised)
                {
                    // First frame of a session or a load: adopt the current
                    // state rather than treating it as a change, or loading a
                    // save inside a dungeon would fade out music that was
                    // already correct.
                    initialised = true;
                    lastWanted = wanted;
                    return;
                }

                if (wanted != lastWanted)
                {
                    lastWanted = wanted;

                    // Both directions go through the same fade. Leaving a
                    // dungeon deserves the handover as much as entering one:
                    // cutting from a boss theme straight to a farm is worse
                    // than the silence.
                    if (manager.IsPlaying)
                        manager.ForceFadeoutAndSilenceFor(FadeOutSeconds + GapSeconds, FadeOutSeconds);
                    else
                        manager.ScheduleNewSong();
                    return;
                }

                // A dungeon theme loops. Once the track has ended, vanilla
                // would sit in silence for up to a hundred seconds before
                // asking for another; pull that forward so the room keeps its
                // score. Only while a dungeon is on screen — the colony keeps
                // vanilla's breathing room.
                if (wanted != null && !manager.IsPlaying && manager.NextSongTimer > LoopGapSeconds)
                    manager.ScheduleNewSong();
            }
            catch (Exception ex)
            {
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Dungeon OST update failed; music falls back "
                              + $"to the ordinary rotation. {ex.GetType().Name}: {ex.Message}", 0x0D57A11);
            }
        }

        /// <summary>
        /// Fade envelope for a dungeon track.
        ///
        /// Only the head is shaped. The TAIL is deliberately left alone: this
        /// track is about to be followed by itself, and fading down and back up
        /// every few minutes would put a hole in a loop that is supposed to be
        /// seamless. When the player leaves the dungeon the fade-out comes from
        /// the manager's own envelope instead, which is what
        /// ForceFadeoutAndSilenceFor drives.
        /// </summary>
        public static float EnvelopeAt(float time) => Mathf.Clamp01(time / FadeInSeconds);

        /// <summary>
        /// Whether vanilla's music transitions should be allowed to fire.
        ///
        /// They must not while the player is looking at a dungeon, and the
        /// reason is that they are evaluated GLOBALLY. Every shipped transition
        /// worker walks Find.Maps (and the pocket map list) and asks whether
        /// its condition holds ANYWHERE — none of them has any notion of which
        /// map is on screen. HorrorRelax is satisfied by Gray Pall on any map,
        /// or by any map holding a PitGate, a FleshmassHeart or a Noctolith. So
        /// an Anomaly colony hijacks the tower's music from four hundred tiles
        /// away, and it does it by a route that never touches ChooseNextSong,
        /// which is why no amount of work on the song PICK could have fixed it.
        ///
        /// A dungeon is a sealed pocket map with its own score. Nothing
        /// happening in the world outside gets a vote on what plays inside it.
        /// </summary>
        public static bool TransitionsAllowed => Wanted == null;

        /// <summary>
        /// Tears down a sequence that is already running, so entering a dungeon
        /// mid-transition hands control back rather than waiting the sequence
        /// out. ForceFadeoutAndSilenceFor routes through StopAndFadeoutSequence,
        /// which clears both the worker and the triggered transition.
        /// </summary>
        public static void CancelRunningSequence(MusicManagerPlay manager)
        {
            if (manager?.TriggeredTransition == null)
                return;
            manager.ForceFadeoutAndSilenceFor(FadeOutSeconds + GapSeconds, FadeOutSeconds);
        }

        /// <summary>Forgets the cached defs and the watch state.</summary>
        public static void Reset()
        {
            cache.Clear();
            allOurs = null;
            lastWanted = null;
            initialised = false;
        }
    }

    /// <summary>
    /// Places the dungeon track.
    ///
    /// PRIORITY LAST, and that is the whole reason this works. The main mod
    /// also postfixes ChooseNextSong and rewrites the result to one of its own
    /// pool; Harmony runs higher-priority postfixes FIRST, so anything at
    /// normal priority could be overwritten by it depending on load order.
    /// Last means we get the final word, which is correct — inside a dungeon
    /// the dungeon's music outranks the general rotation.
    /// </summary>
    [HarmonyPatch(typeof(MusicManagerPlay), "ChooseNextSong")]
    public static class Patch_MusicManagerPlay_DungeonSong
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("JellyCreative.IsekaiLeveling")]
        public static void Postfix(ref SongDef __result)
        {
            try
            {
                SongDef wanted = DungeonOST.Wanted;
                if (wanted != null && wanted.clip != null)
                    __result = wanted;
            }
            catch (Exception ex)
            {
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Dungeon OST selection failed; keeping the "
                              + $"ordinary pick. {ex.GetType().Name}: {ex.Message}", 0x0D57A12);
            }
        }
    }

    /// <summary>
    /// Keeps vanilla's music transitions out of a dungeon.
    ///
    /// This is the patch that actually fixes "the music randomly jumps to
    /// something else and then comes back". See DungeonOST.TransitionsAllowed
    /// for why suppression rather than competition is the only option: the
    /// transition system does not route through the song pick at all, so there
    /// is nothing to out-vote.
    ///
    /// Suppressed only while a dungeon is the map on screen. On the colony,
    /// on the world map and with the feature switched off, every vanilla and
    /// DLC transition behaves exactly as it always has.
    /// </summary>
    [HarmonyPatch(typeof(MusicManagerPlay), nameof(MusicManagerPlay.CheckTransitions))]
    public static class Patch_MusicManagerPlay_HoldTransitions
    {
        [HarmonyPrefix]
        public static bool Prefix(MusicManagerPlay __instance)
        {
            try
            {
                if (DungeonOST.TransitionsAllowed)
                    return true;

                // Entered while a sequence was already running: end it once and
                // let the ordinary pick take over. Self-limiting, because the
                // teardown clears TriggeredTransition.
                DungeonOST.CancelRunningSequence(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Music transition guard failed; vanilla "
                              + $"transitions left alone. {ex.GetType().Name}: {ex.Message}", 0x0D57A13);
                return true;
            }
        }
    }

    /// <summary>
    /// Drives the watch, and applies the fade-in and the main mod's OST volume
    /// to dungeon tracks.
    ///
    /// MusicUpdate reassigns audioSource.volume from scratch every frame, so
    /// scaling it in a postfix cannot compound. The main mod's own fade postfix
    /// deliberately does not touch these tracks — it tests its own defName
    /// prefix, and ours is different — so this is the only thing shaping them.
    /// </summary>
    [HarmonyPatch(typeof(MusicManagerPlay), nameof(MusicManagerPlay.MusicUpdate))]
    public static class Patch_MusicManagerPlay_DungeonFade
    {
        private static readonly AccessTools.FieldRef<MusicManagerPlay, AudioSource> AudioSourceRef =
            AccessTools.FieldRefAccess<MusicManagerPlay, AudioSource>("audioSource");

        /// <summary>Sets the tense flag before vanilla reads it. See PreUpdate.</summary>
        [HarmonyPrefix]
        public static void Prefix(MusicManagerPlay __instance)
        {
            DungeonOST.PreUpdate(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(MusicManagerPlay __instance)
        {
            DungeonOST.Update(__instance);

            try
            {
                if (__instance.disabled || !__instance.IsPlaying)
                    return;
                if (!DungeonOST.IsOurs(__instance.CurrentSong))
                    return;

                AudioSource src = AudioSourceRef(__instance);
                if (src == null)
                    return;

                float factor = DungeonOST.EnvelopeAt(__instance.SongTime)
                             * IsekaiLevelingSettings.IsekaiOSTVolume;
                if (factor >= 0.999f)
                    return;

                src.volume = Mathf.Clamp01(src.volume * factor);
            }
            catch
            {
                // Never throw out of the per-frame audio update.
            }
        }
    }
}
