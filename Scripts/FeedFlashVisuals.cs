// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A transparent red screen flash on every feed, fading out over a couple of seconds - visual
// confirmation that a feed just happened, independent of whatever HUD text scrolled past. Same
// screen-tint technique HuntersSightVisuals.cs already uses (an ordinary child Panel added to
// DaggerfallHUD's own ParentPanel), just driven by a one-shot fade timer instead of an on/off toggle,
// so the two coexist as two independent panels rather than sharing state - a player with Hunter's
// Sight active who then feeds simply sees both tints layered together briefly, which is the correct,
// expected result of two separate red overlays existing at once.
//
// Triggered from VoxsVampirismEffect.ApplyFeedSideEffects - the one shared helper every feed method
// (Feed/DrinkDry/FeedOnAnimal/GainBloodNoHumanity/DrinkDryNoHumanity) already funnels through for
// heal/sound, so hooking in there covers every feed path in the mod (Charm/Stalk/Feast, animal and
// corpse drains, thrall feeds, frenzy feeds) from a single call site rather than needing one at every
// SneakFeedController/ThrallManager feed call site individually.

using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;

namespace VoxsVampirismMod
{
    public class FeedFlashVisuals : MonoBehaviour
    {
        const float peakAlpha = 0.35f;
        const float fadeDurationSeconds = 2.5f;
        static readonly Color flashColor = new Color(0.75f, 0.05f, 0.05f);

        // Time.time of the most recent trigger, or -1 while no flash is in progress - real time
        // (not classic game time) since this is a purely cosmetic, frame-driven fade.
        static float flashStartTime = -1f;

        Panel tintPanel;

        public static void Trigger()
        {
            flashStartTime = Time.time;
        }

        void Update()
        {
            EnsureTintPanel();
            if (tintPanel == null)
                return;

            if (flashStartTime < 0f)
            {
                if (tintPanel.BackgroundColor.a != 0f)
                    tintPanel.BackgroundColor = Color.clear;
                return;
            }

            float elapsed = Time.time - flashStartTime;
            if (elapsed >= fadeDurationSeconds)
            {
                flashStartTime = -1f;
                tintPanel.BackgroundColor = Color.clear;
                return;
            }

            float alpha = peakAlpha * (1f - (elapsed / fadeDurationSeconds));
            Color tint = flashColor;
            tint.a = alpha;
            tintPanel.BackgroundColor = tint;

            Panel root = DaggerfallUI.Instance.DaggerfallHUD.ParentPanel;
            tintPanel.Position = Vector2.zero;
            tintPanel.Size = root.Size;
        }

        void EnsureTintPanel()
        {
            if (tintPanel != null)
                return;

            if (DaggerfallUI.Instance == null || DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            tintPanel = new Panel();
            tintPanel.BackgroundColor = Color.clear;
            tintPanel.Position = Vector2.zero;

            Panel root = DaggerfallUI.Instance.DaggerfallHUD.ParentPanel;
            tintPanel.Size = root.Size;
            root.Components.Add(tintPanel);
        }
    }
}
