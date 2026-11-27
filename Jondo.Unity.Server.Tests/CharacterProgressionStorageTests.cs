using Jondo.Unity.Server.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacterProgressionStorageTests
{
    private const int Server = DatabaseManager.DefaultServerId;

    [Fact]
    public void ExperienceReplacesBothLowerAndHigherTotalsOnTheSameAccountAndServer()
    {
        using var db = Open();

        var points = CharacterProgressionStorage.CopyExperience(db, 10, Server, 1, 100_000, 10);

        Assert.Equal(43, points[2]);
        Assert.Equal(0, points[3]);
        Assert.Equal(2, points.Count);
        Assert.Equal("1:7:75:30|2:10:100000:43|3:10:100000:0|4:8:90:35|5:6:60:25",
            Scalar(db, """
                SELECT group_concat(progress, '|') FROM (
                    SELECT Id || ':' || Level || ':' || Experience || ':' || RemainingPoints AS progress
                    FROM Characters ORDER BY Id
                );
                """));
    }

    [Fact]
    public void QuestCopyReplacesProgressAndResetOnlyRemovesTheAffectedQuest()
    {
        using var db = Open();
        const string read = """
            SELECT group_concat(progress, '|') FROM (
                SELECT CharacterId || ':' || QuestId || ':' || StepId || ':' || Objectives || ':' || Completed || ':' || CompletedAt AS progress
                FROM CharacterQuests ORDER BY CharacterId, QuestId
            );
            """;
        CharacterProgressionStorage.CopyQuest(db, 10, Server, 1, 101, new(101, 9, "7|8:2", false));
        Assert.Equal("1:101:9:7|8:2:0:0|2:101:9:7|8:2:0:0|2:202:1::0:0|3:101:9:7|8:2:0:0|4:101:2::0:0|5:101:3::0:0",
            Scalar(db, read));

        CharacterProgressionStorage.CopyQuest(db, 10, Server, 1, 101, new(101, 9, "7,8", true, 1234));
        Assert.Equal("1:101:9:7|8:2:0:0|2:101:9:7,8:1:1234|2:202:1::0:0|3:101:9:7,8:1:1234|4:101:2::0:0|5:101:3::0:0",
            Scalar(db, read));

        CharacterProgressionStorage.CopyQuest(db, 10, Server, 1, 101, null);
        Assert.Equal("1:101:9:7|8:2:0:0|2:202:1::0:0|4:101:2::0:0|5:101:3::0:0", Scalar(db, read));
    }

    private static SqliteConnection Open()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var setup = db.CreateCommand();
        setup.CommandText = $"""
            CREATE TABLE Characters (Id INTEGER PRIMARY KEY, AccountId INTEGER, ServerId INTEGER,
                Breed INTEGER, Level INTEGER, Experience INTEGER, RemainingPoints INTEGER, Vitality INTEGER,
                Wisdom INTEGER DEFAULT 0, Strength INTEGER DEFAULT 0, Intelligence INTEGER DEFAULT 0,
                Chance INTEGER DEFAULT 0, Agility INTEGER DEFAULT 0);
            INSERT INTO Characters (Id, AccountId, ServerId, Breed, Level, Experience, RemainingPoints, Vitality) VALUES
                (1, 10, {Server}, 1, 7, 75, 30, 0),
                (2, 10, {Server}, 1, 3, 15, 8, 2),
                (3, 10, NULL, 1, 20, 200000, 45, 50),
                (4, 20, {Server}, 1, 8, 90, 35, 0),
                (5, 10, {Server + 1}, 1, 6, 60, 25, 0);
            CREATE TABLE CharacterQuests (CharacterId INTEGER, QuestId INTEGER, StepId INTEGER,
                Objectives TEXT, Completed INTEGER, CompletedAt INTEGER, PRIMARY KEY (CharacterId, QuestId));
            INSERT INTO CharacterQuests VALUES
                (1, 101, 9, '7|8:2', 0, 0), (2, 101, 1, '', 0, 0), (2, 202, 1, '', 0, 0),
                (4, 101, 2, '', 0, 0), (5, 101, 3, '', 0, 0);
            """;
        setup.ExecuteNonQuery();
        return db;
    }

    private static string Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
