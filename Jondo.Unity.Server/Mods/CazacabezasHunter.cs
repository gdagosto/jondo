using System;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods
{
    /// <summary>Temporary adapter to the host hunt implementation for one in-world character.</summary>
    internal sealed class CazacabezasHunter(GameSession me) : ICazacabezasHunter
    {
        internal static CazacabezasHunter? Find(long accountId)
            => ControlApi.InTheWorld(accountId) is { } session ? new CazacabezasHunter(session) : null;

        public string StatusJson()
        {
            long id = me.State.CharacterId;
            int level = me.State.CharacterLevel;
            var alignment = Alignments.Of(id);
            var record = HeadHunts.Of(id);
            var order = HeadHunts.OrderOf(id);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            object? hunt = null;
            if (order != null)
            {
                var prey = DatabaseManager.GetCharacterById(order.Target);
                var preySession = SessionRegistry.FindByCharacter(order.Target);
                var seen = order.Open(now) ? HeadHunts.SeenAt(order, preySession) : null;
                hunt = new
                {
                    target = order.Target,
                    name = prey?.Name ?? "",
                    level = prey?.Level ?? 0,
                    breed = prey?.Breed ?? 0,
                    grade = Math.Max(1, Alignments.Of(order.Target).HonourGrade),
                    online = preySession != null && preySession.IsInWorld,
                    open = order.Open(now),
                    openSeconds = order.Open(now) ? (int)((order.OpenUntilMs - now) / 1000) : 0,
                    restSeconds = order.RestUntilMs > now ? (int)((order.RestUntilMs - now) / 1000) : 0,
                    expiresSeconds = (int)Math.Max(0, (order.TakenMs + (long)HeadHunts.OrderLife.TotalMilliseconds - now) / 1000),
                    seenX = seen?.X,
                    seenY = seen?.Y,
                    bounty = HeadHunts.Bounty(order.Target, prey?.Level ?? 1, level),
                };
            }

            var onMe = HeadHunts.OrdersOn(id);
            return JsonSerializer.Serialize(new
            {
                on = HeadHunts.On,
                character = me.State.CharacterName,
                level,
                side = alignment.Side,
                pvp = alignment.PvpOn,
                grade = alignment.HonourGrade,
                honour = alignment.Honour,
                dishonour = alignment.Dishonour,
                chapeuvepes = record.Chapeuvepes,
                kills = record.Kills,
                survivals = record.Survivals,
                failures = record.Failures,
                streak = record.Streak,
                lockedSeconds = record.LockedUntilMs > now ? (int)((record.LockedUntilMs - now) / 1000) : 0,
                cannot = HeadHunts.CannotHunt(id, level),
                order = hunt,
                huntedBy = onMe.Count,
                hunted = onMe.Any(o => o.Open(now)),
            });
        }

        public string BoardJson()
        {
            var board = HeadHunts.CandidatesFor(me.State.CharacterId, me.State.CharacterLevel);
            return JsonSerializer.Serialize(new
            {
                prey = board.Select(c => new
                {
                    id = c.Id, name = c.Name, level = c.Level, breed = c.Breed, grade = c.Grade,
                    online = c.Online, bounty = c.Bounty, hunters = c.Hunters,
                }),
            });
        }

        public string Take(long target)
        {
            using var context = SessionContext.Push(me);
            return HeadHunts.TakeAsync(me.State.CharacterId, me.State.CharacterLevel, target).GetAwaiter().GetResult();
        }

        public bool Abandon() => HeadHunts.Abandon(me.State.CharacterId);
    }
}
