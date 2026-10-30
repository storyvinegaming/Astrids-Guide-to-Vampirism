// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: instantly kills one of the player's own allies - a Thrall, a created Vampire, or a
// currently-Dominated creature - and grants blood exactly as feeding on them normally would (same
// heal/sound/flash side effects VoxsVampirismEffect.GainBloodNoHumanity already gives TryFeedOnThrall -
// this effect calls that same method directly, nothing reimplemented). A Thrall/created Vampire grants
// the same flat amount a normal feed on them already gives (+3/+6, via ThrallManager.DevourThrall); a
// Dominated creature has no fixed feed amount of its own (Dominate can bind literally any living
// creature), so it uses the same MaxHealth/Corpse Blood Divisor formula an ordinary wild corpse drain
// already uses, for the same "scale by how tough it was" reasoning.
//
// The resulting corpse is left already-drained (SneakFeedDrainedMarker, the exact marker a normal
// sneak-feed corpse drain adds) - see SneakFeedController.HandleDevourThrallCorpseMarking for how
// DevourThrallMarker (added to the live target right before it dies, below) gets translated onto the
// corpse once EnemyDeath.CompleteDeath() actually produces one - the same "tag the live entity first,
// translate onto the corpse via EnemyDeath.OnEnemyDeath" idiom HandleFightsBackEnemyDeath already
// established for FightsBackVariantMarker/FightsBackCorpseMarker.
//
// Fails outright (fizzles, no cost) against anything that isn't currently one of the player's own
// allies - not a real enemy-killing spell, and not usable on a townsperson or an ordinary passive
// creature either. Free of any blood cost, like feeding on a thrall already is.

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
    public class DevourThrallEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Devour-Thrall";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.SupportChance = true;
            properties.AllowedTargets = TargetTypes.SingleTargetAtRange;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Destruction;
            properties.DisableReflectiveEnumeration = true;
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Free. Single target. Instantly kills one of your own",
                "thralls, vampires, or Dominated creatures, and grants",
                "blood exactly as feeding on them would. Their corpse",
                "gives nothing further afterward.");
        }

        // Folds target validity into ChanceSuccess, same shape DominateEffect uses - a fizzled cast
        // against an invalid target costs nothing. Cached so the framework's own (possibly multiple)
        // reads of this getter never re-evaluate the target differently between reads.
        bool? cachedChanceSuccess;

        public override bool ChanceSuccess
        {
            get
            {
                if (cachedChanceSuccess == null)
                    cachedChanceSuccess = EvaluateChanceSuccess();
                return cachedChanceSuccess.Value;
            }
        }

        bool EvaluateChanceSuccess()
        {
            DaggerfallEntityBehaviour targetBehaviour = GetPeeredEntityBehaviour(manager);
            if (!targetBehaviour)
                return false;

            return ThrallManager.IsThrall(targetBehaviour.gameObject) || targetBehaviour.GetComponent<DominateTimer>() != null;
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour targetBehaviour = GetPeeredEntityBehaviour(manager);
            if (!targetBehaviour)
                return;

            EnemyEntity enemyEntity = targetBehaviour.Entity as EnemyEntity;
            if (enemyEntity == null)
                return;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);

            // Tagged BEFORE death - see file header for why the corpse can't be marked directly here.
            targetBehaviour.gameObject.AddComponent<DevourThrallMarker>();

            int amount = ThrallManager.DevourThrall(targetBehaviour.gameObject);
            if (amount < 0)
            {
                // Not a tracked thrall - must be a Dominated creature instead (already confirmed by
                // ChanceSuccess above). ThrallManager.DevourThrall never got the chance to kill it, so
                // that's done here directly instead.
                amount = Mathf.Max(1, enemyEntity.MaxHealth / VoxsVampirism.CorpseBloodDivisor);
                targetBehaviour.Entity.SetHealth(0);
            }

            vampirism.GainBloodNoHumanity(amount);

            DaggerfallUI.AddHUDText(string.Format("You drain the {0} dry, and it falls still. (Blood: {1}/10.)", name, vampirism.GetBloodPoints()));
            VoxsVampirism.Log("Devour Thrall: killed '{0}', gained {1} blood.", name, amount);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirism.LogPrefix + "DevourThrallEffect.Create: Failed to get template from effect broker");
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
                TargetType = TargetTypes.SingleTargetAtRange,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }

    /// <summary>
    /// Tags a live ally the instant Devour Thrall kills it - translated onto the resulting corpse (as
    /// SneakFeedDrainedMarker) by SneakFeedController.HandleDevourThrallCorpseMarking, since
    /// CorpseLootContainer doesn't exist until the death pipeline has actually completed.
    /// </summary>
    public class DevourThrallMarker : MonoBehaviour
    {
    }
}
