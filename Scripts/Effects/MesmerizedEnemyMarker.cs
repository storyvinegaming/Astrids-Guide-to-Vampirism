// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mesmerize Rework spec: attached to a human-class enemy the moment Mesmerize's own AoE pacify
// successfully calms them (MesmerizeEffect.MagicRound()'s per-candidate roll). MesmerizeGazeWatcher
// checks for this marker on activation to decide whether to open the enemy Mesmerize command menu
// (Feed/Loot/Interrogate) instead of passing the click through to vanilla - only Mesmerize's own
// pacification flags a target this way, so an enemy calmed by vanilla's own Pacify effects never
// gets the menu, matching the previous (targeted-cast) behavior exactly. Persists indefinitely once
// added (never removed on menu-open) so the same pacified enemy can be reopened across multiple
// clicks - Feed once, Loot separately, etc. - rather than the old one-shot-only flow. Mirrors
// VampireHunterMarker/DeEscalatableMarker's own single-field-tag shape.

using UnityEngine;

namespace VoxsVampirismMod.Effects
{
    public class MesmerizedEnemyMarker : MonoBehaviour
    {
    }
}
