using Jondo.Unity.ServerMods;

namespace HeroProgressionSync.Server;

public sealed class HeroProgressionSyncMod : IServerMod
{
    public void Initialize(IServerModHost host)
    {
        var sync = new ProgressionSync(host.GetService<ICharacterProgressionWorld>());
        host.RegisterHook<ICharacterProgressionEvents>(sync);
        host.RegisterHook<IQuestProgressMirror>(sync);
    }

    private sealed class ProgressionSync(ICharacterProgressionWorld world) : ICharacterProgressionEvents, IQuestProgressMirror
    {
        public bool MirrorsQuestProgress(ICharacterSession character) => world.IsCompanionController(character);

        public void ExperienceGained(ICharacterSession character)
        {
            if (world.IsCompanionController(character)) world.CopyExperienceToAccount(character);
        }

        public void QuestChanged(ICharacterSession character, int questId)
        {
            if (MirrorsQuestProgress(character)) world.CopyQuestToAccount(character, questId);
        }
    }
}
