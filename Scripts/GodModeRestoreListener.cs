// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Feature 5, without Harmony. VampirismInfection.DeployFullBlownVampirism() (the method that
// instantly advances time two weeks and teleports the player into a crypt) is private with no
// virtual/override hook, and DFU's mod system compiles a mod's source at runtime from a reference
// set that doesn't include HarmonyLib - the original Harmony prefix/postfix patch on that method
// could never actually load in a real installed .dfmod (see RestModeGateWatcher.cs's header for the
// same underlying constraint). This achieves the same GodMode bracketing entirely through public,
// observable UI/effect state instead of patching the method directly.
//
// DeployFullBlownVampirism runs synchronously from the death video's (ANIM0012.VID) OnClose handler
// (see VampirismInfection.ProgressDisease), so watching for that video window is a reliable proxy
// for "the transformation sequence is about to run": GodMode is forced on as soon as the video
// window is detected on top, and stays on continuously through the window closing and the whole
// synchronous DeployFullBlownVampirism call that follows from its own OnClose - by the time this
// component's own Update() next runs and notices the video window is gone, that call has already
// completed either way, so exact frame-level ordering here doesn't matter. This also fires around
// the earlier warning dream video (ANIM0004.VID, shown on day 1 of the infection) since both videos
// play while VampirismInfection is the active incumbent effect - harmless overkill, not narrowed
// further since neither video is otherwise distinguishable without private-field access.
//
// Once the video is gone, GodMode is handed back to whatever it was before (not forced off).
//
// IMPORTANT: this component never actually ran at all through several rounds of testing, because
// VoxsVampirism.ClimateAndCaloriesInstalled was false the whole time (see VoxsVampirism.cs's
// DetectClimateAndCalories - the mod title match was wrong: "Climate & Calories" singular instead of
// the real "Climates & Calories" plural). That means the "Godmode enabled: True/False" log lines
// observed during that testing, and the death that immediately followed them, were NOT this
// component - most likely a manual console "god" toggle used to check inventory mid-test. With
// detection fixed, this is the first time this component's own logic is actually exercised.
//
// minRealSecondsBeforeRestore below (added during that same inconclusive round, restoring on
// movement plus a real-time floor rather than on movement alone) is kept as a reasonable defensive
// choice - Climate & Calories may still apply its hunger/exposure deficit in a lump sum rather than
// gradually - but treat it as unvalidated until a clean test (see this mod's engage/restore log
// lines below) actually confirms GodMode was on for the whole vulnerable window.
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace VoxsVampirismMod
{
    public class GodModeRestoreListener : MonoBehaviour
    {
        const float minRealSecondsBeforeRestore = 15f;

        bool engaged;
        bool waitingForMovement;
        bool restoreValue;
        float movementDetectedAtRealTime = -1f;

        void Update()
        {
            if (!VoxsVampirism.ClimateAndCaloriesInstalled)
                return;

            if (waitingForMovement)
            {
                CheckForMovementAndRestore();
                return;
            }

            bool vidPlaying = DaggerfallUI.Instance.UserInterfaceManager.TopWindow is DaggerfallVidPlayerWindow;

            if (!engaged)
            {
                bool infectionActive = GameManager.Instance.PlayerEffectManager.FindIncumbentEffect<VampirismInfection>() != null;
                if (infectionActive && vidPlaying)
                {
                    restoreValue = GameManager.Instance.PlayerEntity.GodMode;
                    GameManager.Instance.PlayerEntity.GodMode = true;
                    engaged = true;
                    VoxsVampirism.Log("GodModeRestoreListener: engaging. Saved pre-existing GodMode={0}, forced GodMode=true.", restoreValue);
                }
            }
            else if (!vidPlaying)
            {
                engaged = false;
                waitingForMovement = true;
                movementDetectedAtRealTime = -1f;
                VoxsVampirism.Log("GodModeRestoreListener: transformation video closed, GodMode staying true, now waiting for movement + {0}s buffer before restoring.", minRealSecondsBeforeRestore);
            }
        }

        void CheckForMovementAndRestore()
        {
            if (movementDetectedAtRealTime < 0f)
            {
                if (!HasMovementInput())
                    return;

                movementDetectedAtRealTime = Time.realtimeSinceStartup;
                VoxsVampirism.Log("GodModeRestoreListener: movement detected, starting {0}s restore buffer.", minRealSecondsBeforeRestore);
            }

            if (Time.realtimeSinceStartup - movementDetectedAtRealTime < minRealSecondsBeforeRestore)
                return;

            waitingForMovement = false;
            GameManager.Instance.PlayerEntity.GodMode = restoreValue;
            VoxsVampirism.Log("GodModeRestoreListener: restoring. GodMode set back to {0} (buffer elapsed after movement).", restoreValue);
        }

        static bool HasMovementInput()
        {
            InputManager input = InputManager.Instance;
            return input.HasAction(InputManager.Actions.MoveForwards)
                || input.HasAction(InputManager.Actions.MoveBackwards)
                || input.HasAction(InputManager.Actions.MoveLeft)
                || input.HasAction(InputManager.Actions.MoveRight)
                || input.HasAction(InputManager.Actions.Jump);
        }
    }
}
