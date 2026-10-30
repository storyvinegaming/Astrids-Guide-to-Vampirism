// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Internal helper for Mist Form (MistFormEffect) - never granted to the player, never appears in the
// spellbook or crafting stations, just silently attached to the player's own EntityEffectManager for
// as long as Mist Form is active. Exists because vanilla's own PlayerActivate has exactly one public
// hook for "treat a locked door as unlocked": both HandleOpenEffect (interior DaggerfallActionDoor,
// PlayerActivate.cs) and HandleOpenEffectOnExteriorDoor (exterior building doors, whose lock state is
// computed fresh at activation time from building quality/reputation with no persistent field this mod
// could otherwise zero out the way CurrentLockValue works for action doors) both look up
// GameManager.Instance.PlayerEffectManager.FindIncumbentEffect<Open>() and, if one exists, call
// straight into it (TriggerOpenEffect/TriggerExteriorOpenEffect) to bypass the lock check entirely.
// EntityEffectManager.FindIncumbentEffect<T>() matches by "effect is T", not by effect Key, so a
// subclass of vanilla's own Open effect is found by that lookup exactly the same as a real one would
// be - this is the ONLY way to get an exterior building door to open on activation without
// reimplementing PlayerActivate's own building-entry logic (quest hooks, faction/crime handling,
// shop/tavern/residence flags, etc.) from scratch.
//
// Real Open is single-use by design: TriggerOpenEffect/TriggerExteriorOpenEffect both end by calling
// CancelEffect() (forcedRoundsRemaining = 0, ResignAsIncumbent()), consuming the spell the moment any
// one door is activated. Mist Form wants this to work on every door for its whole duration, not just
// the first one, so CancelEffect() is overridden to swallow that self-consumption unconditionally
// unless MistFormEffect explicitly calls EndForGood() when the toggle itself turns off. StartWaitingForDoor
// is overridden to skip Open's own "Ready to open." HUD message and RollChance() gate entirely - Mist
// Form already has its own flat Magicka cost and per-round thirst drain paying for this, Open's own
// independent cost/chance system would be redundant and would spam a message this mod never asked for.
//
// TriggerOpenEffect/TriggerExteriorOpenEffect are also both overridden outright, not just their
// self-consumption - real Open only unlocks a door whose lock value is at or below the caster's own
// level ("unlocks to lock-level of caster", straight from the classic spell description), showing
// "That lock is too powerful for this spell to have any effect" otherwise. Mist Form has no such
// gate in its own spec (a level 9 lock on an ordinary player still got refused this way in testing) -
// these overrides replicate the actual door-opening steps without the level check, and skip the
// original's own CancelEffect() call at the end entirely (superseding the swallow-via-override above).

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;

namespace VoxsVampirismMod.Effects
{
    public class MistFormOpenEffect : Open
    {
        public static readonly string EffectKey = "Mist-Form-Open";

        bool reallyCancel;

        public override void SetProperties()
        {
            base.SetProperties();
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
        }

        public override string GroupName => EffectKey;

        protected override void StartWaitingForDoor()
        {
            // Deliberately empty - see file header. No message, no chance roll, always ready.
        }

        public override void TriggerOpenEffect(DaggerfallActionDoor actionDoor)
        {
            if (actionDoor.IsLocked)
                actionDoor.CurrentLockValue = 0;

            if (!actionDoor.IsLocked && actionDoor.IsClosed)
                actionDoor.ToggleDoor(manager.EntityBehaviour == GameManager.Instance.PlayerEntityBehaviour);
        }

        public override bool TriggerExteriorOpenEffect(int buildingLockValue)
        {
            return true;
        }

        public override void CancelEffect()
        {
            if (reallyCancel)
                base.CancelEffect();
        }

        /// <summary>The real cancel - called only by MistFormEffect.End() when the toggle turns off.</summary>
        public void EndForGood()
        {
            reallyCancel = true;
            CancelEffect();
        }
    }
}
