namespace Jondo.Unity.ServerMods;

/// <summary>The host's live session. Normal character state remains in the host.</summary>
public interface IHeroSession
{
    long CharacterId { get; }
    long AccountId { get; }
    string CharacterName { get; }
    bool IsInWorld { get; }
    bool IsInFight { get; }
    bool HasClient { get; }
    HeroPosition Position { get; set; }
    Task<IDisposable> EnterTurnAsync();
}

public readonly record struct HeroPosition(long MapId, int CellId, int Orientation);

public interface IHeroMember
{
    long CharacterId { get; }
    long MasterId { get; }
    IHeroSession Session { get; }
    bool AutoPass { get; }
}

/// <summary>Read-through access for host features using the package's active group.</summary>
public interface IHeroGroups
{
    IReadOnlyList<IHeroMember> Of(long masterId);
    IHeroMember? Find(long characterId);
    IHeroMember? Named(string name);
    long LootTo(long masterId);
    bool SharesQuests(long characterId);
}

public interface IHeroesGameplay : IHeroGroups, IHeroesDungeonAdmission
{
    int Max { get; }
    string Connect(IHeroSession master, long heroId);
    bool Disconnect(long masterId, long heroId);
    void DisconnectAll(long masterId);
    void MasterLeft(IHeroSession master);
    bool SelectCharacter(long characterId, Func<bool> load);
    bool AskSwitch(IHeroSession master, long heroId);
    bool SwitchPending(long accountId);
    long TakeSwitch(long accountId);
    void Regroup(IHeroSession master);
    Task<string> SwitchAsync(IHeroSession master, long heroId);
    bool SetAutoPass(long masterId, long heroId, bool on);
    bool SetLootTo(long masterId, long characterId);
    bool SetQuestSync(long masterId, long heroId, bool on);
    Task LeavePartyAsync(int partyId, long masterId);
    Task JoinPartyAsync(IHeroSession master);
    Task MasterMovedAsync(IHeroSession master, long mapLeft);
    Task MasterWalkedAsync(IHeroSession master, IReadOnlyList<int> cells);
}

/// <summary>Character storage, real-session selection, party operations and map output.</summary>
public interface IHeroesWorld
{
    int MaxHeroes { get; }
    bool ReloadMapOnSwitch { get; }
    IHeroSession? FindMaster(long accountId);
    bool OwnsCharacter(IHeroSession master, long characterId);
    bool IsClientOnline(long characterId);
    IHeroSession? LoadFollower(IHeroSession master, long characterId);
    void SaveFollower(IHeroSession follower);
    void NotifyPresence(long characterId);
    Task RemoveMasterForSwitchAsync(IHeroSession master, bool reloadMap);
    Task<bool> SelectMasterAsync(IHeroSession master, long characterId, HeroPosition position, bool reloadMap);
    int? PartyOf(long characterId);
    bool IsInParty(long characterId);
    Task JoinMasterPartyAsync(IHeroSession master, long followerId);
    Task RemovePartyMemberAsync(long characterId);
    Task RefreshPartyAfterSwitchAsync(IHeroSession master, long formerId, IReadOnlyCollection<long> held);
    IReadOnlyCollection<int>? WalkableCells(long mapId);
    int CellDistance(int from, int to);
    IReadOnlyList<int> FindPath(int from, int to, HashSet<int> walkable);
    Task DrawFollowerAsync(IHeroSession master, IHeroSession follower);
    Task RemoveActorAsync(long mapId, long characterId);
    Task WalkFollowerAsync(IHeroSession master, long characterId, IReadOnlyList<int> path);
    void DiscoverZaap(long characterId, long mapId);
    Task UpdateAchievementsAsync(IHeroSession master, IHeroSession follower);
}

public interface IHeroesModHost
{
    IHeroesWorld HeroesWorld { get; }
    void RegisterHeroesGameplay(IHeroesGameplay gameplay);
}
