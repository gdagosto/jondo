using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database;

internal static class CharacterProgressionStorage
{
    private const string OtherCharacters = """
        AccountId = $account AND COALESCE(ServerId, $defaultServer) = $server AND Id <> $source
        """;

    internal static Dictionary<long, int> CopyExperience(SqliteConnection connection, long accountId,
        int serverId, long sourceId, long experience, int level)
    {
        using var transaction = connection.BeginTransaction();
        using var select = AccountCommand(connection, accountId, serverId, sourceId);
        select.Transaction = transaction;
        select.CommandText = $"""
            SELECT Id, Breed, Strength, Intelligence, Chance, Agility, Vitality, Wisdom
            FROM Characters WHERE {OtherCharacters};
            """;
        var pointsByCharacter = new Dictionary<long, int>();
        int capital = CharacteristicRules.TotalCapitalForLevel(level);
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                long spent = CharacteristicRules.ComputeDistributionCost(reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(1));
                pointsByCharacter.Add(reader.GetInt64(0), (int)Math.Max(0, capital - spent));
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Characters SET Experience = $xp, Level = $level, RemainingPoints = $points WHERE Id = $id;";
        update.Parameters.AddWithValue("$xp", experience);
        update.Parameters.AddWithValue("$level", level);
        var points = update.Parameters.Add("$points", SqliteType.Integer);
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (characterId, remaining) in pointsByCharacter)
        {
            id.Value = characterId;
            points.Value = remaining;
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return pointsByCharacter;
    }

    internal static void CopyQuest(SqliteConnection connection, long accountId, int serverId, long sourceId,
        int questId, DatabaseManager.QuestRow? progress)
    {
        using var command = AccountCommand(connection, accountId, serverId, sourceId);
        command.Parameters.AddWithValue("$quest", questId);
        if (progress is { } row)
        {
            command.CommandText = $"""
                INSERT INTO CharacterQuests (CharacterId, QuestId, StepId, Objectives, Completed, CompletedAt)
                SELECT Id, $quest, $step, $objectives, $completed, $completedAt
                FROM Characters WHERE {OtherCharacters}
                ON CONFLICT(CharacterId, QuestId) DO UPDATE SET StepId = excluded.StepId,
                    Objectives = excluded.Objectives, Completed = excluded.Completed, CompletedAt = excluded.CompletedAt;
                """;
            command.Parameters.AddWithValue("$step", row.StepId);
            command.Parameters.AddWithValue("$objectives", row.Objectives);
            command.Parameters.AddWithValue("$completed", row.Completed);
            command.Parameters.AddWithValue("$completedAt", row.CompletedAt);
        }
        else
        {
            command.CommandText = $"""
                DELETE FROM CharacterQuests WHERE QuestId = $quest
                AND CharacterId IN (SELECT Id FROM Characters WHERE {OtherCharacters});
                """;
        }
        command.ExecuteNonQuery();
    }

    private static SqliteCommand AccountCommand(SqliteConnection connection, long accountId, int serverId, long sourceId)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$defaultServer", DatabaseManager.DefaultServerId);
        command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$source", sourceId);
        return command;
    }
}
