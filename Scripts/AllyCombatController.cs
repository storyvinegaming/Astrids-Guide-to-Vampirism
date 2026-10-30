// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// The Legion System update: reuses Astrid's Guide to Necromancy's own AllyCombatController wholesale
// (pathing, engage/follow tiers, focus-fire, obstacle/fall/door avoidance, teleport-catch-up, ally-vs-
// ally collision toggle) - a direct port, not a reference (DFU compiles each mod's bundled source into
// its own separate runtime assembly with no compile-time visibility into another mod's types, even
// with both installed together, so this had to be reproduced from scratch rather than shared). This
// REPLACES the mod's own previous ThrallAllyController.cs/DominateAllyController.cs, which were each
// independent, smaller reimplementations of the same shape - one shared component now backs every kind
// of ally this mod has (thralls, created vampires, and Dominate's own bound creatures alike), exactly
// mirroring how Necromancy's own single AllyCombatController already backs both Reanimated and
// Subjugated undead with no type split.
//
// The one deliberate, requested difference from the Necromancy original: budget accounting. Necromancy
// gates its own legion size on Magicka + Mysticism; this mod's own budget (VoxsVampirism.
// GetLegionBudget()) is (10 - Humanity) * 10 + Willpower * 3 - low Humanity and high Willpower both
// grow it, matching the rest of this mod's own "beast is powerful" pattern instead of a spellcasting
// resource. Per-ally Upkeep cost is set by whichever system creates the ally (ThrallManager for
// thralls/created vampires - flat costs matching Necromancy's own Zombie/Vampire tiers respectively per
// explicit request; DominateEffect for Dominate's own targets - MaxHealth-scaled, since Dominate can
// bind any living creature and there's no fixed-tier table for that the way undead types have one).
//
// Necromancy's own CullOldestUntilAffordable/CullAlly KILLS the culled ally outright (SetHealth(0)) -
// deliberately NOT reused here. This mod's own established mechanic for "an ally stops being yours" is
// RELEASE (reverts to hostile, survives) - ThrallManager.ReleaseThrall and DominateTimer's own duration-
// expiry both already work this way, and a budget-driven cull should read the same ("it turns on you"),
// not a sudden kill. OnReleaseRequested is the seam: whichever system created a given ally sets this to
// its own release logic (ThrallManager's own ReleaseThrall for thralls/vampires, DominateTimer.ReleaseNow
// for Dominate targets) - CullOldestUntilBudgetFits below just invokes it on the oldest ally repeatedly,
// with no need to know which kind of ally it actually is.
//
// Scope decision (flagged, not settled): unlike Necromancy's own fixed-tier Reanimate spells, THIS mod's
// three ally-creation paths (the Post-Feed Offer's Create Thrall/Create Vampire, and Dominate) still
// simply REFUSE outright when the budget has no room, exactly like the old band-table cap did - none of
// them cull an EXISTING ally to force room for a new one the way Necromancy's own fixed-tier spells do.
// Forcibly ending an existing ally without being asked, just to honor a new request, felt like a bigger
// behavior change than "reuse the same follower logic" was asking for on its own.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;

namespace VoxsVampirismMod
{
    public class AllyCombatController : MonoBehaviour
    {
        // Every currently active ally of any kind - thrall, created vampire, or Dominate target -
        // registers here, oldest-first (always appended in OnEnable, only ever removed from wherever
        // it is in OnDisable - see CullOldestUntilBudgetFits' own "oldest first" assumption).
        static readonly List<AllyCombatController> activeAllies = new List<AllyCombatController>();

        public static int ActiveAllyCount => activeAllies.Count;

        // This specific ally's own upkeep cost against the shared budget - set once by whichever
        // system creates it (see file header), never recomputed afterward, so it stays fixed for this
        // ally's whole service even if the source stat it was derived from changes later.
        public int Upkeep;

        // Set by the owning system at creation time - lets CullOldestUntilBudgetFits release ANY kind
        // of ally correctly without needing to know which system owns it. Passed a short reason string
        // ("budget") purely for logging; the callback supplies its own player-facing flavor message.
        public System.Action<string> OnReleaseRequested;

        public static int TotalUpkeep()
        {
            int total = 0;
            for (int i = 0; i < activeAllies.Count; i++)
                total += activeAllies[i].Upkeep;

            return total;
        }

        public static bool CanAfford(int additionalCost)
        {
            return TotalUpkeep() + additionalCost <= VoxsVampirism.GetLegionBudget();
        }

        public static string DescribeBudget()
        {
            return string.Format("{0}/{1}", TotalUpkeep(), VoxsVampirism.GetLegionBudget());
        }

        // Called whenever the shared budget itself might have shrunk (VoxsVampirismEffect.
        // ChangeHumanity, on every Humanity change in either direction - Willpower's own constant
        // fluctuation with Blood Points is deliberately NOT hooked reactively here, same scope
        // boundary Necromancy's own Magicka-budget system already has: a shrinking budget from THAT
        // side is only enforced at the next new-ally attempt's own CanAfford check, not proactively
        // monitored for already-active allies). Releases the oldest ally, repeatedly, until total
        // upkeep fits again - a no-op loop if it already does.
        public static void CullOldestUntilBudgetFits()
        {
            while (TotalUpkeep() > VoxsVampirism.GetLegionBudget() && activeAllies.Count > 0)
            {
                AllyCombatController oldest = activeAllies[0];
                if (oldest.OnReleaseRequested == null)
                {
                    VoxsVampirism.Log("AllyCombatController: budget cull could not release the oldest ally (no OnReleaseRequested handler) - stopping to avoid an infinite loop.");
                    break;
                }

                oldest.OnReleaseRequested("budget");

                // Bug fix (full mod audit): every real OnReleaseRequested handler (ThrallManager.
                // ReleaseThrall, DominateTimer.Revert) releases this ally via Destroy(this component) -
                // but Unity's Destroy() only QUEUES destruction for end-of-frame, it does not fire
                // OnDisable() (and therefore does not run activeAllies.Remove(this)) synchronously. The
                // previous version of this loop re-checked "is activeAllies[0] still oldest?" expecting
                // that removal to have already happened - it never had, on any call, so this always
                // logged a false "could not release" error and always stopped after exactly one ally
                // even when several needed to go to fit a big Humanity swing. The handler having a
                // callback at all IS the contract that this ally WILL be released, so removed directly
                // here instead of waiting on OnDisable - safe even once OnDisable does eventually fire
                // later this frame, since List.Remove on an already-absent item is a harmless no-op.
                activeAllies.Remove(oldest);
            }
        }

        EnemyMotor enemyMotor;
        EnemySenses enemySenses;
        DaggerfallEntityBehaviour entityBehaviour;
        CharacterController controller;
        MobileUnit mobile;

        float originalHeight;
        bool obstacleDetected;
        bool fallDetected;
        bool foundUpwardSlope;
        bool foundDoor;
        Vector3 destination;

        const float stopDistance = 4f;
        const float turnSpeed = 20f;
        const float teleportDistance = 20f;

        int ignoreMask;

        // ---- Engagement tiers ---------------------------------------------------------------------
        //
        // Plain byte constants, not an enum - this mod's own hard rule against custom enum types
        // (a nested enum once broke the whole mod's own reflection-based loading). currentTier is
        // diagnostic state, not itself a control-flow switch - FixedUpdate's own priority chain is
        // what actually decides behaviour each tick.
        const byte TierEngage = 0;
        const byte TierFollow = 1;
        byte currentTier = TierFollow;

        // Diagnostic only - logs every senses.Target change with enough context (LOS, GiveUpTimer,
        // tier) to see exactly how a stuck-on-a-wall commitment happened, if one still does.
        DaggerfallEntityBehaviour lastLoggedTarget;

        // Tier 1 (ENGAGE) is always on, dungeons and overland - a hostile in the same room does not
        // need a spell to be worth fighting. Scanned on a short interval rather than every tick since
        // it walks the whole active-enemy list.
        const float engageRadius = 20f;
        const float engageScanInterval = 0.15f;
        float nextEngageScanTime;

        // Player-centric eligibility gate: a hostile must either be close enough to the player to
        // matter, or already attacking the player right now (an archer across a room still counts
        // regardless of distance) - distance-to-ally alone says nothing about whether a target is a
        // real threat to the PLAYER specifically.
        const float engagePlayerRadius = 15f;

        void Awake()
        {
            enemyMotor = GetComponent<EnemyMotor>();
            enemySenses = GetComponent<EnemySenses>();
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            controller = GetComponent<CharacterController>();
            mobile = GetComponentInChildren<MobileUnit>();
            originalHeight = controller.height;
            ignoreMask = ~(1 << LayerMask.NameToLayer("SpellMissiles") | 1 << LayerMask.NameToLayer("Ignore Raycast"));
        }

        // Lets the player walk straight through an ally instead of getting physically blocked by it in
        // a doorway/hallway - CharacterController IS a Collider in Unity, so this is a normal pairwise
        // ignore, not a layer-wide change. Unconditional - the player should always be able to walk
        // through their own court.
        //
        // Ally-vs-ally collision is a separate, opt-in case (VoxsVampirism.AlliesIgnoreEachOtherCollision,
        // off by default): normally a court physically jostles for space like any other pack of
        // creatures; enabling the setting extends the same pass-through treatment pairwise to every
        // other active ally, so they never jam up a corridor.
        void OnEnable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, true);

            if (VoxsVampirism.AlliesIgnoreEachOtherCollision)
                SetIgnoreCollisionWithOtherAllies(true);

            activeAllies.Add(this);
        }

        void OnDisable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, false);

            // Distinguishes a genuine combat death from every OTHER reason this component goes away
            // (a budget/Humanity release, Dominate's own natural expiry, a scene-transition teardown
            // ahead of MaterializeAll's own destroy-and-respawn) - only a real death gets this message;
            // the others already show their own distinct flavor text elsewhere (ReleaseThrall,
            // DominateTimer.Revert) and would be misleading duplicated here.
            bool died = entityBehaviour == null || entityBehaviour.Entity == null || entityBehaviour.Entity.CurrentHealth <= 0;

            activeAllies.Remove(this);

            if (VoxsVampirism.AlliesIgnoreEachOtherCollision)
                SetIgnoreCollisionWithOtherAllies(false);

            if (died)
                DaggerfallUI.AddHUDText(string.Format("Your court numbers {0}. Upkeep: {1}.", ActiveAllyCount, DescribeBudget()));
        }

        void SetIgnoreCollisionWithOtherAllies(bool ignore)
        {
            if (controller == null || !controller.enabled || !controller.gameObject.activeInHierarchy)
                return;

            foreach (AllyCombatController other in activeAllies)
            {
                // The enabled/active guards matter now that this is also called per-tick from
                // EnsurePlayerCollisionIgnored - IgnoreCollision logs an error for inactive colliders.
                if (other == this || other.controller == null || !other.controller.enabled || !other.controller.gameObject.activeInHierarchy)
                    continue;

                Physics.IgnoreCollision(controller, other.controller, ignore);
            }
        }

        // Physics.IgnoreCollision pairings are NOT persistent: Unity silently drops a pair the moment
        // either collider (or its GameObject) is deactivated, and a save load does exactly that to
        // the player object partway through its own sequence - so the one-shot pairing OnEnable set
        // during the load window kept getting wiped by the time the world was actually playable,
        // and both thralls and dominated creatures came back solid after every load (confirmed; and
        // no "Ignore collision failed" errors in the log, meaning the calls applied fine and were
        // wiped afterward, not refused). Reasserted every physics tick instead - the same last-write-
        // wins-per-frame philosophy Mist Form's ConstantEffect already uses for MinMetalToHit - with
        // the active/enabled guards IgnoreCollision itself demands (it refuses, with a logged error,
        // when either collider is inactive). Cost is one PhysX pair-flag write per ally per tick -
        // negligible at court scale.
        void EnsurePlayerCollisionIgnored()
        {
            CharacterController playerController = GameManager.Instance.PlayerController;
            if (controller == null || playerController == null)
                return;
            if (!controller.enabled || !playerController.enabled)
                return;
            if (!controller.gameObject.activeInHierarchy || !playerController.gameObject.activeInHierarchy)
                return;

            Physics.IgnoreCollision(controller, playerController, true);

            if (VoxsVampirism.AlliesIgnoreEachOtherCollision)
                SetIgnoreCollisionWithOtherAllies(true);
        }

        void FixedUpdate()
        {
            EnsurePlayerCollisionIgnored();

            ClearAllyTargeting();

            if (enemySenses.Target != lastLoggedTarget)
            {
                lastLoggedTarget = enemySenses.Target;
                if (lastLoggedTarget == null)
                {
                    VoxsVampirism.Log("AllyCombatController: target cleared - ally={0}, time={1:F2}.", GetInstanceID(), Time.time);
                }
                else
                {
                    bool los = HasLineOfSight(transform.position, lastLoggedTarget.transform.position);
                    VoxsVampirism.Log("AllyCombatController: target changed to '{0}' - ally={1}, LOS={2}, GiveUpTimer={3}, tier={4}, time={5:F2}.",
                        lastLoggedTarget.name, GetInstanceID(), los, enemyMotor.GiveUpTimer, currentTier, Time.time);
                }
            }

            // Committed via the SAME condition EnemyMotor's own HandleNoAction() uses to decide
            // whether IT is still actively pursuing (Target set, GiveUpTimer > 0) - not a raw, per-tick
            // line-of-sight flicker, which has no hysteresis of its own and would abandon/reacquire
            // constantly right at doorway/corner boundaries.
            if (enemySenses.Target != null && enemyMotor.GiveUpTimer > 0)
            {
                DaggerfallEntityBehaviour committedTarget = enemySenses.Target;
                DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
                EnemySenses committedTargetSenses = committedTarget.GetComponent<EnemySenses>();
                bool committedAttackingPlayer = committedTargetSenses != null && committedTargetSenses.Target == player;
                bool committedNearPlayer = (committedTarget.transform.position - player.transform.position).sqrMagnitude
                    <= engagePlayerRadius * engagePlayerRadius;

                if ((committedAttackingPlayer || committedNearPlayer)
                    && HasLineOfSight(transform.position, committedTarget.transform.position))
                {
                    currentTier = TierEngage;
                    return;
                }

                // Not a real threat to the player (or not visible to THIS ally) - clear vanilla's own
                // acquisition rather than trust it, so the engage scan / follow logic below get a
                // chance to run instead of standing still or walking into a wall forever.
                enemySenses.Target = null;
                enemySenses.SecondaryTarget = null;
            }

            // Tier 1 - ENGAGE: nothing currently committed. Look for a hostile within close range and
            // nudge onto it via the same soft idiom the focus-fire case already uses - always on,
            // dungeons and overland.
            if (Time.time >= nextEngageScanTime)
            {
                nextEngageScanTime = Time.time + engageScanInterval;

                DaggerfallEntityBehaviour engageTarget = FindEngageTarget();
                if (engageTarget != null)
                {
                    currentTier = TierEngage;
                    enemyMotor.MakeEnemyHostileToAttacker(engageTarget);
                    return;
                }
            }

            // Tier 2 - FOLLOW.
            currentTier = TierFollow;
            FollowPlayer();
        }

        // VoxsFocusFireBlackboard holds whatever the player's own attack just landed on (set by
        // PlayerAttackWatcher) - a single MakeEnemyHostileToAttacker() nudge per LateUpdate. It sets
        // Target once, but EnemySenses' own periodic re-acquisition scan is free to overwrite it again
        // right after, which is exactly what we want: allies should keep responding to whatever is
        // actually attacking them or the player right now, not stay welded to whatever the player hit
        // a few seconds ago.
        void LateUpdate()
        {
            ClearAllyTargeting();

            DaggerfallEntityBehaviour focusTarget = VoxsFocusFireBlackboard.GetValidTarget();
            if (focusTarget == null || focusTarget == entityBehaviour || focusTarget == GameManager.Instance.PlayerEntityBehaviour)
                return;

            enemyMotor.MakeEnemyHostileToAttacker(focusTarget);
        }

        // Two distinct engine-side ways an ally's Target/SecondaryTarget can end up pointing at
        // another ally, neither of which can be patched directly (base engine code, no Harmony here):
        // vanilla retaliation has no team check at all, and EnemySenses' own periodic re-acquisition
        // only excludes same-team candidates when EnemyInfighting is on (off by default). Clearing an
        // already-set ally Target is always safe - nothing here re-derives one from scratch afterward.
        void ClearAllyTargeting()
        {
            if (IsAlly(enemySenses.Target))
                enemySenses.Target = null;

            if (IsAlly(enemySenses.SecondaryTarget))
                enemySenses.SecondaryTarget = null;
        }

        static bool IsAlly(DaggerfallEntityBehaviour target)
        {
            return target != null && target.Entity != null && target.Entity.Team == MobileTeams.PlayerAlly;
        }

        void FollowPlayer()
        {
            Transform playerTransform = GameManager.Instance.PlayerEntityBehaviour.transform;
            float moveSpeed = ((entityBehaviour.Entity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale) * 1.5f;

            destination = playerTransform.position;

            Vector3 direction = (destination - transform.position).normalized;
            float distance = (destination - transform.position).magnitude;

            if (distance >= teleportDistance)
            {
                TeleportToPlayer(playerTransform);
                return;
            }

            if (distance >= stopDistance)
                AttemptMove(direction, moveSpeed);
            else if (!enemySenses.TargetIsWithinYawAngle(22.5f, destination))
                TurnToTarget(direction);
        }

        // Catches an ally that's fallen too far behind to ever catch up by walking (stuck on geometry,
        // left behind by fast travel/mounted movement, lost through a door the player closed) - only
        // reachable from FollowPlayer(), so it never interrupts active combat. CharacterController must
        // be disabled while moving the transform directly, or its own collision resolution can fight
        // the position change or ignore it outright.
        void TeleportToPlayer(Transform playerTransform)
        {
            Vector3 offset = Random.insideUnitSphere * 3f;
            offset.y = 0f;
            Vector3 newPosition = playerTransform.position + offset;

            controller.enabled = false;
            transform.position = newPosition;
            controller.enabled = true;
        }

        // Called by Command Blood Court - instantly gathers every currently active ally to the
        // player's side, wherever they currently are. Unconditional: no LOS/combat-state filter, same
        // as Necromancy's own original (an earlier LOS-based filter there caused more problems than it
        // solved - a stuck ally's own LOS to its target flickers true/false right at doorway/corner
        // boundaries, which misclassified a stuck ally as "fighting" and skipped it).
        public static void TeleportAllToPlayer()
        {
            DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
            if (player == null)
                return;

            for (int i = 0; i < activeAllies.Count; i++)
            {
                AllyCombatController ally = activeAllies[i];
                if (ally == null)
                    continue;

                ally.enemySenses.Target = null;
                ally.enemySenses.SecondaryTarget = null;
                ally.TeleportToPlayer(player.transform);
            }
        }

        void AttemptMove(Vector3 direction, float moveSpeed)
        {
            if (!enemySenses.TargetIsWithinYawAngle(5.625f, destination))
                TurnToTarget(direction);

            if (!CanFly() && !enemyMotor.IsLevitating && controller.isGrounded)
                direction.y = -2f;

            Vector3 motion = direction * moveSpeed;

            Vector3 direction2d = direction;
            if (!CanFly() && !enemyMotor.IsLevitating)
                direction2d.y = 0;

            ObstacleCheck(direction2d);
            FallCheck(direction2d);

            if (fallDetected || obstacleDetected)
                transform.position = Vector3.MoveTowards(transform.position, destination, moveSpeed * Time.deltaTime * 0.5f);
            else
                controller.Move(motion * Time.deltaTime);
        }

        void TurnToTarget(Vector3 targetDirection)
        {
            transform.forward = Vector3.RotateTowards(transform.forward, targetDirection, turnSpeed * Mathf.Deg2Rad, 0.0f);
        }

        bool CanFly()
        {
            return mobile.Enemy.Behaviour == MobileBehaviour.Flying || mobile.Enemy.Behaviour == MobileBehaviour.Spectral;
        }

        void FallCheck(Vector3 direction)
        {
            if (CanFly() || enemyMotor.IsLevitating || obstacleDetected || foundUpwardSlope || foundDoor)
            {
                fallDetected = false;
                return;
            }

            Vector3 rayOrigin = transform.position + direction;
            Ray ray = new Ray(rayOrigin, Vector3.down);
            RaycastHit hit;
            fallDetected = !Physics.Raycast(ray, out hit, (originalHeight * 0.5f) + 1.5f);
        }

        void ObstacleCheck(Vector3 direction)
        {
            obstacleDetected = false;
            foundUpwardSlope = false;
            foundDoor = false;
            float checkDistance = controller.radius / Mathf.Sqrt(2f);

            Vector3 p1 = transform.position + (Vector3.up * -originalHeight * 0.1388f);
            Vector3 p2 = p1 + (Vector3.up * originalHeight * 0.5f);

            RaycastHit hit;
            if (Physics.CapsuleCast(p1, p2, controller.radius / 2, direction, out hit, checkDistance, ignoreMask))
            {
                obstacleDetected = true;

                DaggerfallEntityBehaviour hitEntity = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
                DaggerfallActionDoor door = hit.transform.GetComponent<DaggerfallActionDoor>();

                if (hitEntity != null)
                {
                    if (hitEntity == GameManager.Instance.PlayerEntityBehaviour)
                        obstacleDetected = false;
                }
                else if (door != null)
                {
                    obstacleDetected = false;
                    foundDoor = true;
                    if (enemySenses.TargetIsWithinYawAngle(22.5f, door.transform.position))
                    {
                        enemySenses.LastKnownDoor = door;
                        enemySenses.DistanceToDoor = Vector3.Distance(transform.position, door.transform.position);
                    }
                }
                else if (!CanFly() && !enemyMotor.IsLevitating)
                {
                    Vector3 checkUp = transform.position + direction;
                    checkUp.y++;
                    Vector3 upDirection = (checkUp - transform.position).normalized;
                    Vector3 up1 = transform.position + (Vector3.up * -originalHeight * 0.25f);
                    Vector3 up2 = up1 + (Vector3.up * originalHeight * 0.75f);

                    if (!Physics.CapsuleCast(up1, up2, controller.radius / 2, upDirection, checkDistance))
                    {
                        obstacleDetected = false;
                        foundUpwardSlope = true;
                    }
                }
            }
        }

        static bool IsAliveHostile(DaggerfallEntityBehaviour behaviour)
        {
            if (behaviour == null || behaviour.Entity == null || behaviour.Entity.CurrentHealth <= 0)
                return false;

            EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
            return enemyEntity != null && enemyEntity.Team != MobileTeams.PlayerAlly;
        }

        static List<DaggerfallEntityBehaviour> ScanAliveHostiles()
        {
            List<DaggerfallEntityBehaviour> result = new List<DaggerfallEntityBehaviour>();
            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                if (IsAliveHostile(behaviour))
                    result.Add(behaviour);
            }

            return result;
        }

        // Solid dungeon/building architecture - the same "Default" layer blocking-geometry checks
        // elsewhere in DFU already treat as solid.
        static readonly int engageLineOfSightMask = 1 << LayerMask.NameToLayer("Default");

        // A single ray at one fixed eye-height can clip a doorframe/lintel even when the doorway is
        // genuinely open - several plausible sample heights rather than one exact measurement, so a
        // doorway only has to be clear at one of them.
        static readonly float[] engageEyeHeightFractions = { 0.5f, 0.75f, 0.9f };

        // Tier 1 must not engage a hostile through a wall just because it's within straight-line range
        // - a hostile in the next room can be well within engageRadius. Without this, committing to a
        // target the ally can approach but never truly reach or lose means it never re-uncommits, so it
        // never resumes following.
        bool HasLineOfSight(Vector3 selfBase, Vector3 targetBase)
        {
            for (int i = 0; i < engageEyeHeightFractions.Length; i++)
            {
                Vector3 fromEye = selfBase + Vector3.up * (originalHeight * engageEyeHeightFractions[i]);
                Vector3 toEye = targetBase + Vector3.up * (originalHeight * engageEyeHeightFractions[i]);
                if (!Physics.Linecast(fromEye, toEye, engageLineOfSightMask, QueryTriggerInteraction.Ignore))
                    return true;
            }

            return false;
        }

        // Tier 1 target selection, in priority order: (1) hostiles whose OWN senses.Target is the
        // player - actively attacking the player right now, (2) hostiles nearest the player, (3)
        // hostiles nearest this ally. All three categories are drawn only from hostiles within
        // engageRadius of THIS ally that ALSO either threaten the player (within engagePlayerRadius of
        // them, or already attacking them) AND have a clear line of sight to this ally.
        DaggerfallEntityBehaviour FindEngageTarget()
        {
            List<DaggerfallEntityBehaviour> hostiles = ScanAliveHostiles();
            DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
            Vector3 playerPosition = player.transform.position;
            float engageRadiusSq = engageRadius * engageRadius;
            float engagePlayerRadiusSq = engagePlayerRadius * engagePlayerRadius;

            DaggerfallEntityBehaviour attackingPlayer = null;
            float attackingPlayerSelfDistSq = float.MaxValue;
            DaggerfallEntityBehaviour nearestToPlayer = null;
            float nearestToPlayerDistSq = float.MaxValue;
            DaggerfallEntityBehaviour nearestToSelf = null;
            float nearestToSelfDistSq = float.MaxValue;

            for (int i = 0; i < hostiles.Count; i++)
            {
                DaggerfallEntityBehaviour hostile = hostiles[i];
                float selfDistSq = (hostile.transform.position - transform.position).sqrMagnitude;
                if (selfDistSq > engageRadiusSq)
                    continue;

                EnemySenses hostileSenses = hostile.GetComponent<EnemySenses>();
                bool attackingPlayerNow = hostileSenses != null && hostileSenses.Target == player;
                float playerDistSq = (hostile.transform.position - playerPosition).sqrMagnitude;

                if (!attackingPlayerNow && playerDistSq > engagePlayerRadiusSq)
                    continue;

                if (!HasLineOfSight(transform.position, hostile.transform.position))
                    continue;

                if (attackingPlayerNow && selfDistSq < attackingPlayerSelfDistSq)
                {
                    attackingPlayerSelfDistSq = selfDistSq;
                    attackingPlayer = hostile;
                }

                if (playerDistSq < nearestToPlayerDistSq)
                {
                    nearestToPlayerDistSq = playerDistSq;
                    nearestToPlayer = hostile;
                }

                if (selfDistSq < nearestToSelfDistSq)
                {
                    nearestToSelfDistSq = selfDistSq;
                    nearestToSelf = hostile;
                }
            }

            if (attackingPlayer != null)
                return attackingPlayer;
            if (nearestToPlayer != null)
                return nearestToPlayer;

            return nearestToSelf;
        }
    }
}
