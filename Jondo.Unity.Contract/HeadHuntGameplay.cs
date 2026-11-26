using System.Data.Common;

namespace Jondo.Unity.ServerMods;

public interface IHuntModHost
{
    IHuntWorld World { get; }
    void RegisterHuntGameplay(IHuntGameplay gameplay);
}

public interface IHuntWorld
{
    HuntSettings Settings { get; }
    SelectedHunter? FindSelectedCharacter(long accountId);
    HuntCharacter? FindCharacter(long characterId);
    IEnumerable<HuntBoardCharacter> FindPvpCharacters(int side, int minimumLevel);
    HuntPresence? FindPresence(long characterId);
    HuntLocation Locate(long mapId);
    HuntAlignment AlignmentOf(long characterId);

    /// <summary>Opens the existing world database for the package's hunt tables. The caller disposes the connection.</summary>
    DbConnection OpenWorldDatabase();

    void AddHonour(long characterId, int amount);
    Task RaisePvpWingsAsync(long characterId);
    Task LeaveArenaQueueAsync(long characterId);
    Task SendNoticeAsync(long characterId, string text);
    Task SendSystemMessageAsync(long characterId, int message, params string[] arguments);
    IDisposable QuietForBots(long first, long second);
}

public readonly record struct HuntSettings(bool Enabled, int WindowMinutes, int RestMinutes);
public sealed record SelectedHunter(long Id, string Name, int Level);
public sealed record HuntCharacter(long Id, string Name, int Level, int Breed);
public sealed record HuntBoardCharacter(HuntCharacter Character, string LastConnection);
public sealed record HuntPresence(string Name, string Language, bool IsInWorld, bool IsInFight, long MapId);
public readonly record struct HuntLocation(bool KnownMap, (int X, int Y)? WorldPosition);
public readonly record struct HuntAlignment(int Side, int? EnemySide, bool Pvp, int HonourGrade, int Honour, int Dishonour)
{
    public bool Sworn => EnemySide.HasValue;
    public bool PvpOn => Sworn && Pvp;
}

public enum HuntActivity { Fight, Koliseo, Merkasako, Dungeon, House }
public readonly record struct HuntCombatant(long Id, int Level);

public interface IHuntGameplay
{
    bool HuntedNow(long characterId);
    Task<bool> HoldsAsync(long characterId, HuntActivity activity, string language, Func<string, Task>? notify);
    Task<bool> RefusesFightAsync(long characterId, long otherId, string language, Func<string, Task> notify);
    void AlignmentChanged(long characterId);
    void CharacterDeleted(long characterId);
    Task AggressionEndedAsync(IReadOnlyList<HuntCombatant> winners, IReadOnlyList<HuntCombatant> losers);
    bool Pay(long characterId, int amount);
    Task<bool> TryTakeOnlineOrderAsync(long characterId, int level, bool inWorld, Func<int, int> choose);
    long? OpenTarget(long characterId);
}
