// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: a toggleable self-buff, modeled on vanilla's own "Light" (DaggerfallWorkshop's
// LightNormal.cs - same IncumbentEffect/Start/Resume/End shape, same magic candle precedent for
// attaching a runtime light to the player) but for night vision rather than illumination, and a
// genuine ON/OFF TOGGLE rather than vanilla Light's own "recast tops up the duration" behavior. See
// HuntersSightVisuals.cs for the actual Light + screen-tint implementation this effect switches on
// and off - kept in a separate plain MonoBehaviour rather than done here, since an effect class isn't
// itself a MonoBehaviour and has no Update() of its own to keep a light/panel in sync.
//
// Toggle mechanism: BecomeIncumbent() (first cast, nothing already running) turns the visuals on.
// AddState(incumbent) (recast while already active) calls manager.EndIncumbentEffect<HuntersSightEffect>(),
// which is the framework's own public API for forcibly ending an incumbent immediately (see
// EntityEffectManager.EndIncumbentEffect<T>() - sets RoundsRemaining=0 and calls End()) - deliberately
// NOT vanilla Light's own AddState behavior (incumbent.RoundsRemaining += RoundsRemaining, i.e. "recast
// extends duration"), since this spell's whole point is a manual toggle, not a rechargeable buff.
// End() (however it's reached - manual toggle-off above, or the effect's own long fallback duration
// finally running out) turns the visuals back off. Confirmed safe to call EndIncumbentEffect from
// within AddState by reading EntityEffectManager.AssignBundle(): Start() (and therefore AttachHost()/
// AddState() for an IncumbentEffect) always runs BEFORE the framework checks IsIncumbent and skips
// adding non-incumbent instances to the live bundle - so this non-incumbent instance is never in
// instancedBundles itself when EndIncumbentEffect looks up "the" incumbent, and can never find/end
// itself by accident.
//
// No level/threshold gate and no potency scaling - Hunter's Sight has no natural axis to scale by
// Humanity (matching Suppress Beast's own "no natural potency axis" precedent from the
// original Humanity spec), it's simply on or off. DurationBase is a long fallback (see Create() below)
// rather than a meaningful gameplay duration - the toggle itself is the real off switch.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;

namespace VoxsVampirismMod.Effects
{
    public class HuntersSightEffect : IncumbentEffect
    {
        public static readonly string EffectKey = "Hunters-Sight";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = true;
            properties.SupportDuration = true;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Illusion;
            properties.DisableReflectiveEnumeration = true;

            // A large DurationBase (see Create() below - a long fallback, not a meaningful gameplay
            // number) fed straight into the framework's own formula-driven cost calc (which multiplies
            // CostA by DurationBase directly) produced a spell-point cost in the hundreds of thousands.
            // Zeroed out entirely - the real SP cost is the engine's own floor, charged-and-refunded
            // via SpellCastCostRefunder.cs like every other spell in this mod.
            properties.DurationCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Blood Economy Rebalance (§3): this toggle previously charged no blood at all - every
        // vampire spell now costs at least 1, and insufficient blood pays the shortfall in Health
        // instead of refusing (§5), via PayBloodOrHealth in BecomeIncumbent below.
        const int bloodCostPerCast = 1;

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "A toggle - clear night vision for as long as it holds.",
                "Costs 1 blood point to raise. Cast again to let it fade.");
        }

        protected override bool IsLikeKind(IncumbentEffect other)
        {
            return other is HuntersSightEffect;
        }

        // Recast-while-active toggles off, rather than topping up duration like vanilla's own Light.
        protected override void AddState(IncumbentEffect incumbent)
        {
            manager.EndIncumbentEffect<HuntersSightEffect>();
        }

        protected override void BecomeIncumbent()
        {
            base.BecomeIncumbent();
            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Charged only on the turn-ON transition - toggling back off (AddState below, via
            // EndIncumbentEffect) is a cancellation, not a new cast, and costs nothing further.
            // Insufficient blood no longer refuses (§5) - the shortfall is paid in Health instead.
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism != null)
            {
                int bloodSpent, hpPercentOwed;
                vampirism.PayBloodOrHealth(bloodCostPerCast, out bloodSpent, out hpPercentOwed);
                if (hpPercentOwed > 0 && caster != null)
                {
                    caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                    DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
                }
            }

            VoxsVampirismMod.HuntersSightVisuals.SetActive(true);
            DaggerfallUI.AddHUDText("The vision sharpens in the dark.");
        }

        public override void Resume(EntityEffectManager.EffectSaveData_v1 effectData, EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Resume(effectData, manager, caster);
            if (IsIncumbent && GetPeeredEntityBehaviour(manager) == GameManager.Instance.PlayerEntityBehaviour)
                VoxsVampirismMod.HuntersSightVisuals.SetActive(true);
        }

        public override void End()
        {
            base.End();
            if (GetPeeredEntityBehaviour(manager) == GameManager.Instance.PlayerEntityBehaviour)
            {
                VoxsVampirismMod.HuntersSightVisuals.SetActive(false);
                DaggerfallUI.AddHUDText("Your night sight fades back to normal.");
            }
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                UnityEngine.Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "HuntersSightEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);

            // A long fallback duration (roughly 90 days of classic-minute rounds) rather than anything
            // meant to actually be felt in play - the toggle (recast to turn off) is the real
            // mechanism, this is just a safety net so the effect can't theoretically outlive a very
            // long unbroken play session without ever being recast.
            EffectSettings templateSettings = new EffectSettings()
            {
                DurationBase = 129600,
                DurationPlus = 0,
                DurationPerLevel = 1,
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
