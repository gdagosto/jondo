using System.Net.Sockets;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Mods;

internal static class HuntGameplay
{
    private static IHuntGameplay? _registered;
    internal static IHuntGameplay? Current => Volatile.Read(ref _registered);

    internal static Action Register(IHuntGameplay gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        if (Interlocked.CompareExchange(ref _registered, gameplay, null) != null)
            throw new InvalidOperationException("Hunt gameplay is already registered.");
        return () => Interlocked.CompareExchange(ref _registered, null, gameplay);
    }

    internal static bool HuntedNow(long characterId) => Current?.HuntedNow(characterId) == true;

    internal static Task<bool> HoldsAsync(NetworkStream? stream, HuntActivity activity)
        => Current?.HoldsAsync(SessionContext.State.CharacterId, activity, SessionContext.State.Language,
            stream == null ? null : text => WriteNoticeAsync(stream, text)) ?? Task.FromResult(false);

    internal static Task<bool> RefusesFightAsync(NetworkStream stream, long characterId, long otherId)
        => Current?.RefusesFightAsync(characterId, otherId, SessionContext.State.Language,
            text => WriteNoticeAsync(stream, text)) ?? Task.FromResult(false);

    internal static Task AggressionEndedAsync(FightInstance fight, bool attackersWon)
        => Current?.AggressionEndedAsync(People(attackersWon ? fight.Azul : fight.Rojo),
            People(attackersWon ? fight.Rojo : fight.Azul)) ?? Task.CompletedTask;

    private static HuntCombatant[] People(IEnumerable<Fighter> side)
        => side.Where(f => !f.IsMonster && !f.EsInvocado && f.Id > 0)
            .Select(f => new HuntCombatant(f.Id, f.Level)).ToArray();

    private static async Task WriteNoticeAsync(NetworkStream stream, string text)
        => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
            ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
}
