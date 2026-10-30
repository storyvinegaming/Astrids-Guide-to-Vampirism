// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Feature 1, without Harmony. DFU's mod system compiles a mod's C# source at runtime from a
// reference set that does not include HarmonyLib, so the Harmony-based prefix patch this mod
// originally used on DaggerfallRestWindow's private DoRestForAWhile()/DoRestUntilHealed() could
// never actually load in a real installed .dfmod (it only worked when compiled inside the Unity
// Editor, which does have Harmony on hand - two different compilation contexts). This replaces that
// patch with a plain polling watcher against PlayerEntity.CurrentRestMode, which is a public
// get/set property DaggerfallRestWindow's own (protected) currentRestMode field just forwards to -
// no reflection, no private-member access, no third-party library needed.
//
// VoxsVampirismEffect.CheckStartRest() still always returns true, so a hungry vampire can open the
// Rest window and reach Loiter. This watcher catches the moment CurrentRestMode is set to FullRest or
// TimedRest (DoRestUntilHealed sets it immediately; DoRestForAWhile sets it once the player answers
// the "how many hours" prompt) and snaps it back to Selection before real time can accumulate -
// DaggerfallRestWindow.TickRest() only progresses rest in increments gated by
// Time.realtimeSinceStartup, so catching this on the very next Update() means nothing has actually
// ticked yet. One visible side effect this doesn't fully avoid (unlike the original Harmony prefix,
// which blocked before the window's own MoveToBed() ever ran): DoRestUntilHealed/the hours-prompt
// callback can reposition the player to a bed marker for a single frame before this snaps the mode
// back - cosmetic only, no time or vitals are actually affected.
//
// Gated on this mod's own Blood Point pool (IsBloodEmpty(), 0 points) rather than vanilla's own
// binary satiation flag (VampirismEffect.IsSatiated()) - vanilla's own satiation window is much
// shorter than it takes to run a full blood pool dry in this mod's own economy, so gating on
// IsSatiated() blocked full rest far more readily than intended. Only a vampire who has genuinely
// spent themselves dry loses the ability to sleep properly.
//
// This also means the gate now applies to ANY code path that sets PlayerEntity.CurrentRestMode to
// FullRest/TimedRest, not just DaggerfallRestWindow's own two methods specifically - broader than
// the original design, but arguably more consistent with "a starving vampire can't fully rest"
// regardless of which UI triggered it.

using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace VoxsVampirismMod
{
    public class RestModeGateWatcher : MonoBehaviour
    {
        const int notSatedTextID = 36;

        void Update()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null || !vampirism.IsBloodEmpty())
                return;

            DaggerfallRestWindow.RestModes mode = GameManager.Instance.PlayerEntity.CurrentRestMode;
            if (mode != DaggerfallRestWindow.RestModes.FullRest && mode != DaggerfallRestWindow.RestModes.TimedRest)
                return;

            GameManager.Instance.PlayerEntity.CurrentRestMode = DaggerfallRestWindow.RestModes.Selection;

            // Guard against message-box spam: if CurrentRestMode somehow gets set back to
            // FullRest/TimedRest on consecutive frames (e.g. by something else re-entering that mode
            // faster than this watcher can reset it), don't stack a new messagebox on top of one
            // we're already showing for the same gate event.
            if (DaggerfallUI.Instance.UserInterfaceManager.TopWindow is DaggerfallMessageBox)
                return;

            DaggerfallMessageBox mb = new DaggerfallMessageBox(DaggerfallUI.Instance.UserInterfaceManager, null, wrapText: true);
            mb.PreviousWindow = DaggerfallUI.Instance.UserInterfaceManager.TopWindow;
            mb.ClickAnywhereToClose = true;
            mb.SetTextTokens(notSatedTextID);
            mb.Show();
        }
    }
}
