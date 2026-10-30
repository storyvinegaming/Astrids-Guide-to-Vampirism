// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// The Legion System update: ported wholesale from Astrid's Guide to Necromancy's own identical file
// (see AllyCombatController.cs's own header for why this is a port, not a shared reference). There is
// no general "player just attacked X" event anywhere in the engine to hook into (DaggerfallUnityItem.
// OnWeaponStrike only fires for enchanted weapons). What every player attack - melee, arrow, or damage
// spell - reliably does is reduce the victim's health while the victim's own EnemySenses.Target is the
// player. So rather than patching anything, this polls health deltas at a modest rate and treats
// "health dropped, victim still alive, victim is currently targeting the player" as "the player is
// fighting this thing right now."

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

namespace VoxsVampirismMod
{
    public class PlayerAttackWatcher : MonoBehaviour
    {
        Dictionary<DaggerfallEntityBehaviour, int> lastHealth = new Dictionary<DaggerfallEntityBehaviour, int>();

        void Start()
        {
            InvokeRepeating("Poll", 0.1f, 0.1f);
        }

        void Poll()
        {
            DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
            List<DaggerfallEntityBehaviour> stillPresent = new List<DaggerfallEntityBehaviour>();

            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                if (behaviour == null || behaviour.Entity == null)
                    continue;

                stillPresent.Add(behaviour);

                EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
                if (enemyEntity != null && enemyEntity.Team == MobileTeams.PlayerAlly)
                    continue; // allies never become the focus-fire target themselves

                int currentHealth = behaviour.Entity.CurrentHealth;
                int previousHealth;
                bool hasPrevious = lastHealth.TryGetValue(behaviour, out previousHealth);

                if (hasPrevious && currentHealth < previousHealth && currentHealth > 0)
                {
                    EnemySenses senses = behaviour.GetComponent<EnemySenses>();
                    if (senses != null && senses.Target == player)
                        VoxsFocusFireBlackboard.SetFocusTarget(behaviour, false);
                }

                lastHealth[behaviour] = currentHealth;
            }

            // Prune entries for enemies no longer present so this doesn't grow unbounded.
            if (lastHealth.Count > stillPresent.Count)
            {
                List<DaggerfallEntityBehaviour> keys = new List<DaggerfallEntityBehaviour>(lastHealth.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (!stillPresent.Contains(keys[i]))
                        lastHealth.Remove(keys[i]);
                }
            }
        }
    }
}
