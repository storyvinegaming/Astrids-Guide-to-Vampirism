// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: a free, instant, self-target utility spell that teleports the player's whole "blood
// court" - every thrall, every created vampire, and every currently-dominated creature - to
// positions near the player, then lets the existing ally AI (AllyCombatController) take over from
// there. Functionally identical to Astrid's Guide to Necromancy's own Command Undead.
//
// The Legion System update unified what this spell calls: AllyCombatController.TeleportAllToPlayer()
// now covers thralls, created vampires, AND Dominate targets all at once (they share one component and
// one activeAllies list now - see AllyCombatController.cs's own header), replacing the two separate
// calls (ThrallAllyController.TeleportAllToPlayer() + DominateAllyController.TeleportAllToPlayer())
// this file used before that unification. Neither touches DominateTimer's remaining duration or any
// thrall roster state - this only repositions.
//
// Affects only this mod's own allies - never touches Astrid's Guide to Necromancy's own, even if that
// mod is also installed, since the two mods have no shared state or component visibility at all (DFU
// compiles each mod's own bundled source into its own separate runtime assembly).
//
// Dormant transforming thralls (a legacy state - see ThrallManager.StartTransform's own header, no new
// transformation can land in it anymore) are still teleported - MaterializeAll/TeleportAllToPlayer both
// operate on whatever's currently in the roster/activeAllies list regardless of dormant state.
//
// SupportChance is enabled (with all-zero ChanceCosts) purely to dodge FormulaHelper's
// zero-active-component cost fudge (same trick Command Undead itself uses, and several of this mod's
// own other spells already use elsewhere) - ChanceSuccess always returns true, this is not a real
// gate.
//
// Renamed Command Blood Court -> Summon Court - display text only, same shape as Scent of Prey's own
// rename to Blood Sense (see that file's own header). EffectKey ("Command-Blood-Court"), this class's
// name, and this file's name all stay UNCHANGED - EffectKey is the actual save-compat-critical
// identifier (referenced by every player's own already-learned spell), so it's never touched by a
// display-only rename. Two places need the new name, not one: GroupName (the spellbook's own per-
// effect detail panel) below, AND EffectBundleSettings.Name in Create() (the spellbook's own spell
// list entry and title, a genuinely separate field - DaggerfallSpellBookWindow.cs reads spell.Name
// directly there, not GroupName at all). A player who already has an old "Command-Blood-Court"-named
// copy in their spellbook won't have this rename retroactively applied to it (GrantSpellsFromBook's
// own "already known" check compares by this exact Name string) - they'd see a second "Summon Court"
// entry granted alongside the old one on their next grant pass, both driving the identical underlying
// effect. Cosmetic/redundant, not harmful.

using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsVampirismMod.Effects
{
    public class CommandBloodCourtEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Command-Blood-Court";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.SupportChance = true;
            properties.DisableReflectiveEnumeration = true;
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        public override bool ChanceSuccess => true;

        // Display-only rename - see file header. EffectKey itself stays "Command-Blood-Court".
        public override string GroupName => "Summon Court";
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Free. Teleports your whole court - every thrall, every",
                "vampire you have made, and anything bound by Dominate -",
                "to your side at once.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            int courtSize = VoxsVampirismMod.AllyCombatController.ActiveAllyCount;
            if (courtSize == 0)
            {
                DaggerfallUI.AddHUDText("An empty court answers no summons.");
                return;
            }

            VoxsVampirismMod.AllyCombatController.TeleportAllToPlayer();

            DaggerfallUI.AddHUDText("Your blood court answers your call.");
            VoxsVampirismMod.VoxsVampirism.Log("Command Blood Court: teleported {0} allies to the player.", courtSize);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                UnityEngine.Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "CommandBloodCourtEffect.Create: Failed to get template from effect broker");
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
                // The spellbook window displays THIS field, not GroupName - see file header for why
                // both need the new name.
                Name = "Summon Court",
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
