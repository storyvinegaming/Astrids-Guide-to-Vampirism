// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// New spell: Mist Form - a toggle (same BecomeIncumbent/AddState/EndIncumbentEffect shape as Hunter's
// Sight/Scent of Prey) that turns the vampire to living mist. While active:
//   - Levitates, via the same LevitateMotor the vanilla Levitate effect drives directly
//     (GameManager.Instance.PlayerMotor.GetComponent<LevitateMotor>().IsLevitating). Move speed
//     (LevitateMotor.LevitateMoveSpeed, vanilla's own Levitate effect never touches this - it always
//     flies at LevitateMotor's fixed 4.0 default) scales with (100 - Humanity*10) as a percent boost
//     over that default - a beast-leaning vampire drifts faster than one clinging to their old self, same
//     "lower Humanity, stronger" direction every other Humanity-scaled ability in this mod uses. Reset
//     back to the unscaled default on End() so it doesn't leak into some later, unrelated levitation.
//   - Immune to non-magical weapon damage, via the same MinMetalToHit mechanic vanilla's own
//     VampirismEffect/LycanthropyEffect already use on the player for silver-only vulnerability
//     (DaggerfallEntity.MinMetalToHit, checked in FormulaHelper.CalculateAttackDamage) - set to the
//     highest tier (Daedric) here rather than vampirism's own Silver, since this is meant to be a much
//     harder wall than "no bronze" is. Reasserted every frame in ConstantEffect() since vanilla's own
//     VampirismEffect.ConstantEffect() reasserts ITS OWN Silver value every frame too and effect
//     ordering between the two isn't guaranteed - last-write-wins each frame is fine here, the same
//     way every other overlapping ConstantEffect state in this mod already behaves. This only checks
//     weapon material tier, not magical enchantment - a non-enchanted Daedric weapon would still get
//     through, same caveat vanilla's own Silver check already has.
//   - Passes straight through solid doors in any interior - dungeon or building alike (Physics.
//     IgnoreCollision between the player's own CharacterController and the door's collider while the
//     player is within immediate range, restored the moment they step away or the effect ends) rather
//     than opening them, since actually opening (SetOpen/ToggleDoor) would animate and play a sound -
//     a mist doesn't need the door to move, only the player. IgnoreCollision only ever affects the
//     player/door pair, never the door's collider itself (which stays enabled throughout) - an earlier
//     version disabled the door's own BoxCollider/MeshCollider components instead, which let enemies
//     pass through right alongside the player since a disabled collider blocks nobody, not just the
//     player it was meant for.
//   - Makes every door - every interior action door AND, unlike the collider trick above, exterior
//     building doors too - open as if unlocked the moment the player actually activates (clicks) one,
//     with no level-vs-lock-value check at all (real Open's own "too powerful" gate is bypassed
//     entirely - see MistFormOpenEffect's own header), via
//     MistFormOpenEffect, a small internal subclass of vanilla's own Open spell effect silently
//     attached for the duration (see that file's own header for exactly why: PlayerActivate's exterior
//     door check has no persistent lock field to zero out the way DaggerfallActionDoor.CurrentLockValue
//     works, only a private, from-scratch-computed lock check with exactly one public override hook -
//     FindIncumbentEffect<Open>() - built for precisely this purpose).
//   - Passes straight through enemies too, not just doors - Physics.IgnoreCollision between the
//     player's own CharacterController and each nearby enemy's CharacterController (the same
//     component type both player and enemy movement already use), refreshed once per MagicRound like
//     the door cache (RefreshEnemyPassthrough) and reverted the moment an enemy falls out of range or
//     the effect ends, using the exact same "civilian/hostile enemy scan, ActiveGameObjectDatabase plus
//     a FindObjectsOfType fallback for anything that route misses" pattern ScentOfPreyEffect already
//     established (see that file's own header for why the fallback scan exists).
//   - Can't attack (WeaponManager.Sheathed forced true every frame - the same field vanilla's own
//     "Sheathed" branch of WeaponManager's update short-circuits attack processing on) or cast any
//     OTHER spell. Deliberately NOT implemented via DaggerfallEntity.IsSilenced (vanilla's own Silence
//     effect uses that) - IsSilenced is checked by SetReadySpell() itself, before OnNewReadySpell even
//     fires, which would make it impossible to ever cast Mist Form AGAIN to toggle it back off. Instead
//     this polls manager.HasReadySpell/ReadySpell every frame in ConstantEffect() and calls
//     manager.AbortReadySpell() on anything that isn't Mist Form's own bundle - confirmed safe to call
//     from ConstantEffect (a separate call stack from SetReadySpell entirely), unlike calling it
//     synchronously from inside the OnNewReadySpell event itself, which fires in the middle of
//     SetReadySpell() BEFORE that method's own next line dereferences readySpell.Settings.TargetType
//     unconditionally - aborting (nulling readySpell) from inside that event handler would throw a
//     NullReferenceException back inside vanilla's own SetReadySpell.
//
// Cost: 1 blood point to activate, then 1 more every 4 magic rounds active (per explicit design -
// briefly changed to a pure real-time accrual during a bug hunt, reverted on request) - 5 SP
// charged-and-refunded via the shared SpellCastCostRefunder.cs hook on activation, same as every
// other spell in this mod. Blood Economy Rebalance (§5): neither the activation nor the per-round
// drain refuses/collapses at 0 blood - both pay through VoxsVampirismEffect.PayBloodOrHealth, which
// converts any shortfall to direct HP damage instead.
//
// The runaway-drain incident (documented so the safety floor below never gets "simplified" away):
// a live player death showed magic rounds arriving at roughly 35-40 PER REAL SECOND, sustained, in
// ordinary dungeon play - about 200x the normal one-per-5-real-seconds cadence at DFU's default 12x
// timescale - which turned this drain into ~10 charges/second and killed through the desperate-
// casting HP fallback in about a second. The player was NOT resting or fast traveling. Root cause
// was never conclusively named: EntityEffectBroker's own round clock is confirmed load-safe
// (InitMagicRoundTimer on SaveLoadManager.OnLoad), SyntheticTimeIncrease self-clears every frame,
// and this mod never touches RaiseTime/TimeScale - so something external was driving world time
// itself at that rate (a stuck accelerated-travel timescale being the classic candidate in a
// TravelOptions-based install). The charge below therefore keeps its 4-round cadence for every sane
// state but refuses to fire more than once per minRealSecondsBetweenCharges of REAL time - a floor
// that mathematically cannot engage below ~80x timescale (4 game-minutes take 3 real seconds at
// exactly 80x) - and LOGS loudly the first time it ever engages, so if that broken state returns it
// gets caught by name in Player.log instead of by a dead character.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;

namespace VoxsVampirismMod.Effects
{
    public class MistFormEffect : IncumbentEffect
    {
        public static readonly string EffectKey = "Mist-Form";

        const float doorCacheRadius = 25f;
        const float doorEffectRadius = 3.5f;
        const float enemyPassthroughRadius = 20f;

        readonly List<DaggerfallActionDoor> cachedNearbyDoors = new List<DaggerfallActionDoor>();
        readonly HashSet<DaggerfallActionDoor> disabledDoors = new HashSet<DaggerfallActionDoor>();
        readonly HashSet<CharacterController> ignoredEnemyControllers = new HashSet<CharacterController>();

        // 1 blood point to activate, then 1 more every 4 magic rounds active. The real-time floor is
        // a pure anomaly guard, invisible in all normal play - see the file header's runaway-drain
        // incident for exactly why it exists and must stay.
        const int activationBloodCost = 1;
        const int roundsPerBloodCost = 4;
        const float minRealSecondsBetweenCharges = 3f;
        int roundsSinceLastBloodCost;
        float lastChargeRealTime = -1000f;
        bool clampLoggedSinceLastCharge;

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = true;
            properties.SupportDuration = true;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.DisableReflectiveEnumeration = true;

            // Same flat-cost trick every other toggle in this mod uses - see HuntersSightEffect's own
            // header for why a large DurationBase fed into the framework's own cost formula is a
            // problem otherwise (spell costs in the hundreds of thousands).
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
                "A toggle. 1 blood point to activate, then 1 more every 4 rounds",
                "while active. Levitate, pass through doors (or unlock them) and",
                "enemies, and ignore mundane damage - but you cannot fight or",
                "cast while so unmade. At 0 blood, upkeep is paid in Health",
                "instead. Cast again to return to normal.");
        }

        protected override bool IsLikeKind(IncumbentEffect other)
        {
            return other is MistFormEffect;
        }

        // Recast-while-active toggles off, rather than refreshing/stacking duration.
        protected override void AddState(IncumbentEffect incumbent)
        {
            manager.EndIncumbentEffect<MistFormEffect>();
        }

        protected override void BecomeIncumbent()
        {
            base.BecomeIncumbent();
            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Insufficient blood no longer refuses activation (Blood Economy Rebalance, §5) - the
            // shortfall is paid in Health instead, via the same PayBloodOrHealth every blood-costing
            // spell in this mod now uses. Magicka is charged-and-refunded by SpellCastCostRefunder.cs
            // (the central hook), not here.
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                manager.EndIncumbentEffect<MistFormEffect>();
                return;
            }

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(activationBloodCost, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0 && caster != null)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            SetLevitating(true);
            EnsureMistFormOpenActive();
            EnsureLyreziInvisibilityActive();
            PlayMistSound();
            VoxsVampirismMod.MistFormVisuals.SetActive(true);
            DaggerfallUI.AddHUDText("Your body thins into drifting mist.");
        }

        public override void Resume(EntityEffectManager.EffectSaveData_v1 effectData, EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Resume(effectData, manager, caster);
            if (IsIncumbent && GetPeeredEntityBehaviour(manager) == GameManager.Instance.PlayerEntityBehaviour)
            {
                SetLevitating(true);
                EnsureMistFormOpenActive();
                EnsureLyreziInvisibilityActive();
                VoxsVampirismMod.MistFormVisuals.SetActive(true);
            }
        }

        // Lyrezi clan feature (Clan Features spec, §2): Mist Form also grants Invisibility for its
        // duration, for the Lyrezi bloodline specifically. Reuses vanilla's own InvisibilityNormal
        // effect directly (a plain attachable IncumbentEffect - confirmed cleanly attachable via the
        // same AssignBundle technique EnsureMistFormOpenActive above already uses) rather than
        // reimplementing transparency/AI-detection logic from scratch. InvisibilityTrue (bypasses even
        // magical detection) was deliberately not used - stronger than a passive clan perk warrants.
        // No self-managed "EndForGood" hook the way MistFormOpenEffect has (that's this mod's own
        // subclass; InvisibilityNormal is vanilla's), so End() below explicitly ends it instead - the
        // same manager.EndIncumbentEffect<T>() API the engine's own code already uses elsewhere to end
        // someone else's active Invisibility (EntityEffectManager.cs, "attacking breaks stealth").
        void EnsureLyreziInvisibilityActive()
        {
            if (VoxsVampirismMod.VoxsVampirism.GetPlayerVampireClan() != VampireClans.Lyrezi)
                return;
            if (manager.FindIncumbentEffect<InvisibilityNormal>() != null)
                return;

            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(InvisibilityNormal.EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "MistFormEffect.EnsureLyreziInvisibilityActive: Invisibility-Normal template not registered.");
                return;
            }

            // Long fallback duration matching Mist Form's own (see Create() below) - the real off
            // switch is Mist Form's own End(), which force-ends this too, not this running out.
            EffectSettings settings = new EffectSettings
            {
                DurationBase = 129600, DurationPlus = 0, DurationPerLevel = 1,
            };
            EffectEntry entry = new EffectEntry { Key = InvisibilityNormal.EffectKey, Settings = settings };
            EffectBundleSettings bundleSettings = new EffectBundleSettings()
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = InvisibilityNormal.EffectKey,
                Effects = new EffectEntry[] { entry },
            };

            EntityEffectBundle bundle = new EntityEffectBundle(bundleSettings, caster);
            manager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);
        }

        // Lazily loaded, same defensive shape as VoxsVampirismEffect's own feedingSound - a missing/
        // failed load never blocks the cast itself, just silently skips the sound. Played only on the
        // turn-ON transition (BecomeIncumbent), not on toggling off, matching Hunter's Sight's own
        // "charged only on activation" convention.
        static AudioClip mistSound;

        static void PlayMistSound()
        {
            if (mistSound == null)
                mistSound = VoxsVampirismMod.VoxsVampirism.Mod.GetAsset<AudioClip>("mist.ogg");
            if (mistSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(mistSound, DaggerfallUnity.Settings.SoundVolume);
        }

        public override void End()
        {
            base.End();
            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            SetLevitating(false);

            WeaponManager weaponManager = GameManager.Instance.WeaponManager;
            if (weaponManager != null)
                weaponManager.Sheathed = false;

            MistFormOpenEffect openHelper = manager.FindIncumbentEffect<MistFormOpenEffect>() as MistFormOpenEffect;
            if (openHelper != null)
                openHelper.EndForGood();

            // Lyrezi clan feature cleanup - InvisibilityNormal has no self-managed end hook of its
            // own the way MistFormOpenEffect does, so it's ended explicitly here. A harmless no-op if
            // it was never attached (not Lyrezi, or Mist Form ended before BecomeIncumbent ever ran).
            if (manager.FindIncumbentEffect<InvisibilityNormal>() != null)
                manager.EndIncumbentEffect<InvisibilityNormal>();

            foreach (DaggerfallActionDoor door in disabledDoors)
            {
                if (door != null)
                    SetDoorPassthrough(door, false);
            }
            disabledDoors.Clear();

            CharacterController playerController = GameManager.Instance.PlayerController;
            if (playerController != null)
            {
                foreach (CharacterController enemyController in ignoredEnemyControllers)
                {
                    if (enemyController != null)
                        Physics.IgnoreCollision(playerController, enemyController, false);
                }
            }
            ignoredEnemyControllers.Clear();

            VoxsVampirismMod.MistFormVisuals.SetActive(false);
            DaggerfallUI.AddHUDText("You knit back into solid flesh.");
        }

        const float baseLevitateMoveSpeed = 4f;

        void SetLevitating(bool state)
        {
            // PlayerMotor itself null-checked, not just the LevitateMotor lookup result - End() calls
            // this during teardown paths (scene transitions, curse cure) where the player rig can be
            // transiently unavailable, and this effect is the mod's documented round-timer-freeze
            // ground zero (see file header).
            if (GameManager.Instance.PlayerMotor == null)
                return;

            LevitateMotor levitateMotor = GameManager.Instance.PlayerMotor.GetComponent<LevitateMotor>();
            if (levitateMotor == null)
                return;

            levitateMotor.IsLevitating = state;
            levitateMotor.LevitateMoveSpeed = state ? GetHumanityScaledLevitateSpeed() : baseLevitateMoveSpeed;
        }

        static float GetHumanityScaledLevitateSpeed()
        {
            float boostPercent = 100 - (VoxsVampirismMod.VoxsVampirism.GetPlayerHumanity() * 10);
            return baseLevitateMoveSpeed * (1f + boostPercent / 100f);
        }

        // Silently attaches MistFormOpenEffect (see its own file header) if not already present, so
        // every door - dungeon or town - opens as if unlocked the moment it's actually activated. A
        // no-op once attached (FindIncumbentEffect keeps finding the same instance for the rest of
        // this toggle's duration, see MistFormOpenEffect's own header for why it never self-consumes).
        void EnsureMistFormOpenActive()
        {
            if (manager.FindIncumbentEffect<MistFormOpenEffect>() != null)
                return;

            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(MistFormOpenEffect.EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "MistFormEffect.EnsureMistFormOpenActive: Mist-Form-Open template not registered.");
                return;
            }

            // ChancePerLevel must be nonzero even though BypassChance means the roll's result is never
            // actually consulted - Open inherits SupportChance=true from its own SetProperties, and
            // BaseEntityEffect.Start() unconditionally calls SetChanceSuccess() -> RollChance() ->
            // ChanceValue() (casterLevel / settings.ChancePerLevel) regardless of that flag, which
            // divided by zero here the same way this mod's own SuppressBeast/Dominate did early on.
            EffectSettings settings = new EffectSettings
            {
                DurationBase = 1, DurationPlus = 0, DurationPerLevel = 1,
                ChanceBase = 100, ChancePlus = 0, ChancePerLevel = 1,
            };
            EffectEntry entry = new EffectEntry { Key = MistFormOpenEffect.EffectKey, Settings = settings };
            EffectBundleSettings bundleSettings = new EffectBundleSettings()
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = MistFormOpenEffect.EffectKey,
                Effects = new EffectEntry[] { entry },
            };

            EntityEffectBundle bundle = new EntityEffectBundle(bundleSettings, caster);
            manager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows | AssignBundleFlags.BypassChance);
        }

        public override void ConstantEffect()
        {
            base.ConstantEffect();

            DaggerfallEntityBehaviour entityBehaviour = GetPeeredEntityBehaviour(manager);
            if (entityBehaviour != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Transiently null during scene transitions (full mod audit) - an uncaught NRE inside a
            // recurring effect's ConstantEffect/MagicRound is the round-timer-freeze crash class
            // documented in NightWalkerSpawner.GuardedReconcile's own header (and this effect was the
            // original victim of that incident), so skip the tick rather than trust PlayerObject is
            // always available for UpdateNearbyDoors' own position read below.
            if (GameManager.Instance.PlayerObject == null)
                return;

            // Immunity to non-magical weapons - reasserted every frame, see file header.
            entityBehaviour.Entity.MinMetalToHit = WeaponMaterialTypes.Daedric;

            // Can't attack while dissolved into mist.
            WeaponManager weaponManager = GameManager.Instance.WeaponManager;
            if (weaponManager != null)
                weaponManager.Sheathed = true;

            // Can't cast anything else - see file header for why this polls rather than using
            // IsSilenced. Mist Form's own recast (to toggle off) is explicitly exempted.
            if (manager.HasReadySpell && !IsMistFormBundle(manager.ReadySpell))
            {
                manager.AbortReadySpell();
                DaggerfallUI.AddHUDText("The mist swallows the working before it can take shape.");
            }

            UpdateNearbyDoors();
        }

        static bool IsMistFormBundle(EntityEffectBundle spell)
        {
            if (spell == null || spell.Settings.Effects == null)
                return false;

            for (int i = 0; i < spell.Settings.Effects.Length; i++)
            {
                if (spell.Settings.Effects[i].Key == EffectKey)
                    return true;
            }
            return false;
        }

        // Any interior (dungeon OR building) - phasing straight through a door's collider only makes
        // sense somewhere the door is a real DaggerfallActionDoor object with a collider to disable in
        // the first place, which interiors have and open-world exteriors don't. Exterior building
        // entrances are a different system entirely (StaticDoor, no per-object collider to toggle) and
        // are instead handled by MistFormOpenEffect (see EnsureMistFormOpenActive) - open as if
        // unlocked the moment they're actually activated, same as every interior action door also gets
        // for free through that same helper.
        void UpdateNearbyDoors()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            bool insideAnyInterior = playerEnterExit != null && playerEnterExit.IsPlayerInside;
            if (!insideAnyInterior)
                return;

            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            float effectRadiusSq = doorEffectRadius * doorEffectRadius;

            for (int i = 0; i < cachedNearbyDoors.Count; i++)
            {
                DaggerfallActionDoor door = cachedNearbyDoors[i];
                if (door == null)
                    continue;

                bool inRange = (door.transform.position - playerPosition).sqrMagnitude <= effectRadiusSq;

                if (inRange && !disabledDoors.Contains(door))
                {
                    SetDoorPassthrough(door, true);
                    disabledDoors.Add(door);
                }
                else if (!inRange && disabledDoors.Contains(door))
                {
                    SetDoorPassthrough(door, false);
                    disabledDoors.Remove(door);
                }
            }
        }

        // Only ever ignores collision between the player's own CharacterController and this specific
        // door's collider(s) - the door's collider stays enabled the whole time, so every other entity
        // (enemies included) still collides with it normally. See file header for why this replaced an
        // earlier version that disabled the door's collider outright.
        static void SetDoorPassthrough(DaggerfallActionDoor door, bool ignore)
        {
            CharacterController playerController = GameManager.Instance.PlayerController;
            if (playerController == null)
                return;

            BoxCollider box = door.GetComponent<BoxCollider>();
            if (box != null)
                Physics.IgnoreCollision(playerController, box, ignore);

            MeshCollider mesh = door.GetComponent<MeshCollider>();
            if (mesh != null)
                Physics.IgnoreCollision(playerController, mesh, ignore);
        }

        void RefreshNearbyDoorCache()
        {
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            float radiusSq = doorCacheRadius * doorCacheRadius;

            cachedNearbyDoors.Clear();
            DaggerfallActionDoor[] allDoors = UnityEngine.Object.FindObjectsOfType<DaggerfallActionDoor>();
            for (int i = 0; i < allDoors.Length; i++)
            {
                DaggerfallActionDoor door = allDoors[i];
                if (door != null && (door.transform.position - playerPosition).sqrMagnitude <= radiusSq)
                    cachedNearbyDoors.Add(door);
            }
        }

        // Lets the player physically overlap enemies rather than being blocked/pushed by them - a mist
        // has no body to collide with. Scoped to actual enemy-type entities (hostile or otherwise),
        // not civilians, matching the spec's own "enemies" wording. Same civilian-free enemy scan
        // ScentOfPreyEffect already uses (ActiveGameObjectDatabase.GetActiveEnemyBehaviours() plus a
        // FindObjectsOfType fallback for anything that registration path misses - see that file's own
        // header for why the fallback exists) rather than duplicating a different approach here.
        void RefreshEnemyPassthrough()
        {
            CharacterController playerController = GameManager.Instance.PlayerController;
            if (playerController == null)
                return;

            Vector3 playerPosition = playerController.transform.position;
            float radiusSq = enemyPassthroughRadius * enemyPassthroughRadius;

            HashSet<CharacterController> nearby = new HashSet<CharacterController>();
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

                if ((behaviour.transform.position - playerPosition).sqrMagnitude > radiusSq)
                    continue;

                CharacterController enemyController = behaviour.GetComponent<CharacterController>();
                if (enemyController == null)
                    continue;

                nearby.Add(enemyController);

                if (!ignoredEnemyControllers.Contains(enemyController))
                {
                    Physics.IgnoreCollision(playerController, enemyController, true);
                    ignoredEnemyControllers.Add(enemyController);
                }
            }

            List<CharacterController> noLongerNearby = new List<CharacterController>();
            foreach (CharacterController controller in ignoredEnemyControllers)
            {
                if (controller == null || !nearby.Contains(controller))
                    noLongerNearby.Add(controller);
            }

            for (int i = 0; i < noLongerNearby.Count; i++)
            {
                CharacterController controller = noLongerNearby[i];
                if (controller != null)
                    Physics.IgnoreCollision(playerController, controller, false);
                ignoredEnemyControllers.Remove(controller);
            }
        }

        public override void MagicRound()
        {
            base.MagicRound();

            if (GetPeeredEntityBehaviour(manager) != GameManager.Instance.PlayerEntityBehaviour)
                return;

            // Same transient-null guard as ConstantEffect's own (see there) - covers
            // RefreshNearbyDoorCache/RefreshEnemyPassthrough's PlayerObject position reads and the
            // PlayerMotor lookup below. The upkeep charge further down deliberately still runs - a
            // skipped round of door-phasing costs nothing, but a skipped upkeep round would be free
            // Mist Form time.
            if (GameManager.Instance.PlayerObject != null && GameManager.Instance.PlayerMotor != null)
            {
                RefreshNearbyDoorCache();
                RefreshEnemyPassthrough();

                LevitateMotor levitateMotor = GameManager.Instance.PlayerMotor.GetComponent<LevitateMotor>();
                if (levitateMotor != null && levitateMotor.IsLevitating)
                    levitateMotor.LevitateMoveSpeed = GetHumanityScaledLevitateSpeed();
            }

            // Ongoing cost: 1 blood every 4 rounds (Blood Economy Rebalance, §5 - no refusal at 0
            // blood, the desperate-casting HP percentage is charged in its place). The real-time
            // floor is the runaway-round anomaly guard documented in the file header - when it
            // blocks, the counter deliberately is NOT reset, so the charge fires on the first round
            // after the floor clears rather than being quietly skipped.
            roundsSinceLastBloodCost++;
            if (roundsSinceLastBloodCost < roundsPerBloodCost)
                return;

            if (Time.unscaledTime - lastChargeRealTime < minRealSecondsBetweenCharges)
            {
                if (!clampLoggedSinceLastCharge)
                {
                    clampLoggedSinceLastCharge = true;
                    VoxsVampirism.Log("Mist Form upkeep: ANOMALY GUARD ENGAGED - 4 magic rounds arrived within {0:F1} real seconds of the last charge (normal is ~20s at default timescale). Something is driving game time far faster than real time; charge deferred until the floor clears.",
                        Time.unscaledTime - lastChargeRealTime);
                }
                return;
            }

            roundsSinceLastBloodCost = 0;
            lastChargeRealTime = Time.unscaledTime;
            clampLoggedSinceLastCharge = false;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(1, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0 && caster != null)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("The mist itself starts to unravel, fed on your own dying vitae.");
            }
            VoxsVampirism.Log("Mist Form upkeep: spent {0} blood, {1}% HP owed.", bloodSpent, hpPercentOwed);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "MistFormEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);

            // Long fallback duration, matching every other toggle in this mod - the real off switch is
            // the recast (or hitting max thirst), not this running out.
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
