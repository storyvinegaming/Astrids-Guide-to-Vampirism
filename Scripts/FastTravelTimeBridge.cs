// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Blood Point spec (A5): fast travel costs a flat 1 blood point per 5 days of travel (rounded up),
// which REPLACES the passive 12-hour drain for the jumped time entirely rather than stacking with
// it - the passive-drain timer is shifted forward by the same delta (same bracketing technique the
// old Humanity-window system used) so ApplyBloodDrain's own loop doesn't also try to "catch up" a
// jump's worth of 12-hour ticks on the very next MagicRound after arrival. DaggerfallTravelPopUp
// exposes two real, public static events for exactly this kind of bracketing: OnPreFastTravel (fires
// before DaggerfallDateTime.RaiseTime() is called) and OnPostFastTravel (fires after - including
// after the game's own existing vampire-specific arrival-time adjustment, which already forces a
// vampire to never arrive between 6am and 6pm regardless of travel type, so there's real precedent
// for DFU already treating fast travel carefully for vampires specifically).
//
// If the player can't afford the trip's cost, travel still proceeds and they simply arrive at 0
// blood (VoxsVampirismEffect.SpendBloodForFastTravel clamps rather than refuses) - Humanity itself
// has nothing passive left to shift or cost at all (its only two sources, murder and Suppress Beast,
// are both already action-triggered). Actual world time (weather, NPC schedules, etc.) is never
// touched here. This only applies to fast travel - normal time passage from walking, resting, or
// waiting still drains blood passively exactly as designed.
//
// "Allow Daylight Arrival" Mod Setting (off by default): vanilla DFU's own performFastTravel()
// (DaggerfallTravelPopUp.cs, read directly) force-delays arrival to dusk whenever a vampire would
// otherwise land during daylight - "Vampires... never arrive between 6am and 6pm regardless of
// travel type", via DaggerfallDateTime.RaiseTime((DuskHour - Hour) * 3600) the moment IsDay is true,
// right after the trip's own main RaiseTime(travelTimeTotalMins * 60) call. That method is private
// with no virtual hook and this mod never uses Harmony, so the push itself can't be intercepted or
// cancelled - it's undone here instead, in OnPostFastTravel, by resetting the clock to what it would
// have read WITHOUT the push. That earlier value is fully recoverable, not guessed: the push only
// ever ADDS whole hours (an exact multiple of 3600 seconds), so preTravelTime + travelTimeTotalMins
// (captured before the trip; travelTimeTotalMins is `protected`, not accessible from outside a
// subclass, so read via reflection - the same technique VoxsVampirism.cs's own
// ReadBrokerLastGameMinute already established precedent for) is EXACTLY the pre-push clock reading,
// regardless of whether DFU's own push fired or not. DaggerfallDateTime.FromClassicDaggerfallTime is
// a genuine absolute-set, not just another RaiseTime forward-add, so this can undo the push cleanly.
// Applied BEFORE the blood-cost section below reads its own delta, so a bypassed push is never
// double-charged as extra "waiting for dusk" travel time.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Utility;

namespace VoxsVampirismMod
{
    public class FastTravelTimeBridge : MonoBehaviour
    {
        uint preTravelTime;
        int preTravelTimeTotalMins;
        bool preTravelSpeedCautious;

        static System.Reflection.FieldInfo travelTimeTotalMinsField;

        void Awake()
        {
            DaggerfallTravelPopUp.OnPreFastTravel += OnPreFastTravel;
            DaggerfallTravelPopUp.OnPostFastTravel += OnPostFastTravel;
        }

        void OnDestroy()
        {
            DaggerfallTravelPopUp.OnPreFastTravel -= OnPreFastTravel;
            DaggerfallTravelPopUp.OnPostFastTravel -= OnPostFastTravel;
        }

        void OnPreFastTravel(DaggerfallTravelPopUp popup)
        {
            preTravelTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            preTravelTimeTotalMins = ReadTravelTimeTotalMins(popup);
            // Public property, no reflection needed - captured for UndoDaylightArrivalPushIfEnabled's
            // own cautious-arrival adjustment below.
            preTravelSpeedCautious = popup != null && popup.SpeedCautious;
        }

        static int ReadTravelTimeTotalMins(DaggerfallTravelPopUp popup)
        {
            try
            {
                if (travelTimeTotalMinsField == null)
                    travelTimeTotalMinsField = typeof(DaggerfallTravelPopUp).GetField("travelTimeTotalMins",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (travelTimeTotalMinsField == null || popup == null)
                    return -1;
                return (int)travelTimeTotalMinsField.GetValue(popup);
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        void OnPostFastTravel()
        {
            UndoDaylightArrivalPushIfEnabled();

            uint postTravelTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
            uint delta = postTravelTime - preTravelTime;
            if (delta == 0)
                return;

            VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
            if (vampirism == null)
                return;

            vampirism.ShiftTimersForFastTravel(delta);

            // 1 point per 5 days of travel, rounded up at each step (a 6-day trip is 2 days-of-5, so
            // 2 points, not 1) - integer ceiling division done in two steps rather than floating point.
            uint days = (delta + DaggerfallDateTime.MinutesPerDay - 1) / DaggerfallDateTime.MinutesPerDay;
            uint cost = (days + 4) / 5;
            vampirism.SpendBloodForFastTravel((int)cost);
        }

        // See file header. A no-op whenever the setting is off, the reflection lookup failed
        // (preTravelTimeTotalMins stays -1), or the player isn't currently a vampire at all - the
        // vanilla push only ever applies to HasVampirism()/DamageFromSunlight in the first place, and
        // this setting is scoped to vampires specifically (Career.DamageFromSunlight is a vanilla
        // character-creation trait unrelated to this mod, left untouched).
        void UndoDaylightArrivalPushIfEnabled()
        {
            if (!VoxsVampirism.AllowDaylightArrival || preTravelTimeTotalMins < 0)
                return;
            if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                return;

            DaggerfallDateTime clock = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime;
            uint naturalArrivalTime = preTravelTime + (uint)preTravelTimeTotalMins;
            uint actualArrivalTime = clock.ToClassicDaggerfallTime();
            if (actualArrivalTime != naturalArrivalTime)
            {
                clock.FromClassicDaggerfallTime(naturalArrivalTime);
                VoxsVampirism.Log("FastTravelTimeBridge: Allow Daylight Arrival - undid vanilla's forced dusk delay (was classic minute {0}, restored to {1}).",
                    actualArrivalTime, naturalArrivalTime);
            }

            // Vanilla's own cautious-travel morning adjustment (performFastTravel's `else if
            // (speedCautious)` branch, replicated verbatim below): a vampire NEVER got it - the
            // vampire dusk push sits in the `if` ahead of it, so the else-branch was structurally
            // unreachable for a vampire regardless of this mod. With the dusk push undone, a cautious
            // vampire landed at the raw natural arrival time - day or night, purely dependent on trip
            // length - which read as the toggle "only sometimes" working (explicit report). Applied
            // here whether or not the dusk push fired at all (a natural night arrival never triggers
            // the push, but cautious travel should still morning-shift it, exactly as it does for
            // every non-vampire), so a cautious vampire now gets the same "arrive a little after
            // 7am" guarantee every other cautious traveler has always had.
            if (preTravelSpeedCautious)
            {
                if (clock.Hour < 7 || (clock.Hour == 7 && clock.Minute < 10))
                {
                    float raiseTime = ((7 - clock.Hour) * 3600)
                        + ((10 - clock.Minute) * 60)
                        - clock.Second;
                    clock.RaiseTime(raiseTime);
                    VoxsVampirism.Log("FastTravelTimeBridge: Allow Daylight Arrival - cautious travel, shifted night arrival forward to just after 7am.");
                }
                else if (clock.Hour > 17)
                {
                    float raiseTime = ((31 - clock.Hour) * 3600)
                        + ((10 - clock.Minute) * 60)
                        - clock.Second;
                    clock.RaiseTime(raiseTime);
                    VoxsVampirism.Log("FastTravelTimeBridge: Allow Daylight Arrival - cautious travel, shifted evening arrival forward to just after 7am next morning.");
                }
            }
        }
    }
}
