namespace Jondo.Unity.ServerMods;

public static class ServerModApi
{
    public const int Version = 1;
}

public interface IServerMod
{
    void Initialize(IServerModHost host);

    /// <summary>Stops a successfully initialized mod while the server is still running.</summary>
    void Shutdown() { }
}

public interface IServerModHost
{
    void Log(string message);

    /// <summary>Registers cleanup for a resource, including when initialization fails.</summary>
    void RegisterCleanup(Action cleanup);
}
