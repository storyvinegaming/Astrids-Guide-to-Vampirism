// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Haarvenu clan feature (Clan Features spec, §2): identical to VampiricFlamesEffect in every
// mechanical respect - same AreaAtRange shape, same 50 + (10-Humanity)*casterLevel damage formula,
// same 1-blood/5-SP-charged-then-refunded cost (charged from SpellCastCostRefunder.cs, not here, for
// the exact same "MagicRound only runs once per struck target, so an empty-blast cast needs the
// central hook" reason documented in VampiricFlamesEffect.cs's own header) - just ice-elemental
// instead of fire.
//
// Clan-exclusive: registered with the broker like every other spell (needed regardless of who can
// learn it), but NOT part of the universal bookSpellBundles grant list - VoxsVampirism.
// GrantSpellsFromBook gates this one specific bundle on VampireClan == Haarvenu, so only a Haarvenu
// vampire ever sees it offered.
//
// Same opt-in blast exclusion as Vampiric Flames (VoxsVampirismMod.VampiricFireSparesCaster/
// VampiricFireSparesAllies Mod Settings, both off by default) - see that file's own header.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsVampirismMod.Effects
{
    public class VampiricIceEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Vampiric-Ice";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.AreaAtRange;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Destruction;
            properties.SupportChance = true;
            properties.DisableReflectiveEnumeration = true;
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Always succeeds - Chance is only enabled to avoid the zero-active-component cost fudge,
        // same as the framework's own minimal-cost trick used elsewhere in this mod.
        public override bool ChanceSuccess
        {
            get { return true; }
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "A blast of vampiric frost, costing 5 Magicka and 1 blood point",
                "each cast. Weaker the more human you remain, stronger the more",
                "beast. Clan Haarvenu's own working.");
        }

        public override void MagicRound()
        {
            base.MagicRound();

            DaggerfallEntityBehaviour targetBehaviour = GetPeeredEntityBehaviour(manager);
            if (!targetBehaviour || caster == null)
                return;

            // See VampiricFlamesEffect.cs's own header/MagicRound - identical opt-in exclusion, same
            // two Mod Settings shared between both spells.
            if (VoxsVampirismMod.VoxsVampirism.VampiricFireSparesCaster && targetBehaviour == caster)
                return;
            if (VoxsVampirismMod.VoxsVampirism.VampiricFireSparesAllies)
            {
                EnemyEntity targetEnemyEntity = targetBehaviour.Entity as EnemyEntity;
                if (targetEnemyEntity != null && targetEnemyEntity.Team == MobileTeams.PlayerAlly)
                    return;
            }

            DaggerfallEntity targetEntity = targetBehaviour.Entity;
            int humanity = VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity();
            int damage = 50 + (10 - humanity) * caster.Entity.Level;

            targetEntity.DecreaseHealth(damage);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "VampiricIceEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 100,
                ChancePlus = 0,
                ChancePerLevel = 1,
            };
            EffectEntry effectEntry = new EffectEntry()
            {
                Key = template.Properties.Key,
                Settings = templateSettings,
            };

            return new EffectBundleSettings()
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.AreaAtRange,
                ElementType = ElementTypes.Cold,
                Name = EffectKey,
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
