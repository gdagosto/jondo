using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal static class FightParticipants
{
    private static IFightParticipantPolicy? _policy;
    internal static IFightParticipantPolicy? Policy => Volatile.Read(ref _policy);

    internal static Action Register(IFightParticipantPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (Interlocked.CompareExchange(ref _policy, policy, null) != null)
            throw new InvalidOperationException("A fight participant policy is already registered.");
        return () => Interlocked.CompareExchange(ref _policy, null, policy);
    }
}
