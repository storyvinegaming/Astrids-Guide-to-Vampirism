// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Feature 2's hood-raised state. Earlier revisions of this file added a custom toggle (first a
// keybind, then hooking DFU's "Use" action mode) - both wrong, because DFU already has a real
// mechanism for this that has nothing to do with either: DaggerfallUnityItem.NextVariant() cycles a
// cloak's own paper doll texture variant, and it's wired to a MIDDLE-CLICK on the item (paper doll,
// accessory slot, or inventory list - see DaggerfallInventoryWindow.PaperDoll_OnMiddleMouseClick /
// LocalItemListScroller_OnItemMiddleClick), not to "Use" and not to anything this mod adds. Cloaks
// (MensClothing/WomensClothing Casual_cloak and Formal_cloak) are confirmed to be one of the item
// types NextVariant() supports.
//
// So there is no toggle for this mod to implement at all - the player already has the real
// mechanism, native to the game. This class just OBSERVES the equipped cloak's own CurrentVariant
// each tick and treats IsHoodRaised as true whenever it's one of hoodedCloakVariants below.
//
// hoodedCloakVariants was visually confirmed in testing (0-based indexing): out of the cloak's full
// variant set, indices 1, 2, and 5 show a raised hood on the paper doll - not a single fixed index,
// so this is a set membership check rather than equality against one value.
//
// IsHoodRaised is exposed as static state (this being a single-player game with exactly one
// instance of this component) so VoxsVampirismEffect can read it every ConstantEffect/MagicRound
// tick without a GetComponent lookup.

using UnityEngine;
using System.Collections.Generic;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;

namespace VoxsVampirismMod
{
    public class HoodController : MonoBehaviour
    {
        static readonly HashSet<int> hoodedCloakVariants = new HashSet<int> { 1, 2, 5 };

        public static bool IsHoodRaised { get; private set; }

        int lastLoggedVariant = int.MinValue;

        void Update()
        {
            if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
            {
                IsHoodRaised = false;
                return;
            }

            DaggerfallUnityItem cloak = GetEquippedCloak();
            int variant = cloak != null ? cloak.CurrentVariant : -1;

            if (variant != lastLoggedVariant)
            {
                lastLoggedVariant = variant;
                VoxsVampirism.Log("HoodController: equipped cloak CurrentVariant={0} (TotalVariants={1}, template={2}).",
                    variant, cloak != null ? cloak.TotalVariants : -1, cloak != null ? cloak.TemplateIndex : -1);
            }

            bool raised = cloak != null && hoodedCloakVariants.Contains(variant);
            if (raised != IsHoodRaised)
            {
                IsHoodRaised = raised;
                VoxsVampirism.Log("HoodController: hood now {0} (driven by cloak's own paper doll variant).", raised ? "raised" : "lowered");
            }
        }

        static DaggerfallUnityItem GetEquippedCloak()
        {
            ItemEquipTable equipTable = GameManager.Instance.PlayerEntity.ItemEquipTable;
            return equipTable.GetItem(EquipSlots.Cloak1) ?? equipTable.GetItem(EquipSlots.Cloak2);
        }
    }
}
