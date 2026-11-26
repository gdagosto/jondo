namespace Jondo.Unity.ServerMods;

/// <summary>Collects the Heroes roster and proposes payment before the host commits dungeon entry.</summary>
public interface IHeroesDungeonAdmission
{
    IReadOnlyList<IHeroMember> DungeonFollowers(IHeroSession master);
    DungeonAdmissionPlan PlanDungeonAdmission(DungeonAdmissionRequest request);
    Task MoveDungeonFollowersAsync(IHeroSession master, long mapLeft, IReadOnlyList<IHeroMember> admitted);
}

public readonly record struct DungeonFollowerOptions(long CharacterId, int Keys, bool HasKeyring, bool WantsKey);

/// <summary>The master's key count excludes the item already reserved for their own entry.</summary>
public sealed record DungeonAdmissionRequest(long MasterId, int MasterKeys, bool RequiresKey,
    IReadOnlyList<DungeonFollowerOptions> Followers);

public enum DungeonFollowerEntry { Free, Keyring, Key }

public readonly record struct DungeonFollowerAdmission(long CharacterId, DungeonFollowerEntry Entry, long PayerId = 0);

/// <summary>A positive shortfall rejects the entire plan.</summary>
public sealed record DungeonAdmissionPlan(IReadOnlyList<DungeonFollowerAdmission> Followers, int MissingKeys = 0);
