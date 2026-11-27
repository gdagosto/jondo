using Jondo.Unity.Server.Database;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Quests;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Mods;

internal sealed partial class CharacterWorld : ICharacterProgressionWorld
{
    public bool IsCompanionController(ICharacterSession character)
        => character is GameSession { HasTransport: true, IsInWorld: true, ServerId: > 0 }
            && CompanionSessions.Policy is { } policy && policy.Find(character.CharacterId) == null;

    public void CopyExperienceToAccount(ICharacterSession character)
    {
        var source = (GameSession)character;
        long experience = source.State.Experience;
        int level = source.State.CharacterLevel;
        using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
        connection.Open();
        var points = CharacterProgressionStorage.CopyExperience(connection, source.AccountId, source.ServerId,
            source.CharacterId, experience, level);
        foreach (var follower in ProgressionFollowers(source))
        {
            if (!points.TryGetValue(follower.CharacterId, out int remaining)) continue;
            follower.State.Experience = experience;
            follower.State.CharacterLevel = level;
            follower.State.CharacterRemainingPoints = remaining;
        }
    }

    public void CopyQuestToAccount(ICharacterSession character, int questId)
    {
        var source = (GameSession)character;
        var run = source.State.Quests?.Run(questId);
        DatabaseManager.QuestRow? progress = run == null ? null : Quests.ProgressRow(questId, run);
        using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
        connection.Open();
        CharacterProgressionStorage.CopyQuest(connection, source.AccountId, source.ServerId,
            source.CharacterId, questId, progress);

        foreach (var follower in ProgressionFollowers(source))
        {
            if (follower.State.Quests is not { } log) continue;
            if (run != null && progress is { } row)
            {
                QuestRun copy = log.Restore(questId, row.StepId, row.Objectives, row.Completed, run.FinishedAt);
                foreach (var (objective, doneAt) in run.DoneAt) copy.DoneAt.Add(objective, doneAt);
            }
            else
            {
                log.Forget(questId);
            }
        }
    }

    private static IEnumerable<GameSession> ProgressionFollowers(GameSession source)
        => CompanionSessions.Of(source.CharacterId).Select(member => member.Session).OfType<GameSession>()
            .Where(follower => follower.AccountId == source.AccountId && follower.ServerId == source.ServerId);
}
