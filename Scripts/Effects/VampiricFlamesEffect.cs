// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Area-of-effect Destruction spell granted by Astrid's Guide to Vampirism. TargetTypes.AreaAtRange
// needs no radius property of its own to configure - DaggerfallMissile's own fixed ExplosionRadius
// already handles finding every entity caught in the blast at the framework level (confirmed by
// reading DaggerfallMissile.cs directly: DoAreaOfEffect uses Physics.OverlapSphereNonAlloc with its
// own constant radius, entirely outside any effect's own SetProperties/EffectSettings). What that
// means here: the framework creates one SEPARATE EntityEffectManager/effect instance PER STRUCK
// TARGET for an AoE cast, each with its own independent MagicRound() calls - there is no single
// shared instance whose MagicRound() sees the whole target list at once the way VampiricDrainEffect
// (SingleTargetAtRange, exactly one target, no such problem) can assume.
//
// Cost (1 blood, 5 SP charged-then-refunded) is charged exactly ONCE per cast even though the
// framework creates one effect instance per struck target - see SpellCastCostRefunder.cs's own
// header for why that charge lives there instead of here.
//
// Damage formula: 50 + (10 - Humanity) * caster level - a flat 50 base plus one (10-Humanity) chunk
// per character level, so the spell now scales with the caster's own level as well as Humanity (the
// old flat 1 + (10-Humanity)*10 formula never grew with level at all). At Humanity 10 this is just the
// flat 50 base regardless of level (10-10=0); at Humanity 0 it's 50 + 10 per level.
// AreaAtRange (a target point, area centered there, matching vanilla Fireball's own shape) rather
// than AreaAroundCaster - flagged as the chosen interpretation, not explicitly confirmed. No
// caster-exclusion special-cased - vanilla's own AreaAtRange spells (Fireball included) can hit the
// caster too if they're standing close enough to their own impact point, and this doesn't deviate
// from that by default - two Mod Settings ("Vampiric Flames/Ice Spare Caster"/"...Spare Allies",
// VoxsVampirismMod.VampiricFireSparesCaster/VampiricFireSparesAllies, both off by default) let the
// player opt into excluding themselves and/or their own court (thralls, created vampires, Dominated
// creatures - anything with Team == PlayerAlly) from the blast, checked in MagicRound() below since
// the framework's own AoE targeting has no such exclusion to hook into.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsVampirismMod.Effects
{
    public class VampiricFlamesEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Vampiric-Flames";

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
                "An explosion of vampiric fire, costing 5 Magicka and 1 blood point",
                "each cast. Weaker the more human you remain, stronger the more",
                "beast.");
        }

        // Cost is no longer handled here at all - see SpellCastCostRefunder.cs's own header for why.
        // This effect has no eligibility gate (ChanceSuccess is always true) and MagicRound only ever
        // runs once per struck target, so a cast that hits nobody would never reach any per-effect
        // code here - the central hook fires on every real cast attempt regardless, which is the only
        // place that can charge blood for that case too.
        public override void MagicRound()
        {
            base.MagicRound();

            DaggerfallEntityBehaviour targetBehaviour = GetPeeredEntityBehaviour(manager);
            if (!targetBehaviour || caster == null)
                return;

            // AreaAtRange has no per-caster/per-team exclusion of its own (confirmed reading
            // DaggerfallMissile.cs's own DoAreaOfEffect - a plain OverlapSphereNonAlloc, everyone
            // caught in the radius takes the same damage, caster included, same as vanilla's own AoE
            // spells) - these two Mod Settings are what an explicit exclusion would otherwise be.
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
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "VampiricFlamesEffect.Create: Failed to get template from effect broker");
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
                ElementType = ElementTypes.Fire,
                Name = EffectKey,
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
