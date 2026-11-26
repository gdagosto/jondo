using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;

namespace Jondo.Unity.Server.Network
{
    internal static class HeroPresence
    {
        /// <summary>A hero drawn on a map: who, where, facing where, and his account.</summary>
        internal readonly record struct Shown(DbCharacter Character, int Cell, int Facing, long AccountId);

        /// <summary>The heroes standing on a map: those of the people on it, out of any fight.</summary>
        public static IEnumerable<Shown> On(long mapId)
        {
            foreach (var master in SessionRegistry.OnMap(mapId).ToList())
            {
                if (master.CharacterId <= 0 || master.State.IsInFight) continue;
                foreach (var hero in Heroes.Of(master.CharacterId))
                {
                    var state = hero.Session.State;
                    if (state.IsInFight || state.MapId != mapId) continue;
                    var character = DatabaseManager.GetCharacterById(hero.CharacterId);
                    if (character != null) yield return new Shown(character, state.CellId, state.Orientation, master.AccountId);
                }
            }
        }

        public static Task LeaveAsync(Heroes.Hero hero)
            => HeroesWorld.RemoveActorAsync(hero.Session.MapId, hero.CharacterId);

        public static Task MasterMovedAsync(GameSession master, long mapLeft)
            => Heroes.Gameplay?.MasterMovedAsync(master, mapLeft) ?? Task.CompletedTask;

        public static Task MasterWalkedAsync(GameSession master, IReadOnlyList<int> cells)
            => Heroes.Gameplay?.MasterWalkedAsync(master, cells) ?? Task.CompletedTask;
    }
}
