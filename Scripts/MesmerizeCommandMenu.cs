// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mesmerize Command Menu spec: a successful Mesmerize opens a command menu instead of just ending
// hostility. Two entry points with two DIFFERENT option sets, since a townsperson and a pacified
// enemy can never be the same target.
//
// Mesmerize Rework spec: Mesmerize itself is now a self-cast spell (see MesmerizeEffect.cs) that
// primes the caster's gaze and AoE-pacifies nearby human enemies, rather than a direct targeted hit
// on either kind of target - so BOTH entry points below are now reached via activation (the player's
// normal "talk"/"use" click), redirected by MesmerizeGazeWatcher.cs, the one place in this mod that
// intercepts vanilla's own activate dispatch (no Harmony - see that file's own header for the
// LateUpdate-based technique this required).
//
//   - ShowForTownsperson(GameObject) - reached from MesmerizeGazeWatcher when the player activates a
//     townsperson (StaticNPC/MobilePersonNPC/WoD-gated person billboard) while their gaze is primed
//     (VoxsVampirismEffect.IsGazePrimed, set by the self-cast). Mesmerize itself always succeeds
//     against a townsperson (no roll at all - not a modified formula). Menu: Loot, Interrogate - no
//     Feed (removed per explicit request; Charm/Stalk already cover feeding on a townsperson without
//     Mesmerize at all).
//   - ShowForEnemy(DaggerfallEntityBehaviour) - reached from MesmerizeGazeWatcher when the player
//     activates an enemy carrying MesmerizedEnemyMarker (stamped by MesmerizeEffect's own AoE pacify
//     on a successful per-target humanity-scaled roll) - independent of gaze-priming entirely,
//     pacification itself is the gate on this side. Menu: Feed, Loot, Interrogate.
//
// Post-Feed Offers update: Create Thrall moved OUT of this menu entirely - it's no longer an at-will
// action reachable from here at all, townsperson or enemy. The only door left to thrall (or vampire)
// creation is now the rare, unprompted offer NarrativeFeedSceneEngine shows after a successful
// non-violent scene feed (see that file's own Post-Feed Offer region) - the rarity IS the feature; an
// always-available menu option made it a shopping decision instead of an event.
//
// No custom enum types anywhere in this file, per this mod's established convention - two separate
// entry-point methods instead of a type-tag parameter.

using System;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;

namespace VoxsVampirismMod
{
    public static class MesmerizeCommandMenu
    {
        // Mesmerize's own cost (VoxsVampirismMod.Effects.MesmerizeEffect.bloodCostPerCast) - charged
        // for the townsperson entry path via CommitTownspersonMesmerize below, since that's a
        // keybind-triggered menu action, not a real spell cast (SpellCastCostRefunder.cs's
        // OnCastReadySpell hook never fires for it, and there's no Magicka to charge/refund either,
        // matching every other free keybind action in SneakFeedController). The enemy entry path
        // already paid this via MesmerizeEffect.Evaluate() itself before the roll - never charged
        // twice.
        const int mesmerizeBloodCost = 1;

        // Opening this menu (and backing out of it without picking anything) has zero consequence -
        // no blood cost, no cooldown stamp, no "succeeded" messaging. Those all used to fire the
        // instant "Mesmerize" was picked from the feed menu, before this command menu was even shown,
        // which meant merely opening it and cancelling out still counted as a completed feed for the
        // day. CommitTownspersonMesmerize (below) is what actually pays for it now, called once, only
        // when the player picks a real action (Loot/Interrogate) from this menu.
        public static void ShowForTownsperson(GameObject npcObject)
        {
            if (npcObject == null)
                return;

            VoxsVampirism.Log("MesmerizeCommandMenu: menu opened for townsperson '{0}' (no cost yet).", npcObject.name);
            ShowMenu(true, npcObject, null);
        }

        // Pays Mesmerize's own 1-blood cost - deferred here from ShowForTownsperson so opening the
        // command menu and backing out never has consequences. Call exactly once, right before a real
        // action actually proceeds, from each of DoLoot/DoInterrogateTownsperson below - never called
        // twice in the same encounter, since picking either of those options closes the menu.
        //
        // Does NOT stamp SneakFeedController's own feed cooldown (an earlier version did) - a
        // townsperson's Mesmerize menu never offers Feed at all (see file header), so Loot/Interrogate
        // aren't a feed and shouldn't count as one. Stamping it here meant mesmerizing a townsperson to
        // loot or interrogate them silently blocked a real narrative feed scene on that same target for
        // the rest of the day - not the intent, corrected per explicit report. Loot's own one-shot
        // MesmerizeLootedMarker already stops the gold roll from being repeated regardless.
        static void CommitTownspersonMesmerize(GameObject npcObject)
        {
            VoxsVampirismEffect vampirism = GetVampirism();
            if (vampirism == null)
                return;

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(mesmerizeBloodCost, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0)
            {
                DaggerfallEntityBehaviour playerBehaviour = GameManager.Instance.PlayerEntityBehaviour;
                playerBehaviour.Entity.DecreaseHealth(Mathf.RoundToInt(playerBehaviour.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            DaggerfallUI.AddHUDText("Their will folds into yours without resistance.");
            VoxsVampirism.Log("MesmerizeCommandMenu: committed for townsperson '{0}'.", npcObject.name);
        }

        public static void ShowForEnemy(DaggerfallEntityBehaviour targetBehaviour)
        {
            if (targetBehaviour == null)
                return;

            VoxsVampirism.Log("MesmerizeCommandMenu: opened for enemy '{0}'.", targetBehaviour.name);
            ShowMenu(false, null, targetBehaviour);
        }

        static VoxsVampirismEffect GetVampirism()
        {
            return GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
        }

        // A wrapped flavor prompt shows first (DaggerfallListPickerWindow's own native layout has no
        // room for freeform text) before the actual command-menu picker opens.
        static void ShowMenu(bool isTownsperson, GameObject npcObject, DaggerfallEntityBehaviour enemyBehaviour)
        {
            VoxsVampirism.ShowWrappedPromptThenAction(
                "The eyes of your prey glaze over...",
                () => ShowMenuPicker(isTownsperson, npcObject, enemyBehaviour));
        }

        static void ShowMenuPicker(bool isTownsperson, GameObject npcObject, DaggerfallEntityBehaviour enemyBehaviour)
        {
            bool canInterrogate = isTownsperson || HasDirectionTarget(enemyBehaviour);

            DaggerfallListPickerWindow picker = new TitledListPickerWindow(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, "Mesmerize");
            // Feed is enemy-only - a mesmerized townsperson's own menu is Loot/Interrogate only, no
            // Feed (removed per explicit request; Charm/Stalk already cover feeding on a townsperson
            // without needing Mesmerize at all) and no Create Thrall (moved out entirely - see file
            // header, Post-Feed Offers update). Alphabetical within each branch.
            if (!isTownsperson)
                picker.ListBox.AddItem("Feed");
            if (canInterrogate)
                picker.ListBox.AddItem("Interrogate");
            picker.ListBox.AddItem("Loot");

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();
                switch (itemString)
                {
                    case "Feed":
                        DoFeed(enemyBehaviour);
                        break;
                    case "Loot":
                        DoLoot(npcObject, enemyBehaviour);
                        break;
                    case "Interrogate":
                        if (isTownsperson)
                            DoInterrogateTownsperson(npcObject);
                        else
                            DoInterrogateDirection(enemyBehaviour);
                        break;
                }
            };

            DaggerfallUI.UIManager.PushWindow(picker);
        }

        #region Feed

        // Enemy targets only - a mesmerized townsperson's menu no longer offers this at all (Charm/
        // Stalk already cover feeding on a townsperson). Guaranteed, no roll - Mesmerize's own
        // success (winning the roll) is what already established control. Human tier (+3, Humanity-
        // neutral - the target survives, so this is Feed()'s own living-target outcome, never
        // DoDrainHumanCorpse's corpse-tier -1 Humanity) for an EnemyClass-typed target; animal tier
        // (+1, neutral) for an EnemyMonster-typed one.
        //
        // Gated on SneakFeedController's own 24h feed cooldown (same marker/mechanism a normal
        // townsperson feed uses), stamped on the enemy's own GameObject - reopening this menu and
        // picking Feed repeatedly on the same still-pacified target would otherwise let a single
        // Mesmerize refill blood indefinitely, which isn't the intent (corrected per explicit report;
        // an earlier pass removed this gate entirely on the mistaken assumption a pacified enemy is
        // never worth re-gating - it is, just per-day rather than the strict one-shot-forever
        // MesmerizeFedMarker an even earlier version used).
        //
        // internal, not private: SneakFeedController.TryFindTarget's own direct feed-key path calls
        // this too, for a mesmerized target hit that way instead of through this menu - see that call
        // site's own comment for why (its generic "non-hostile enemy" fallback couldn't tell a
        // mesmerized target apart from a genuinely passive wild animal, which bypassed this cooldown
        // entirely when the player used the keybind instead of activating).
        internal static void DoFeed(DaggerfallEntityBehaviour enemyBehaviour)
        {
            if (enemyBehaviour == null)
                return;

            if (SneakFeedController.IsOnFeedCooldown(enemyBehaviour.gameObject))
            {
                DaggerfallUI.AddHUDText("You already drank your fill from them today.");
                return;
            }

            VoxsVampirismEffect vampirism = GetVampirism();
            if (vampirism == null)
                return;

            bool humanTier = enemyBehaviour.EntityType == EntityTypes.EnemyClass;
            if (humanTier)
                vampirism.Feed();
            else
                vampirism.FeedOnAnimal();

            SneakFeedController.StampFeedCooldown(enemyBehaviour.gameObject);

            DaggerfallUI.AddHUDText(string.Format("They offer no resistance as you drink. (Blood: {0}/10.)", vampirism.GetBloodPoints()));
            VoxsVampirism.Log("MesmerizeCommandMenu: Feed ({0}).", humanTier ? "human-tier" : "animal-tier");
        }

        #endregion

        #region Loot

        // Townsperson: the exact vanilla pickpocket-townsperson odds SneakFeedController.
        // KillNpcAndLeaveCorpse already uses (33% chance of 1-6 gold). Enemy: a true TransferAll from
        // the entity's own real Items collection, not a read-only copy - two live collections sharing
        // the same item references would risk a duplicate-pickup bug; a full transfer cleanly empties
        // their inventory into the loot window while leaving them alive, matching "take items without
        // killing them" exactly. MesmerizeLootedMarker blocks re-rolling the townsperson gold chance
        // by reopening the menu and picking Loot again.
        static void DoLoot(GameObject npcObject, DaggerfallEntityBehaviour enemyBehaviour)
        {
            GameObject markerTarget = npcObject != null ? npcObject : enemyBehaviour.gameObject;
            if (markerTarget.GetComponent<MesmerizeLootedMarker>() != null)
            {
                DaggerfallUI.AddHUDText("There is nothing left on them to take.");
                return;
            }
            markerTarget.AddComponent<MesmerizeLootedMarker>();

            if (npcObject != null)
                CommitTownspersonMesmerize(npcObject);

            Vector3 position = markerTarget.transform.position;
            DaggerfallLoot loot = GameObjectHelper.CreateLootContainer(
                LootContainerTypes.DroppedLoot, InventoryContainerImages.Ground, position, null,
                DaggerfallLootDataTables.randomTreasureArchive, 0, DaggerfallUnity.NextUID);
            // Never meant to be seen/walked up to in the world - purely a data conduit for the
            // inventory window opened below, so its own appearance is irrelevant.
            loot.gameObject.SetActive(false);

            if (npcObject != null)
            {
                int goldFound = 0;
                if (Dice100.FailedRoll(33))
                {
                    goldFound = UnityEngine.Random.Range(0, 6) + 1;
                    loot.Items.AddItem(ItemBuilder.CreateGoldPieces(goldFound));
                }
                VoxsVampirism.Log("MesmerizeCommandMenu: Loot (townsperson) - gold={0}.", goldFound);
            }
            else
            {
                loot.Items.TransferAll(enemyBehaviour.Entity.Items);
                VoxsVampirism.Log("MesmerizeCommandMenu: Loot (enemy) - items transferred.");
            }

            DaggerfallUI.Instance.InventoryWindow.LootTarget = loot;
            DaggerfallUI.UIManager.PushWindow(DaggerfallUI.Instance.InventoryWindow);
            DestroyLootContainerOnClose(loot.gameObject);
        }

        static void DestroyLootContainerOnClose(GameObject lootObject)
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            EventHandler handler = null;
            handler = (sender, args) =>
            {
                if (uiManager.TopWindow is DaggerfallInventoryWindow)
                    return;

                uiManager.OnWindowChange -= handler;
                if (lootObject != null)
                    UnityEngine.Object.Destroy(lootObject);
            };
            uiManager.OnWindowChange += handler;
        }

        #endregion

        #region Interrogate

        // Townsperson: real dialogue, but forced always-cooperative for the duration - opens with a
        // one-time HUD flavor line framing the whole conversation as compelled (DFU's own dialogue
        // answer text is static, parsed classic data with no per-line rewrite hook available, so this
        // is the honest scope: one framing line, not rewritten dialogue). talk_npcBehavior is a
        // GLOBAL, not per-NPC, debug flag (TalkManager.cs's own NPCsKnowEverything()) - toggled on
        // here, and back off the moment the talk window closes (DestroyLootContainerOnClose's own
        // OnWindowChange pattern, simplified - TalkToStaticNPC/TalkToMobileNPC push the talk window
        // synchronously, so no need to wait for it to first appear the way the loot window did).
        static void DoInterrogateTownsperson(GameObject npcObject)
        {
            CommitTownspersonMesmerize(npcObject);

            DaggerfallUI.AddHUDText("Their eyes go distant and unfocused - their voice comes flat and dreamlike, answering before they've even finished hearing the question.");

            Wenzil.Console.ConsoleCommandsDatabase.ExecuteCommand("talk_npcBehavior", "3");

            StaticNPC staticNpc = npcObject.GetComponent<StaticNPC>();
            if (staticNpc != null)
            {
                GameManager.Instance.TalkManager.TalkToStaticNPC(staticNpc, false);
            }
            else
            {
                MobilePersonNPC mobileNpc = npcObject.GetComponent<MobilePersonNPC>();
                if (mobileNpc != null)
                    GameManager.Instance.TalkManager.TalkToMobileNPC(mobileNpc);
            }

            ResetTalkBehaviorOnClose();
        }

        static void ResetTalkBehaviorOnClose()
        {
            IUserInterfaceManager uiManager = DaggerfallUI.UIManager;
            EventHandler handler = null;
            handler = (sender, args) =>
            {
                if (uiManager.TopWindow is DaggerfallTalkWindow)
                    return;

                uiManager.OnWindowChange -= handler;
                Wenzil.Console.ConsoleCommandsDatabase.ExecuteCommand("talk_npcsKnowUsual");
                VoxsVampirism.Log("MesmerizeCommandMenu: Interrogate - talk_npcsKnowUsual restored.");
            };
            uiManager.OnWindowChange += handler;
        }

        // Enemy target, dungeon quest-context only: reveals the compass bearing toward another
        // currently-spawned quest Foe/Person, nearest by distance if more than one candidate exists
        // (no tie-break specified in the original request). A quest resource's position only exists
        // transiently on its own QuestResourceBehaviour.transform while actually spawned in the loaded
        // scene (confirmed by reading QuestResource.cs directly - Foe/Person cache no position of
        // their own) - Place/Item/unspawned resources are automatically excluded by the same
        // QuestResourceBehaviour != null filter HasDirectionTarget below also uses, no special-casing
        // needed per resource type.
        const float verticalThreshold = 4f;

        // Response shown via ShowWrappedPromptThenAction (a real message box, click/Escape to close)
        // rather than a transient HUD text line - unlike Loot/most of this menu's other feedback,
        // this is a real line of "dialogue" being reported back to the player and reads better with
        // room to actually sit on screen instead of scrolling past with everything else.
        static void DoInterrogateDirection(DaggerfallEntityBehaviour targetBehaviour)
        {
            QuestResourceBehaviour ownQrb = targetBehaviour.GetComponent<QuestResourceBehaviour>();
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;

            QuestResourceBehaviour nearest = FindNearestOtherQuestTarget(ownQrb, playerPosition);
            if (nearest == null)
            {
                VoxsVampirism.ShowWrappedPromptThenAction(
                    "Their mind offers nothing more - whatever they knew, it isn't here.", () => { });
                return;
            }

            Vector3 delta = nearest.transform.position - playerPosition;
            string direction = DescribeDirection(delta);
            VoxsVampirism.ShowWrappedPromptThenAction(
                string.Format("Their mind cracks open under the pressure - \"They're... {0} of here.\"", direction), () => { });
            VoxsVampirism.Log("MesmerizeCommandMenu: Interrogate direction - '{0}' toward '{1}'.", direction, nearest.name);
        }

        // Cheap pre-check reused both to decide whether "Interrogate" even appears in the menu (see
        // ShowMenu) and, if chosen, to actually resolve the target - a small quest/resource scan, only
        // ever run on a menu-open or an actual Interrogate pick, never per-frame.
        static bool HasDirectionTarget(DaggerfallEntityBehaviour targetBehaviour)
        {
            if (targetBehaviour == null || !GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
                return false;

            QuestResourceBehaviour ownQrb = targetBehaviour.GetComponent<QuestResourceBehaviour>();
            return FindNearestOtherQuestTarget(ownQrb, GameManager.Instance.PlayerObject.transform.position) != null;
        }

        static QuestResourceBehaviour FindNearestOtherQuestTarget(QuestResourceBehaviour exclude, Vector3 playerPosition)
        {
            QuestResourceBehaviour nearest = null;
            float nearestSqDist = float.MaxValue;

            ulong[] activeQuests = QuestMachine.Instance.GetAllActiveQuests();
            for (int i = 0; i < activeQuests.Length; i++)
            {
                Quest quest = QuestMachine.Instance.GetQuest(activeQuests[i]);
                if (quest == null)
                    continue;

                CollectNearestCandidate(quest, typeof(Foe), exclude, playerPosition, ref nearest, ref nearestSqDist);
                CollectNearestCandidate(quest, typeof(Person), exclude, playerPosition, ref nearest, ref nearestSqDist);
            }

            return nearest;
        }

        static void CollectNearestCandidate(Quest quest, Type resourceType, QuestResourceBehaviour exclude, Vector3 playerPosition,
            ref QuestResourceBehaviour nearest, ref float nearestSqDist)
        {
            QuestResource[] resources = quest.GetAllResources(resourceType);
            if (resources == null)
                return;

            for (int i = 0; i < resources.Length; i++)
            {
                QuestResourceBehaviour qrb = resources[i].QuestResourceBehaviour;
                if (qrb == null || qrb == exclude)
                    continue;

                float sqDist = (qrb.transform.position - playerPosition).sqrMagnitude;
                if (sqDist < nearestSqDist)
                {
                    nearestSqDist = sqDist;
                    nearest = qrb;
                }
            }
        }

        // No DFU compass/bearing utility exists anywhere in the engine (checked directly) - this is
        // the whole of the needed math. World +Z is north/away in the same sense DFU's own compass/
        // automap already use. Up/Down is a flat delta.y threshold, not a discrete floor index - DFU
        // dungeons have no such concept (confirmed - no levelIndex/DungeonLevel/CurrentLevel field
        // anywhere in DaggerfallDungeon.cs/PlayerEnterExit.cs).
        static string DescribeDirection(Vector3 delta)
        {
            string horizontal = DescribeHorizontal(delta.x, delta.z);
            if (delta.y > verticalThreshold)
                return horizontal + " and up a level";
            if (delta.y < -verticalThreshold)
                return horizontal + " and down a level";
            return horizontal;
        }

        static string DescribeHorizontal(float dx, float dz)
        {
            float angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (angle < 0f)
                angle += 360f;

            if (angle >= 315f || angle < 45f)
                return "north";
            if (angle < 135f)
                return "east";
            if (angle < 225f)
                return "south";
            return "west";
        }

        #endregion
    }

    /// <summary>
    /// Blocks a second Loot roll against the same mesmerized target within one encounter - without
    /// this, reopening the command menu and picking Loot repeatedly would let a player re-roll the
    /// townsperson gold chance for free.
    /// </summary>
    public class MesmerizeLootedMarker : MonoBehaviour
    {
    }
}
