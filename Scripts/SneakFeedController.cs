// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Feature 3: a dedicated keybind that feeds directly on whatever the player is looking at - no
// confirmation menu, since the keybind itself is already the deliberate "I want to feed" action.
// Pickpocketing (previously offered alongside Feed in a shared menu, including on StaticNPCs, which
// vanilla's own Steal mode never supported at all) was dropped entirely along with the menu - this
// keybind is feed-only now. Uses its own forward raycast (plain Physics.Raycast from the camera),
// separate from PlayerActivate's own raycast, and reuses PlayerActivate's public activation-distance
// constants purely for range-matching consistency with vanilla, not because this shares any of its
// dispatch.
//
// Three distinct target types, each with its own rules:
//   - Awake StaticNPC/MobilePersonNPC townspeople: opens a Feed menu with skill-rolled options - see
//     ShowFeedMenu for the full option/guard-calling matrix. A successful feed while already at 2
//     blood points or fewer risks killing the victim outright (except Feast, which always does) -
//     see FeedOnAwakeNpc.
//   - Corpses (DaggerfallLoot with ContainerType == CorpseMarker): no menu, no location gate, no
//     roll - a dead body can't wake up and cry out, and there's nothing left to charm or subdue.
//     Each corpse can only be drained once (SneakFeedDrainedMarker below), same one-attempt shape as
//     the old pickpocket marker used to have.
//   - Passive (non-hostile) monsters/enemy classes: no menu (Charm's Etiquette roll makes no sense
//     against an animal), no location gate, no roll, gated on EnemyMotor.IsHostile being false - a
//     passified/friendly creature isn't going to fight back or summon guards either. Never subject
//     to the low-blood kill roll - that's awake-NPC-only. Same 24h-per-target feed cooldown every
//     other feed path uses (see DoFeedOnPassiveAnimal) - was unlimited at first, which let a single
//     passive/pacified creature be fed on indefinitely by spamming the key (explicit report).
//
// Travelling NPCs compatibility (Nexus Mods #1362) - explicit report: its wilderness wanderers
// couldn't be fed on at all. Read directly from the mod's own shipped source (extracted from its
// AssetBundle - it ships as loose C# text, same as every DFU mod, just not left on disk after
// compile): a wanderer is a deliberately minimal GameObject ("no EnemyMotor/EnemySenses/entity = no
// enemy AI can ever touch them", per that mod's own comment) with NONE of this file's existing
// target components - just a CharacterController (the raycast's actual hit target) plus a
// TravellingNPCActivator implementing DaggerfallWorkshop.Game.IPlayerActivable (a real public core
// interface, referenced directly here - no reflection needed for that part) so the game's own
// activate-click finds it, and a CHILD GameObject carrying the game's own MobilePersonBillboard for
// its visual (also a real public core class). TravellingNPCActivator/WandererRecord themselves are
// that mod's own internal (non-public) types, compiled into their own separate runtime assembly like
// every DFU mod's source is - genuinely inaccessible from here at compile time, not just unlinked -
// so there's no way to call into their own menu/despawn logic directly, and no need to: IPlayerActivable
// plus a child MobilePersonBillboard is exactly the signature GetSceneLocationTag/FeedOnNpc need,
// treating it as an ordinary opaque GameObject same as every other target type here. Gated on
// VoxsVampirism.TravellingNPCsInstalled so the extra GetComponentInChildren scan never runs at all
// when the content mod isn't present.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;
using VoxsVampirismMod.Effects;
using VoxsVampirismMod.Scenes;

namespace VoxsVampirismMod
{
    public class SneakFeedController : MonoBehaviour
    {
        // Excludes the Player layer, matching PlayerActivate's own raycast mask exactly - without
        // this, the ray (originating at the camera, which sits close to the player's own capsule
        // collider) can self-hit or graze the player's own DaggerfallEntityBehaviour at point-blank
        // range and never reach anything beyond it. Confirmed via the SneakFeed log: several attempts
        // hit 'PlayerAdvanced' (EntityType=Player) at ~0.2 units - this was silently swallowing
        // attempts to feed on anything low and close, like a corpse lying at the player's feet.
        static readonly int raycastLayerMask = ~(1 << LayerMask.NameToLayer("Player"));

        // World of Daggerfall compatibility: its farms/shrines are dressed with plain decorative
        // DaggerfallBillboard people (never a real StaticNPC/MobilePersonNPC - not even vanilla DFU
        // can talk to them, since they were never wired into TalkManager either). Only one archive is
        // confirmed so far (3101, seen in a live SneakFeed diagnostic log aimed at a farmer outside a
        // WoD farm) - add more here if another WoD person type still logs as unrecognized (the same
        // "raycast hit 'DaggerfallBillboard [TEXTURE.X, Index=Y]' ... none of StaticNPC/..." line
        // reports the exact archive to add). Every record within a listed archive counts as a person -
        // WoD appears to dedicate a whole archive to its own person sprites rather than mixing them
        // into a shared scenery archive.
        static readonly HashSet<int> worldOfDaggerfallPersonArchives = new HashSet<int> { 3101 };

        internal static bool IsWorldOfDaggerfallPersonBillboard(DaggerfallBillboard billboard)
        {
            return worldOfDaggerfallPersonArchives.Contains(billboard.Summary.Archive);
        }

        void Update()
        {
            if (!GameManager.Instance.IsPlayerOnHUD || InputManager.Instance.IsPaused)
                return;

            KeyCode keyCode = VoxsVampirism.SneakFeedKeyCode;
            if (keyCode == KeyCode.None || !InputManager.Instance.GetKeyDown(keyCode))
                return;

            if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                return;

            // Blocks every target type uniformly (NPC menu, corpse, monster) at this single choke
            // point - nothing about feeding is subtle enough to attempt with active guards nearby.
            if (AreGuardsPursuing())
            {
                DaggerfallUI.AddHUDText("Not with the guards on your heels!");
                return;
            }

            // Wilderness Hunt spec: a press TryFindTarget itself didn't consume (see its own header)
            // falls through here - only actually starts a hunt if the scene context resolves to
            // wilderness; every other "found nothing" case outside the wilderness does exactly what it
            // always did (nothing at all).
            if (!TryFindTarget())
                TryStartWildernessHunt();
        }

        // Deliberately looser than (and independent of) GetSceneLocationTag()'s own "wilderness" tag -
        // that classification runs through PlayerGPS.IsPlayerInTown(mustBeInLocationRect: false, ...),
        // which checks CurrentLocationType with NO rect membership check at all. CurrentLocationType
        // itself is never reset when the player leaves a location's rect (confirmed by reading
        // PlayerGPS.cs directly - only DiscoverCurrentLocation ever assigns it, nothing clears it back
        // on exit) - it's "undefined," i.e. STALE, once HasCurrentLocation goes false, not "none." With
        // World of Daggerfall installed (which scatters small farm/shrine/lone-tavern locations across
        // formerly open land), passing anywhere near any one of them latches CurrentLocationType to a
        // town-like value that then never clears again, so IsPlayerInTown(false, ...) reads "in town"
        // everywhere from then on, permanently - confirmed as the actual cause of a wilderness hunt
        // never triggering, live-tested this session, no matter how far the player moved away
        // afterward. Eligible everywhere except: indoors anywhere at all (temple, home, shop, tavern,
        // guildhall, whatever has a roof - there's no wildlife indoors, a broader net than just "temple
        // or home" but the same underlying reason), a genuine town/hamlet/village exterior (checked via
        // the RELIABLE live-updated IsPlayerInLocationRect flag first, which IS properly cleared on
        // exit unlike CurrentLocationType alone - a farm/shrine/lone-tavern's own LocationType is
        // deliberately not in the excluded set, so passing one no longer blocks hunting even while
        // standing right next to it), or a dungeon interior.
        static bool IsWildernessHuntEligible()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInside || playerEnterExit.IsPlayerInsideDungeon)
                return false;

            PlayerGPS playerGPS = GameManager.Instance.PlayerGPS;
            if (!playerGPS.IsPlayerInLocationRect)
                return true;

            DFRegion.LocationTypes locationType = playerGPS.CurrentLocationType;
            return locationType != DFRegion.LocationTypes.TownCity
                && locationType != DFRegion.LocationTypes.TownHamlet
                && locationType != DFRegion.LocationTypes.TownVillage;
        }

        // Wilderness Hunt spec: replaces the removed Hunt Animal spell as the wilderness blood-
        // recovery valve. Cooldown is a global, save-persisted timestamp (VoxsVampirismEffect.
        // IsAnimalHuntOnCooldown), not the per-NPC daily marker feed scenes use - checked here, before
        // the scene ever starts, so an on-cooldown press shows a short flavor refusal instead of
        // silently doing nothing (matching this mod's own "every keybind press gets some feedback"
        // convention). Actually stamping the cooldown happens later, at Approach-commit inside
        // NarrativeFeedSceneEngine's own RunApproach - see TryStartAnimalHunt's header for why.
        void TryStartWildernessHunt()
        {
            if (!IsWildernessHuntEligible())
            {
                VoxsVampirism.Log("SneakFeed: wilderness hunt not offered - inside, in a dungeon, or in a real town (locationType={0}, inLocationRect={1}, inside={2}, insideDungeon={3}).",
                    GameManager.Instance.PlayerGPS.CurrentLocationType, GameManager.Instance.PlayerGPS.IsPlayerInLocationRect,
                    GameManager.Instance.PlayerEnterExit.IsPlayerInside, GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon);
                return;
            }

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            if (vampirism.IsAnimalHuntOnCooldown())
            {
                VoxsVampirism.Log("SneakFeed: wilderness hunt refused - still on cooldown.");
                DaggerfallUI.AddHUDText("The wild has nothing more for you yet.");
                return;
            }

            NarrativeFeedSceneEngine.TryStartAnimalHunt();
        }

        // Active hostile Knight_CityWatch enemies are the actual mechanism behind DFU's own
        // guard-pursuit (PlayerEntity.SpawnCityGuards spawns exactly this enemy type) - there's no
        // simpler "is the player currently being pursued" flag exposed anywhere, so this checks for
        // their real-world presence directly. includingPacified:false so a guard standing around
        // un-alerted (if that's even reachable for this type) doesn't count as active pursuit.
        static bool AreGuardsPursuing()
        {
            return GameManager.Instance.HowManyEnemiesOfType(MobileTypes.Knight_CityWatch, true, false) > 0;
        }

        // Plain int constants rather than a nested enum - see the mod's own history for why: a
        // nested enum in this mod's source once broke the ENTIRE mod's loading (DFU's runtime
        // compiler reflection scan choked on it), so custom enums are never used here. Residence and
        // Inn are deliberately narrow (same private-location definition as the original design) -
        // everywhere else, including other indoor building types like shops or guild halls, counts as
        // Streets for both guard-calling and the roleplay flavor text below.
        const int LocationResidence = 0;
        const int LocationInn = 1;
        const int LocationStreets = 2;

        internal static int GetLocationCategory()
        {
            if (GameManager.Instance.PlayerEnterExit.IsPlayerInside)
            {
                DFLocation.BuildingTypes buildingType = GameManager.Instance.PlayerEnterExit.BuildingType;
                if (buildingType >= DFLocation.BuildingTypes.House1 && buildingType <= DFLocation.BuildingTypes.House6)
                    return LocationResidence;
                if (buildingType == DFLocation.BuildingTypes.Tavern)
                    return LocationInn;
            }

            return LocationStreets;
        }

        // Narrative Feeding Scenes' own five-tag location vocabulary (streets/inn/residence/palace/
        // wilderness) - a separate, richer classification from GetLocationCategory above (which
        // stays flat-menu-only, unchanged). Indoors: Tavern->inn, House1-6/HouseForSale->residence,
        // Palace->palace, every other indoor building type->streets (matching GetLocationCategory's
        // own "everywhere else counts as Streets" precedent - shops, guild halls, temples, etc.).
        // Outdoors: PlayerGPS.IsPlayerInTown(mustBeOutside:true) is the same composite check vanilla
        // itself uses to tell "in a settlement" from "wilderness between locations" - there is no
        // dedicated engine flag for either state alone. This composite test is closed/exclusive by
        // construction: any location type it doesn't recognize (including anything World of
        // Daggerfall or another location-adding mod introduces) falls to wilderness automatically,
        // with no PWoD-specific code needed here.
        internal const string SceneLocationStreets = "streets";
        internal const string SceneLocationInn = "inn";
        internal const string SceneLocationResidence = "residence";
        internal const string SceneLocationPalace = "palace";
        internal const string SceneLocationWilderness = "wilderness";

        // Guild Locations update: six more specific interior tags, checked ahead of the generic
        // Tavern/Palace/House/fallback-to-streets checks below. Temple is its own distinct
        // DFLocation.BuildingTypes value (straightforward). Mages Guild/Fighters Guild/Knightly Order
        // all share DFU's single generic BuildingTypes.GuildHall value - which guild owns a given hall
        // is only knowable via the building's own FactionID -> FactionFile.GuildGroups lookup (see
        // ResolveGuildHallTag). Dark Brotherhood does NOT use BuildingTypes.GuildHall at all, despite
        // being a real, enterable, faction-owned location - DFU's own sanctuaries are secretly
        // House-type buildings (PlayerGPS.cs's own comment: "Do not undiscover residence if it's a
        // Thieves Guild or Dark Brotherhood hideout") - confirmed live after an earlier version of
        // GetSceneLocationTag only ran ResolveGuildHallTag behind a BuildingTypes.GuildHall gate and
        // silently misread every Dark Brotherhood location as a plain residence; the lookup now runs
        // unconditionally there instead, safe because ResolveGuildHallTag itself already returns null
        // for anything that isn't one of its four recognized faction groups. Thieves Guild has no
        // distinct GuildGroups value of its own in DFU (GeneralPopulace doubles for it, gated on
        // SocialGroups.Underworld) and no enterable interior was found anywhere in engine source
        // actually faction-tagged that way - the Thieves Guild operates without public halls in
        // vanilla Daggerfall. This tag is wired in for forward compatibility (a mod-added building, or
        // a future DFU change, could start producing it) but ResolveGuildHallTag can never actually
        // return it today - the fragments tagged for it simply sit dormant.
        internal const string SceneLocationDarkBrotherhood = "darkbrotherhood";
        internal const string SceneLocationTemple = "temple";
        internal const string SceneLocationFightersGuild = "fightersguild";
        internal const string SceneLocationKnightlyOrder = "knightlyorder";
        internal const string SceneLocationMagesGuild = "magesguild";
        internal const string SceneLocationThievesGuild = "thievesguild";

        internal static string GetSceneLocationTag()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInside)
            {
                DFLocation.BuildingTypes buildingType = playerEnterExit.BuildingType;

                if (buildingType == DFLocation.BuildingTypes.Temple)
                    return SceneLocationTemple;

                bool isHouseType = (buildingType >= DFLocation.BuildingTypes.House1 && buildingType <= DFLocation.BuildingTypes.House6)
                    || buildingType == DFLocation.BuildingTypes.HouseForSale;

                if (buildingType == DFLocation.BuildingTypes.GuildHall)
                {
                    // Real guild halls only - Mages/Fighters/Knightly Order all legitimately use this
                    // building type, so every group ResolveGuildHallTag recognizes is fair game here.
                    string guildTag = ResolveGuildHallTag(playerEnterExit.FactionID);
                    if (guildTag != null)
                        return guildTag;
                }
                else if (isHouseType)
                {
                    // Dark Brotherhood ONLY - not the full ResolveGuildHallTag sweep the real GuildHall
                    // branch above uses. DFU's own Dark Brotherhood hideouts are secretly implemented
                    // as ordinary House-type buildings (PlayerGPS.cs's own comment: "Do not undiscover
                    // residence if it's a Thieves Guild or Dark Brotherhood hideout"), confirmed live
                    // (a player standing inside one had it read as a plain residence) - but checking
                    // every recognized faction group here, not just this one, caused a regression:
                    // an ordinary Tavern's own FactionID can be tied to a "trainer" service NPC (e.g.
                    // FactionFile.FactionIDs.Fighter_Trainers, referenced directly in
                    // PlayerEnterExit.cs) whose own ggroup happens to be FightersGuild despite the
                    // building itself never being any kind of real guild hall - confirmed live too (an
                    // ordinary Inn read as Fighters Guild once this ran unconditionally for every
                    // building type). Dark Brotherhood is the one confirmed case of a real secret
                    // guild hideout inside an ordinary House-type building, so only that one group is
                    // checked, and only for House-type buildings specifically - Tavern/Palace/etc.
                    // never reach this branch at all, exactly as before this whole fix.
                    if (ResolveGuildHallTag(playerEnterExit.FactionID) == SceneLocationDarkBrotherhood)
                        return SceneLocationDarkBrotherhood;
                }

                if (buildingType == DFLocation.BuildingTypes.Tavern)
                    return SceneLocationInn;
                if (buildingType == DFLocation.BuildingTypes.Palace)
                    return SceneLocationPalace;
                if (isHouseType)
                    return SceneLocationResidence;

                return IsMainCastleCity() ? SceneLocationPalace : SceneLocationStreets;
            }

            if (!GameManager.Instance.PlayerGPS.IsPlayerInTown(false, true))
                return SceneLocationWilderness;

            // Bug fix (explicit report): this used to also apply IsMainCastleCity() out here, tagging
            // every ordinary townsperson standing on the open streets of Daggerfall/Wayrest/Sentinel as
            // Palace. The "extension of the castle" reasoning (see IsMainCastleCity's own comment) is
            // only defensible for an unclassified INTERIOR within one of these three cities (a dungeon,
            // or a room DFU doesn't classify as any known BuildingType) - the open streets themselves
            // are still just streets, city guards and all, not palace grounds. Outdoors is now always
            // plain Streets (wilderness already excluded above).
            return SceneLocationStreets;
        }

        // Daggerfall, Wayrest, and Sentinel are the three province capitals whose own Palace building
        // gets a unique proper name instead of the generic "Palace" every other region's own count/
        // duke palace gets (FormulaHelper.GenerateBuildingName's own Palace case, TEXT.RSC records
        // 475/476/477 - "Castle Daggerfall"/"Castle Wayrest"/"Castle Sentinel") - Daggerfall's own
        // three "great castles." Wherever this tag would otherwise fall back to plain Streets from an
        // UNCLASSIFIED INTERIOR (a dungeon, or any indoor space that isn't already its own more specific
        // tag - Temple/GuildHall/Tavern/Palace/Residence) within one of these three cities specifically,
        // it pulls from the Palace scene pool instead, since these three settlements are themselves an
        // extension of their own castle rather than an ordinary city dungeon/building. Deliberately NOT
        // applied to the open streets themselves (see GetSceneLocationTag's own outdoor branch, fixed
        // separately) - only indoors. Every other region's own capital (with its own generic, unnamed
        // Palace) is unaffected - only its actual Palace building interior gets the Palace tag, exactly
        // as before.
        static bool IsMainCastleCity()
        {
            string locationName = GameManager.Instance.PlayerGPS.CurrentLocalizedLocationName;
            return locationName == TextManager.Instance.GetLocalizedText("daggerfall")
                || locationName == TextManager.Instance.GetLocalizedText("wayrest")
                || locationName == TextManager.Instance.GetLocalizedText("sentinel");
        }

        // Maps a GuildHall interior's own owning faction to one of the four guild-hall location tags
        // this mod actually special-cases (Dark Brotherhood, Mages Guild, Fighters Guild, Knightly
        // Order - the last one correctly shared across every region-specific order faction, since
        // they all carry the same GuildGroups.KnightlyOrder classification in FACTION.TXT). Returns
        // null for a hall with no faction data, or one whose group isn't one of these four - the
        // caller falls through to the plain "streets" classification in that case, same as any other
        // unremarkable indoor building type.
        static string ResolveGuildHallTag(uint factionID)
        {
            FactionFile.FactionData factionData;
            if (!GameManager.Instance.PlayerEntity.FactionData.GetFactionData((int)factionID, out factionData))
                return null;

            switch ((FactionFile.GuildGroups)factionData.ggroup)
            {
                case FactionFile.GuildGroups.DarkBrotherHood: return SceneLocationDarkBrotherhood;
                case FactionFile.GuildGroups.MagesGuild: return SceneLocationMagesGuild;
                case FactionFile.GuildGroups.FightersGuild: return SceneLocationFightersGuild;
                case FactionFile.GuildGroups.KnightlyOrder: return SceneLocationKnightlyOrder;
                default: return null;
            }
        }

        // Logs every keypress (bounded to explicit player action, not per-frame spam) so a failed
        // attempt is diagnosable from Player.log alone - this whole raycast/component chain had zero
        // visibility before, and "why didn't it feed" was otherwise unanswerable from outside the
        // Unity Editor.
        //
        // Wilderness Hunt spec: returns whether the press was actually consumed by something (a feed,
        // a menu, a status message, any of it) - Update() uses false to fall through into the
        // wilderness hunt trigger. "Consumed" is deliberately narrower than "hit something": a
        // StaticNPC/MobilePersonNPC/WoD billboard found but out of range, or a raycast that hit
        // nothing/unrecognized scenery, both count as false ("no valid feed target in reach," per the
        // spec's own wording) - but a thrall, a corpse/loot object, or any other recognized creature
        // (hostile or passive) all count as true regardless of range/eligibility, since the player was
        // clearly aiming at something specific, not empty wilderness.
        bool TryFindTarget()
        {
            Camera mainCamera = GameManager.Instance.MainCamera;
            if (!mainCamera)
                return true;

            Ray ray = new Ray(mainCamera.transform.position, mainCamera.transform.forward);
            RaycastHit hit;
            float maxDistance = Mathf.Max(
                Mathf.Max(PlayerActivate.StaticNPCActivationDistance, PlayerActivate.MobileNPCActivationDistance),
                Mathf.Max(PlayerActivate.CorpseActivationDistance, PlayerActivate.DefaultActivationDistance));
            if (!Physics.Raycast(ray, out hit, maxDistance, raycastLayerMask))
            {
                VoxsVampirism.Log("SneakFeed: raycast hit nothing within {0:F1} units.", maxDistance);
                return false;
            }

            StaticNPC staticNpc = hit.transform.GetComponent<StaticNPC>();
            if (staticNpc != null)
            {
                bool inRange = hit.distance <= PlayerActivate.StaticNPCActivationDistance;
                if (inRange)
                {
                    FeedOnNpc(staticNpc.gameObject);
                    return true;
                }

                VoxsVampirism.Log("SneakFeed: hit StaticNPC '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                return false;
            }

            MobilePersonNPC mobileNpc = hit.transform.GetComponent<MobilePersonNPC>();
            if (mobileNpc != null)
            {
                bool inRange = hit.distance <= PlayerActivate.MobileNPCActivationDistance;
                if (inRange)
                {
                    FeedOnNpc(mobileNpc.gameObject);
                    return true;
                }

                VoxsVampirism.Log("SneakFeed: hit MobilePersonNPC '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                return false;
            }

            if (VoxsVampirism.WorldOfDaggerfallInstalled)
            {
                DaggerfallBillboard billboard = hit.transform.GetComponent<DaggerfallBillboard>();
                if (billboard != null && IsWorldOfDaggerfallPersonBillboard(billboard))
                {
                    bool inRange = hit.distance <= PlayerActivate.StaticNPCActivationDistance;
                    if (inRange)
                    {
                        FeedOnNpc(billboard.gameObject);
                        return true;
                    }

                    VoxsVampirism.Log("SneakFeed: hit World of Daggerfall person billboard '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                    return false;
                }
            }

            if (VoxsVampirism.TravellingNPCsInstalled)
            {
                // IPlayerActivable alone would also match doors/levers/other mods' own activatable
                // objects - requiring a child MobilePersonBillboard too (see file header) narrows this
                // to specifically a townsperson-shaped wanderer, matching that mod's own actual
                // wanderer structure exactly rather than any activatable thing in the world.
                if (hit.transform.GetComponent<IPlayerActivable>() != null
                    && hit.transform.GetComponentInChildren<MobilePersonBillboard>() != null)
                {
                    bool inRange = hit.distance <= PlayerActivate.MobileNPCActivationDistance;
                    if (inRange)
                    {
                        FeedOnNpc(hit.transform.gameObject);
                        return true;
                    }

                    VoxsVampirism.Log("SneakFeed: hit Travelling NPCs wanderer '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                    return false;
                }
            }

            DaggerfallLoot loot = hit.transform.GetComponent<DaggerfallLoot>();
            if (loot != null)
            {
                bool isCorpse = loot.ContainerType == LootContainerTypes.CorpseMarker;
                bool inRange = hit.distance <= PlayerActivate.CorpseActivationDistance;
                bool alreadyDrained = IsCorpseDrained(loot);
                if (isCorpse && inRange && !alreadyDrained)
                {
                    // Dispatch priority (Blood Economy Rebalance, §1): fights-back/vampire-species
                    // corpses keep their own full-refill special case; this mod's own murdered
                    // townspeople (TownspersonCorpseMarker, stamped by KillNpcAndLeaveCorpse) keep
                    // their flat +3/-1 Humanity tier; everything else uses the HP/divisor formula via
                    // CorpseBloodInfoMarker (stamped for every real enemy death - see
                    // HandleAnyEnemyDeathForBloodInfo), with a bloodless check BEFORE marking drained
                    // at all, since a refused drain "consumes nothing" and shouldn't burn the
                    // one-attempt marker. A corpse with no marker at all (pre-mod save, or a
                    // third-party mod's own death path) falls back to the old flat animal tier.
                    FightsBackCorpseMarker vampireCorpse = loot.GetComponent<FightsBackCorpseMarker>();
                    CorpseBloodInfoMarker bloodInfo = loot.GetComponent<CorpseBloodInfoMarker>();
                    bool isVampireSpecies = vampireCorpse != null || (bloodInfo != null && bloodInfo.IsVampireSpecies);

                    if (isVampireSpecies)
                    {
                        MarkCorpseDrained(loot);
                        DoDrainVampireCorpse(vampireCorpse != null && vampireCorpse.IsAncient);
                    }
                    else if (loot.GetComponent<TownspersonCorpseMarker>() != null)
                    {
                        MarkCorpseDrained(loot);
                        DoDrainHumanCorpse();
                    }
                    else if (bloodInfo != null && bloodInfo.IsBloodless)
                    {
                        DaggerfallUI.AddHUDText("Nothing flows in this one.");
                    }
                    else if (bloodInfo != null)
                    {
                        MarkCorpseDrained(loot);
                        DoDrainWildCorpse(bloodInfo.MaxHealth);
                    }
                    else
                    {
                        MarkCorpseDrained(loot);
                        DoDrainAnimalCorpse();
                    }
                }
                else if (isCorpse && inRange && alreadyDrained)
                    DaggerfallUI.AddHUDText("This corpse has already been drained.");
                else
                    VoxsVampirism.Log("SneakFeed: hit DaggerfallLoot '{0}' at {1:F1} units, ContainerType={2} (isCorpse={3}), inRange={4}, alreadyDrained={5}.",
                        hit.transform.name, hit.distance, loot.ContainerType, isCorpse, inRange, alreadyDrained);
                return true;
            }

            DaggerfallEntityBehaviour entityBehaviour = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
            if (entityBehaviour != null)
            {
                if (ThrallManager.IsThrall(entityBehaviour.gameObject))
                {
                    bool thrallInRange = hit.distance <= PlayerActivate.DefaultActivationDistance;
                    if (thrallInRange)
                        ShowThrallMenu(entityBehaviour.gameObject);
                    else
                        VoxsVampirism.Log("SneakFeed: hit own thrall '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                    return true;
                }

                // Bug fix: a Dominated creature is never ThrallManager.IsThrall (Dominate targets
                // aren't roster-tracked at all), so it fell through to the generic "hostile==false, in
                // range" branch below - the free, cooldown-less instant feed meant for genuinely wild
                // animals, not something bound to the player. That let a Dominated ally be fed on
                // indefinitely. Checked here, before that fallback, the same way the thrall check
                // above short-circuits it for actual thralls - ThrallManager.TryFeedOnDominated
                // enforces its own 24h-per-creature cooldown (DominateTimer.LastFedTime).
                if (entityBehaviour.GetComponent<DominateTimer>() != null)
                {
                    bool dominatedInRange = hit.distance <= PlayerActivate.DefaultActivationDistance;
                    if (dominatedInRange)
                        ThrallManager.TryFeedOnDominated(entityBehaviour.gameObject);
                    else
                        VoxsVampirism.Log("SneakFeed: hit own Dominated creature '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                    return true;
                }

                // Same bug class as the Dominated check above, same fix - Mesmerize's AoE pacify sets
                // EnemyMotor.IsHostile = false on its targets (see MesmerizeEffect.RunAoePacify), which
                // is otherwise indistinguishable from a genuinely wild passive animal to the generic
                // "hostile==false, in range" fallback below - that fallback is the free, cooldown-less
                // instant feed meant for actual wildlife, not something the player just pacified with a
                // spell. Confirmed live: pressing the Sneak Feed key on a mesmerized enemy fed
                // indefinitely, completely bypassing the 24h cooldown MesmerizeCommandMenu.DoFeed
                // enforces when reached the normal way (activate -> command menu -> Feed). Routed here
                // to that exact same method (made internal for this call) rather than duplicating its
                // cooldown-check/pay/HUD-text logic a second time.
                if (entityBehaviour.GetComponent<MesmerizedEnemyMarker>() != null)
                {
                    bool mesmerizedInRange = hit.distance <= PlayerActivate.DefaultActivationDistance;
                    if (mesmerizedInRange)
                        MesmerizeCommandMenu.DoFeed(entityBehaviour);
                    else
                        VoxsVampirism.Log("SneakFeed: hit mesmerized enemy '{0}' at {1:F1} units, out of range.", hit.transform.name, hit.distance);
                    return true;
                }

                bool isEnemyType = entityBehaviour.EntityType == EntityTypes.EnemyMonster || entityBehaviour.EntityType == EntityTypes.EnemyClass;
                bool inRange = hit.distance <= PlayerActivate.DefaultActivationDistance;
                EnemyMotor motor = hit.transform.GetComponent<EnemyMotor>();
                bool nonHostile = motor != null && !motor.IsHostile;
                if (isEnemyType && inRange && nonHostile)
                    DoFeedOnPassiveAnimal(entityBehaviour.gameObject);
                else
                {
                    // The instant no-menu feed only ever works on something that isn't actively
                    // fighting - an alert, hostile creature isn't the free meal a passive one is.
                    // Previously this failed with zero player-facing feedback (log-only), which read
                    // as the keybind simply not working.
                    if (isEnemyType && inRange && motor != null && motor.IsHostile)
                        DaggerfallUI.AddHUDText("It's too alert to feed on quietly - finish the fight first.");

                    VoxsVampirism.Log("SneakFeed: hit DaggerfallEntityBehaviour '{0}' at {1:F1} units, EntityType={2} (isEnemyType={3}), inRange={4}, motorFound={5}, IsHostile={6}.",
                        hit.transform.name, hit.distance, entityBehaviour.EntityType, isEnemyType, inRange, motor != null, motor != null && motor.IsHostile);
                }
                return true;
            }

            VoxsVampirism.Log("SneakFeed: raycast hit '{0}' at {1:F1} units, but it has none of StaticNPC/MobilePersonNPC/DaggerfallLoot/DaggerfallEntityBehaviour.",
                hit.transform.name, hit.distance);
            return false;
        }

        // The menu itself is always openable now, even on cooldown - it used to refuse to open at all
        // here, which is what made "opening a menu and backing out" look like it was somehow the thing
        // costing a feed attempt. The cooldown is now enforced per-option, inside ShowFeedMenu's own
        // dispatch below, so merely opening (and cancelling out of) the menu never has consequences.
        //
        // Narrative Feeding Scenes are a core feature now, not an optional toggle - every awake-
        // townsperson feed goes through the scene engine unconditionally. ShowFeedMenu (below) isn't
        // dead code even so: TryStartScene itself still falls back to it defensively if it can't find
        // a live VoxsVampirismEffect or the fragment library fails to load - a genuine "this really
        // can't work right now" safety net, not a player-facing opt-out.
        void FeedOnNpc(GameObject npcObject)
        {
            NarrativeFeedSceneEngine.TryStartScene(npcObject, GetSceneLocationTag());
        }

        // ===== Drained-corpse persistence (explicit exploit report: save-then-reload let a corpse be
        // drained again) =====
        //
        // SneakFeedDrainedMarker is a plain MonoBehaviour on the corpse's own loot GameObject, and no
        // save system persists components - worse, the same loss happens WITHOUT any save involved,
        // since leaving a dungeon/interior and returning tears the corpse GameObject down and rebuilds
        // it from DFU's own serialized loot state, marker-free either way. The persistent authority is
        // therefore this LoadID set, not the marker: every drain records the corpse's DFU LoadID here
        // (the same stable identity the Dominate save records already key on), the set rides in this
        // mod's own custom save data (VoxsVampirismEffect.CustomSaveData_v5.drainedCorpses), and
        // every already-drained check consults marker OR set - so the answer survives both a save/load
        // round trip and a plain leave-and-return. The marker itself is kept purely as the fast
        // same-scene path plus coverage for any corpse with no LoadID at all (which DFU never
        // serializes, so it can't outlive its scene in any way that matters). Entries for corpses
        // that eventually despawn for good are harmless 8-byte leftovers, deliberately never pruned -
        // pruning "not currently loaded" would wrongly forget every drained corpse in every OTHER
        // scene.
        static readonly HashSet<ulong> drainedCorpseLoadIDs = new HashSet<ulong>();

        public static void MarkCorpseDrained(DaggerfallLoot loot)
        {
            if (loot == null)
                return;

            if (loot.GetComponent<SneakFeedDrainedMarker>() == null)
                loot.gameObject.AddComponent<SneakFeedDrainedMarker>();
            if (loot.LoadID != 0)
                drainedCorpseLoadIDs.Add(loot.LoadID);
        }

        public static bool IsCorpseDrained(DaggerfallLoot loot)
        {
            if (loot == null)
                return false;

            if (loot.GetComponent<SneakFeedDrainedMarker>() != null)
                return true;
            return loot.LoadID != 0 && drainedCorpseLoadIDs.Contains(loot.LoadID);
        }

        public static ulong[] GetDrainedCorpseSaveRecords()
        {
            ulong[] records = new ulong[drainedCorpseLoadIDs.Count];
            drainedCorpseLoadIDs.CopyTo(records);
            return records;
        }

        // Called from VoxsVampirismEffect.RestoreSaveData - which only runs when the loaded save has
        // vampirism data at all, so the set is ALSO cleared unconditionally on every load start
        // (ClearDrainedCorpsesOnStartLoad below), the same stale-static-state-across-a-load fix
        // ThrallManager.OnStartLoad/HuntersSightVisuals already needed for their own statics.
        public static void RestoreDrainedCorpseRecords(ulong[] records)
        {
            drainedCorpseLoadIDs.Clear();
            if (records == null)
                return;

            for (int i = 0; i < records.Length; i++)
                drainedCorpseLoadIDs.Add(records[i]);

            VoxsVampirism.Log("SneakFeed: restored {0} drained-corpse record(s) from save.", records.Length);
        }

        static void ClearDrainedCorpsesOnStartLoad(SaveData_v1 saveData)
        {
            drainedCorpseLoadIDs.Clear();
        }

        // Shared with MesmerizeCommandMenu (public) so its own townsperson command-menu actions can't
        // slip past a cooldown that a different feed attempt already stamped today.
        public static bool IsOnFeedCooldown(GameObject npcObject)
        {
            SneakFeedCooldownMarker cooldown = npcObject.GetComponent<SneakFeedCooldownMarker>();
            if (cooldown == null)
                return false;

            uint elapsed = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime() - cooldown.lastAttemptTime;
            return elapsed < DaggerfallDateTime.MinutesPerDay;
        }

        public static void StampFeedCooldown(GameObject npcObject)
        {
            SneakFeedCooldownMarker cooldown = npcObject.GetComponent<SneakFeedCooldownMarker>();
            if (cooldown == null)
                cooldown = npcObject.AddComponent<SneakFeedCooldownMarker>();
            cooldown.lastAttemptTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        }

        // MobilePersonNPC GameObjects are pooled and reused for a completely different randomized
        // person when recycled (PopulationManager.PopulationPool) - without this, a cooldown marker
        // left on a pooled slot would wrongly carry over onto whichever new person that slot gets
        // reassigned to next, blocking someone the player has never actually met. Subscribing to
        // OnMobileNPCEnable (fired right after RandomiseNPC() gives a recycled slot its new identity)
        // clears any leftover marker at exactly the right moment. StaticNPCs aren't pooled this way -
        // their GameObjects are torn down and rebuilt fresh each time their building interior loads -
        // so they need no equivalent handling.
        void Awake()
        {
            PopulationManager.OnMobileNPCEnable += ClearCooldownOnRecycle;
            EnemyDeath.OnEnemyDeath += HandleFightsBackEnemyDeath;
            EnemyDeath.OnEnemyDeath += HandleAnyEnemyDeathForBloodInfo;
            EnemyDeath.OnEnemyDeath += HandleDevourThrallCorpseMarking;
            EntityEffectBroker.OnNewMagicRound += CheckFledReturns;
            EntityEffectBroker.OnNewMagicRound += CheckFrenzy;
            SaveLoadManager.OnStartLoad += ClearDrainedCorpsesOnStartLoad;
        }

        void OnDestroy()
        {
            PopulationManager.OnMobileNPCEnable -= ClearCooldownOnRecycle;
            EnemyDeath.OnEnemyDeath -= HandleFightsBackEnemyDeath;
            EnemyDeath.OnEnemyDeath -= HandleAnyEnemyDeathForBloodInfo;
            EnemyDeath.OnEnemyDeath -= HandleDevourThrallCorpseMarking;
            EntityEffectBroker.OnNewMagicRound -= CheckFledReturns;
            EntityEffectBroker.OnNewMagicRound -= CheckFrenzy;
            SaveLoadManager.OnStartLoad -= ClearDrainedCorpsesOnStartLoad;
        }

        static void ClearCooldownOnRecycle(PopulationManager.PoolItem poolItem)
        {
            if (poolItem.npc == null)
                return;

            SneakFeedCooldownMarker cooldown = poolItem.npc.GetComponent<SneakFeedCooldownMarker>();
            if (cooldown != null)
                Destroy(cooldown);

            // Every other per-person marker this mod ever leaves on a pooled MobilePersonNPC gets the
            // same treatment (full mod audit - this handler originally only knew about the cooldown
            // marker): a recycled slot is a completely different randomized person, so a leftover
            // FightsBackFledMarker would force-reveal a stranger at the original victim's stale
            // position when CheckFledReturns fires, and a leftover MesmerizeLootedMarker would
            // permanently deny Mesmerize-looting someone the player has never met.
            FightsBackFledMarker fled = poolItem.npc.GetComponent<FightsBackFledMarker>();
            if (fled != null)
            {
                pendingFledReturns.Remove(poolItem.npc.gameObject);
                Destroy(fled);
            }

            MesmerizeLootedMarker looted = poolItem.npc.GetComponent<MesmerizeLootedMarker>();
            if (looted != null)
                Destroy(looted);
        }

        // Three options indoors (Residence/Inn), two outdoors ("Stalk" dropped - it only makes sense
        // against someone who's actually asleep in a private place). Item text is used for dispatch
        // rather than list index, since the option set's length differs between the two menus and
        // index arithmetic would be an easy way to silently pick the wrong handler. "Create Thrall"
        // moved out entirely (Mesmerize Command Menu spec) - it's now exclusively an outcome of the
        // new "Mesmerize" option below, never its own independent roll. A wrapped flavor prompt shows
        // first (DaggerfallListPickerWindow's own native layout has no room for freeform text) before
        // the actual picker opens.
        // internal static (not private/instance) - uses no instance state, and
        // NarrativeFeedSceneEngine falls back to this directly if a scene can't start (e.g. no live
        // VoxsVampirismEffect found, which "shouldn't happen" here but is checked defensively
        // anyway, matching this file's own established convention).
        internal static void ShowFeedMenu(GameObject npcObject, int locationCategory)
        {
            VoxsVampirism.ShowWrappedPromptThenAction(
                "The hunt begins...",
                () => ShowFeedOptionsPicker(npcObject, locationCategory));
        }

        static void ShowFeedOptionsPicker(GameObject npcObject, int locationCategory)
        {
            DaggerfallListPickerWindow picker = new TitledListPickerWindow(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, "Hunt");
            // Alphabetical (Charm, Feast, Stalk) - Cancel stays pinned last regardless, matching the
            // near-universal "dismiss/back out" convention rather than sorting it in where it would
            // otherwise land first ("Cancel" < "Charm" alphabetically). Mesmerize Rework spec: the
            // "Mesmerize" entry that used to live here is gone - Mesmerize is a self-cast spell now
            // (see MesmerizeEffect.cs/MesmerizeGazeWatcher.cs), reached by casting it and then
            // activating a townsperson directly, never through this menu.
            picker.ListBox.AddItem("Charm");
            picker.ListBox.AddItem("Feast");
            if (locationCategory != LocationStreets)
                picker.ListBox.AddItem("Stalk");
            picker.ListBox.AddItem("Cancel");

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();

                // The menu itself is always reachable now, even on cooldown (see FeedOnNpc) - but a
                // real feed-tier option can't be selected twice in the same day, so the gate moved
                // here, per-option, instead of blocking the whole menu from opening.
                bool isFeedOption = itemString == "Charm" || itemString == "Stalk" || itemString == "Feast";
                if (isFeedOption && IsOnFeedCooldown(npcObject))
                {
                    DaggerfallUI.AddHUDText("They've already had enough of you for one day.");
                    return;
                }

                switch (itemString)
                {
                    case "Charm":
                        StampFeedCooldown(npcObject);
                        DoCharm(npcObject, locationCategory);
                        break;
                    case "Stalk":
                        StampFeedCooldown(npcObject);
                        DoStealth(npcObject, locationCategory);
                        break;
                    case "Feast":
                        StampFeedCooldown(npcObject);
                        DoFeast(npcObject, locationCategory);
                        break;
                    // Cancel (or anything else): no state changes at all, and no roleplay popup -
                    // nothing actually happened.
                }
            };

            DaggerfallUI.UIManager.PushWindow(picker);
        }

        // Etiquette-based social manipulation, available both indoors and outdoors. Never calls
        // guards - win or lose, charming (or failing to charm) someone doesn't visibly alarm anyone
        // watching the way a struggle would. Rolled TWICE, taking the better result (Mesmerize's
        // §1c "advantage" bonus) - implemented as two independent Etiquette rolls, succeeding if
        // EITHER passes, which is mathematically the same "advantage" shape a percentile
        // roll-under-skill check gets from rolling twice and taking the lower/better roll. Left
        // unconditional (not gated on "has Mesmerize been cast recently") since Mesmerize is granted
        // to every vampire unconditionally by the book - gating on "knows Mesmerize" would be a no-op
        // check that's always true in practice, so there was nothing to gate against.
        static void DoCharm(GameObject npcObject, int locationCategory)
        {
            PlayerEntity player = GameManager.Instance.PlayerEntity;
            int etiquetteSkill = player.Skills.GetLiveSkillValue(DFCareer.Skills.Etiquette);
            bool success = Dice100.SuccessRoll(etiquetteSkill) || Dice100.SuccessRoll(etiquetteSkill);

            if (success)
                FeedOnAwakeNpc(npcObject, "Your gaze holds them a moment longer than any mortal charm could - you charm your prey into offering their throat.");
            else
            {
                DaggerfallUI.AddHUDText("Your charms fail to work this time.");
                TryTriggerFightsBack(npcObject, guaranteed: false);
            }

            ShowRoleplayMessage(GetCharmDescription(locationCategory, success));
        }

        // Only ever reachable indoors (excluded from the outdoor menu entirely) - the original
        // sneak-feed Stealth roll, unchanged: quiet on success, guards on failure.
        static void DoStealth(GameObject npcObject, int locationCategory)
        {
            PlayerEntity player = GameManager.Instance.PlayerEntity;
            int stealthSkill = player.Skills.GetLiveSkillValue(DFCareer.Skills.Stealth);
            bool success = Dice100.SuccessRoll(stealthSkill);

            if (success)
            {
                FeedOnAwakeNpc(npcObject, "You feed quietly while they sleep, unnoticed.");
            }
            else
            {
                DaggerfallUI.AddHUDText("Your victim wakes and cries out!");
                VoxsVampirism.ReportVampiricAssaultCrime();
                TryTriggerFightsBack(npcObject, guaranteed: false);
            }

            ShowRoleplayMessage(GetStealthDescription(locationCategory, success));
        }

        // Deliberately kills the victim in exchange for a full blood refill (VoxsVampirismEffect.
        // DrinkDry(), not the usual +3-blood Feed()) - only on a successful Hand-to-Hand roll; a
        // failed roll is a normal failed attempt. Outdoors, there's no hiding this, so guards are
        // called unconditionally regardless of outcome - unchanged from before. Indoors, a failed
        // roll now ALWAYS triggers the fights-back event (guaranteed, no chance roll - a botched
        // feast means the victim fights for their life every time) INSTEAD of the old "guards called
        // on failure" consequence - the victim fights back rather than screaming for help, so calling
        // guards on top would double-punish the same failed attempt. Outdoors, guards were already
        // called unconditionally above before the roll even happened, so the fights-back event on
        // top of that is additive, not a replacement - "outdoors both can occur," per the spec's own
        // framing. Never subject to the low-blood willpower check in FeedOnAwakeNpc - this option
        // already always kills on success regardless of blood level, so there's nothing left for
        // that roll to decide.
        static void DoFeast(GameObject npcObject, int locationCategory)
        {
            bool safeIndoor = locationCategory != LocationStreets;
            if (!safeIndoor)
                VoxsVampirism.ReportVampiricAssaultCrime();

            PlayerEntity player = GameManager.Instance.PlayerEntity;
            int handToHandSkill = player.Skills.GetLiveSkillValue(DFCareer.Skills.HandToHand);
            bool success = Dice100.SuccessRoll(handToHandSkill);

            if (success)
            {
                FeastOnNpc(npcObject);
            }
            else
            {
                DaggerfallUI.AddHUDText(safeIndoor
                    ? "You lunge for the kill, but they're stronger than they looked - and angrier."
                    : "You fail to subdue your victim!");
                TryTriggerFightsBack(npcObject, guaranteed: true);
            }

            ShowRoleplayMessage(GetFeastDescription(locationCategory, success));
        }

        static void ShowRoleplayMessage(string text)
        {
            // wrapText:true is required here - it defaults to false, which left these paragraph-
            // length descriptions rendering as one unbroken line stretching off both edges of the
            // screen instead of wrapping within the message box.
            DaggerfallMessageBox box = new DaggerfallMessageBox(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, wrapText: true);
            box.ClickAnywhereToClose = true;
            box.SetText(text);
            box.Show();
        }

        static string GetCharmDescription(int locationCategory, bool success)
        {
            switch (locationCategory)
            {
                case LocationResidence:
                    return success
                        ? "With practiced charm and a few honeyed words, you coax your host into lowering their guard entirely. They tilt their head back, almost dreamily, and you feed at your leisure in the quiet of their own home, gone before they've fully registered what happened."
                        : "Your attempt at charm falls flat here - something in your manner rings hollow, and your host's smile stiffens into wary silence. You back off before it turns into anything worse, the moment lost.";
                case LocationInn:
                    return success
                        ? "Amid the noise and firelight of the common room, you lean in close and spin a tale that has your mark hanging on every word. Lost in your voice, they scarcely notice the brief, cold kiss at their throat before you slip back into the crowd."
                        : "Your words don't land the way you intended - your mark's eyes narrow instead of soften, and the easy warmth of the tavern turns suddenly cold around you. You let the moment pass, unnoticed by anyone but them.";
                default: // LocationStreets
                    return success
                        ? "You catch your mark's eye in passing and strike up a conversation so effortless they never think to question why they've stopped walking, or why they feel so strangely at ease as you draw close."
                        : "Your charm doesn't take here - out in the open, with too many distractions and too little time, your mark simply shrugs off the attempt and continues on their way, oblivious to how close they came.";
            }
        }

        static string GetStealthDescription(int locationCategory, bool success)
        {
            switch (locationCategory)
            {
                case LocationInn:
                    return success
                        ? "The inn is dark and quiet at this hour. You ease open a door left carelessly unlatched, find its occupant sound asleep, and feed unhurried before slipping back into the corridor as though you were never there."
                        : "You misjudge the creak of a floorboard, or perhaps the depth of their sleep - whatever the cause, your victim wakes with a start and a cry loud enough to carry through the thin inn walls.";
                default: // LocationResidence (this option never reaches LocationStreets - excluded from that menu entirely)
                    return success
                        ? "You slip through the house without so much as a floorboard's creak, find your host deep in sleep, and feed slowly and completely before withdrawing into the dark, leaving no more trace than a dream."
                        : "You've barely leaned over the bed when your host's eyes snap open - some instinct or restless dream has robbed you of the moment, and they bolt upright with a shout before you can react.";
            }
        }

        static string GetFeastDescription(int locationCategory, bool success)
        {
            switch (locationCategory)
            {
                case LocationResidence:
                    return success
                        ? "You take hold of your host with total, merciless intent, and this time you don't stop - not until there's nothing left to take. Their body slumps in your arms, drained utterly, and the bloodlust that drove you finally, briefly, goes quiet."
                        : "You lunge for your host meaning to drain them completely, but they slip free of your grasp before the killing feed can begin, crying out in fear and pain.";
                case LocationInn:
                    return success
                        ? "You corner your mark somewhere the inn's other guests won't see, and this time hold nothing back - by the time you're done, there's nothing left of them to save."
                        : "You go for the kill, but your mark fights harder than expected and breaks free before you can finish what you started, their scream carrying through the inn.";
                default: // LocationStreets
                    return success
                        ? "Right there on the street, with no attempt at concealment, you drain your mark completely - a killing so open and total there will be no mistaking what happened here, or who did it."
                        : "You try to take everything from your mark right there in the open, but they wrench free before the kill is complete, and their scream draws every eye on the street.";
            }
        }

        // internal static (not private/instance) - uses no instance state, and Narrative Feeding
        // Scenes (Scripts/Scenes/NarrativeFeedSceneEngine.cs) calls this directly for its own
        // bloodlust-converted and deliberate-Feast resolutions.
        internal static void FeastOnNpc(GameObject npcObject)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You drink your victim dry.");
                return;
            }

            vampirism.DrinkDry();
            DaggerfallUI.AddHUDText(string.Format("You drink your victim dry. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));

            VoxsVampirism.Log("SneakFeed: Feast - killing '{0}'.", npcObject.name);
            KillNpcAndLeaveCorpse(npcObject);
        }

        // Shared final step once a feed on an awake townsperson has actually succeeded via Charm/
        // Stealth (Feast goes through FeastOnNpc instead, which already always kills on success).
        // Checks blood level BEFORE the actual feed call, so "low blood" reflects the starving state
        // the player was actually in at the moment of feeding, not the post-feed result - and so the
        // Willpower roll below can be resolved BEFORE deciding whether the -1 Humanity murder penalty
        // applies on top of the normal (Humanity-neutral) Feed().
        // Generic forced-kill line every deliberate feed (Charm/Stealth) shares - a frenzy-triggered
        // feed passes its own distinct forcedKillMessage instead (see TryFrenzyFeed), so losing
        // control involuntarily never reads identically to a deliberate feed that happened to go too
        // far.
        const string defaultForcedKillMessage = "The bloodlust takes hold - you drain your victim dry!";

        // internal (not private) + two new optional suppress flags: Narrative Feeding Scenes (see
        // Scripts/Scenes/NarrativeFeedSceneEngine.cs) already resolves its own equivalent of the
        // low-blood forced-kill risk up front, once per scene, at beat 0 ("hunger's grip") rather
        // than hidden here after the fact - and relocates the vampire-reveal-on-success roll into
        // its own complication-only trigger. Both flags default false/off so every flat-menu caller
        // (Charm/Stealth here, TryFrenzyFeed) keeps this method's original behavior byte-for-byte.
        internal static void FeedOnAwakeNpc(GameObject npcObject, string successMessage, string forcedKillMessage = defaultForcedKillMessage,
            bool suppressLowBloodCheck = false, bool suppressVampireReveal = false)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText(successMessage);
                return;
            }

            bool wasLowBlood = !suppressLowBloodCheck && vampirism.IsBloodLow();
            bool forcedKill = false;

            if (wasLowBlood)
            {
                // At 2 blood or fewer the bloodlust is close to total - each feed on an awake
                // townsperson risks draining them to death. The player's live Willpower is their
                // percent chance to resist that urge and hold back, same direct-stat-as-percent
                // convention as the Etiquette/Stealth rolls above.
                int willpower = GameManager.Instance.PlayerEntity.Stats.LiveWillpower;
                if (Dice100.SuccessRoll(willpower))
                    VoxsVampirism.Log("SneakFeed: low-blood kill roll resisted (Willpower {0}).", willpower);
                else
                {
                    VoxsVampirism.Log("SneakFeed: low-blood kill roll failed (Willpower {0}) - killing '{1}'.", willpower, npcObject.name);
                    forcedKill = true;
                }
            }

            vampirism.Feed();
            if (forcedKill)
                vampirism.ChangeHumanity(-1);

            DaggerfallUI.AddHUDText(string.Format("{0} (Blood: {1}/10.)", successMessage, vampirism.GetBloodPoints()));

            if (forcedKill)
            {
                DaggerfallUI.AddHUDText(forcedKillMessage);
                KillNpcAndLeaveCorpse(npcObject);
                return;
            }

            // A successful Charm/Stealth feed can rarely reveal the "townsperson" wasn't mortal at
            // all - never checked on the forced-kill path above, since the victim is already dead and
            // replaced by then, nothing left to reveal.
            if (!suppressVampireReveal)
                TryTriggerVampireRevealOnSuccess(npcObject);
        }

        // Spawns a human corpse where the victim stood, then hides the original NPC - vanilla
        // townspeople have no health/death state to trigger on their own, so this fakes the same end
        // result (DaggerfallLoot's CorpseMarker path) directly rather than just despawning them with
        // nothing left behind. Same technique VoxsNecromancy's own test_corpse console command uses:
        // spawn a throwaway Knight purely to harvest a fully-initialized EnemyEntity (stats/
        // MobileEnemy data/corpse texture) for CreateLootableCorpseMarker, then deactivate it -
        // bypassing the real death pipeline entirely rather than trying to kill the spawned enemy
        // and waiting for it to produce a corpse on its own. The spawned Knight template's own
        // equipment/gold is never transferred - a slain villager shouldn't suddenly be carrying
        // knight gear - and gold is added separately below to match vanilla's own
        // pickpocket-a-townsperson odds instead.
        internal static void KillNpcAndLeaveCorpse(GameObject npcObject)
        {
            Vector3 position = npcObject.transform.position;

            GameObject[] spawned = GameObjectHelper.CreateFoeGameObjects(position, MobileTypes.Knight, 1);
            if (spawned == null || spawned.Length == 0)
            {
                VoxsVampirism.Log("SneakFeed: forced kill - failed to spawn corpse template, falling back to despawn only.");
                npcObject.SetActive(false);
                return;
            }

            GameObject enemyGameObject = spawned[0];
            DaggerfallEntityBehaviour behaviour = enemyGameObject.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity == null)
            {
                VoxsVampirism.Log("SneakFeed: forced kill - spawned corpse template had no EnemyEntity, falling back to despawn only.");
                GameObject.Destroy(enemyGameObject);
                npcObject.SetActive(false);
                return;
            }

            MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;
            int corpseTexture = (mobileEnemy.Gender == MobileGender.Female && mobileEnemy.FemaleCorpseTexture != 0)
                ? mobileEnemy.FemaleCorpseTexture
                : mobileEnemy.CorpseTexture;

            DaggerfallLoot loot = GameObjectHelper.CreateLootableCorpseMarker(
                GameManager.Instance.PlayerObject,
                enemyGameObject,
                enemyEntity,
                corpseTexture,
                DaggerfallUnity.NextUID);

            // Marks this as a genuine murdered townsperson (Blood Economy Rebalance, §1) so the
            // corpse dispatch routes it to DoDrainHumanCorpse's flat tier rather than the HP/divisor
            // formula every wild monster/enemy corpse now uses - this path bypasses EnemyDeath.
            // CompleteDeath() entirely (see this method's own header), so it never gets a
            // CorpseBloodInfoMarker from HandleAnyEnemyDeathForBloodInfo either way.
            if (loot != null)
                loot.gameObject.AddComponent<TownspersonCorpseMarker>();

            // Matches vanilla's own PlayerActivate.Pickpocket() gold-find odds for a townsperson
            // (33% chance of 1-6 gold pieces, otherwise nothing) - real pickpocketing of townspeople
            // never awards items either, only gold, so that's all this corpse carries too.
            int goldFound = 0;
            if (loot != null && Dice100.FailedRoll(33))
            {
                goldFound = Random.Range(0, 6) + 1;
                loot.Items.AddItem(ItemBuilder.CreateGoldPieces(goldFound));
            }

            enemyGameObject.SetActive(false);
            npcObject.SetActive(false);

            VoxsVampirism.Log("SneakFeed: forced kill - left corpse (created={0}, gold={1}) in place of '{2}'.", loot != null, goldFound, npcObject.name);
        }

        // Temple frenzy: the massacre (Werewolf Reveals update, §4). Kills every StaticNPC currently
        // present in the interior the player is standing in, via the exact same corpse-swap machinery
        // above - one shared implementation, applied per NPC, nothing reimplemented. Skips any NPC
        // still bound to a live quest resource (QuestResourceBehaviour) - confirmed safe either way
        // (quest state lives on the Person resource inside QuestMachine, never on the StaticNPC
        // GameObject itself, and DFU tears down and freshly rebuilds an entire interior - StaticNPCs
        // included - on every exit/re-entry regardless, so nothing here is more than a same-visit
        // illusion), but skipping quest-bound NPCs anyway avoids the one real annoyance: a questgiver
        // vanishing mid-interaction until the player leaves and comes back.
        internal static int MassacreCurrentInterior()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (!playerEnterExit.IsPlayerInside || playerEnterExit.Interior == null)
                return 0;

            StaticNPC[] npcs = playerEnterExit.Interior.GetComponentsInChildren<StaticNPC>();
            int killed = 0;
            for (int i = 0; i < npcs.Length; i++)
            {
                StaticNPC npc = npcs[i];
                if (npc == null || !npc.gameObject.activeInHierarchy)
                    continue;
                if (npc.GetComponent<QuestResourceBehaviour>() != null)
                    continue;

                KillNpcAndLeaveCorpse(npc.gameObject);
                killed++;
            }

            VoxsVampirism.Log("SneakFeed: temple massacre - killed {0} NPC(s) in current interior.", killed);
            return killed;
        }

        // Human corpse: grants blood like a normal Feed() (+3) but ALSO costs 1 Humanity, kept at
        // murder-tier (Blood Point spec, B1) - draining a corpse is not the same act as feeding on
        // someone alive, and a player shouldn't be able to kill someone by another means and then
        // drain the corpse "for free."
        void DoDrainHumanCorpse()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You drain the last blood from the corpse.");
                return;
            }

            vampirism.Feed();
            vampirism.ChangeHumanity(-1);
            DaggerfallUI.AddHUDText(string.Format("You drain the last blood from the corpse. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));
        }

        // Fallback tier (Blood Economy Rebalance, §1) for a corpse with no CorpseBloodInfoMarker at
        // all - a pre-mod save or a third-party mod's own death path that bypassed
        // HandleAnyEnemyDeathForBloodInfo. Flat +1, same as a live passive feed, Humanity-neutral.
        void DoDrainAnimalCorpse()
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You drain the last blood from the corpse.");
                return;
            }

            vampirism.FeedOnAnimal();
            DaggerfallUI.AddHUDText(string.Format("You drain the last blood from the corpse. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));
        }

        // Wild monster/enemy corpse (Blood Economy Rebalance, §1): Max(1, maxHealth / divisor) blood,
        // Humanity-neutral, read live from the "Corpse Blood Divisor" Mod Setting.
        void DoDrainWildCorpse(int maxHealth)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You drain the last blood from the corpse.");
                return;
            }

            int divisor = Mathf.Max(1, VoxsVampirism.CorpseBloodDivisor);
            int amount = Mathf.Max(1, maxHealth / divisor);
            vampirism.GainBloodNoHumanity(amount);
            DaggerfallUI.AddHUDText(string.Format("You drain the last blood from the corpse. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));
            VoxsVampirism.Log("SneakFeed: wild corpse drain - maxHealth={0}, divisor={1}, gained={2}.", maxHealth, divisor, amount);
        }

        // Passive/non-hostile monster or enemy class - always treated as the "animal" tier (+2 blood,
        // Humanity-neutral) regardless of whether the specific target happens to be
        // EntityTypes.EnemyMonster or EnemyClass; the spec's own examples describe this whole
        // instant-feed category collectively, in contrast to the deliberate menu-driven interactions
        // with awake townspeople.
        //
        // Gated on the same 24h feed cooldown (IsOnFeedCooldown/StampFeedCooldown) every other feed
        // path in this mod uses - this "free instant feed" tier used to be the one exception with no
        // cooldown at all, which meant standing next to a single passive/pacified creature and
        // spamming the Sneak Feed key refilled blood indefinitely (explicit report). Uses the target's
        // own GameObject as the cooldown key, same as a townsperson - a wild animal that wanders off
        // and a different one that wanders into the same spot are different GameObjects regardless, so
        // this never wrongly blocks a genuinely new target.
        void DoFeedOnPassiveAnimal(GameObject targetObject)
        {
            if (IsOnFeedCooldown(targetObject))
            {
                DaggerfallUI.AddHUDText("It has nothing left to give you today.");
                return;
            }

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You feed on the helpless creature.");
                return;
            }

            vampirism.FeedOnAnimal();
            StampFeedCooldown(targetObject);
            DaggerfallUI.AddHUDText(string.Format("You feed on the helpless creature. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));
        }

        #region Victim fights back (Humanity spec extension, item 2)

        // 90% mortal (Thief/Knight/Mage) / 10% vampire (of which SpawnFightsBackEnemy's own 20%
        // ancient-within-vampire roll further splits into 8% vampire / 2% ancient overall) - one roll
        // for the vampire-or-not split, a second nested roll for ancient-or-not. Defaults, flagged as
        // adjustable per the spec's own request.
        const int fightsBackVampirePercent = 10;
        const int fightsBackRevealPercent = 1;   // flat chance a SUCCESSFUL Charm/Stealth feed reveals a vampire/ancient instead
        const int fledReturnDelayMinutes = 1440; // 24 game-hours, same cadence as the per-NPC feed cooldown elsewhere in this file

        static readonly MobileTypes[] fightsBackMortalTypes = { MobileTypes.Thief, MobileTypes.Knight, MobileTypes.Mage };
        static readonly MobileTypes[] daedraTypes = { MobileTypes.Daedroth, MobileTypes.FireDaedra, MobileTypes.FrostDaedra, MobileTypes.DaedraSeducer };
        static readonly List<GameObject> pendingFledReturns = new List<GameObject>();

        // Reveal handoff kinds - plain string constants, no enum type, matching this mod's own hard
        // rule. Passed to SpawnFightsBackEnemy to select which spawn table/messages/markers apply.
        internal const string RevealKindMortal = "mortal";
        internal const string RevealKindVampire = "vampire";
        internal const string RevealKindWerewolf = "werewolf";
        internal const string RevealKindDaedra = "daedra";

        // Failed Stealth/Charm feed - chance scales with low Humanity (the beast draws worse
        // attention): (10 - Humanity) * 2.5 percent, 0% at Humanity 10 up to 25% at Humanity 0.
        // Failed Feast passes guaranteed=true instead - always triggers, no roll, per the spec's own
        // "a botched feast means the victim fights every time." Static (no instance state used by this
        // method or SpawnFightsBackEnemy below) so other static classes (NarrativeFeedSceneEngine
        // included) can call it directly without needing a SneakFeedController reference.
        public static void TryTriggerFightsBack(GameObject npcObject, bool guaranteed)
        {
            if (!guaranteed)
            {
                int humanity = VoxsVampirism.GetPlayerHumanity();
                int chance = Mathf.RoundToInt((10 - humanity) * 2.5f);
                if (!Dice100.SuccessRoll(chance))
                    return;
            }

            int roll = Random.Range(1, 101);
            bool isVampireVariant = roll <= fightsBackVampirePercent;

            SpawnFightsBackEnemy(npcObject, isVampireVariant ? RevealKindVampire : RevealKindMortal, trackForReturn: !isVampireVariant);
        }

        // A successful Charm/Stealth feed can, at a small flat rate, reveal that the "townsperson"
        // was a vampire (or something older) all along - the feed already happened normally by the
        // time this is checked (Feed() already applied), this is a twist on top, not instead of it.
        // Never spawns the mortal variants - a mortal who was going to fight back would have done so
        // before the feed succeeded, not after.
        static void TryTriggerVampireRevealOnSuccess(GameObject npcObject)
        {
            if (!Dice100.SuccessRoll(fightsBackRevealPercent))
                return;

            SpawnFightsBackEnemy(npcObject, RevealKindVampire, trackForReturn: false);
        }

        // Generalized reveal handoff - one shared implementation for every "the townsperson was never
        // real" spawn (Werewolf Reveals update): plain mortal fights-back, vampire/vampire-ancient
        // reveal, werewolf/wereboar reveal, and daedra reveal all funnel through here, differing only
        // in which MobileTypes table they draw from and which message narrates the reveal. revealKind
        // is one of the RevealKindXxx string constants above - no enum type, per this mod's own hard
        // rule.
        internal static void SpawnFightsBackEnemy(GameObject npcObject, string revealKind, bool trackForReturn)
        {
            Vector3 position = npcObject.transform.position;
            npcObject.SetActive(false);

            MobileTypes type;
            string revealMessage;
            bool isAncient = false;

            switch (revealKind)
            {
                case RevealKindVampire:
                    // Same ~20% ancient-within-vampire ratio the flat menu's own reveal roll uses.
                    isAncient = Dice100.SuccessRoll(20);
                    type = isAncient ? MobileTypes.VampireAncient : MobileTypes.Vampire;
                    revealMessage = isAncient
                        ? "Something in their eyes was never mortal at all - and whatever it is, it is far, far older than you."
                        : "For one unguarded instant their true face shows through - another predator, wearing a mortal's skin.";
                    break;

                case RevealKindWerewolf:
                    type = Random.value < 0.5f ? MobileTypes.Werewolf : MobileTypes.Wereboar;
                    revealMessage = "The disguise sheds like a coat gone too small - claws lengthening, jaw reshaping itself around a howl still finding its pitch.";
                    break;

                case RevealKindDaedra:
                    type = ResolveDaedraType();
                    revealMessage = "The mortal shape burns away like paper catching flame, and what stands in the ash was never mortal at all.";
                    break;

                default: // RevealKindMortal
                    type = fightsBackMortalTypes[Random.Range(0, fightsBackMortalTypes.Length)];
                    revealMessage = "They were never the easy mark they looked like - they were ready for you.";
                    break;
            }

            GameObject enemy = GameObjectHelper.CreateEnemy(type.ToString(), type, position, MobileGender.Unspecified, null, MobileReactions.Hostile);

            if (revealKind == RevealKindVampire)
            {
                FightsBackVariantMarker variantMarker = enemy.AddComponent<FightsBackVariantMarker>();
                variantMarker.IsAncient = isAncient;
                // The original "townsperson" was never real - stays hidden permanently, no return
                // tracking. Mesmerize also can't de-escalate this one (see DeEscalatableMarker/
                // MesmerizeEffect's own targeting rule) - no marker added here at all.
            }
            else
            {
                // Werewolf/daedra reveals reuse the mortal fights-back's own DeEscalatableMarker
                // tagging for pattern consistency, but it's flavor-only bookkeeping (IsFightsBackSpawn
                // is no longer read anywhere - see DeEscalatableMarker's own header). The real
                // eligibility gate for de-escalation is MesmerizeEffect's own EntityType/EnemyGroup
                // check: Humanoid-group werewolves/wereboars pass it structurally, Daedra-group
                // daedra fail it structurally - nothing here decides that either way.
                DeEscalatableMarker deEscalatable = enemy.AddComponent<DeEscalatableMarker>();
                deEscalatable.IsFightsBackSpawn = revealKind == RevealKindMortal;

                // Only the plain mortal variant was ever a real townsperson who might return -
                // werewolf/daedra reveals, like the vampire reveal, were never real to begin with.
                if (revealKind == RevealKindMortal && trackForReturn)
                    TrackNpcForReturn(npcObject, fledReturnDelayMinutes);
            }

            DaggerfallUI.AddHUDText(revealMessage);
            VoxsVampirism.Log("Fights-back triggered: revealKind={0}, type={1}.", revealKind, type);
        }

        // Daedra reveal spawn table (Werewolf Reveals update, §1b): uniform among the four "lesser"
        // daedra, with a small rare chance of the Daedra Lord - mirroring how the vampire reveal keeps
        // its own rare Ancient tier.
        const int daedraLordPercent = 10;

        static MobileTypes ResolveDaedraType()
        {
            if (Dice100.SuccessRoll(daedraLordPercent))
                return MobileTypes.DaedraLord;

            return daedraTypes[Random.Range(0, daedraTypes.Length)];
        }

        // Hides npcObject and schedules it to reappear after delayMinutes (see CheckFledReturns) -
        // extracted from SpawnFightsBackEnemy's own mortal-variant path so Narrative Feeding Scenes'
        // hunter_arrives handler (Scripts/Scenes/NarrativeFeedSceneEngine.cs) can reuse the exact
        // same "the original victim flees, returning later" mechanic for its own hidden victim,
        // without duplicating the private pendingFledReturns list-mutation logic across files.
        internal static void TrackNpcForReturn(GameObject npcObject, uint delayMinutes)
        {
            FightsBackFledMarker fled = npcObject.AddComponent<FightsBackFledMarker>();
            fled.ReturnTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime() + delayMinutes;
            pendingFledReturns.Add(npcObject);
        }

        // Tags the resulting corpse loot the moment a fights-back vampire/ancient actually dies, via
        // the base game's own EnemyDeath.OnEnemyDeath - the only reliable hook for this, since
        // DaggerfallLoot itself retains no MobileTypes/EnemyGroup classification once created, so
        // there's no way to recover "was this a rival vampire" after the fact any other way.
        // CorpseLootContainer is set by EnemyDeath.CompleteDeath() before it raises this same event,
        // so it's already populated by the time this handler runs.
        static void HandleFightsBackEnemyDeath(object sender, System.EventArgs e)
        {
            EnemyDeath death = sender as EnemyDeath;
            if (death == null)
                return;

            FightsBackVariantMarker variantMarker = death.GetComponent<FightsBackVariantMarker>();
            if (variantMarker == null)
                return;

            DaggerfallEntityBehaviour behaviour = death.GetComponent<DaggerfallEntityBehaviour>();
            if (behaviour == null || behaviour.CorpseLootContainer == null)
                return;

            FightsBackCorpseMarker corpseMarker = behaviour.CorpseLootContainer.gameObject.AddComponent<FightsBackCorpseMarker>();
            corpseMarker.IsAncient = variantMarker.IsAncient;
        }

        // Stamps MaxHealth/bloodless-classification onto EVERY enemy's corpse the moment it dies -
        // separate from HandleFightsBackEnemyDeath above (which only tags this mod's own scripted
        // fights-back vampires) since this runs unconditionally for any enemy, wild or otherwise.
        // Bloodless = genuine Undead (DFCareer.EnemyGroups, the same classification DominateEffect/
        // ScentOfPreyEffect already use) or Golem-affinity (all four atronachs, uniformly - Blood
        // Economy Rebalance §1 flags Flesh Atronach as a judgment call and this treats it the same as
        // its Iron/Fire/Ice siblings). Vampire/VampireAncient are carved OUT of the Undead bucket -
        // they keep blood, flagged IsVampireSpecies instead so the corpse dispatch routes them to the
        // same full-refill treatment as a scripted fights-back kill, not the bloodless exclusion.
        static void HandleAnyEnemyDeathForBloodInfo(object sender, System.EventArgs e)
        {
            EnemyDeath death = sender as EnemyDeath;
            if (death == null)
                return;

            DaggerfallEntityBehaviour behaviour = death.GetComponent<DaggerfallEntityBehaviour>();
            if (behaviour == null || behaviour.CorpseLootContainer == null)
                return;

            EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
            if (enemyEntity == null)
                return;

            bool isVampireSpecies = enemyEntity.MobileEnemy.ID == (int)MobileTypes.Vampire
                || enemyEntity.MobileEnemy.ID == (int)MobileTypes.VampireAncient;

            // Same EntityType-aware guard DominateEffect/ScentOfPreyEffect already use - GetEnemyGroup()
            // switches on CareerIndex, which for a class-based human enemy (EntityTypes.EnemyClass)
            // means something else entirely and can collide with a real MonsterCareers.Undead value,
            // so it's only trustworthy for genuine EntityTypes.EnemyMonster targets.
            // Daedra ruling (Werewolf Reveals update, §1b): daedra are bloodless too - whatever runs
            // in a Daedroth/Fire or Frost Daedra/Daedra Seducer/Daedra Lord, it isn't blood a vampire
            // can use. Applies mod-wide (any daedra corpse, not just a reveal_daedra spawn).
            bool isUndeadOrDaedraGroup = behaviour.EntityType == EntityTypes.EnemyMonster
                && (enemyEntity.GetEnemyGroup() == DFCareer.EnemyGroups.Undead || enemyEntity.GetEnemyGroup() == DFCareer.EnemyGroups.Daedra);
            bool isBloodless = !isVampireSpecies && (isUndeadOrDaedraGroup || enemyEntity.MobileEnemy.Affinity == MobileAffinity.Golem);

            CorpseBloodInfoMarker marker = behaviour.CorpseLootContainer.gameObject.AddComponent<CorpseBloodInfoMarker>();
            marker.MaxHealth = enemyEntity.MaxHealth;
            marker.IsBloodless = isBloodless;
            marker.IsVampireSpecies = isVampireSpecies;

            VoxsVampirism.Log("CorpseBloodInfo: '{0}' died - MaxHealth={1}, IsBloodless={2}, IsVampireSpecies={3}.",
                behaviour.name, marker.MaxHealth, marker.IsBloodless, marker.IsVampireSpecies);
        }

        // Devour Thrall (DevourThrallEffect.cs) tags its own victim with DevourThrallMarker the instant
        // before killing it - translated onto the resulting corpse here (as SneakFeedDrainedMarker, the
        // exact marker a normal sneak-feed corpse drain already adds) via the same "tag the live entity
        // before it dies, translate the tag onto the corpse once it does" idiom HandleFightsBackEnemyDeath
        // already established for FightsBackVariantMarker/FightsBackCorpseMarker above - CorpseLootContainer
        // only exists once EnemyDeath.CompleteDeath() has actually run, so there's no way to mark the
        // corpse directly from inside the spell's own MagicRound().
        static void HandleDevourThrallCorpseMarking(object sender, System.EventArgs e)
        {
            EnemyDeath death = sender as EnemyDeath;
            if (death == null)
                return;

            if (death.GetComponent<DevourThrallMarker>() == null)
                return;

            DaggerfallEntityBehaviour behaviour = death.GetComponent<DaggerfallEntityBehaviour>();
            if (behaviour == null || behaviour.CorpseLootContainer == null)
                return;

            MarkCorpseDrained(behaviour.CorpseLootContainer);
        }

        // Mortal fights-back victims return later rather than being gone for good, unlike every other
        // "kill" path in this file (Feast, forced low-blood kill), which are permanent - checked once
        // per magic round (roughly once a game-minute) rather than every frame, since a disabled
        // GameObject doesn't run its own Update() to check this itself.
        // Wrapped in try/catch (guarded, not the raw handler) - a subscriber to EntityEffectBroker.
        // OnNewMagicRound that throws uncaught aborts the broker's own round-firing loop before it
        // advances its private lastGameMinute marker, which then re-fires the SAME round every single
        // rendered frame forever (the confirmed root cause of a severe past near-instant-death bug -
        // see NightWalkerSpawner.GuardedReconcile's own header for the full incident). Same hardening
        // already applied to NightWalkerSpawner.Reconcile/ThrallManager.CheckTransformations/
        // DominateTimer.Tick, the other three direct subscribers to this event - this and CheckFrenzy
        // below were the two remaining unguarded ones, found in a full mod audit.
        static void CheckFledReturns()
        {
            try
            {
                CheckFledReturnsCore();
            }
            catch (System.Exception ex)
            {
                VoxsVampirism.Log("SneakFeedController: CheckFledReturns threw ({0}: {1}) - swallowed so EntityEffectBroker's round loop is never aborted (see comment above).",
                    ex.GetType().Name, ex.Message);
            }
        }

        static void CheckFledReturnsCore()
        {
            if (pendingFledReturns.Count == 0)
                return;

            uint now = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            for (int i = pendingFledReturns.Count - 1; i >= 0; i--)
            {
                GameObject npc = pendingFledReturns[i];
                if (npc == null)
                {
                    pendingFledReturns.RemoveAt(i);
                    continue;
                }

                FightsBackFledMarker marker = npc.GetComponent<FightsBackFledMarker>();
                if (marker == null)
                {
                    pendingFledReturns.RemoveAt(i);
                    continue;
                }

                if (now < marker.ReturnTime)
                    continue;

                pendingFledReturns.RemoveAt(i);
                Destroy(marker);
                npc.SetActive(true);
                VoxsVampirism.Log("SneakFeed: fled NPC '{0}' has returned.", npc.name);
            }
        }

        // Rival vampire/ancient corpse: big heal and a full hunger reset like a Feast kill, but
        // deliberately NO Humanity change in either direction - vampire blood is potent but it isn't
        // human, so neither the -15 human-corpse penalty nor any gain applies. Flagged per the spec's
        // own note as an easy-to-change design call, not a firm rule.
        void DoDrainVampireCorpse(bool isAncient)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
            {
                DaggerfallUI.AddHUDText("You drain the last blood from the corpse.");
                return;
            }

            vampirism.DrinkDryNoHumanity();
            DaggerfallUI.AddHUDText(string.Format(
                isAncient
                    ? "You drink something far older than your own bloodline - the strength of it is dizzying. (Blood: {0}/10.)"
                    : "Vampire blood, thick and potent - it isn't human, but it fills you all the same. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));
        }

        #endregion

        #region Thrall menu (Humanity spec extension, item 3c)

        // Create Vampire removed from here entirely - the only door to it now is the rare Post-Feed
        // Offer (NarrativeFeedSceneEngine), which creates and transforms a fresh victim directly
        // rather than converting an existing thrall. An existing thrall can no longer be upgraded to
        // a vampire after the fact.
        void ShowThrallMenu(GameObject thrallObject)
        {
            DaggerfallListPickerWindow picker = new TitledListPickerWindow(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow, "Mesmerized");
            picker.ListBox.AddItem("Feed");
            picker.ListBox.AddItem("Feast");
            picker.ListBox.AddItem("Cancel");

            picker.OnItemPicked += (index, itemString) =>
            {
                picker.CloseWindow();
                switch (itemString)
                {
                    case "Feed":
                        ThrallManager.TryFeedOnThrall(thrallObject);
                        break;
                    case "Feast":
                        DoMurderThrall(thrallObject);
                        break;
                    // Cancel (or anything else): nothing happens.
                }
            };

            DaggerfallUI.UIManager.PushWindow(picker);
        }

        // Full DrinkDry() treatment on the player's own side (full blood refill, 30% heal, the
        // standard -1 Humanity murder penalty) - no special mercy for the victim being the player's
        // own thrall. The thrall's own death is real (SetHealth(0) via ThrallManager.MurderThrall),
        // not the townsperson fake-kill technique, since a thrall is a genuine live EnemyEntity -
        // roster cleanup happens automatically via ThrallManager's own EnemyDeath subscription.
        void DoMurderThrall(GameObject thrallObject)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            vampirism.DrinkDry();
            DaggerfallUI.AddHUDText(string.Format("You drink your own creation dry. (Blood: {0}/10.)",
                vampirism.GetBloodPoints()));

            ThrallManager.MurderThrall(thrallObject);
        }

        #endregion

        #region Frenzy proximity events (blood-empty involuntary feed)

        // Suppression window after a frenzy event actually FIRES (chance roll succeeded) - not after
        // every check, only a triggered one. Without this a low-Humanity, blood-empty player standing
        // in a tavern would get re-rolled every game-minute. Default, flagged as adjustable.
        const int frenzySuppressionMinutes = 240; // 4 in-game hours

        static uint lastFrenzyEventTime;

        // Tracks eligibility state purely so state CHANGES (blood emptying/refilling, toggling the
        // setting, pausing) get one log line each rather than logging every single game-minute check -
        // matches this mod's own established "no per-round logging" convention elsewhere.
        static bool frenzyChecksActive;

        // Wrapped in try/catch for the same reason CheckFledReturns above is - see that method's own
        // comment.
        void CheckFrenzy()
        {
            try
            {
                CheckFrenzyCore();
            }
            catch (System.Exception ex)
            {
                VoxsVampirism.Log("SneakFeedController: CheckFrenzy threw ({0}: {1}) - swallowed so EntityEffectBroker's round loop is never aborted (see CheckFledReturns' own comment).",
                    ex.GetType().Name, ex.Message);
            }
        }

        void CheckFrenzyCore()
        {
            bool eligible = VoxsVampirism.FrenzyEventsEnabled
                && GameManager.Instance.IsPlayerOnHUD
                && !InputManager.Instance.IsPaused
                && GameManager.Instance.PlayerEffectManager.HasVampirism();

            VoxsVampirismEffect vampirism = eligible
                ? GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect
                : null;
            eligible = eligible && vampirism != null && vampirism.IsBloodEmpty();

            if (eligible != frenzyChecksActive)
            {
                frenzyChecksActive = eligible;
                VoxsVampirism.Log("Frenzy checks {0}.", eligible
                    ? "started (blood fully spent)"
                    : "stopped (blood no longer empty, paused, HUD not active, or setting disabled)");
            }

            if (!eligible)
                return;

            // Blocked the same way the keybind handler itself is - never frenzy mid-fight-with-the-
            // watch, and this doubles as the "not mid feed-menu/dialogue/other UI" gate, since
            // IsPlayerOnHUD is already false whenever any window (feed menu included) is open.
            if (AreGuardsPursuing())
                return;

            uint now = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            if (lastFrenzyEventTime != 0 && now - lastFrenzyEventTime < frenzySuppressionMinutes)
                return;

            GameObject target = FindNearestValidFrenzyTarget();
            if (target == null)
                return;

            int humanity = VoxsVampirism.GetPlayerHumanity();
            int chance = (10 - humanity) * 2;
            if (!Dice100.SuccessRoll(chance))
                return;

            lastFrenzyEventTime = now;
            TriggerFrenzyEvent(target);
        }

        // Indoors, scans the current interior's StaticNPCs (the same technique ScentOfPreyEffect's own
        // detection scan uses, since StaticNPCs are structurally outside every other object registry).
        // Outdoors, scans ActiveGameObjectDatabase's civilian mobile registry. Either way, ineligible
        // candidates (on cooldown, out of range) are filtered DURING the scan, not after finding the
        // closest one overall - a thrall standing closer than any valid target never enters either
        // pool in the first place (thralls are EnemyEntity-backed, not StaticNPC/civilian-mobile), so
        // "nearest valid target" can never silently no-op because the true nearest object turned out
        // to be a thrall.
        GameObject FindNearestValidFrenzyTarget()
        {
            Vector3 playerPosition = GameManager.Instance.PlayerObject.transform.position;
            float rangeUnits = Mathf.Max(PlayerActivate.StaticNPCActivationDistance, PlayerActivate.MobileNPCActivationDistance);
            float rangeSq = rangeUnits * rangeUnits;

            GameObject best = null;
            float bestDistSq = float.MaxValue;

            PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
            if (playerEnterExit.IsPlayerInside && playerEnterExit.Interior != null)
            {
                StaticNPC[] indoorNpcs = playerEnterExit.Interior.GetComponentsInChildren<StaticNPC>();
                for (int i = 0; i < indoorNpcs.Length; i++)
                {
                    StaticNPC npc = indoorNpcs[i];
                    if (npc == null || !npc.gameObject.activeInHierarchy)
                        continue;

                    ConsiderFrenzyCandidate(npc.gameObject, playerPosition, rangeSq, ref best, ref bestDistSq);
                }
            }
            else
            {
                foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveCivilianMobileBehaviours())
                {
                    if (behaviour == null)
                        continue;

                    ConsiderFrenzyCandidate(behaviour.gameObject, playerPosition, rangeSq, ref best, ref bestDistSq);
                }
            }

            return best;
        }

        void ConsiderFrenzyCandidate(GameObject npc, Vector3 playerPosition, float rangeSq, ref GameObject best, ref float bestDistSq)
        {
            float distSq = (npc.transform.position - playerPosition).sqrMagnitude;
            if (distSq > rangeSq || distSq >= bestDistSq)
                return;

            SneakFeedCooldownMarker cooldown = npc.GetComponent<SneakFeedCooldownMarker>();
            if (cooldown != null)
            {
                uint elapsed = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime() - cooldown.lastAttemptTime;
                if (elapsed < DaggerfallDateTime.MinutesPerDay)
                    return;
            }

            bestDistSq = distSq;
            best = npc;
        }

        void TriggerFrenzyEvent(GameObject target)
        {
            DaggerfallUI.AddHUDText("Your vision narrows. The smell of them is everything.");

            // Live Willpower - already carries the blood-drain Willpower penalty (ApplyBloodDrain in
            // VoxsVampirismEffect, -10 at exactly 0 blood, which is the only state frenzy can fire
            // from), so the starving beast is deliberately harder to resist here. Same roll
            // convention as the existing low-blood forced-kill check inside FeedOnAwakeNpc.
            int willpower = GameManager.Instance.PlayerEntity.Stats.LiveWillpower;
            bool resisted = Dice100.SuccessRoll(willpower);

            VoxsVampirism.Log("Frenzy event: target='{0}', Willpower={1}, resisted={2}.", target.name, willpower, resisted);

            if (resisted)
            {
                DaggerfallUI.AddHUDText("With an effort, you master it. The moment passes.");
                return;
            }

            // Dark Brotherhood update (§3): the Brotherhood tolerates a guest's hunger (autoSuccess
            // scenes), not a guest's frenzy in their own sanctuary - a loss of control here is
            // answered with steel, not a normal involuntary feed attempt.
            if (GetSceneLocationTag() == SceneLocationDarkBrotherhood)
            {
                TriggerDarkBrotherhoodFrenzyBacklash();
                return;
            }

            TryFrenzyFeed(target);
        }

        // Three hostile Assassin-class enemies, spread in a small circle around the player so they
        // don't spawn fully stacked - same GameObjectHelper.CreateEnemy call NarrativeFeedSceneEngine.
        // RunHunterArrives already uses for its own single-enemy spawn.
        static void TriggerDarkBrotherhoodFrenzyBacklash()
        {
            Vector3 center = GameManager.Instance.PlayerObject.transform.position;
            DaggerfallUI.AddHUDText("The Brotherhood tolerates hunger. It does not tolerate this. Steel answers steel.");

            for (int i = 0; i < 3; i++)
            {
                Vector3 offset = Quaternion.Euler(0, i * 120f, 0) * Vector3.forward * 2f;
                GameObjectHelper.CreateEnemy(MobileTypes.Assassin.ToString(), MobileTypes.Assassin,
                    center + offset, MobileGender.Unspecified, null, MobileReactions.Hostile);
            }

            VoxsVampirism.Log("Frenzy event: fired inside Dark Brotherhood hall - spawned 3 hostile Assassins instead of a feed attempt.");
        }

        // Involuntary version of DoStealth - same roll (Stealth), same success/failure shape (quiet
        // feed through the existing FeedOnAwakeNpc pipeline vs. guards called and a fights-back
        // chance), reused rather than reimplemented so the low-blood forced-kill Willpower check
        // inside FeedOnAwakeNpc, the 1% vampire-reveal-on-success roll, and the fights-back trigger all apply
        // automatically with no special-casing. Only the flavor text is distinct, so a frenzy-feed
        // never reads as identical to a deliberately chosen one. No ShowRoleplayMessage popup here
        // (unlike the player-chosen menu options) - an involuntary event interrupting the player with
        // a modal message box on top of losing control of the moment felt like the wrong kind of
        // intrusive, so this stays to lightweight HUD text only.
        void TryFrenzyFeed(GameObject npcObject)
        {
            StampFeedCooldown(npcObject);

            PlayerEntity player = GameManager.Instance.PlayerEntity;
            int stealthSkill = player.Skills.GetLiveSkillValue(DFCareer.Skills.Stealth);
            bool success = Dice100.SuccessRoll(stealthSkill);

            VoxsVampirism.Log("Frenzy feed: target='{0}', Stealth={1}, success={2}.", npcObject.name, stealthSkill, success);

            if (success)
            {
                // Distinct forced-kill line for a frenzy specifically (Blood Economy Rebalance
                // follow-up) - the beast, not the player, was in control here, so waking to find the
                // victim already dead reads very differently from a deliberate Charm/Stealth that
                // happened to go too far.
                FeedOnAwakeNpc(npcObject, "When you come back to yourself, the taste is already in your mouth.",
                    "There is no controlling it now - by the time your mind catches up to your hands, they are already dead.");
            }
            else
            {
                DaggerfallUI.AddHUDText("You lunge before you mean to - and they see you do it.");
                VoxsVampirism.ReportVampiricAssaultCrime();
                TryTriggerFightsBack(npcObject, guaranteed: false);
            }
        }

        #endregion
    }

    /// <summary>
    /// Featureless marker limiting each corpse to a single drain - added to a corpse's DaggerfallLoot
    /// GameObject the first time it's fed on.
    /// </summary>
    public class SneakFeedDrainedMarker : MonoBehaviour
    {
    }

    /// <summary>
    /// Tracks the classic-minutes timestamp of the last feed attempt (any menu option, success or
    /// failure) against a specific awake NPC - see FeedOnNpc/StampFeedCooldown/ClearCooldownOnRecycle.
    /// </summary>
    public class SneakFeedCooldownMarker : MonoBehaviour
    {
        public uint lastAttemptTime;
    }

    /// <summary>
    /// Attached to a live fights-back vampire/vampire-ancient spawn at creation - read back by
    /// HandleFightsBackEnemyDeath when it dies, to tag the resulting corpse with a matching
    /// FightsBackCorpseMarker (see SneakFeedController's own "victim fights back" region for why this
    /// two-step live-marker-then-corpse-marker handoff is needed at all).
    /// </summary>
    public class FightsBackVariantMarker : MonoBehaviour
    {
        public bool IsAncient;
    }

    /// <summary>
    /// Attached to a fights-back vampire/vampire-ancient's corpse loot once it dies - lets
    /// SneakFeedController.TryFindTarget's corpse branch route a drain attempt to
    /// DoDrainVampireCorpse instead of the normal human/animal corpse handling.
    /// </summary>
    public class FightsBackCorpseMarker : MonoBehaviour
    {
        public bool IsAncient;
    }

    /// <summary>
    /// Attached to a mortal fights-back victim's original (hidden) NPC GameObject - tracks when it
    /// should reappear (SneakFeedController.CheckFledReturns), unlike every other "kill" path in this
    /// file, which are permanent.
    /// </summary>
    public class FightsBackFledMarker : MonoBehaviour
    {
        public uint ReturnTime;
    }

    /// <summary>
    /// Attached to a corpse created by KillNpcAndLeaveCorpse (a murdered townsperson) - lets
    /// SneakFeedController.TryFindTarget's corpse branch route a drain attempt to DoDrainHumanCorpse
    /// (flat +3/-1 Humanity, unchanged) rather than the HP/divisor formula every other corpse now
    /// uses (Blood Economy Rebalance, §1). Needed because isEnemyClass alone can't tell "this mod's
    /// own murdered NPC" apart from a wild EntityTypes.EnemyClass monster (Vampire, Giant, etc.) -
    /// both use a class-type template internally, but only the former should get the human tier.
    /// </summary>
    public class TownspersonCorpseMarker : MonoBehaviour
    {
    }

    /// <summary>
    /// Stamped on EVERY enemy corpse (not just fights-back ones) the moment it dies, via
    /// HandleAnyEnemyDeathForBloodInfo - DaggerfallLoot itself retains no HP/EnemyGroup/Affinity
    /// data once created (confirmed by reading DaggerfallLoot.cs/GameObjectHelper.
    /// CreateLootContainer directly), so this is the only point where that information is still
    /// available to capture. MaxHealth feeds the HP/divisor blood formula; IsBloodless excludes
    /// undead (minus vampires, which carry IsVampireSpecies instead) and golem/atronach-affinity
    /// corpses from draining entirely (Blood Economy Rebalance, §1).
    /// </summary>
    public class CorpseBloodInfoMarker : MonoBehaviour
    {
        public int MaxHealth;
        public bool IsBloodless;
        public bool IsVampireSpecies;
    }
}
