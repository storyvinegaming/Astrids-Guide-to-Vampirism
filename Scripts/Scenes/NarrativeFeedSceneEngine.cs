// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Narrative Feeding Scenes spec: replaces the flat town-feed menu (SneakFeedController.ShowFeedMenu)
// with a short procedurally-assembled scene, entirely as a narrative skin over the exact same
// underlying mechanics - every roll, blood value, Humanity effect, guard rule, and fights-back
// trigger this file touches routes through SneakFeedController/VoxsVampirismEffect's own existing
// public surface, never reimplementing any of it. Static class, no MonoBehaviour - architecturally
// identical to MesmerizeCommandMenu.cs (pure UI-window-chain driven, via the same
// VoxsVampirism.ShowWrappedPromptThenAction/CardListPickerWindow machinery every other menu in
// this mod already uses).
//
// Beat structure (see each Run* method below for the full detail):
//   0. Hunger's grip - a low-blood (<=3) Willpower gate, once per scene, replacing the flat menu's
//      own post-feed forced-kill roll for the duration of a scene.
//   1. Opening - scene-setting prose, selected by location/time context.
//   2. Approach - the player's choice among the selected opening's own four authored approaches
//      (three non-lethal + exactly one feast-method) plus Withdraw - no more universal fixed
//      Etiquette/Stealth/Feast options; every approach's label, roll, and method are opening-specific.
//   3. Complication (probabilistic) - a middle beat with its own mini-choice, resolving through a
//      small closed effect vocabulary (SceneEffects.cs).
//   4. Resolution - the pending approach's roll resolves through the unchanged existing feed
//      pipeline, narrated by a matched resolution fragment.
//
// Scope for this pass: the town feed encounter only. Corpse-draining, thrall interactions, frenzy
// events, and the Mesmerize Command Menu are untouched.
//
// Voice-over: every fragment box (opening/complication/resolution/offer) calls
// FeedSceneVoiceController.PlayForId(fragment.id) right before showing its own text - see that file's
// own header for the id-to-asset-name convention and its graceful-missing-clip fallback. Most of the
// fixed non-fragment text elsewhere in this file (Hunger's Grip, the peaceful-resolution line) has no
// JSON fragment id to look a clip up by and stays silent; RunTempleMassacre is the one exception,
// calling PlayForId("temple_massacre") directly by its own hardcoded id since a real recording exists
// for it despite the text itself being fixed prose, not fragment data. Stopping the previous line on
// scene-continue/scene-exit is NOT handled here at all - see VoxsVampirism.
// ShowWrappedPromptThenAction/ShowYesNoPromptThenAction's own header for why that lives there instead.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod.Effects;
using VoxsVampirismMod.UI;

namespace VoxsVampirismMod.Scenes
{
    public static class NarrativeFeedSceneEngine
    {
        // Mutable per-scene state, threaded through every beat's UI-callback closures - a scene is
        // never concurrent with another (each is a fully modal chain of pushed windows), so a single
        // mutable instance per TryStartScene call is safe.
        class SceneState
        {
            public GameObject npcObject;
            public string locationTag;
            public List<string> contextTags;
            public VoxsVampirismEffect vampirism;
            public bool isBloodlustDriven;
            public OpeningApproach pendingApproach; // the specific approach picked at Approach-commit
            public int pendingModifierTotal;
            public int humanityAtSceneStart;

            // Dark Brotherhood sanctioned feeding (§3) - true when the selected opening carried
            // autoSuccess, waiving the Approach-beat roll for every non-feast method. See RunOpening/
            // RunResolution.
            public bool openingAutoSuccess;

            // The selected opening's own four approaches (RunOpening) - RunApproach builds the menu
            // directly from this list.
            public List<OpeningApproach> approaches;

            // Wilderness Hunt spec: true for a TryStartAnimalHunt scene (npcObject is null in this
            // case - there's no townsperson at all) - see NarrativeFeedSceneEngine's own Wilderness
            // Hunt region for every place this branches behavior.
            public bool isAnimalHunt;

            // Wilderness Hunt spec: set by RunHuntTwist - the hunt found something other than
            // intended prey, so resolution/spawn selection both draw from the twist table instead of
            // the ordinary animal one.
            public bool huntIsTwist;
        }

        static readonly MobileTypes[] hunterTypes = { MobileTypes.Knight, MobileTypes.Mage };

        // Same lazy-load-and-cache shape MistFormEffect.PlayMistSound/VoxsVampirismEffect's own
        // feedingSound already use - a missing/failed load never blocks the scene itself, just
        // silently skips the sound. Played when the approach roll's OUTCOME is shown (ShowResolutionBox/
        // ShowHuntResolutionBox), not at the moment the player commits to a roll option - an earlier
        // version played it on commit instead, moved per explicit request so the sound lands with the
        // reveal, not the choice. Skipped entirely when no real roll happened (Dark Brotherhood
        // sanctioned feeding's waived roll, or an unrecognized-skill fallback).
        static AudioClip diceSound;

        static void PlayDiceSound()
        {
            if (diceSound == null)
                diceSound = VoxsVampirism.Mod.GetAsset<AudioClip>("dice.ogg");
            if (diceSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(diceSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Entry point, called from SneakFeedController.FeedOnNpc when Narrative Feeding Scenes is
        // enabled. Falls back to the classic flat menu (SneakFeedController.ShowFeedMenu) if a live
        // VoxsVampirismEffect can't be found (shouldn't happen here - FeedOnNpc's own caller already
        // confirmed HasVampirism() - but checked defensively, matching this mod's own established
        // convention) or if the fragment library failed to load, rather than leaving the player with
        // no feed option at all.
        public static void TryStartScene(GameObject npcObject, string locationTag)
        {
            if (npcObject == null)
                return;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                VoxsVampirism.Log("Scene: no live VoxsVampirismEffect found - falling back to the flat feed menu.");
                SneakFeedController.ShowFeedMenu(npcObject, SneakFeedController.GetLocationCategory());
                return;
            }

            if (!FragmentLibraryLoader.IsAvailable)
            {
                VoxsVampirism.Log("Scene: fragment library unavailable - falling back to the flat feed menu.");
                SneakFeedController.ShowFeedMenu(npcObject, SneakFeedController.GetLocationCategory());
                return;
            }

            SceneState state = new SceneState
            {
                npcObject = npcObject,
                locationTag = locationTag,
                contextTags = BuildContextTags(locationTag),
                vampirism = vampirism,
                humanityAtSceneStart = vampirism.GetHumanity(),
            };

            VoxsVampirism.Log("Scene: starting for '{0}' at location '{1}'.", npcObject.name, locationTag);

            FeedSceneMusicController.OnSceneStart();
            RunHungerGrip(state);
        }

        // Wilderness Hunt spec: entry point for SneakFeedController's own wilderness feed-key
        // fallback - replaces the removed Hunt Animal spell as the wilderness blood-recovery valve.
        // No npcObject at all (there's no townsperson here) - see the Wilderness Hunt region below for
        // the whole hunt-specific flow. Cooldown is checked by the caller BEFORE this is ever reached
        // (a global, save-persisted timestamp - VoxsVampirismEffect.IsAnimalHuntOnCooldown - not the
        // per-NPC daily marker feed scenes use), and stamped at Approach-commit inside RunApproach
        // itself, mirroring the townsperson scenes' own "Withdraw is free, rolling isn't" rule exactly.
        // Beat 0 (Hunger's Grip) is skipped entirely - there is no non-lethal path to convert and no
        // townsperson at risk, so the gate's whole purpose is absent; this goes straight to RunOpening.
        public static void TryStartAnimalHunt()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                VoxsVampirism.Log("Scene: animal hunt requested but no live VoxsVampirismEffect found - doing nothing.");
                return;
            }

            if (!FragmentLibraryLoader.IsAvailable)
            {
                VoxsVampirism.Log("Scene: animal hunt requested but the fragment library is unavailable - doing nothing.");
                return;
            }

            string locationTag = SneakFeedController.SceneLocationWilderness;
            List<string> contextTags = BuildContextTags(locationTag);
            contextTags.Add("animalhunt");

            SceneState state = new SceneState
            {
                npcObject = null,
                locationTag = locationTag,
                contextTags = contextTags,
                vampirism = vampirism,
                humanityAtSceneStart = vampirism.GetHumanity(),
                isAnimalHunt = true,
            };

            VoxsVampirism.Log("Scene: animal hunt starting.");

            FeedSceneMusicController.OnSceneStart();
            RunOpening(state);
        }

        static List<string> BuildContextTags(string locationTag)
        {
            List<string> tags = new List<string> { locationTag };

            bool indoor = locationTag != SneakFeedController.SceneLocationStreets && locationTag != SneakFeedController.SceneLocationWilderness;
            tags.Add(indoor ? "indoor" : "outdoor");

            int hour = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.Hour;
            tags.Add(hour >= 22 || hour < DaggerfallDateTime.DawnHour ? "latenight" : "night");

            return tags;
        }

        #region Beat 0 - Hunger's grip

        // At <=3 blood, roll Willpower once, up front - failure marks the whole scene as bloodlust-
        // driven (any later non-lethal success converts to a Feast at Resolution) and shows a
        // distinct warning beat before Approach, so the player chooses with full knowledge. This
        // replaces FeedOnAwakeNpc's own post-feed <=2-blood forced-kill roll for the duration of a
        // scene (called with suppressLowBloodCheck:true at Resolution) - one Willpower moment per
        // scene, up front, not a hidden second roll after the player has already committed.
        static void RunHungerGrip(SceneState state)
        {
            int blood = state.vampirism.GetBloodPoints();
            if (blood > 3)
            {
                state.isBloodlustDriven = false;
                RunOpening(state);
                return;
            }

            int willpower = GameManager.Instance.PlayerEntity.Stats.LiveWillpower;
            bool resisted = Dice100.SuccessRoll(willpower);
            state.isBloodlustDriven = !resisted;

            VoxsVampirism.Log("Scene: hunger's-grip roll at {0} blood (Willpower {1}) - resisted={2}.", blood, willpower, resisted);

            if (resisted)
            {
                RunOpening(state);
                return;
            }

            VoxsVampirism.ShowWrappedPromptThenAction(
                "The hunger is louder than your intent tonight...",
                () => RunOpening(state));
        }

        #endregion

        #region Beat 1 - Opening

        static void RunOpening(SceneState state)
        {
            FragmentData opening = state.isAnimalHunt
                ? FragmentLibraryLoader.SelectHuntFragment("opening", state.locationTag, state.contextTags,
                    state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints())
                : FragmentLibraryLoader.SelectFragment("opening", state.locationTag, state.contextTags,
                    state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());

            string fallbackText = state.isAnimalHunt
                ? "The wild is quiet, but the hunger insists there is prey nearby, if you can find it."
                : "You've spotted your prey. What do you do?";
            string text = opening != null ? SubstituteTokens(opening.text, state) : fallbackText;

            state.approaches = opening != null ? opening.approaches : null;
            state.openingAutoSuccess = opening != null && opening.autoSuccess;

            // The four approaches logged in one line, tied to the opening/context that produced
            // them, so a label/scene mismatch is a single grep away.
            VoxsVampirism.Log("Scene: opening fragment '{0}' offers approaches: {1}.",
                opening != null ? opening.id : "(fallback)", DescribeApproaches(state.approaches));

            FeedSceneVoiceController.PlayForId(opening != null ? opening.id : null);
            VoxsVampirism.ShowWrappedPromptThenAction(text, () => RunApproach(state));
        }

        static string DescribeApproaches(List<OpeningApproach> approaches)
        {
            if (approaches == null || approaches.Count == 0)
                return "(none)";

            List<string> parts = new List<string>();
            for (int i = 0; i < approaches.Count; i++)
            {
                OpeningApproach a = approaches[i];
                parts.Add(string.Format("[{0}->{1}]", a != null ? a.roll : "?", a != null ? a.method : "?"));
            }
            return string.Join(", ", parts.ToArray());
        }

        #endregion

        #region Beat 2 - Approach

        // The menu is built entirely from the selected opening's own four approaches - no more
        // hardcoded per-approach availability rule (Stealth's old indoor-only gate included): if an
        // opening's author didn't place a stealth-method entry there, it simply doesn't appear,
        // exactly as authored. If an opening somehow has no approaches at all (fragment library
        // returned no opening, or malformed data slipped past load-time validation), falls back to a
        // single default lethal option rather than an empty, dead-end menu. Withdraw is always
        // present, always last, and always free - no cooldown stamp, matching "reading the scene and
        // stepping back should be free."
        static void RunApproach(SceneState state)
        {
            List<OpeningApproach> approaches = state.approaches;
            if (approaches == null || approaches.Count == 0)
            {
                VoxsVampirism.Log("Scene: opening had no approaches at all - falling back to a default lethal-only menu.");
                approaches = new List<OpeningApproach> { state.isAnimalHunt ? OpeningApproach.DefaultHunt() : OpeningApproach.DefaultFeast() };
            }

            CardListPickerWindow picker = new CardListPickerWindow(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, "Hunt");
            Dictionary<string, OpeningApproach> labelToApproach = new Dictionary<string, OpeningApproach>();

            for (int i = 0; i < approaches.Count; i++)
            {
                OpeningApproach approach = approaches[i];
                if (approach == null || string.IsNullOrEmpty(approach.label) || string.IsNullOrEmpty(approach.roll) || string.IsNullOrEmpty(approach.method))
                    continue;

                string fullLabel = string.Format("{0} [{1}]", approach.label, approach.roll);
                if (labelToApproach.ContainsKey(fullLabel))
                    continue; // defensive - a duplicate label in hand-authored data shouldn't crash selection

                picker.AddOption(fullLabel);
                labelToApproach[fullLabel] = approach;
            }
            picker.AddOption("Withdraw");

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();

                if (itemString == "Withdraw")
                {
                    VoxsVampirism.Log("Scene: withdrew at Approach - no cost, no cooldown.");
                    FeedSceneMusicController.OnSceneEnd();
                    return;
                }

                OpeningApproach chosen;
                if (!labelToApproach.TryGetValue(itemString, out chosen))
                    return;

                if (state.isAnimalHunt)
                {
                    // Global, save-persisted cooldown (not the per-NPC daily marker) - already
                    // confirmed clear by the caller before the scene even started (SneakFeedController's
                    // own wilderness trigger), so there's no refusal check here, only the stamp itself -
                    // at approach-commit, not scene-start, so Withdraw above stays free and only an
                    // actual roll (win or lose) consumes it. See TryStartAnimalHunt's own header.
                    state.vampirism.StampAnimalHuntCooldown();
                }
                else
                {
                    if (SneakFeedController.IsOnFeedCooldown(state.npcObject))
                    {
                        DaggerfallUI.AddHUDText("They've already had enough of you for one day.");
                        return;
                    }

                    // Stamped here, at approach-commit, regardless of what happens next - this single
                    // stamp point is what makes "Withdraw before any roll is free; aborting from a
                    // complication costs the attempt" true with no separate bookkeeping.
                    SneakFeedController.StampFeedCooldown(state.npcObject);
                }

                state.pendingApproach = chosen;
                state.pendingModifierTotal = 0;

                VoxsVampirism.Log("Scene: approach committed - '{0}' [{1}] (method={2}).", chosen.label, chosen.roll, chosen.method);

                RunComplicationGate(state);
            };

            DaggerfallUI.UIManager.PushWindow(picker);
        }

        #endregion

        #region Beat 3 - Complication

        const string TempleFrenzyComplicationId = "complication_temple_frenzy_01";

        static void RunComplicationGate(SceneState state)
        {
            // Temple frenzy auto-trigger: feeding on holy ground while this deep into the hunger
            // isn't left to the normal SceneComplicationChancePercent roll - GetFragmentByIdIfEligible
            // still enforces the fragment's own "requires" block (bloodMax: 1 currently), so this
            // only ever actually fires under the exact same blood condition the fragment was authored
            // for; only the ROLL is bypassed, not the fragment's own eligibility. Never reachable for
            // an animal hunt (wilderness-only, never tagged "temple"), but guarded explicitly anyway.
            FragmentData forcedComplication = null;
            if (!state.isAnimalHunt && state.locationTag == SneakFeedController.SceneLocationTemple)
            {
                forcedComplication = FragmentLibraryLoader.GetFragmentByIdIfEligible(
                    TempleFrenzyComplicationId, state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());
            }

            FragmentData complication;
            if (forcedComplication != null)
            {
                VoxsVampirism.Log("Scene: temple frenzy auto-triggered (blood {0}) - bypassing the normal complication roll.", state.vampirism.GetBloodPoints());
                complication = forcedComplication;
            }
            else
            {
                if (!Dice100.SuccessRoll(VoxsVampirism.SceneComplicationChancePercent))
                {
                    RunResolution(state);
                    return;
                }

                complication = state.isAnimalHunt
                    ? FragmentLibraryLoader.SelectHuntFragment("complication", state.locationTag, state.contextTags,
                        state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints())
                    : FragmentLibraryLoader.SelectFragment("complication", state.locationTag, state.contextTags,
                        state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());
            }

            if (complication == null || complication.choices == null || complication.choices.Count == 0)
            {
                VoxsVampirism.Log("Scene: complication rolled but no usable complication fragment - skipping to resolution.");
                RunResolution(state);
                return;
            }

            VoxsVampirism.Log("Scene: complication fragment '{0}'.", complication.id);

            FeedSceneVoiceController.PlayForId(complication.id);
            VoxsVampirism.ShowWrappedPromptThenAction(
                SubstituteTokens(complication.text, state),
                () => RunComplicationChoices(state, complication));
        }

        static void RunComplicationChoices(SceneState state, FragmentData complication)
        {
            CardListPickerWindow picker = new CardListPickerWindow(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, "Hunt");
            Dictionary<string, FragmentChoice> labelToChoice = new Dictionary<string, FragmentChoice>();

            for (int i = 0; i < complication.choices.Count; i++)
            {
                FragmentChoice choice = complication.choices[i];
                if (choice == null || string.IsNullOrEmpty(choice.label))
                    continue;

                string label = !string.IsNullOrEmpty(choice.roll) ? string.Format("{0} [{1}]", choice.label, choice.roll) : choice.label;
                if (labelToChoice.ContainsKey(label))
                    continue; // defensive - a duplicate label in hand-authored data shouldn't crash selection

                picker.AddOption(label);
                labelToChoice[label] = choice;
            }

            if (labelToChoice.Count == 0)
            {
                VoxsVampirism.Log("Scene: complication '{0}' had no usable choices - skipping to resolution.", complication.id);
                RunResolution(state);
                return;
            }

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();

                FragmentChoice choice;
                if (!labelToChoice.TryGetValue(itemString, out choice))
                {
                    RunResolution(state);
                    return;
                }

                ResolveChoice(state, choice);
            };

            DaggerfallUI.UIManager.PushWindow(picker);
        }

        // A roll choice names a DFCareer.Skills value (an ENGINE enum, not one declared in this mod's
        // own source - referencing it via Enum.TryParse is unaffected by this mod's own "no custom
        // enum" rule), rolls d100 vs the player's live skill value, and tallies the skill unconditionally
        // (win or lose) via PlayerEntity.TallySkill, matching vanilla pickpocketing's own precedent -
        // these scenes should genuinely exercise the character sheet. A plain choice just applies its
        // own effect directly.
        static void ResolveChoice(SceneState state, FragmentChoice choice)
        {
            SceneEffect effect;

            if (!string.IsNullOrEmpty(choice.roll))
            {
                DFCareer.Skills skill;
                if (!TryParseSkill(choice.roll, out skill))
                {
                    VoxsVampirism.Log("Scene: complication choice named unrecognized skill '{0}' - falling back to end_scene.", choice.roll);
                    ApplyEffect(state, new SceneEffect { Kind = SceneEffects.KindEndScene, Value = 0 });
                    return;
                }

                int skillValue = GetCappedLiveSkillValue(skill);
                bool success = Dice100.SuccessRoll(skillValue);
                GameManager.Instance.PlayerEntity.TallySkill(skill, 1);

                VoxsVampirism.Log("Scene: complication skill check '{0}' ({1}) - success={2}.", skill, skillValue, success);

                effect = SceneEffects.ParseEffect(success ? choice.success : choice.failure);
            }
            else
            {
                effect = SceneEffects.ParseEffect(choice.effect);
            }

            ApplyEffect(state, effect);
        }

        // Authored skill names come in natural display form ("Hand-to-Hand", "Critical Strike"),
        // which doesn't match the C# enum member spelling (HandToHand, CriticalStrike) that a plain
        // Enum.TryParse requires - stripping spaces/hyphens and parsing case-insensitively lets
        // fragment data use whichever form reads naturally without a silent, always-fails mismatch.
        // Used for every skill name this file ever parses (complication choices, opening approaches).
        static bool TryParseSkill(string raw, out DFCareer.Skills skill)
        {
            if (string.IsNullOrEmpty(raw))
            {
                skill = default(DFCareer.Skills);
                return false;
            }

            string normalized = raw.Replace(" ", string.Empty).Replace("-", string.Empty);
            return System.Enum.TryParse(normalized, true, out skill);
        }

        // Every skill value a scene roll reads comes through here, capped at 90 - a master-level
        // character (skill 100+) would otherwise auto-succeed every d100 scene check forever, and
        // these are meant to stay tense at any level. Applied to the BASE value, before any
        // complication bonus/penalty (pendingModifierTotal) - a complication's own +5/+10 can still
        // push the final chance past 90, and a penalty pulls it below, both exactly as authored.
        const int SceneSkillRollCap = 90;

        static int GetCappedLiveSkillValue(DFCareer.Skills skill)
        {
            return Mathf.Min(SceneSkillRollCap, GameManager.Instance.PlayerEntity.Skills.GetLiveSkillValue(skill));
        }

        static void ApplyEffect(SceneState state, SceneEffect effect)
        {
            switch (effect.Kind)
            {
                case SceneEffects.KindBonus:
                    state.pendingModifierTotal += effect.Value;
                    RunResolution(state);
                    break;

                case SceneEffects.KindPenalty:
                    state.pendingModifierTotal -= effect.Value;
                    RunResolution(state);
                    break;

                case SceneEffects.KindBlood:
                    if (!state.vampirism.TrySpendBlood(effect.Value))
                        DaggerfallUI.AddHUDText("You have nothing left to give.");
                    RunResolution(state);
                    break;

                case SceneEffects.KindAbort:
                    DaggerfallUI.AddHUDText("You pull back - the moment slips away.");
                    VoxsVampirism.Log("Scene: aborted from complication - cooldown already consumed at approach-commit.");
                    FeedSceneMusicController.OnSceneEnd();
                    break;

                case SceneEffects.KindEndScene:
                    ShowPeacefulResolution();
                    break;

                case SceneEffects.KindEscalate:
                    RunDeescalation(state);
                    break;

                case SceneEffects.KindHumanity:
                    if (effect.Value > 0)
                    {
                        // A humanity-gaining choice is the character choosing restraint over blood -
                        // there is no feed to resolve after it, so this ends the scene the same way
                        // KindAbort does rather than continuing to a Charm/Stealth/Feast resolution.
                        // The scene ends on this choice regardless of whether
                        // TryApplySceneHumanityGain's own daily cooldown actually grants the point
                        // this time - the character's choice not to feed happened either way; only
                        // the HUD message differs.
                        bool granted = state.vampirism.TryApplySceneHumanityGain();
                        DaggerfallUI.AddHUDText(granted
                            ? "You pull back and let them go - a small mercy, and Humanity for it."
                            : "You pull back and let them go - the moment slips away, changing nothing.");
                        VoxsVampirism.Log("Scene: humanity-gain choice ended the scene without feeding (granted={0}).", granted);
                        FeedSceneMusicController.OnSceneEnd();
                    }
                    else
                    {
                        state.vampirism.ApplySceneHumanityLoss();
                        RunResolution(state);
                    }
                    break;

                case SceneEffects.KindRevealVampire:
                    RunRevealVampire(state);
                    break;

                case SceneEffects.KindHunterArrives:
                    RunHunterArrives(state);
                    break;

                case SceneEffects.KindRevealWerewolf:
                    RunRevealWerewolf(state);
                    break;

                case SceneEffects.KindRevealDaedra:
                    RunRevealDaedra(state);
                    break;

                case SceneEffects.KindTempleMassacre:
                    RunTempleMassacre(state);
                    break;

                case SceneEffects.KindHuntTwist:
                    RunHuntTwist(state);
                    break;

                default:
                    RunResolution(state);
                    break;
            }
        }

        // Applies to escalate only (a failed skill-check whose failure effect is escalate, or a
        // plain complication choice whose effect is escalate directly - the "explicit player choice
        // to fight" the spec asks for, since the fixed Approach mechanical set has no room for a 6th
        // "attack" option) - never to reveal_vampire/hunter_arrives, which explicitly skip this beat.
        // Thaumaturgy is the mechanically correct school (vanilla's own Pacify/Calm effects are
        // Thaumaturgy) - success resolves peacefully, failure proceeds to the same guaranteed
        // fights-back trigger a failed Feast already uses.
        static void RunDeescalation(SceneState state)
        {
            int thaumaturgy = GetCappedLiveSkillValue(DFCareer.Skills.Thaumaturgy);
            bool calmed = Dice100.SuccessRoll(thaumaturgy);
            GameManager.Instance.PlayerEntity.TallySkill(DFCareer.Skills.Thaumaturgy, 1);

            VoxsVampirism.Log("Scene: de-escalation Thaumaturgy roll ({0}) - calmed={1}.", thaumaturgy, calmed);

            if (calmed)
                ShowPeacefulResolution();
            else
            {
                SneakFeedController.TryTriggerFightsBack(state.npcObject, guaranteed: true);
                FeedSceneMusicController.OnSceneEnd();
            }
        }

        static void ShowPeacefulResolution()
        {
            VoxsVampirism.Log("Scene: resolved peacefully.");
            VoxsVampirism.ShowWrappedPromptThenAction(
                "The moment passes. Whatever almost happened here, it doesn't happen now.",
                () => FeedSceneMusicController.OnSceneEnd());
        }

        // reveal_vampire: no de-escalation beat (explicit spec exclusion - "you cannot soothe a thing
        // that was hunting you," the same rule that already makes Mesmerize useless against a real
        // vampire/ancient). Reuses the exact existing fights-back vampire-variant machinery - the
        // NPC-swap, the corpse-feed rules on victory, Mesmerize's own uselessness against it - all
        // already built, nothing reimplemented here.
        static void RunRevealVampire(SceneState state)
        {
            DaggerfallUI.AddHUDText("Something in their gaze is wrong - too still, too old, too aware.");
            SneakFeedController.SpawnFightsBackEnemy(state.npcObject, SneakFeedController.RevealKindVampire, trackForReturn: false);
            FeedSceneMusicController.OnSceneEnd();
        }

        // reveal_werewolf (§1): unlike the vampire reveal, this one IS de-escalatable at both layers -
        // a werewolf/wereboar is a mortal mind wearing a beast, not an undead one. This pre-fight
        // Thaumaturgy beat mirrors RunDeescalation's own structure exactly; mid-fight Mesmerize needs
        // no extra code here at all - Werewolf/Wereboar map to DFCareer.EnemyGroups.Humanoid (confirmed
        // against source), which already satisfies MesmerizeEffect's own eligibility check on its own.
        static void RunRevealWerewolf(SceneState state)
        {
            int thaumaturgy = GetCappedLiveSkillValue(DFCareer.Skills.Thaumaturgy);
            bool calmed = Dice100.SuccessRoll(thaumaturgy);
            GameManager.Instance.PlayerEntity.TallySkill(DFCareer.Skills.Thaumaturgy, 1);

            VoxsVampirism.Log("Scene: reveal_werewolf - Thaumaturgy roll ({0}) - calmed={1}.", thaumaturgy, calmed);

            if (calmed)
                ShowPeacefulResolution();
            else
            {
                SneakFeedController.SpawnFightsBackEnemy(state.npcObject, SneakFeedController.RevealKindWerewolf, trackForReturn: false);
                FeedSceneMusicController.OnSceneEnd();
            }
        }

        // reveal_daedra (§1b): no de-escalation beat, either layer - not a judgment call. Daedra map
        // to DFCareer.EnemyGroups.Daedra, which MesmerizeEffect's own eligibility check already
        // excludes structurally (only EnemyClass or Humanoid-group EnemyMonster targets qualify), so
        // this mirrors RunRevealVampire's own directness with no pre-fight check at all.
        static void RunRevealDaedra(SceneState state)
        {
            SneakFeedController.SpawnFightsBackEnemy(state.npcObject, SneakFeedController.RevealKindDaedra, trackForReturn: false);
            FeedSceneMusicController.OnSceneEnd();
        }

        // hunter_arrives: the original victim is hidden and scheduled to return later (reusing
        // SneakFeedController.TrackNpcForReturn, the same mechanic a mortal fights-back spawn's own
        // victim gets), then a hostile Knight/Mage spawns with a VampireHunterMarker attached - no
        // TownspersonCorpseMarker/FightsBackVariantMarker, so the hunter is a plain combat kill to
        // every other system in this mod (falls through to the generic corpse-tagging path on death).
        // This is exactly what makes "not a murder for Humanity purposes" require no extra code -
        // nothing in this mod calls ChangeHumanity off a plain kill in the first place.
        static void RunHunterArrives(SceneState state)
        {
            Vector3 position = state.npcObject.transform.position;
            state.npcObject.SetActive(false);
            SneakFeedController.TrackNpcForReturn(state.npcObject, DaggerfallDateTime.MinutesPerDay);

            MobileTypes type = hunterTypes[Random.Range(0, hunterTypes.Length)];
            GameObject hunter = GameObjectHelper.CreateEnemy(type.ToString(), type, position, MobileGender.Unspecified, null, MobileReactions.Hostile);
            hunter.AddComponent<VampireHunterMarker>();

            DaggerfallUI.AddHUDText("A blade catches the light before you ever see them coming - someone knew exactly what you are.");
            VoxsVampirism.Log("Scene: hunter_arrives - spawned {0}.", type);
            FeedSceneMusicController.OnSceneEnd();
        }

        // temple_massacre (§4): the scene's own terminal beat - no resolution fragment follows, this
        // ends the scene directly, same as reveal_vampire/hunter_arrives above. Every NPC currently in
        // the temple interior is killed via the existing corpse-swap machinery (one shared
        // implementation - see SneakFeedController.MassacreCurrentInterior/KillNpcAndLeaveCorpse).
        // DrinkDryNoHumanity() gives the full blood-to-10 refill plus the same heal/feeding-sound/
        // screen-flash every other successful feed already gets (ApplyFeedSideEffects) WITHOUT its
        // own baked-in Humanity cost, since the -3 here is this scene's own distinct penalty, not
        // DrinkDry's usual -1 (ChangeHumanity already clamps to [0,10] and fires its own HUD/status
        // update). No guard report anywhere in this path - "no witnesses survive" is the spec's own
        // default, satisfied simply by never calling VoxsVampirism.ReportVampiricAssaultCrime here.
        static void RunTempleMassacre(SceneState state)
        {
            int killed = SneakFeedController.MassacreCurrentInterior();
            state.vampirism.DrinkDryNoHumanity();
            state.vampirism.ChangeHumanity(-3);

            VoxsVampirism.Log("Scene: temple_massacre - {0} NPC(s) killed, blood filled to 10, humanity now {1}/10.",
                killed, state.vampirism.GetHumanity());

            string text = "When it's over, you are still standing in the nave, and you are the only one who is. " +
                "The silence afterward has a different quality than the silence before - the kind a room only learns once. " +
                "You do not remember deciding to do this. You remember deciding nothing at all.";

            int newHumanity = state.vampirism.GetHumanity();
            if (newHumanity != state.humanityAtSceneStart)
                text = string.Format("{0}\n\n(Humanity: {1}/10)", text, newHumanity);

            // Fixed prose (no JSON fragment id to key off), but a real recording exists for this
            // specific beat - see this file's own header for why this is the one exception that
            // calls PlayForId directly rather than off fragment.id.
            FeedSceneVoiceController.PlayForId("temple_massacre");
            VoxsVampirism.ShowWrappedPromptThenAction(text, () => FeedSceneMusicController.OnSceneEnd());
        }

        #endregion

        #region Beat 4 - Resolution

        // Shared by every non-lethal approach's success HUD line below (Stealth/Etiquette/Opportunity
        // alike) - deliberately generic rather than method-flavored ("...while they sleep, unnoticed",
        // "your gaze holds them...", "the opening closes around you..."). Those older, method-fixed
        // lines predate resolution fragments existing at all and are still exactly what the classic
        // flat-menu fallback (SneakFeedController's own Charm/Stealth calls) still uses, where there's
        // no fragment box to follow and say anything more specific. Inside an actual scene, though, the
        // matched resolution fragment's own bespoke text is about to narrate this exact approach and
        // outcome in the box that opens right after this HUD line - a second, fixed narrative claim
        // here would only ever be redundant at best and directly contradictory at worst (a fragment
        // describing a struggle, a distraction, a lie, whatever it was actually authored around) since
        // fragments vary per opening/location/context and this line can't know which one just ran.
        const string GenericSuccessMessage = "You feed.";

        // Guard-calling in a guard-call location - Streets (open, public) or Palace (heavily
        // patrolled) - not Wilderness (no guards present to call out in open country between
        // settlements) or Inn/Residence (private). This extends DoFeast's own original "outdoors,
        // guards are called unconditionally" rule to the two new location tags it never had to
        // consider before.
        static bool IsGuardCallLocation(string locationTag)
        {
            return locationTag == SneakFeedController.SceneLocationStreets || locationTag == SneakFeedController.SceneLocationPalace;
        }

        static void RunResolution(SceneState state)
        {
            // Wilderness Hunt spec: none of this method's own feast/stealth/etiquette/opportunity
            // dispatch means anything for a hunt (no npcObject, no guard-calling, no corpse-kill
            // helper to call) - redirected to its own dedicated resolution entirely. See the
            // Wilderness Hunt region below.
            if (state.isAnimalHunt)
            {
                RunHuntResolution(state);
                return;
            }

            OpeningApproach approach = state.pendingApproach;

            DFCareer.Skills skill;
            bool success;

            // -1 = no roll actually happened (auto-success or an unrecognized skill name) - kept
            // separate from state.pendingModifierTotal (already logged on its own below) so the final
            // log line can show the player's actual live skill value at the moment of the roll, not
            // just the modifier applied on top of it - the only way to tell "bad luck" from "the
            // number itself was lower than expected" after the fact, since GetLiveSkillValue isn't
            // otherwise recorded anywhere. Also what BuildRollSummaryLine uses to decide whether a
            // real roll happened at all.
            int baseSkillValue = -1;
            int roll1 = -1;
            int roll2 = -1;

            // Dark Brotherhood sanctioned feeding (§3): a non-feast approach under an autoSuccess
            // opening skips the roll entirely and succeeds automatically - the Brotherhood offers the
            // victim, there's nothing to fail. No TallySkill either, since no roll happened. The
            // feast-method approach is never waived - taking more than was offered is still a kill.
            bool rollWaived = state.openingAutoSuccess && approach.method != "feast";

            if (rollWaived)
            {
                success = true;
                VoxsVampirism.Log("Scene: approach '{0}' auto-succeeded (Dark Brotherhood sanctioned feeding) - roll waived.", approach.label);
            }
            else if (!TryParseSkill(approach.roll, out skill))
            {
                // Defensive only - RunApproach only ever offers an approach whose label/roll/method
                // are all already non-empty; an unrecognized skill name here means a typo slipped
                // past that check (e.g. a roll value that isn't a real DFU skill at all).
                VoxsVampirism.Log("Scene: approach '{0}' named unrecognized skill '{1}' - treating as a failed attempt.", approach.label, approach.roll);
                success = false;
            }
            else
            {
                baseSkillValue = GetCappedLiveSkillValue(skill);
                int chance = baseSkillValue + state.pendingModifierTotal;

                // Same formula Dice100.SuccessRoll uses internally (its own comment: "Same as
                // Random.Range(1, 101) <= chanceSuccess") - rolled manually via Dice100.Roll() instead
                // so the actual 1-100 value(s) can be shown on the resolution box, not just the
                // pass/fail outcome.
                roll1 = Dice100.Roll();
                bool roll1Success = roll1 <= chance;

                if (approach.method == "etiquette")
                {
                    // Etiquette keeps Charm's own "advantage" (Mesmerize's §1c bonus) - two
                    // independent rolls, succeeding if either passes. Both always rolled (not short-
                    // circuited on roll1's own success) so both values are always available to show.
                    roll2 = Dice100.Roll();
                    success = roll1Success || roll2 <= chance;
                }
                else
                {
                    success = roll1Success;
                }

                // Every approach roll tallies its skill now, win or lose - previously only
                // Opportunity did; generalizing the approach system to be fully data-driven means
                // every approach earns the same character-sheet exercise, not just the bespoke one.
                GameManager.Instance.PlayerEntity.TallySkill(skill, 1);
            }

            // Bloodlust conversion: every non-lethal method (stealth/etiquette/opportunity) that
            // succeeds while bloodlust-driven resolves as a Feast instead - the feast method is
            // already lethal, so there's nothing for it to convert to.
            bool convertedToFeast = success && state.isBloodlustDriven && approach.method != "feast";
            string effectiveMethod = convertedToFeast ? "feast" : approach.method;

            VoxsVampirism.Log("Scene: resolving approach='{0}' roll={1} method={2} success={3} convertedToFeast={4} baseSkillValue={5} modifierTotal={6} effectiveChance={7}.",
                approach.label, approach.roll, approach.method, success, convertedToFeast,
                baseSkillValue >= 0 ? baseSkillValue.ToString() : "n/a",
                state.pendingModifierTotal,
                baseSkillValue >= 0 ? (baseSkillValue + state.pendingModifierTotal).ToString() : "n/a");

            // A successful Feast on the streets at night draws no guards - the streets are empty
            // (Night Walkers notwithstanding) and there's no one left awake to raise the alarm. Only
            // the success case is exempted - a botched Feast is loud regardless of the hour (the
            // victim's own scream, per the failure-path resolution text, not witness density), and
            // Palace is unaffected either way (heavily patrolled around the clock).
            bool nightStreetFeastGoesUnwitnessed = effectiveMethod == "feast" && success
                && state.locationTag == SneakFeedController.SceneLocationStreets
                && DaggerfallUnity.Instance.WorldTime.Now.IsNight;

            if (effectiveMethod == "feast" && IsGuardCallLocation(state.locationTag) && !nightStreetFeastGoesUnwitnessed)
                VoxsVampirism.ReportVampiricAssaultCrime();

            if (effectiveMethod == "feast")
            {
                if (success)
                    SneakFeedController.FeastOnNpc(state.npcObject);
                else
                    // Feast's own guaranteed-on-failure fights-back trigger, unchanged (confirmed
                    // decision) - the fights-back relocation applies only to the loud-failure methods'
                    // own probabilistic trigger, not this separate pre-existing mechanic.
                    SneakFeedController.TryTriggerFightsBack(state.npcObject, guaranteed: true);
            }
            else if (approach.method == "stealth")
            {
                if (success)
                {
                    // Generic on purpose (see GenericSuccessMessage's own header) - the resolution
                    // fragment box that follows already narrates this specific approach/opening in its
                    // own words; a fixed method-flavored HUD line here ("...while they sleep,
                    // unnoticed") would sit ahead of that box making a claim the fragment's own text
                    // might directly contradict (not every Stealth approach involves a sleeping
                    // target, for one).
                    SneakFeedController.FeedOnAwakeNpc(state.npcObject, GenericSuccessMessage,
                        suppressLowBloodCheck: true, suppressVampireReveal: true);
                }
                else
                {
                    // Loud failure profile - guards are called unconditionally, matching the original
                    // fixed Stealth option's own behavior. Generic for the same reason as above: the
                    // failure-branch resolution fragment already narrates exactly how this particular
                    // attempt went wrong, in its own words - this line only needs to convey the
                    // mechanical consequence (guards incoming), not guess at the specific staging.
                    DaggerfallUI.AddHUDText("Guards have been alerted.");
                    VoxsVampirism.ReportVampiricAssaultCrime();
                }
            }
            else if (approach.method == "etiquette")
            {
                if (success)
                {
                    SneakFeedController.FeedOnAwakeNpc(state.npcObject, GenericSuccessMessage,
                        suppressLowBloodCheck: true, suppressVampireReveal: true);
                }
                // Soft failure profile: no guards, no fights-back (relocated to complications only) -
                // a normal failed attempt, matching Charm's own "never calls guards, win or lose."
            }
            else // opportunity - soft failure profile
            {
                if (success)
                {
                    SneakFeedController.FeedOnAwakeNpc(state.npcObject, GenericSuccessMessage,
                        suppressLowBloodCheck: true, suppressVampireReveal: true);
                }
                // Failure: no guards, no fights-back - the chance simply closes. The per-NPC daily
                // attempt is already consumed (stamped at Approach-commit), so nothing further
                // happens here.
            }

            ShowResolutionBox(state, effectiveMethod, success, convertedToFeast, approach.roll, baseSkillValue, roll1, roll2);
        }

        static void ShowResolutionBox(SceneState state, string effectiveMethod, bool success, bool convertedToFeast,
            string skillName, int baseSkillValue, int roll1, int roll2)
        {
            FragmentData resolution = FragmentLibraryLoader.SelectResolution(effectiveMethod, success, state.locationTag, state.contextTags,
                state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());
            string text = resolution != null ? SubstituteTokens(resolution.text, state) : DefaultResolutionText(success);
            text = BuildRollSummaryLine(skillName, baseSkillValue, state.pendingModifierTotal, roll1, roll2, success) + text;

            if (convertedToFeast)
                text = "The hunger overtakes your intent - you meant only to sip, but there was no stopping once it began.\n\n" + text;

            int newHumanity = state.vampirism.GetHumanity();
            if (newHumanity != state.humanityAtSceneStart)
            {
                // Guaranteed append regardless of whether the matched fragment's own text already
                // included a Humanity mention - the spec's own hard requirement that the number and
                // direction always appear in the box, not left to fragment authors to remember.
                string flavor = newHumanity > state.humanityAtSceneStart
                    ? "A flicker of your old self returns."
                    : "Your humanity wanes.";
                text = string.Format("{0}\n\n{1} (Humanity: {2}/10)", text, flavor, newHumanity);
            }

            VoxsVampirism.Log("Scene: resolution fragment '{0}' (method={1}, success={2}).",
                resolution != null ? resolution.id : "(fallback)", effectiveMethod, success);

            // Only for a real roll (baseSkillValue >= 0) - a waived/auto-success or unrecognized-skill
            // fallback has nothing to rattle.
            if (baseSkillValue >= 0)
                PlayDiceSound();
            FeedSceneVoiceController.PlayForId(resolution != null ? resolution.id : null);
            VoxsVampirism.ShowWrappedPromptThenAction(text, () => TryShowPostFeedOffer(state, effectiveMethod, success));
        }

        static string DefaultResolutionText(bool success)
        {
            return success ? "It's done." : "It doesn't go as planned.";
        }

        // Shown atop a resolution box so the player can see exactly what decided the outcome - the
        // skill rolled, the actual 1-100 roll(s), and what they needed to roll under (base skill
        // value plus any complication modifier, already folded into one number - the breakdown
        // itself isn't shown, just the final target). Format: "[Success: Stealth 34/62]" - a second
        // roll (Etiquette's own "best of two") joins both values with a comma, e.g. "34,56/62".
        // Returns empty string (never null) when no real roll happened (baseSkillValue < 0 - a waived
        // Dark Brotherhood approach or an unrecognized-skill fallback), so callers can always safely
        // prepend the result without a null check.
        static string BuildRollSummaryLine(string skillName, int baseSkillValue, int modifierTotal, int roll1, int roll2, bool success)
        {
            if (string.IsNullOrEmpty(skillName) || baseSkillValue < 0)
                return string.Empty;

            int chance = baseSkillValue + modifierTotal;
            string rollPart = roll2 >= 0
                ? string.Format("{0},{1}", roll1, roll2)
                : roll1.ToString();

            // A trailing space after the closing bracket so the box's own narrative text - immediately
            // following via \n\n - never visually runs up against it.
            return string.Format("[{0}: {1} {2}/{3}] \n\n",
                success ? "Success" : "Failure", skillName, rollPart, chance);
        }

        #endregion

        #region Post-Feed Offer

        // Post-Feed Offers update: a rare, unprompted Create Vampire/Create Thrall offer shown after a
        // genuine non-violent scene feed - this is now the ONLY door to thrall or vampire creation in
        // the whole mod (Mesmerize Command Menu's own Create Thrall option was removed entirely - see
        // MesmerizeCommandMenu.cs's own header). "success && effectiveMethod != feast" excludes Feast,
        // AND a bloodlust-converted Feast (RunResolution already folds that into effectiveMethod=="feast"
        // before this is ever reached), AND a failed attempt of any method. An aborted/withdrawn scene
        // never reaches this at all - Withdraw (RunApproach) and a complication's own abort/reveal/
        // hunter-arrives/temple-massacre branches (ApplyEffect) each end the scene directly, never
        // routing through ShowResolutionBox. Frenzy feeds are likewise structurally excluded - that
        // path lives entirely in SneakFeedController and never calls into this engine at all. One offer
        // maximum per scene: Create Vampire (rarer) is rolled first; Create Thrall is only rolled if
        // the vampire offer didn't fire.
        const int postFeedOfferBloodCost = 3;

        static void TryShowPostFeedOffer(SceneState state, string effectiveMethod, bool success)
        {
            if (!success || effectiveMethod == "feast")
            {
                FeedSceneMusicController.OnSceneEnd();
                return;
            }

            // The Legion System update: capacity gating moved from a pre-roll Humanity-band headcount
            // check to a post-roll budget check against the SPECIFIC kind rolled - a created vampire
            // (VampireUpkeep) and a thrall (ThrallUpkeep) no longer cost the same against the shared
            // AllyCombatController budget, so which one to check depends on which one actually came up.
            // Still reads as "an offer the player can't accept never shows" from the player's own
            // perspective either way - the budget check just now happens a step later than it used to.
            string offerKind = null;
            if (Dice100.SuccessRoll(VoxsVampirism.CreateVampireOfferChancePercent))
                offerKind = "vampire";
            else if (Dice100.SuccessRoll(VoxsVampirism.CreateThrallOfferChancePercent))
                offerKind = "thrall";

            VoxsVampirism.Log("Scene: post-feed offer roll - vampireChance={0}, thrallChance={1}, result={2}.",
                VoxsVampirism.CreateVampireOfferChancePercent, VoxsVampirism.CreateThrallOfferChancePercent, offerKind ?? "none");

            if (offerKind == null)
            {
                FeedSceneMusicController.OnSceneEnd();
                return;
            }

            int offerCost = offerKind == "vampire" ? ThrallManager.VampireUpkeepCost : ThrallManager.ThrallUpkeepCost;
            if (!AllyCombatController.CanAfford(offerCost))
            {
                VoxsVampirism.Log("Scene: post-feed offer '{0}' rolled but the legion budget has no room ({1}, needs {2} more) - skipping.",
                    offerKind, AllyCombatController.DescribeBudget(), offerCost);
                FeedSceneMusicController.OnSceneEnd();
                return;
            }

            ShowPostFeedOffer(state, offerKind);
        }

        static void ShowPostFeedOffer(SceneState state, string offerKind)
        {
            FragmentData offer = FragmentLibraryLoader.SelectOffer(offerKind, state.locationTag, state.contextTags,
                state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());
            if (offer == null)
            {
                VoxsVampirism.Log("Scene: post-feed offer '{0}' rolled but no usable offer fragment found - skipping.", offerKind);
                FeedSceneMusicController.OnSceneEnd();
                return;
            }

            VoxsVampirism.Log("Scene: post-feed offer fragment '{0}' shown (kind={1}).", offer.id, offerKind);

            string text = SubstituteTokens(offer.text, state);
            FeedSceneVoiceController.PlayForId(offer.id);
            VoxsVampirism.ShowYesNoPromptThenAction(text,
                () => AcceptPostFeedOffer(state, offerKind),
                () =>
                {
                    VoxsVampirism.Log("Scene: post-feed offer '{0}' declined.", offerKind);
                    FeedSceneMusicController.OnSceneEnd();
                });
        }

        // Blood-mixing cost, same PayBloodOrHealth/desperate-casting-HP-burn pattern used everywhere
        // else in this mod - CAN kill on a reckless accept, exactly like every other blood-or-health
        // cost. Humanity-neutral either way (deliberate default - see the spec's own note: nothing in
        // the established economy charges Humanity for either creation path, and a rare 2%/10% event
        // shouldn't also double-punish on top of its own rarity).
        static void AcceptPostFeedOffer(SceneState state, string offerKind)
        {
            int bloodSpent, hpPercentOwed;
            state.vampirism.PayBloodOrHealth(postFeedOfferBloodCost, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0)
            {
                DaggerfallEntityBehaviour playerBehaviour = GameManager.Instance.PlayerEntityBehaviour;
                playerBehaviour.Entity.DecreaseHealth(Mathf.RoundToInt(playerBehaviour.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            Vector3 position = state.npcObject.transform.position;
            state.npcObject.SetActive(false);

            if (offerKind == "vampire")
            {
                // StartTransform (called inside CreateThrallAndStartTransform) already shows its own
                // HUD confirmation line - no second message needed here.
                ThrallManager.CreateThrallAndStartTransform(position);
            }
            else
            {
                ThrallManager.CreateThrall(position);
                DaggerfallUI.AddHUDText(string.Format("You open a vein and press it to their lips - when they rise, they are yours. Court: {0}.",
                    AllyCombatController.DescribeBudget()));
            }

            VoxsVampirism.Log("Scene: post-feed offer '{0}' accepted - blood spent={1}, hpPercentOwed={2}.", offerKind, bloodSpent, hpPercentOwed);

            FeedSceneMusicController.OnSceneEnd();
        }

        #endregion

        #region Wilderness Hunt

        // Wilderness Hunt spec: replaces the removed Hunt Animal spell as the wilderness blood-
        // recovery valve. Reuses the exact same Opening/Approach/Complication machinery a townsperson
        // scene uses (see each of those methods' own state.isAnimalHunt branches above), but resolves
        // through this dedicated region instead of RunResolution's own feast/stealth/etiquette/
        // opportunity dispatch, which has no meaning here - RunResolution itself just redirects to
        // RunHuntResolution below when state.isAnimalHunt is set.
        //
        // Only bonus/penalty/blood/humanity/abort/hunt_twist are ever meaningful on an animalhunt-
        // tagged complication choice - escalate/reveal_*/temple_massacre/end_scene all assume a
        // townsperson (state.npcObject, null for a hunt) and are never authored onto animalhunt
        // complications in the shipped data. Not defended against here - same trust this file already
        // extends to every other tag/effect combination staying within its own documented vocabulary.

        struct WeightedAnimal
        {
            public MobileTypes Type;
            public int Weight;
            public WeightedAnimal(MobileTypes type, int weight) { Type = type; Weight = weight; }
        }

        // Weighted toward mid-yield prey - weights flagged for tuning, per spec.
        static readonly WeightedAnimal[] huntAnimalTable =
        {
            new WeightedAnimal(MobileTypes.GrizzlyBear, 3),
            new WeightedAnimal(MobileTypes.SabertoothTiger, 2),
            new WeightedAnimal(MobileTypes.GiantBat, 3),
            new WeightedAnimal(MobileTypes.Rat, 2),
            new WeightedAnimal(MobileTypes.Spider, 2),
        };

        // Wilderness monsters that were never prey - werewolves/wereboars deliberately excluded (the
        // reveal system owns lycanthrope surprises) and nothing bloodless (a twist with no blood
        // payout at all would be purely punitive, not "a harder meal" - see HandleAnyEnemyDeathForBloodInfo's
        // own IsBloodless classification, which none of these three trip). Weights flagged for tuning.
        static readonly WeightedAnimal[] huntTwistTable =
        {
            new WeightedAnimal(MobileTypes.Orc, 3),
            new WeightedAnimal(MobileTypes.Spriggan, 2),
            new WeightedAnimal(MobileTypes.Centaur, 2),
        };

        static MobileTypes PickWeighted(WeightedAnimal[] table)
        {
            int totalWeight = 0;
            for (int i = 0; i < table.Length; i++)
                totalWeight += table[i].Weight;

            int roll = Random.Range(0, totalWeight);
            int cumulative = 0;
            for (int i = 0; i < table.Length; i++)
            {
                cumulative += table[i].Weight;
                if (roll < cumulative)
                    return table[i].Type;
            }

            return table[table.Length - 1].Type;
        }

        // hunt_twist: a guaranteed encounter, no roll at all - "the hunt finds something other than
        // intended prey" per the spec's own framing, not a contest to fail. Jumps straight past
        // RunHuntResolution's own approach roll entirely, resolving success=true unconditionally
        // (matching the shipped data's own resolution_hunt_twist_01, success:true fixed).
        static void RunHuntTwist(SceneState state)
        {
            state.huntIsTwist = true;
            VoxsVampirism.Log("Scene: hunt_twist triggered - resolving through the twist table, no roll.");
            ShowHuntResolutionBox(state, true, null, -1, -1);
        }

        static void RunHuntResolution(SceneState state)
        {
            OpeningApproach approach = state.pendingApproach;

            DFCareer.Skills skill;
            bool success;
            int baseSkillValue = -1;
            int roll1 = -1;

            if (!TryParseSkill(approach.roll, out skill))
            {
                VoxsVampirism.Log("Scene: hunt approach '{0}' named unrecognized skill '{1}' - treating as a failed attempt.", approach.label, approach.roll);
                success = false;
            }
            else
            {
                baseSkillValue = GetCappedLiveSkillValue(skill);
                int chance = baseSkillValue + state.pendingModifierTotal;

                // Same formula Dice100.SuccessRoll uses internally, rolled manually so the actual
                // value can be shown on the resolution box - see RunResolution's own comment.
                roll1 = Dice100.Roll();
                success = roll1 <= chance;
                GameManager.Instance.PlayerEntity.TallySkill(skill, 1);
            }

            VoxsVampirism.Log("Scene: resolving hunt approach='{0}' roll={1} success={2} baseSkillValue={3} modifierTotal={4} effectiveChance={5}.",
                approach.label, approach.roll, success,
                baseSkillValue >= 0 ? baseSkillValue.ToString() : "n/a",
                state.pendingModifierTotal,
                baseSkillValue >= 0 ? (baseSkillValue + state.pendingModifierTotal).ToString() : "n/a");

            ShowHuntResolutionBox(state, success, approach.roll, baseSkillValue, roll1);
        }

        static void ShowHuntResolutionBox(SceneState state, bool success, string skillName, int baseSkillValue, int roll1)
        {
            FragmentData resolution = FragmentLibraryLoader.SelectHuntResolution(success, state.huntIsTwist, state.locationTag, state.contextTags,
                state.vampirism.GetHumanity(), state.vampirism.GetBloodPoints());
            string text = resolution != null ? SubstituteTokens(resolution.text, state) : DefaultResolutionText(success);
            text = BuildRollSummaryLine(skillName, baseSkillValue, state.pendingModifierTotal, roll1, -1, success) + text;

            VoxsVampirism.Log("Scene: hunt resolution fragment '{0}' (success={1}, isTwist={2}).",
                resolution != null ? resolution.id : "(fallback)", success, state.huntIsTwist);

            // Spawn happens before the box is dismissed (same "mechanical consequence first, box
            // narrates it" ordering RunResolution's own Feast/Stealth/Etiquette branches already use)
            // - nothing feeds automatically, the player fights and drains through the existing corpse
            // economy (HP/divisor blood via CorpseBloodInfoMarker, same as any other wild kill). A
            // failed hunt spawns nothing at all - the cooldown was already consumed at Approach-commit
            // regardless, so a botched roll means waiting out the wild before trying again.
            if (success)
                SpawnHuntEnemy(state.huntIsTwist);

            if (baseSkillValue >= 0)
                PlayDiceSound();
            FeedSceneVoiceController.PlayForId(resolution != null ? resolution.id : null);
            VoxsVampirism.ShowWrappedPromptThenAction(text, () => FeedSceneMusicController.OnSceneEnd());
        }

        // Spawns directly in front of the player, hostile, facing them - the same established
        // CreateEnemy spawn pattern the now-removed HuntAnimalEffect used to use, minus the passive
        // reaction (this is a fight, not a summon).
        static void SpawnHuntEnemy(bool isTwist)
        {
            MobileTypes chosen = PickWeighted(isTwist ? huntTwistTable : huntAnimalTable);

            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            Vector3 spawnPosition = playerTransform.position + playerTransform.forward * 4f;

            GameObject spawned = GameObjectHelper.CreateEnemy(chosen.ToString(), chosen, spawnPosition, MobileGender.Unspecified, null, MobileReactions.Hostile);
            if (spawned != null)
                spawned.transform.LookAt(playerTransform.position);

            VoxsVampirism.Log("Scene: hunt resolved successfully - spawned '{0}' from the {1} table.", chosen, isTwist ? "twist" : "animal");
        }

        #endregion

        #region Token substitution

        static string SubstituteTokens(string text, SceneState state)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Replace("{victim}", VictimDescriptor(state.locationTag));
            text = text.Replace("{building}", BuildingDescriptor(state.locationTag));
            text = text.Replace("{time}", TimeDescriptor());
            return text;
        }

        // No real per-NPC name/profession data exists to draw on (StaticNPC carries no profession
        // field) - a generic descriptor, matching 20+ existing flavor strings elsewhere in this mod
        // that already use "your host"/"your mark" rather than a real name.
        static string VictimDescriptor(string locationTag)
        {
            if (locationTag == SneakFeedController.SceneLocationResidence) return "your host";
            if (locationTag == SneakFeedController.SceneLocationPalace) return "the noble";
            if (locationTag == SneakFeedController.SceneLocationWilderness) return "the traveler";
            return "your mark"; // inn, streets
        }

        static string BuildingDescriptor(string locationTag)
        {
            if (locationTag == SneakFeedController.SceneLocationResidence) return "the house";
            if (locationTag == SneakFeedController.SceneLocationInn) return "the inn";
            if (locationTag == SneakFeedController.SceneLocationPalace) return "the palace";
            if (locationTag == SneakFeedController.SceneLocationWilderness) return "the open country";
            return "the street";
        }

        static string TimeDescriptor()
        {
            int hour = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.Hour;
            return (hour >= 22 || hour < DaggerfallDateTime.DawnHour) ? "the dead of night" : "the night";
        }

        #endregion
    }
}
