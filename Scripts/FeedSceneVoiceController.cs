// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Plays a voice-over line for a Narrative Feeding Scene fragment, matched purely by id: a fragment
// with id "opening_residence_asleep_01" looks for an asset named "opening_residence_asleep_01.ogg" in
// the mod bundle. The recordings are being produced by a voice actor reading only each fragment's own
// text field, one clip per id - so the lookup is deliberately just the id with ".ogg" appended, no
// separate mapping table to keep in sync as new lines get recorded and dropped in.
//
// Missing clips fail gracefully by design - a line not yet recorded (or never going to be) should
// never block the scene, just play silently. Early development used a shared "test.ogg" placeholder
// here to make the wiring audibly verifiable before any real recordings existed; removed now that
// the real per-id recordings are in place.
//
// "Feed Scene Voice Acting" (Mod Settings, on by default) gates PlayForId's own actual playback -
// checked there rather than at each of NarrativeFeedSceneEngine's own four call sites, so disabling it
// mid-session still lets Stop() (called unconditionally, before the gate) cut anything already playing.
//
// Stop() is called from VoxsVampirism.ShowWrappedPromptThenAction/ShowYesNoPromptThenAction
// themselves, not from anywhere in NarrativeFeedSceneEngine.cs directly - every fragment box this mod
// ever shows (and every non-fragment prompt/menu everywhere else in the mod) is dismissed through one
// of those two shared helpers, so hooking the stop there catches "player continues to the next scene
// beat" AND "player exits the scene via Escape" uniformly, without needing a separate stop call at
// each of NarrativeFeedSceneEngine's own dozen scene-end call sites. Calling Stop() when nothing is
// playing (the overwhelming majority of the mod's own message boxes, which never had any voice line to
// begin with) is a cheap no-op, not a behavior change for anything outside a feeding scene.
//
// Independent dedicated AudioSource, not the shared DaggerfallUI.Instance.DaggerfallAudioSource this
// mod's own feeding/mist one-shot sounds already use - a voice line needs to be individually stoppable
// on demand (cut off mid-line when the player continues past it) without also cutting off any other
// one-shot SFX that happens to be playing on that same shared channel at the same moment.

using UnityEngine;
using DaggerfallWorkshop;

namespace VoxsVampirismMod
{
    public class FeedSceneVoiceController : MonoBehaviour
    {
        static FeedSceneVoiceController instance;

        AudioSource voiceSource;
        bool hasLoggedMissingClip;

        void Awake()
        {
            instance = this;

            voiceSource = gameObject.AddComponent<AudioSource>();
            voiceSource.loop = false;
            voiceSource.playOnAwake = false;
        }

        // Stops whatever's currently playing (if anything) regardless of id, then - only if id is
        // non-empty and a clip actually exists for it - plays that id's own clip. Called with a null/
        // empty id whenever a beat has no matched fragment at all (e.g. RunOpening's own fallback flat
        // text when nothing in the library matched), so any previously-playing line still gets cut even
        // though there's nothing new to play in its place. A missing clip likewise just leaves the
        // scene silent from here on, logged once so a genuinely missing recording is still noticeable
        // in Player.log without ever blocking play.
        public static void PlayForId(string id)
        {
            if (instance == null)
                return;

            instance.voiceSource.Stop();

            if (string.IsNullOrEmpty(id) || !VoxsVampirism.FeedSceneVoiceEnabled)
                return;

            AudioClip clip = VoxsVampirism.Mod.GetAsset<AudioClip>(id + ".ogg");
            if (clip == null)
            {
                if (!instance.hasLoggedMissingClip)
                {
                    instance.hasLoggedMissingClip = true;
                    VoxsVampirism.Log("FeedSceneVoiceController: no clip found for '{0}.ogg' - playing silently.", id);
                }
                return;
            }

            instance.voiceSource.volume = DaggerfallUnity.Settings.SoundVolume;
            instance.voiceSource.clip = clip;
            instance.voiceSource.Play();
        }

        public static void Stop()
        {
            if (instance == null)
                return;

            instance.voiceSource.Stop();
        }
    }
}
