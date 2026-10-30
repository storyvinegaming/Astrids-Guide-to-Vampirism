// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mod entry point. Registers VoxsVampirismEffect as a replacement for vanilla's "Vampirism-Curse"
// key (EntityEffectBroker.RegisterEffectTemplate(effect, allowReplacement:true)) plus this mod's
// twelve new spells, called directly from Init() rather than from EntityEffectBroker's
// OnRegisterCustomEffects event. EntityEffectBroker is a MonoBehaviour on a core game object
// present from boot, so its own Start() (which does the reflective registration and then raises
// OnRegisterCustomEffects) has already run and already fired that event long before any mod's
// [Invoke(StateManager.StateTypes.Start, 0)] entry point is called - subscribing to the event there
// is too late, the event has already fired with no listeners, and the handler never runs.
// Confirmed against Astrid's Guide to Necromancy, which never uses that event at all and instead
// calls RegisterEffectTemplate directly from its own Init()/LoadSettings() - the same approach
// used here.
//
// Does NOT use Harmony. DFU's mod system compiles a mod's C# source at runtime (see
// Mod.CompileSourceToAssemblies/ModManager.CompileFromSourceAssets) from a reference set that does
// not include HarmonyLib - a mod distributed as raw source (the only form this project's Mod
// Builder produces) referencing HarmonyLib fails to compile at real launch even though it compiles
// fine inside the Unity Editor, where 0Harmony.dll is available as a Plugin. Features 1 and 5, which
// originally used Harmony prefixes/postfixes on private vanilla methods with no virtual/override
// hook, are instead implemented as plain polling MonoBehaviours against public state
// (RestModeGateWatcher, GodModeRestoreListener) - see each file's own header for details.

using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect.FallExe;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod.Effects;

namespace VoxsVampirismMod
{
    public class VoxsVampirism : MonoBehaviour
    {
        static Mod mod;

        // Exposed for effects/components that need to load bundled assets (e.g. Mod.GetAsset<AudioClip>)
        // without each keeping their own reference to the mod instance.
        public static Mod Mod { get { return mod; } }

        // Unique book ID for Astrid's Guide to Vampirism (must match the "ID" field in
        // Books/Mapping/AstridVampireBookMapping.json). Astrid's Guide to Necromancy uses 90210 for
        // its own book - this is a different, non-colliding ID.
        const int VampireBookID = 90211;

        public static bool KeepOriginalHeadOnPaperDoll { get; private set; }
        public static KeyCode SneakFeedKeyCode { get; private set; } = KeyCode.None;
        public static int HuntersSightLightRange { get; private set; } = 35;
        public static bool HuntersSightTintEnabled { get; private set; } = true;
        public static Color HuntersSightLightColor { get; private set; } = new Color(1f, 0.4f, 0.35f);
        public static Color HuntersSightTintColor { get; private set; } = new Color(0.55f, 0.05f, 0.05f, 0.10f);
        // DFU's own ColorPicker (see "Hunter's Sight Tint Color" above) has no alpha control at all -
        // confirmed by reading ColorPicker.cs directly, every color it can produce comes out fully
        // opaque. This slider is the only actual way to control the tint's opacity in-game; applied by
        // overwriting HuntersSightTintColor's own alpha channel in LoadSettings below, ignoring
        // whatever alpha the picker's stored Color32 happens to carry. 0-100 percent, default 10
        // (matches the original hardcoded 0.10f alpha default above).
        public static int HuntersSightTintAlphaPercent { get; private set; } = 10;
        public static int ScentOfPreyDetectionRangeFeet { get; private set; } = 200;
        // Blood Sense's own target scan runs on this real-time interval (via ConstantEffect(), not
        // MagicRound()) rather than being tied to the game's classic-minute round cadence - see
        // ScentOfPreyEffect.cs's own header for why. 1-5 seconds, default 1.
        public static int ScentOfPreyRefreshIntervalSeconds { get; private set; } = 1;
        // Vampiric Drain's own AoE corpse-detection radius - 1-100 feet, default 20 (the original
        // fixed value was 10). See VampiricDrainEffect.cs's own header for why a wider radius is
        // often needed in practice.
        public static int VampiricDrainRangeFeet { get; private set; } = 20;
        public static bool FrenzyEventsEnabled { get; private set; } = true;
        // Independent toggles for the two scene-accompaniment systems (FeedSceneMusicController/
        // FeedSceneVoiceController) - each checked at its own single call site. Narrative Feeding
        // Scenes themselves are a core feature with no opt-out toggle of their own (see
        // SneakFeedController.FeedOnNpc).
        public static bool FeedSceneMusicEnabled { get; private set; } = true;
        public static bool FeedSceneVoiceEnabled { get; private set; } = true;
        public static int SceneComplicationChancePercent { get; private set; } = 50;
        public static int NightWalkerCount { get; private set; } = 3;
        public static bool PopulateResidencesAtNight { get; private set; } = true;
        // Post-Feed Offers: rare, unprompted Create Vampire/Create Thrall offers after a successful
        // non-violent scene feed - see NarrativeFeedSceneEngine's own Post-Feed Offer region.
        public static int CreateVampireOfferChancePercent { get; private set; } = 2;
        public static int CreateThrallOfferChancePercent { get; private set; } = 10;
        // Wilderness Hunt spec: hours between wilderness animal-hunt attempts - see
        // VoxsVampirismEffect.IsAnimalHuntOnCooldown. 0 disables the cooldown entirely.
        public static int AnimalHuntCooldownHours { get; private set; } = 12;
        public static bool ShowBloodTracker { get; private set; } = true;
        // 0=Top Left, 1=Top Right, 2=Bottom Left, 3=Bottom Right - matches the MultipleChoiceKey
        // option order in modsettings.json (BloodPointTracker reads this directly).
        public static int BloodTrackerPositionIndex { get; private set; }
        public static bool ClimateAndCaloriesInstalled { get; private set; }
        // World of Daggerfall compatibility (SneakFeedController's billboard-person recognition) -
        // gates that feature off entirely when the content mod isn't present, matching
        // ClimateAndCaloriesInstalled's own pattern.
        public static bool WorldOfDaggerfallInstalled { get; private set; }
        // Travelling NPCs compatibility (SneakFeedController's own wanderer recognition) - gates that
        // feature off entirely when the content mod isn't present, matching WorldOfDaggerfallInstalled's
        // own pattern. See SneakFeedController.cs's own header for why this needed special-casing at
        // all: those wanderers are deliberately minimal GameObjects (no StaticNPC/MobilePersonNPC/
        // DaggerfallEntityBehaviour) for performance, so none of this mod's existing target checks
        // ever recognized them.
        public static bool TravellingNPCsInstalled { get; private set; }
        // Blood Economy Rebalance: monster/enemy corpses grant Max(1, maxHP / this) blood.
        public static int CorpseBloodDivisor { get; private set; } = 40;
        // Blood Economy Rebalance (§5, desperate casting): percent of max Health charged per Blood
        // Point a spell/action can't cover, instead of refusing the cast outright.
        public static int DesperateCastingHpPercent { get; private set; } = 20;

        // The Legion System update: how many "Health points of Dominate target" one point of upkeep
        // buys - see AllyCombatController.cs's own header for the full budget design. Thralls/created
        // vampires use flat upkeep costs instead (set directly in ThrallManager), since Dominate is the
        // one ally type with no fixed type/tier to key a flat cost off - it can bind any living creature.
        public static int DominateUpkeepDivisor { get; private set; } = 4;

        // Off by default - when on, DominateTimer's own countdown never runs at all (see that file's
        // own header), so a Dominated creature never reverts on its own duration expiring. The shared
        // legion budget still applies either way - a permanent Dominate can still be released early if
        // it's the oldest ally and the budget can no longer hold it.
        public static bool PermanentDominate { get; private set; }

        // The Legion System update: opt-in ally-vs-ally collision pass-through, off by default -
        // mirrors Astrid's Guide to Necromancy's own AlliedUndeadIgnoreEachOtherCollision setting
        // exactly (see AllyCombatController.SetIgnoreCollisionWithOtherAllies). Normally a court
        // physically jostles for space like any other pack of creatures; enabling this lets them pass
        // through each other too, so they never jam up a corridor.
        public static bool AlliesIgnoreEachOtherCollision { get; private set; }

        // Off by default - Vampiric Flames/Ice's own MagicRound() checks these directly (see that
        // file's own header) rather than the framework ever excluding a target: DFU's AreaAtRange
        // targeting has no per-caster/per-team exclusion of its own, so the caster and any of their
        // own allies caught in the blast radius take full damage exactly like anyone else struck,
        // same as vanilla's own AoE spells never exclude the caster either.
        public static bool VampiricFireSparesCaster { get; private set; }
        public static bool VampiricFireSparesAllies { get; private set; }

        // Off by default - see FastTravelTimeBridge.cs's own header for how this is actually enforced
        // (no Harmony, so DFU's own DaggerfallTravelPopUp.performFastTravel() can't be patched
        // directly; the forced dusk arrival is undone afterward instead, via reflection).
        public static bool AllowDaylightArrival { get; private set; }

        // Shared accessor so the book-granted spell effects (Effects/*.cs) can read the player's
        // current Humanity (0-10) for their own potency scaling without each duplicating the
        // GetRacialOverrideEffect-as-VoxsVampirismEffect lookup. Falls back to the baseline (5) if
        // the player isn't currently a vampire at all - shouldn't happen in practice since only a
        // vampire can ever have these spells to cast, but a safe, neutral default beats a null ref.
        public static int GetPlayerHumanity()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            return vampirism != null ? vampirism.GetHumanity() : 5;
        }

        // The Legion System update: the shared budget every ally (thrall, created vampire, Dominate
        // target alike) draws upkeep against - see AllyCombatController.cs's own header for the full
        // design and why this direction (low Humanity, high Willpower both grow it) was chosen over
        // Necromancy's own Magicka+Mysticism formula. LiveWillpower already carries this mod's own
        // Blood-Linked Attributes penalty (Willpower drops as Blood Points run low), so a starving
        // vampire's own legion capacity shrinks right along with everything else that stat touches.
        public static int GetLegionBudget()
        {
            int humanity = GetPlayerHumanity();
            int willpower = GameManager.Instance.PlayerEntity.Stats.LiveWillpower;
            return (10 - humanity) * 10 + willpower * 3;
        }

        // Clan Features spec, §2: shared accessor so clan-gated features (Blood Surge's own Garlythi/
        // Selenu/Vraseth sub-effects, Mist Form's Lyrezi Invisibility, Quicken Blood's Thrafey bonus,
        // the Haarvenu-only spell grant) don't each duplicate the GetRacialOverrideEffect-as-
        // VoxsVampirismEffect cast/null-check. VampireClans.None if the player isn't a vampire at all.
        public static VampireClans GetPlayerVampireClan()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            return vampirism != null ? vampirism.VampireClan : VampireClans.None;
        }

        static readonly KeyCode[] sneakFeedKeys = { KeyCode.None, KeyCode.F, KeyCode.V, KeyCode.N };

        // Book spell grants (Feature 6/8) - built once in RegisterCustomEffects, after all spell
        // templates are registered, since each Create() needs to look its own template up from the
        // broker first. Same two-phase shape as Astrid's Guide to Necromancy's own LoadSettings.
        // Every spell here is granted unconditionally on book read - no level gate, no threshold, per
        // the Humanity system redesign (spells no longer gate on level; their POTENCY scales with
        // Humanity instead - see each effect's own file).
        static readonly List<EffectBundleSettings> bookSpellBundles = new List<EffectBundleSettings>();

        // Haarvenu clan feature (Clan Features spec, §2): Vampiric Ice, granted only to a Haarvenu
        // vampire - kept separate from bookSpellBundles above (which every vampire gets unconditionally)
        // and offered by GrantSpellsFromBook's own clan check instead.
        static EffectBundleSettings haarvenuSpellBundle;

        // Diagnostic logging - deliberately plain, ungated Debug.Log calls (not behind a debug
        // setting) at a small, fixed set of one-time moments (init, transformation, book grant) so a
        // single grep for this prefix in Player.log tells you exactly how far this mod got, instead
        // of having to infer it from unrelated log lines or repeated in-game testing. No per-frame or
        // per-round logging - see the moments listed in each call site's own comment.
        public const string LogPrefix = "Astrid's Vampirism: ";

        public static void Log(string message)
        {
            Debug.Log(LogPrefix + message);
        }

        public static void Log(string format, params object[] args)
        {
            Debug.LogFormat(LogPrefix + format, args);
        }

        // ===== TIME DIAGNOSTIC (root-causing the racing-magic-rounds anomaly) =====
        //
        // Live sessions keep showing magic rounds arriving in sustained bursts (~40/sec against a
        // normal 0.2/sec - see MistFormEffect's own anomaly guard log lines), which eats any real
        // round-counted duration (Blood Surge's 10 rounds, Mist Form's 4-round upkeep, Dominate's
        // timer) orders of magnitude too fast. Magic rounds fire strictly one per elapsed GAME
        // MINUTE (EntityEffectBroker.Update, read directly), and vanilla's clock (WorldTime.cs, read
        // directly) advances via exactly three levers anything in the process can pull: the public
        // WorldTime.TimeScale field (scaled deltaTime multiplier, default 12), the public
        // WorldTime.RaiseTimeInSeconds field (consumed additively every frame), and direct
        // DaggerfallDateTime.RaiseTime() calls. EasyTimeScale was disabled by the player with no
        // change, so this watches all the levers at once and logs, whenever the clock actually races
        // (or any time TimeScale changes at all), exactly which lever is live and what the numbers
        // are - so the culprit gets named in Player.log instead of guessed at.
        // Session data so far (2026-08-15): TimeScale=12 and the visible clock advance normally, yet
        // rounds arrive at EXACTLY one per rendered frame (300-301 per 5s window), sustained, even
        // with the game paused in a menu - and per NightWalkerSpawner's own per-round stack traces,
        // every single one comes through vanilla's own EntityEffectBroker.Update(), not any mod
        // raising the event directly. The one mechanism consistent with all of that at once is a
        // MINUTE DEBT: the broker's private lastGameMinute marker sitting tens of thousands of
        // minutes BEHIND the loaded save's clock (the same session logged the classic-minute counter
        // jumping +100,216 across the load) and being paid down one round per frame. The marker is
        // private, so it's read via reflection here (plain System.Reflection - the Dynamic Music
        // compat in FeedSceneMusicController already established that precedent) and burst-logged
        // per-frame whenever a window trips the anomaly check, which proves or kills the debt theory
        // in a single glance: debt shows as lastGameMinute far below the clock and climbing ~60/sec.
        int diagRoundsThisWindow;
        int diagRoundsThisFrame;
        int diagBurstFramesToLog;
        uint diagClockAtLastRound;
        float diagWindowStartRealTime = -1f;
        uint diagWindowStartClassicMinute;
        float diagLastLoggedTimeScale = float.MinValue;
        static System.Reflection.FieldInfo brokerLastGameMinuteField;

        void OnEnable()
        {
            EntityEffectBroker.OnNewMagicRound += CountRoundForTimeDiagnostic;
        }

        void OnDisable()
        {
            EntityEffectBroker.OnNewMagicRound -= CountRoundForTimeDiagnostic;
        }

        void CountRoundForTimeDiagnostic()
        {
            diagRoundsThisWindow++;
            diagRoundsThisFrame++;
            if (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.WorldTime != null)
                diagClockAtLastRound = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        }

        static long ReadBrokerLastGameMinute()
        {
            try
            {
                if (brokerLastGameMinuteField == null)
                    brokerLastGameMinuteField = typeof(EntityEffectBroker).GetField("lastGameMinute",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (brokerLastGameMinuteField == null || GameManager.Instance.EntityEffectBroker == null)
                    return -1;
                return (uint)brokerLastGameMinuteField.GetValue(GameManager.Instance.EntityEffectBroker);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        void Update()
        {
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            if (dfUnity == null || dfUnity.WorldTime == null)
                return;
            WorldTime worldTime = dfUnity.WorldTime;

            // Any TimeScale change at all is worth a line - normal installs never touch it outside
            // fast travel/rest, so a spontaneous change mid-dungeon IS the story.
            if (worldTime.TimeScale != diagLastLoggedTimeScale)
            {
                if (diagLastLoggedTimeScale != float.MinValue)
                    Log("TimeDiag: WorldTime.TimeScale changed {0} -> {1}.", diagLastLoggedTimeScale, worldTime.TimeScale);
                else
                    Log("TimeDiag: WorldTime.TimeScale is {0} at startup.", worldTime.TimeScale);
                diagLastLoggedTimeScale = worldTime.TimeScale;
            }

            // The RaiseTimeInSeconds side-channel is consumed by WorldTime.Update the same frame it's
            // set, so catching it non-zero here means something wrote to it within this very frame.
            if (worldTime.RaiseTimeInSeconds > 0f)
                Log("TimeDiag: WorldTime.RaiseTimeInSeconds is {0:F1} this frame - something is injecting time through the side-channel.", worldTime.RaiseTimeInSeconds);

            uint nowClassicMinute = worldTime.DaggerfallDateTime.ToClassicDaggerfallTime();

            // Per-frame burst detail while an anomaly is fresh - see the debt-theory comment above
            // for how to read it. Logged BEFORE resetting the per-frame round count.
            if (diagBurstFramesToLog > 0)
            {
                diagBurstFramesToLog--;
                Log("TimeDiag/frame {0}: roundsThisFrame={1}, clockAtUpdate={2}, clockAtLastRound={3}, broker.lastGameMinute={4} (debt {5}).",
                    Time.frameCount, diagRoundsThisFrame, nowClassicMinute, diagClockAtLastRound,
                    ReadBrokerLastGameMinute(), (long)nowClassicMinute - ReadBrokerLastGameMinute());
            }
            diagRoundsThisFrame = 0;

            if (diagWindowStartRealTime < 0f)
            {
                diagWindowStartRealTime = Time.unscaledTime;
                diagWindowStartClassicMinute = nowClassicMinute;
                diagRoundsThisWindow = 0;
                return;
            }

            float windowSpan = Time.unscaledTime - diagWindowStartRealTime;
            if (windowSpan < 5f)
                return;

            // At default 12x, 5 real seconds = 1 game minute = 1 round. Anything past 3 is a genuine
            // anomaly, logged with every lever's current value plus the raw date fields (so a jump
            // shows WHICH component of the date moved - minutes rolling fast vs day/year snapping).
            uint minutesElapsed = nowClassicMinute - diagWindowStartClassicMinute;
            if (diagRoundsThisWindow > 3 || minutesElapsed > 3)
            {
                DaggerfallDateTime now = worldTime.DaggerfallDateTime;
                Log("TimeDiag: CLOCK RACING - {0} magic rounds / {1} game-minutes in {2:F1} real seconds (normal ~1 per 5s). WorldTime.TimeScale={3}, Time.timeScale={4}, paused={5}, topWindow={6}, date: Y{7} M{8} D{9} {10:D2}:{11:D2}.",
                    diagRoundsThisWindow, minutesElapsed, windowSpan,
                    worldTime.TimeScale, Time.timeScale,
                    GameManager.IsGamePaused,
                    DaggerfallUI.UIManager != null && DaggerfallUI.UIManager.TopWindow != null ? DaggerfallUI.UIManager.TopWindow.GetType().Name : "none",
                    now.Year, now.Month, now.Day, now.Hour, now.Minute);
                diagBurstFramesToLog = 6;
            }

            diagWindowStartRealTime = Time.unscaledTime;
            diagWindowStartClassicMinute = nowClassicMinute;
            diagRoundsThisWindow = 0;
        }

        // Shows a wrapped, click-to-dismiss flavor line ahead of a follow-up menu/action - shared by
        // SneakFeedController's feed menu and MesmerizeCommandMenu's command menu, both of which want
        // a short prompt ahead of a DaggerfallListPickerWindow, which has no room of its own for
        // freeform text (its native PICK00I0.IMG layout is a fixed-size list box only). Same wrap-
        // capable DaggerfallMessageBox construction already used elsewhere in this mod (RestModeGate-
        // Watcher/HungerStatusInfoBox), chained to the follow-up via the same deferred OnWindowChange
        // latch this mod already uses for the transformation-offer and talk-window timing fixes.
        //
        // FeedSceneVoiceController.Stop() fires here (and in ShowYesNoPromptThenAction below) rather
        // than from NarrativeFeedSceneEngine.cs's own dozen scene-end/beat-transition call sites - this
        // is the one chokepoint every box this mod ever shows is dismissed through, whether by click or
        // by Escape-cancel (DaggerfallPopupWindow's own CancelWindow still changes TopWindow away from
        // box, which this handler already reacts to either way), so it uniformly catches "player
        // continues to the next beat" and "player exits the scene" without missing a spot. A no-op
        // everywhere outside a feeding scene, since nothing is ever playing to begin with.
        public static void ShowWrappedPromptThenAction(string promptText, Action followUp)
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            DaggerfallMessageBox box = new DaggerfallMessageBox(uiManager, uiManager.TopWindow, wrapText: true);
            box.SetText(promptText);
            box.ClickAnywhereToClose = true;
            box.Show();

            EventHandler handler = null;
            handler = (sender, args) =>
            {
                if (uiManager.TopWindow == box)
                    return;

                uiManager.OnWindowChange -= handler;
                FeedSceneVoiceController.Stop();
                followUp();
            };
            uiManager.OnWindowChange += handler;
        }

        // Same wrap-capable manual DaggerfallMessageBox/AddButton construction ShowTransformationOffer
        // already uses for the book's own curse-acceptance prompt (no wrapText-capable convenience
        // overload exists for CommonMessageBoxButtons.YesNo) - pulled out as a shared helper here so
        // NarrativeFeedSceneEngine's own Post-Feed Offer beat doesn't duplicate it a third time.
        public static void ShowYesNoPromptThenAction(string promptText, Action onYes, Action onNo)
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            DaggerfallMessageBox box = new DaggerfallMessageBox(uiManager, uiManager.TopWindow, wrapText: true);
            box.SetText(promptText);
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.No, true);
            box.OnButtonClick += (sender, button) =>
            {
                sender.CloseWindow();
                FeedSceneVoiceController.Stop();
                if (button == DaggerfallMessageBox.MessageBoxButtons.Yes)
                    onYes();
                else
                    onNo();
            };
            box.Show();
        }

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;

            Log("Mod init started.");

            GameObject go = new GameObject(mod.Title);
            go.AddComponent<VoxsVampirism>();
            go.AddComponent<HoodController>();
            go.AddComponent<SneakFeedController>();
            go.AddComponent<RestModeGateWatcher>();
            go.AddComponent<GodModeRestoreListener>();
            go.AddComponent<HungerStatusInfoBox>();
            go.AddComponent<FastTravelTimeBridge>();
            go.AddComponent<HuntersSightVisuals>();
            go.AddComponent<ScentOfPreyVisuals>();
            go.AddComponent<ThrallManager>();
            go.AddComponent<FeedFlashVisuals>();
            go.AddComponent<MistFormVisuals>();
            go.AddComponent<BloodPointTracker>();
            go.AddComponent<SpellCastCostRefunder>();
            go.AddComponent<NightWalkerSpawner>();
            go.AddComponent<ResidencePopulator>();
            go.AddComponent<MesmerizeGazeWatcher>();
            go.AddComponent<FeedSceneMusicController>();
            go.AddComponent<FeedSceneVoiceController>();
            go.AddComponent<PlayerAttackWatcher>();

            RegisterCustomEffects();

            RegisterBookUseHandler();

            // Clan Features spec, §5: grant the book at character creation too, matching Astrid's
            // Guide to Necromancy's own GrantAstridBook hook exactly (VoxsNecromancy.cs) - same event,
            // same NewCharacter gate. Reuses GrantVampireBook as-is; its own "already has it" scan
            // already prevents double-granting against the transformation-time call in
            // VoxsVampirismEffect.Start(), no new guard needed.
            StartGameBehaviour.OnStartGame += (sender, args) =>
            {
                StartGameBehaviour behaviour = sender as StartGameBehaviour;
                if (behaviour != null && behaviour.LastStartMethod == StartGameBehaviour.StartMethods.NewCharacter)
                    GrantVampireBook(GameManager.Instance.PlayerEntity);
            };

            // MigrateStaleMesmerizeSpell's own header explains the bug - run on every load, not just
            // the next time the player happens to reread the book or GrantSpellsFromBook otherwise
            // runs, since the crash it fixes happens the moment the spellbook UI is opened at all.
            SaveLoadManager.OnLoad += data =>
            {
                PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                if (playerEntity != null && GameManager.Instance.PlayerEffectManager.HasVampirism())
                    MigrateStaleMesmerizeSpell(playerEntity, playerEntity.GetSpells());
            };

            VoxsVampirismConsoleCommands.RegisterCommands();

            mod.LoadSettingsCallback = LoadSettings;
            mod.LoadSettings();
            mod.IsReady = true;

            Log("Mod init completed.");
        }

        static void RegisterCustomEffects()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;

            RegisterEffect(effectBroker, new VoxsVampirismEffect());
            RegisterEffect(effectBroker, new VampiricDrainEffect());
            RegisterEffect(effectBroker, new ScentOfPreyEffect());
            RegisterEffect(effectBroker, new MesmerizeEffect());
            RegisterEffect(effectBroker, new VampiricFlamesEffect());
            RegisterEffect(effectBroker, new SuppressBeastEffect());
            RegisterEffect(effectBroker, new DominateEffect());
            RegisterEffect(effectBroker, new HuntersSightEffect());
            RegisterEffect(effectBroker, new CommandBloodCourtEffect());
            RegisterEffect(effectBroker, new DevourThrallEffect());
            RegisterEffect(effectBroker, new MistFormEffect());
            // Internal helper for Mist Form (see its own file header) - never granted to the player,
            // never added to bookSpellBundles below, just needs a broker template so AssignBundle can
            // instantiate it silently while Mist Form is active.
            RegisterEffect(effectBroker, new MistFormOpenEffect());
            RegisterEffect(effectBroker, new QuickenBloodEffect());
            RegisterEffect(effectBroker, new BloodSurgeEffect());
            RegisterEffect(effectBroker, new VampiricIceEffect());

            // Book grants (Feature 6) - every vampire spell is granted unconditionally on read, no
            // level gate. Shroud of Night was removed entirely (Humanity system redesign) - hood is
            // now the only day-travel mechanism below Humanity 10.
            bookSpellBundles.Clear();

            AddBookSpell(ScentOfPreyEffect.Create(), ScentOfPreyEffect.EffectKey);
            AddBookSpell(VampiricDrainEffect.Create(), VampiricDrainEffect.EffectKey);
            AddBookSpell(MesmerizeEffect.Create(), MesmerizeEffect.EffectKey);
            AddBookSpell(VampiricFlamesEffect.Create(), VampiricFlamesEffect.EffectKey);
            AddBookSpell(SuppressBeastEffect.Create(), SuppressBeastEffect.EffectKey);
            AddBookSpell(DominateEffect.Create(), DominateEffect.EffectKey);
            AddBookSpell(HuntersSightEffect.Create(), HuntersSightEffect.EffectKey);
            AddBookSpell(CommandBloodCourtEffect.Create(), CommandBloodCourtEffect.EffectKey);
            AddBookSpell(DevourThrallEffect.Create(), DevourThrallEffect.EffectKey);
            AddBookSpell(MistFormEffect.Create(), MistFormEffect.EffectKey);
            AddBookSpell(QuickenBloodEffect.Create(), QuickenBloodEffect.EffectKey);
            AddBookSpell(BloodSurgeEffect.Create(), BloodSurgeEffect.EffectKey);

            haarvenuSpellBundle = VampiricIceEffect.Create();
            Log("Spell bundle '{0}' creation (Haarvenu-exclusive): {1}", VampiricIceEffect.EffectKey,
                string.IsNullOrEmpty(haarvenuSpellBundle.Name) ? "FAILED" : "OK");
        }

        static void RegisterEffect(EntityEffectBroker effectBroker, BaseEntityEffect effect)
        {
            effect.SetProperties();
            bool ok = effectBroker.RegisterEffectTemplate(effect, true);
            Log("Effect template '{0}' ({1}) registration: {2}", effect.Key, effect.GetType().Name, ok ? "OK" : "FAILED");
        }

        static void AddBookSpell(EffectBundleSettings bundle, string effectKey)
        {
            // Each XEffect.Create() returns a default, empty EffectBundleSettings (Name left null)
            // if it couldn't find its own template via HasEffectTemplate - Name is the simplest
            // reliable success/failure signal available from the returned struct.
            bool ok = !string.IsNullOrEmpty(bundle.Name);
            Log("Spell bundle '{0}' creation: {1}", effectKey, ok ? "OK" : "FAILED (see RegisterEffect result above)");
            if (!ok)
                return;

            bookSpellBundles.Add(bundle);
        }

        static void LoadSettings(ModSettings settings, ModSettingsChange change)
        {
            KeepOriginalHeadOnPaperDoll = settings.GetValue<bool>("General", "Keep Original Head On Paper Doll");
            HuntersSightLightRange = settings.GetValue<int>("General", "Hunter's Sight Light Range");
            HuntersSightTintEnabled = settings.GetValue<bool>("General", "Hunter's Sight Screen Tint");
            HuntersSightLightColor = settings.GetValue<Color32>("General", "Hunter's Sight Light Color");
            HuntersSightTintAlphaPercent = settings.GetValue<int>("General", "Hunter's Sight Tint Transparency");
            Color huntersSightTintColor = settings.GetValue<Color32>("General", "Hunter's Sight Tint Color");
            huntersSightTintColor.a = HuntersSightTintAlphaPercent / 100f;
            HuntersSightTintColor = huntersSightTintColor;
            ScentOfPreyDetectionRangeFeet = settings.GetValue<int>("General", "Blood Sense Detection Range");
            ScentOfPreyRefreshIntervalSeconds = settings.GetValue<int>("General", "Blood Sense Refresh Interval");
            VampiricDrainRangeFeet = settings.GetValue<int>("General", "Vampiric Drain Range");
            ShowBloodTracker = settings.GetValue<bool>("General", "Show Blood Tracker");
            BloodTrackerPositionIndex = settings.GetValue<int>("General", "Blood Tracker Position");
            CorpseBloodDivisor = settings.GetValue<int>("General", "Corpse Blood Divisor");
            DesperateCastingHpPercent = settings.GetValue<int>("General", "Desperate Casting HP Cost %");
            DominateUpkeepDivisor = settings.GetValue<int>("General", "Dominate Upkeep Divisor");
            PermanentDominate = settings.GetValue<bool>("General", "Permanent Dominate");
            AlliesIgnoreEachOtherCollision = settings.GetValue<bool>("General", "Allies Ignore Each Other's Collision");
            VampiricFireSparesCaster = settings.GetValue<bool>("General", "Vampiric Flames/Ice Spare Caster");
            VampiricFireSparesAllies = settings.GetValue<bool>("General", "Vampiric Flames/Ice Spare Allies");
            AllowDaylightArrival = settings.GetValue<bool>("General", "Allow Daylight Arrival");

            int feedKeyIdx = settings.GetValue<int>("Feeding", "Sneak Feed Key");
            SneakFeedKeyCode = feedKeyIdx < sneakFeedKeys.Length
                ? sneakFeedKeys[feedKeyIdx]
                : ParseKeyCode(settings.GetValue<string>("Feeding", "Sneak Feed Custom Key Bind"), KeyCode.F);

            FrenzyEventsEnabled = settings.GetValue<bool>("Feeding", "Frenzy Events");
            FeedSceneMusicEnabled = settings.GetValue<bool>("Feeding", "Feed Scene Music");
            FeedSceneVoiceEnabled = settings.GetValue<bool>("Feeding", "Feed Scene Voice Acting");
            SceneComplicationChancePercent = settings.GetValue<int>("Feeding", "Scene Complication Chance");
            NightWalkerCount = settings.GetValue<int>("Feeding", "Night Walker Count");
            PopulateResidencesAtNight = settings.GetValue<bool>("Feeding", "Populate Residences At Night");
            CreateVampireOfferChancePercent = settings.GetValue<int>("Feeding", "Create Vampire Offer Chance");
            CreateThrallOfferChancePercent = settings.GetValue<int>("Feeding", "Create Thrall Offer Chance");
            AnimalHuntCooldownHours = settings.GetValue<int>("Feeding", "Animal Hunt Cooldown Hours");

            Log("Resolved keybinds: Sneak Feed={0}", SneakFeedKeyCode);

            ClimateAndCaloriesInstalled = DetectClimateAndCalories();
            WorldOfDaggerfallInstalled = DetectWorldOfDaggerfall();
            TravellingNPCsInstalled = DetectTravellingNPCs();
        }

        static KeyCode ParseKeyCode(string text, KeyCode fallback)
        {
            KeyCode result;
            if (!string.IsNullOrEmpty(text) && System.Enum.TryParse(text, true, out result))
                return result;

            return fallback;
        }

        // Climate & Calories has no hard dependency reference in this mod and its real GUID isn't
        // known in this workspace, so detection is by title match rather than
        // ModManager.GetModFromGUID (the more robust lookup necromancy's own DEE-detection code
        // uses). If the real GUID becomes available, swapping this for a GetModFromGUID call would
        // be more robust against the mod being renamed.
        //
        // The mod's real title is "Climates & Calories" (plural "Climates") - an earlier version of
        // this constant used the singular "Climate & Calories", which made GetMod() return null
        // every time and silently disabled all of Feature 5, with GodModeRestoreListener bailing out
        // on the first line of its own Update() every frame. Matching is now done by normalizing
        // (lowercase, strip whitespace) and checking for both "climat" and "calor" as substrings,
        // rather than a single exact string, so this survives singular/plural and spacing variations
        // - the mod's own log lines use both "Climates & Calories" and "Climates&Calories".
        static bool DetectClimateAndCalories()
        {
            foreach (Mod candidate in ModManager.Instance.Mods)
            {
                if (candidate == null || !IsClimateAndCaloriesTitle(candidate.Title))
                    continue;

                Log("Climate & Calories detected: title='{0}' enabled={1}", candidate.Title, candidate.Enabled);
                return candidate.Enabled;
            }

            Log("Climate & Calories NOT detected among loaded mods.");
            foreach (Mod candidate in ModManager.Instance.Mods)
            {
                if (candidate != null && !string.IsNullOrEmpty(candidate.Title) && candidate.Title.ToLowerInvariant().Contains("calor"))
                    Log("  candidate with 'calor' in title (did not match): '{0}'", candidate.Title);
            }

            return false;
        }

        static bool IsClimateAndCaloriesTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.ToLowerInvariant().Replace(" ", string.Empty);
            return normalized.Contains("climat") && normalized.Contains("calor");
        }

        // Same title-match approach as Climate & Calories above, for the same reason - no known GUID
        // to key off of. "World of Daggerfall" adds new farms/shrines populated with decorative
        // person-shaped billboards (plain DaggerfallBillboard, never a real StaticNPC/MobilePersonNPC
        // - confirmed via a live SneakFeed diagnostic log: "raycast hit 'DaggerfallBillboard
        // [TEXTURE.3101, Index=41]' ... but it has none of StaticNPC/MobilePersonNPC/DaggerfallLoot/
        // DaggerfallEntityBehaviour"). Since even vanilla DFU can't talk to these - they were never
        // wired into TalkManager either - this is a real content gap, not something this mod broke.
        // Excludes "patch"/"biome"/"terrain" so this doesn't also fire for the separate compatibility/
        // reskin submods (Guard Tower Knight Removal's own title parenthetically mentions "World of
        // Daggerfall Patch"; the Biomes/Terrain submods are visual-only, add no new NPCs) - harmless
        // either way since SneakFeedController's own billboard archive whitelist (see
        // WorldOfDaggerfallPersonArchives) is the real gate, this just avoids a misleading "detected"
        // log line for an unrelated submod.
        static bool DetectWorldOfDaggerfall()
        {
            foreach (Mod candidate in ModManager.Instance.Mods)
            {
                if (candidate == null || !IsWorldOfDaggerfallTitle(candidate.Title))
                    continue;

                Log("World of Daggerfall detected: title='{0}' enabled={1}", candidate.Title, candidate.Enabled);
                return candidate.Enabled;
            }

            Log("World of Daggerfall NOT detected among loaded mods.");
            return false;
        }

        static bool IsWorldOfDaggerfallTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            string normalized = title.ToLowerInvariant().Replace(" ", string.Empty);
            if (!normalized.Contains("worldofdaggerfall"))
                return false;

            return !normalized.Contains("patch") && !normalized.Contains("biome") && !normalized.Contains("terrain");
        }

        // Same title-match approach as World of Daggerfall above, for the same reason - "Travelling
        // NPCs" (Nexus Mods #1362, by L1nebecK) ships as loose source, not a dedicated public API this
        // mod could reference at compile time. Its exact ModTitle is "Travelling NPCs" (confirmed by
        // reading the mod's own .dfmod.json directly, extracted from the shipped AssetBundle) - matched
        // loosely (normalized, substring) the same way every other cross-mod title check in this file
        // already is, so a minor title tweak in a future version doesn't silently break detection.
        static bool DetectTravellingNPCs()
        {
            foreach (Mod candidate in ModManager.Instance.Mods)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.Title))
                    continue;

                string normalized = candidate.Title.ToLowerInvariant().Replace(" ", string.Empty);
                if (!normalized.Contains("travellingnpcs") && !normalized.Contains("travelingnpcs"))
                    continue;

                Log("Travelling NPCs detected: title='{0}' enabled={1}", candidate.Title, candidate.Enabled);
                return candidate.Enabled;
            }

            Log("Travelling NPCs NOT detected among loaded mods.");
            return false;
        }

        // Feature 6. Called from VoxsVampirismEffect.Start(), which fires exactly once when the
        // stage-two curse effect begins.
        public static void GrantVampireBook(PlayerEntity playerEntity)
        {
            if (playerEntity == null)
                return;

            List<DaggerfallUnityItem> books = playerEntity.Items.SearchItems(ItemGroups.Books);
            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].message == VampireBookID)
                {
                    Log("GrantVampireBook: skipped, player already has it.");
                    return;
                }
            }

            DaggerfallUnityItem book = new DaggerfallUnityItem(ItemGroups.Books, 0);
            book.message = VampireBookID;
            book.value = 100;

            // Legacy enchantment purely so IsEnchanted returns true, which is what the inventory
            // window's Magic Items tab filter actually checks - see Astrid's Guide to Necromancy's
            // own GrantAstridBook for the same trick and why RegensHealth (equip-slot-only, inert on
            // a book) rather than SpecialArtifactEffect (which ItemHelper.GetItemInfo() specifically
            // reads as "this is the Oghma Infinium") was chosen.
            book.legacyMagic = new DaggerfallEnchantment[] { new DaggerfallEnchantment { type = EnchantmentTypes.RegensHealth, param = 0 } };

            playerEntity.Items.AddItem(book);
            DaggerfallUI.AddHUDText("A slim volume appears in your bag, bound in dark leather...");
            Log("GrantVampireBook: granted (book ID {0}).", VampireBookID);
        }

        // Books all share one TemplateIndex (Books.Book0) and ItemHelper.RegisterItemUseHandler keeps
        // only a single delegate per index (a straight dictionary overwrite, not a multicast) - so if
        // Astrid's Guide to Necromancy is also installed and registers on this same slot, whichever
        // mod's Init() runs LAST wins outright. This capture-and-chain makes THIS mod's registration
        // safe regardless of order (falls through to whatever was already registered for any other
        // book), but the reverse isn't guaranteed: necromancy's own OnAstridBookUsed doesn't chain to
        // a prior handler, so if it registers after this mod, vampirism's book-use grant would stop
        // firing (the book would still open and show its text either way, just without granting
        // spells).
        //
        // Checked against a real Player.log: "ModManager - started loading mod: Astrid's Guide to
        // Necromancy" appears before "...Astrid's Guide to Vampirism" for the load order this mod has
        // actually been tested under, so necromancy's handler is already registered by the time this
        // one runs and gets captured as previousHandler below - this caveat is not currently biting.
        // It would only become a problem if this mod's load order were moved ahead of necromancy's.
        static void RegisterBookUseHandler()
        {
            ItemHelper itemHelper = DaggerfallUnity.Instance.ItemHelper;
            ItemHelper.ItemUseHandler existingHandler;
            bool hadExisting = itemHelper.GetItemUseHandler((int)Books.Book0, out existingHandler);
            ItemHelper.ItemUseHandler previousHandler = hadExisting ? existingHandler : null;
            Log("RegisterBookUseHandler: existing handler on Books.Book0 found={0}.", hadExisting);

            itemHelper.RegisterItemUseHandler((int)Books.Book0, (item, collection) => OnBookUsed(item, collection, previousHandler));
        }

        // Book-driven transformation (Clan Features spec, §3). Every branch returns false so the
        // book's own text still opens normally afterward (confirmed via DaggerfallInventoryWindow.cs:
        // an ItemUseHandler returning true is what SKIPS the normal book-open code, so false is what
        // every existing branch here already relied on) - the new behavior is an additional side
        // effect layered on the same use action, exactly like the spell-grant HUD text already was.
        static bool OnBookUsed(DaggerfallUnityItem item, ItemCollection collection, ItemHelper.ItemUseHandler previousHandler)
        {
            if (item.message == VampireBookID)
            {
                PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                if (GameManager.Instance.PlayerEffectManager.HasVampirism())
                {
                    GrantSpellsFromBook(playerEntity);
                }
                else if (playerEntity.Level < 2)
                {
                    Log("OnBookUsed: level-1 non-vampire reader - no offer.");
                    DaggerfallUI.AddHUDText("Astrid's handwriting swims before your eyes - you are not ready for this yet. Return to me once you have grown.");
                }
                else
                {
                    Log("OnBookUsed: level-{0} non-vampire reader - will show transformation offer once the book closes.", playerEntity.Level);
                    ShowTransformationOfferAfterBookCloses(playerEntity);
                }
                return false;
            }

            return previousHandler != null && previousHandler(item, collection);
        }

        // The book itself only actually opens AFTER OnBookUsed returns (DaggerfallInventoryWindow's
        // own use-item handling queues DaggerfallUIMessages.dfuiOpenBookReaderWindow via PostMessage,
        // processed on a later DaggerfallUI.Update() tick, not synchronously here) - a message box
        // pushed directly from inside OnBookUsed gets buried the moment the book reader window opens
        // on top of it a frame later, making the offer invisible even though it fired (confirmed via
        // Player.log: the "showing transformation offer" line logged, but nothing was ever visible).
        // Waits for the book reader window to actually become the top window, THEN waits for it to
        // close again, before finally showing the real offer - IUserInterfaceManager.OnWindowChange
        // (DaggerfallUI.UIManager) fires on every push/pop, so this two-phase latch (sawBookOpen) is
        // robust regardless of exactly which tick the queued book-open message resolves on.
        static void ShowTransformationOfferAfterBookCloses(PlayerEntity playerEntity)
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            bool sawBookOpen = false;
            EventHandler handler = null;
            handler = (sender, args) =>
            {
                bool bookIsTop = uiManager.TopWindow is DaggerfallBookReaderWindow;
                if (bookIsTop)
                {
                    sawBookOpen = true;
                    return;
                }

                if (!sawBookOpen)
                    return;

                uiManager.OnWindowChange -= handler;
                ShowTransformationOffer(playerEntity);
            };
            uiManager.OnWindowChange += handler;
        }

        // Level 2+, non-vampire: offers the voluntary transformation path. On Yes, this is the exact
        // real, public API vanilla's own "diseaseplayer vampire" console command and its combat-bite
        // infection path both already use - CreateVampirismDisease() builds the stage-one disease
        // bundle, AssignBundle(..., BypassSavingThrows) makes it take hold unconditionally (a saving
        // throw against a curse you asked for wouldn't make sense). Everything downstream - the day-1
        // dream, day-3 fake-death, the two-week jump, the crypt teleport, DeployFullBlownVampirism -
        // lives entirely inside vanilla's own VampirismInfection and runs untouched from here.
        static void ShowTransformationOffer(PlayerEntity playerEntity)
        {
            string message = "The book draws you in, whispering of the dark gift it could bestow opon you. Vampirism. Do you accept?";
            // Built via the wrap-capable constructor + manual SetText/AddButton (mirroring what the
            // CommonMessageBoxButtons.YesNo convenience overload does internally) rather than that
            // overload directly - it has no wrapText parameter and no public way to enable wrapping
            // after construction, so every Yes/No prompt in this mod is built this way instead.
            DaggerfallMessageBox box = new DaggerfallMessageBox(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, wrapText: true);
            box.SetText(message);
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
            box.AddButton(DaggerfallMessageBox.MessageBoxButtons.No, true);
            box.OnButtonClick += (sender, button) =>
            {
                sender.CloseWindow();
                if (button != DaggerfallMessageBox.MessageBoxButtons.Yes)
                    return;

                EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
                EntityEffectBundle bundle = manager.CreateVampirismDisease();
                manager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);
                Log("ShowTransformationOffer: player accepted - vampirism disease bundle assigned.");
            };
            box.Show();
        }

        // Every vampire spell is granted unconditionally - no level gate, no qualification check.
        // Potency instead scales with Humanity at cast/tick time (see each effect's own file). Gated
        // on HasVampirism() when called from the book-use handler above - a pre-existing gap that
        // closed as a necessary side effect: previously ANY reader, vampire or not, silently received
        // these vampire-only spells.
        //
        // Public (not just the book-use handler's own concern anymore): VoxsVampirismEffect's own
        // clan-choice popup calls this directly the moment a clan is picked, so the spellbook is
        // populated automatically on transformation rather than requiring the player to go read the
        // book first. The book itself still grants the same way on read - now purely a backup/re-grant
        // path (e.g. a player who deliberately deleted a spell from their spellbook can get it back by
        // reading the book again), since AddSpell's own "already known by name" check here makes a
        // second call for an already-known spell a safe no-op either way.
        public static void GrantSpellsFromBook(PlayerEntity playerEntity)
        {
            EffectBundleSettings[] known = playerEntity.GetSpells();
            int grantedCount = 0;

            MigrateStaleMesmerizeSpell(playerEntity, known);

            for (int i = 0; i < bookSpellBundles.Count; i++)
            {
                EffectBundleSettings bundle = bookSpellBundles[i];

                bool alreadyKnown = false;
                for (int j = 0; j < known.Length; j++)
                {
                    if (known[j].Name == bundle.Name)
                    {
                        alreadyKnown = true;
                        break;
                    }
                }

                if (!alreadyKnown)
                {
                    playerEntity.AddSpell(bundle);
                    grantedCount++;
                }
            }

            // Haarvenu clan feature (Clan Features spec, §2): Vampiric Ice is offered only to a
            // Haarvenu vampire, kept out of the universal bookSpellBundles loop above.
            if (!string.IsNullOrEmpty(haarvenuSpellBundle.Name) && GetPlayerVampireClan() == VampireClans.Haarvenu)
            {
                bool alreadyKnownIce = false;
                for (int j = 0; j < known.Length; j++)
                {
                    if (known[j].Name == haarvenuSpellBundle.Name)
                    {
                        alreadyKnownIce = true;
                        break;
                    }
                }

                if (!alreadyKnownIce)
                {
                    playerEntity.AddSpell(haarvenuSpellBundle);
                    grantedCount++;
                }
            }

            if (grantedCount > 0)
                DaggerfallUI.AddHUDText(string.Format("You commit {0} new spell{1} to memory.", grantedCount, grantedCount == 1 ? "" : "s"));
            else
                DaggerfallUI.AddHUDText("The book holds nothing new for you yet.");
        }

        // Migration: a Mesmerize spellbook entry granted before its self-cast rework added
        // SupportDuration=true (see MesmerizeEffect.SetProperties' own header) still carries whatever
        // EffectSettings it was originally granted with - AddSpell snapshots the bundle onto the
        // player's OWN spellbook at grant time, it's never re-synced when this mod's own Create()
        // template changes later, and the "already known" check above matches by Name alone, so the
        // normal grant loop would never touch it again. That stale entry's DurationPerLevel/
        // DurationBase are both 0 (from before Mesmerize had any duration settings at all), and once
        // SupportDuration=true started requiring a nonzero DurationPerLevel to avoid a divide-by-zero
        // in the spellbook's own cost calculation, that stale entry crashed it every single frame the
        // spellbook was open (confirmed: a DivideByZeroException flood in Player.log, sourced from a
        // third-party mod's own parallel cost-formula override, not this mod's code - but triggered by
        // this mod's own stale data). A fresh MesmerizeEffect.Create() bundle always sets DurationBase
        // to 1, so 0 unambiguously means "granted before this fix" - detected and replaced in place via
        // SetSpell (preserves spellbook position/order) rather than left to accumulate forever.
        static void MigrateStaleMesmerizeSpell(PlayerEntity playerEntity, EffectBundleSettings[] known)
        {
            for (int j = 0; j < known.Length; j++)
            {
                if (known[j].Name != MesmerizeEffect.EffectKey)
                    continue;
                if (known[j].Effects == null || known[j].Effects.Length == 0)
                    continue;
                if (known[j].Effects[0].Settings.DurationBase != 0)
                    continue;

                playerEntity.SetSpell(j, MesmerizeEffect.Create());
                Log("MigrateStaleMesmerizeSpell: replaced stale Mesmerize spellbook entry (DurationBase was 0).");
                return;
            }
        }

        // Used by SneakFeedController on a failed Feed attempt (Feature 3). Vanilla's Crimes enum has
        // no vampirism-specific entry and, per Astrid's Guide to Necromancy's own documented reasoning
        // for its analogous case, a mod cannot add a genuinely new value to a compiled, fixed C# enum
        // that ships baked into the game executable - the spec's suggestion of a new "VampiricAssault"
        // crime type isn't actually possible for a real, separately-distributed mod (necromancy's own
        // comment on this exact constraint says as much, and reuses Crimes.Murder rather than adding
        // one). This reuses Crimes.Assault instead - the spec's own "closest existing fit" - rather
        // than Murder, since a failed feed is a struggle, not a killing.
        public static void ReportVampiricAssaultCrime()
        {
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            playerEntity.CrimeCommitted = PlayerEntity.Crimes.Assault;
            playerEntity.SpawnCityGuards(true);
        }
    }

    // DaggerfallListPickerWindow's own native layout (the PICK00I0.IMG background, list box fixed at
    // (26,27) size (138,72)) has no built-in room for a title - its pickerPanel/listBox fields are
    // protected specifically so a subclass can reach them, which is the only way to add one. Used by
    // SneakFeedController's own feed menu ("Hunt") and MesmerizeCommandMenu's command menu
    // ("Mesmerize") so the player always knows which menu they're looking at at a glance.
    public class TitledListPickerWindow : DaggerfallListPickerWindow
    {
        readonly string title;
        readonly TextLabel titleLabel = new TextLabel();

        public TitledListPickerWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous, string title)
            : base(uiManager, previous)
        {
            this.title = title;
        }

        protected override void Setup()
        {
            base.Setup();

            // Slides the list box down and shrinks it (keeping its bottom edge fixed, so the
            // scrollbar/prev-next buttons below stay aligned) to make guaranteed room for the title,
            // rather than assuming the native background's own top margin is already empty enough - a
            // shorter list box just means the existing scrollbar (already wired up by the base class)
            // kicks in a little sooner for a long option list, not a real cost for this mod's short
            // menus (4-5 items each). titleTopY tuned against live in-game screenshots - the first
            // attempt (a small ~12-unit gap) put the title right on the scroll's decorative rolled
            // top edge; the second (42) overshot, sitting too far down into the parchment; settled
            // here after both rounds of feedback.
            float originalListTop = listBox.Position.y;
            const float titleTopY = 30f;
            const float titleToListGap = 14f;
            float newListTop = titleTopY + titleToListGap;
            float downwardShift = newListTop - originalListTop;

            titleLabel.Text = title;
            titleLabel.Position = new Vector2(0, titleTopY);
            titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            pickerPanel.Components.Add(titleLabel);

            listBox.Position = new Vector2(listBox.Position.x, newListTop);
            listBox.Size = new Vector2(listBox.Size.x, listBox.Size.y - downwardShift);

            // Bug fix: ListBox.RowsDisplayed defaults to a hardcoded 9 (its own field initializer)
            // and is never auto-derived from Size - only SetRowsDisplayedByHeight() recalculates it,
            // and nothing was calling it here. That meant every TitledListPickerWindow rendered/hit-
            // tested as if it still had the full, unshrunk 72-unit box, silently working for this
            // mod's existing 3-item menus (well under 9 either way) but breaking the first menu to
            // actually need close to or more than 9 rows (the clan ceremony's 9-clan list) - the
            // lowest rows were laid out below the box's real (shrunk) clickable bounds, unclickable
            // even though drawn. Must run AFTER items are added (Count==0 is a no-op, per its own
            // implementation) - callers of this class already add items before Show()/PushWindow, and
            // Setup() itself only ever runs once the window is actually pushed, so Count is already
            // correct by the time this executes.
            listBox.SetRowsDisplayedByHeight();
        }
    }
}
