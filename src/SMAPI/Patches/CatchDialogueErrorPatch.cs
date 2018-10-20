using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Harmony;
using StardewModdingAPI.Framework.Patching;
using StardewModdingAPI.Framework.Reflection;
using StardewValley;

namespace StardewModdingAPI.Patches
{
    /// <summary>A Harmony patch for <see cref="Dialogue"/> method which intercepts invalid dialogue lines and logs an error instead of crashing.</summary>
    internal class DialoguePatch : IHarmonyPatch
    {
        /*********
        ** Private methods
        *********/
        /// <summary>Writes messages to the console and log file on behalf of the game.</summary>
        private static IMonitor MonitorForGame;

        /// <summary>Simplifies access to private code.</summary>
        private static Reflector Reflection;


        /*********
        ** Accessors
        *********/
        /// <summary>A unique name for this patch.</summary>
        public string Name => $"{nameof(GameLocation)}.{nameof(GameLocation.updateSeasonalTileSheets)}";


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitorForGame">Writes messages to the console and log file on behalf of the game.</param>
        /// <param name="reflector">Simplifies access to private code.</param>
        public DialoguePatch(IMonitor monitorForGame, Reflector reflector)
        {
            DialoguePatch.MonitorForGame = monitorForGame;
            DialoguePatch.Reflection = reflector;
        }


        /// <summary>Apply the Harmony patch.</summary>
        /// <param name="harmony">The Harmony instance.</param>
        public void Apply(HarmonyInstance harmony)
        {
            ConstructorInfo constructor = AccessTools.Constructor(typeof(Dialogue), new[] { typeof(string), typeof(NPC) });
            MethodInfo prefix = AccessTools.Method(this.GetType(), nameof(DialoguePatch.Prefix));

            harmony.Patch(constructor, new HarmonyMethod(prefix), null);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The method to call instead of the Dialogue constructor.</summary>
        /// <param name="__instance">The instance being patched.</param>
        /// <param name="masterDialogue">The dialogue being parsed.</param>
        /// <param name="speaker">The NPC for which the dialogue is being parsed.</param>
        /// <returns>Returns whether to execute the original method.</returns>
        /// <remarks>This method must be static for Harmony to work correctly. See the Harmony documentation before renaming arguments.</remarks>
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Argument names are defined by Harmony.")]
        private static bool Prefix(Dialogue __instance, string masterDialogue, NPC speaker)
        {
            // get private members
            bool nameArraysTranslated = DialoguePatch.Reflection.GetField<bool>(typeof(Dialogue), "nameArraysTranslated").GetValue();
            IReflectedMethod translateArraysOfStrings = DialoguePatch.Reflection.GetMethod(typeof(Dialogue), "TranslateArraysOfStrings");
            IReflectedMethod parseDialogueString = DialoguePatch.Reflection.GetMethod(__instance, "parseDialogueString");
            IReflectedMethod checkForSpecialDialogueAttributes = DialoguePatch.Reflection.GetMethod(__instance, "checkForSpecialDialogueAttributes");
            IReflectedField<List<string>> dialogues = DialoguePatch.Reflection.GetField<List<string>>(__instance, "dialogues");

            // replicate base constructor
            if (dialogues.GetValue() == null)
                dialogues.SetValue(new List<string>());

            // duplicate code with try..catch
            try
            {
                if (!nameArraysTranslated)
                    translateArraysOfStrings.Invoke();
                __instance.speaker = speaker;
                parseDialogueString.Invoke(masterDialogue);
                checkForSpecialDialogueAttributes.Invoke();
            }
            catch (Exception baseEx) when (baseEx.InnerException is TargetInvocationException invocationEx && invocationEx.InnerException is Exception ex)
            {
                string name = !string.IsNullOrWhiteSpace(speaker?.Name) ? speaker.Name : null;
                DialoguePatch.MonitorForGame.Log($"Failed parsing dialogue string{(name != null ? $" for {name}" : "")}:\n{masterDialogue}\n{ex}", LogLevel.Error);

                parseDialogueString.Invoke("...");
                checkForSpecialDialogueAttributes.Invoke();
            }

            return false;
        }
    }
}