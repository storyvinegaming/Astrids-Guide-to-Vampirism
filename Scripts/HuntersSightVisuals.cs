// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Visual side of the Hunter's Sight spell (HuntersSightEffect) - a runtime Light plus a translucent
// full-screen HUD tint, toggled together by HuntersSightEffect.SetActive(bool). Deliberately does NOT
// touch RenderSettings.ambientLight or anything PlayerAmbientLight.cs (base game) already owns -
// that component recomputes and overwrites ambientLight every LateUpdate based on player location/
// time of day, so fighting it directly would be a losing battle and would also stack unpredictably
// with whatever lighting mods the player has installed. A plain Light attached to the player is just
// another ordinary light source in the scene - the same category as a torch or the vanilla "Light"
// spell's magic candle (see DaggerfallWorkshop's own LightNormal.cs, the closest built-in precedent),
// so it composes safely with any lighting mod rather than overriding it.
//
// The red tint is a separate concern from the light - it's pure screen-space flavor (the "everything
// looks slightly red" Night-Eye look), not what actually helps the player see. It's implemented as an
// ordinary child Panel added to DaggerfallHUD's own ParentPanel (the same root Panel the base game's
// own FadeBehaviour tints for death/transition fades - confirmed by reading FadeBehaviour.cs/
// DaggerfallUI.cs, though this uses its own separate Panel rather than fadeTargetPanel itself, so it
// never fights a real fade-to-black). ScreenComponentCollection (DaggerfallHUD.ParentPanel.Components)
// has no Insert() - only Add() - so this panel unavoidably draws on top of vitals/compass/crosshair
// rather than behind them. Left as-is rather than worked around: the alpha is low enough that this
// reads as a screen-wide tint over everything, which is if anything MORE faithful to how Oblivion's
// own Night-Eye actually tints the whole screen including its UI, not just the 3D view.
//
// Both the light's color and the tint's color (including the tint's alpha/opacity, settable via the
// color picker's hex field) are Mod Settings (VoxsVampirism.HuntersSightLightColor/TintColor) rather
// than fixed constants, read live every frame the same way HuntersSightLightRange already is - a
// player who dislikes the default reddish "Night-Eye" look can pick any color for either or both
// independently of the "Hunter's Sight Screen Tint" on/off toggle above.
//
// No custom enum types anywhere in this file, per this mod's established convention (a nested enum
// once broke the whole mod's loading via a DFU runtime-compiler reflection failure).

using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterface;

namespace VoxsVampirismMod
{
    public class HuntersSightVisuals : MonoBehaviour
    {
        const float lightIntensity = 1.3f;

        static bool active;

        Light nightLight;
        Panel tintPanel;

        public static void SetActive(bool value)
        {
            active = value;
        }

        // Bug fix (reported live, same root cause as the ThrallManager thrall-roster leak fixed
        // earlier this session): active is static, so it survives a Load Game within the same running
        // session untouched unless something explicitly sets it. HuntersSightEffect.Resume() only
        // turns it back on if THIS save's own restored effects genuinely include an active Hunter's
        // Sight - a save where the effect was never active (e.g. one predating the character even
        // becoming a vampire) has nothing to call Resume() on at all, so active silently kept
        // whatever value it held from a previous save/session, and the light/tint kept showing.
        // OnStartLoad fires unconditionally on EVERY load, before any of DFU's own effect restoration
        // - resetting here first means it only ends up true again if this load's own Resume() actually
        // runs afterward with a genuinely active effect.
        void OnEnable()
        {
            SaveLoadManager.OnStartLoad += ResetOnStartLoad;
        }

        void OnDisable()
        {
            SaveLoadManager.OnStartLoad -= ResetOnStartLoad;
        }

        static void ResetOnStartLoad(SaveData_v1 saveData)
        {
            active = false;
        }

        // Everything below runs continuously (cheap: a couple of null checks and field assignments
        // once both objects exist) rather than reacting only to SetActive() calls - this way the
        // light/panel get created lazily whenever their dependencies (PlayerObject, DaggerfallHUD)
        // actually become available, with no ordering requirement between this component's own Awake()
        // and the rest of the game finishing startup, and the tint panel stays correctly sized across
        // any resolution change with no resize event needed.
        void Update()
        {
            EnsureLight();
            EnsureTintPanel();

            if (nightLight != null)
            {
                nightLight.enabled = active;

                // Read live every frame (not just baked in once at creation) so a Mod Settings change
                // takes effect immediately - ModSettingsWindow re-invokes the mod's LoadSettingsCallback
                // as soon as the player saves, without needing to re-toggle the spell.
                nightLight.range = VoxsVampirismMod.VoxsVampirism.HuntersSightLightRange;
                nightLight.color = VoxsVampirismMod.VoxsVampirism.HuntersSightLightColor;
            }

            if (tintPanel != null)
            {
                // "Hunter's Sight Screen Tint" Mod Setting - read live every frame, same reasoning as
                // the light's own range above, so toggling it off mid-session (or mid-cast) takes
                // effect immediately without needing to re-toggle the spell. Only the screen-space
                // tint is gated by this - the light itself (the part that actually helps you see)
                // stays on regardless.
                bool wantTint = active && VoxsVampirismMod.VoxsVampirism.HuntersSightTintEnabled;
                tintPanel.BackgroundColor = wantTint ? VoxsVampirismMod.VoxsVampirism.HuntersSightTintColor : Color.clear;

                Panel root = DaggerfallUI.Instance.DaggerfallHUD.ParentPanel;
                tintPanel.Position = Vector2.zero;
                tintPanel.Size = root.Size;
            }
        }

        void EnsureLight()
        {
            if (nightLight != null || GameManager.Instance.PlayerObject == null)
                return;

            GameObject lightObject = new GameObject("HuntersSightLight");
            lightObject.transform.SetParent(GameManager.Instance.PlayerObject.transform, false);

            float height = GameManager.Instance.PlayerController != null
                ? GameManager.Instance.PlayerController.height * 0.5f
                : 1f;
            lightObject.transform.localPosition = new Vector3(0f, height, 0f);

            nightLight = lightObject.AddComponent<Light>();
            nightLight.type = LightType.Point;
            // color/range are set every frame in Update() (from the live Mod Settings values), not here.
            nightLight.intensity = lightIntensity;
            nightLight.shadows = LightShadows.None;
            nightLight.enabled = active;
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
