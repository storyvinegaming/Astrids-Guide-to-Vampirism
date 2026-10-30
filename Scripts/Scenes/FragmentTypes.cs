// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Narrative Feeding Scenes spec: the fragment library's own data shape, deserialized from
// Scenes/FeedScenes.json via FullSerializer (TextAssetReader.TryRead<FragmentLibrary>, the same
// nested-List-of-custom-classes capability already proven by ThrallManager's own ThrallRecord_v1[]
// and, at engine scale, Quest.cs's QuestSaveData_v1). Plain public-field classes, no [Serializable]
// attribute needed (FullSerializer doesn't require one - only JsonUtility would), and no enum types
// anywhere in this file per this mod's own hard rule - type/approach/method/roll are all plain
// strings, resolved against closed vocabularies at runtime (SceneEffects.cs, DFCareer.Skills via
// Enum.TryParse - an ENGINE enum, not one declared in this mod's own source, so the "no custom enum"
// rule doesn't apply to referencing it).

using System.Collections.Generic;

namespace VoxsVampirismMod.Scenes
{
    public class FragmentLibrary
    {
        public List<FragmentData> fragments;
    }

    public class FragmentData
    {
        public string id;

        // "opening" | "approach_label" | "complication" | "resolution" | "offer" - plain string, no
        // enum.
        public string type;

        // Location/context tags this fragment is eligible under: "streets"|"inn"|"residence"|
        // "palace"|"wilderness", plus "indoor"|"outdoor", "night"|"latenight". Empty/omitted tags
        // means this fragment is an always-eligible fallback for its type (see
        // FragmentLibraryLoader.SelectFragment) - guarantees scene assembly can never come up empty.
        public List<string> tags;

        // Optional gate by player state (Humanity band, blood level) - null means no gate. This is
        // how low Humanity biases scenes darker and starving injects desperation content, using the
        // dials that already exist as selectors rather than a separate difficulty system.
        public FragmentRequires requires;

        // Second-person present tense, matching this mod's existing flavor-text voice. Supports
        // {victim}/{building}/{time}/{humanityNote} token substitution - see
        // NarrativeFeedSceneEngine's own token-substitution helper for the full set.
        public string text;

        // Complications only - the mini-choice branch shown alongside this fragment's text.
        public List<FragmentChoice> choices;

        // resolution only: which mechanical method and outcome this fragment narrates. method uses
        // "stealth" | "etiquette" | "feast" | "opportunity"; success selects the success-branch text
        // vs the failure-branch text for that method.
        public string method;
        public bool? success;

        // opening only: this opening's own four approaches, each authored to fit its specific
        // situation - no more universal fixed Etiquette/Stealth/Feast options (the "approach_label"
        // fragment type is fully retired). Exactly one entry with method=="feast" is required -
        // FragmentLibraryLoader validates this at load time and injects a default lethal option into
        // any opening found missing one, so NarrativeFeedSceneEngine can always assume at least one
        // feast-method approach exists once a fragment library has actually loaded.
        public List<OpeningApproach> approaches;

        // opening only: Dark Brotherhood sanctioned-feeding flag - when true, every non-feast
        // approach's roll at Resolution is waived and auto-succeeds (the Brotherhood offers the
        // victim, there's nothing to fail). The feast-method approach is never waived - taking more
        // than was offered is still a kill. See NarrativeFeedSceneEngine.RunResolution.
        public bool autoSuccess;

        // offer only: "thrall" | "vampire" - which rare post-feed offer this fragment's text narrates.
        // See NarrativeFeedSceneEngine's own Post-Feed Offer region.
        public string offer;
    }

    public class OpeningApproach
    {
        public string label;

        // A DFCareer.Skills name/display name (e.g. "Climbing", "Hand-to-Hand", "Critical Strike") -
        // resolved leniently (spaces/hyphens stripped, case-insensitive) rather than requiring exact
        // C# enum-member spelling, since authored content naturally uses display-style names. See
        // NarrativeFeedSceneEngine.TryParseSkill.
        public string roll;

        // "stealth" | "etiquette" | "feast" | "opportunity" | "hunt" - selects both which resolution
        // pool narrates the outcome and the failure consequence profile (stealth = loud/guards-called,
        // etiquette and opportunity = soft/no-consequence, feast = the existing lethal pipeline, hunt =
        // Wilderness Hunt's own animal-spawn resolution, animalhunt openings only).
        public string method;

        // The guaranteed-to-exist lethal fallback - injected by FragmentLibraryLoader's own load-time
        // validation whenever a (non-animalhunt) opening is missing a feast-method approach, and
        // reused by NarrativeFeedSceneEngine's own defensive fallback if an opening has no approaches
        // at all. One canonical source for both, so they can never drift apart.
        public static OpeningApproach DefaultFeast()
        {
            return new OpeningApproach { label = "Take them by force, and take everything", roll = "HandToHand", method = "feast" };
        }

        // Wilderness Hunt spec: the animalhunt equivalent of DefaultFeast() above - reused by
        // NarrativeFeedSceneEngine's own defensive fallback if an animalhunt opening somehow has no
        // approaches at all (animalhunt openings are exempt from ValidateAndRepairOpenings' own
        // feast-injection, so this is the only guaranteed-to-exist hunt approach).
        public static OpeningApproach DefaultHunt()
        {
            return new OpeningApproach { label = "Track it down by instinct alone", roll = "Mysticism", method = "hunt" };
        }
    }

    public class FragmentRequires
    {
        public int? humanityMax;
        public int? humanityMin;
        public int? bloodMin;
        public int? bloodMax;
    }

    public class FragmentChoice
    {
        public string label;

        // Flat-effect choice (mutually exclusive with roll/success/failure below) - one of the
        // closed vocabulary strings SceneEffects.ParseEffect understands: "bonus:N", "penalty:N",
        // "blood:1", "abort", "escalate", "end_scene", "humanity:+1", "humanity:-1",
        // "reveal_vampire", "hunter_arrives", "reveal_werewolf", "reveal_daedra", "temple_massacre".
        public string effect;

        // Skill-check choice (mutually exclusive with effect above) - names any DFCareer.Skills
        // value (e.g. "Lockpicking", "Thaumaturgy"), resolved via Enum.TryParse, rolled d100 vs the
        // player's live skill value. success/failure are each their own effect string, same closed
        // vocabulary as effect above.
        public string roll;
        public string success;
        public string failure;
    }
}
