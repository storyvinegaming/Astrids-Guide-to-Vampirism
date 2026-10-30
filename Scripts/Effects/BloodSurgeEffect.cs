// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: Blood Surge (Blood Economy Rebalance, §6) - the blood answering the call of violence.
// A self-target, fixed-10-round buff (IncumbentEffect, same duration-buff shape SuppressBeastEffect
// already uses) granting a flat bonus to every weapon skill group - Archery, Axe, Blunt Weapon, Long
// Blade, Short Blade, plus Hand-to-Hand - via SetSkillMod (EntityEffect.cs's own protected API,
// same technique vanilla's own VampirismEffect.ApplyVampireAdvantages already uses for its own
// multi-skill bonus, read directly rather than guessed). SetSkillMod writes into a skillMods array
// owned by THIS effect instance; EntityEffectManager.UpdateEntityMods() sums every live effect's own
// array every tick and clears/reapplies it fresh, so nothing needs to be manually undone on End() -
// the moment this effect leaves the live bundle, its contribution simply drops out of the next sum.
//
// The bonus scales with how far the vampire has embraced the beast - recomputed from LIVE Humanity
// every MagicRound() (not fixed once at cast time), so a recast-refresh naturally re-evaluates the
// band without any extra logic, and the bonus itself even shifts mid-buff if Humanity changes while
// it's active:
//   Humanity 6-10 -> +10
//   Humanity 1-5  -> +20   (the exact-5 midpoint is placed here - the "below 50" reading of the
//                            original 100-point spec, since the phrasing left that edge ambiguous)
//   Humanity 0    -> +30
//
// This is the melee build's entry into the blood economy - until now every blood-spending spell
// served casters; Blood Surge makes a martial vampire hunt for the same reason a Flames-slinger does.
// Costs 1 blood, charged once on activation via VoxsVampirismEffect.PayBloodOrHealth (desperate
// casting, §5, applies here like every other blood-costing spell) - 5 SP charged-and-refunded via
// SpellCastCostRefunder.cs, not here.
//
// Clan Features spec, §2: three clans piggyback a sub-effect onto Blood Surge's own duration, applied
// via ApplyClanSubEffect() from both BecomeIncumbent (first cast) and AddState (recast-refresh, so
// the sub-effect's own duration gets refreshed alongside Blood Surge's, not left counting down from
// its original cast) -
//   Garlythi: vanilla Shield's real magnitude (classic spell 17, looked up via EntityEffectBroker.
//   GetClassicSpellRecord/ClassicSpellRecordDataToEffectBundleSettings - the same pattern PlayerEntity.
//   AssignVampireSpell itself uses) but with Blood Surge's own 10-round duration substituted for
//   Shield's own classic duration.
//   Selenu: DFU has no separate literal "Immunity" effect - vanilla's own Selenu grant (resistFire,
//   classic spell 11) resolved to ElementalResistance's Fire variant ("ElementalResistance-Fire"),
//   which raises a resistance CHANCE, not a flat block. A custom ChanceBase=100/ChancePerLevel=1
//   (this mod's own "always succeeds" convention, e.g. VampiricFlamesEffect) makes that chance guaranteed
//   for Blood Surge's duration, which is what "Immunity" actually means here.
//   Vraseth: handled inline in MagicRound() below (just one more SetSkillMod call, no sub-effect to
//   attach).
// Both Garlythi and Selenu's attaches use AssignBundle(..., AssignBundleFlags.BypassSavingThrows) -
// same technique MistFormEffect.EnsureMistFormOpenActive() already established for attaching an
// internal helper effect directly, bypassing the spellbook/casting system entirely.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Save;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;

namespace VoxsVampirismMod.Effects
{
    public class BloodSurgeEffect : IncumbentEffect
    {
        public static readonly string EffectKey = "Blood-Surge";

        const int roundsDuration = 10;
        const int bloodCostPerCast = 1;

        static readonly DFCareer.Skills[] weaponSkills =
        {
            DFCareer.Skills.ShortBlade,
            DFCareer.Skills.LongBlade,
            DFCareer.Skills.HandToHand,
            DFCareer.Skills.Axe,
            DFCareer.Skills.BluntWeapon,
            DFCareer.Skills.Archery,
        };

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

            // Fixed 10-round duration, not level-scaled - zeroed the same way every other fixed-
            // duration toggle/buff in this mod avoids the framework's own formula-driven cost blowup.
            properties.DurationCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "For ten rounds, the blood answers violence with violence -",
                "a bonus to every weapon skill and Hand-to-Hand: +10 at",
                "Humanity 6-10, +20 at 1-5, +30 at 0. Costs 1 blood point.",
                "Recasting refreshes the duration rather than stacking.");
        }

        protected override bool IsLikeKind(IncumbentEffect other)
        {
            return other is BloodSurgeEffect;
        }

        // Recast-while-active refreshes the duration rather than toggling off (unlike Mist Form/
        // Scent of Prey/Hunter's Sight) - Blood Surge is a combat buff meant to be topped up
        // mid-fight, not a toggle you'd want to accidentally cancel with a repeat cast. Pinned to the
        // fixed roundsDuration constant explicitly, not the framework-computed RoundsRemaining (which
        // would be DurationBase + DurationPerLevel*casterLevel - see Create()'s own header for why
        // DurationPerLevel can't just be 0 here, and why that means the framework's own value can't
        // be trusted to stay exactly 10 rounds).
        // Catch-up guard: a fixed 10-round duration decremented directly by the framework (unlike
        // every other toggle in this mod, which uses a long fallback duration with its own real off-
        // switch specifically to sidestep this) is exposed to same-frame magic-round bursts - and
        // those bursts are NOT rare. Confirmed directly: opening the spellbook to cast Blood Surge in
        // the first place pauses the game (PauseWhileOpen), and DFU's own EntityEffectBroker keeps
        // advancing the game clock behind ANY paused window regardless (its own source comment: "game
        // time still passes") - so however many real seconds are spent browsing the spellbook become
        // a burst of magic rounds the instant the menu closes and the cast lands, all arriving within
        // the same real frame. Mist Form's own anomaly-guard log confirmed this happening constantly
        // this session (bursts of 4+ rounds within 0.1 real seconds) - harmless for every other
        // toggle's own 129600-round fallback, but enough to wipe out this whole 10-round buff in the
        // same instant it's cast. Real duration is tracked here instead (trueRoundsRemaining),
        // decremented at most once per real Unity frame, and reasserted into the framework's own
        // RoundsRemaining every MagicRound() call so a same-frame burst can only ever cost 1 of the
        // 10 rounds no matter how many catch-up rounds land in it.
        int trueRoundsRemaining;
        int lastDecrementFrame = -1;

        // Recast-while-active refreshes the duration rather than toggling off (unlike Mist Form/
        // Scent of Prey/Hunter's Sight) - Blood Surge is a combat buff meant to be topped up
        // mid-fight, not a toggle you'd want to accidentally cancel with a repeat cast. Pinned to the
        // fixed roundsDuration constant explicitly, not the framework-computed RoundsRemaining (which
        // would be DurationBase + DurationPerLevel*casterLevel - see Create()'s own header for why
        // DurationPerLevel can't just be 0 here, and why that means the framework's own value can't
        // be trusted to stay exactly 10 rounds).
        protected override void AddState(IncumbentEffect incumbent)
        {
            incumbent.RoundsRemaining = roundsDuration;

            BloodSurgeEffect incumbentBloodSurge = incumbent as BloodSurgeEffect;
            if (incumbentBloodSurge != null)
            {
                incumbentBloodSurge.trueRoundsRemaining = roundsDuration;
                incumbentBloodSurge.lastDecrementFrame = -1;
            }

            // A recast-refresh charges the same 1 blood a fresh cast does (explicit report - the
            // refresh was free, so keeping the buff up forever cost 1 blood total instead of 1 per
            // ten rounds). Charged on THIS instance (the new, non-incumbent one AddState runs on),
            // whose own hasChargedCost is still false - the incumbent's own flag stays true and is
            // never consulted again, so no double-charge either way.
            ChargeCostOnce();

            ApplyClanSubEffect();
            AnnounceBuffs();
        }

        bool hasChargedCost;

        protected override void BecomeIncumbent()
        {
            base.BecomeIncumbent();
            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Same pin as AddState above - the framework's own initial RoundsRemaining (computed from
            // DurationBase/DurationPerLevel before this ever runs) is overwritten here so the real
            // duration is always exactly 10 rounds, never caster-level-scaled.
            RoundsRemaining = roundsDuration;
            trueRoundsRemaining = roundsDuration;
            lastDecrementFrame = -1;

            ChargeCostOnce();
            ApplyClanSubEffect();
            DaggerfallUI.AddHUDText("Your blood rises to meet the fight.");
            AnnounceBuffs();
        }

        // Spells out exactly which buffs this cast granted - the flat weapon-skill bonus (whose
        // actual value depends on live Humanity, not something the player can eyeball from the spell
        // description alone) plus whichever clan sub-effect just got attached. Called from both
        // BecomeIncumbent (first cast) and AddState (recast-refresh) since either can change what's
        // granted - Humanity can shift the bonus band mid-playthrough, and this is also where a
        // future clan buff should get its own line: ApplyClanSubEffect and this method are meant to
        // be extended together, one new "else if (clan == ...)" branch in each, so the message never
        // silently falls out of sync with what the clan actually grants.
        void AnnounceBuffs()
        {
            int bonus = GetSkillBonus();
            VampireClans clan = VoxsVampirismMod.VoxsVampirism.GetPlayerVampireClan();

            string skillLine = clan == VampireClans.Vraseth
                ? string.Format("+{0} to all weapon skills, Hand-to-Hand, and Running.", bonus)
                : string.Format("+{0} to all weapon skills and Hand-to-Hand.", bonus);
            DaggerfallUI.AddHUDText(skillLine);

            if (clan == VampireClans.Garlythi)
                DaggerfallUI.AddHUDText("The blood also shields you.");
            else if (clan == VampireClans.Selenu)
                DaggerfallUI.AddHUDText("The blood also renders you immune to fire.");
        }

        // Clan Features spec, §2 - see file header. Garlythi/Selenu only; Vraseth is handled inline
        // in MagicRound() below since it's just another SetSkillMod call, not a sub-effect to attach.
        void ApplyClanSubEffect()
        {
            if (caster == null)
                return;

            VampireClans clan = VoxsVampirismMod.VoxsVampirism.GetPlayerVampireClan();
            if (clan == VampireClans.Garlythi)
                ApplyGarlythiShield();
            else if (clan == VampireClans.Selenu)
                ApplySelenuFireResistance();
        }

        void ApplyGarlythiShield()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            SpellRecord.SpellRecordData recordData;
            if (!effectBroker.GetClassicSpellRecord(17, out recordData))
                return;

            EffectBundleSettings shieldBundleSettings;
            if (!effectBroker.ClassicSpellRecordDataToEffectBundleSettings(recordData, BundleTypes.Spell, out shieldBundleSettings))
                return;
            if (shieldBundleSettings.Effects == null || shieldBundleSettings.Effects.Length == 0)
                return;

            // Real Magnitude values from Shield's own classic record, kept - only Duration is
            // substituted with Blood Surge's own fixed 10 rounds, so the two expire together.
            EffectSettings shieldSettings = shieldBundleSettings.Effects[0].Settings;
            shieldSettings.DurationBase = roundsDuration;
            shieldSettings.DurationPlus = 0;
            shieldSettings.DurationPerLevel = 1;

            EffectEntry shieldEntry = new EffectEntry
            {
                Key = shieldBundleSettings.Effects[0].Key,
                Settings = shieldSettings,
            };
            EffectBundleSettings bundleSettings = new EffectBundleSettings
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = "Garlythi-Shield",
                Effects = new EffectEntry[] { shieldEntry },
            };

            EntityEffectBundle bundle = new EntityEffectBundle(bundleSettings, caster);
            manager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);
        }

        void ApplySelenuFireResistance()
        {
            EffectSettings resistSettings = new EffectSettings
            {
                DurationBase = roundsDuration,
                DurationPlus = 0,
                DurationPerLevel = 1,
                // Guaranteed (not just raised) resistance chance for Blood Surge's duration - see file
                // header for why this, not the classic Resist Fire spell's own level-scaled chance, is
                // what "Immunity" means here.
                ChanceBase = 100,
                ChancePlus = 0,
                ChancePerLevel = 1,
            };
            EffectEntry resistEntry = new EffectEntry { Key = "ElementalResistance-Fire", Settings = resistSettings };
            EffectBundleSettings bundleSettings = new EffectBundleSettings
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = "Selenu-FireImmunity",
                Effects = new EffectEntry[] { resistEntry },
            };

            EntityEffectBundle bundle = new EntityEffectBundle(bundleSettings, caster);
            manager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);
        }

        void ChargeCostOnce()
        {
            if (hasChargedCost)
                return;
            hasChargedCost = true;

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
        }

        public override void MagicRound()
        {
            base.MagicRound();

            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Catch-up guard - see trueRoundsRemaining's own comment above. base.MagicRound() just
            // decremented the framework's own RoundsRemaining unconditionally; this reasserts the
            // REAL value, only actually advancing once per real frame regardless of how many times
            // MagicRound() itself gets called this frame (once per catch-up round in a burst).
            if (Time.frameCount != lastDecrementFrame)
            {
                lastDecrementFrame = Time.frameCount;
                trueRoundsRemaining--;
            }

            if (trueRoundsRemaining <= 0)
            {
                RoundsRemaining = 0; // let the framework's own next pass End() it normally
                return;
            }
            RoundsRemaining = trueRoundsRemaining;

            // Recomputed from live Humanity every round (not fixed at cast time) - see file header
            // for why this is what makes a recast-refresh re-evaluate the band for free.
            int bonus = GetSkillBonus();
            for (int i = 0; i < weaponSkills.Length; i++)
                SetSkillMod(weaponSkills[i], bonus);

            // Vraseth clan feature (Clan Features spec, §2): same bonus, Running skill too.
            if (VoxsVampirismMod.VoxsVampirism.GetPlayerVampireClan() == VampireClans.Vraseth)
                SetSkillMod(DFCareer.Skills.Running, bonus);
        }

        static int GetSkillBonus()
        {
            int humanity = VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity();
            if (humanity <= 0)
                return 30;
            if (humanity <= 5)
                return 20;
            return 10;
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "BloodSurgeEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            // DurationPerLevel must be nonzero even though the actual duration is pinned to exactly
            // roundsDuration in code (see BecomeIncumbent/AddState) - properties.SupportDuration=true
            // means BaseEntityEffect.Start() and third-party cost-reduction mods alike divide by this
            // value when computing spell-point costs (confirmed via a real DivideByZeroException flood
            // in Player.log the moment the spellbook was opened) regardless of whether the framework's
            // own computed RoundsRemaining is ever actually used.
            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                DurationBase = roundsDuration,
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
