// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Self-target, Duration-only buff (IncumbentEffect, same boilerplate shape vanilla's own simple
// self-buffs use) granted by Astrid's Guide to Vampirism. While active (a flat 24 real game hours -
// DurationBase=1440 classic-minute rounds, no per-level scaling, matching the spec's own fixed
// duration), VoxsVampirismEffect.ApplyDaylightMagicRound treats the clan attribute/skill bonus
// tradeoff as forced ON regardless of real daylight state - the vampire simply doesn't regain their
// clan bonuses in the dark for as long as this is active, the same "no bonus" state daylight alone
// normally causes (itself now Humanity-scaled, not a flat zero - see ApplyClanBonusScaling). This
// effect does NOT touch sun/holy damage or the Health/Fatigue penalties at all - purely the clan
// bonus, nothing else.
//
// Casting grants +1 Humanity - but only on a genuine, cooldown-respecting cast. The whole cooldown
// mechanism is SupportChance/ChanceSuccess, reused here as a pure gate rather than a real random
// roll (same "always something" trick MesmerizeEffect/VampiricFlamesEffect use to
// avoid FormulaHelper's zero-active-component cost fudge, except here the check is real). Confirmed
// by reading EntityEffectManager.cs directly: Effect.Start() always runs regardless of ChanceSuccess,
// but a failed ChanceSuccess discards the effect before it's ever added to the live bundle - so
// MagicRound() (where the real Humanity grant and cooldown stamp happen, guarded by hasFired same as
// Astrid's Guide to Necromancy's own Subjugate Undead) never runs for a cast that fails this check.
// Recasting before the previous successful cast's own 24-hour cooldown has elapsed fizzles outright,
// with the same "Save versus spell made" framework messaging any other failed Chance effect gets -
// no separate failure handling needed on this end. Since the buff's own duration and its cast
// cooldown are both exactly 24 hours, there's no window where the old buff is still running by the
// time a new cast would even be allowed - no stacking/refresh behavior to design around either.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;

namespace VoxsVampirismMod.Effects
{
    public class SuppressBeastEffect : IncumbentEffect
    {
        public static readonly string EffectKey = "Suppress-Beast";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = true;
            properties.SupportDuration = true;
            properties.SupportChance = true;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Illusion;
            properties.DisableReflectiveEnumeration = true;

            // A flat 24h (1440-round) DurationBase fed into the framework's own formula-driven cost
            // calc (GetEffectComponentCosts multiplies CostA by DurationBase directly) produced a
            // spell-point cost in the thousands - that formula assumes small, level-scaling duration
            // values, not a fixed real-world-time constant like this effect's. Zeroed out entirely -
            // the real SP cost is the engine's own floor, charged-and-refunded via
            // SpellCastCostRefunder.cs like every other spell in this mod.
            properties.DurationCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Blood Economy Rebalance (§3): this spell previously charged no blood at all - every vampire
        // spell now costs at least 1. Charged in MagicRound below, past the hasFired/cooldown gate,
        // so a cooldown-blocked cast (ChanceSuccess false, effect never added to the live bundle,
        // MagicRound never runs) stays free - the same "no valid attempt, no cost" shape every other
        // spell's own eligibility gate uses.
        const int bloodCostPerCast = 1;

        // Pure check, no mutation - see file header for exactly why the real work has to live in
        // MagicRound() instead of here or in Start().
        public override bool ChanceSuccess
        {
            get
            {
                VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
                return vampirism != null && vampirism.IsSuppressBeastOffCooldown();
            }
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "For 24 hours, you forgo your clan's gifts even after",
                "nightfall, and gain 1 Humanity for the discipline. Costs",
                "1 blood point. Cannot be recast until a full day has passed.");
        }

        protected override bool IsLikeKind(IncumbentEffect other)
        {
            return (other is SuppressBeastEffect);
        }

        protected override void AddState(IncumbentEffect incumbent)
        {
            incumbent.RoundsRemaining += RoundsRemaining;
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(bloodCostPerCast, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0 && caster != null)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            vampirism.StampSuppressBeastCooldown();
            vampirism.ChangeHumanity(1);
            DaggerfallUI.AddHUDText("You force the beast in you back down, and feel a little more yourself for it.");
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "SuppressBeastEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                DurationBase = 1440,
                DurationPlus = 0,
                DurationPerLevel = 1,
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
