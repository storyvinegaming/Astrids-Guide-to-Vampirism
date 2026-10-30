// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// The Thrall system's roster, cap/band logic, break-free-on-rising-Humanity, and persistence. Create
// Thrall itself (the feed-menu option) and the thrall interaction menu (Feed/Murder/Create Vampire)
// live in SneakFeedController.cs, which calls into this class's public API - this file owns the
// roster and the rules, SneakFeedController owns the player-facing menu/roll flow that drives it.
//
// PERSISTENCE APPROACH (flagged per the spec's own request to be explicit about this): tracked
// roster entries + always-resummon, NOT live cross-scene GameObject persistence. Every thrall's live
// GameObject is destroyed and recreated fresh (full health, default AI state) on every scene
// transition and fast travel arrival - the same fundamental approach Astrid's Guide to Necromancy
// uses for its own reanimated allies (confirmed by reading VoxsNecromancy.cs/ReanimationSpawner.cs:
// RecordActiveReanimations/RestoreActiveReanimations destroy-and-respawn via CreateFoeGameObjects,
// not SetActive(true)/(false) toggling of the same object). Necromancy actually needs TWO separate
// systems for this - an in-memory snapshot for scene transitions within a session, plus a completely
// separate Mod.SaveDataInterface-based system for surviving a cold relaunch - because its ally data
// lives on live ReanimatedLifetime components that must be scanned/captured before every teardown.
// This mod's thralls don't have that problem: the roster (this file's own "thralls" list) is ALWAYS
// the single source of truth, updated directly by SneakFeedController's own actions (create/feed/
// murder/transform), never read back off live GameObjects - so one unified mechanism covers both
// scene transitions AND cold save/reload identically: materialize fresh from the roster, unmade
// entries simply given no live representation until they're needed again. The roster itself piggy-
// backs on VoxsVampirismEffect's own existing per-character save data (CustomSaveData_v1.thralls)
// rather than a separate Mod.SaveDataInterface file, since Humanity's own persisted state already
// lives there and thralls are conceptually the same kind of "this vampire's own condition" data.
//
// Materialization triggers mirror necromancy's own restore-side events exactly (PlayerEnterExit's
// four transition events, plus the same OnPreFastTravel/OnPostFastTravel/OnEnterLocationRect
// one-frame-position-lag workaround necromancy's own comment explains: OnPostFastTravel fires before
// the player's position has actually updated, so materializing there would place thralls at the OLD
// location - deferred to the next OnEnterLocationRect instead).

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod.Effects;
using FullSerializer;

namespace VoxsVampirismMod
{
    public class ThrallManager : MonoBehaviour
    {
        // Mutable runtime record - kept separate from the serializable ThrallRecord_v1 struct below
        // (which has no live GameObject reference, since GameObjects can't be serialized and don't
        // survive a save/reload anyway - see the file header on the persistence approach).
        class ThrallRuntimeRecord
        {
            public MobileTypes MobileType;
            public bool IsVampire;
            public uint CreationTime;
            public uint LastFedTime;
            public bool IsTransforming;
            public uint TransformCompleteTime;
            public GameObject LiveObject;
            public MobileTeams OriginalTeam;
            public MobileGender Gender;
        }

        // Oldest-first by construction - always appended to the end, only ever removed from wherever
        // the specific record is (index 0 for "oldest"), never reordered.
        static readonly List<ThrallRuntimeRecord> roster = new List<ThrallRuntimeRecord>();

        public static int ActiveThrallCount => roster.Count;

        static readonly MobileTypes[] mortalCandidates = { MobileTypes.Thief, MobileTypes.Knight, MobileTypes.Mage };

        // The Legion System update: flat upkeep costs against the shared AllyCombatController budget
        // (VoxsVampirism.GetLegionBudget()), replacing the old Humanity-band headcount table entirely.
        // Matches Astrid's Guide to Necromancy's own Zombie/Vampire tier costs exactly, per explicit
        // request - a mortal thrall costs the same as Necromancy's own Zombie tier, a created vampire
        // the same as its own Vampire tier. Dominate uses its own MaxHealth-scaled cost instead (see
        // DominateEffect.cs) since it has no fixed type/tier to key a flat cost off.
        internal const int ThrallUpkeepCost = 56;
        internal const int VampireUpkeepCost = 85;

        static uint NowClassic()
        {
            return DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        }

        #region Creation (called from SneakFeedController's Create Thrall option)

        // Caller (SneakFeedController) is responsible for the roll/cap/hunger-stage-4 checks before
        // calling this - this method itself always succeeds at creating one, unconditionally.
        public static void CreateThrall(Vector3 position)
        {
            MobileTypes chosen = mortalCandidates[Random.Range(0, mortalCandidates.Length)];

            ThrallRuntimeRecord record = new ThrallRuntimeRecord
            {
                MobileType = chosen,
                IsVampire = false,
                CreationTime = NowClassic(),
                LastFedTime = 0,
                IsTransforming = false,
                TransformCompleteTime = 0,
                // Rolled once here and reused on every future Materialize() call (scene transitions,
                // fast travel, save/load, Create Vampire's own re-materialize) - previously this was
                // left as MobileGender.Unspecified and passed straight through to CreateEnemy, which
                // rolls its own random gender whenever it sees Unspecified, so a thrall's model
                // visibly changed gender every time it got re-materialized.
                Gender = Random.value < 0.5f ? MobileGender.Male : MobileGender.Female,
            };

            Materialize(record, position);
            roster.Add(record);

            VoxsVampirism.Log("Thrall created: type={0}. Roster count={1}.", chosen, roster.Count);
        }

        // Post-Feed Offers update - Create Vampire offer entry point (NarrativeFeedSceneEngine). Reuses
        // the existing creation + transform machinery outright rather than reimplementing either:
        // CreateThrall's own roster-add always appends to the end (oldest-first, see the roster field's
        // own comment), so the just-created record is always the last one, and StartTransform expects
        // exactly a live roster member's own GameObject (FindByLiveObject) - never callable directly on
        // a fresh townsperson. The brief intermediate "ordinary mortal thrall" state this passes through
        // is never actually visible to the player: the offer's own victim GameObject was already hidden
        // before this is ever called, and StartTransform now completes the swap to a vampire instantly
        // (no dormant window at all - see StartTransform's own header) within this same call, before
        // the next rendered frame.
        public static void CreateThrallAndStartTransform(Vector3 position)
        {
            CreateThrall(position);
            if (roster.Count > 0)
                StartTransform(roster[roster.Count - 1].LiveObject);
        }

        static void Materialize(ThrallRuntimeRecord record, Vector3 position)
        {
            MobileTypes type = record.IsVampire ? MobileTypes.Vampire : record.MobileType;

            // insideUnitSphere jitters all three axes, not just XZ - left uncorrected, the Y component
            // (up to 3 units either way) could place a fresh thrall/vampire below the floor with
            // nothing underneath to catch it, and CharacterController never recovers from spawning
            // already inside solid geometry. Necromancy's own restore step (VoxsNecromancy.
            // RestoreActiveReanimations) hits the exact same insideUnitSphere offset and fixes it the
            // same way - clamping Y back to the known-good source position (the player, an already-
            // materialized thrall, or the townsperson being converted, all of which are always
            // standing on valid ground) rather than trusting the sphere's own vertical component.
            Vector3 spawnPosition = position + Random.insideUnitSphere * 3f;
            spawnPosition.y = position.y;

            GameObject go = GameObjectHelper.CreateEnemy(type.ToString(), type, spawnPosition, record.Gender, null, MobileReactions.Passive);

            // THE save-load duplication fix (the isLoadingSave transition-gating above was necessary
            // but not sufficient): DFU's own save system (SerializableEnemy, attached by the enemy
            // prefab CreateEnemy just instantiated) was serializing every ally into the save like an
            // ordinary dungeon enemy, and on load SerializableStateManager unconditionally
            // reconstructs every serialized enemy as a flat copy - right type and team, none of this
            // mod's components - completely independent of MaterializeAll's own roster respawn. One
            // inert duplicate appeared per ally per load, then got re-serialized, compounding every
            // save/load cycle. Necromancy hit the same collision and chose to reconcile against
            // DFU's restored copies (VoxsNecromancy.ReconcileAfterLoad); this mod's design already
            // respawns allies fresh from the roster on every transition, so DFU's copy is pure
            // poison here - removing the component (it deregisters itself in its own OnDestroy)
            // keeps allies out of DFU's enemy serialization entirely. PurgeStrayAlliesAfterLoad
            // (below) cleans the copies already baked into saves made before this fix existed.
            SerializableEnemy serializableEnemy = go.GetComponent<SerializableEnemy>();
            if (serializableEnemy != null)
                Destroy(serializableEnemy);

            DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity != null)
            {
                record.OriginalTeam = enemyEntity.Team;
                enemyEntity.Team = MobileTeams.PlayerAlly;
            }

            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            if (motor != null)
                motor.IsHostile = false;

            AllyCombatController controller = go.AddComponent<AllyCombatController>();
            controller.Upkeep = record.IsVampire ? VampireUpkeepCost : ThrallUpkeepCost;
            // Closes over record, not go/controller - re-set fresh on every Materialize() call (initial
            // creation, every scene transition/fast travel/load re-materialize alike), so it always
            // targets whichever record this call is currently materializing, never a stale one from an
            // earlier live GameObject that's since been destroyed and replaced.
            controller.OnReleaseRequested = reason => ReleaseThrall(record, BuildBudgetCullMessage(record));
            // Dormant transforming thralls stand by rather than following/fighting (the simpler of
            // the two options the spec offered - see StartTransform's own comment) - achieved by
            // just disabling the whole ally-AI component rather than adding separate dormant-state
            // handling inside it. OnEnable/OnDisable still fire either way, so a dormant thrall still
            // correctly counts against the shared budget while transforming.
            controller.enabled = !record.IsTransforming;

            record.LiveObject = go;
        }

        // Shared by both the budget-driven cull (AllyCombatController.CullOldestUntilBudgetFits, via
        // OnReleaseRequested above) and the old Humanity-band break-free message this replaces -
        // "becoming fully human costs you the last of your hold" only when Humanity is AT its ceiling
        // right now (checked fresh, not "was this the last crossing in a multi-step change" - simpler,
        // and reads identically in practice since 10 is still 10 regardless of how the cull batch got
        // there).
        static string BuildBudgetCullMessage(ThrallRuntimeRecord record)
        {
            if (VoxsVampirism.GetPlayerHumanity() >= 10)
            {
                return string.Format(
                    "Becoming fully human costs you the last of your hold - your {0} realizes, all at once, that it was never truly yours, and turns on you.",
                    record.IsVampire ? "vampire" : "thrall");
            }

            return record.IsVampire
                ? "The bond breaks - the vampire you made shakes off your hold and turns on you, hungry and free."
                : "Your hold falters - the thrall's own will comes rushing back, and it turns on you.";
        }

        #endregion

        #region Query (used by SneakFeedController's raycast dispatch to recognize a thrall)

        public static bool IsThrall(GameObject liveObject)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].LiveObject == liveObject)
                    return true;
            }

            return false;
        }

        static ThrallRuntimeRecord FindByLiveObject(GameObject liveObject)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].LiveObject == liveObject)
                    return roster[i];
            }

            return null;
        }

        #endregion

        #region Thrall menu actions (called from SneakFeedController)

        // Dormant (transforming) thralls can't be fed on, and the same 24h-per-NPC cooldown shape
        // SneakFeedCooldownMarker already uses for townspeople applies per-thrall here too - both
        // checked up front so a blocked attempt never touches hunger/heal/break-free at all. Once
        // past those, the feed itself always applies regardless of the break-free roll below - the
        // roll only decides whether the thrall stays bound afterward.
        public static bool TryFeedOnThrall(GameObject liveObject)
        {
            ThrallRuntimeRecord record = FindByLiveObject(liveObject);
            if (record == null)
                return false;

            if (record.IsTransforming)
            {
                DaggerfallUI.AddHUDText("Your thrall lies dormant, changing - it cannot be fed on now.");
                return false;
            }

            uint currentTime = NowClassic();
            if (record.LastFedTime != 0 && currentTime - record.LastFedTime < DaggerfallDateTime.MinutesPerDay)
            {
                DaggerfallUI.AddHUDText("You have already taken from this one today.");
                return false;
            }

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return false;

            int amount = record.IsVampire ? 6 : 3;
            vampirism.GainBloodNoHumanity(amount);
            record.LastFedTime = NowClassic();

            DaggerfallUI.AddHUDText(string.Format(
                record.IsVampire ? "You drink deep from the vampire you made. (Blood: {0}/10.)"
                                  : "You feed from your thrall, who endures it without complaint. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));

            int humanity = VoxsVampirism.GetPlayerHumanity();
            bool safe = Dice100.SuccessRoll(100 - humanity * 10);
            if (!safe)
            {
                DaggerfallUI.AddHUDText(record.IsVampire
                    ? "The vampire you made recoils from the bite - your hold breaks, and it turns on you!"
                    : "Your thrall's own will surges back at the bite - your hold breaks, and it turns on you!");
                ReleaseThrall(record, null);
            }

            return true;
        }

        // Real death (SetHealth(0)) rather than the townsperson fake-kill technique - a thrall is a
        // genuine live EnemyEntity, so it gets a genuine death, dropping normal loot through DFU's
        // own EnemyDeath.CompleteDeath() pipeline. Roster cleanup happens automatically via this
        // class's own EnemyDeath.OnEnemyDeath subscription (HandleEnemyDeath below) rather than here,
        // so a thrall killed any other way (e.g. by an enemy in a fight) is cleaned up identically.
        public static void MurderThrall(GameObject liveObject)
        {
            DaggerfallEntityBehaviour behaviour = liveObject.GetComponent<DaggerfallEntityBehaviour>();
            if (behaviour != null && behaviour.Entity != null)
                behaviour.Entity.SetHealth(0);
        }

        // Devour Thrall (DevourThrallEffect.cs) entry point - kills a thrall/created vampire outright
        // (same real death as MurderThrall above) and reports how much blood a normal feed on them
        // would have granted (+3/+6, matching TryFeedOnThrall's own amounts), so the caller can apply
        // it via VoxsVampirismEffect.GainBloodNoHumanity for the same heal/sound/flash side effects a
        // normal feed already gets. Returns -1 if liveObject isn't a currently-tracked thrall at all -
        // the caller's own ChanceSuccess check should already have ruled that out for anything except a
        // Dominated creature, which this class knows nothing about (DevourThrallEffect handles that
        // case itself).
        public static int DevourThrall(GameObject liveObject)
        {
            ThrallRuntimeRecord record = FindByLiveObject(liveObject);
            if (record == null)
                return -1;

            int amount = record.IsVampire ? 6 : 3;
            MurderThrall(liveObject);
            return amount;
        }

        // Instant now - no dormant wind-up window at all, the vampire rises the moment the player
        // agrees (CompleteTransformation below does the actual swap). The old IsTransforming/
        // TransformCompleteTime dormant path (and CheckTransformations' own per-round tick) is left in
        // place purely so a save made before this change, with a thrall genuinely mid-transformation
        // at the time, still finishes correctly on its own timer - StartTransform itself never sets
        // that state anymore, so no new transformation can ever land in it.
        public static void StartTransform(GameObject liveObject)
        {
            ThrallRuntimeRecord record = FindByLiveObject(liveObject);
            if (record == null || record.IsVampire || record.IsTransforming)
                return;

            CompleteTransformation(record);
        }

        #endregion

        #region Break-free on rising Humanity (called from VoxsVampirismEffect.ChangeHumanity)

        // The Legion System update: was a Humanity-band headcount table crossing check; now just
        // re-checks the shared budget on every Humanity change (either direction - rising Humanity is
        // the only direction that can actually shrink GetLegionBudget() under the new formula, but
        // checking unconditionally costs nothing when nothing needs to give). Delegates the actual
        // release to AllyCombatController.CullOldestUntilBudgetFits, which releases oldest-first across
        // EVERY active ally - thralls, created vampires, AND currently-Dominated creatures alike, since
        // they now share one pool - via each ally's own OnReleaseRequested callback (thralls/vampires
        // route back through ReleaseThrall below; Dominate's own targets route through DominateTimer.
        // ReleaseNow - see DominateEffect.cs).
        public static void OnHumanityChanged(int oldHumanity, int newHumanity)
        {
            AllyCombatController.CullOldestUntilBudgetFits();
        }

        static void ReleaseThrall(ThrallRuntimeRecord record, string message)
        {
            roster.Remove(record);

            if (record.LiveObject != null)
            {
                DaggerfallEntityBehaviour behaviour = record.LiveObject.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity != null)
                    enemyEntity.Team = record.OriginalTeam;

                EnemyMotor motor = record.LiveObject.GetComponent<EnemyMotor>();
                if (motor != null)
                    motor.IsHostile = true;

                EnemySenses senses = record.LiveObject.GetComponent<EnemySenses>();
                if (senses != null)
                {
                    senses.Target = null;
                    senses.SecondaryTarget = null;
                }

                AllyCombatController controller = record.LiveObject.GetComponent<AllyCombatController>();
                if (controller != null)
                    Destroy(controller);

                // A freed mortal thrall can still be Mesmerized to spare it (its EntityType==
                // EnemyClass already makes it eligible under MesmerizeEffect's own targeting check) -
                // a freed created vampire cannot (EntityType==EnemyMonster + EnemyGroup==Undead falls
                // outside that same check already) - see DeEscalatableMarker's own header for why no
                // extra exclusion logic is needed here for the vampire case.
                DeEscalatableMarker marker = record.LiveObject.AddComponent<DeEscalatableMarker>();
                marker.IsFightsBackSpawn = false;
            }

            if (!string.IsNullOrEmpty(message))
                DaggerfallUI.AddHUDText(message);

            VoxsVampirism.Log("Thrall released: type={0}, isVampire={1}. Roster count={2}.", record.MobileType, record.IsVampire, roster.Count);
        }

        #endregion

        #region Per-round ticks (transform timer, thrall-death roster cleanup)

        void OnEnable()
        {
            EntityEffectBroker.OnNewMagicRound += CheckTransformations;
            EnemyDeath.OnEnemyDeath += HandleEnemyDeath;

            PlayerEnterExit.OnTransitionExterior += OnTransition;
            PlayerEnterExit.OnTransitionInterior += OnTransition;
            PlayerEnterExit.OnTransitionDungeonExterior += OnTransition;
            PlayerEnterExit.OnTransitionDungeonInterior += OnTransition;

            // Dominate-follows-across-transitions fix (see RecordActiveDominatesForTransition's own
            // header) - the departure-side half. Necromancy's identical mechanism for its own allies
            // hooks both of these for the same reason: an ordinary walk through a door only ever
            // raises OnPreTransition, but fast travel raises OnPreFastTravel instead, never
            // OnPreTransition at all.
            PlayerEnterExit.OnPreTransition += OnPreTransition;
            DaggerfallTravelPopUp.OnPreFastTravel += OnPreFastTravelForDominates;

            DaggerfallTravelPopUp.OnPostFastTravel += OnPostFastTravel;
            PlayerGPS.OnEnterLocationRect += OnEnterLocationRect;

            SaveLoadManager.OnStartLoad += OnStartLoad;
            SaveLoadManager.OnLoad += OnSaveLoaded;
        }

        void OnDisable()
        {
            EntityEffectBroker.OnNewMagicRound -= CheckTransformations;
            EnemyDeath.OnEnemyDeath -= HandleEnemyDeath;

            PlayerEnterExit.OnTransitionExterior -= OnTransition;
            PlayerEnterExit.OnTransitionInterior -= OnTransition;
            PlayerEnterExit.OnTransitionDungeonExterior -= OnTransition;
            PlayerEnterExit.OnTransitionDungeonInterior -= OnTransition;

            PlayerEnterExit.OnPreTransition -= OnPreTransition;
            DaggerfallTravelPopUp.OnPreFastTravel -= OnPreFastTravelForDominates;

            DaggerfallTravelPopUp.OnPostFastTravel -= OnPostFastTravel;
            PlayerGPS.OnEnterLocationRect -= OnEnterLocationRect;

            SaveLoadManager.OnStartLoad -= OnStartLoad;
            SaveLoadManager.OnLoad -= OnSaveLoaded;
        }

        // Bug fix (mirrors Astrid's Guide to Necromancy's own identical fix for its own reanimated
        // allies - VoxsNecromancy.isLoadingSave/OnStartLoad/ReconcileAfterLoad - confirmed by reading
        // that mod's own source directly): loading a save routes through the exact same PlayerEnterExit
        // transition events a live transition does (SaveLoadManager.LoadGame() calls
        // RestorePositionHelper(), which raises OnTransitionX same as walking through a door) - WITHOUT
        // this flag, OnTransition's own MaterializeAll() call below ALSO fires during a load, on top of
        // OnSaveLoaded's own MaterializeAll() call moments later, and since MaterializeAll() only
        // destroys whatever LiveObject each record ALREADY points to before spawning a fresh one -
        // Destroy() is deferred to end-of-frame, not immediate - a second MaterializeAll() call in the
        // same load window doesn't correctly clean up the FIRST call's own freshly-spawned live objects
        // either, so a duplicate set piled up on every single load. This was the exact wrong assumption
        // an earlier version of this fix made ("MaterializeAll() already destroys-and-respawns
        // idempotently either way" - it doesn't, across two SEPARATE calls in the same frame).
        //
        // isLoadingSave is set true the instant a load starts (SaveLoadManager.OnStartLoad, fires
        // before any of the load's own PlayerEnterExit events) and reset false only once OnSaveLoaded
        // itself runs (SaveLoadManager.OnLoad fires last, after DFU's own RestoreSaveData/RestoreRecords
        // has already completed) - covering the whole load sequence, not just the moment the roster data
        // itself gets restored. OnTransition (below) skips its own MaterializeAll() call entirely while
        // this is true, so ONLY OnSaveLoaded's own call ever actually materializes anything during a load.
        static bool isLoadingSave;

        // Bug fix (reported live: loading a save made BEFORE the character ever became a vampire
        // spawned a vampire thrall that belonged to a completely different, later save). Root cause:
        // roster/dominatesToRestore are static, so they persist in memory across a Load Game within
        // the same running session - they're only ever reset by RestoreRecords/RestoreDominateRecords,
        // both of which are called from VoxsVampirismEffect.RestoreSaveData, which only runs at all if
        // THIS save actually has vampirism custom save data to restore (an EntityEffectManager restore
        // callback on an effect instance that, for a save where the character was never a vampire,
        // never gets created in the first place - and RestoreSaveData(object dataIn) itself early-
        // returns on a null dataIn without touching either stash regardless). Loading save B (no
        // vampirism data) right after save A (had a thrall) left roster still holding save A's thrall
        // record; OnSaveLoaded's own MaterializeAll() call further down doesn't know or care which
        // save produced its data, so it faithfully spawned save A's thrall into save B's world.
        // OnStartLoad fires unconditionally on EVERY load, regardless of the target save's own content
        // (registered in OnEnable above, never conditional) - clearing both stashes here first means
        // they only end up non-empty if THIS load's own RestoreRecords/RestoreDominateRecords actually
        // runs afterward with real data, exactly like a save's own missing/absent data should behave.
        static void OnStartLoad(SaveData_v1 saveData)
        {
            isLoadingSave = true;
            roster.Clear();
            dominatesToRestore = null;
            // Same stale-static-state family as the two clears above - a load that happens between a
            // transition's Record and Restore would otherwise replay the old records after the load.
            dominatesInTransit.Clear();
            DestroyPreLoadDominates();
        }

        // Bug fix (reported live: loading an earlier save that had 2 dominated humans "spawned with
        // the multiple humans" - the pre-load session's extra dominates, all genuinely functional,
        // instantly present the moment the load finished). Root cause: a dominated creature respawned
        // by a transition (RestoreDominatesAfterTransition -> CreateEnemy) has its SerializableEnemy
        // deliberately stripped, so it exists in NO save file at all - and loading a save doesn't
        // always tear the streamed world down (an exterior-to-exterior load in the same region keeps
        // it), so those session-only creatures survive INTO the loaded game. PurgeStrayAlliesAfterLoad
        // can't catch them: it deliberately spares every DominateTimer-carrier, because by the time it
        // runs, RestoreDominatedAllies' own legitimately-reattached creatures carry one too (see its
        // own comment for the load where that sweep destroyed the freshly-restored giant). At
        // OnStartLoad time there's no such ambiguity - the restore hasn't run yet, so EVERY live
        // DominateTimer in the scene belongs to the dying pre-load session, unconditionally:
        //  - no SerializableEnemy = one of this mod's own transition respawns, part of no save ever -
        //    the whole GameObject goes;
        //  - has SerializableEnemy = a real DFU-serialized world enemy the incoming load owns the
        //    state of - only this mod's components are stripped (DestroyImmediate, not Destroy: plain
        //    Destroy defers to end-of-frame, and RestoreDominatedAllies' own find-or-add later this
        //    same load would find the dying component and write the restored state into an object
        //    about to be wiped), leaving DFU to restore the enemy itself; if the incoming save has a
        //    dominate record for it, RestoreDominatedAllies re-adds fresh components from that record,
        //    and if it doesn't, the creature correctly comes back as whatever the save says it was.
        static void DestroyPreLoadDominates()
        {
            DominateTimer[] timers = UnityEngine.Object.FindObjectsOfType<DominateTimer>();
            for (int i = 0; i < timers.Length; i++)
            {
                DominateTimer timer = timers[i];
                if (timer.GetComponent<SerializableEnemy>() == null)
                {
                    VoxsVampirism.Log("DestroyPreLoadDominates: destroying pre-load session dominate '{0}' (mod-spawned, part of no save).", timer.name);
                    Destroy(timer.gameObject);
                }
                else
                {
                    VoxsVampirism.Log("DestroyPreLoadDominates: stripping pre-load dominate components from serialized enemy '{0}' (incoming save owns its state).", timer.name);
                    AllyCombatController controller = timer.GetComponent<AllyCombatController>();
                    if (controller != null)
                        DestroyImmediate(controller);
                    DestroyImmediate(timer);
                }
            }
        }

        static void OnSaveLoaded(SaveData_v1 saveData)
        {
            isLoadingSave = false;
            // Order matters: reattach dominated creatures FIRST (gives them their DominateTimer/
            // AllyCombatController back), THEN sweep - the sweep treats a PlayerAlly enemy with
            // neither component as a stale leftover to revert, so reattachment must win the race.
            RestoreDominatedAllies();
            PurgeStrayAlliesAfterLoad();
            MaterializeAll();
        }

        #region Dominate persistence (VoxsVampirismEffect.CustomSaveData_v5.dominates calls into this)

        // Dominate save-load fix: a dominated creature is an ordinary DFU-serialized world enemy
        // (SerializableEnemy - deliberately NOT stripped from it the way Materialize strips it from
        // this mod's own spawned thralls, since the creature itself must survive in the save), so
        // DFU faithfully saves and restores its flipped PlayerAlly team and IsHostile=false - but
        // the components that actually make it BEHAVE dominated (DominateTimer's revert countdown,
        // AllyCombatController's follow/fight AI) are plain MonoBehaviours that no save system
        // persists. Loading therefore produced a passive, allied-team husk that neither followed
        // nor ever reverted to hostile - permanently broken, compounding across every dominated
        // creature ever saved. Necromancy's reanimated allies never had this problem because that
        // mod records its allies' LoadIDs in its own save data and reattaches components to DFU's
        // restored enemies after load (VoxsNecromancy.ReconcileAfterLoad) - this is the same
        // mechanism for Dominate: snapshot every live DominateTimer at save time (keyed by the
        // enemy's own DFU LoadID, the same stable identity Necromancy's reconcile matches on),
        // stash the records during restore, and reattach in OnSaveLoaded once DFU has finished
        // rebuilding the scene's enemies.
        [fsObject("v1")]
        public struct DominateRecord_v1
        {
            public ulong loadID;
            public int roundsRemaining;
            public int originalTeam;
            public int upkeep;
            // Added alongside TryFeedOnDominated's own 24h cooldown fix - a save made before this
            // field existed deserializes it as 0 ("never fed"), which is exactly the correct/safe
            // default, so no separate versioned struct was needed for this addition.
            public uint lastFedTime;
            // Added alongside the record-and-respawn-is-a-move fix: a dominated creature that has
            // traveled through any transition is one of this mod's own CreateEnemy spawns
            // (SerializableEnemy stripped, DaggerfallEnemy.LoadID 0) - it exists in NO DFU-serialized
            // scene data, so a loadID of 0 here means "spawn a fresh creature of mobileTypeId near
            // the player" rather than "match a restored world enemy". A save made before this field
            // existed deserializes it as 0, but such a save also never produced a loadID-0 record in
            // the first place (they were skipped at save time entirely), so 0-means-Rat can never be
            // misread from old data.
            public int mobileTypeId;
        }

        public static DominateRecord_v1[] GetDominateSaveRecords()
        {
            DominateTimer[] timers = UnityEngine.Object.FindObjectsOfType<DominateTimer>();
            List<DominateRecord_v1> records = new List<DominateRecord_v1>();
            for (int i = 0; i < timers.Length; i++)
            {
                DominateTimer timer = timers[i];

                DaggerfallEntityBehaviour behaviour = timer.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity == null || enemyEntity.CurrentHealth <= 0)
                    continue;

                // LoadID 0 = one of this mod's own traveling spawns, which DFU never serializes -
                // previously SKIPPED here outright, which silently lost every dominated creature from
                // the save the moment it had traveled through even one transition (its original, the
                // only serialized copy, no longer exists). Saved with loadID 0 + mobileTypeId instead,
                // which RestoreDominatedAllies spawns fresh near the player rather than LoadID-matching.
                DaggerfallEnemy enemy = timer.GetComponent<DaggerfallEnemy>();
                ulong loadID = enemy != null ? enemy.LoadID : 0;

                AllyCombatController controller = timer.GetComponent<AllyCombatController>();
                records.Add(new DominateRecord_v1
                {
                    loadID = loadID,
                    roundsRemaining = timer.RoundsRemaining,
                    originalTeam = (int)timer.OriginalTeam,
                    upkeep = controller != null ? controller.Upkeep : 1,
                    lastFedTime = timer.LastFedTime,
                    mobileTypeId = enemyEntity.MobileEnemy.ID,
                });
            }

            return records.ToArray();
        }

        // Stashed at RestoreSaveData time (which runs mid-load, before DFU has finished restoring
        // enemies) and consumed by RestoreDominatedAllies once OnLoad fires - same deferred shape
        // the thrall roster's own restore-then-materialize already uses.
        static DominateRecord_v1[] dominatesToRestore;

        public static void RestoreDominateRecords(DominateRecord_v1[] records)
        {
            dominatesToRestore = records;
        }

        static void RestoreDominatedAllies()
        {
            DominateRecord_v1[] records = dominatesToRestore;
            dominatesToRestore = null;
            if (records == null || records.Length == 0)
                return;

            DaggerfallEnemy[] allEnemies = UnityEngine.Object.FindObjectsOfType<DaggerfallEnemy>();
            int restored = 0;
            for (int r = 0; r < records.Length; r++)
            {
                DominateRecord_v1 record = records[r];

                // loadID 0 = a mod-spawned traveling copy with no DFU-serialized world enemy to match
                // (see DominateRecord_v1.mobileTypeId's own comment) - spawned fresh near the player
                // from record state instead, exactly like arriving after a transition.
                if (record.loadID == 0)
                {
                    SpawnDominatedCreature((MobileTypes)record.mobileTypeId, record.roundsRemaining,
                        (MobileTeams)record.originalTeam, record.upkeep, record.lastFedTime);
                    restored++;
                    continue;
                }

                DaggerfallEnemy match = null;
                for (int i = 0; i < allEnemies.Length; i++)
                {
                    DaggerfallEnemy candidate = allEnemies[i];
                    if (candidate != null && candidate.LoadID == record.loadID)
                    {
                        match = candidate;
                        break;
                    }
                }

                if (match == null)
                {
                    VoxsVampirism.Log("RestoreDominatedAllies: no enemy with LoadID {0} in the loaded scene - that binding is gone.", record.loadID);
                    continue;
                }

                DaggerfallEntityBehaviour behaviour = match.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity == null || enemyEntity.CurrentHealth <= 0)
                    continue;

                // Same wiring DominateEffect.MagicRound does at cast time, minus the parts DFU's own
                // enemy restore already reproduced (team/hostility state) - reasserted anyway, cheap
                // and makes this correct even if DFU's own restore of those fields ever changes.
                enemyEntity.Team = MobileTeams.PlayerAlly;
                EnemyMotor motor = match.GetComponent<EnemyMotor>();
                EnemySenses senses = match.GetComponent<EnemySenses>();
                if (motor != null)
                    motor.IsHostile = false;
                if (senses != null)
                {
                    senses.Target = null;
                    senses.SecondaryTarget = null;
                }

                // Reuse an existing timer rather than stacking a second one - a pre-load dominated
                // creature can survive scene persistence (exterior-to-exterior load) with its timer
                // still attached; the saved record is authoritative for its state either way.
                DominateTimer timer = match.GetComponent<DominateTimer>();
                if (timer == null)
                    timer = match.gameObject.AddComponent<DominateTimer>();
                timer.RoundsRemaining = record.roundsRemaining;
                timer.OriginalTeam = (MobileTeams)record.originalTeam;
                timer.LastFedTime = record.lastFedTime;

                AllyCombatController controller = match.GetComponent<AllyCombatController>();
                if (controller == null)
                    controller = match.gameObject.AddComponent<AllyCombatController>();
                controller.Upkeep = record.upkeep;

                // Player-collision-ignore needs no one-shot call here (an earlier version had one,
                // and it still failed): Physics.IgnoreCollision pairs get silently wiped whenever
                // either collider is deactivated, which the load sequence does to the player after
                // this handler runs - AllyCombatController.EnsurePlayerCollisionIgnored reasserts the
                // pair every physics tick instead, covering every ally (thrall or dominated, fresh or
                // reused controller) uniformly.

                // Follower ambient "attract" sounds (EnemySounds.PlayAttractSound - idle moans/barks/
                // moves triggered purely by player proximity, no team check of its own at all) read
                // wrong for something at your own side - silenced for the same reason a real thrall
                // would be, restored by DominateTimer.Revert if this creature ever reverts to hostile.
                EnemySounds enemySounds = match.GetComponent<EnemySounds>();
                if (enemySounds != null)
                    enemySounds.enabled = false;

                string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                controller.OnReleaseRequested = reason => timer.ReleaseNow(
                    string.Format("The bond breaks - your hold has grown too thin to keep the {0} bound, and it turns on you.", name));

                restored++;
            }

            VoxsVampirism.Log("RestoreDominatedAllies: reattached {0}/{1} dominated creature(s) after load.", restored, records.Length);
        }

        // Bug fix: a Dominated creature followed the player across an ordinary save/load (see
        // RestoreDominatedAllies above) but NOT across an ordinary scene transition - walking out of
        // a dungeon, entering a building, or fast traveling all tear down and rebuild the current
        // scene's enemies same as DFU always does for non-quest enemies, and unlike a thrall (which
        // is respawned fresh from the persistent roster on every single transition via MaterializeAll)
        // a Dominated creature has no roster entry of its own at all - its only state is the live
        // DominateTimer/AllyCombatController sitting on a GameObject that's about to be destroyed.
        // Mirrors Astrid's Guide to Necromancy's own identical fix for its own reanimated allies
        // (VoxsNecromancy.RecordActiveReanimations/RestoreActiveReanimations) exactly: snapshot every
        // live DominateTimer just BEFORE the scene tears down (OnPreTransition/OnPreFastTravel, fired
        // department-side), then spawn a brand-new enemy of the same type near the player once arrived
        // (OnTransition/OnEnterLocationRect, mirroring ThrallManager.Materialize's own respawn shape) -
        // unlike RestoreDominatedAllies's own LoadID matching (meaningless here; the destination scene
        // has no relationship at all to whatever the creature's LoadID was in the old one). Gated on
        // isLoadingSave the same as everything else here, since a load ALSO fires these same
        // PlayerEnterExit events as a side effect - RestoreDominatedAllies already owns that case.
        struct DominateTransitionRecord
        {
            public MobileTypes mobileType;
            public int roundsRemaining;
            public MobileTeams originalTeam;
            public int upkeep;
            public uint lastFedTime;
            // Bug fix (reported live: a Dominated creature whose duration should have run out mid-
            // fast-travel still showed up under control on arrival, requiring the player to wait out
            // its FULL original duration again). No live DominateTimer exists between
            // RecordActiveDominatesForTransition and RestoreDominatesAfterTransition - the component
            // (and its EntityEffectBroker.OnNewMagicRound subscription) is destroyed along with the
            // old scene before travel and only recreated after - so every magic round that fires
            // during the trip (DFU's own EntityEffectBroker.Update() catches up on elapsed classic-
            // minutes in a burst, capped at 2 in-game days per its own maxCatchupDays) ticks nothing
            // down for it. roundsRemaining was being restored completely unadjusted, as if no time had
            // passed at all. Stamping the classic-minute clock here lets the restore side compute how
            // many rounds SHOULD have ticked away during the trip and subtract them.
            public uint recordedAtClassicMinute;
        }

        static readonly List<DominateTransitionRecord> dominatesInTransit = new List<DominateTransitionRecord>();

        static void RecordActiveDominatesForTransition()
        {
            if (isLoadingSave)
                return;

            dominatesInTransit.Clear();

            DominateTimer[] timers = UnityEngine.Object.FindObjectsOfType<DominateTimer>();
            for (int i = 0; i < timers.Length; i++)
            {
                DominateTimer timer = timers[i];

                DaggerfallEntityBehaviour behaviour = timer.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity == null || enemyEntity.CurrentHealth <= 0)
                    continue;

                // A dominated quest Foe stays home rather than traveling - destroying a quest
                // resource's live GameObject (see below - every recorded creature is destroyed) risks
                // breaking its quest's own machinery, and respawning it via CreateEnemy would produce
                // a non-quest copy the quest can't see either. It keeps its binding in its home scene
                // (the pre-record-and-respawn behavior for everything), it just doesn't follow.
                if (timer.GetComponent<DaggerfallWorkshop.Game.Questing.QuestResourceBehaviour>() != null)
                {
                    VoxsVampirism.Log("RecordActiveDominatesForTransition: '{0}' is a quest resource - staying in its home scene, not traveling.", timer.name);
                    continue;
                }

                AllyCombatController controller = timer.GetComponent<AllyCombatController>();
                dominatesInTransit.Add(new DominateTransitionRecord
                {
                    mobileType = (MobileTypes)enemyEntity.MobileEnemy.ID,
                    roundsRemaining = timer.RoundsRemaining,
                    originalTeam = timer.OriginalTeam,
                    upkeep = controller != null ? controller.Upkeep : 1,
                    lastFedTime = timer.LastFedTime,
                    recordedAtClassicMinute = NowClassic(),
                });

                // Bug fix (confirmed live via Player.log: entering a dungeon with 2 dominated humans
                // and stepping back out produced 6). This record-and-respawn cycle assumed the old
                // scene's teardown would destroy the originals - it doesn't, in two separate ways:
                // DFU only DISABLES the exterior world on entering a dungeon/interior (re-enabling it,
                // original dominates included, the moment the player steps back out), and this mod's
                // own respawned copies are created parentless at scene root (CreateEnemy with parent
                // null), so no scene teardown ever touches them either. Both legs die here at once:
                // the recorded creature is destroyed the moment its state is snapshotted, so the
                // respawn on arrival is a true MOVE, never a copy. The destroy is Unity-deferred to
                // end of frame, safely after this same synchronous transition's own respawn runs.
                Destroy(timer.gameObject);
            }

            if (dominatesInTransit.Count > 0)
                VoxsVampirism.Log("RecordActiveDominatesForTransition: recorded {0} dominated creature(s) before transition (originals destroyed - the respawn is a move, not a copy).", dominatesInTransit.Count);
        }

        static void RestoreDominatesAfterTransition()
        {
            if (isLoadingSave)
                return;
            if (dominatesInTransit.Count == 0)
                return;

            Vector3 center = GameManager.Instance.PlayerObject.transform.position;
            int respawnedCount = 0;
            for (int i = 0; i < dominatesInTransit.Count; i++)
            {
                DominateTransitionRecord record = dominatesInTransit[i];

                // Bug fix - see DominateTransitionRecord.recordedAtClassicMinute's own comment. Each
                // elapsed classic-minute during the trip is one magic round this creature's own
                // (nonexistent, mid-trip) DominateTimer never got to consume - charged here instead,
                // all at once, the moment it would next matter. Skipped entirely under Permanent
                // Dominate, matching DominateTimer.Tick's own identical guard - a permanent bond has
                // nothing to expire.
                int adjustedRounds = record.roundsRemaining;
                if (!VoxsVampirism.PermanentDominate)
                {
                    uint elapsedMinutes = NowClassic() - record.recordedAtClassicMinute;
                    adjustedRounds = record.roundsRemaining - (int)elapsedMinutes;
                }

                if (adjustedRounds <= 0)
                {
                    string expiredName = TextManager.Instance.GetLocalizedEnemyName((int)record.mobileType);
                    DaggerfallUI.AddHUDText(string.Format("Your hold over the {0} gave out somewhere along the way - it's gone.", expiredName));
                    VoxsVampirism.Log("RestoreDominatesAfterTransition: '{0}' expired mid-transit (had {1} rounds, {2} elapsed) - not respawned.",
                        record.mobileType, record.roundsRemaining, NowClassic() - record.recordedAtClassicMinute);
                    continue;
                }

                SpawnDominatedCreature(record.mobileType, adjustedRounds, record.originalTeam, record.upkeep, record.lastFedTime);
                respawnedCount++;
            }

            VoxsVampirism.Log("RestoreDominatesAfterTransition: respawned {0}/{1} dominated creature(s) near player at new location (rest expired mid-transit).",
                respawnedCount, dominatesInTransit.Count);
            dominatesInTransit.Clear();
        }

        // Spawns a fresh, fully-wired dominated creature near the player from pure record state -
        // shared by RestoreDominatesAfterTransition (arriving after a transition/fast travel) and
        // RestoreDominatedAllies' own spawn-fresh branch (loading a save whose dominate was one of
        // these mod-spawned traveling copies rather than a DFU-serialized world enemy).
        static void SpawnDominatedCreature(MobileTypes mobileType, int roundsRemaining, MobileTeams originalTeam, int upkeep, uint lastFedTime)
        {
            Vector3 center = GameManager.Instance.PlayerObject.transform.position;

            // Same insideUnitSphere-with-clamped-Y jitter Materialize uses for thralls - see that
            // method's own comment for why the Y component specifically needs clamping to the
            // known-good source position rather than trusted as-is.
            Vector3 spawnPosition = center + Random.insideUnitSphere * 3f;
            spawnPosition.y = center.y;

            GameObject go = GameObjectHelper.CreateEnemy(mobileType.ToString(), mobileType, spawnPosition, MobileGender.Unspecified, null, MobileReactions.Passive);

            // Same reason Materialize strips this from thralls - keeps DFU's own enemy
            // serialization from producing a duplicate flat copy on the next save/load. This mod's
            // own save records carry these creatures instead (see GetDominateSaveRecords' spawn-fresh
            // records).
            SerializableEnemy serializableEnemy = go.GetComponent<SerializableEnemy>();
            if (serializableEnemy != null)
                Destroy(serializableEnemy);

            DaggerfallEntityBehaviour behaviour = go.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity != null)
                enemyEntity.Team = MobileTeams.PlayerAlly;

            EnemyMotor motor = go.GetComponent<EnemyMotor>();
            if (motor != null)
                motor.IsHostile = false;

            DominateTimer timer = go.AddComponent<DominateTimer>();
            timer.RoundsRemaining = roundsRemaining;
            timer.OriginalTeam = originalTeam;
            timer.LastFedTime = lastFedTime;

            AllyCombatController controller = go.AddComponent<AllyCombatController>();
            controller.Upkeep = upkeep;

            EnemySounds enemySounds = go.GetComponent<EnemySounds>();
            if (enemySounds != null)
                enemySounds.enabled = false;

            string name = enemyEntity != null ? TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID) : "creature";
            controller.OnReleaseRequested = reason => timer.ReleaseNow(
                string.Format("The bond breaks - your hold has grown too thin to keep the {0} bound, and it turns on you.", name));
        }

        // Feed-on-Dominated cooldown fix: SneakFeedController's own raycast handling checks
        // ThrallManager.IsThrall first (which a Dominate target never is - it's not roster-tracked),
        // then fell through to its own "hostile==false, in range" passive-animal branch - the free,
        // unlimited instant feed meant for genuinely wild, un-owned creatures, not something bound to
        // the player. That let a Dominated creature be fed on indefinitely with no cooldown at all.
        // Same 24h-per-creature shape as TryFeedOnThrall above, but tracked on DominateTimer.LastFedTime
        // directly (see that field's own comment) rather than a ThrallRuntimeRecord. No break-free
        // roll on a feed here (unlike TryFeedOnThrall) - not part of what was asked, and Dominate
        // already has its own separate revert paths (natural expiry, budget cull). Devour Thrall
        // (DevourThrallEffect.cs) is deliberately NOT gated by this at all - it kills its target
        // outright via a completely different code path (GainBloodNoHumanity called directly), never
        // touching LastFedTime.
        public static bool TryFeedOnDominated(GameObject liveObject)
        {
            DominateTimer timer = liveObject.GetComponent<DominateTimer>();
            if (timer == null)
                return false;

            uint currentTime = NowClassic();
            if (timer.LastFedTime != 0 && currentTime - timer.LastFedTime < DaggerfallDateTime.MinutesPerDay)
            {
                DaggerfallUI.AddHUDText("You have already taken from this one today.");
                return false;
            }

            DaggerfallEntityBehaviour behaviour = liveObject.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity == null)
                return false;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return false;

            // Same MaxHealth/Corpse Blood Divisor scaling Devour Thrall's own Dominate-target branch
            // and an ordinary wild corpse drain both already use - Dominate has no fixed type/tier of
            // its own to key a flat feed amount off, since it can bind literally any living creature.
            int amount = Mathf.Max(1, enemyEntity.MaxHealth / VoxsVampirism.CorpseBloodDivisor);
            vampirism.GainBloodNoHumanity(amount);
            timer.LastFedTime = currentTime;

            string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
            DaggerfallUI.AddHUDText(string.Format("You feed from the {0} bound to your will. (Blood: {1}/10.)", name, vampirism.GetBloodPoints()));

            return true;
        }

        #endregion

        // Two kinds of stray ally objects can exist the moment a load completes, and both must go
        // before MaterializeAll spawns the court fresh from the restored roster:
        //
        //  1. DFU-restored flat copies: saves made before Materialize started stripping
        //     SerializableEnemy (see there) contain every ally as an ordinary serialized enemy,
        //     which DFU reconstructs on load - right type and team, none of this mod's components.
        //     These are the reported per-load duplicate vampires, and old saves keep producing them
        //     until swept. Matched conservatively: PlayerAlly team, a mobile type actually present
        //     in the restored roster, and none of the components any LEGITIMATE allied object
        //     carries - this mod's own AllyCombatController or DominateTimer, or Necromancy's
        //     ReanimatedLifetime (looked up by name, it lives in a different mod assembly; its own
        //     ReconcileAfterLoad has already reattached it by now, since Necromancy loads and
        //     subscribes to OnLoad before this mod does) - so another mod's allies are never touched.
        //
        //  2. Orphaned live allies from the pre-load session: loading doesn't always tear the whole
        //     scene down (an exterior-to-exterior load keeps the streamed world), so allies spawned
        //     before the load can survive INTO the loaded game while the restored roster's LiveObject
        //     references are all null - MaterializeAll would then spawn a second, working copy right
        //     next to the surviving one. At this point in a load every legitimate roster ally is by
        //     definition not-yet-spawned, so any AllyCombatController WITHOUT a DominateTimer is an
        //     orphan - the timer exclusion matters because RestoreDominatedAllies has already run by
        //     now and its reattached dominated creatures legitimately carry both components (an
        //     earlier version of this sweep treated any controller as an orphan and destroyed the
        //     exact creature the restore had just fixed, every single load).
        static void PurgeStrayAlliesAfterLoad()
        {
            DaggerfallEntityBehaviour[] allBehaviours = UnityEngine.Object.FindObjectsOfType<DaggerfallEntityBehaviour>();
            int purged = 0;
            for (int i = 0; i < allBehaviours.Length; i++)
            {
                DaggerfallEntityBehaviour behaviour = allBehaviours[i];
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity == null)
                    continue;

                // A dominated creature RestoreDominatedAllies just reattached carries BOTH an
                // AllyCombatController AND a DominateTimer - the orphan test must exclude it, or the
                // sweep destroys the exact creature the restore just fixed (this happened: "any
                // AllyCombatController is an orphan" was written before RestoreDominatedAllies
                // existed and ran first, and the freshly re-dominated giant vanished on every load).
                // A genuine orphaned pre-load thrall spawn only ever has the controller, never a
                // DominateTimer, so timer-bearers are simply never orphans.
                bool hasDominateTimer = behaviour.GetComponent<DominateTimer>() != null;
                bool isOrphanedAlly = behaviour.GetComponent<AllyCombatController>() != null && !hasDominateTimer;
                bool isRestoredFlatCopy = !isOrphanedAlly
                    && enemyEntity.Team == MobileTeams.PlayerAlly
                    && !hasDominateTimer
                    && behaviour.GetComponent("ReanimatedLifetime") == null
                    && RosterContainsMobileType(enemyEntity.MobileEnemy.ID);

                // A PlayerAlly-team enemy that ISN'T a thrall-type flat copy, carries none of this
                // mod's components (RestoreDominatedAllies has already reattached everything it has
                // records for by this point), none of Necromancy's (ReanimatedLifetime for raised
                // allies, SubjugationTimer for its own bound-existing-undead - by-name lookups,
                // different mod assembly), and isn't a quest resource, is a stale Dominate leftover:
                // a creature dominated in a save made before Dominate persistence existed, restored
                // by DFU as a passive allied husk that neither follows nor ever reverts (the exact
                // reported "giants I dominated on previous saves are now passive" state). It's a
                // real world enemy, not a duplicate - so it's REVERTED to its own natural team and
                // hostility (the same restoration DominateTimer.Revert performs on expiry, using
                // MobileEnemy.Team since no snapshotted original survives) rather than destroyed.
                bool isStaleDominateLeftover = !isOrphanedAlly && !isRestoredFlatCopy
                    && enemyEntity.Team == MobileTeams.PlayerAlly
                    && !hasDominateTimer
                    && behaviour.GetComponent("ReanimatedLifetime") == null
                    && behaviour.GetComponent("SubjugationTimer") == null
                    && behaviour.GetComponent<DaggerfallWorkshop.Game.Questing.QuestResourceBehaviour>() == null;

                if (isOrphanedAlly || isRestoredFlatCopy)
                {
                    VoxsVampirism.Log("PurgeStrayAlliesAfterLoad: destroying stray ally object '{0}' ({1}).",
                        behaviour.name, isOrphanedAlly ? "orphaned pre-load ally" : "DFU-restored flat copy");
                    Destroy(behaviour.gameObject);
                    purged++;
                }
                else if (isStaleDominateLeftover)
                {
                    enemyEntity.Team = enemyEntity.MobileEnemy.Team;
                    EnemyMotor strayMotor = behaviour.GetComponent<EnemyMotor>();
                    if (strayMotor != null)
                        strayMotor.IsHostile = true;
                    VoxsVampirism.Log("PurgeStrayAlliesAfterLoad: reverted stale Dominate leftover '{0}' to its natural team ({1}).",
                        behaviour.name, enemyEntity.MobileEnemy.Team);
                    purged++;
                }
            }

            if (purged > 0)
                VoxsVampirism.Log("PurgeStrayAlliesAfterLoad: handled {0} stray ally object(s) before re-materializing the court.", purged);
        }

        // Matches against the live-object type each record would actually materialize as (a
        // transformed record spawns MobileTypes.Vampire regardless of its original MobileType - the
        // same selection Materialize itself makes).
        static bool RosterContainsMobileType(int mobileEnemyId)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                ThrallRuntimeRecord record = roster[i];
                MobileTypes liveType = record.IsVampire ? MobileTypes.Vampire : record.MobileType;
                if ((int)liveType == mobileEnemyId)
                    return true;
            }
            return false;
        }

        // Never let an exception escape this into EntityEffectBroker's round loop - an escaping
        // subscriber exception aborts that loop before the broker's own lastGameMinute marker
        // advances, locking the whole game into one magic round per frame forever (the exact
        // catastrophe NightWalkerSpawner.GuardedReconcile's own header documents in full - its
        // Reconcile was the live case).
        static bool hasLoggedCheckTransformationsFailure;

        static void CheckTransformations()
        {
            try
            {
                CheckTransformationsCore();
            }
            catch (System.Exception ex)
            {
                if (!hasLoggedCheckTransformationsFailure)
                {
                    hasLoggedCheckTransformationsFailure = true;
                    VoxsVampirism.Log("ThrallManager: CheckTransformations threw ({0}: {1}) - swallowed to protect the broker's round loop, logged once per session.",
                        ex.GetType().Name, ex.Message);
                }
            }
        }

        static void CheckTransformationsCore()
        {
            uint now = NowClassic();
            for (int i = 0; i < roster.Count; i++)
            {
                ThrallRuntimeRecord record = roster[i];
                if (!record.IsTransforming || now < record.TransformCompleteTime)
                    continue;

                CompleteTransformation(record);
            }
        }

        static void CompleteTransformation(ThrallRuntimeRecord record)
        {
            GameObject oldObject = record.LiveObject;
            Vector3 position = oldObject != null ? oldObject.transform.position : GameManager.Instance.PlayerObject.transform.position;
            if (oldObject != null)
                Destroy(oldObject);

            record.IsVampire = true;
            record.IsTransforming = false;
            record.TransformCompleteTime = 0;
            Materialize(record, position);

            DaggerfallUI.AddHUDText(string.Format("Your thrall rises anew - no longer mortal, and now truly your own. Court: {0}.",
                AllyCombatController.DescribeBudget()));
            VoxsVampirism.Log("Thrall transformation complete: now a Vampire ally.");
        }

        // A thrall that dies (in a fight, or via MurderThrall's own SetHealth(0)) is cleaned out of
        // the roster here, uniformly regardless of cause - this is the ONLY place roster entries are
        // removed for "died" rather than "released/broke free" (see ReleaseThrall for that separate
        // path). Also implicitly cancels an in-progress transformation with no special handling
        // needed, since the record (and its IsTransforming flag) simply stops existing.
        static void HandleEnemyDeath(object sender, System.EventArgs e)
        {
            EnemyDeath death = sender as EnemyDeath;
            if (death == null)
                return;

            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].LiveObject == death.gameObject)
                {
                    VoxsVampirism.Log("Thrall died: type={0}, isVampire={1}, wasTransforming={2}.",
                        roster[i].MobileType, roster[i].IsVampire, roster[i].IsTransforming);
                    roster.RemoveAt(i);
                    return;
                }
            }
        }

        #endregion

        #region Materialization on scene transitions / fast travel (see file header for the approach)

        static bool justFastTravelled;

        static void OnTransition(PlayerEnterExit.TransitionEventArgs args)
        {
            // See isLoadingSave's own header above - a load fires this same event as a side effect of
            // restoring the player's position, and OnSaveLoaded already handles materializing once the
            // load actually completes.
            if (isLoadingSave)
                return;

            MaterializeAll();
            RestoreDominatesAfterTransition();
        }

        static void OnPreTransition(PlayerEnterExit.TransitionEventArgs args)
        {
            RecordActiveDominatesForTransition();
        }

        static void OnPreFastTravelForDominates(DaggerfallTravelPopUp popup)
        {
            RecordActiveDominatesForTransition();
        }

        static void OnPostFastTravel()
        {
            // Same one-frame position lag necromancy's own OnPostFastTravel handling documents -
            // deferred to the next OnEnterLocationRect instead of materializing here directly.
            justFastTravelled = true;
        }

        static void OnEnterLocationRect(DFLocation location)
        {
            if (!justFastTravelled)
                return;

            justFastTravelled = false;

            // See isLoadingSave's own header above - defensive, same reasoning as OnTransition's own
            // guard (a load's own restoration could plausibly trigger this path too).
            if (isLoadingSave)
                return;

            MaterializeAll();
            RestoreDominatesAfterTransition();
        }

        // Destroys any live objects still tracked (defensive - DFU's own scene teardown may already
        // have discarded them, but destroying an already-gone reference is a safe no-op) and spawns a
        // fresh live object for every roster entry, clustered near the player exactly like
        // necromancy's own restore step. Dormant (transforming) thralls materialize with their ally
        // AI still disabled, matching their state before the transition.
        public static void MaterializeAll()
        {
            if (roster.Count == 0)
                return;

            Vector3 center = GameManager.Instance.PlayerObject.transform.position;
            for (int i = 0; i < roster.Count; i++)
            {
                ThrallRuntimeRecord record = roster[i];
                if (record.LiveObject != null)
                    Destroy(record.LiveObject);

                Materialize(record, center);
            }

            VoxsVampirism.Log("Thrall court materialized ({0}) near player after scene transition/fast travel.", roster.Count);
        }

        #endregion

        #region Persistence (VoxsVampirismEffect.CustomSaveData_v1.thralls calls into this)

        [fsObject("v1")]
        public struct ThrallRecord_v1
        {
            public int mobileTypeId;
            public bool isVampire;
            public uint creationTime;
            public uint lastFedTime;
            public bool isTransforming;
            public uint transformCompleteTime;
            public int gender;
        }

        public static ThrallRecord_v1[] GetSaveRecords()
        {
            ThrallRecord_v1[] records = new ThrallRecord_v1[roster.Count];
            for (int i = 0; i < roster.Count; i++)
            {
                ThrallRuntimeRecord record = roster[i];
                records[i] = new ThrallRecord_v1
                {
                    mobileTypeId = (int)record.MobileType,
                    isVampire = record.IsVampire,
                    creationTime = record.CreationTime,
                    lastFedTime = record.LastFedTime,
                    isTransforming = record.IsTransforming,
                    transformCompleteTime = record.TransformCompleteTime,
                    gender = (int)record.Gender,
                };
            }

            return records;
        }

        // Roster entries are restored without a live GameObject (MaterializeAll runs separately, on
        // the next scene transition/fast-travel arrival after load - the same event-driven path used
        // for every other transition, so a fresh load doesn't need its own special-cased spawn call
        // here). Order is preserved exactly as saved, keeping oldest-first intact.
        public static void RestoreRecords(ThrallRecord_v1[] records)
        {
            roster.Clear();
            if (records == null)
                return;

            for (int i = 0; i < records.Length; i++)
            {
                ThrallRecord_v1 saved = records[i];

                // A save made before gender was tracked deserializes this as 0 (MobileGender.
                // Unspecified, FullSerializer's default for a field that didn't exist yet) - rolled
                // once here rather than left Unspecified, which would otherwise keep re-triggering
                // CreateEnemy's own per-materialize random gender forever (the exact bug this fix is
                // for), just delayed until the first post-load materialize instead of avoided.
                MobileGender gender = (MobileGender)saved.gender;
                if (gender == MobileGender.Unspecified)
                    gender = Random.value < 0.5f ? MobileGender.Male : MobileGender.Female;

                roster.Add(new ThrallRuntimeRecord
                {
                    MobileType = (MobileTypes)saved.mobileTypeId,
                    IsVampire = saved.isVampire,
                    CreationTime = saved.creationTime,
                    LastFedTime = saved.lastFedTime,
                    IsTransforming = saved.isTransforming,
                    TransformCompleteTime = saved.transformCompleteTime,
                    Gender = gender,
                    LiveObject = null,
                });
            }

            VoxsVampirism.Log("Thrall roster restored from save: {0} entries.", roster.Count);
        }

        #endregion
    }
}
