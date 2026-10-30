// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Debug/testing console commands, following the exact registration pattern DFU's own
// VampirismEffect.cs uses (Wenzil.Console's ConsoleCommandsDatabase.RegisterCommand, no interface
// needed - just a static method matching the ConsoleCommandCallback delegate shape) and the same
// shape Astrid's Guide to Necromancy's own VoxsNecromancyConsoleCommands uses.

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;
using Wenzil.Console;

namespace VoxsVampirismMod
{
    public static class VoxsVampirismConsoleCommands
    {
        public static void RegisterCommands()
        {
            try
            {
                ConsoleCommandsDatabase.RegisterCommand(SetHumanity.name, SetHumanity.description, SetHumanity.usage, SetHumanity.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SetBlood.name, SetBlood.description, SetBlood.usage, SetBlood.Execute);
                ConsoleCommandsDatabase.RegisterCommand(ChooseVampireClan.name, ChooseVampireClan.description, ChooseVampireClan.usage, ChooseVampireClan.Execute);
                ConsoleCommandsDatabase.RegisterCommand(GiveVampireBook.name, GiveVampireBook.description, GiveVampireBook.usage, GiveVampireBook.Execute);
            }
            catch (System.Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message, true);
            }
        }

        private static class SetHumanity
        {
            public static readonly string name = "sethumanity";
            public static readonly string description = "Directly sets the vampire's Humanity score (0-10), for testing.";
            public static readonly string usage = "sethumanity <number>";

            public static string Execute(params string[] args)
            {
                if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                    return "Player is not a vampire.";

                int value;
                if (args == null || args.Length < 1 || !int.TryParse(args[0], out value))
                    return usage;

                VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
                if (vampirism == null)
                    return "Player is not a vampire.";

                vampirism.SetHumanity(value);
                return string.Format("Humanity set to {0}.", vampirism.GetHumanity());
            }
        }

        private static class SetBlood
        {
            public static readonly string name = "setblood";
            public static readonly string description = "Directly sets the vampire's Blood Points (0-10), for testing.";
            public static readonly string usage = "setblood <number>";

            public static string Execute(params string[] args)
            {
                if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                    return "Player is not a vampire.";

                int value;
                if (args == null || args.Length < 1 || !int.TryParse(args[0], out value))
                    return usage;

                VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
                if (vampirism == null)
                    return "Player is not a vampire.";

                vampirism.SetBloodPoints(value);
                return string.Format("Blood set to {0}.", vampirism.GetBloodPoints());
            }
        }

        // Reopens the clan selection+confirmation ceremony (VoxsVampirismEffect.ReopenClanSelection) -
        // async/UI-driven, unlike SetHumanity/SetBlood above, so the returned string is just an
        // acknowledgement that the menu opened, not a final result.
        private static class ChooseVampireClan
        {
            public static readonly string name = "choose_vamp_clan";
            public static readonly string description = "Reopens the vampire clan selection ceremony, replacing your current clan with whichever you pick.";
            public static readonly string usage = "choose_vamp_clan";

            public static string Execute(params string[] args)
            {
                if (!GameManager.Instance.PlayerEffectManager.HasVampirism())
                    return "Player is not a vampire.";

                VoxsVampirismEffect vampirism = GameManager.Instance.PlayerEffectManager.GetRacialOverrideEffect() as VoxsVampirismEffect;
                if (vampirism == null)
                    return "Player is not a vampire.";

                vampirism.ReopenClanSelection();
                return "Choose your clan.";
            }
        }

        // Deliberately NOT gated on HasVampirism() like the commands above - the book is the entry
        // point for non-vampires too (a level 2+ mortal reading it gets the transformation offer),
        // so a mortal asking for the book is a legitimate use, not an error. GrantVampireBook's own
        // "already has it" scan makes repeat calls a safe no-op.
        private static class GiveVampireBook
        {
            public static readonly string name = "give_vampire_book";
            public static readonly string description = "Adds Astrid's Guide to Vampirism (the book) to your inventory, if you don't already have it.";
            public static readonly string usage = "give_vampire_book";

            public static string Execute(params string[] args)
            {
                VoxsVampirism.GrantVampireBook(GameManager.Instance.PlayerEntity);
                return "Done - check your inventory's Magic Items tab.";
            }
        }
    }
}
