// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: Quicken Blood - a self-target instant heal. Each cast spends exactly 1 blood point for
// a flat 25% max Health AND Fatigue heal - "castable repeatedly" means repeat casting is how you buy
// more healing, not a single scaling cast. No cooldown - just the standard single-instance-per-cast
// hasFired guard every instant effect in this mod uses (Mesmerize/Dominate/VampiricFlames) to guard
// against a stray extra MagicRound tick. 5 SP charged-and-refunded via SpellCastCostRefunder.cs like
// every other spell in this mod.
//
// Blood Economy Rebalance (§5): the one spell with TWO identities depending on the pool, rather than
// the generic PayBloodOrHealth desperate-casting conversion every other blood-costing spell uses.
// With ≥1 blood, unchanged (blood -> Health+Fatigue). At exactly 0 blood, it INVERTS: Health itself
// pays the desperate-casting HP percentage (VoxsVampirism.DesperateCastingHpPercent) and Fatigue is
// restored by that same amount - Health -> Fatigue instead of Blood -> Health+Fatigue. Both branches
// share the one setting so they scale together if it's adjusted.
//
// Thrafey clan feature (Clan Features spec, §2): the Health/Fatigue return is increased by 10%,
// applied identically in both branches above for consistency - a Thrafey vampire's own blood (or, at
// 0 blood, own strength) simply goes further.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsVampirismMod.Effects
{
    public class QuickenBloodEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Quicken-Blood";

        const int bloodCostPerCast = 1;
        const float healFraction = 0.25f;

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Restoration;
            properties.SupportChance = true;
            properties.DisableReflectiveEnumeration = true;
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Always succeeds - Chance is only enabled to avoid the zero-active-component cost fudge,
        // same trick VampiricFlamesEffect/MesmerizeEffect use.
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
                "Converts 1 blood point into Health and Fatigue - 25% of your",
                "maximum of each. Cast again for more. At 0 blood, draws on",
                "your own strength instead: Health pays the cost, Fatigue is",
                "what's restored.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (!casterBehaviour)
                return;

            DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect vampirism =
                GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect()
                    as DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect;
            if (vampirism == null)
                return;

            DaggerfallEntity entity = casterBehaviour.Entity;
            float thrafeyMultiplier = VoxsVampirismMod.VoxsVampirism.GetPlayerVampireClan() == VampireClans.Thrafey ? 1.1f : 1f;

            // Two identities depending on the pool (Blood Economy Rebalance, §5): with blood to
            // spend, blood becomes Health and Fatigue as normal. At 0 blood, this INVERTS instead of
            // refusing - Health itself pays the desperate-casting percentage, and Fatigue is restored
            // by that same amount, so the spell still does something (steadies you) even with nothing
            // left to draw on, just at a cost paid the other direction.
            if (vampirism.GetBloodPoints() > 0)
            {
                vampirism.TrySpendBlood(bloodCostPerCast);
                entity.IncreaseHealth(Mathf.RoundToInt(entity.MaxHealth * healFraction * thrafeyMultiplier));
                entity.IncreaseFatigue(Mathf.RoundToInt(entity.MaxFatigue * healFraction * thrafeyMultiplier));
                DaggerfallUI.AddHUDText(string.Format("The wound closes over. (Blood: {0}/10.)", vampirism.GetBloodPoints()));
            }
            else
            {
                float desperateFraction = VoxsVampirismMod.VoxsVampirism.DesperateCastingHpPercent / 100f;
                entity.DecreaseHealth(Mathf.RoundToInt(entity.MaxHealth * desperateFraction));
                entity.IncreaseFatigue(Mathf.RoundToInt(entity.MaxFatigue * desperateFraction * thrafeyMultiplier));
                DaggerfallUI.AddHUDText("You have no blood left to spend - you tear at your own strength instead, and feel your legs steady.");
            }
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "QuickenBloodEffect.Create: Failed to get template from effect broker");
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
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
