// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Night Walkers: a small, configurable number of ordinary townspeople stay out on town streets at
// night instead of every single one going indoors, so a vampire actually has someone to hunt after
// dark. DFU's own PopulationManager.UpdateMobiles() (read directly - it's a private method) forcibly
// deactivates EVERY pooled MobilePersonNPC the instant DaggerfallDateTime.IsDay goes false, with the
// recycle condition baked in unconditionally over its own internal pool - there is no flag, no
// override, no per-NPC exception point to hook into. The only viable path is to build new
// MobilePersonNPC instances entirely outside that pool, from the same public API PopulationManager
// itself uses internally (GameObjectHelper.InstantiatePrefab + DaggerfallUnity.
// Option_MobileNPCPrefab + CityNavigation's own coordinate-conversion methods + MobilePersonNPC.
// RandomiseNPC()/MobilePersonMotor.InitMotor()) - since these are never added to that pool, the
// night-recycle check never touches them.
//
// No roster/persistence needed - fully ephemeral and regenerated on demand, matching
// PopulationManager's own civilians (confirmed: neither MobilePersonNPC nor MobilePersonMotor
// implement ISerializableGameObject, so nothing here risks duplicating on save/load).
// SneakFeedController already recognizes any MobilePersonNPC on sight, so feeding on a night walker
// needs no changes anywhere else - they're indistinguishable ordinary townspeople to every other
// system in this mod.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;

namespace VoxsVampirismMod
{
    public class NightWalkerSpawner : MonoBehaviour
    {
        // The only state kept - no persistence, matching PopulationManager's own civilians (a fresh
        // game launch or save reload simply has none until the next reconcile tick finds it's night
        // and tops them up).
        static readonly List<GameObject> activeNightWalkers = new List<GameObject>();

        // WorldTime.OnDusk/OnDawn fire on the exact hour-boundary edge (immediate, correct for the
        // common case) but can be skipped entirely by a large time jump (fast travel, Rest) per
        // WorldTime's own doc comment - EntityEffectBroker.OnNewMagicRound (this mod's own established
        // "roughly once a game-minute" periodic-check event, already used by ThrallManager/
        // SneakFeedController) is the reconciling fallback that catches everything the discrete events
        // might miss, including simply arriving at an already-dark town.
        void Awake()
        {
            WorldTime.OnDusk += HandleDusk;
            WorldTime.OnDawn += HandleDawn;
            EntityEffectBroker.OnNewMagicRound += HandleNewMagicRound;

            // Same event set ThrallManager.cs's own OnEnable subscribes to for its own cross-scene
            // GameObject cleanup - the old location's CityNavigation becomes invalid once
            // StreamingWorld tears down that scene chunk, so every night walker is destroyed
            // unconditionally on any transition rather than risking a stale reference.
            PlayerEnterExit.OnTransitionExterior += HandleTransition;
            PlayerEnterExit.OnTransitionInterior += HandleTransition;
            PlayerEnterExit.OnTransitionDungeonExterior += HandleTransition;
            PlayerEnterExit.OnTransitionDungeonInterior += HandleTransition;

            DaggerfallTravelPopUp.OnPostFastTravel += HandlePostFastTravel;
        }

        void OnDestroy()
        {
            WorldTime.OnDusk -= HandleDusk;
            WorldTime.OnDawn -= HandleDawn;
            EntityEffectBroker.OnNewMagicRound -= HandleNewMagicRound;

            PlayerEnterExit.OnTransitionExterior -= HandleTransition;
            PlayerEnterExit.OnTransitionInterior -= HandleTransition;
            PlayerEnterExit.OnTransitionDungeonExterior -= HandleTransition;
            PlayerEnterExit.OnTransitionDungeonInterior -= HandleTransition;

            DaggerfallTravelPopUp.OnPostFastTravel -= HandlePostFastTravel;
        }

        // ROOT CAUSE of the racing-magic-rounds anomaly lived here, so this guard is load-bearing -
        // do not remove it. EntityEffectBroker.Update() only advances its private lastGameMinute
        // marker AFTER its round-firing loop completes; an exception escaping ANY OnNewMagicRound
        // subscriber aborts that loop first, so the marker never advances and the broker re-fires a
        // round EVERY SINGLE FRAME forever (~60/sec against a normal 0.2/sec - proven live via the
        // TimeDiag instrumentation in VoxsVampirism.cs: lastGameMinute frozen dead while the debt
        // grew, one round per frame, even while paused). Reconcile()'s own SpawnOne was that escaping
        // thrower for days (CityNavigation.WorldToNavGridPosition NRE on ungridded locations - see
        // Reconcile's own grid check now), which silently turned every round-counted duration in the
        // whole game - Blood Surge's 10 rounds, Mist Form's 4-round upkeep, Dominate timers, other
        // mods' spell durations alike - into per-frame burn, and killed the player through Mist
        // Form's desperate-casting drain more than once before it was traced. The catch below is the
        // guarantee the marker can never be starved by THIS subscriber again, whatever future edits
        // to Reconcile do; the grid check is merely the current known thrower fixed properly.
        static bool hasLoggedReconcileFailure;

        static void HandleDusk() { GuardedReconcile(); }
        static void HandleDawn() { DespawnAll(); }
        static void HandleNewMagicRound() { GuardedReconcile(); }
        static void HandleTransition(PlayerEnterExit.TransitionEventArgs args) { DespawnAll(); }
        static void HandlePostFastTravel() { DespawnAll(); }

        static void GuardedReconcile()
        {
            try
            {
                Reconcile();
            }
            catch (System.Exception ex)
            {
                if (!hasLoggedReconcileFailure)
                {
                    hasLoggedReconcileFailure = true;
                    VoxsVampirism.Log("NightWalkerSpawner: Reconcile threw ({0}: {1}) - swallowed so EntityEffectBroker's round loop is never aborted (see comment above), logged once per session.",
                        ex.GetType().Name, ex.Message);
                }
            }
        }

        // Prunes dead/inactive entries, then tops up to the target count - but only while it's
        // actually night and the player is standing on an eligible town street (SneakFeedController's
        // own existing "streets" classification, reused directly rather than reinventing the same
        // IsPlayerInside/IsPlayerInTown logic). Naturally does nothing once dawn breaks or the player
        // steps indoors/leaves town, without needing a separate "should despawn now" check - the next
        // tick simply stops topping up, and the transition/dawn handlers above cover an outright clear.
        static void Reconcile()
        {
            PruneDeadWalkers();

            int targetCount = VoxsVampirism.NightWalkerCount;
            if (targetCount <= 0)
                return;

            if (!DaggerfallUnity.Instance.WorldTime.Now.IsNight)
                return;

            if (SneakFeedController.GetSceneLocationTag() != SneakFeedController.SceneLocationStreets)
                return;

            DaggerfallLocation dfLocation = GameManager.Instance.StreamingWorld.CurrentPlayerLocationObject;
            if (dfLocation == null)
                return;

            CityNavigation cityNavigation = dfLocation.GetComponent<CityNavigation>();
            if (cityNavigation == null)
                return;

            // The proven thrower behind the racing-rounds anomaly (see GuardedReconcile's own header):
            // some locations carry a CityNavigation component whose nav grid was never actually built
            // (FormatNavigation never ran - CityWidth/CityHeight still 0), and every navigation call
            // on it NREs internally. Skip those locations outright - there's no grid to walk anyway.
            if (cityNavigation.CityWidth <= 0 || cityNavigation.CityHeight <= 0)
                return;

            int spawnedThisTick = 0;
            while (activeNightWalkers.Count < targetCount)
            {
                GameObject walker = SpawnOne(dfLocation, cityNavigation);
                if (walker == null)
                    break;

                activeNightWalkers.Add(walker);
                spawnedThisTick++;
            }

            if (spawnedThisTick > 0)
            {
                VoxsVampirism.Log("NightWalkerSpawner: spawned {0} night walker(s), now {1}/{2}.",
                    spawnedThisTick, activeNightWalkers.Count, targetCount);
            }
        }

        // A walker killed/corpse-swapped via the existing feed pipeline (KillNpcAndLeaveCorpse,
        // SpawnFightsBackEnemy) goes inactive (SetActive(false)) rather than destroyed - treated here
        // as gone so a replacement can spawn. A walker merely fed on non-lethally (Charm/Stalk
        // success) stays active and is left alone, still wandering.
        static void PruneDeadWalkers()
        {
            for (int i = activeNightWalkers.Count - 1; i >= 0; i--)
            {
                GameObject walker = activeNightWalkers[i];
                if (walker == null || !walker.activeSelf)
                    activeNightWalkers.RemoveAt(i);
            }
        }

        // Mirrors PopulationManager.SpawnAvailableMobile()/CreateNewPoolItem()'s own real working
        // sequence exactly - same coordinate-conversion chain, same RandomiseNPC-then-InitMotor
        // ordering. DaggerfallEntityBehaviour.Entity auto-populates correctly with no extra work here:
        // the prefab's own EntityType is already baked to CivilianNPC, and DaggerfallEntityBehaviour.
        // Awake() unconditionally constructs a real CivilianEntity for that type before any Update()
        // can run, so MobilePersonMotor.Update()'s own entityBehaviour.Entity.IsParalyzed check never
        // sees a null Entity.
        static GameObject SpawnOne(DaggerfallLocation dfLocation, CityNavigation cityNavigation)
        {
            PlayerGPS playerGPS = GameManager.Instance.PlayerGPS;
            DFPosition playerWorldPos = new DFPosition(playerGPS.WorldX, playerGPS.WorldZ);
            DFPosition originGridPos = cityNavigation.WorldToNavGridPosition(playerWorldPos);

            DFPosition spawnGridPos;
            if (!cityNavigation.GetRandomSpawnPosition(originGridPos, out spawnGridPos))
                return null;

            DFPosition spawnWorldPos = cityNavigation.NavGridToWorldPosition(spawnGridPos);
            Vector3 scenePosition = cityNavigation.WorldToScenePosition(spawnWorldPos);

            GameObject go = GameObjectHelper.InstantiatePrefab(
                DaggerfallUnity.Instance.Option_MobileNPCPrefab.gameObject, "Night Walker", dfLocation.transform, scenePosition);

            // WorldToScenePosition's own terrain-height resampling gets the ground roughly right but
            // not the billboard's own pivot offset - PopulationManager.SpawnAvailableMobile() always
            // follows up with exactly this call (same size convention, only the Y component is used)
            // to raycast down and snap the billboard's visual base to actual ground. Skipping this
            // was the second half of what read as "signatures under the ground" - not just the
            // NullReferenceException leak above, but genuinely unrefined placement for whichever
            // walkers did survive it.
            GameObjectHelper.AlignBillboardToGround(go, new Vector2(0, 2f));

            MobilePersonNPC npc = go.GetComponent<MobilePersonNPC>();
            MobilePersonMotor motor = go.GetComponent<MobilePersonMotor>();
            motor.cityNavigation = cityNavigation;

            // Required before RandomiseNPC() - MobilePersonNPC.Asset (the billboard SetPerson()
            // writes into) is a plain get/set property with no auto-resolution of its own; it's never
            // populated by Awake(), only by whoever constructs the NPC. PopulationManager.
            // CreateNewPoolItem() sets this explicitly (poolItem.npc.Asset = motor.MobileAsset) and
            // this line was missing here - RandomiseNPC() -> SetPerson() -> Asset.SetPerson(...)
            // threw a NullReferenceException on every single spawn attempt as a result, which aborted
            // setup after the GameObject already existed in the scene (never reaching InitMotor() or
            // SetActive(true), and never reaching the caller's activeNightWalkers.Add() either) -
            // leaking one broken, unpositioned, untracked NPC per magic round, indefinitely, all night.
            npc.Asset = motor.MobileAsset;

            npc.RandomiseNPC(ResolveRegionRace());
            motor.InitMotor();

            go.SetActive(true);

            return go;
        }

        // Same FactionFile.FactionRaces -> Races mapping PopulationManager.GetEntityRace() uses
        // internally (that method is private, so reimplemented here) - keeps night walkers matching
        // the current region's own population race instead of defaulting to one race everywhere.
        static Races ResolveRegionRace()
        {
            switch (GameManager.Instance.PlayerGPS.ClimateSettings.People)
            {
                case FactionFile.FactionRaces.Redguard: return Races.Redguard;
                case FactionFile.FactionRaces.Nord: return Races.Nord;
                default: return Races.Breton;
            }
        }

        // Real Destroy() on every tracked instance, never SetActive(false) - matches ThrallManager.
        // MaterializeAll()'s own "always resummon, never toggle" convention, and avoids
        // MobilePersonMotor.OnDestroy()'s ClearMotor() throwing on a stale cityNavigation reference by
        // destroying proactively before the location itself unloads.
        static void DespawnAll()
        {
            if (activeNightWalkers.Count == 0)
                return;

            int count = activeNightWalkers.Count;
            for (int i = 0; i < activeNightWalkers.Count; i++)
            {
                if (activeNightWalkers[i] != null)
                    Destroy(activeNightWalkers[i]);
            }
            activeNightWalkers.Clear();

            VoxsVampirism.Log("NightWalkerSpawner: despawned {0} night walker(s).", count);
        }
    }
}
