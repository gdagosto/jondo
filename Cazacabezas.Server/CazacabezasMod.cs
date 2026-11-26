using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.ServerMods;
using Jondo.Unity.Launcher;

namespace Cazacabezas.Server;

public sealed class CazacabezasMod : IServerMod
{
    public void Initialize(IServerModHost host)
    {
        var hunts = (ICazacabezasHost)host;
        Register("status", (hunter, _) => new(200, hunter.StatusJson()));
        Register("board", (hunter, _) => new(200, hunter.BoardJson()));
        Register("take", Take);
        Register("abandon", (hunter, _) => hunter.Abandon()
            ? new(200, """{"abandoned":true}""")
            : Conflict("none"));

        void Register(string name, Func<ICazacabezasHunter, string, ServerModControlResponse> handle)
        {
            host.RegisterAuthenticatedControlRoute(Contract.Prefijo + "headhunts/" + name, request =>
            {
                var hunter = hunts.FindHunter(request.AccountId);
                return hunter == null ? Conflict("world") : handle(hunter, request.Body);
            });
        }
    }

    private static ServerModControlResponse Take(ICazacabezasHunter hunter, string body)
    {
        long target = ReadTarget(body);
        string result = hunter.Take(target);
        return result == "ok"
            ? new(200, new JsonObject { ["taken"] = target }.ToJsonString())
            : Conflict(result);
    }

    private static long ReadTarget(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("target", out var target) &&
                target.ValueKind == JsonValueKind.Number && target.TryGetInt64(out long id))
                return id;
        }
        catch (JsonException) { }
        return 0;
    }

    private static ServerModControlResponse Conflict(string error)
        => new(409, new JsonObject { ["error"] = error }.ToJsonString());
}
