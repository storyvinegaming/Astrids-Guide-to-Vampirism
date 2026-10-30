// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Narrative Feeding Scenes spec: loads Scenes/FeedScenes.json lazily (on first scene start, not at
// mod Init()) via TextAssetReader.TryRead<FragmentLibrary> - the same general engine utility DFU
// itself uses for loose-file-overridable mod data, confirmed to check StreamingAssets/Scenes/
// FeedScenes.json on disk FIRST (dev/player editable without rebuilding) before falling back to the
// mod-bundled TextAsset (matched by filename only), deserializing through FullSerializer via
// SaveLoadManager.Deserialize internally. A load failure degrades gracefully (IsAvailable false,
// narrative scenes simply unavailable that session) rather than blocking mod startup - this file is
// intentionally the only place that ever calls TryRead, so every other file just asks IsAvailable/
// SelectFragment and never has to think about the load path itself.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Utility.AssetInjection;

namespace VoxsVampirismMod.Scenes
{
    public static class FragmentLibraryLoader
    {
        const string RelativePath = "Scenes/FeedScenes.json";

        static FragmentLibrary library;
        static bool loadAttempted;

        // Per-type last-used fragment id - avoids immediately repeating the same opening/
        // complication/resolution fragment back-to-back when more than one is eligible. Not
        // persisted across sessions; a fresh game launch starts with no memory, which is fine (the
        // spec only asks to avoid repeats within play, not across a cold restart).
        static readonly Dictionary<string, string> lastUsedIdPerType = new Dictionary<string, string>();

        static void EnsureLoaded()
        {
            if (loadAttempted)
                return;
            loadAttempted = true;

            FragmentLibrary loaded = null;
            bool ok = false;
            try
            {
                ok = TextAssetReader.TryRead<FragmentLibrary>(RelativePath, out loaded);
            }
            catch (System.Exception ex)
            {
                // A hand-edited loose-file override with malformed JSON is the most likely real-world
                // cause - fails loudly to the log rather than crashing scene assembly or silently
                // falling back to the flat menu with no explanation.
                VoxsVampirism.Log("FragmentLibraryLoader: exception loading '{0}': {1}", RelativePath, ex.Message);
                loaded = null;
                ok = false;
            }

            if (!ok || loaded == null || loaded.fragments == null)
            {
                VoxsVampirism.Log("FragmentLibraryLoader: failed to load '{0}' - narrative scenes unavailable this session.", RelativePath);
                library = null;
                return;
            }

            ValidateAndRepairOpenings(loaded);

            library = loaded;
            VoxsVampirism.Log("FragmentLibraryLoader: loaded {0} fragments from '{1}'.", library.fragments.Count, RelativePath);
        }

        // The feast-method approach is the player's guaranteed lethal door and the bloodlust-
        // conversion target - it must exist on every opening, no exceptions... except an animalhunt
        // opening (Wilderness Hunt spec), which is exempt entirely: the whole scene already ends in
        // violence (a spawned hostile animal), so a separate "take them by force" option is meaningless
        // - there's no one to take by force. Runs once at load time (not per-scene) so a malformed
        // opening is repaired exactly once, loudly, rather than silently degrading every time that
        // particular opening happens to get selected.
        static void ValidateAndRepairOpenings(FragmentLibrary loadedLibrary)
        {
            for (int i = 0; i < loadedLibrary.fragments.Count; i++)
            {
                FragmentData fragment = loadedLibrary.fragments[i];
                if (fragment == null || fragment.type != "opening")
                    continue;

                if (fragment.tags != null && fragment.tags.Contains("animalhunt"))
                    continue;

                bool hasFeast = false;
                if (fragment.approaches != null)
                {
                    for (int j = 0; j < fragment.approaches.Count; j++)
                    {
                        if (fragment.approaches[j] != null && fragment.approaches[j].method == "feast")
                        {
                            hasFeast = true;
                            break;
                        }
                    }
                }

                if (hasFeast)
                    continue;

                VoxsVampirism.Log("FragmentLibraryLoader: opening '{0}' has no feast-method approach - injecting the default lethal option.", fragment.id);
                if (fragment.approaches == null)
                    fragment.approaches = new List<OpeningApproach>();
                fragment.approaches.Add(OpeningApproach.DefaultFeast());
            }
        }

        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return library != null;
            }
        }

        // Scene continuity fix: the five location values are their own dimension, checked separately
        // from every other tag (indoor/outdoor, night/latenight) rather than lumped into one generic
        // any-overlap match. Under the old any-overlap rule, a fragment tagged ["streets","outdoor",
        // "night"] could still be picked for a ["residence","indoor","night"] scene purely because
        // both happened to share "night" - a real continuity break (a streets-flavored line surfacing
        // mid-bedroom-scene). Referenced from SneakFeedController's own constants rather than
        // duplicated literals, so there's exactly one place that ever spells out the five values.
        static readonly HashSet<string> locationTagValues = new HashSet<string>
        {
            SneakFeedController.SceneLocationStreets,
            SneakFeedController.SceneLocationInn,
            SneakFeedController.SceneLocationResidence,
            SneakFeedController.SceneLocationPalace,
            SneakFeedController.SceneLocationWilderness,
            SneakFeedController.SceneLocationDarkBrotherhood,
            SneakFeedController.SceneLocationTemple,
            SneakFeedController.SceneLocationFightersGuild,
            SneakFeedController.SceneLocationKnightlyOrder,
            SneakFeedController.SceneLocationMagesGuild,
            SneakFeedController.SceneLocationThievesGuild,
        };

        // Filters by type + tag/requires eligibility, picks uniformly at random among eligible, and
        // avoids repeating the type's most recently used fragment when more than one candidate
        // qualifies. Returns null only if the pool is genuinely empty for this type/context - the
        // starter library's own untagged fallback fragments (empty tags, no requires) are what
        // guarantees that never happens in practice; callers should still treat null defensively
        // (see NarrativeFeedSceneEngine's own fallback text) rather than assume it can't occur.
        // Used directly for "opening" and "complication" - "resolution" has its own extra filter
        // dimension (method+success), handled by SelectResolution below, which funnels through this
        // same core. The "approach_label" fragment type is retired entirely - openings now carry
        // their own four approaches directly (FragmentData.approaches), authored per-situation rather
        // than pulled from a shared label pool.
        public static FragmentData SelectFragment(string type, string locationTag, List<string> contextTags, int humanity, int blood)
        {
            return SelectFragment(type, locationTag, contextTags, humanity, blood, null);
        }

        // resolution only: additionally filtered by fragment.method == method AND fragment.success ==
        // success, on top of the usual type/tags/requires eligibility.
        public static FragmentData SelectResolution(string method, bool success, string locationTag, List<string> contextTags, int humanity, int blood)
        {
            return SelectFragment("resolution", locationTag, contextTags, humanity, blood,
                candidate => candidate.method == method && candidate.success.HasValue && candidate.success.Value == success);
        }

        // offer only: additionally filtered by fragment.offer == offerKind ("thrall" | "vampire"), on
        // top of the usual type/tags/requires eligibility. Post-Feed Offers update - see
        // NarrativeFeedSceneEngine's own Post-Feed Offer region.
        public static FragmentData SelectOffer(string offerKind, string locationTag, List<string> contextTags, int humanity, int blood)
        {
            return SelectFragment("offer", locationTag, contextTags, humanity, blood,
                candidate => candidate.offer == offerKind);
        }

        // Wilderness Hunt spec: opening/complication selection for an animal hunt - additionally
        // filtered to fragment.tags containing "animalhunt", on top of the usual type/tags/requires
        // eligibility. Necessary because ordinary townsperson wilderness fragments ALSO carry the
        // "wilderness" location tag (the same locationTag a hunt scene uses) - without this extra
        // filter, an ordinary wilderness opening/complication could leak into a hunt scene (ClassifyLocationFit
        // alone can't tell the two apart, since both share the same location tag).
        public static FragmentData SelectHuntFragment(string type, string locationTag, List<string> contextTags, int humanity, int blood)
        {
            return SelectFragment(type, locationTag, contextTags, humanity, blood,
                candidate => candidate.tags != null && candidate.tags.Contains("animalhunt"));
        }

        // Wilderness Hunt spec: hunt resolution selection - additionally filtered by fragment.method
        // == "hunt" AND fragment.success == success AND fragment.tags containing "animalhunt", same
        // leak-prevention reasoning as SelectHuntFragment above, PLUS a "hunttwist" tag match/exclusion:
        // isTwist requires the resolution to also carry "hunttwist" (the one twist-table resolution),
        // while a normal (non-twist) roll requires it NOT to - otherwise a normal successful hunt could
        // occasionally draw the twist-flavored resolution text purely by chance.
        public static FragmentData SelectHuntResolution(bool success, bool isTwist, string locationTag, List<string> contextTags, int humanity, int blood)
        {
            return SelectFragment("resolution", locationTag, contextTags, humanity, blood,
                candidate =>
                {
                    if (candidate.method != "hunt" || !candidate.success.HasValue || candidate.success.Value != success)
                        return false;
                    if (candidate.tags == null || !candidate.tags.Contains("animalhunt"))
                        return false;

                    bool hasTwistTag = candidate.tags.Contains("hunttwist");
                    return hasTwistTag == isTwist;
                });
        }

        // Two-tier eligible pool: "matched" (the fragment explicitly carries this scene's own
        // location tag) is tried first and preferred whenever it has anything to offer; "fallback"
        // (the fragment carries no location tag at all - context-tag/requires-eligible but location-
        // agnostic) is only used when the matched tier comes up empty. A fragment carrying a
        // DIFFERENT location tag than the scene's own is excluded outright before either tier, no
        // matter what else it shares with the current context - the actual continuity fix.
        static FragmentData SelectFragment(string type, string locationTag, List<string> contextTags, int humanity, int blood,
            System.Func<FragmentData, bool> extraFilter)
        {
            EnsureLoaded();
            if (library == null || library.fragments == null)
                return null;

            List<FragmentData> matched = new List<FragmentData>();
            List<FragmentData> fallback = new List<FragmentData>();

            for (int i = 0; i < library.fragments.Count; i++)
            {
                FragmentData candidate = library.fragments[i];
                if (candidate == null || candidate.type != type)
                    continue;
                if (extraFilter != null && !extraFilter(candidate))
                    continue;
                if (!PassesRequires(candidate, humanity, blood))
                    continue;

                int fit = ClassifyLocationFit(candidate, locationTag);
                if (fit == LocationExcluded)
                    continue;
                if (!PassesNonLocationTags(candidate, contextTags))
                    continue;

                if (fit == LocationMatched)
                    matched.Add(candidate);
                else
                    fallback.Add(candidate);
            }

            List<FragmentData> eligible = matched.Count > 0 ? matched : fallback;
            bool usedFallbackTier = matched.Count == 0;

            if (eligible.Count == 0)
            {
                VoxsVampirism.Log("FragmentLibraryLoader: no eligible '{0}' fragment for location '{1}', context [{2}] (humanity={3}, blood={4}).",
                    type, locationTag, contextTags != null ? string.Join(",", contextTags.ToArray()) : "", humanity, blood);
                return null;
            }

            // Last-used tracking is keyed by type only (not by the extra filter) - a slightly coarser
            // "don't immediately repeat" than per-approach/per-method would be, but simple and still
            // achieves the spec's own intent (avoid back-to-back repeats) without a combinatorial key.
            string lastUsedId;
            lastUsedIdPerType.TryGetValue(type, out lastUsedId);
            if (eligible.Count > 1 && lastUsedId != null)
            {
                List<FragmentData> withoutLastUsed = new List<FragmentData>();
                for (int i = 0; i < eligible.Count; i++)
                {
                    if (eligible[i].id != lastUsedId)
                        withoutLastUsed.Add(eligible[i]);
                }
                if (withoutLastUsed.Count > 0)
                    eligible = withoutLastUsed;
            }

            FragmentData chosen = eligible[Random.Range(0, eligible.Count)];
            lastUsedIdPerType[type] = chosen.id;

            VoxsVampirism.Log("FragmentLibraryLoader: selected '{0}' fragment '{1}' for location '{2}' from {3} eligible candidate(s) ({4}).",
                type, chosen.id, locationTag, eligible.Count, usedFallbackTier ? "fallback tier" : "location-matched");

            return chosen;
        }

        const int LocationExcluded = 0;
        const int LocationMatched = 1;
        const int LocationFallback = 2;

        // A fragment carrying no location tag at all (regardless of any other tags it might have) is
        // always LocationFallback. A fragment carrying the scene's own location tag - even alongside
        // others - is LocationMatched, checked first so a fragment intentionally covering more than
        // one location (e.g. tagged for both "inn" and "streets") still matches correctly in either.
        // Only a fragment whose location tag(s) are ALL different from the scene's own is
        // LocationExcluded - the hard veto that fixes the continuity bug.
        static int ClassifyLocationFit(FragmentData candidate, string locationTag)
        {
            if (candidate.tags == null || candidate.tags.Count == 0)
                return LocationFallback;

            bool hasAnyLocationTag = false;
            for (int i = 0; i < candidate.tags.Count; i++)
            {
                string tag = candidate.tags[i];
                if (!locationTagValues.Contains(tag))
                    continue;

                hasAnyLocationTag = true;
                if (tag == locationTag)
                    return LocationMatched;
            }

            return hasAnyLocationTag ? LocationExcluded : LocationFallback;
        }

        // Everything else (indoor/outdoor, night/latenight, and any future non-location tag) keeps
        // the original any-overlap semantics, but only over the fragment's OWN non-location tags -
        // location fit is decided entirely by ClassifyLocationFit above, never by a shared time-of-
        // day tag riding along with a mismatched location one.
        static bool PassesNonLocationTags(FragmentData candidate, List<string> contextTags)
        {
            if (candidate.tags == null || candidate.tags.Count == 0)
                return true;

            bool hasNonLocationTag = false;
            for (int i = 0; i < candidate.tags.Count; i++)
            {
                string tag = candidate.tags[i];
                if (locationTagValues.Contains(tag))
                    continue;

                hasNonLocationTag = true;
                if (contextTags != null && contextTags.Contains(tag))
                    return true;
            }

            return !hasNonLocationTag;
        }

        // Forced-trigger lookup (temple frenzy auto-trigger, NarrativeFeedSceneEngine.RunComplicationGate)
        // - bypasses the normal tag/location/random-selection pipeline entirely and returns one exact
        // fragment by id, but still runs it through the SAME PassesRequires check every other selection
        // path uses, so a forced trigger can never fire outside the fragment's own authored requires
        // block (e.g. this fragment's own "bloodMax": 1) even if a caller's own guard condition drifts
        // out of sync with the JSON. Returns null if the id doesn't exist or requires isn't currently met.
        public static FragmentData GetFragmentByIdIfEligible(string id, int humanity, int blood)
        {
            EnsureLoaded();
            if (library == null || library.fragments == null)
                return null;

            for (int i = 0; i < library.fragments.Count; i++)
            {
                FragmentData candidate = library.fragments[i];
                if (candidate != null && candidate.id == id)
                    return PassesRequires(candidate, humanity, blood) ? candidate : null;
            }

            return null;
        }

        static bool PassesRequires(FragmentData candidate, int humanity, int blood)
        {
            FragmentRequires requires = candidate.requires;
            if (requires == null)
                return true;

            if (requires.humanityMax.HasValue && humanity > requires.humanityMax.Value)
                return false;
            if (requires.humanityMin.HasValue && humanity < requires.humanityMin.Value)
                return false;
            if (requires.bloodMax.HasValue && blood > requires.bloodMax.Value)
                return false;
            if (requires.bloodMin.HasValue && blood < requires.bloodMin.Value)
                return false;

            return true;
        }
    }
}
