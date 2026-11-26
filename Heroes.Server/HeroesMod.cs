using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.ServerMods;

namespace Heroes.Server;

public sealed class HeroesMod : IServerMod
{
    private HeroGroups? _groups;

    public void Initialize(IServerModHost host)
    {
        var control = (IHeroesControl)host;
        var gameplay = (IHeroesModHost)host;
        var world = gameplay.HeroesWorld;
        var groups = _groups = new HeroGroups(world, host.Log);
        gameplay.RegisterHeroesGameplay(groups);

        Register("list", HeroesControlOperation.List);
        Register("portrait", HeroesControlOperation.Portrait);
        Register("quests", HeroesControlOperation.Quests);
        Register("autopass", HeroesControlOperation.AutoPass);
        Register("loot", HeroesControlOperation.Loot);
        Register("sheet", HeroesControlOperation.Sheet);
        Register("create", HeroesControlOperation.Create);
        Register("name", HeroesControlOperation.Name);
        Register("preview", HeroesControlOperation.Preview);
        RegisterLifecycle("connect", (master, id, _) =>
        {
            string result = groups.Connect(master, id);
            return result == "ok" ? Reply(200, new { connected = true }) : Error(result);
        });
        RegisterLifecycle("disconnect", (master, id, _) =>
            groups.Disconnect(master.CharacterId, id) ? Reply(200, new { disconnected = true }) : Error("none"));
        RegisterLifecycle("switch", (master, id, later) =>
        {
            if (later)
                return groups.AskSwitch(master, id) ? Reply(200, new { switching = true }) : Error("none");
            string result = groups.SwitchAsync(master, id).GetAwaiter().GetResult();
            return result == "ok" ? Reply(200, new { switched = true }) : Error(result);
        }, takeTurn: false);

        void Register(string name, HeroesControlOperation operation)
            => host.RegisterAuthenticatedControlRoute(Path(name), request => control.Handle(operation, request));

        void RegisterLifecycle(string name, Func<IHeroSession, long, bool, ServerModControlResponse> handle,
            bool takeTurn = true)
            => host.RegisterAuthenticatedControlRoute(Path(name), request =>
            {
                var master = world.FindMaster(request.AccountId);
                if (master == null) return Error("world");
                long id = Number(request.Body, "id");
                bool later = name == "switch" && Number(request.Body, "later") != 0;
                using var turn = takeTurn || later ? master.EnterTurnAsync().GetAwaiter().GetResult() : null;
                return master.IsInWorld ? handle(master, id, later) : Error("world");
            });
    }

    public void Shutdown() => _groups?.Stop();

    private static string Path(string route) => Contract.Prefijo + "heroes/" + route;
    private static ServerModControlResponse Reply(int status, object data) => new(status, JsonSerializer.Serialize(data));
    private static ServerModControlResponse Error(string reason) => Reply(409, new { error = reason });

    private static long Number(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
        }
        catch (JsonException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
