// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Narrative Feeding Scenes spec (hunter_arrives): attached to a spawned vampire hunter enemy at
// creation. A hunter is spawned as an ordinary MobileTypes.Knight/Mage (EntityTypes.EnemyClass), the
// same career types the mortal fights-back spawns already use - which means it is NOT structurally
// excluded from Mesmerize's own AoE pacify the way a Vampire/VampireAncient is (that exclusion falls
// out of EntityType==EnemyMonster, which a class-based human never matches). This marker is what
// MesmerizeEffect.RunAoePacify() checks instead - "their training runs deeper than any charm you
// carry," a hunter who came prepared and trained to resist exactly this. Mirrors DeEscalatableMarker's
// own single-field-tag shape.

using UnityEngine;

namespace VoxsVampirismMod.Effects
{
    public class VampireHunterMarker : MonoBehaviour
    {
    }
}
