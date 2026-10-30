// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mesmerize Rework spec: redirects the player's own normal activate click (InputManager.Actions.
// ActivateCenterObject - the vanilla "talk"/"use" button, not a dedicated keybind) to the Mesmerize
// command menu when it hits an eligible target, in place of vanilla's own dialogue/no-op. No hook
// exists anywhere in DFU to intercept or cancel PlayerActivate's own dispatch (confirmed by reading
// PlayerActivate.cs directly - ActivateStaticNPC/ActivateMobileNPC run unconditionally, synchronously,
// with no cancellable event).
//
// An earlier version of this file tried to win a race against PlayerActivate's own Update() using
// [DefaultExecutionOrder(-1000)], disabling PlayerActivate for one frame when it won. Confirmed
// unreliable by live testing (intermittent - "works sometimes, other times it doesn't"), almost
// certainly because DFU mods are compiled and loaded as their own runtime assembly, and Unity's script
// execution order system does not reliably honor [DefaultExecutionOrder] on types outside the
// original build-time compiled assemblies - so the two Update() calls were racing with no real
// guarantee either way.
//
// The fix depends on no execution ordering at all - two different techniques for two different
// situations, split by whether vanilla actually opens a window worth reacting to:
//   - StaticNPC/MobilePersonNPC (real townspeople - activating one genuinely opens a
//     DaggerfallTalkWindow in vanilla): react in LateUpdate(), a hard Unity per-frame PHASE guarantee
//     - every script's Update() across the whole scene, mod-loaded or not, always finishes before any
//     script's LateUpdate() runs, enforced by the engine's own frame loop rather than by per-script
//     ordering. If a DaggerfallTalkWindow just appeared on top of the UI stack THIS frame and the
//     caster's gaze is primed, the talk window is popped and the Mesmerize menu takes its place
//     instead - before the frame is ever rendered, so vanilla dialogue is never actually seen.
//   - WoD-gated person billboards (never opens ANY vanilla window - "even vanilla can't talk to
//     them," per SneakFeedController's own header) and MesmerizedEnemyMarker-tagged enemies (vanilla
//     has no special activate-handling for a living enemy GameObject at all): nothing to preempt, so
//     opening the menu here is purely additive - a plain Update()-driven raycast is enough, no race
//     to win.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod.Effects;

namespace VoxsVampirismMod
{
    public class MesmerizeGazeWatcher : MonoBehaviour
    {
        // Same Player-layer exclusion PlayerActivate/SneakFeedController's own raycasts already use -
        // without it the ray can self-hit the player's own capsule at point-blank range.
        static readonly int raycastLayerMask = ~(1 << LayerMask.NameToLayer("Player"));

        bool talkWindowWasOpenLastFrame;

        void Update()
        {
            if (!GameManager.Instance.IsPlayerOnHUD || InputManager.Instance.IsPaused)
                return;

            if (!InputManager.Instance.ActionComplete(InputManager.Actions.ActivateCenterObject))
                return;

            if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                return;

            Camera mainCamera = GameManager.Instance.MainCamera;
            if (!mainCamera)
                return;

            Ray ray = new Ray(mainCamera.transform.position, mainCamera.transform.forward);
            RaycastHit hit;
            float maxDistance = Mathf.Max(PlayerActivate.StaticNPCActivationDistance, PlayerActivate.DefaultActivationDistance);
            if (!Physics.Raycast(ray, out hit, maxDistance, raycastLayerMask))
                return;

            if (TryRedirectWorldOfDaggerfallBillboard(hit))
                return;

            TryRedirectPacifiedEnemy(hit);
        }

        // WoD person billboards never open any vanilla window on activation (they aren't wired into
        // TalkManager at all), so there's nothing for LateUpdate's own swap technique to react to -
        // this has to be additive instead, gated on gaze-priming the same way the LateUpdate path is.
        static bool TryRedirectWorldOfDaggerfallBillboard(RaycastHit hit)
        {
            if (!VoxsVampirism.WorldOfDaggerfallInstalled)
                return false;

            DaggerfallBillboard billboard = hit.transform.GetComponent<DaggerfallBillboard>();
            if (billboard == null || !SneakFeedController.IsWorldOfDaggerfallPersonBillboard(billboard))
                return false;
            if (hit.distance > PlayerActivate.StaticNPCActivationDistance)
                return false;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null || !vampirism.IsGazePrimed)
                return false;

            vampirism.ConsumeGaze();
            VoxsVampirism.Log("MesmerizeGazeWatcher: primed gaze consumed on WoD billboard '{0}' - opening townsperson menu.", billboard.name);
            MesmerizeCommandMenu.ShowForTownsperson(billboard.gameObject);
            return true;
        }

        static void TryRedirectPacifiedEnemy(RaycastHit hit)
        {
            if (hit.distance > PlayerActivate.DefaultActivationDistance)
                return;

            DaggerfallEntityBehaviour entityBehaviour = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
            if (entityBehaviour == null || entityBehaviour.GetComponent<MesmerizedEnemyMarker>() == null)
                return;

            // Bug fix (full mod audit): MesmerizedEnemyMarker persists indefinitely once added (see
            // that file's own header) - if the same target was later separately bound via Dominate, it
            // still carries the marker, and nothing here checked for that. Left unguarded, activating
            // it opened MesmerizeCommandMenu's own enemy menu, whose Feed option is gated on
            // SneakFeedController's SneakFeedCooldownMarker - a completely SEPARATE 24h cooldown store
            // from DominateTimer.LastFedTime, which is what ThrallManager.TryFeedOnDominated (reached
            // via the Sneak Feed keybind, the ONE normal way to feed on a Dominated ally) actually
            // stamps. That let a Dominated creature be fed on twice per day - once through each key -
            // doubling blood income from a single bound ally. A DominateTimer's presence means this
            // target is now a bound ally with its own dedicated feed path and cooldown, so this menu
            // (and its differently-cooled Feed option) no longer applies to it at all - skipped
            // entirely, same as any other target this watcher was never meant to react to; the click
            // falls through to vanilla untouched, exactly matching the Sneak Feed keybind's own
            // priority order (SneakFeedController.TryFindTarget checks DominateTimer before this same
            // marker for the identical reason).
            if (entityBehaviour.GetComponent<DominateTimer>() != null)
                return;

            VoxsVampirism.Log("MesmerizeGazeWatcher: activated pacified enemy '{0}' - opening enemy menu.", entityBehaviour.name);
            MesmerizeCommandMenu.ShowForEnemy(entityBehaviour);
        }

        // LateUpdate, not Update - see file header. Runs every frame regardless of the activate
        // button, since it's reacting to a window that already opened, not initiating one.
        void LateUpdate()
        {
            bool talkWindowOpenNow = DaggerfallUI.UIManager.TopWindow is DaggerfallTalkWindow;
            bool justOpened = talkWindowOpenNow && !talkWindowWasOpenLastFrame;
            talkWindowWasOpenLastFrame = talkWindowOpenNow;

            if (!justOpened)
                return;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null || !vampirism.IsGazePrimed)
                return;

            GameObject npcObject = RaycastTownsperson();
            if (npcObject == null)
                return;

            DaggerfallUI.UIManager.PopWindow();
            vampirism.ConsumeGaze();
            VoxsVampirism.Log("MesmerizeGazeWatcher: primed gaze consumed on '{0}' - swapped vanilla dialogue for the townsperson menu.", npcObject.name);
            MesmerizeCommandMenu.ShowForTownsperson(npcObject);
        }

        // Re-raycasts at the moment the talk window is detected (same frame the player clicked,
        // before they've had a chance to move) to identify which StaticNPC/MobilePersonNPC vanilla
        // just opened dialogue with - mirrors SneakFeedController.TryFindTarget's own recognition and
        // per-type activation-distance checks.
        static GameObject RaycastTownsperson()
        {
            Camera mainCamera = GameManager.Instance.MainCamera;
            if (!mainCamera)
                return null;

            Ray ray = new Ray(mainCamera.transform.position, mainCamera.transform.forward);
            RaycastHit hit;
            float maxDistance = Mathf.Max(PlayerActivate.StaticNPCActivationDistance, PlayerActivate.MobileNPCActivationDistance);
            if (!Physics.Raycast(ray, out hit, maxDistance, raycastLayerMask))
                return null;

            StaticNPC staticNpc = hit.transform.GetComponent<StaticNPC>();
            if (staticNpc != null)
                return hit.distance <= PlayerActivate.StaticNPCActivationDistance ? staticNpc.gameObject : null;

            MobilePersonNPC mobileNpc = hit.transform.GetComponent<MobilePersonNPC>();
            if (mobileNpc != null)
                return hit.distance <= PlayerActivate.MobileNPCActivationDistance ? mobileNpc.gameObject : null;

            return null;
        }
    }
}
