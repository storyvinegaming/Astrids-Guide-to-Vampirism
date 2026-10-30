// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mesmerize Rework spec: self-cast now, not targeted-at-range. Two simultaneous effects fire on
// cast: priming the caster's own gaze (VoxsVampirismEffect.PrimeGaze() - the next townsperson
// activation opens the Mesmerize command menu instead of dialogue, see MesmerizeGazeWatcher.cs) and
// pacifying every human-class enemy within a 10-foot AoE around the caster, each rolled individually
// with the same Humanity-scaled formula the old single-target version used. Create Thrall/Loot/
// Interrogate/Feed themselves (MesmerizeCommandMenu.cs) are unchanged - only how each menu is
// reached changes.
//
// Human-class-only AoE targeting absorbs the old vampire-immunity/lycanthrope rules structurally,
// with no special-case code: EntityTypes.EnemyClass excludes every EnemyMonster-typed thing -
// vampires, undead, daedra, and werewolves/wereboars alike are all confirmed EnemyMonster this
// session, not EnemyClass. The one exception that stays an explicit check is hunters
// (VampireHunterMarker) - "preparation, not nature" protects them even though they're EnemyClass
// like any other human.
//
// Old targeted-cast machinery removed entirely: no more per-target ChanceSuccess/Evaluate() gate (a
// self-cast trivially always "hits" its own caster - only the per-enemy AoE rolls are probabilistic
// now, internal to MagicRound(), never exposed as the spell's own success chance), no more
// GetPeeredEntityBehaviour target resolution, no more direct MesmerizeCommandMenu.ShowForEnemy()
// call from here - that's MesmerizeGazeWatcher's job now, gated on MesmerizedEnemyMarker instead.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect.Arena2;

namespace VoxsVampirismMod.Effects
{
    public class MesmerizeEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Mesmerize";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = true;
            properties.AllowedTargets = EntityEffectBroker.TargetFlags_Self;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Illusion;
            properties.SupportChance = false; // no overall pass/fail roll anymore - only the per-enemy AoE rolls, internal to MagicRound()
            properties.DisableReflectiveEnumeration = true;
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };

            // SupportDuration=true + zeroed DurationCosts, same flat-cost trick every other spell in
            // this mod uses (see HuntersSightEffect's own header) - NOT because Mesmerize has a real
            // duration to speak of (it's an instant self-cast), but because leaving every one of
            // SupportDuration/SupportChance/SupportMagnitude false/unset (as this effect did after
            // its self-cast rework) trips FormulaHelper.CalculateEffectCosts' own "no active
            // components" fallback (its own comment: "e.g. Teleport") - a fixed fudge-cost formula
            // completely unrelated to this mod's own zeroed-cost convention, which is what was
            // producing a 20 SP spellbook cost instead of the uniform 5 every other spell in this mod
            // lands on (still refunded via SpellCastCostRefunder.cs either way, but the engine auto-
            // deducts the real amount BEFORE that refund runs - a player with 6-19 Magicka could fail
            // to cast at all). Duration itself is otherwise irrelevant to this effect.
            properties.SupportDuration = true;
            properties.DurationCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        const int bloodCostPerCast = 1;
        const float aoeRadiusFeet = 10f;
        const float aoeRadiusUnits = aoeRadiusFeet * 0.3048f;

        // Per-target pacify flash - same one-shot billboard technique VampiricDrainEffect/Astrid's
        // Guide to Necromancy's CorpseExplosion/Deathknell already use (GameObjectHelper.
        // CreateDaggerfallBillboardGameObject + one of DFU's own spell-impact archives), reusing the
        // Poison archive (377) specifically - the same one Deathknell already reuses for its own green
        // "life drained away" look (confirmed in that file's own comment), so no color tint is needed
        // to read as green the way VampiricDrainEffect needed one to push Fire (375) toward red.
        // Raised a bit above the target's own root/feet position so it flashes near chest height on a
        // standing target rather than at the ground.
        const int pacifyVisualArchive = 377;
        const int pacifyVisualRecord = 1;
        const int pacifyVisualFramesPerSecond = 15;
        static readonly Vector3 pacifyVisualOffset = new Vector3(0f, 1f, 0f);

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Self-cast. The next townsperson you meet eyes with opens a",
                "command menu - Loot or Interrogate them, and they always",
                "answer. Also attempts to pacify every human enemy within",
                "ten feet; a pacified one opens that same menu, Feed added.",
                "Costs 1 blood point. Has no hold over anything that isn't human.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect vampirism =
                GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect()
                    as DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect;
            if (vampirism == null)
                return;

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(bloodCostPerCast, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0 && caster != null)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            vampirism.PrimeGaze();
            int pacifiedCount = RunAoePacify();

            DaggerfallUI.AddHUDText("Your gaze sharpens; the night leans closer.");
            VoxsVampirism.Log("Mesmerize: self-cast - blood spent, gaze primed, {0} enemy(ies) pacified in AoE.", pacifiedCount);
        }

        // Reuses ScentOfPreyEffect.FindLivingTargetsInRange's own enemy-enumeration technique
        // (ActiveGameObjectDatabase's fast registry unioned with a genuine FindObjectsOfType scan,
        // since some mod-added enemies skip the fast registry - confirmed necessary this session).
        int RunAoePacify()
        {
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            float radiusSq = aoeRadiusUnits * aoeRadiusUnits;
            // Beast-leaning (low Humanity) succeeds more often now, matching every other
            // Humanity-scaled vampire working in this mod (Dominate's own chance formula included) -
            // always succeeds at Humanity 0, a coin flip at Humanity 10. Previously ran the other way
            // (more reliable the more human the caster remained), which was inconsistent with the
            // rest of the mod's own "beast is powerful" pattern.
            int humanity = VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity();
            int successChance = 50 + (10 - humanity) * 5;

            HashSet<DaggerfallEntityBehaviour> candidates = new HashSet<DaggerfallEntityBehaviour>(ActiveGameObjectDatabase.GetActiveEnemyBehaviours());
            DaggerfallEntityBehaviour[] allBehaviours = UnityEngine.Object.FindObjectsOfType<DaggerfallEntityBehaviour>();
            for (int i = 0; i < allBehaviours.Length; i++)
            {
                if (allBehaviours[i].EntityType == EntityTypes.EnemyClass)
                    candidates.Add(allBehaviours[i]);
            }

            int pacifiedCount = 0;
            foreach (DaggerfallEntityBehaviour targetBehaviour in candidates)
            {
                // Human-class only - this is the whole of the old vampire/undead/daedra/lycanthrope
                // exclusion now, falling straight out of EntityType rather than any special-case code
                // (see file header).
                if (targetBehaviour == null || targetBehaviour.EntityType != EntityTypes.EnemyClass)
                    continue;

                EnemyEntity enemyEntity = targetBehaviour.Entity as EnemyEntity;
                if (enemyEntity == null || enemyEntity.Team == MobileTeams.PlayerAlly)
                    continue;

                if ((targetBehaviour.transform.position - playerPosition).sqrMagnitude > radiusSq)
                    continue;

                // Hunters resist - "preparation, not nature" - the one exclusion that stays explicit,
                // same as the old single-target version.
                if (targetBehaviour.GetComponent<VampireHunterMarker>() != null)
                {
                    VoxsVampirism.Log("Mesmerize AoE: '{0}' resists - hunter training.", targetBehaviour.name);
                    continue;
                }

                bool success = Dice100.SuccessRoll(successChance);
                VoxsVampirism.Log("Mesmerize AoE: '{0}' roll ({1}) - success={2}.", targetBehaviour.name, successChance, success);
                if (!success)
                    continue;

                EnemyMotor motor = targetBehaviour.GetComponent<EnemyMotor>();
                bool wasHostile = motor != null && motor.IsHostile;
                if (motor != null)
                    motor.IsHostile = false;

                EnemySenses senses = targetBehaviour.GetComponent<EnemySenses>();
                if (senses != null)
                {
                    senses.Target = null;
                    senses.SecondaryTarget = null;
                }

                if (targetBehaviour.GetComponent<MesmerizedEnemyMarker>() == null)
                    targetBehaviour.gameObject.AddComponent<MesmerizedEnemyMarker>();

                SpawnPacifyVisual(targetBehaviour.transform.position + pacifyVisualOffset);

                DaggerfallUI.AddHUDText(wasHostile
                    ? "Their hostility melts away, and they trouble you no further."
                    : "Their will folds into yours without resistance.");

                pacifiedCount++;
            }

            return pacifiedCount;
        }

        // See the field-group comment above pacifyVisualArchive for why this is the Poison archive
        // specifically. No sound here deliberately - the pacify HUD text is the only feedback needed
        // per target, and stacking an impact sound per enemy in a multi-target AoE would get noisy fast.
        static void SpawnPacifyVisual(Vector3 position)
        {
            GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(pacifyVisualArchive, pacifyVisualRecord, null);
            go.transform.position = position;

            Billboard billboard = go.GetComponent<Billboard>();
            billboard.FramesPerSecond = pacifyVisualFramesPerSecond;
            billboard.FaceY = true;
            billboard.OneShot = true;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                meshRenderer.receiveShadows = false;
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                UnityEngine.Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "MesmerizeEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 100,
                ChancePlus = 0,
                ChancePerLevel = 1,
                // DurationPerLevel must be nonzero even though the real effect is a one-shot
                // MagicRound with nothing that reads RoundsRemaining afterward - see SetProperties'
                // own comment for why SupportDuration is true at all, and HuntersSightEffect's own
                // Create() for the exact same DivideByZeroException this avoids.
                DurationBase = 1,
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
