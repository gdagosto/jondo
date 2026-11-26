namespace Jondo.Unity.ServerMods;

/// <summary>Temporary bridge to the host's legacy Heroes control handlers.</summary>
public interface IHeroesControl
{
    ServerModControlResponse Handle(HeroesControlOperation operation, AuthenticatedControlRequest request);
}

public enum HeroesControlOperation
{
    List = 0,
    Portrait = 3,
    Quests = 4,
    Sheet = 7,
    Create = 9,
    Name = 10,
    Preview = 11,
}
