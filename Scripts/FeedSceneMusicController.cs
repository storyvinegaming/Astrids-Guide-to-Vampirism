// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Fades the game's own music out and a custom heartbeat track in when a Narrative Feeding Scene
// starts, and reverses it when the scene ends - however it ends. Not just the final resolution box
// closing, but also an immediate Withdraw, an aborted complication, and every reveal/hunter-arrival/
// temple-massacre handoff into real combat, none of which should leave the heartbeat track playing
// over a fight - see NarrativeFeedSceneEngine.cs's own call sites into OnSceneEnd for the full set.
//
// "Feed Scene Music" (Mod Settings, on by default) gates OnSceneStart entirely - when off, sceneActive
// never becomes true, so OnSceneEnd/Update's own fade logic (both already gated on sceneActive) simply
// never engage and the player's normal music plays through scenes untouched, no separate check needed
// anywhere else in this file.
//
// DaggerfallSongPlayer.AudioSource.volume is overwritten every frame by SongManager.Update()
// (confirmed by reading both directly) - Gain (0-10, the MIDI synth's own output multiplier in
// OnAudioFilterRead, not fought by anything else) is the one lever that survives being set
// externally, so that's what's faded instead of the AudioSource volume. SongManager.StopPlaying()/
// StartPlaying() (not DaggerfallSongPlayer.Stop()/Play() directly - SongManager's own Update() would
// just restart it next frame otherwise) gate whether vanilla music plays at all; SongManager keeps
// tracking weather/time/location context even while stopped, so calling StartPlaying() again always
// resumes whatever track context currently calls for (not necessarily the exact track interrupted,
// and always restarted from position 0 - DaggerfallSongPlayer has no resume-from-position support).
//
// The custom heartbeat clip plays on its own dedicated AudioSource - never DaggerfallSongPlayer's own
// (frame-stomped) or the shared DaggerfallUI.Instance.DaggerfallAudioSource (a one-shot SFX channel
// this mod's own feeding/mist sounds already share, not appropriate for an independently-faded loop).
//
// No fade utility exists anywhere in DFU (checked directly - the only "fade" hits in engine source are
// the unrelated screen-fade system) - this is a from-scratch Update()-driven lerp, same "static
// trigger, instance Update() does the work" shape FeedFlashVisuals.cs already established for a
// different kind of fade, adapted here with a real singleton instance reference since (unlike that
// file) actual Play()/Stop()/StartPlaying() calls need to happen exactly at each trigger, not just
// whenever Update() next happens to notice a flag changed.
//
// DaggerfallUI.Instance.DaggerfallSongPlayer returned null in live testing while the "Dynamic Music"
// mod (github.com/numidium/dfu-mods, DynamicMusic/) was active - confirmed via a NullReferenceException
// that fired before the heartbeat track ever got a chance to play at all, since the whole method threw
// first. Every access to it now goes through TryGetSongPlayer()/TryGetSongManager(), both null-safe
// with a FindObjectOfType fallback in case some other mod moved it - if genuinely unavailable, the
// vanilla-music side of the swap (the fade-out, the Gain restore, the StopPlaying/StartPlaying calls)
// is silently skipped, but the heartbeat track itself still plays and fades normally either way, so a
// missing DaggerfallSongPlayer degrades to "no music fades out, but the heartbeat still plays" rather
// than "nothing works at all."
//
// Dynamic Music compatibility (best-effort, via reflection): Dynamic Music (public source at
// github.com/numidium/dfu-mods/tree/master/DynamicMusic/Scripts) plays through its own entirely
// separate DynamicSongPlayer, not vanilla's DaggerfallSongPlayer/SongManager at all - and unlike
// vanilla, there is NO passive external lever here. Confirmed by reading both of its own scripts
// directly: DynamicSongPlayer.Update() derives its own Gain FROM AudioSource.volume every frame
// ("Gain = AudioSource.volume * 5f"), so writing Gain externally gets stomped back instantly. The
// obvious fallback - fading AudioSource.volume directly - ALSO gets stomped, because the higher-level
// DynamicMusic.Update() itself unconditionally sets "dynamicSongPlayer.AudioSource.volume =
// DaggerfallUnity.Settings.MusicVolume" every frame in its own State.Normal (and drives its own
// competing Lerp during its own FadingIn/FadingOut states) - it owns that property outright, the same
// way vanilla's SongManager owns AudioSource.volume (just without vanilla's one safe side-channel,
// Gain, since Dynamic Music derives that FROM volume instead of leaving it alone). The only reliable
// way to take manual control is to stop DynamicMusic's own Update() from running at all for the
// scene's duration: DynamicMusic.Instance is itself a MonoBehaviour (a plain Unity Behaviour.enabled
// toggle, not a per-frame race - unlike the PlayerActivate/MesmerizeGazeWatcher situation earlier,
// this only needs to hold a state across many frames, not win a single-frame ordering race, so
// execution order between the two mods' components doesn't matter here). Disabled at scene start
// (before any fade begins, so nothing fights the fade-in), manually faded via AudioSource.volume
// while disabled, and re-enabled only once the fade-out has fully completed and volume is already
// back at its original value - a clean handoff with no audible jump. Since it's a separate mod's own
// compiled assembly, there's no compile-time type reference possible - every access goes through
// reflection instead (an AppDomain assembly scan for the type, cached once since it never changes
// across a session). Every step degrades silently (logged once, not every frame) if Dynamic Music
// isn't installed, or a future version of it renames something this depends on.

using System;
using System.Reflection;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace VoxsVampirismMod
{
    public class FeedSceneMusicController : MonoBehaviour
    {
        const float fadeDurationSeconds = 1.5f; // flagged, adjustable

        static FeedSceneMusicController instance;

        AudioSource heartbeatSource;
        AudioClip heartbeatClip;

        float fadeStartTime = -1f;
        bool fadingIn;
        bool sceneActive;
        float capturedOriginalGain = 5f;
        float fadeFromHeartbeatVolume;
        float fadeFromGain;

        // Dynamic Music's own AudioSource.volume, faded in parallel while its own Behaviour is
        // disabled - see file header for why disabling it is necessary at all.
        float capturedOriginalDynamicMusicVolume = 1f;
        float fadeFromDynamicMusicVolume;
        bool disabledDynamicMusic;

        // True once we've logged the "couldn't find it" cases once - avoids re-logging every single
        // scene start/end/frame for the lifetime of a session where it's simply not present at all.
        bool hasLoggedMissingSongPlayer;
        bool hasLoggedMissingSongManager;
        bool hasLoggedMissingDynamicMusic;

        // Resolved once (the assembly scan is the expensive part) and reused for the rest of the
        // session - null forever once scanned if Dynamic Music genuinely isn't installed.
        static bool dynamicMusicTypeScanned;
        static Type dynamicMusicType;

        void Awake()
        {
            instance = this;

            heartbeatSource = gameObject.AddComponent<AudioSource>();
            heartbeatSource.loop = true;
            heartbeatSource.playOnAwake = false;
            heartbeatSource.volume = 0f;

            heartbeatClip = VoxsVampirism.Mod.GetAsset<AudioClip>("vampire_heartbeat.ogg");
            if (heartbeatClip != null)
                heartbeatSource.clip = heartbeatClip;
            else
                VoxsVampirism.Log("FeedSceneMusicController: 'vampire_heartbeat.ogg' not found in the mod bundle - scene music swap disabled.");
        }

        DaggerfallSongPlayer TryGetSongPlayer()
        {
            DaggerfallSongPlayer songPlayer = DaggerfallUI.Instance != null ? DaggerfallUI.Instance.DaggerfallSongPlayer : null;
            if (songPlayer == null)
                songPlayer = UnityEngine.Object.FindObjectOfType<DaggerfallSongPlayer>();

            if (songPlayer == null && !hasLoggedMissingSongPlayer)
            {
                hasLoggedMissingSongPlayer = true;
                VoxsVampirism.Log("FeedSceneMusicController: no DaggerfallSongPlayer found anywhere in the scene - vanilla music won't fade, but the heartbeat track still will.");
            }

            return songPlayer;
        }

        SongManager TryGetSongManager()
        {
            SongManager songManager = DaggerfallUI.Instance != null ? DaggerfallUI.Instance.GetComponent<SongManager>() : null;
            if (songManager == null)
                songManager = UnityEngine.Object.FindObjectOfType<SongManager>();

            if (songManager == null && !hasLoggedMissingSongManager)
            {
                hasLoggedMissingSongManager = true;
                VoxsVampirism.Log("FeedSceneMusicController: no SongManager found anywhere in the scene - vanilla music won't be stopped/resumed.");
            }

            return songManager;
        }

        // Best-effort Dynamic Music reflection lookup - see file header. Returns the live
        // DynamicMusic.Instance itself (as a Behaviour, for the enabled toggle) and, via the out
        // parameter, its own DynamicSongPlayer's AudioSource (the actual volume lever) - or
        // null/null if Dynamic Music isn't installed or its internals don't match what this was
        // written against.
        Behaviour TryGetDynamicMusicBehaviour(out AudioSource audioSource)
        {
            audioSource = null;

            if (!dynamicMusicTypeScanned)
            {
                dynamicMusicTypeScanned = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type candidate = assembly.GetType("DynamicMusic.DynamicMusic");
                    if (candidate != null)
                    {
                        dynamicMusicType = candidate;
                        VoxsVampirism.Log("FeedSceneMusicController: Dynamic Music detected - will take over its own volume control during scenes.");
                        break;
                    }
                }
            }

            if (dynamicMusicType == null)
                return null; // not installed - the common case, not worth logging

            PropertyInfo instanceProperty = dynamicMusicType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            object dynamicMusicInstance = instanceProperty != null ? instanceProperty.GetValue(null, null) : null;
            Behaviour behaviour = dynamicMusicInstance as Behaviour;

            if (behaviour != null)
            {
                FieldInfo songPlayerField = dynamicMusicType.GetField("dynamicSongPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
                object songPlayer = songPlayerField != null ? songPlayerField.GetValue(dynamicMusicInstance) : null;
                if (songPlayer != null)
                {
                    PropertyInfo audioSourceProperty = songPlayer.GetType().GetProperty("AudioSource", BindingFlags.Public | BindingFlags.Instance);
                    audioSource = audioSourceProperty != null ? audioSourceProperty.GetValue(songPlayer, null) as AudioSource : null;
                }
            }

            if ((behaviour == null || audioSource == null) && !hasLoggedMissingDynamicMusic)
            {
                hasLoggedMissingDynamicMusic = true;
                VoxsVampirism.Log("FeedSceneMusicController: Dynamic Music detected but couldn't be fully reached via reflection - skipping compatibility handling for it.");
            }

            return behaviour != null && audioSource != null ? behaviour : null;
        }

        // Called once, right before a Narrative Feeding Scene's first beat actually shows (never for
        // the classic-flat-menu fallback path, which isn't a real scene) - see
        // NarrativeFeedSceneEngine.TryStartScene.
        public static void OnSceneStart()
        {
            if (instance == null || instance.heartbeatClip == null || instance.sceneActive || !VoxsVampirism.FeedSceneMusicEnabled)
                return;

            instance.sceneActive = true;
            instance.fadingIn = true;
            instance.fadeStartTime = Time.unscaledTime;
            instance.fadeFromHeartbeatVolume = instance.heartbeatSource.volume;

            DaggerfallSongPlayer songPlayer = instance.TryGetSongPlayer();
            instance.fadeFromGain = songPlayer != null ? songPlayer.Gain : 5f;
            instance.capturedOriginalGain = instance.fadeFromGain;

            // Disabled BEFORE the fade starts, not after - Dynamic Music fights AudioSource.volume
            // every frame in its own Update(), so the fade-in itself would never visibly progress
            // unless its own Update() is already stopped first (see file header).
            AudioSource dmAudioSource;
            Behaviour dmBehaviour = instance.TryGetDynamicMusicBehaviour(out dmAudioSource);
            instance.disabledDynamicMusic = dmBehaviour != null;
            if (dmBehaviour != null)
            {
                dmBehaviour.enabled = false;
                instance.fadeFromDynamicMusicVolume = dmAudioSource.volume;
                instance.capturedOriginalDynamicMusicVolume = instance.fadeFromDynamicMusicVolume;
            }

            instance.heartbeatSource.Play();
            VoxsVampirism.Log("FeedSceneMusicController: scene started - fading to heartbeat track (originalGain={0:F1}).", instance.capturedOriginalGain);
        }

        // Called from every terminal point a scene can end at, whatever form that ending takes -
        // idempotent/safe to call from a beat that never actually calls OnSceneStart's counterpart
        // twice (guarded by sceneActive), so no call site needs to reason about whether the scene
        // "really" started before ending.
        public static void OnSceneEnd()
        {
            if (instance == null || !instance.sceneActive)
                return;

            instance.fadingIn = false;
            instance.fadeStartTime = Time.unscaledTime;
            instance.fadeFromHeartbeatVolume = instance.heartbeatSource.volume;

            DaggerfallSongPlayer songPlayer = instance.TryGetSongPlayer();
            instance.fadeFromGain = songPlayer != null ? songPlayer.Gain : 0f;

            if (instance.disabledDynamicMusic)
            {
                AudioSource dmAudioSource;
                instance.TryGetDynamicMusicBehaviour(out dmAudioSource);
                instance.fadeFromDynamicMusicVolume = dmAudioSource != null ? dmAudioSource.volume : 0f;
            }

            // Restarts vanilla music now, silently (Gain is still faded down/at 0) - Update() below
            // brings Gain back up over the same duration the heartbeat track fades out, and starting
            // it now rather than after the fade completes lets SongManager pick whatever track
            // context currently calls for immediately, rather than snapshotting a stale choice.
            SongManager songManager = instance.TryGetSongManager();
            if (songManager != null)
                songManager.StartPlaying();

            VoxsVampirism.Log("FeedSceneMusicController: scene ended - fading back to normal music.");
        }

        void Update()
        {
            // Safety net for however a scene's own UI gets abandoned without going through any of
            // NarrativeFeedSceneEngine's own OnSceneEnd call sites - confirmed this needs to exist:
            // DaggerfallPopupWindow (the base class every window this mod's scenes use, directly or
            // via DaggerfallMessageBox, ultimately derives from) has its own built-in Escape-to-cancel
            // handling (GetBackButtonUp() -> CancelWindow() -> a separate OnCancel event, entirely
            // distinct from OnItemPicked/the click-driven follow-up actions this mod's own scene code
            // hooks) - pressing Escape closes the window without ever running the Withdraw/resolution
            // logic those follow-ups carry, which is what actually calls OnSceneEnd() everywhere else.
            //
            // Gated on fadeStartTime < 0f (no fade CURRENTLY running) - without this guard, the check
            // below is ALSO true for the entire duration of a normal fade-OUT (TopWindow is
            // legitimately HUD while the heartbeat fades back down - that's the expected end state,
            // not abandonment), which would fire every single frame of that fade, each call resetting
            // fadeStartTime back to "now" via OnSceneEnd's own unconditional timestamp write - a
            // self-sustaining retrigger loop that never lets the fade finish (confirmed in a live-
            // testing regression this exact form was written to fix). Restricting this to the steady
            // state (fully faded to the heartbeat, no fade in-flight, waiting on the next beat) makes
            // it a one-shot detector - safe against normal beat-to-beat progression for the same reason
            // as always: every transition happens synchronously, never leaving the window stack empty
            // for a full rendered frame outside of true abandonment or this steady state.
            if (sceneActive && fadeStartTime < 0f && DaggerfallUI.UIManager.TopWindow is DaggerfallHUD)
            {
                VoxsVampirism.Log("FeedSceneMusicController: scene UI abandoned (Escape or similar) - treating as scene end.");
                OnSceneEnd();
            }

            if (fadeStartTime < 0f || heartbeatClip == null)
                return;

            float t = Mathf.Clamp01((Time.unscaledTime - fadeStartTime) / fadeDurationSeconds);
            DaggerfallSongPlayer songPlayer = TryGetSongPlayer();

            float targetHeartbeatVolume = fadingIn ? DaggerfallUnity.Settings.MusicVolume : 0f;
            float targetGain = fadingIn ? 0f : capturedOriginalGain;
            float targetDynamicMusicVolume = fadingIn ? 0f : capturedOriginalDynamicMusicVolume;

            heartbeatSource.volume = Mathf.Lerp(fadeFromHeartbeatVolume, targetHeartbeatVolume, t);
            if (songPlayer != null)
                songPlayer.Gain = Mathf.Lerp(fadeFromGain, targetGain, t);

            if (disabledDynamicMusic)
            {
                AudioSource dmAudioSource;
                TryGetDynamicMusicBehaviour(out dmAudioSource);
                if (dmAudioSource != null)
                    dmAudioSource.volume = Mathf.Lerp(fadeFromDynamicMusicVolume, targetDynamicMusicVolume, t);
            }

            if (t < 1f)
                return;

            fadeStartTime = -1f;
            if (fadingIn)
            {
                // Fully faded to the heartbeat track - cleanly stop vanilla's own MIDI processing
                // rather than leaving it silently running at Gain 0.
                SongManager songManager = TryGetSongManager();
                if (songManager != null)
                    songManager.StopPlaying();
            }
            else
            {
                heartbeatSource.Stop();
                sceneActive = false;

                // Hand control back to Dynamic Music only now that its own volume is already sitting
                // at (or very near) capturedOriginalDynamicMusicVolume - a clean handoff, no audible
                // jump, since its own Update() will just keep setting the same value from here.
                if (disabledDynamicMusic)
                {
                    AudioSource dmAudioSource;
                    Behaviour dmBehaviour = TryGetDynamicMusicBehaviour(out dmAudioSource);
                    if (dmBehaviour != null)
                        dmBehaviour.enabled = true;
                    disabledDynamicMusic = false;
                }
            }
        }
    }
}
