// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Ticks down Dominate's own command duration (in standard DFU magic rounds, via
// EntityEffectBroker.OnNewMagicRound - the same real, engine-driven cadence this mod's own
// VoxsVampirismEffect.MagicRound() relies on elsewhere). On expiry, reverts Team back to whatever it
// was before domination (snapshotted at cast time in OriginalTeam) and explicitly restores IsHostile -
// the creature survives and simply reverts to hostile, it does not die.
//
// The Legion System update: Revert() is now also reachable early, via ReleaseNow - called by
// AllyCombatController.CullOldestUntilBudgetFits (through the AllyCombatController.OnReleaseRequested
// callback DominateEffect.cs wires up at cast time) when the shared legion budget shrinks and this
// happens to be the oldest active ally. Same revert either way; only the HUD message differs (natural
// expiry vs "the budget can no longer hold you").
//
// "Permanent Dominate" (Mod Setting, off by default) makes Tick() a no-op entirely - RoundsRemaining
// is still set/tracked normally by DominateEffect.cs regardless (so toggling the setting off mid-
// session resumes counting down from whatever was last assigned, rather than needing its own separate
// state), it just never actually gets decremented or checked while the setting is on. The shared
// legion budget still applies either way - ReleaseNow is a completely separate path from Tick and
// isn't gated by this setting at all, so a permanent Dominate can still be released early.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsVampirismMod.Effects
{
    public class DominateTimer : MonoBehaviour
    {
        public int RoundsRemaining;
        public MobileTeams OriginalTeam;

        // Feed-on-Dominated cooldown (ThrallManager.TryFeedOnDominated) - tracked here rather than a
        // ThrallRuntimeRecord since a Dominate target is never roster-tracked. 0 = never fed.
        public uint LastFedTime;

        void OnEnable()
        {
            EntityEffectBroker.OnNewMagicRound += Tick;
        }

        void OnDisable()
        {
            EntityEffectBroker.OnNewMagicRound -= Tick;
        }

        // The try/catch is load-bearing, not defensive fluff: an exception escaping any
        // OnNewMagicRound subscriber aborts EntityEffectBroker's round loop before its own
        // lastGameMinute marker advances, locking the whole game into one magic round per frame
        // forever - see NightWalkerSpawner.GuardedReconcile's own header for the live case.
        void Tick()
        {
            try
            {
                if (VoxsVampirismMod.VoxsVampirism.PermanentDominate)
                    return;

                RoundsRemaining--;
                if (RoundsRemaining > 0)
                    return;

                Revert(null);
            }
            catch (System.Exception ex)
            {
                Debug.LogErrorFormat("{0}DominateTimer.Tick threw ({1}: {2}) - swallowed to protect the broker's round loop.",
                    VoxsVampirismMod.VoxsVampirism.LogPrefix, ex.GetType().Name, ex.Message);
            }
        }

        // Called externally (see file header) - bypasses the RoundsRemaining countdown entirely, same
        // revert either way. message is used as-is (already built by the caller); Tick's own natural-
        // expiry path builds its own message internally instead (see Revert's own null-message branch).
        public void ReleaseNow(string message)
        {
            Revert(message);
        }

        void Revert(string overrideMessage)
        {
            DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity != null)
                enemyEntity.Team = OriginalTeam;

            EnemyMotor motor = GetComponent<EnemyMotor>();
            EnemySenses senses = GetComponent<EnemySenses>();
            if (motor != null)
                motor.IsHostile = true;
            if (senses != null)
            {
                senses.Target = null;
                senses.SecondaryTarget = null;
            }

            AllyCombatController controller = GetComponent<AllyCombatController>();
            if (controller != null)
                Destroy(controller);

            // Restores the ambient "attract" sounds DominateEffect/ThrallManager.RestoreDominatedAllies
            // silenced while this creature was bound - it's an ordinary hostile enemy again now.
            EnemySounds enemySounds = GetComponent<EnemySounds>();
            if (enemySounds != null)
                enemySounds.enabled = true;

            if (enemyEntity != null)
            {
                string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                string message = overrideMessage != null
                    ? overrideMessage
                    : string.Format("Your hold over the {0} fades - it turns on you once more.", name);
                DaggerfallUI.AddHUDText(message);
            }

            Destroy(this);
        }
    }
}
