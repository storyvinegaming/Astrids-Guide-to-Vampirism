// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Blood Point spec (A1b): a persistent HUD element showing the pool at a glance - ten small dots in
// a horizontal row, filled for an available point, hollow for a spent one. Attached to
// DaggerfallUI.Instance.DaggerfallHUD.NativePanel - the same panel activeSpells/popupText/
// midScreenTextLabel are added to in DaggerfallHUD's own setup - rather than ParentPanel (what
// FeedFlashVisuals/HuntersSightVisuals use for their own screen-tint overlays), so this inherits HUD
// scaling automatically via NativePanel's own fixed 320x200 native coordinate space and AutoSize
// transform, with no manual .Scale assignment needed on this component's own part.
//
// Dot graphics are two small procedurally-generated textures (filled disc, hollow ring), same
// per-pixel Texture2D.SetPixels technique ScentOfPreyVisuals.CreateMistTexture() already uses - no
// shipped image assets, nothing that can fail to load.
//
// Redraws only on VoxsVampirismEffect.OnBloodPointsChanged (an actual change to the pool), not every
// frame - Update() here is only used for the lightweight lazy-creation check (DaggerfallHUD isn't
// guaranteed to exist yet at mod init) and the visibility gate (hidden whenever the player isn't a
// vampire, the HUD itself isn't showing, or the "Show Blood Tracker" Mod Setting is off).

using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterface;

namespace VoxsVampirismMod
{
    public class BloodPointTracker : MonoBehaviour
    {
        const int dotCount = 10;
        const float dotSize = 8f;
        const float dotSpacing = 2f;
        const float edgeMargin = 4f;
        static readonly Color dotColor = new Color(0.75f, 0.05f, 0.05f, 1f);

        static Texture2D filledTexture;
        static Texture2D hollowTexture;

        Panel container;
        Panel[] dots;
        bool subscribedToChanges;
        bool wasVisible;

        void Update()
        {
            EnsureCreated();
            if (container == null)
                return;

            bool wantVisible = VoxsVampirism.ShowBloodTracker
                && GameManager.Instance.IsPlayerOnHUD
                && GameManager.Instance.PlayerEffectManager.HasVampirism();

            // Re-syncs from the live pool on every hidden->visible transition, not just once at
            // creation - the DaggerfallHUD panel this is parented to persists across a save load (the
            // same GameObject, never recreated), so without this the dots kept showing whatever they
            // were last drawn as (often 0/empty, from before the player was even a vampire) until the
            // next OnBloodPointsChanged event happened to fire from some unrelated cast.
            if (wantVisible && !wasVisible)
                Redraw(GetCurrentBloodPoints());
            wasVisible = wantVisible;

            if (container.Enabled != wantVisible)
                container.Enabled = wantVisible;

            UpdatePosition();
        }

        void EnsureCreated()
        {
            if (container != null)
                return;

            if (DaggerfallUI.Instance == null || DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            if (filledTexture == null)
                filledTexture = CreateFilledDotTexture();
            if (hollowTexture == null)
                hollowTexture = CreateHollowDotTexture();

            container = new Panel();
            container.BackgroundColor = Color.clear;
            container.Size = new Vector2(dotCount * dotSize + (dotCount - 1) * dotSpacing, dotSize);

            dots = new Panel[dotCount];
            for (int i = 0; i < dotCount; i++)
            {
                Panel dot = new Panel();
                dot.Size = new Vector2(dotSize, dotSize);
                dot.Position = new Vector2(i * (dotSize + dotSpacing), 0f);
                dot.BackgroundTexture = hollowTexture;
                container.Components.Add(dot);
                dots[i] = dot;
            }

            DaggerfallUI.Instance.DaggerfallHUD.NativePanel.Components.Add(container);

            if (!subscribedToChanges)
            {
                VoxsVampirismEffect.OnBloodPointsChanged += OnBloodPointsChanged;
                subscribedToChanges = true;
            }

            Redraw(GetCurrentBloodPoints());
        }

        void OnDestroy()
        {
            if (subscribedToChanges)
            {
                VoxsVampirismEffect.OnBloodPointsChanged -= OnBloodPointsChanged;
                subscribedToChanges = false;
            }
        }

        void OnBloodPointsChanged(int points)
        {
            Redraw(points);
        }

        static int GetCurrentBloodPoints()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            return vampirism != null ? vampirism.GetBloodPoints() : 0;
        }

        void Redraw(int points)
        {
            if (dots == null)
                return;

            for (int i = 0; i < dots.Length; i++)
                dots[i].BackgroundTexture = i < points ? filledTexture : hollowTexture;
        }

        // Anchor choice from the "Blood Tracker Position" Mod Setting (0=Top Left, 1=Top Right,
        // 2=Bottom Left, 3=Bottom Right) - Top Left is the default, the one corner vanilla's own HUD
        // (vitals bottom-left, compass bottom-right, spell icons/crosshair center) leaves empty.
        void UpdatePosition()
        {
            Panel root = DaggerfallUI.Instance.DaggerfallHUD.NativePanel;
            Vector2 rootSize = root.Size;
            Vector2 size = container.Size;

            switch (VoxsVampirism.BloodTrackerPositionIndex)
            {
                case 1: // Top Right
                    container.Position = new Vector2(rootSize.x - size.x - edgeMargin, edgeMargin);
                    break;
                case 2: // Bottom Left
                    container.Position = new Vector2(edgeMargin, rootSize.y - size.y - edgeMargin);
                    break;
                case 3: // Bottom Right
                    container.Position = new Vector2(rootSize.x - size.x - edgeMargin, rootSize.y - size.y - edgeMargin);
                    break;
                default: // Top Left
                    container.Position = new Vector2(edgeMargin, edgeMargin);
                    break;
            }
        }

        static Texture2D CreateFilledDotTexture()
        {
            const int size = 16;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = (size / 2f) - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    pixels[y * size + x] = dist <= radius ? dotColor : Color.clear;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        static Texture2D CreateHollowDotTexture()
        {
            const int size = 16;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float outerRadius = (size / 2f) - 1f;
            float innerRadius = outerRadius - 2.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    pixels[y * size + x] = (dist <= outerRadius && dist >= innerRadius) ? dotColor : Color.clear;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
