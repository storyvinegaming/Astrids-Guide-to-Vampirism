// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Narrative Feeding Scenes spec: the closed effect vocabulary every fragment choice resolves
// through - "the fence that keeps the fragment library from growing a parallel rules system as
// content accumulates" (the spec's own words). Plain string-tag parsing into a small result struct,
// no enum type (this mod's own hard rule) - SceneEffect.Kind is one of the KindXxx string constants
// below, checked by the engine's own switch, same convention as SneakFeedController's own
// LocationResidence/LocationInn/LocationStreets int constants.

namespace VoxsVampirismMod.Scenes
{
    public struct SceneEffect
    {
        public string Kind;
        public int Value;
    }

    public static class SceneEffects
    {
        public const string KindBonus = "bonus";
        public const string KindPenalty = "penalty";
        public const string KindBlood = "blood";
        public const string KindAbort = "abort";
        public const string KindEscalate = "escalate";
        public const string KindEndScene = "end_scene";
        public const string KindHumanity = "humanity";
        public const string KindRevealVampire = "reveal_vampire";
        public const string KindHunterArrives = "hunter_arrives";
        public const string KindRevealWerewolf = "reveal_werewolf";
        public const string KindRevealDaedra = "reveal_daedra";
        public const string KindTempleMassacre = "temple_massacre";
        // Wilderness Hunt spec: animalhunt complications only - the hunt finds something other than
        // intended prey. See NarrativeFeedSceneEngine.RunHuntTwist.
        public const string KindHuntTwist = "hunt_twist";

        // Never throws and never returns an unrecognized Kind - a malformed or missing effect
        // string (a typo in the JSON, a field left blank) falls back to end_scene (the softest,
        // safest possible outcome: the moment just resolves peacefully) rather than crashing scene
        // assembly or silently doing nothing. Logged when that fallback fires so a bad fragment is
        // diagnosable from Player.log instead of just reading as "that complication did nothing."
        public static SceneEffect ParseEffect(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new SceneEffect { Kind = KindEndScene, Value = 0 };

            string[] parts = raw.Split(':');
            string kind = parts[0].Trim();

            switch (kind)
            {
                case KindBonus:
                case KindPenalty:
                    {
                        int amount;
                        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out amount))
                            amount = 10;
                        return new SceneEffect { Kind = kind, Value = amount };
                    }

                case KindBlood:
                    {
                        int amount;
                        if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out amount))
                            amount = 1;
                        return new SceneEffect { Kind = kind, Value = amount };
                    }

                case KindHumanity:
                    {
                        bool negative = parts.Length >= 2 && parts[1].Trim().StartsWith("-");
                        return new SceneEffect { Kind = kind, Value = negative ? -1 : 1 };
                    }

                case KindAbort:
                case KindEscalate:
                case KindEndScene:
                case KindRevealVampire:
                case KindHunterArrives:
                case KindRevealWerewolf:
                case KindRevealDaedra:
                case KindTempleMassacre:
                case KindHuntTwist:
                    return new SceneEffect { Kind = kind, Value = 0 };

                default:
                    VoxsVampirismMod.VoxsVampirism.Log("SceneEffects: unrecognized effect string '{0}' - falling back to end_scene.", raw);
                    return new SceneEffect { Kind = KindEndScene, Value = 0 };
            }
        }
    }
}
