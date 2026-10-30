// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Targeted spell granted by Astrid's Guide to Vampirism, modeled on Astrid's Guide to Necromancy's
// own Subjugate Undead (same team-flip/allied-AI shape) but for a LIVING target instead of undead -
// see AllyCombatController.cs/DominateTimer.cs for the combat AI and expiry handling. The Legion
// System update ported Necromancy's own AllyCombatController wholesale (see that file's own header
// for why this is a port, not a shared reference) - this mod's own thralls/created vampires/Dominate
// targets all share that one component now, instead of the separate DominateAllyController this file
// used to add here.
//
// Chance of success is itself the potency axis here (beast-leaning by nature, no separate scaling
// formula needed): (10 - Humanity) * 10 percent, via the same Dice100.SuccessRoll() pattern already
// used elsewhere in this mod (e.g. SneakFeedController's Stealth check) - a high-Humanity caster is
// close to unable to Dominate at all. On failure: straightforward fizzle, no side effect - same as
// SubjugateUndeadEffect's own failed-roll case.
//
// Duration on success is its own separate formula now, deliberately decoupled from chance: 20 + 4 *
// Humanity rounds (per explicit request) - a MORE human caster keeps what they successfully dominate
// longer, even though they succeed less often to begin with. Optionally uncapped entirely: the
// "Permanent Dominate" Mod Setting (off by default) skips DominateTimer's own countdown altogether -
// see that file's own header - so a creature bound this way never reverts on its own, only via the
// shared legion budget (VoxsVampirism.GetLegionBudget()) running out of room for it.
//
// Valid targets: everything except Undead and Daedra (DFCareer.EnemyGroups, checked via
// EnemyEntity.GetEnemyGroup() the same way SubjugateUndeadEffect checks for Undead specifically) -
// including hostile humanoid class enemies, not just animals.
//
// The Legion System update: concurrent cap is now the shared AllyCombatController budget (thralls,
// created vampires, and Dominate targets all draw from the same pool - VoxsVampirism.GetLegionBudget())
// rather than the old Humanity-band headcount table. Dominate has no fixed type/tier the way thralls
// do (it can bind literally any living creature), so its own upkeep cost is scaled by the target's own
// MaxHealth instead of a flat number - Max(1, MaxHealth / VoxsVampirism.DominateUpkeepDivisor), same
// "scale by how tough it is" shape the mod's own Corpse Blood Divisor already uses elsewhere. Casting
// against a target the budget can't afford fizzles exactly like a failed roll, folded into the same
// ChanceSuccess check rather than a separate "wasted cast" path.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Utility;
using FullSerializer;

namespace VoxsVampirismMod.Effects
{
    public class DominateEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Dominate";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.SupportChance = true;
            properties.SupportDuration = true;
            properties.AllowedTargets = TargetTypes.SingleTargetAtRange;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.DisableReflectiveEnumeration = true;

            // Zeroed (was a real formula-driven cost, CostA/CostB/OffsetGold nonzero) so the engine's
            // own SP charge lands on the same 5-point floor every other spell in this mod does,
            // refunded via SpellCastCostRefunder.cs's central hook - the real cost was showing as a
            // caster-level/skill-scaled number (e.g. 60 SP) in the spellbook instead of the uniform 5
            // this mod's own spells all charge-then-refund.
            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
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
                "Bends a living creature to your will, to fight at your side,",
                "at a cost of 1 blood point. Chance to succeed is (10 minus",
                "your Humanity) times 10; duration is 20 rounds plus 4 more",
                "for every point of Humanity you still hold - the beast gambles",
                "better, the human holds longer. Fails against the undead or",
                "daedra, and Dominated creatures count against your court.");
        }

        // Folds target validity, the concurrent cap, AND the real percentage roll into one check -
        // see the file header for why. Cached so the framework's own (possibly multiple) reads of
        // this getter never re-roll or re-evaluate the target differently between reads.
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

            EnemyEntity enemyEntity = targetBehaviour.Entity as EnemyEntity;
            if (enemyEntity == null || enemyEntity.Team == MobileTeams.PlayerAlly)
                return false;

            // EnemyEntity.GetEnemyGroup() (FormulaHelper.GetEnemyEntityEnemyGroup) switches on
            // CareerIndex without ever checking EntityType first - it was only ever written for
            // monster-type enemies, where CareerIndex really is a MonsterCareers value. For a
            // class-based human enemy (EntityTypes.EnemyClass), CareerIndex is a completely different
            // number (MobileTypes.ID - 128, a 0-based index into the 19 human classes) that happens to
            // land in the same numeric range - e.g. Knight's class index (17) collides with
            // MonsterCareers.Zombie (17), so a Knight reads back as Undead and got silently rejected
            // by the check below on every single cast. Barbarian (15, collides with SkeletalWarrior)
            // and Knight_CityWatch (18, collides with Ghost) have the same problem. A human class enemy
            // is never literally undead or a daedra, so the group check below only applies to genuine
            // monster-type targets - class-based humans always pass through untouched.
            if (targetBehaviour.EntityType == EntityTypes.EnemyMonster)
            {
                DFCareer.EnemyGroups group = enemyEntity.GetEnemyGroup();
                if (group == DFCareer.EnemyGroups.Undead || group == DFCareer.EnemyGroups.Daedra)
                    return false;
            }

            // Concurrent cap: the shared legion budget (see file header) - this target's own upkeep
            // cost, computed fresh from its current MaxHealth, must fit alongside every other currently
            // active ally (thrall, created vampire, or another Dominated creature).
            int humanity = VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity();
            int upkeep = Mathf.Max(1, enemyEntity.MaxHealth / VoxsVampirismMod.VoxsVampirism.DominateUpkeepDivisor);
            if (!VoxsVampirismMod.AllyCombatController.CanAfford(upkeep))
                return false;

            // Blood cost (1 point, matching Vampiric Flames/Ice) is charged unconditionally the
            // instant the cast fires, from SpellCastCostRefunder.cs's own shared hook - not here, and
            // not gated on any of the validity checks above. See that file's own header for exactly
            // why: charging it here meant an invalid target or an over-budget cast was free, which
            // read as "did this cast even work?" rather than "did I actually commit blood to try".

            int successChance = (10 - humanity) * 10;
            return Dice100.SuccessRoll(successChance);
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

            int humanity = VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity();
            int durationRounds = 20 + 4 * (11 - humanity);

            MobileTeams originalTeam = enemyEntity.Team;
            enemyEntity.Team = MobileTeams.PlayerAlly;

            // Flipping Team alone doesn't stop a creature that was already mid-attack on the player -
            // EnemyMotor's own attack/pursuit state machine only reacts to Target/IsHostile, neither
            // of which a Team change touches on its own.
            EnemyMotor motor = targetBehaviour.GetComponent<EnemyMotor>();
            EnemySenses senses = targetBehaviour.GetComponent<EnemySenses>();
            if (motor != null)
                motor.IsHostile = false;
            if (senses != null)
            {
                senses.Target = null;
                senses.SecondaryTarget = null;
            }

            // Bug fix (confirmed live via Player.log: a single Dominated Assassin/Nightblade ended up
            // as 3 independent DominateTimer components on the SAME GameObject after a dungeon fight
            // full of casts, "unpacked" into 3 separate duplicate creatures the moment a transition's
            // record/respawn cycle ran FindObjectsOfType<DominateTimer>() and treated each COMPONENT
            // as its own creature). ChanceSuccess's own Team==PlayerAlly check is meant to refuse a
            // recast on an already-owned ally, but TargetTypes.SingleTargetAtRange is a real in-flight
            // missile - casting Dominate a second (or third) time at the same target while the first
            // cast's own missile hasn't landed yet (easy to do in a chaotic fight, or just recasting
            // because nothing visibly happened yet) lets multiple independent casts each check Team
            // BEFORE any of them has actually flipped it, so all of them pass and all of them used to
            // stack their own DominateTimer. Reusing an existing timer here - refreshing it exactly
            // like a genuine recast should feel, rather than duplicating - closes this regardless of
            // the precise race, the same "find-or-add, never just add" pattern ThrallManager.
            // RestoreDominatedAllies already established for the analogous reattach-after-load case.
            DominateTimer timer = targetBehaviour.GetComponent<DominateTimer>();
            bool isRefresh = timer != null;
            if (timer == null)
                timer = targetBehaviour.gameObject.AddComponent<DominateTimer>();
            timer.RoundsRemaining = durationRounds;
            timer.OriginalTeam = originalTeam;

            string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
            if (isRefresh)
                VoxsVampirismMod.VoxsVampirism.Log("Dominate: '{0}' was already bound - refreshed the existing bond instead of stacking a second one.", name);

            VoxsVampirismMod.AllyCombatController controller = targetBehaviour.GetComponent<VoxsVampirismMod.AllyCombatController>();
            if (controller == null)
                controller = targetBehaviour.gameObject.AddComponent<VoxsVampirismMod.AllyCombatController>();
            controller.Upkeep = Mathf.Max(1, enemyEntity.MaxHealth / VoxsVampirismMod.VoxsVampirism.DominateUpkeepDivisor);

            // Follower ambient "attract" sounds (EnemySounds.PlayAttractSound - idle moans/barks/
            // moves triggered purely by player proximity, no team check of its own at all) read wrong
            // for something at your own side. Restored by DominateTimer.Revert if this creature ever
            // reverts to hostile.
            EnemySounds enemySounds = targetBehaviour.GetComponent<EnemySounds>();
            if (enemySounds != null)
                enemySounds.enabled = false;
            // Budget-driven cull (AllyCombatController.CullOldestUntilBudgetFits) invokes this with
            // "budget" when the shared legion budget shrinks and this happens to be the oldest active
            // ally - routes back through DominateTimer's own revert-to-hostile logic (survives, doesn't
            // die) rather than a kill, matching this mod's own established release mechanic.
            controller.OnReleaseRequested = reason => timer.ReleaseNow(
                string.Format("The bond breaks - your hold has grown too thin to keep the {0} bound, and it turns on you.", name));

            DaggerfallUI.AddHUDText(string.Format("The {0} bends to your will. Court: {1}.", name, VoxsVampirismMod.AllyCombatController.DescribeBudget()));
            VoxsVampirismMod.VoxsVampirism.Log("Dominate: '{0}' dominated for {1} rounds (Humanity {2}, upkeep {3}). Legion budget: {4}.",
                name, durationRounds, humanity, controller.Upkeep, VoxsVampirismMod.AllyCombatController.DescribeBudget());
        }

        // Same reasoning as Necromancy's own SubjugateUndeadEffect.SaveData_v1, and load-bearing for
        // the same reason: this bundle lives on the TARGET enemy's own EntityEffectManager, and DFU
        // serializes/restores enemy effect bundles - without persisting hasFired, a freshly
        // deserialized instance would re-run the whole conversion once more on the first magic round
        // after a load, stacking a second DominateTimer (and resetting the duration) on top of the
        // one ThrallManager.RestoreDominatedAllies has already reattached from the real saved record.
        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public bool hasFired;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.hasFired = hasFired;
            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            hasFired = data.hasFired;
        }

        #endregion

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "DominateEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);

            // ChanceBase is a nominal placeholder only - ChanceSuccess overrides the real roll
            // entirely. DurationBase is a long fallback, same pattern as every other toggle in this
            // mod (Mist Form, Hunter's Sight) and for the same reason: MagicRound only ever runs its
            // real logic ONCE (hasFired) before going permanently inert, and the actual duration is
            // tracked entirely by DominateTimer's own independent countdown, attached inside that one
            // firing. A short, normally-decrementing framework duration here was a real, separate bug
            // (found investigating a report that a dominated creature stopped following after a save/
            // load): DFU's own effect framework counts RoundsRemaining down every magic round
            // regardless of DominateTimer, and once it hit 0, End() fired and the WHOLE bundle was
            // silently dropped from instancedBundles - meaning any save made afterward had no Dominate
            // record at all for that creature, even though DominateTimer/AllyCombatController were
            // still actively running it as a live ally in the current session. Loading such a save
            // restored the creature as an ordinary undominated enemy, with nothing left to Resume().
            // A long fallback here means the framework's own duration is never the thing that ends
            // this - DominateTimer's own Revert()/ReleaseNow() is, exactly as intended - so the bundle
            // (and therefore the save data) stays alive for as long as the creature is actually bound,
            // Permanent Dominate included.
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 50,
                ChancePlus = 0,
                ChancePerLevel = 1,
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
                TargetType = TargetTypes.SingleTargetAtRange,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
