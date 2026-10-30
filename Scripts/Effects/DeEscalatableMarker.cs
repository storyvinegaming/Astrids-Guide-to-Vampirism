// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Marks an enemy GameObject as eligible for Mesmerize's own "de-escalate" outcome - a successful
// pacify (MesmerizeEffect.MagicRound) on a marked target disengages AND despawns it, with distinct
// flavor text, rather than just leaving it standing there peacefully pacified (Mesmerize's normal,
// unmarked-target behavior - see MesmerizeEffect.cs's own header for why only marked targets get the
// stronger treatment). Attached to:
//   - A "victim fights back" spawn (SneakFeedController.TryTriggerFightsBack) for the mortal variants
//     only (Thief/Knight/Mage) - IsFightsBackSpawn = true. The vampire/vampire-ancient variants are
//     never marked with this at all, since MesmerizeEffect's own eligibility check (EntityType==
//     EnemyClass, or EnemyMonster whose EnemyGroup is Humanoid) already excludes them structurally -
//     no separate check needed here for that exclusion.
//   - A freed thrall (ThrallManager, when a thrall breaks free over-cap or on a failed feed-safety
//     roll) - IsFightsBackSpawn = false. A freed mortal thrall (EntityType==EnemyClass) is still
//     eligible for the same reason above; a freed CREATED VAMPIRE is not eligible for the same
//     structural reason the fights-back vampire variants aren't - Mesmerize simply never succeeds
//     against it, so this marker is harmless (never checked) on that specific case.
//
// IsFightsBackSpawn only changes the flavor text used on de-escalation (SneakFeedController's own
// "the mortal was ready for you" framing vs. a plain "your former thrall wanders off" framing) - it
// does not change eligibility, which is entirely governed by MesmerizeEffect's own target-type check.

using UnityEngine;

namespace VoxsVampirismMod.Effects
{
    public class DeEscalatableMarker : MonoBehaviour
    {
        public bool IsFightsBackSpawn;
    }
}
