// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// The Legion System update: ported wholesale from Astrid's Guide to Necromancy's own identical file
// (see AllyCombatController.cs's own header for why this is a port, not a shared reference). A single
// shared "what is the player fighting right now" value, rather than each ally independently re-deriving
// it. Self-cleaning: once the target dies, is gone, or has itself become an ally (Team ==
// PlayerAlly - e.g. a Dominate cast landed on it), it's forgotten automatically so allies fall back to
// their own senses instead of chasing a stale target.
//
// hardLock exists in the ported API surface but has no caller in this mod - Necromancy's own
// CommandUndeadEffect sets it true for a deliberate hard-focus command, and this mod has no equivalent
// spell (Command Blood Court is a pure teleport, not a focus command). Kept rather than trimmed so the
// ported shape matches the original exactly; PlayerAttackWatcher (the only caller here) always passes
// false.

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;

namespace VoxsVampirismMod
{
    public static class VoxsFocusFireBlackboard
    {
        static DaggerfallEntityBehaviour focusTarget;
        static bool hardLock;

        public static void SetFocusTarget(DaggerfallEntityBehaviour victim, bool hardLock)
        {
            focusTarget = victim;
            VoxsFocusFireBlackboard.hardLock = hardLock;
        }

        public static bool IsHardLock => hardLock;

        public static DaggerfallEntityBehaviour GetValidTarget()
        {
            if (focusTarget == null || focusTarget.gameObject == null || !focusTarget.gameObject.activeInHierarchy
                || focusTarget.Entity == null || focusTarget.Entity.CurrentHealth <= 0
                || focusTarget.Entity.Team == MobileTeams.PlayerAlly)
            {
                focusTarget = null;
                hardLock = false;
                return null;
            }

            return focusTarget;
        }
    }
}
