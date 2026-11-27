using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal static class CharacterProgressionEvents
{
    internal static bool HasListeners => ServerModHooks.All<ICharacterProgressionEvents>().Count > 0;

    internal static void ExperienceGained(GameSession character)
        => Notify(character, hook => hook.ExperienceGained(character));

    internal static void QuestChanged(GameSession character, int questId)
        => Notify(character, hook => hook.QuestChanged(character, questId));

    private static void Notify(GameSession character, Action<ICharacterProgressionEvents> notify)
    {
        foreach (var hook in ServerModHooks.All<ICharacterProgressionEvents>())
        {
            try { notify(hook); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Character progression] Update for {character.CharacterId} failed: {ex.Message}");
            }
        }
    }
}
