// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Subclass of vanilla VampirismEffect (same "Vampirism-Curse" key), registered with
// allowReplacement:true in VoxsVampirism.RegisterCustomEffects so it stands in for the vanilla
// effect everywhere the game looks it up (classic save import, StartQuest, etc.). Implements
// Features 1, 2, 4, 6 and 7 of the mod plus the Humanity system; Features 3, 5 and 8 live in their
// own files and reach into this class only through its small public surface (Feed()/DrinkDry()/
// FeedOnAnimal(), IsSatiated() inherited from vanilla, GetHumanity()/ChangeHumanity()).
//
// Max Health halving (Feature 2) is applied in ConstantEffect() rather than MagicRound(), even
// though the spec describes it alongside the MagicRound-driven stat/skill suppression. This is a
// deliberate deviation: SetMaxHealthLimiter is "constant" state that DaggerfallEntity.
// ClearConstantEffects() wipes back to off every single frame, to be re-asserted by whichever
// effects want it active THAT frame (see vanilla VampirismEffect's own ConstantEffect() override,
// which re-asserts metal/disease/paralysis immunity the same way). Applying the halving only once
// per game-minute in MagicRound would mean it sits wiped-and-off for nearly every rendered frame in
// between. Stat/skill mods (SetStatMod/ChangeStatMod) are NOT frame-cleared, so those stay in
// MagicRound exactly as specified.
//
// BLOOD POINT SYSTEM (0-10, starts at 10 on transformation): replaces the old 4-stage hunger dial
// entirely. A spendable resource - feeding refills it, spells/thralls/healing spend it, and a slow
// passive drain (1 point per 12 in-game hours, independent of feeding events - see nextDrainTime)
// is the SECONDARY pressure, not the main one. 0 points is the new "starving" - frenzy eligibility,
// the Rest window's FullRest/TimedRest lock, and the RestModeGateWatcher gate all key off
// IsBloodEmpty() now instead of a hunger stage. Every spend funnels through TrySpendBlood, which
// refuses (no mutation at all) rather than clamping, so a spell/action either fully happens or not
// at all - never a partial, silently-capped cost.
//
// HUMANITY SYSTEM (0-10, starts at 5 on transformation): rescaled from the old 0-100 system, and
// the whole economy collapsed to exactly two movers - ChangeHumanity(-1) on every murder (DrinkDry,
// a forced kill, murdering your own thrall, draining a human corpse) and ChangeHumanity(+1) from a
// successful Suppress Beast cast (SuppressBeastEffect, still gated by its own 24h cast cooldown).
// The old four-source cooldown-bucket system and the two passive daily drifts are gone entirely -
// there's nothing left for a cooldown to gate now that every source either fires unconditionally
// (murder) or is already self-gated by its own spell cooldown (Suppress Beast). This also closes the
// old "repeat murders inside one cooldown window were Humanity-free" gap for free, with no separate
// fix needed. See ApplyClanBonusScaling (clan bonus multiplier), ApplyDaylightConstantEffect
// (Humanity 10 = no sun/holy damage at all; Humanity 8+ removes the daylight Health penalty;
// Humanity 2- adds a mirrored Fatigue penalty), and CheckFastTravel (Humanity 10 travels by day
// same as a raised hood).
//
// SAVE MIGRATION: a save made before this rewrite has hungerStage (1-4) and humanity (0-100) stored
// under the OLD CustomSaveData_v1 shape, kept below byte-for-byte as a pure deserialization target
// (FullSerializer resolves dataIn to whatever type was actually on disk) - RestoreSaveData maps
// hungerStage 1/2/3/4 -> blood 10/7/4/0 and old humanity -> round(old/10) the first time such a save
// loads, then GetSaveData() only ever writes the new CustomSaveData_v2 shape from then on.

using System.Collections.Generic;
using UnityEngine;
using FullSerializer;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod;
using VoxsVampirismMod.Effects;

namespace DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects
{
    public class VoxsVampirismEffect : VampirismEffect
    {
        static uint NowClassic()
        {
            return DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        }

        const int maxBloodPoints = 10;
        const uint drainIntervalMinutes = 720; // 12 game-hours

        // The pool - see file header. Genuine persisted state (not derived from a timestamp), since
        // it only ever changes through explicit gain/spend calls plus the passive drain tick.
        int bloodPoints = maxBloodPoints;

        // Classic-minutes timestamp of the next scheduled passive-drain tick, advanced by
        // drainIntervalMinutes every tick regardless of whether a point was actually available to
        // drain - a steady, unbroken cadence from whenever it was initialized, not reset by feeding
        // (feeding just adds points directly; there's no "window" left to restart the way the old
        // hunger-stage system needed).
        uint nextDrainTime;

        // One-shot flavor flags (Blood Point spec, A1) - fire once on first crossing into "low" (<=5)
        // or "empty" (0), not on every point change, and both reset the moment blood returns to full
        // so the next real decline announces fresh again.
        bool hasAnnouncedLowBlood;
        bool hasAnnouncedEmptyBlood;

        // Raised on every actual change to bloodPoints (gain, spend, drain, fast-travel cost) so the
        // HUD dot tracker (BloodPointTracker) can redraw on change instead of polling every frame.
        public static event System.Action<int> OnBloodPointsChanged;

        // Humanity - see the file header for the whole system. 5 is the new baseline (was 50/100).
        int humanity = 5;

        // Suppress Beast's own 24h cast cooldown (SuppressBeastEffect) - classic-minutes timestamp
        // of the last successful cast, 0 meaning "never cast". Tracked here (not on the spell effect
        // instance itself) since a fresh IncumbentEffect instance is created per cast and can't carry
        // state across casts on its own the way this class's own persisted fields can.
        uint suppressBeastLastCastTime;

        // Narrative Feeding Scenes spec: classic-minutes timestamp of the last scene-sourced
        // Humanity GAIN (0 = never) - the "gains capped at one per 24 in-game hours" guardrail,
        // same shape as suppressBeastLastCastTime above. Scene-sourced LOSSES are uncapped (matching
        // how murders already work) and need no timestamp of their own. Persisted (not an in-memory-
        // only field) so a save/reload can't be used to bypass the cap.
        uint sceneHumanityGainLastTime;

        // Wilderness Hunt spec: classic-minutes timestamp of the last animal-hunt attempt (0 = never),
        // gating SneakFeedController's own wilderness feed-key trigger - a global, save-persisted
        // cooldown (VoxsVampirism.AnimalHuntCooldownHours, Mod Setting, default 12h), not the per-NPC
        // daily marker feed scenes use, since there's no NPC object here to attach a marker to.
        uint animalHuntLastTime;

        /// <summary>Whether an animal hunt (NarrativeFeedSceneEngine.TryStartAnimalHunt) is allowed to start right now.</summary>
        public bool IsAnimalHuntOnCooldown()
        {
            if (VoxsVampirism.AnimalHuntCooldownHours <= 0)
                return false;

            uint cooldownMinutes = (uint)VoxsVampirism.AnimalHuntCooldownHours * 60;
            return animalHuntLastTime != 0 && NowClassic() - animalHuntLastTime < cooldownMinutes;
        }

        /// <summary>Records a hunt approach actually being rolled (win or lose) - see TryStartAnimalHunt's own header for why this is stamped at approach-commit, not scene start.</summary>
        public void StampAnimalHuntCooldown()
        {
            animalHuntLastTime = NowClassic();
        }

        // Mesmerize Rework spec: classic-minutes timestamp of the last Mesmerize self-cast (0 =
        // never/consumed), gating whether the next townsperson activation opens the Mesmerize command
        // menu (MesmerizeGazeWatcher) instead of normal dialogue. Deliberately NOT added to the
        // persisted save-data structs below - a handful of in-game minutes of "primed" state is not
        // worth surviving a save/reload the way suppressBeastLastCastTime's 24h cooldown is; losing it
        // on reload is unnoticeable and simpler than versioning the save schema for it.
        uint gazePrimedAtTime;
        const uint gazePrimeDurationMinutes = 5; // flagged, adjustable

        // True for gazePrimeDurationMinutes after PrimeGaze() - see MesmerizeEffect.MagicRound() (the
        // cast) and MesmerizeGazeWatcher (the consuming activation). Lazily computed on read - no
        // periodic tick clears this, it simply stops reading true once the window passes.
        public bool IsGazePrimed => gazePrimedAtTime != 0 && NowClassic() - gazePrimedAtTime < gazePrimeDurationMinutes;

        // Re-priming while already primed refreshes the timer (a recast fully resets the window,
        // matching the spec's own "casting again while primed refreshes the timer" requirement) - no
        // special-casing needed, just overwrite the timestamp unconditionally.
        public void PrimeGaze()
        {
            gazePrimedAtTime = NowClassic();
            VoxsVampirism.Log("VoxsVampirismEffect: gaze primed for {0} minute(s).", gazePrimeDurationMinutes);
        }

        public void ConsumeGaze()
        {
            gazePrimedAtTime = 0;
            VoxsVampirism.Log("VoxsVampirismEffect: gaze consumed.");
        }

        public override void Start(EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Start(manager, caster);

            // The single most important diagnostic line this mod logs: if this line is missing from
            // Player.log after a transformation, the "Vampirism-Curse" key resolved to vanilla's own
            // VampirismEffect instead of this subclass (e.g. the RegisterEffectTemplate call in
            // VoxsVampirism.RegisterCustomEffects failed or never ran) - every other feature in this
            // mod (rest gate, daylight, blood, Humanity, the book) lives here and would be silently
            // inert.
            VoxsVampirism.Log("VoxsVampirismEffect.Start() firing - this subclass is now the active vampire curse.");

            // Clan Features spec, §1: vanilla's own AssignPlayerVampireSpells (Levitate/Charm Mortal/
            // Calm Humanoid plus per-clan extras) has already run by the time this fires - strip all
            // of it in one bulk call so this mod's own spell roster is the entire vampire spellset.
            // Charm Mortal/Calm Humanoid are pure redundancy now (Mesmerize already reimplements both
            // combined) and Levitate's traversal niche is already covered by Mist Form.
            GameManager.Instance.PlayerEntity.DeleteTaggedSpells(PlayerEntity.vampireSpellTag);
            VoxsVampirism.Log("Start(): stripped vanilla's tagged vampire spells (tag='{0}').", PlayerEntity.vampireSpellTag);

            bloodPoints = maxBloodPoints;
            nextDrainTime = NowClassic() + drainIntervalMinutes;
            hasAnnouncedLowBlood = false;
            hasAnnouncedEmptyBlood = false;
            humanity = 5;
            suppressBeastLastCastTime = 0;
            sceneHumanityGainLastTime = 0;
            gazePrimedAtTime = 0;

            VoxsVampirism.GrantVampireBook(GameManager.Instance.PlayerEntity);
            BeginClanCeremony();
        }

        // Transformation ceremony (rework of the old bare clan popup). Clan Features spec, §4 still
        // applies: every transformation - book-triggered or a wild bite, no distinction needed - gets
        // this same flow, and the accepted clan simply overwrites whatever vanilla's own region-based
        // lookup produced. What changed: vanilla's own "death is not eternal" popup used to sit on top
        // of a bare, unpaused, enum-ordered clan list with no descriptions and no confirmation. Now
        // the whole sequence is one paced ceremony - this mod's own wake message, then a paused
        // (PauseWhileOpen), alphabetical, Escape-proof (AllowCancel=false; a transformation with no
        // clan chosen is broken state, so backing all the way out isn't offered) clan list, then a
        // per-clan gift description with Accept/Go Back - with the feeding scenes' own heartbeat/
        // music swap (FeedSceneMusicController.OnSceneStart/OnSceneEnd; unscaled-time fades, so the
        // pause never stalls them, and GameManager.PauseGame only touches Time.timeScale, never the
        // AudioListener - both confirmed directly) running underneath until the choice is accepted.
        //
        // Vanilla's popup (TEXT.RSC 401) can't be prevented from here without Harmony -
        // VampirismInfection.DeployFullBlownVampirism pushes it AFTER AssignBundle (and therefore
        // this effect's Start()) returns - and it's actually pushed TWICE: DaggerfallUI.MessageBox(id)
        // calls Show() internally, then DeployFullBlownVampirism calls mb.Show() again on the same
        // instance (both read directly from those files). PushWindow/PopWindow are fully synchronous
        // (OnWindowChange raised inline, confirmed in UserInterfaceManager.cs), so the latch below
        // sees each push the moment it happens: it stays armed for the ceremony's whole duration and
        // closes EVERY DaggerfallMessageBox that tops the stack that this ceremony didn't push itself
        // (logged, defensively capped), disarming only once the player accepts a clan. Accepted
        // tradeoff: another mod's unrelated message box appearing during these few paused seconds
        // would be eaten too.
        const string wakeMessageText =
            "You wake to stone above you and stone below, in a dark that no longer feels like darkness. " +
            "Your heart is still, silence where a pulse should be. And there is a hunger, which has been " +
            "waiting, very patiently, for you to rise again. The blood in your veins is old, and it has a " +
            "name. Choose whose gift you carry.";

        readonly List<IUserInterfaceWindow> ceremonyWindows = new List<IUserInterfaceWindow>();
        System.EventHandler ceremonyLatch;
        int ceremonyBoxesClosed;

        void BeginClanCeremony()
        {
            FeedSceneMusicController.OnSceneStart();

            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            ceremonyBoxesClosed = 0;
            ceremonyLatch = (sender, args) =>
            {
                DaggerfallMessageBox topBox = uiManager.TopWindow as DaggerfallMessageBox;
                if (topBox == null || ceremonyWindows.Contains(topBox))
                    return;
                if (ceremonyBoxesClosed >= 4)
                    return; // defensive cap - see header comment; only vanilla's own two pushes are expected

                ceremonyBoxesClosed++;
                VoxsVampirism.Log("Clan ceremony: closed non-ceremony message box {0} (vanilla's own post-transformation popup).", ceremonyBoxesClosed);
                topBox.CloseWindow();
            };
            uiManager.OnWindowChange += ceremonyLatch;

            ShowWakeMessage();
        }

        // Debug/testing entry point - VoxsVampirismConsoleCommands' own "choose_vamp_clan" reopens
        // just the selection+confirmation portion of the ceremony above, replacing whatever clan is
        // currently set (FinalizeClanChoice below already just overwrites VampireClan unconditionally,
        // same as it does for a genuine first transformation - no special "switching" handling
        // needed). BeginClanCeremony's own wake message is skipped - it's written for someone who has
        // just become a vampire, not one who's been walking around as one for a while - but the same
        // pause+heartbeat treatment is kept for consistency. No vanilla popup exists to eat in this
        // path (nothing re-triggers VampirismInfection), so BeginClanCeremony's own ceremonyLatch
        // machinery for that is skipped entirely; FinalizeClanChoice's null-guard on it is what makes
        // that safe to skip.
        public void ReopenClanSelection()
        {
            FeedSceneMusicController.OnSceneStart();
            ShowClanSelectionMenu();
        }

        void ShowWakeMessage()
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            DaggerfallMessageBox box = new DaggerfallMessageBox(uiManager, uiManager.TopWindow, wrapText: true);
            box.SetText(wakeMessageText);
            box.ClickAnywhereToClose = true;
            box.PauseWhileOpen = true;
            // Escape would CancelWindow() past the OnClose continuation below - with cancel off, the
            // click-anywhere dismissal (a real CloseWindow, which always raises OnClose) is the only
            // way forward, so the ceremony can't be stranded half-done.
            box.AllowCancel = false;
            ceremonyWindows.Add(box);

            // Reuses FeedSceneVoiceController wholesale rather than a second voice mechanism - same
            // AudioSource, same "Feed Scene Voice Acting" Mod Setting gate, same play-stops-whatever-
            // was-playing behavior PlayForId already has. Stopped explicitly on close (not just
            // superseded by a later PlayForId call, since nothing later in the ceremony plays a voice
            // line at all) so it never bleeds into clan selection.
            FeedSceneVoiceController.PlayForId("new_vampire_01");
            box.OnClose += () =>
            {
                FeedSceneVoiceController.Stop();
                ShowClanSelectionMenu();
            };
            box.Show();
        }

        void ShowClanSelectionMenu()
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            TitledListPickerWindow picker = new TitledListPickerWindow(uiManager, uiManager.TopWindow, "Choose Your Clan");
            picker.PauseWhileOpen = true;
            picker.AllowCancel = false;
            ceremonyWindows.Add(picker);

            List<string> clanNames = new List<string>();
            VampireClans[] clans = (VampireClans[])System.Enum.GetValues(typeof(VampireClans));
            for (int i = 0; i < clans.Length; i++)
            {
                if (clans[i] != VampireClans.None)
                    clanNames.Add(clans[i].ToString());
            }
            clanNames.Sort(System.StringComparer.Ordinal);
            for (int i = 0; i < clanNames.Count; i++)
                picker.ListBox.AddItem(clanNames[i]);

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();

                VampireClans chosen;
                if (System.Enum.TryParse(itemString, out chosen))
                    ShowClanConfirmation(chosen);
                else
                    ShowClanSelectionMenu(); // unreachable (items come straight from the enum) - defensive
            };

            uiManager.PushWindow(picker);
        }

        void ShowClanConfirmation(VampireClans clan)
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            DaggerfallMessageBox box = new DaggerfallMessageBox(uiManager, uiManager.TopWindow, wrapText: true);
            box.SetText(string.Format("The blood of clan {0} carries this gift: {1} Will you carry it?",
                clan, GetClanGiftDescription(clan)));
            box.PauseWhileOpen = true;
            ceremonyWindows.Add(box);

            // Yes = accept, No = back to the clan list. Adding buttons already disables Escape-cancel
            // in DaggerfallMessageBox itself, so no stranding path exists here either.
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.No, true);
            box.OnButtonClick += (sender, button) =>
            {
                sender.CloseWindow();
                if (button == DaggerfallMessageBox.MessageBoxButtons.Yes)
                    FinalizeClanChoice(clan);
                else
                    ShowClanSelectionMenu();
            };
            box.Show();
        }

        // Facts sourced from the ReadMe's own Vampire Clans list - keep the two in sync if a clan's
        // gift ever changes.
        static string GetClanGiftDescription(VampireClans clan)
        {
            switch (clan)
            {
                case VampireClans.Anthotis:
                    return "your mind sharpens beyond mortal reach. Gain +20 Intelligence.";
                case VampireClans.Garlythi:
                    return "Blood Surge also wraps you in a Shield.";
                case VampireClans.Haarvenu:
                    return "you master not only flame, but frost, Gain the Vampiric Ice spell.";
                case VampireClans.Khulari:
                    return "while Blood Surge  is active, your weapon strikes carry a one-in-ten chance to Paralyze.";
                case VampireClans.Lyrezi:
                    return "Mist Form also renders you Invisible for as long as you maintain it.";
                case VampireClans.Montalion:
                    return "your will hardens. Gain +10 Willpower.";
                case VampireClans.Selenu:
                    return "while Blood Surge is active, gain immunity to Fire.";
                case VampireClans.Thrafey:
                    return "Quicken Blood restores additioanl Health and Fatigue.";
                case VampireClans.Vraseth:
                    return "While Blood Surge is active, gain a bonus to Running.";
                default:
                    return "its gift is secret.";
            }
        }

        void FinalizeClanChoice(VampireClans clan)
        {
            VampireClan = clan;
            VoxsVampirism.Log("Clan ceremony: player accepted clan '{0}'.", clan);
            DaggerfallUI.AddHUDText(string.Format("The blood of clan {0} now runs in you.", clan));

            // Automatically populates the spellbook on transformation, clan chosen first so a
            // Haarvenu pick also gets its own clan-exclusive Vampiric Ice in the same pass -
            // reading the book is no longer required to actually learn anything. The book's own
            // grant-on-read (VoxsVampirism.OnBookUsed) still works exactly as before, now purely
            // a backup/re-grant path (e.g. recovering a spell the player deliberately deleted).
            VoxsVampirism.GrantSpellsFromBook(GameManager.Instance.PlayerEntity);

            // Ceremony over - disarm the vanilla-box-eating latch, forget the ceremony's own windows,
            // and fade the heartbeat back out to whatever the world's own music wants to be.
            if (ceremonyLatch != null)
            {
                DaggerfallUI.UIManager.OnWindowChange -= ceremonyLatch;
                ceremonyLatch = null;
            }
            ceremonyWindows.Clear();
            FeedSceneMusicController.OnSceneEnd();
        }

        /// <summary>Whether Suppress Beast (SuppressBeastEffect) is allowed to succeed right now.</summary>
        public bool IsSuppressBeastOffCooldown()
        {
            return suppressBeastLastCastTime == 0 || NowClassic() - suppressBeastLastCastTime >= DaggerfallDateTime.MinutesPerDay;
        }

        /// <summary>Records a successful Suppress Beast cast, starting its 24h cooldown.</summary>
        public void StampSuppressBeastCooldown()
        {
            suppressBeastLastCastTime = NowClassic();
        }

        // EntityEffectManager.DoConstantEffects()/DoMagicRound() iterate every active effect bundle
        // on the entity with no try/catch of their own - an uncaught exception from any one effect
        // aborts that whole per-tick foreach, silently skipping every other mod's effect that would
        // otherwise run later in the same tick (confirmed by reading EntityEffectManager.cs directly -
        // there is no per-effect isolation at all). Since this override reaches into more live state
        // per tick than vanilla's own ConstantEffect/MagicRound ever did (PlayerEnterExit, hood state),
        // everything beyond base.ConstantEffect()/base.MagicRound() is wrapped below so a problem here
        // can never take down another mod's effect processing that happens to share this same
        // entity's bundle list.
        public override void ConstantEffect()
        {
            base.ConstantEffect();

            try
            {
                ApplyDaylightConstantEffect();
            }
            catch (System.Exception ex)
            {
                Debug.LogError(VoxsVampirism.LogPrefix + "VoxsVampirismEffect.ApplyDaylightConstantEffect (called from ConstantEffect) threw: " + ex);
            }
        }

        // Logged only when the combination changes (not per-frame) - proof that ConstantEffect is
        // actually running its daylight logic at all, and what it's deciding. Vanilla's own
        // CreateCompoundRace() sets SunDamage/HolyDamage ON unconditionally when the compound race is
        // first created, so if this method were silently never running (e.g. never invoked by the
        // framework for some reason not covered by the try/catch in ConstantEffect above), the
        // symptom would look exactly like "hood/Humanity never stop sun damage and the daylight
        // penalties never apply" - both simultaneously, from vanilla's default going untouched
        // forever. This line is what distinguishes that from a logic bug in the conditions below.
        int lastLoggedDaylightStateHash = int.MinValue;

        // PassiveSpecialsEffect's DamageFromSunlight()/DamageFromHolyPlaces() (vanilla's actual
        // damage ticks) each check TWO independent sources with OR: this class's own race flag
        // (CustomRace.SpecialAbilities, handled below) and a permanent per-character Career flag
        // (DamageFromSunlight / DamageFromHolyPlaces respectively) set at character creation for
        // players who took the matching special disadvantage. DaggerfallUI.cs's own daytime
        // fast-travel gate also checks the Career sun flag directly, independently of (and before)
        // this class's own CheckFastTravel() override. A suppression that only ever touched the race
        // flag would leave a character with either disadvantage still burning (and, for sunlight,
        // still travel-blocked) regardless of hood/Humanity - captured and toggled here the same way,
        // so both vanilla systems see both flags suppressed together.
        //
        // Captured lazily (once, on whichever tick first has a valid entityBehaviour) rather than in
        // Start(), since Start() only fires on a brand new transformation - a save loaded after the
        // curse already exists goes through Resume() instead, which would never populate this if it
        // were only captured in Start(), leaving the career flags suppressed forever with nothing to
        // restore them to. Capturing on first tick works for both paths.
        bool originalCareerDamageFromSunlight;
        bool originalCareerDamageFromHolyPlaces;
        bool capturedOriginalCareerFlag;

        void ApplyDaylightConstantEffect()
        {
            DaggerfallEntityBehaviour entityBehaviour = GetPeeredEntityBehaviour(manager);
            if (!entityBehaviour)
                return;

            if (!capturedOriginalCareerFlag)
            {
                originalCareerDamageFromSunlight = entityBehaviour.Entity.Career.DamageFromSunlight;
                originalCareerDamageFromHolyPlaces = entityBehaviour.Entity.Career.DamageFromHolyPlaces;
                capturedOriginalCareerFlag = true;
            }

            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            bool inSunlight = playerEnterExit != null && playerEnterExit.IsPlayerInSunlight;
            bool hooded = HoodController.IsHoodRaised;
            bool fullyHuman = humanity >= 10;

            // Hood stops the burning outright (both flags); at Humanity 10 so does the condition
            // alone, with no cloak needed at all - it does not touch the clan-bonus tradeoff, which
            // lives in ApplyClanBonusScaling instead.
            bool sunDamageBypassed = hooded || fullyHuman;
            bool sunDamageFlagSet = inSunlight && !sunDamageBypassed;

            if (sunDamageFlagSet)
                CustomRace.SpecialAbilities |= DFCareer.SpecialAbilityFlags.SunDamage;
            else
                CustomRace.SpecialAbilities &= ~DFCareer.SpecialAbilityFlags.SunDamage;

            SetCareerSunDamageSuppressed(entityBehaviour, sunDamageBypassed);

            // Holy-place damage is never gated on hood - the book's own "On the Sun" chapter frames
            // the hood purely as a sunlight/cloak mechanism, and holy-place danger has always been a
            // permanent condition of the curse in vanilla (no cloak trick helps there either). Only
            // Humanity 10 erases it, matching the "no sun damage, no holy-place damage... this
            // doesn't require a cloak or hood at all" endpoint description.
            if (fullyHuman)
                CustomRace.SpecialAbilities &= ~DFCareer.SpecialAbilityFlags.HolyDamage;
            else
                CustomRace.SpecialAbilities |= DFCareer.SpecialAbilityFlags.HolyDamage;

            SetCareerHolyDamageSuppressed(entityBehaviour, fullyHuman);

            // Daylight Health penalty - removed entirely at Humanity 8 and above.
            if (inSunlight && humanity < 8)
                entityBehaviour.Entity.SetMaxHealthLimiter(entityBehaviour.Entity.RawMaxHealth / 2);

            // Daylight Fatigue penalty - only at Humanity 2 and below, mirroring the Health
            // penalty's own magnitude (a straight halving). DFU has no direct "MaxFatigueLimiter"
            // primitive the way SetMaxHealthLimiter exists for Health (MaxFatigue is purely computed
            // from LiveStrength/LiveEndurance, nothing settable/limitable sits in front of it), so
            // this instead clamps CurrentFatigue directly each tick - a continuous cap with the same
            // practical effect as SetMaxHealthLimiter's own continuous capping of CurrentHealth.
            if (inSunlight && humanity <= 2)
            {
                int cappedFatigue = entityBehaviour.Entity.MaxFatigue / 2;
                if (entityBehaviour.Entity.CurrentFatigue > cappedFatigue)
                    entityBehaviour.Entity.CurrentFatigue = cappedFatigue;
            }

            LogDaylightStateIfChanged(inSunlight, hooded, fullyHuman, sunDamageFlagSet);
        }

        bool? lastLoggedCareerSunSuppressed;

        void SetCareerSunDamageSuppressed(DaggerfallEntityBehaviour entityBehaviour, bool suppress)
        {
            entityBehaviour.Entity.Career.DamageFromSunlight = suppress ? false : originalCareerDamageFromSunlight;

            if (lastLoggedCareerSunSuppressed != suppress)
            {
                lastLoggedCareerSunSuppressed = suppress;
                VoxsVampirism.Log("Career.DamageFromSunlight suppressed={0} (original character choice was {1})", suppress, originalCareerDamageFromSunlight);
            }
        }

        bool? lastLoggedCareerHolySuppressed;

        void SetCareerHolyDamageSuppressed(DaggerfallEntityBehaviour entityBehaviour, bool suppress)
        {
            entityBehaviour.Entity.Career.DamageFromHolyPlaces = suppress ? false : originalCareerDamageFromHolyPlaces;

            if (lastLoggedCareerHolySuppressed != suppress)
            {
                lastLoggedCareerHolySuppressed = suppress;
                VoxsVampirism.Log("Career.DamageFromHolyPlaces suppressed={0} (original character choice was {1})", suppress, originalCareerDamageFromHolyPlaces);
            }
        }

        void LogDaylightStateIfChanged(bool inSunlight, bool hooded, bool fullyHuman, bool sunDamageFlagSet)
        {
            int hash = (inSunlight ? 4 : 0) | (hooded ? 2 : 0) | (sunDamageFlagSet ? 1 : 0) | (fullyHuman ? 8 : 0);
            if (hash == lastLoggedDaylightStateHash)
                return;

            lastLoggedDaylightStateHash = hash;
            VoxsVampirism.Log("Daylight state changed: inSunlight={0} hooded={1} fullyHuman={2} sunDamageFlagSet={3} compoundRace.SpecialAbilities={4}",
                inSunlight, hooded, fullyHuman, sunDamageFlagSet, CustomRace.SpecialAbilities);
        }

        public override void MagicRound()
        {
            base.MagicRound();

            try
            {
                ApplyDaylightMagicRound();
            }
            catch (System.Exception ex)
            {
                Debug.LogError(VoxsVampirism.LogPrefix + "VoxsVampirismEffect.ApplyDaylightMagicRound (called from MagicRound) threw: " + ex);
            }

            try
            {
                ApplyBloodDrain();
            }
            catch (System.Exception ex)
            {
                Debug.LogError(VoxsVampirism.LogPrefix + "VoxsVampirismEffect.ApplyBloodDrain (called from MagicRound) threw: " + ex);
            }
        }

        bool? lastLoggedTradeoffActive;

        void ApplyDaylightMagicRound()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            bool inSunlight = playerEnterExit != null && playerEnterExit.IsPlayerInSunlight;

            // Suppress Beast (SuppressBeastEffect) forces this same tradeoff for its own 24-hour
            // duration regardless of real daylight state - "the vampire does not regain clan
            // attribute/skill bonuses in the dark" for as long as it's active.
            bool suppressed = inSunlight || manager.FindIncumbentEffect<SuppressBeastEffect>() != null;

            ApplyClanBonusScaling(suppressed);

            if (lastLoggedTradeoffActive != suppressed)
            {
                lastLoggedTradeoffActive = suppressed;
                VoxsVampirism.Log("MagicRound daylight tradeoff (attribute/skill bonus scaling) active={0} (inSunlight={1}) humanity={2}", suppressed, inSunlight, humanity);
            }
        }

        // Per the current design, combat no longer advances blood - only the explicit Feed/DrinkDry/
        // FeedOnAnimal actions do. base.OnWeaponHitEntity() still runs, so a combat hit still satiates
        // VANILLA's own binary satiation gate (UpdateSatiation()) - Feature 1's rest gating, which
        // reads that vanilla state directly, is unaffected by this.
        const int khulariParalysisChancePercent = 10;

        public override void OnWeaponHitEntity(PlayerEntity playerEntity, DaggerfallEntity targetEntity = null)
        {
            base.OnWeaponHitEntity(playerEntity, targetEntity);
            ApplyKhulariParalysisProc(targetEntity);
        }

        // Khulari clan feature (Clan Features spec, §2): while Blood Surge is active, weapon hits have
        // a 10% chance to inflict Paralysis - attaches vanilla's own Paralyze effect directly, the
        // same AssignBundle(..., BypassSavingThrows) technique MistFormEffect.
        // EnsureMistFormOpenActive() already established for attaching an internal helper effect
        // without going through the spellbook/casting system at all.
        void ApplyKhulariParalysisProc(DaggerfallEntity targetEntity)
        {
            if (VampireClan != VampireClans.Khulari || targetEntity == null)
                return;
            if (manager.FindIncumbentEffect<BloodSurgeEffect>() == null)
                return;
            if (!Dice100.SuccessRoll(khulariParalysisChancePercent))
                return;

            DaggerfallEntityBehaviour targetBehaviour = targetEntity.EntityBehaviour;
            EntityEffectManager targetManager = targetBehaviour != null ? targetBehaviour.GetComponent<EntityEffectManager>() : null;
            if (targetManager == null)
                return;

            // ChanceBase/ChancePerLevel must be nonzero even though the roll above already decided
            // this fires - this mod's own established div-by-zero rule (a Support* property always
            // needs its matching *PerLevel nonzero, or FormulaHelper/third-party cost mods divide by
            // zero the moment the spellbook is opened).
            EffectSettings paralyzeSettings = new EffectSettings
            {
                DurationBase = 1,
                DurationPlus = 0,
                DurationPerLevel = 1,
                ChanceBase = 100,
                ChancePlus = 0,
                ChancePerLevel = 1,
            };
            EffectEntry paralyzeEntry = new EffectEntry { Key = Paralyze.EffectKey, Settings = paralyzeSettings };
            EffectBundleSettings bundleSettings = new EffectBundleSettings
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.SingleTargetAtRange,
                ElementType = ElementTypes.Magic,
                Name = "Khulari-Paralysis",
                Effects = new EffectEntry[] { paralyzeEntry },
            };

            EntityEffectBundle bundle = new EntityEffectBundle(bundleSettings, manager.EntityBehaviour);
            targetManager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);
            VoxsVampirism.Log("Khulari proc: paralyzed '{0}'.", targetEntity.Name);
        }

        // Feature 1: the top-level satiation gate on opening the Rest window is removed entirely -
        // FullRest/TimedRest are gated instead by RestModeGateWatcher, which polls for those two
        // specific modes being chosen, leaving Loiter reachable regardless of satiation.
        public override bool CheckStartRest(PlayerEntity playerEntity)
        {
            return true;
        }

        // Feature 2 / Humanity endpoint: hood raised or Humanity 10 both let a vampire travel by
        // day; absent either, fall back to vanilla's own popup-and-block behavior.
        public override bool CheckFastTravel(PlayerEntity playerEntity)
        {
            if (HoodController.IsHoodRaised || humanity >= 10)
                return true;

            return base.CheckFastTravel(playerEntity);
        }

        // Feature 4: when enabled, fall back to the standard racial head (gender/FaceIndex based)
        // instead of the vampire head texture.
        public override bool GetCustomHeadImageData(PlayerEntity entity, out ImageData imageDataOut)
        {
            if (VoxsVampirism.KeepOriginalHeadOnPaperDoll)
            {
                imageDataOut = new ImageData();
                return false;
            }

            return base.GetCustomHeadImageData(entity, out imageDataOut);
        }

        // Lazily loaded (rather than at Start()) since the mod's asset bundle is available well
        // before any feed actually happens, and this way a missing/failed load never blocks a feed
        // itself - just silently skips the sound, same defensive shape as VoxsNecromancy's own
        // lazily-loaded sound clips.
        static AudioClip feedingSound;

        /// <summary>
        /// Sates vanilla thirst (UpdateSatiation), grants +3 blood, applies the shared feed side
        /// effects (heal, sound). Humanity-neutral - Charm/Stealth feeding is a neutral choice by
        /// design. Called by SneakFeedController for a normal Charm/Stealth feed AND for a forced
        /// stage-4-style kill (the caller applies ChangeHumanity(-1) itself in that case, since this
        /// method has no way to know the feed turned lethal after the fact).
        /// </summary>
        public void Feed()
        {
            UpdateSatiation();
            GainBlood(3);
            ApplyFeedSideEffects(0.1f);
        }

        /// <summary>
        /// Sates vanilla thirst, refills blood to full, applies the shared feed side effects at a
        /// stronger 30% heal (matching the greater risk of the kill this represents), and always
        /// costs 1 Humanity. Used by SneakFeedController's Feast action and thrall-murder, which
        /// kill the victim in exchange for this stronger reset - every other human feed path uses
        /// Feed() instead.
        /// </summary>
        public void DrinkDry()
        {
            UpdateSatiation();
            GainBloodFull();
            ApplyFeedSideEffects(0.3f);
            ChangeHumanity(-1);
        }

        /// <summary>
        /// Feeding on a LIVE passive animal or non-hostile creature - grants a flat +1 blood
        /// (Blood Economy Rebalance, §2), Humanity-neutral, regardless of the creature's HP. Corpse
        /// draining is a separate, stronger act for anything the mod can classify (GainBloodNoHumanity
        /// with the HP/divisor formula, via SneakFeedController.DoDrainWildCorpse) - taking only what
        /// you need from something alive is restraint; a corpse can be drained to the last drop. Still
        /// used as the flat fallback for a corpse with no CorpseBloodInfoMarker at all (see
        /// SneakFeedController.DoDrainAnimalCorpse).
        /// </summary>
        public void FeedOnAnimal()
        {
            UpdateSatiation();
            GainBlood(1);
            ApplyFeedSideEffects(0.1f);
        }

        /// <summary>
        /// Same mechanics as DrinkDry() (full blood refill, 30% heal) but WITHOUT any Humanity
        /// change - used for draining a rival vampire/vampire-ancient's corpse (SneakFeedController's
        /// fights-back event): vampire blood is potent but it isn't human.
        /// </summary>
        public void DrinkDryNoHumanity()
        {
            UpdateSatiation();
            GainBloodFull();
            ApplyFeedSideEffects(0.3f);
        }

        /// <summary>
        /// Same mechanics as Feed() (satiate, heal/sound) but for an arbitrary blood amount and
        /// WITHOUT any Humanity change - used by ThrallManager for feeding on the player's own
        /// thralls/created vampires (+3 / +6 respectively), which the Humanity system's thrall
        /// extension never touches either way.
        /// </summary>
        public void GainBloodNoHumanity(int amount)
        {
            UpdateSatiation();
            GainBlood(amount);
            ApplyFeedSideEffects(0.1f);
        }

        // Shared by Feed()/DrinkDry()/FeedOnAnimal()/GainBloodNoHumanity() - heals healFraction of
        // max Health/Fatigue and plays the feeding sound. Only the heal amount differs between
        // callers, so only that is parameterized.
        void ApplyFeedSideEffects(float healFraction)
        {
            DaggerfallEntityBehaviour entityBehaviour = GetPeeredEntityBehaviour(manager);
            if (entityBehaviour != null)
            {
                DaggerfallEntity entity = entityBehaviour.Entity;
                entity.CurrentHealth += Mathf.RoundToInt(entity.MaxHealth * healFraction);
                entity.CurrentFatigue += Mathf.RoundToInt(entity.MaxFatigue * healFraction);
            }

            if (feedingSound == null)
                feedingSound = VoxsVampirism.Mod.GetAsset<AudioClip>("feeding.ogg");
            if (feedingSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(feedingSound, DaggerfallUnity.Settings.SoundVolume);

            FeedFlashVisuals.Trigger();
        }

        // Replaces the old always-either-full-bonus-or-zero SuppressVampireClanBonuses. Vanilla's own
        // ApplyVampireAdvantages() (private, called from base.MagicRound() just before this class's
        // own override regains control) unconditionally SetStatMod's every clan bonus to a fixed +20
        // (stats) / +30 (skills) every single round, clan-independent except Anthotis's own +20
        // Intelligence - so re-calling SetStatMod here with a scaled value safely overwrites whatever
        // vanilla (or a previous daylight/Humanity pass this same tick) just set, with no need to
        // read anything back first.
        //
        // Two regimes:
        //   - Not in daylight: base bonus scaled by GetClanBonusMultiplier() - tapers linearly from
        //     1.0x at Humanity 5 down to exactly 0 at Humanity 10 (a fully-10 vampire keeps none of
        //     the clan bonus, day or night - the real cost of full sun/holy immunity), and rises to
        //     1.5x at Humanity 0 (a beast-leaning vampire is stronger by night than the baseline).
        //   - In daylight: the existing zero-out stays exactly as before for Humanity 5 and above;
        //     below 5 it becomes a real NEGATIVE debuff instead of a mere absence of bonus, scaling
        //     linearly to -20 at Humanity 0. Uniform value for both stats and skills regardless of
        //     their different base magnitudes (20 vs 30), matching the spec's own single formula.
        void ApplyClanBonusScaling(bool inSunlight)
        {
            const int baseStatBonus = 20;
            const int baseSkillBonus = 30;
            // Montalion clan feature (Clan Features spec, §2): +10 Willpower, run through the exact
            // same multiplier/sunlight-penalty shape as every other clan-bonus stat for consistency
            // (weaker at high Humanity, stronger at low, zero at Humanity 10) - half of baseStatBonus,
            // so its own sunlight penalty is proportionally half as steep too (-2/point vs -4/point).
            const int montalionStatBonus = 10;

            int statValue;
            int skillValue;
            int montalionValue;

            if (inSunlight)
            {
                int penalty = humanity < 5 ? -4 * (5 - humanity) : 0;
                statValue = penalty;
                skillValue = penalty;
                montalionValue = humanity < 5 ? -2 * (5 - humanity) : 0;
            }
            else
            {
                float multiplier = GetClanBonusMultiplier();
                statValue = Mathf.RoundToInt(baseStatBonus * multiplier);
                skillValue = Mathf.RoundToInt(baseSkillBonus * multiplier);
                montalionValue = Mathf.RoundToInt(montalionStatBonus * multiplier);
            }

            SetStatMod(DFCareer.Stats.Strength, statValue);
            SetStatMod(DFCareer.Stats.Willpower, statValue);
            SetStatMod(DFCareer.Stats.Agility, statValue);
            SetStatMod(DFCareer.Stats.Endurance, statValue);
            SetStatMod(DFCareer.Stats.Personality, statValue);
            SetStatMod(DFCareer.Stats.Speed, statValue);
            SetStatMod(DFCareer.Stats.Luck, statValue);
            if (VampireClan == VampireClans.Anthotis)
                SetStatMod(DFCareer.Stats.Intelligence, statValue);
            // Additive on top of the universal +20/penalty Willpower already set above - ChangeStatMod
            // stacks rather than overwriting, same as the blood-drain Willpower/Personality penalties
            // further down this file already rely on.
            if (VampireClan == VampireClans.Montalion)
                ChangeStatMod(DFCareer.Stats.Willpower, montalionValue);

            SetSkillMod(DFCareer.Skills.Jumping, skillValue);
            SetSkillMod(DFCareer.Skills.Running, skillValue);
            SetSkillMod(DFCareer.Skills.Stealth, skillValue);
            SetSkillMod(DFCareer.Skills.CriticalStrike, skillValue);
            SetSkillMod(DFCareer.Skills.Climbing, skillValue);
            SetSkillMod(DFCareer.Skills.HandToHand, skillValue);
        }

        // H10 is special-cased to exactly 0 rather than trusting the formula to land there, since
        // (10-10)/5 would give exactly 0f anyway here but this guards against any future tweak to
        // the formula silently leaving a nonzero sliver at the advertised "gone entirely" endpoint.
        float GetClanBonusMultiplier()
        {
            if (humanity >= 10)
                return 0f;
            if (humanity >= 5)
                return (10 - humanity) / 5f;

            return 1f + ((5 - humanity) / 10f);
        }

        // Willpower/Personality penalties (Blood Point spec, A4) run after ApplyClanBonusScaling
        // above, so ChangeStatMod stacks additively on top of whichever base/scaled/negative value
        // that just set - a low-blood, low-Humanity vampire caught outdoors in daylight is hit by
        // all of these independently, not just one.
        //
        // Willpower: -(10 - bloodPoints), 0 at full down to -10 empty. Personality: (bloodPoints*3)-20,
        // +10 at full down to -20 empty - deliberately linear replacements for the old 4-stage curves.
        void ApplyBloodDrain()
        {
            uint currentTime = NowClassic();
            while (currentTime >= nextDrainTime)
            {
                nextDrainTime += drainIntervalMinutes;
                if (bloodPoints > 0)
                    ChangeBloodPoints(-1);
            }

            int willpowerPenalty = -(maxBloodPoints - bloodPoints);
            ChangeStatMod(DFCareer.Stats.Willpower, willpowerPenalty);

            int personalityMod = (bloodPoints * 3) - 20;
            ChangeStatMod(DFCareer.Stats.Personality, personalityMod);
        }

        /// <summary>Current blood points, 0-10.</summary>
        public int GetBloodPoints()
        {
            return bloodPoints;
        }

        /// <summary>Whether blood is fully spent (0) - the new "starving" state.</summary>
        public bool IsBloodEmpty()
        {
            return bloodPoints <= 0;
        }

        /// <summary>Whether blood is at or below 2 - the forced-kill gate on a successful feed.</summary>
        public bool IsBloodLow()
        {
            return bloodPoints <= 2;
        }

        /// <summary>
        /// The one spend gate every blood-costing spell/action calls. Refuses (no mutation at all,
        /// returns false) if the pool can't cover the cost - never partially spends or clamps below
        /// zero. Callers are expected to check this BEFORE any Magicka charge or other side effect,
        /// per the spec's own "refuses before any spend" rule.
        /// </summary>
        public bool TrySpendBlood(int amount)
        {
            if (amount <= 0)
                return true;
            if (bloodPoints < amount)
                return false;

            ChangeBloodPoints(-amount);
            return true;
        }

        /// <summary>
        /// Desperate casting (Blood Economy Rebalance, §5): the payment gate every blood-costing
        /// spell/action now uses instead of TrySpendBlood's outright refusal. Never refuses - spends
        /// whatever blood is available, then reports how many points were still missing as a percent
        /// of max HP (VoxsVampirism.DesperateCastingHpPercent per missing point) for the caller to
        /// apply as direct Health damage. A vampire with nothing left to spend pays in their own
        /// vitae instead of being turned away.
        /// </summary>
        public void PayBloodOrHealth(int bloodCost, out int bloodSpent, out int hpPercentOwed)
        {
            bloodCost = Mathf.Max(bloodCost, 0);
            bloodSpent = Mathf.Min(bloodCost, bloodPoints);
            int missing = bloodCost - bloodSpent;
            if (bloodSpent > 0)
                ChangeBloodPoints(-bloodSpent);

            // Capped at 95 (full mod audit): missing * DesperateCastingHpPercent has no natural
            // ceiling - at the setting's own slider maximum (50%), the 3-blood Post-Feed Offer
            // accepted at 0 blood came out to 150% of max HP, an unconditional kill no matter the
            // player's current Health. Desperate casting is meant to be able to kill a player who
            // gambles at LOW Health, never to be a guaranteed death sentence from full - 95 keeps the
            // worst case survivable-from-full while still nearly lethal.
            hpPercentOwed = Mathf.Min(95, missing * VoxsVampirismMod.VoxsVampirism.DesperateCastingHpPercent);
        }

        /// <summary>Adds blood, clamped at 10. Used by every feed source except a full-refill kill.</summary>
        public void GainBlood(int amount)
        {
            if (amount <= 0)
                return;

            ChangeBloodPoints(amount);
        }

        /// <summary>Refills blood to exactly 10 - the Feast/rival-vampire-corpse "full refill" case.</summary>
        public void GainBloodFull()
        {
            ChangeBloodPoints(maxBloodPoints - bloodPoints);
        }

        // Core mutator every gain/spend/drain funnels through - clamps to [0, maxBloodPoints], fires
        // OnBloodPointsChanged only on an actual change, and reports the one-shot threshold flavor.
        void ChangeBloodPoints(int delta)
        {
            int oldValue = bloodPoints;
            bloodPoints = Mathf.Clamp(bloodPoints + delta, 0, maxBloodPoints);
            if (bloodPoints == oldValue)
                return;

            if (OnBloodPointsChanged != null)
                OnBloodPointsChanged(bloodPoints);

            ReportBloodFlavor();
        }

        // One-shot flavor lines (Blood Point spec, A1) - first dropping to 5 or below, first
        // reaching 0, and returning to full each get exactly one line, not one per point change.
        void ReportBloodFlavor()
        {
            if (bloodPoints >= maxBloodPoints)
            {
                if (hasAnnouncedLowBlood || hasAnnouncedEmptyBlood)
                    DaggerfallUI.AddHUDText("Your blood runs full and strong again.");
                hasAnnouncedLowBlood = false;
                hasAnnouncedEmptyBlood = false;
                return;
            }

            if (bloodPoints <= 0 && !hasAnnouncedEmptyBlood)
            {
                hasAnnouncedEmptyBlood = true;
                DaggerfallUI.AddHUDText("You have nothing left to spend - you are starving.");
            }
            else if (bloodPoints <= 5 && !hasAnnouncedLowBlood)
            {
                hasAnnouncedLowBlood = true;
                DaggerfallUI.AddHUDText("Your blood is thinning - you should feed soon.");
            }
        }

        /// <summary>
        /// Blood cost for a fast-travel trip (FastTravelTimeBridge) - clamps at 0 rather than
        /// refusing (fast travel always proceeds; you just arrive starving if you couldn't afford
        /// it), and shows its own distinct arrival flavor only when it actually clamped.
        /// </summary>
        public void SpendBloodForFastTravel(int cost)
        {
            if (cost <= 0)
                return;

            int oldValue = bloodPoints;
            bloodPoints = Mathf.Max(0, bloodPoints - cost);
            if (bloodPoints == oldValue)
                return;

            if (OnBloodPointsChanged != null)
                OnBloodPointsChanged(bloodPoints);

            if (bloodPoints <= 0)
            {
                hasAnnouncedEmptyBlood = true;
                DaggerfallUI.AddHUDText("The road was long, and there was nothing to hunt - you arrive starving.");
            }

            VoxsVampirism.Log("Fast travel blood cost: {0} (blood {1} -> {2}).", cost, oldValue, bloodPoints);
        }

        /// <summary>Directly sets blood points (clamped [0,10]), for the "setblood" console command.</summary>
        public void SetBloodPoints(int value)
        {
            int clamped = Mathf.Clamp(value, 0, maxBloodPoints);
            ChangeBloodPoints(clamped - bloodPoints);
        }

        /// <summary>Current Humanity, 0-10. Used by HungerStatusInfoBox and console diagnostics.</summary>
        public int GetHumanity()
        {
            return humanity;
        }

        /// <summary>
        /// Directly sets Humanity to an exact value (clamped to [0, 10]), for the "sethumanity"
        /// console command - routes through ChangeHumanity so the same status-display/change-message
        /// behavior any other Humanity shift gets applies here too.
        /// </summary>
        public void SetHumanity(int value)
        {
            int clamped = Mathf.Clamp(value, 0, 10);
            ChangeHumanity(clamped - humanity);
        }

        /// <summary>
        /// Low-level Humanity change - clamps to [0, 10] and always shows a HUD message. Public so
        /// Suppress Beast (SuppressBeastEffect) can grant its own +1 directly, and so
        /// SneakFeedController/ThrallManager can apply the flat -1 murder penalty at every call site
        /// that represents a killing (DrinkDry already calls this itself; a forced kill or a human
        /// corpse drain call it explicitly after their own Feed()).
        /// </summary>
        public void ChangeHumanity(int delta)
        {
            if (delta == 0)
                return;

            int oldHumanity = humanity;
            humanity = Mathf.Clamp(humanity + delta, 0, 10);
            if (humanity == oldHumanity)
                return;

            DaggerfallUI.AddHUDText(GetHumanityChangeMessage(humanity - oldHumanity, humanity));
            VoxsVampirism.Log("Humanity changed by {0} to {1}.", humanity - oldHumanity, humanity);

            // Thrall system: rising Humanity shrinks the thrall cap band-by-band - ThrallManager
            // itself decides whether any band boundary was actually crossed and frees the oldest
            // thrall(s) if so. No-ops immediately on a falling/unchanged Humanity.
            ThrallManager.OnHumanityChanged(oldHumanity, humanity);
        }

        /// <summary>
        /// Narrative Feeding Scenes spec: the guardrail on a scene-choice Humanity GAIN - at most one
        /// per 24 in-game hours, same suppressBeastLastCastTime/NowClassic()/MinutesPerDay shape
        /// IsSuppressBeastOffCooldown already uses, so nightly scene-play can't outpace Suppress
        /// Beast's own deliberate 1/day climb. Returns false (no state change at all, not even a
        /// blocked-attempt stamp) if the cap is still active, so the caller can show its own "the
        /// moment passes through you, changing nothing" fallback narration instead of the real gain
        /// text - true means the gain actually applied and ChangeHumanity's own HUD message already
        /// fired.
        /// </summary>
        public bool TryApplySceneHumanityGain()
        {
            uint now = NowClassic();
            if (sceneHumanityGainLastTime != 0 && now - sceneHumanityGainLastTime < DaggerfallDateTime.MinutesPerDay)
                return false;

            sceneHumanityGainLastTime = now;
            ChangeHumanity(1);
            return true;
        }

        /// <summary>
        /// Narrative Feeding Scenes spec: a scene-choice Humanity LOSS - uncapped, matching how every
        /// other Humanity-losing action (a murder, a forced kill, a human corpse drain) already
        /// works. Kept as its own method (rather than callers using ChangeHumanity(-1) directly) so
        /// every scene-effect call site reads the same "gain vs loss" shape.
        /// </summary>
        public void ApplySceneHumanityLoss()
        {
            ChangeHumanity(-1);
        }

        // Simplified to two flavor lines (gain/loss) now that every real change moves Humanity by
        // exactly 1 at a time - the old "large vs small" distinction had nothing left to distinguish.
        static string GetHumanityChangeMessage(int actualDelta, int newHumanity)
        {
            string flavor = actualDelta > 0
                ? "A flicker of your old self returns."
                : "Your humanity wanes.";

            return string.Format("{0} (Humanity: {1}/10)", flavor, newHumanity);
        }

        // Called by FastTravelTimeBridge after a fast travel completes - shifts the passive-drain
        // timestamp forward by the exact delta the world clock just jumped, so the drain loop above
        // doesn't try to "catch up" a jump's worth of 12-hour ticks all at once - SpendBloodForFastTravel's
        // own flat per-distance charge is what represents that trip's cost instead. Humanity has
        // nothing passive left to shift (its only sources are murder and Suppress Beast, both
        // already action-triggered, not time-based).
        public void ShiftTimersForFastTravel(uint delta)
        {
            if (delta == 0)
                return;

            nextDrainTime += delta;
            VoxsVampirism.Log("Fast travel: shifted blood drain timer forward by {0} classic minutes to cancel out the time-compression jump.", delta);
        }

        // A save made before this rewrite has its effect data stored as the OLD CustomSaveData_v1
        // shape (hungerStage 1-4, humanity 0-100) - kept below completely unchanged so FullSerializer
        // can still deserialize it; RestoreSaveData below maps it into the new fields once, then
        // GetSaveData() only ever writes CustomSaveData_v2 from that point on.
        [fsObject("v1")]
        public new struct CustomSaveData_v1
        {
            public object baseData;
            public int hungerStage;
            public uint currentStageStartTime;
            public int lastAnnouncedHungerStage;
            public int? humanity;
            public int animalFeedsSinceLastStageDrop;
            public uint[] humanityCooldownLastShift;
            public uint stage4WindowStart;
            public uint stage1WindowStart;
            public uint suppressBeastLastCastTime;
            public ThrallManager.ThrallRecord_v1[] thralls;
        }

        // Pure migration target now (like CustomSaveData_v1 above) - kept byte-for-byte so
        // FullSerializer can still deserialize a save written before the Narrative Feeding Scenes
        // save bump. GetSaveData() only ever writes CustomSaveData_v3 from here on.
        [fsObject("v2")]
        public struct CustomSaveData_v2
        {
            public object baseData;
            public int bloodPoints;
            public uint nextDrainTime;
            public bool hasAnnouncedLowBlood;
            public bool hasAnnouncedEmptyBlood;
            public int humanity;
            public uint suppressBeastLastCastTime;
            public ThrallManager.ThrallRecord_v1[] thralls;
        }

        // Narrative Feeding Scenes spec: adds sceneHumanityGainLastTime on top of every CustomSaveData_v2
        // field, so the scene-gain 24h cap survives a save/reload instead of being farmable by
        // reloading. Pure migration target now (like v1/v2 above) - kept byte-for-byte so FullSerializer
        // can still deserialize a save written before the Wilderness Hunt save bump. GetSaveData() only
        // ever writes CustomSaveData_v4 from here on.
        [fsObject("v3")]
        public struct CustomSaveData_v3
        {
            public object baseData;
            public int bloodPoints;
            public uint nextDrainTime;
            public bool hasAnnouncedLowBlood;
            public bool hasAnnouncedEmptyBlood;
            public int humanity;
            public uint suppressBeastLastCastTime;
            public uint sceneHumanityGainLastTime;
            public ThrallManager.ThrallRecord_v1[] thralls;
        }

        // Wilderness Hunt spec: adds animalHuntLastTime on top of every CustomSaveData_v3 field, so the
        // hunt cooldown survives a save/reload instead of being farmable by reloading. Pure migration
        // target now (like v1/v2/v3 above) - kept byte-for-byte so FullSerializer can still deserialize
        // a save written before the Dominate-persistence save bump. GetSaveData() only ever writes
        // CustomSaveData_v5 from here on.
        [fsObject("v4")]
        public struct CustomSaveData_v4
        {
            public object baseData;
            public int bloodPoints;
            public uint nextDrainTime;
            public bool hasAnnouncedLowBlood;
            public bool hasAnnouncedEmptyBlood;
            public int humanity;
            public uint suppressBeastLastCastTime;
            public uint sceneHumanityGainLastTime;
            public uint animalHuntLastTime;
            public ThrallManager.ThrallRecord_v1[] thralls;
        }

        // Dominate save-load fix: adds dominates on top of every CustomSaveData_v4 field, so a
        // dominated creature's binding (timer, original team, upkeep - keyed by the enemy's own DFU
        // LoadID) survives a save/reload instead of leaving a passive allied husk - see
        // ThrallManager's own Dominate-persistence region for the whole mechanism.
        [fsObject("v5")]
        public struct CustomSaveData_v5
        {
            public object baseData;
            public int bloodPoints;
            public uint nextDrainTime;
            public bool hasAnnouncedLowBlood;
            public bool hasAnnouncedEmptyBlood;
            public int humanity;
            public uint suppressBeastLastCastTime;
            public uint sceneHumanityGainLastTime;
            public uint animalHuntLastTime;
            public ThrallManager.ThrallRecord_v1[] thralls;
            public ThrallManager.DominateRecord_v1[] dominates;
            // Added alongside the drained-corpse persistence fix (explicit exploit report: save-then-
            // reload let a corpse be drained again) - the DFU LoadIDs of every corpse already drained,
            // see SneakFeedController's own drained-corpse region. A save made before this field
            // existed deserializes it as null ("nothing recorded"), the correct default - same
            // no-new-version precedent DominateRecord_v1's own added fields established.
            public ulong[] drainedCorpses;
        }

        public override object GetSaveData()
        {
            CustomSaveData_v5 data = new CustomSaveData_v5();
            data.baseData = base.GetSaveData();
            data.bloodPoints = bloodPoints;
            data.nextDrainTime = nextDrainTime;
            data.hasAnnouncedLowBlood = hasAnnouncedLowBlood;
            data.hasAnnouncedEmptyBlood = hasAnnouncedEmptyBlood;
            data.humanity = humanity;
            data.suppressBeastLastCastTime = suppressBeastLastCastTime;
            data.sceneHumanityGainLastTime = sceneHumanityGainLastTime;
            data.animalHuntLastTime = animalHuntLastTime;
            data.thralls = ThrallManager.GetSaveRecords();
            data.dominates = ThrallManager.GetDominateSaveRecords();
            data.drainedCorpses = SneakFeedController.GetDrainedCorpseSaveRecords();
            return data;
        }

        // FullSerializer embeds whichever exact type was actually saved for this object-typed field,
        // so dataIn comes back boxed as CustomSaveData_v5 (current format), CustomSaveData_v4 (the
        // pre-Dominate-persistence format), CustomSaveData_v3 (the pre-Wilderness-Hunt format),
        // CustomSaveData_v2 (the pre-scene-engine format), CustomSaveData_v1 (the old hunger-stage/
        // 0-100-Humanity format), or - for a save made before any of these systems existed - as plain
        // VampirismEffect.CustomSaveData_v1, which fails every "is" check and falls through to the
        // final fresh-defaults branch. Order matters: v5 checked first since it's the common case
        // going forward. Only the v5 branch has dominate records to hand over; every older branch
        // leaves the stash untouched (RestoreDominatedAllies consumes and clears it on every load
        // either way, so a stale stash can't leak across loads).
        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            if (dataIn is CustomSaveData_v5)
            {
                CustomSaveData_v5 data = (CustomSaveData_v5)dataIn;
                base.RestoreSaveData(data.baseData);
                bloodPoints = data.bloodPoints;
                nextDrainTime = data.nextDrainTime;
                hasAnnouncedLowBlood = data.hasAnnouncedLowBlood;
                hasAnnouncedEmptyBlood = data.hasAnnouncedEmptyBlood;
                humanity = data.humanity;
                suppressBeastLastCastTime = data.suppressBeastLastCastTime;
                sceneHumanityGainLastTime = data.sceneHumanityGainLastTime;
                animalHuntLastTime = data.animalHuntLastTime;
                ThrallManager.RestoreRecords(data.thralls);
                ThrallManager.RestoreDominateRecords(data.dominates);
                SneakFeedController.RestoreDrainedCorpseRecords(data.drainedCorpses);
            }
            else if (dataIn is CustomSaveData_v4)
            {
                CustomSaveData_v4 data = (CustomSaveData_v4)dataIn;
                base.RestoreSaveData(data.baseData);
                bloodPoints = data.bloodPoints;
                nextDrainTime = data.nextDrainTime;
                hasAnnouncedLowBlood = data.hasAnnouncedLowBlood;
                hasAnnouncedEmptyBlood = data.hasAnnouncedEmptyBlood;
                humanity = data.humanity;
                suppressBeastLastCastTime = data.suppressBeastLastCastTime;
                sceneHumanityGainLastTime = data.sceneHumanityGainLastTime;
                animalHuntLastTime = data.animalHuntLastTime;
                ThrallManager.RestoreRecords(data.thralls);
                ThrallManager.RestoreDominateRecords(null);
            }
            else if (dataIn is CustomSaveData_v3)
            {
                CustomSaveData_v3 data = (CustomSaveData_v3)dataIn;
                base.RestoreSaveData(data.baseData);
                bloodPoints = data.bloodPoints;
                nextDrainTime = data.nextDrainTime;
                hasAnnouncedLowBlood = data.hasAnnouncedLowBlood;
                hasAnnouncedEmptyBlood = data.hasAnnouncedEmptyBlood;
                humanity = data.humanity;
                suppressBeastLastCastTime = data.suppressBeastLastCastTime;
                sceneHumanityGainLastTime = data.sceneHumanityGainLastTime;
                animalHuntLastTime = 0;
                ThrallManager.RestoreRecords(data.thralls);
            }
            else if (dataIn is CustomSaveData_v2)
            {
                CustomSaveData_v2 data = (CustomSaveData_v2)dataIn;
                base.RestoreSaveData(data.baseData);
                bloodPoints = data.bloodPoints;
                nextDrainTime = data.nextDrainTime;
                hasAnnouncedLowBlood = data.hasAnnouncedLowBlood;
                hasAnnouncedEmptyBlood = data.hasAnnouncedEmptyBlood;
                humanity = data.humanity;
                suppressBeastLastCastTime = data.suppressBeastLastCastTime;
                sceneHumanityGainLastTime = 0;
                animalHuntLastTime = 0;
                ThrallManager.RestoreRecords(data.thralls);
            }
            else if (dataIn is CustomSaveData_v1)
            {
                CustomSaveData_v1 data = (CustomSaveData_v1)dataIn;
                base.RestoreSaveData(data.baseData);

                int oldStage = data.hungerStage;
                bloodPoints = oldStage <= 1 ? 10 : oldStage == 2 ? 7 : oldStage == 3 ? 4 : 0;
                nextDrainTime = NowClassic() + drainIntervalMinutes;
                hasAnnouncedLowBlood = false;
                hasAnnouncedEmptyBlood = false;
                humanity = Mathf.Clamp(Mathf.RoundToInt((data.humanity ?? 50) / 10f), 0, 10);
                suppressBeastLastCastTime = data.suppressBeastLastCastTime;
                sceneHumanityGainLastTime = 0;
                animalHuntLastTime = 0;
                ThrallManager.RestoreRecords(data.thralls);

                VoxsVampirism.Log("Migrated pre-Blood-Point save: hungerStage {0} -> {1} blood, old Humanity {2} -> {3}/10.",
                    oldStage, bloodPoints, data.humanity ?? 50, humanity);
            }
            else
            {
                base.RestoreSaveData(dataIn);
                bloodPoints = maxBloodPoints;
                nextDrainTime = NowClassic() + drainIntervalMinutes;
                hasAnnouncedLowBlood = false;
                hasAnnouncedEmptyBlood = false;
                humanity = 5;
                suppressBeastLastCastTime = 0;
                sceneHumanityGainLastTime = 0;
                animalHuntLastTime = 0;
                ThrallManager.RestoreRecords(null);
            }
        }
    }
}
