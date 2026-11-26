namespace Jondo.Unity.ServerMods;

/// <summary>Temporary access to the host's hunt implementation during the Cazacabezas migration.</summary>
public interface ICazacabezasHost
{
    ICazacabezasHunter? FindHunter(long accountId);
}

public interface ICazacabezasHunter
{
    string StatusJson();
    string BoardJson();
    string Take(long target);
    bool Abandon();
}
