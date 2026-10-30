// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Reworked entirely - no longer a targeted projectile that damages a living creature for a
// percentage of its health. Now a self-target spell that senses every corpse within 10 feet and
// feeds off all of them at once. Corpse detection reuses the exact technique Astrid's Guide to
// Necromancy's own CorpseFinder.cs already established (read directly rather than guessed, since
// that file was built and tested for this same purpose): ActiveGameObjectDatabase.GetActiveLoot()
// enumerates every live DaggerfallLoot in the scene (a real, direct scene registry, not a raycast or
// physics overlap), filtered to loot.ContainerType == LootContainerTypes.CorpseMarker.
//
// Blood Economy Rebalance (§4): per-corpse resolution is now identical to a manual SneakFeedController
// drain of that same corpse - the HP/divisor formula for an ordinary wild corpse, full-refill-to-10
// for a vampire-species/fights-back corpse (dominates any summed gain from other corpses in the same
// pile), flat +3/-1 Humanity for one of this mod's own murdered townspeople, and a silent skip (no
// marker, no visual) for a bloodless corpse mixed into the pile - see MagicRound() for the exact
// per-corpse marker checks, which mirror SneakFeedController.TryFindTarget's own corpse dispatch.
// Costs no blood at all (see SpellCastCostRefunder.cs's own header) - one of two blood-cost-exempt
// spells in the mod now (Devour Thrall is the other - free for the same reason feeding on a thrall
// already is): this is how a vampire ACQUIRES blood, so taxing it would tax the refuel
// itself.
//
// "Cannot be fed on or subject to this spell again" is achieved by reusing SneakFeedController's own
// SneakFeedDrainedMarker - the exact marker a normal sneak-feed corpse drain already adds to a corpse
// after use (see SneakFeedController.TryFindTarget's own corpse branch, which already refuses a
// second attempt against a marked corpse with "This corpse has already been drained."). Reusing that
// same component means the exclusion works BOTH ways for free: a corpse this spell already drained
// can't be sneak-fed on afterward, and a corpse the player already sneak-fed on can't be caught by
// this spell either - one shared marker, no new bookkeeping needed.
//
// All corpses found this cast are folded into a single VoxsVampirismEffect.GainBloodNoHumanity call
// (one combined blood gain, one heal/sound/flash trigger) rather than one call per corpse - "feeds
// off them" reads as one action drawing on several sources at once, not several separate feeds in a
// row, and this also matches how every other multi-target effect in this mod (Vampiric Flames' own
// AoE) only applies its caster-side side effects once per cast, not once per thing it affected.
//
// Per-corpse VISUAL is a separate matter from the once-per-cast feed sound/flash above, and DOES
// play once per corpse - the same "spawn a one-shot animated impact billboard at the corpse's own
// position" technique Astrid's Guide to Necromancy's own Deathknell/CorpseExplosion effects already
// use (GameObjectHelper.CreateDaggerfallBillboardGameObject + DaggerfallMissile's own spell-impact
// texture archives - Deathknell reuses the Poison archive (377) for its own green "life drained
// away" look, Corpse Explosion the Fire archive (375) for its blast). DFU's runtime mod compiler
// can't ship or compile a new texture/shader, so there's no way to author a genuinely new red sprite
// from loose .cs source - this reuses the Fire archive (already the reddest/warmest of the four
// available spell-impact archives: Cold=376, Fire=375, Magic=379, Poison=377) and pushes it further
// toward blood-red with a material color multiply on top, best-effort ("if possible" per the
// request) since it isn't verified whether DFU's own billboard shader honors a material color tint
// the way a standard Unity unlit shader would - if it doesn't, the fire archive alone still reads
// reasonably red/warm on its own.
//
// No real Magicka cost - properties.MagnitudeCosts is zeroed and ChanceSuccess always true, the same
// "avoid the framework's zero-active-component cost fudge" trick VampiricFlames/Mesmerize use, but via
// SupportMagnitude specifically rather than SupportChance (the trick those two use) - this effect key
// already existed before this rework with SupportMagnitude declared, and any player who already
// learned the old version has that shape (MagnitudeBase/Plus/PerLevel, all real nonzero values)
// baked into their own already-saved spell data, which this rework's own Create() never touches or
// migrates (AddSpell only grants NEW spells, never replaces an already-known one by the same Name).
// Switching the declared component to SupportChance instead (this file's first version of this
// rework) left old-format ChancePerLevel unset at 0 for anyone in that position - FormulaHelper.
// CalculateEffectCosts (called every frame the spellbook window is open, for every known spell)
// divides by PerLevel per component and crashed hard on exactly that, confirmed via a real
// DivideByZeroException flood in Player.log the moment the spellbook was opened. Staying on
// SupportMagnitude avoids ever touching that gap, for old AND new grants alike.
//
// Firing timing (investigated re: "doesn't always drain all the nearby corpses"): properties.
// SupportDuration is never set here, so EntityEffect.SetDuration() leaves RoundsRemaining at 0 -
// meaning EntityEffectManager.DoMagicRound() (which only re-invokes an effect whose RoundsRemaining
// is still > 0) never calls this effect's MagicRound() again after the first. The actual, and only,
// call happens synchronously the instant the spell is cast, straight from EntityEffectManager.
// AssignBundle's own "effect is ready, gets initial magic round" line - not delayed to the broker's
// own round timer at all. So this isn't a timing/registration bug: every corpse whose DaggerfallLoot
// is already active in the scene at the moment of casting gets scanned in the same pass, in one
// foreach loop, with no partial/skipped iterations possible. The actual cause of "missed" corpses in
// practice is almost certainly geometric - radiusFeet was a fixed 10 feet (~3 Unity units), which a
// spread-out melee (kiting, a flight of stairs, a corpse that rolled around a corner) easily exceeds
// even when the pile still reads as "nearby" to the player's own eye - now a Mod Settings slider
// ("Vampiric Drain Range", 1-100 feet, default 10) for exactly that reason.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Utility;

namespace VoxsVampirismMod.Effects
{
    public class VampiricDrainEffect : BaseEntityEffect
    {
        public static readonly string EffectKey = "Vampiric-Drain";

        // Read live from Mod Settings (default 10 feet, was a fixed const) each time it's needed,
        // same "picks up a changed value without needing to relearn/recast" pattern ScentOfPreyEffect.
        // DetectRadiusUnits already uses. Feet-to-Unity-units conversion matches every other range
        // setting in this mod - MeshReader.GlobalScale treats one Unity unit as approximately one
        // real-world meter, so this is a reasonable approximation, not exact.
        static float RadiusFeet => VoxsVampirismMod.VoxsVampirism.VampiricDrainRangeFeet;
        static float RadiusUnits => RadiusFeet * 0.3048f;

        // Fire spell-impact archive/record (DaggerfallMissile.cs's own fireMissileArchive) - the
        // reddest/warmest of DFU's four spell-impact billboard archives, reused here the same way
        // Astrid's Guide to Necromancy's Deathknell/CorpseExplosion reuse Poison/Fire respectively
        // for their own corpse-consuming visuals. Tinted further toward red below.
        const int drainVisualArchive = 375;
        const int drainVisualRecord = 1;
        const int drainVisualFramesPerSecond = 15;
        static readonly Color drainVisualTint = new Color(1f, 0.15f, 0.1f);

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            // Mysticism, not Destruction (explicit report: the spell silently did nothing for any
            // character with spell absorption). EntityEffectManager.TryAbsorption only ever absorbs
            // Destruction-school effects (its own comment: "Currently only absorbing Destruction
            // magic... to prevent something as benign as a self-heal from player being blocked") and
            // runs against EVERY incoming effect including the caster's own self-casts - so an
            // Atronach-type absorber ate their own Vampiric Drain on every single cast (career-based
            // absorption always passes its chance check). This is a self-cast utility drain, not an
            // attack - Mysticism (the school necromancy's own corpse magic uses) both fits and steps
            // outside the absorption check entirely. Save-safe: MagicSkill lives on the registered
            // template, re-read every boot - never baked into a player's saved spellbook entry.
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.SupportMagnitude = true;
            properties.DisableReflectiveEnumeration = true;
            properties.MagnitudeCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Always succeeds - this effect has no real chance/roll of its own (see file header for why
        // SupportMagnitude, not SupportChance, is the declared component here).
        public override bool ChanceSuccess
        {
            get { return true; }
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                string.Format("Free. Drains every corpse within {0} feet at once - blood", Mathf.RoundToInt(RadiusFeet)),
                "gained from each scales with its own max health (minimum 1).",
                "Undead, daedra, and golem corpses give nothing. Each corpse",
                "can only answer this call, or a normal feed, once.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (!casterBehaviour)
                return;

            Vector3 casterPosition = casterBehaviour.transform.position;
            float radiusSq = RadiusUnits * RadiusUnits;
            int divisor = Mathf.Max(1, VoxsVampirismMod.VoxsVampirism.CorpseBloodDivisor);

            int consumedCount = 0;
            int pendingGain = 0;
            int humanityDelta = 0;
            bool anyFullRefill = false;

            // Same registry-plus-real-scan union ScentOfPreyEffect/MistFormEffect already use for
            // enemies (full mod audit - this was the one scanner in the mod without it):
            // ActiveGameObjectDatabase only knows about loot registered through DaggerfallLoot.Awake's
            // standard path, and third-party content mods can produce corpses that skip or delay that
            // registration - which silently never drained. FindObjectsOfType is a genuine direct scene
            // scan independent of the registry; only ever run once per cast (this is a one-shot
            // effect), so the per-frame cost that call is normally too slow for doesn't apply.
            HashSet<DaggerfallLoot> lootCandidates = new HashSet<DaggerfallLoot>(ActiveGameObjectDatabase.GetActiveLoot());
            DaggerfallLoot[] allLoot = UnityEngine.Object.FindObjectsOfType<DaggerfallLoot>();
            for (int i = 0; i < allLoot.Length; i++)
                lootCandidates.Add(allLoot[i]);

            foreach (DaggerfallLoot loot in lootCandidates)
            {
                if (loot == null || loot.ContainerType != LootContainerTypes.CorpseMarker)
                    continue;

                if (VoxsVampirismMod.SneakFeedController.IsCorpseDrained(loot))
                    continue;

                if ((loot.transform.position - casterPosition).sqrMagnitude > radiusSq)
                    continue;

                // Per-corpse resolution identical to a manual drain (Blood Economy Rebalance, §4) -
                // same marker checks SneakFeedController.TryFindTarget's corpse branch uses.
                VoxsVampirismMod.FightsBackCorpseMarker vampireCorpse = loot.GetComponent<VoxsVampirismMod.FightsBackCorpseMarker>();
                VoxsVampirismMod.CorpseBloodInfoMarker bloodInfo = loot.GetComponent<VoxsVampirismMod.CorpseBloodInfoMarker>();
                bool isVampireSpecies = vampireCorpse != null || (bloodInfo != null && bloodInfo.IsVampireSpecies);

                if (bloodInfo != null && bloodInfo.IsBloodless && !isVampireSpecies)
                {
                    // Bloodless corpses are skipped silently within a mixed pile, not refused loudly -
                    // no marker added, so a later single-target drain attempt can still try (and get
                    // the same "Nothing flows in this one" refusal) if the player wants to check.
                    continue;
                }

                VoxsVampirismMod.SneakFeedController.MarkCorpseDrained(loot);
                SpawnDrainVisual(loot.transform.position);
                consumedCount++;

                if (isVampireSpecies)
                {
                    anyFullRefill = true;
                }
                else if (loot.GetComponent<VoxsVampirismMod.TownspersonCorpseMarker>() != null)
                {
                    pendingGain += 3;
                    humanityDelta -= 1;
                }
                else if (bloodInfo != null)
                {
                    pendingGain += Mathf.Max(1, bloodInfo.MaxHealth / divisor);
                }
                else
                {
                    pendingGain += 1;
                }
            }

            if (consumedCount == 0)
            {
                DaggerfallUI.AddHUDText("You sense nothing dead close enough to draw from.");
                return;
            }

            DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect vampirism =
                GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect()
                    as DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects.VoxsVampirismEffect;
            if (vampirism == null)
                return;

            if (anyFullRefill)
                vampirism.GainBloodFull();
            else
                vampirism.GainBloodNoHumanity(pendingGain);
            if (humanityDelta != 0)
                vampirism.ChangeHumanity(humanityDelta);

            DaggerfallUI.AddHUDText(string.Format("You draw the last blood from {0} corpse{1} nearby. (Blood: {2}/10.)",
                consumedCount, consumedCount == 1 ? "" : "s", vampirism.GetBloodPoints()));
            VoxsVampirismMod.VoxsVampirism.Log("VampiricDrain: {0} corpses drained, pendingGain={1}, anyFullRefill={2}, humanityDelta={3}, divisor={4}.",
                consumedCount, pendingGain, anyFullRefill, humanityDelta, divisor);
        }

        // Same billboard-spawn shape as DeathknellEffect.SpawnLeechVisual/CorpseExplosionEffect.
        // SpawnExplosionVisual - a one-shot animated impact sprite at the corpse's own position. No
        // sound here deliberately - the feeding sound already plays once for the whole cast via
        // GainBloodNoHumanity above, so a second sound per corpse would just stack/overlap.
        static void SpawnDrainVisual(Vector3 position)
        {
            GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(drainVisualArchive, drainVisualRecord, null);
            go.transform.position = position;

            Billboard billboard = go.GetComponent<Billboard>();
            billboard.FramesPerSecond = drainVisualFramesPerSecond;
            billboard.FaceY = true;
            billboard.OneShot = true;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.receiveShadows = false;
                if (meshRenderer.material != null)
                    meshRenderer.material.color = drainVisualTint;
            }
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                Debug.LogError(VoxsVampirismMod.VoxsVampirism.LogPrefix + "VampiricDrainEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                MagnitudeBaseMin = 1,
                MagnitudeBaseMax = 1,
                MagnitudePlusMin = 1,
                MagnitudePlusMax = 1,
                MagnitudePerLevel = 1,
            };
            EffectEntry effectEntry = new EffectEntry()
            {
                Key = template.Properties.Key,
                Settings = templateSettings,
            };

            return new EffectBundleSettings()
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 22,
                Effects = new EffectEntry[] { effectEntry },
            };
        }
    }
}
