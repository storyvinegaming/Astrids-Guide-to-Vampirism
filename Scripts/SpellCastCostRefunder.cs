// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Blood Economy Rebalance: "Magicka is just the toll the engine insists on collecting, and we hand
// it straight back" - the one shared post-cast hook every vampire spell's Magicka refund runs
// through, rather than each effect file re-implementing its own. EntityEffectManager.SetReadySpell
// (read directly from DFU's own source) computes a spell's real spell-point cost via FormulaHelper.
// CalculateTotalEffectCosts and the engine auto-deducts that amount in CastReadySpell/
// CastNoAnimSpell/PlayerSpellCasting_OnReleaseFrame - entirely independent of anything any effect's
// own code does. That formula also enforces a hard floor of 5 SP (FormulaHelper.cs's own
// castCostFloor) - THIS is the real "engine-forced minimum" every spell in this mod lands on, Dominate
// included (its ChanceCosts/DurationCosts were originally left as real nonzero values, producing a
// caster-level/skill-scaled cost - e.g. 60 SP - well above this floor and inconsistent with every
// other spell's uniform 5; zeroed out in DominateEffect.cs's own SetProperties() to match).
//
// EntityEffectManager.OnCastReadySpell fires exactly once per real cast attempt - for a CasterOnly
// instant spell AND a targeted/AoE missile spell alike - the moment AssignBundle or the missile is
// actually launched, regardless of whether anything ends up struck. That makes it the correct
// universal "the cast fired" hook: this refunds the engine's own auto-charged SP for every one of
// this mod's own spells here, unconditionally, even if the spell's own internal logic then decides
// to refuse/fizzle for its own reasons (a cooldown-blocked Suppress Beast, an invalid-Dominate-target,
// an empty-blast Vampiric Flames) - a fizzled cast shouldn't cost real Magicka either, and recomputing
// the SAME formula SetReadySpell used (rather than assuming a flat 5) means this is correct for
// Dominate's real formula cost too, not just the floor.
//
// Vampiric Flames, Vampiric Ice (Haarvenu's clan-exclusive twin, identical shape), and Dominate are
// the spells whose BLOOD cost also lives here rather than in their own file. Vampiric Flames/Ice:
// neither has a target-eligibility gate at all (ChanceSuccess is always true), and MagicRound() only
// ever runs once per struck target - meaning if the blast hits nobody, no MagicRound ever runs and no
// per-effect code could ever charge blood for that cast. Dominate: originally charged from inside its
// own ChanceSuccess (EvaluateChanceSuccess), but only once past its own target/budget validity checks -
// an invalid target (undead, daedra, already-yours) or a cast the legion budget can't afford was free,
// which read as "did this cast even work?" rather than "did I actually commit blood to try" (explicit
// request: spend at the moment of casting, not gated on any kind of success). Moved here so the cost
// is unconditional the instant the cast fires, before any target validity is even evaluated - the
// same footing Vampiric Flames/Ice already stand on. This hook is the only place guaranteed to fire
// for the "cast at nothing/nothing valid" case, so all three spells' 1-blood cost (via
// VoxsVampirismEffect.PayBloodOrHealth, desperate-casting §5 applies here too) is charged here,
// unconditionally, once per cast. Every other spell keeps its own blood-cost call in its own file,
// past its own eligibility checks, so a legitimate "no valid target" refusal stays free - see each
// effect's own header for exactly where.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using VoxsVampirismMod.Effects;

namespace VoxsVampirismMod
{
    public class SpellCastCostRefunder : MonoBehaviour
    {
        static readonly HashSet<string> ourSpellKeys = new HashSet<string>
        {
            MesmerizeEffect.EffectKey,
            DominateEffect.EffectKey,
            VampiricFlamesEffect.EffectKey,
            VampiricIceEffect.EffectKey,
            MistFormEffect.EffectKey,
            ScentOfPreyEffect.EffectKey,
            HuntersSightEffect.EffectKey,
            QuickenBloodEffect.EffectKey,
            VampiricDrainEffect.EffectKey,
            SuppressBeastEffect.EffectKey,
            BloodSurgeEffect.EffectKey,
            // Incidental fix - CommandBloodCourtEffect was missing from this set entirely, so it never
            // actually got its own engine-charged SP refunded like every other spell in this mod does.
            CommandBloodCourtEffect.EffectKey,
            DevourThrallEffect.EffectKey,
        };

        // Vampiric Flames/Vampiric Ice/Dominate are the only spells whose blood cost is charged from
        // this hook rather than their own file - see file header for exactly why.
        const int unconditionalBloodCost = 1;

        void OnEnable()
        {
            EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
            if (manager != null)
                manager.OnCastReadySpell += HandleCastReadySpell;
        }

        void OnDisable()
        {
            EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
            if (manager != null)
                manager.OnCastReadySpell -= HandleCastReadySpell;
        }

        // Wrapped in try/catch (guarded, not the raw handler) - EntityEffectManager.OnCastReadySpell
        // fires mid-cast, AFTER AssignBundle/the missile launches but BEFORE the engine resets its own
        // readySpell/castInProgress/instantCast bookkeeping (confirmed by reading EntityEffectManager.cs
        // directly). An uncaught exception here would propagate out of the engine's own
        // PlayerSpellCasting_OnReleaseFrame/CastReadySpell call and leave that state stuck, plausibly
        // soft-locking the player out of casting anything else until reload - the same "don't let an
        // uncaught exception in a shared engine callback corrupt state beyond this mod" reasoning
        // NightWalkerSpawner.GuardedReconcile/ThrallManager.CheckTransformations/DominateTimer.Tick are
        // already hardened with, for the analogous EntityEffectBroker.OnNewMagicRound case.
        void HandleCastReadySpell(EntityEffectBundle spell)
        {
            try
            {
                HandleCastReadySpellCore(spell);
            }
            catch (System.Exception ex)
            {
                VoxsVampirism.Log("SpellCastCostRefunder: HandleCastReadySpell threw ({0}: {1}) - swallowed so the engine's own cast-state bookkeeping is never left corrupted.",
                    ex.GetType().Name, ex.Message);
            }
        }

        void HandleCastReadySpellCore(EntityEffectBundle spell)
        {
            if (spell == null || spell.CasterEntityBehaviour != GameManager.Instance.PlayerEntityBehaviour)
                return;
            if (spell.Settings.Effects == null || spell.Settings.Effects.Length != 1)
                return;

            string key = spell.Settings.Effects[0].Key;
            if (!ourSpellKeys.Contains(key))
                return;

            FormulaHelper.SpellCost cost = FormulaHelper.CalculateTotalEffectCosts(
                spell.Settings.Effects, spell.Settings.TargetType, spell.CasterEntityBehaviour.Entity, spell.Settings.MinimumCastingCost);
            spell.CasterEntityBehaviour.Entity.IncreaseMagicka(cost.spellPointCost);
            VoxsVampirism.Log("CastCostRefund: refunded {0} SP for '{1}'.", cost.spellPointCost, key);

            if (key == VampiricFlamesEffect.EffectKey || key == VampiricIceEffect.EffectKey || key == DominateEffect.EffectKey)
                ChargeUnconditionalBlood(spell.CasterEntityBehaviour, key);
        }

        static void ChargeUnconditionalBlood(DaggerfallEntityBehaviour caster, string key)
        {
            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            int bloodSpent, hpPercentOwed;
            vampirism.PayBloodOrHealth(unconditionalBloodCost, out bloodSpent, out hpPercentOwed);
            if (hpPercentOwed > 0)
            {
                caster.Entity.DecreaseHealth(Mathf.RoundToInt(caster.Entity.MaxHealth * hpPercentOwed / 100f));
                DaggerfallUI.AddHUDText("You burn what little remains in your veins.");
            }

            VoxsVampirism.Log("CastCostRefund: '{0}' charged {1} blood, {2}% HP owed.", key, bloodSpent, hpPercentOwed);
        }
    }
}
