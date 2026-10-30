// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Populate Residences At Night: vanilla DFU hides every StaticNPC "people flat" inside a House1-4
// (and HouseForSale) interior outside that building type's own open-hours window (PlayerActivate.
// IsBuildingOpen - a flat per-building-type hour table; House1 is permanently "closed" per the table
// itself, House2-6 open 0600-1800; House5/House6 are never gated at all, so they already show NPCs
// regardless of time). DaggerfallInterior.AddPeople() still always instantiates the StaticNPC
// GameObject either way (read directly - confirmed this session), it's just SetActive(false) before
// Start() ever runs - so nothing here needs Harmony: reacting to the already-existing hidden objects
// right after PlayerEnterExit.OnTransitionInterior fires is enough (AddPeople() has already run and
// BuildingType/FactionID/Interior are already valid by the time that event fires).
//
// Scope is deliberately residences only, never shops/guild halls/temples/taverns - those have their
// own click-time-checked "is this open" logic elsewhere in PlayerActivate that a force-reactivated
// shopkeeper could collide with in confusing ways; residence occupants have no such special-cased
// interaction. Player-owned houses are explicitly excluded too (DaggerfallBankManager.IsHouseOwned) -
// a vampire's own house shouldn't spontaneously fill with feed targets.
//
// One-shot per transition, no periodic reconciliation needed (unlike NightWalkerSpawner) - vanilla
// itself never re-evaluates a StaticNPC's active state after AddPeople() runs once at interior-
// creation time, and nothing needs to be undone on exit either, since DaggerfallInterior (and every
// StaticNPC under it) is destroyed and freshly rebuilt from scratch on every exit/re-entry regardless.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Banking;
using DaggerfallWorkshop.Game.Questing;

namespace VoxsVampirismMod
{
    public class ResidencePopulator : MonoBehaviour
    {
        void Awake()
        {
            PlayerEnterExit.OnTransitionInterior += HandleTransitionInterior;
        }

        void OnDestroy()
        {
            PlayerEnterExit.OnTransitionInterior -= HandleTransitionInterior;
        }

        static void HandleTransitionInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            if (!VoxsVampirism.PopulateResidencesAtNight)
                return;

            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            DFLocation.BuildingTypes buildingType = playerEnterExit.BuildingType;
            bool isResidence = (buildingType >= DFLocation.BuildingTypes.House1 && buildingType <= DFLocation.BuildingTypes.House6)
                || buildingType == DFLocation.BuildingTypes.HouseForSale;

            // TEMPORARY diagnostic pass (Player.log doesn't distinguish "not a residence" from "never
            // fired" otherwise) - logs every interior transition, not just residences, so a building
            // the player expected to count as a residence but which isn't classified as one shows up
            // clearly instead of just silently producing no ResidencePopulator line at all.
            if (!isResidence)
            {
                VoxsVampirism.Log("ResidencePopulator: entered '{0}' - not classified as a residence, skipping.", buildingType);
                return;
            }

            bool isOwned = DaggerfallBankManager.IsHouseOwned(playerEnterExit.BuildingDiscoveryData.buildingKey);
            if (isOwned)
            {
                VoxsVampirism.Log("ResidencePopulator: entered '{0}' (owned) - skipping, not repopulating your own house.", buildingType);
                return;
            }

            if (playerEnterExit.Interior == null)
            {
                VoxsVampirism.Log("ResidencePopulator: entered '{0}' but PlayerEnterExit.Interior was null - nothing to scan.", buildingType);
                return;
            }

            // includeInactive:true is essential here - unlike this mod's other GetComponentsInChildren
            // <StaticNPC>() call sites (SneakFeedController's frenzy-target scan, MassacreCurrentInterior),
            // which intentionally only want the active ones, this is the one call site that wants the
            // hidden ones.
            StaticNPC[] npcs = playerEnterExit.Interior.GetComponentsInChildren<StaticNPC>(true);
            int alreadyActive = 0;
            int reactivated = 0;

            // World-space position alone doesn't say whether an NPC is actually near the player -
            // DFU's streaming world can put both at a large shared offset. Logging the player's own
            // position alongside each NPC's lets the two be compared directly (distance, height delta)
            // instead of eyeballing raw coordinates.
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            VoxsVampirism.Log("ResidencePopulator: player position at entry = {0}.", playerPosition);
            for (int i = 0; i < npcs.Length; i++)
            {
                StaticNPC npc = npcs[i];
                if (npc == null)
                    continue;

                // activeSelf alone isn't the full story - a GameObject can have its own flag true while
                // an ancestor Transform is inactive, which would make it invisible/unreachable despite
                // "activeSelf" reporting true. Logging activeInHierarchy alongside activeSelf, plus the
                // immediate parent's name and active state, and world position, so a mismatch here is
                // diagnosable rather than assumed.
                bool activeSelf = npc.gameObject.activeSelf;
                bool activeInHierarchy = npc.gameObject.activeInHierarchy;
                Transform parent = npc.transform.parent;
                float distanceFromPlayer = Vector3.Distance(playerPosition, npc.transform.position);
                float heightDelta = npc.transform.position.y - playerPosition.y;
                VoxsVampirism.Log("ResidencePopulator: NPC '{0}' - activeSelf={1}, activeInHierarchy={2}, parent='{3}' (parent.activeSelf={4}), pos={5}, distanceFromPlayer={6:F1}, heightDelta={7:F1}.",
                    npc.name, activeSelf, activeInHierarchy, parent != null ? parent.name : "(none)",
                    parent != null ? parent.gameObject.activeSelf : (bool?)null, npc.transform.position, distanceFromPlayer, heightDelta);

                // Active-in-hierarchy and reasonably positioned still isn't proof it actually renders or
                // is clickable - checking the billboard's own reported size, whether its MeshRenderer is
                // present/enabled with a real mesh, and whether the interaction Collider that
                // DaggerfallInterior.AddPeople() attaches is present/enabled, to rule those out too.
                Billboard billboard = npc.GetComponent<Billboard>();
                MeshRenderer meshRenderer = npc.GetComponent<MeshRenderer>();
                MeshFilter meshFilter = npc.GetComponent<MeshFilter>();
                Collider collider = npc.GetComponent<Collider>();
                VoxsVampirism.Log("ResidencePopulator: NPC '{0}' components - Billboard={1} (Size={2}), MeshRenderer={3} (enabled={4}), MeshFilter.sharedMesh={5}, Collider={6} (enabled={7}, bounds={8}).",
                    npc.name,
                    billboard != null, billboard != null ? billboard.Summary.Size.ToString() : "(no billboard)",
                    meshRenderer != null, meshRenderer != null ? meshRenderer.enabled : (bool?)null,
                    meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.name : "(none)",
                    collider != null, collider != null ? collider.enabled : (bool?)null,
                    collider != null ? collider.bounds.ToString() : "(no collider)");

                if (activeSelf)
                {
                    alreadyActive++;

                    // If the NPC's own flag is on but it's still not active-in-hierarchy, something
                    // above it in the chain is off - walk up (bounded to this interior) and turn on
                    // whatever's inactive, rather than assuming activeSelf alone means "done."
                    if (!activeInHierarchy)
                    {
                        Transform t = npc.transform.parent;
                        while (t != null && t != playerEnterExit.Interior.transform.parent)
                        {
                            if (!t.gameObject.activeSelf)
                            {
                                VoxsVampirism.Log("ResidencePopulator: reactivating inactive ancestor '{0}' of NPC '{1}'.", t.name, npc.name);
                                t.gameObject.SetActive(true);
                            }
                            t = t.parent;
                        }
                    }
                    continue;
                }

                npc.gameObject.SetActive(true);

                // Reactivation alone triggers Unity's deferred StaticNPC.Start(), which correctly
                // handles the generic-NPC/Questor case on its own - but explicitly calling this too is
                // still needed to fully match what the vanilla "building open" branch does for named/
                // individual NPCs specifically: it re-checks whether this individual is already quest-
                // placed elsewhere and correctly re-hides it if so (guarding against ever showing a
                // duplicate of a quest-critical unique NPC), and attaches the broader
                // ActiveFactionPersons-based quest-resource binding Start() alone would miss. A no-op
                // for ordinary generic-faction house occupants.
                QuestMachine.Instance.SetupIndividualStaticNPC(npc.gameObject, npc.Data.factionID);
                reactivated++;
            }

            // Always logged (not just when reactivated > 0) - a residence with zero StaticNPCs in its
            // own layout data at all (npcs.Length == 0) is a real, common case in vanilla RMB block
            // art, not a bug in this reactivation logic, and this line is what makes that
            // distinguishable from a genuine failure to reactivate from Player.log alone.
            VoxsVampirism.Log("ResidencePopulator: entered '{0}' (unowned) - {1} StaticNPC(s) total, {2} already active, {3} reactivated.",
                buildingType, npcs.Length, alreadyActive, reactivated);
        }
    }
}
