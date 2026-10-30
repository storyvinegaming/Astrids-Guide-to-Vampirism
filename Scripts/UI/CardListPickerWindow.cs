// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// BG3-inspired card list: each option renders as its own left-aligned, word-wrapped, hover-
// highlighted panel instead of a single clipped list row. DaggerfallListPickerWindow's own ListBox
// lays rows out at a hardcoded 138-virtual-px width with word-wrap available but off by default -
// this mod's longer authored option labels ("Cross the room without waking so much as the dust
// [Stealth]") were getting silently clipped mid-sentence there. This window is built entirely from
// stock DFU UI primitives already exercised elsewhere in the engine/mod: DaggerfallUI.Instance.
// SetDaggerfallPopupStyle(PopupStyle.Parchment, panel) is the exact call DaggerfallMessageBox uses
// for its own bordered parchment box; TextLabel already supports WrapText/WrapWords/MaxWidth (just
// unused by ListBox's own row rendering); BaseScreenComponent.MouseOverBackgroundColor gives free
// hover highlighting with no manual mouse-enter/leave wiring needed; and 288 virtual px is DFU's own
// established "safe" prose width (DaggerfallMessageBox's own wrapText constructor), more than double
// the old 138px list box.
//
// Exposes the same OnItemPicked(index, itemString) delegate shape DaggerfallListPickerWindow already
// uses, so callers built against that contract only need to swap the constructor and the
// AddItem->AddOption call - nothing about their own picking logic changes.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace VoxsVampirismMod.UI
{
    public class CardListPickerWindow : DaggerfallPopupWindow
    {
        const float panelWidth = 288f;
        const float cardHorizontalPadding = 10f;
        const float cardVerticalPadding = 5f;
        const float cardSpacing = 3f;
        const float titleTopY = 12f;
        const float titleToCardsGap = 16f;
        const float panelVerticalMargin = 12f;
        const float panelBottomMargin = 20f; // a bit more breathing room below the last card than the flat top margin alone gives

        // Safety budget inside DFU's fixed 320x200 virtual canvas - checked against the mod's own
        // actual authored option lengths and comfortably never hit in practice, so no scrolling is
        // implemented here. If a future edit to FeedScenes.json ever produces content taller than
        // this, it's logged (see Setup below) rather than silently clipped or broken.
        const float maxContentHeight = 200f - panelVerticalMargin - panelBottomMargin - 8f;

        readonly string title;
        readonly List<string> options = new List<string>();

        readonly Panel cardPanel = new Panel();
        readonly TextLabel titleLabel = new TextLabel();

        public delegate void OnItemPickedEventHandler(int index, string itemString);
        public event OnItemPickedEventHandler OnItemPicked;

        public CardListPickerWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous, string title)
            : base(uiManager, previous)
        {
            this.title = title;
        }

        // Mirrors ListBox.AddItem - call once per option before the window is pushed. All actual
        // layout happens in Setup(), which DaggerfallBaseWindow.Update() only invokes lazily on the
        // first Update() tick after the window is pushed - safely after every AddOption call a caller
        // makes, since PushWindow always happens after building the option list.
        public void AddOption(string label)
        {
            options.Add(label);
        }

        protected override void Setup()
        {
            base.Setup();

            DaggerfallUI.Instance.SetDaggerfallPopupStyle(DaggerfallUI.PopupStyle.Parchment, cardPanel);
            cardPanel.HorizontalAlignment = HorizontalAlignment.Center;
            cardPanel.VerticalAlignment = VerticalAlignment.Middle;
            cardPanel.Size = new Vector2(panelWidth, panelVerticalMargin * 2f);
            NativePanel.Components.Add(cardPanel);

            titleLabel.Text = title;
            titleLabel.Position = new Vector2(0, titleTopY);
            titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            cardPanel.Components.Add(titleLabel);

            float cardWidth = panelWidth - cardHorizontalPadding * 2f;
            float y = titleTopY + titleToCardsGap;

            for (int i = 0; i < options.Count; i++)
            {
                int index = i;

                Button card = new Button();
                card.BackgroundColor = new Color(0.35f, 0.24f, 0.09f, 0.18f);
                card.MouseOverBackgroundColor = new Color(0.55f, 0.4f, 0.12f, 0.4f);

                card.Label.WrapText = true;
                card.Label.WrapWords = true;
                card.Label.MaxWidth = (int)(cardWidth - cardHorizontalPadding * 2f);
                card.Label.HorizontalAlignment = HorizontalAlignment.Left;
                card.Label.HorizontalTextAlignment = TextLabel.HorizontalTextAlignmentSetting.Left;
                card.Label.VerticalAlignment = VerticalAlignment.Middle;
                card.Label.Position = new Vector2(cardHorizontalPadding, 0);
                card.Label.Text = options[i];

                float cardHeight = card.Label.TextHeight + cardVerticalPadding * 2f;
                card.HorizontalAlignment = HorizontalAlignment.None;
                card.Position = new Vector2(cardHorizontalPadding, y);
                card.Size = new Vector2(cardWidth, cardHeight);

                card.OnMouseClick += (sender, position) => RaiseOnItemPicked(index);

                cardPanel.Components.Add(card);

                y += cardHeight + cardSpacing;
            }

            float contentHeight = y - cardSpacing + panelBottomMargin;
            if (contentHeight > maxContentHeight)
            {
                VoxsVampirism.Log("CardListPickerWindow: content height {0:F0} exceeds the {1:F0} safety budget for '{2}' ({3} options) - layout may overflow the canvas.",
                    contentHeight, maxContentHeight, title, options.Count);
            }

            cardPanel.Size = new Vector2(panelWidth, contentHeight);
        }

        void RaiseOnItemPicked(int index)
        {
            if (OnItemPicked != null)
                OnItemPicked(index, options[index]);
        }
    }
}
