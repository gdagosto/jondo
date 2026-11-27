namespace Jondo.Unity.ServerMods;

/// <summary>Notifications after character experience or a quest's progress changes.</summary>
public interface ICharacterProgressionEvents
{
    void ExperienceGained(ICharacterSession character);
    void QuestChanged(ICharacterSession character, int questId);
}

/// <summary>Allows a mod that mirrors quest state to take over from companion action replay.</summary>
public interface IQuestProgressMirror
{
    bool MirrorsQuestProgress(ICharacterSession character);
}

public interface ICharacterProgressionWorld
{
    bool IsCompanionController(ICharacterSession character);
    void CopyExperienceToAccount(ICharacterSession character);
    void CopyQuestToAccount(ICharacterSession character, int questId);
}
