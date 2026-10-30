// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Redesigned from its original "mark nearby townspeople as compass blips" version (DetectEffect +
// HUDCompass, the standard vanilla DetectEnemy/DetectMagic shape) into a Detect-Life-style effect:
// every living creature within a configurable range (120 feet by default, see the "Scent Of Prey
// Detection Range" Mod Settings slider) trails a small drifting mist of red particles, refreshed
// continuously and visible through walls, closer to Oblivion's own Detect Life. See
// ScentOfPreyVisuals.cs for the actual effect (a real 3D ParticleSystem rendered with one of DFU's own
// existing shaders that already ignores scene depth, rather than a custom-authored glow/outline shader
// - DFU's runtime mod compiler has no access to Unity's shader compiler, so writing a brand new shader
// from loose .cs files isn't possible; reusing an existing always-on-top shader achieves the same
// "see them through walls" result by a different, fully achievable mechanism).
//
// The old compass-blip version likely never worked reliably on human-class enemies (Knight, Barbarian,
// Knight_CityWatch) for the same reason Dominate silently failed on them (see DominateEffect.cs's own
// header comment): PlayerGPS.GetEntityFlags -> EnemyEntity.GetEnemyGroup() has the identical
// CareerIndex/MonsterCareers collision bug, so those three classes would have been flagged Undead
// rather than Humanoid and never shown as blips at all. This rewrite sidesteps PlayerGPS's flag system
// entirely - scanning ActiveGameObjectDatabase.GetActiveEnemyBehaviours() directly and applying the
// same EntityType-aware Undead/Daedra exclusion DominateEffect uses (only trust GetEnemyGroup() for
// genuine EntityTypes.EnemyMonster targets; EntityTypes.EnemyClass humans are never excluded as
// Undead/Daedra, since that classification never legitimately applies to them).
//
// "Living" here means: any non-dead, non-allied enemy/monster (hostile or passive, human-class or
// beast, excluding genuine Undead/Daedra), plus every civilian NPC (outdoor wandering civilians via
// ActiveGameObjectDatabase.GetActiveCivilianMobileBehaviours(), indoor StaticNPC billboards via the
// same GetComponentsInChildren sweep the original version used, since StaticNPCs are structurally
// outside both PlayerGPS and ActiveGameObjectDatabase's tracked object sets entirely).
//
// Now a genuine ON/OFF TOGGLE (cast once to turn on, cast again to turn off), matching Hunter's
// Sight's own toggle mechanism exactly: AddState(incumbent) (recast while already active) calls
// manager.EndIncumbentEffect<ScentOfPreyEffect>() - the framework's own public API for forcibly ending
// an incumbent immediately - rather than the previous version's "refresh to a fresh duration" behavior.
// Dropped the Humanity-scaled duration this effect used to have along with that change: a toggle's
// "how long it stays on" is however long the player leaves it on, not something a duration formula
// should also be trying to own, so this now has no Humanity scaling at all - the same "no natural axis
// to scale" reasoning Suppress Beast/Hunter's Sight already use.
//
// DurationBase is a long fallback (see Create() below) rather than a meaningful gameplay duration, for
// the same reason Hunter's Sight's is: the toggle itself is the real off switch. That large a
// DurationBase fed into the framework's own formula-driven cost calc (which multiplies CostA by
// DurationBase directly) is exactly what produced the "thousands of spell points" bug Suppress Beast
// and Hunter's Sight both hit earlier - so DurationCosts is zeroed out here too; the real SP cost is
// the engine's own floor, charged-and-refunded via SpellCastCostRefunder.cs like every other spell in
// this mod. Also costs 1 blood point to enable (charged once, on activation, not per round) -
// insufficient blood no longer refuses (Blood Economy Rebalance, §5), it pays the shortfall in Health.
//
// Renamed Scent of Prey -> Blood Sense (Blood Economy Rebalance, §7) - display text only. EffectKey
// ("Scent-Of-Prey"), this class's name, and this file's name all stay UNCHANGED - EffectKey is the
// save-compat-critical identifier already-learned player spells reference, and changing it risks the
// exact class of DivideByZeroException bug this mod hit earlier when a spell's declared shape changed
// under already-learned saved data (see VampiricDrainEffect.cs's own header for that incident). Only
// GroupName (the actual spellbook/spellmaker display name) is overridden to a literal string instead
// of returning EffectKey.
//
// Quest-target highlighting (§7): FindLivingTargetsInRange() also checks each candidate for a
// QuestResourceBehaviour component (DFU's own quest-target marker, attached directly to a live
// enemy/NPC GameObject when it's spawned/linked as a quest Foe or Person resource - read directly
// from Assets/Scripts/Game/Questing/Components/QuestResourceBehaviour.cs rather than guessed) and
// returns a second HashSet of quest-flagged transforms alongside the existing target list.
// ScentOfPreyVisuals colors those markers differently - the blood of destiny burns brighter.
//
// Target scan cadence: originally re-ran FindLivingTargetsInRange() once per MagicRound (the
// game's own classic-minute clock, which at default timescale is roughly a handful of real seconds
// per round but varies with the player's own timescale setting) - meaning a target that just moved
// into range could take that long to actually appear. Moved to ConstantEffect() (called once per
// rendered frame for every active effect, per EntityEffect.cs's own doc comment) instead, self-
// throttled against Time.unscaledTime so the actual scan still only runs on a fixed real-time
// interval (1-5 seconds, "Blood Sense Refresh Interval" Mod Settings slider, default 3) rather than
// every frame - decoupling detection latency from the game's own round timing entirely, and making
// it tunable. The scan itself (FindObjectsOfType<DaggerfallEntityBehaviour>() among other lookups)
// is a genuine full-scene sweep, which is why this stays interval-gated rather than truly per-frame -
// see FindLivingTargetsInRange's own comment on why FindObjectsOfType is needed here at all.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Questing;

namespace VoxsVampirismMod.Effects
{
    public class ScentOfPreyEffect : IncumbentEffect
    {
        public static readonly string EffectKey = "Scent-Of-Prey";

        // Read live from Mod Settings (default 120 feet) each time FindLivingTargetsInRange() runs,
        // rather than baked in once - ModSettingsWindow re-invokes the mod's LoadSettingsCallback as
        // soon as the player saves, so this picks up a changed value on the very next MagicRound
        // without needing to re-toggle the spell. Converted to Unity's world units - this project's
        // MeshReader.GlobalScale treats one Unity unit as approximately one real-world meter (see its
        // own "True scale" comment), so this is a reasonable approximation, not an exact conversion.
        static float DetectRadiusUnits => VoxsVampirismMod.VoxsVampirism.ScentOfPreyDetectionRangeFeet * 0.3048f;

        // Real-time (not magic-round) throttle for the scan below - see file header. -1000 so the
        // very first ConstantEffect() tick after casting scans immediately rather than waiting out
        // a full interval with no markers shown.
        float lastScanRealTime = -1000f;

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = true;
            properties.SupportDuration = true;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Thaumaturgy;
            properties.DisableReflectiveEnumeration = true;

            // See file header - a large fallback DurationBase fed into the framework's own cost
            // formula produces an insane cost, so this is zeroed out and replaced with a flat charge.
            properties.DurationCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        const int bloodCostToEnable = 1;

        // Display-only rename (Blood Economy Rebalance, §7) - see file header for why EffectKey
        // itself stays "Scent-Of-Prey".
        public override string GroupName => "Blood Sense";
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                string.Format("A toggle - every living creature within {0} feet marks", VoxsVampirismMod.VoxsVampirism.ScentOfPreyDetectionRangeFeet),
                "itself to your eyes through walls in a drifting mist,",
                "quest prey burning a different color than the rest.",
                "Costs 1 blood point to raise. Cast again to let it fade.");
        }

        protected override bool IsLikeKind(IncumbentEffect other)
        {
            return other is ScentOfPreyEffect;
        }

        // Recast-while-active toggles off, rather than refreshing/stacking duration.
        protected override void AddState(IncumbentEffect incumbent)
        {
            manager.EndIncumbentEffect<ScentOfPreyEffect>();
        }

        protected override void BecomeIncumbent()
        {
            base.BecomeIncumbent();
            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Insufficient blood no longer refuses activation (Blood Economy Rebalance, §5) - the
            // shortfall is paid in Health instead. Magicka is charged-and-refunded by
            // SpellCastCostRefunder.cs, not here.
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                manager.EndIncumbentEffect<ScentOfPreyEffect>();
                return;
            }

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(bloodCostToEnable, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0 && caster != null)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            DaggerfallUI.AddHUDText("The scent of life sharpens around you.");
        }

        public override void End()
        {
            base.End();
            if (GetPeeredEntityBehaviour(manager) == GameManager.Instance.PlayerEntityBehaviour)
            {
                VoxsVampirismMod.ScentOfPreyVisuals.Clear();
                DaggerfallUI.AddHUDText("The scent fades.");
            }
        }

        public override void ConstantEffect()
        {
            base.ConstantEffect();

            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Transiently null during scene transitions (full mod audit) - an uncaught NRE inside a
            // recurring effect's ConstantEffect/MagicRound is the round-timer-freeze crash class
            // documented in NightWalkerSpawner.GuardedReconcile's own header, so skip the scan
            // rather than trust these are always available. The scan simply runs on the next tick.
            if (GameManager.Instance.PlayerObject == null || GameManager.Instance.PlayerEnterExit == null)
                return;

            if (Time.unscaledTime - lastScanRealTime < VoxsVampirismMod.VoxsVampirism.ScentOfPreyRefreshIntervalSeconds)
                return;
            lastScanRealTime = Time.unscaledTime;

            HashSet<Transform> questTargets;
            List<Transform> targets = FindLivingTargetsInRange(out questTargets);
            VoxsVampirismMod.ScentOfPreyVisuals.SetTargets(targets, questTargets);
        }

        static List<Transform> FindLivingTargetsInRange(out HashSet<Transform> questTargets)
        {
            List<Transform> result = new List<Transform>();
            questTargets = new HashSet<Transform>();
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            float radius = DetectRadiusUnits;
            float radiusSq = radius * radius;

            // ActiveGameObjectDatabase.GetActiveEnemyBehaviours() only returns enemies that were
            // registered through DaggerfallEnemy.Awake() (see ActiveGameObjectDatabase.RegisterEnemy) -
            // the standard path every vanilla-spawned enemy goes through. Some third-party content
            // mods place enemies through a different route that skips or delays that registration
            // (observed: a world-content mod's placed Knights weren't detected here at all until
            // attacked, at which point some other system - not this scan - clearly already knew about
            // them, since SneakFeedController's own raycast, which never went through
            // ActiveGameObjectDatabase in the first place, always found them fine). Object.
            // FindObjectsOfType<DaggerfallEntityBehaviour>() is a genuine, direct scene scan
            // independent of that registry, catching anything the fast path might miss - only ever
            // called on the real-time interval ConstantEffect() throttles to (see file header, "Blood
            // Sense Refresh Interval" Mod Settings slider, 1-5 seconds), not per-frame, so the
            // performance cost this class is normally too slow for doesn't apply here.
            HashSet<DaggerfallEntityBehaviour> enemyCandidates = new HashSet<DaggerfallEntityBehaviour>(ActiveGameObjectDatabase.GetActiveEnemyBehaviours());
            DaggerfallEntityBehaviour[] allBehaviours = UnityEngine.Object.FindObjectsOfType<DaggerfallEntityBehaviour>();
            for (int i = 0; i < allBehaviours.Length; i++)
            {
                DaggerfallEntityBehaviour behaviour = allBehaviours[i];
                if (behaviour.EntityType == EntityTypes.EnemyClass || behaviour.EntityType == EntityTypes.EnemyMonster)
                    enemyCandidates.Add(behaviour);
            }

            foreach (DaggerfallEntityBehaviour behaviour in enemyCandidates)
            {
                if (behaviour == null || behaviour.Entity == null || behaviour.Entity.CurrentHealth <= 0)
                    continue;

                EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
                if (enemyEntity == null || enemyEntity.Team == MobileTeams.PlayerAlly)
                    continue;

                // Same EntityType-aware fix DominateEffect uses - only genuine monster-type entities
                // have a CareerIndex that GetEnemyGroup() can correctly read as Undead/Daedra; a
                // class-based human enemy's CareerIndex means something else entirely (see file header).
                if (behaviour.EntityType == EntityTypes.EnemyMonster)
                {
                    DFCareer.EnemyGroups group = enemyEntity.GetEnemyGroup();
                    if (group == DFCareer.EnemyGroups.Undead || group == DFCareer.EnemyGroups.Daedra)
                        continue;
                }

                if ((behaviour.transform.position - playerPosition).sqrMagnitude <= radiusSq)
                {
                    result.Add(behaviour.transform);
                    if (behaviour.GetComponent<QuestResourceBehaviour>() != null)
                        questTargets.Add(behaviour.transform);
                }
            }

            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveCivilianMobileBehaviours())
            {
                if (behaviour == null)
                    continue;

                if ((behaviour.transform.position - playerPosition).sqrMagnitude <= radiusSq)
                {
                    result.Add(behaviour.transform);
                    if (behaviour.GetComponent<QuestResourceBehaviour>() != null)
                        questTargets.Add(behaviour.transform);
                }
            }

            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInside && playerEnterExit.Interior != null)
            {
                StaticNPC[] indoorNpcs = playerEnterExit.Interior.GetComponentsInChildren<StaticNPC>();
                for (int i = 0; i < indoorNpcs.Length; i++)
                {
                    StaticNPC npc = indoorNpcs[i];
                    if (npc == null || !npc.gameObject.activeInHierarchy)
                        continue;

                    if ((npc.transform.position - playerPosition).sqrMagnitude <= radiusSq)
                    {
                        result.Add(npc.transform);
                        if (npc.GetComponent<QuestResourceBehaviour>() != null)
                            questTargets.Add(npc.transform);
                    }
                }
            }

            return result;
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "ScentOfPreyEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);

            // A long fallback duration (roughly 90 days of classic-minute rounds, matching Hunter's
            // Sight's own toggle fallback) rather than anything meant to actually be felt in play - the
            // toggle (recast to turn off) is the real mechanism, this is just a safety net.
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
                // The spellbook window displays THIS field (EffectBundleSettings.Name), not
                // GroupName - GroupName only labels the per-effect detail panel inside a spell's own
                // page (DaggerfallSpellBookWindow.cs:647), while the spell list entry and title both
                // read spell.Name/spellSettings.Name directly (same file, lines 271/549). Missed on
                // the first rename pass, which only changed GroupName - fixed here so the spellbook
                // actually shows "Blood Sense" rather than the internal "Scent-Of-Prey" identifier.
                // A player who already has an old "Scent-Of-Prey"-named copy in their spellbook won't
                // have this rename retroactively applied to it (GrantSpellsFromBook's "already known"
                // check compares by this exact Name string) - they'd see a second "Blood Sense" entry
                // granted alongside the old one on their next grant pass, both driving the identical
                // underlying effect. Cosmetic/redundant, not harmful - EffectKey itself (the
                // save-compat-critical identifier) is unchanged.
                Name = "Blood Sense",
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
