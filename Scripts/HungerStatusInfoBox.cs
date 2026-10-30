// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Feature 7 addition: appends a third messagebox, showing Blood Points and Humanity, after
// vanilla's own location+health status sequence (bound to the Status action, "I" by default).
//
// That sequence (DaggerfallUI.DisplayStatusInfo(), which chains a location box into a health box via
// DaggerfallMessageBox.AddNextMessageBox) is private, and the "I" keypress that triggers it is
// consumed by GameManager.Update() posting an internal UI message this mod has no access to - there
// is no hook to append a third box onto that specific chain, and no Harmony to patch the private
// method that builds it. InputManager.ActionComplete() is a stateless frame comparison rather than a
// one-shot/consumable flag though (confirmed by reading InputManager.cs directly), so polling the
// same Status action independently here doesn't interfere with vanilla's own handling of it.
//
// Instead of trying to inject into vanilla's specific box chain, this watches for the same keypress
// and then waits for the UI to go through "a messagebox appeared, then that messagebox chain fully
// closed" before showing its own box - i.e. it waits for vanilla's own sequence to completely finish
// on its own, however many boxes that turns out to be, rather than assuming a fixed count or trying
// to identify vanilla's specific box instances. This is slower than truly inserting into the chain
// (there's a beat after clicking through health before this one appears) but needs nothing from
// vanilla's internals to stay correct.
//
// State is tracked as a plain int (not a nested enum) deliberately: a nested enum here previously
// broke this mod's loading ENTIRELY, not just this one feature. DFU's mod system scans every type in
// the compiled mod assembly via reflection to find its [Invoke] entry point, and that scan itself
// threw ("Field 'state' is an enum type with a bad underlying type"), so Init() never ran and the
// whole mod was inert for a session - daylight, hunger, the book, everything - not just this box.
// Confirmed as the sole cause: this was the only custom enum type declared anywhere in this mod's
// source.

using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace VoxsVampirismMod
{
    public class HungerStatusInfoBox : MonoBehaviour
    {
        const int StateIdle = 0;
        const int StateWaitingForVanillaBoxToAppear = 1;
        const int StateWaitingForVanillaBoxToClose = 2;

        // Safety cap in case the Status keypress doesn't end up opening a messagebox at all (e.g.
        // blocked by some other UI state) - without this, a missed vanilla box would leave this
        // stuck waiting forever instead of resetting to listen for the next keypress.
        const int maxFramesToWaitForVanillaBox = 60;

        int state = StateIdle;
        int framesWaited;

        void Update()
        {
            if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
            {
                state = StateIdle;
                return;
            }

            switch (state)
            {
                case StateIdle:
                    if (InputManager.Instance.ActionComplete(InputManager.Actions.Status))
                    {
                        state = StateWaitingForVanillaBoxToAppear;
                        framesWaited = 0;
                    }
                    break;

                case StateWaitingForVanillaBoxToAppear:
                    if (IsMessageBoxOnTop())
                    {
                        state = StateWaitingForVanillaBoxToClose;
                    }
                    else if (++framesWaited > maxFramesToWaitForVanillaBox)
                    {
                        state = StateIdle;
                    }
                    break;

                case StateWaitingForVanillaBoxToClose:
                    if (!IsMessageBoxOnTop())
                    {
                        ShowHungerStatusBox();
                        state = StateIdle;
                    }
                    break;
            }
        }

        static bool IsMessageBoxOnTop()
        {
            return DaggerfallUI.Instance.UserInterfaceManager.TopWindow is DaggerfallMessageBox;
        }

        static void ShowHungerStatusBox()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            int blood = vampirism.GetBloodPoints();
            int humanity = vampirism.GetHumanity();

            // Humanity is a hard requirement to always be visible, not a hidden internal number -
            // shown here as a second line on the same existing status box rather than a separate
            // window, per the Humanity system spec.
            DaggerfallMessageBox box = new DaggerfallMessageBox(DaggerfallUI.Instance.UserInterfaceManager, null, wrapText: true);
            box.ClickAnywhereToClose = true;
            box.SetText(new string[]
            {
                string.Format("Your blood: {0}/10 ({1}).", blood, GetBloodLabel(blood)),
                string.Format("Your humanity: {0}/10 ({1}).", humanity, GetHumanityLabel(humanity)),
            });
            box.Show();
        }

        // Tiers built around the same thresholds ReportBloodFlavor's own one-shot messages key off
        // (full=10, low<=5, empty=0), with a middle "nearly spent" tier matching the forced-kill
        // gate's own <=2 threshold.
        static string GetBloodLabel(int blood)
        {
            if (blood >= 10) return "full";
            if (blood > 5) return "sated";
            if (blood > 2) return "thinning";
            if (blood > 0) return "nearly spent";
            return "starving";
        }

        // Tiers built around the Blood Point spec's own milestone crossing points (2/5/8).
        static string GetHumanityLabel(int humanity)
        {
            if (humanity >= 10) return "unburdened";
            if (humanity >= 8) return "mostly yourself";
            if (humanity >= 5) return "balanced";
            if (humanity >= 3) return "slipping";
            if (humanity > 0) return "nearly lost";
            return "gone";
        }
    }
}
