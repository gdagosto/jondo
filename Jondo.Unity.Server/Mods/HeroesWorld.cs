using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Mods;

internal sealed class HeroesWorld : IHeroesWorld
{
    public int MaxHeroes => ServerSettings.Current.HeroesMax;
    public bool ReloadMapOnSwitch => ServerSettings.Current.HeroSwitchReloadsMap;

    public IHeroSession? FindMaster(long accountId) => ControlApi.InTheWorld(accountId);

    public bool OwnsCharacter(IHeroSession master, long characterId)
        => DatabaseManager.CharacterBelongsToAccount(characterId, master.AccountId);

    public bool IsClientOnline(long characterId) => SessionRegistry.FindByCharacter(characterId) != null;

    public IHeroSession? LoadFollower(IHeroSession master, long characterId)
    {
        var owner = (GameSession)master;
        var session = GameSession.SinSocket();
        session.BindAccount(owner.AccountId, owner.ServerId, owner.State.Language);
        using var context = SessionContext.Push(session);
        return CharacterSelectionHandler.LoadForHero(characterId) ? session : null;
    }

    public void SaveFollower(IHeroSession follower)
    {
        var session = (GameSession)follower;
        using var context = SessionContext.Push(session);
        session.State.IsInFight = false;
        DatabaseManager.SaveCurrentCharacter();
    }

    public void NotifyPresence(long characterId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GuildHandler.TellConnectionAsync(characterId, SessionRegistry.IsOnline(characterId));
                await AllianceHandler.HeroConnectionAsync(characterId);
            }
            catch (Exception ex) { Program.LogDebug($"[Heroes] {characterId}'s guild and alliance not told: {ex.Message}"); }
        });
    }

    public async Task RemoveMasterForSwitchAsync(IHeroSession master, bool reloadMap)
    {
        var session = (GameSession)master;
        using var context = SessionContext.Push(session);
        DatabaseManager.SaveCurrentCharacter();
        await MapPresenceService.Live.RemoveAsync(session.Map, session.CharacterId, reloadMap ? session.Id : Guid.Empty);
    }

    public async Task<bool> SelectMasterAsync(IHeroSession master, long heroId, HeroPosition position, bool reload)
    {
        var session = (GameSession)master;
        using var context = SessionContext.Push(session);
        var backend = new GameEntryBackend();
        long formerId = session.CharacterId;
        if (!backend.SelectCharacter(session, heroId)) return false;

        var state = session.State;
        // Where the master stood: a hero is at his side, not where he was last saved.
        state.MapId = position.MapId;
        state.CellId = position.CellId;
        state.Orientation = position.Orientation;
        var character = backend.FindCharacter(heroId);
        if (character == null) return false;
        backend.LoadCharacterState(session, character);
        DatabaseManager.SaveCurrentCharacter();

        if (!reload) ZaapDiscoveries.Discover(heroId, position.MapId);
        await backend.SendInitialStateAsync(session, character);
        await backend.AnnounceMapPresenceAsync(session, character);
        // The map is not loaded again: the actors on it are only
        // told anew -- the one played before gone, the one played now drawn where he stood --,
        // which is what makes a switch quick. The whole map again when the setting asks for it.
        if (reload) await backend.SendMapInitializationAsync(session, character);
        else
            await session.SendAsync(MapActorMessages.Refreshed(character, position.CellId, position.Orientation, session.AccountId));
        // His look told as a change of gear tells it: an inventory left open kept drawing the
        // one played before over the new one's bag.
        await session.SendAsync(ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
        // His journal, the quests he follows, the NPCs' marks his quests put on this map and his
        // achievements: the map's block sends them, and with no map loaded again the client kept
        // showing the journal and the achievements of the one played before until the next login.
        if (!reload)
        {
            await Quests.SendSwitchedJournalAsync(session, formerId);
            await Quests.SendMarksAsync(session, position.MapId);
            await Achievements.SendListAsync(session);
        }

        // The wardrobe's draft belongs to the one played before. His wardrobe, spells and bar
        // come with the map's block; with no map loaded again they are sent here, or the
        // client keeps the other hero's outfits and spells. His look went just above.
        session.State.IsWardrobeDraftLoaded = false;
        if (!reload)
        {
            await WardrobeStateService.Live.SendOwnedAsync(session);
            await session.SendAsync(SpellMessages.Live.Snapshot(session));
            await ShortcutBarMessages.Live.SendSpellAsync(session);
        }
        return true;
    }

    public int? PartyOf(long characterId) => Parties.Of(characterId)?.Id;
    public bool IsInParty(long characterId) => Parties.IsInParty(characterId);
    public Task JoinMasterPartyAsync(IHeroSession master, long followerId)
        => PartyHandler.HeroJoinedAsync((GameSession)master, followerId);
    public Task RemovePartyMemberAsync(long characterId) => PartyHandler.HeroLeftAsync(characterId);
    public Task RefreshPartyAfterSwitchAsync(IHeroSession master, long formerId, IReadOnlyCollection<long> held)
        => PartyHandler.SwitchedAsync((GameSession)master, formerId, held);

    public IReadOnlyCollection<int>? WalkableCells(long mapId)
        => MapManager.WalkableCells.GetValueOrDefault(mapId);
    public int CellDistance(int from, int to) => MapGeometry.Distance(from, to);
    public IReadOnlyList<int> FindPath(int from, int to, HashSet<int> walkable)
        => MapGeometry.FindShortestPath(from, to, walkable);

    public async Task DrawFollowerAsync(IHeroSession master, IHeroSession follower)
    {
        var position = follower.Position;
        var character = DatabaseManager.GetCharacterById(follower.CharacterId);
        if (character == null || follower.IsInFight || master.IsInFight) return;
        try
        {
            await SessionRegistry.BroadcastToMapAsync(position.MapId,
                MapActorMessages.Refreshed(character, position.CellId, position.Orientation, master.AccountId), Guid.Empty);
        }
        catch (Exception ex) { Program.LogDebug($"[Heroes] {follower.CharacterId} could not be drawn: {ex.Message}"); }
    }

    Task IHeroesWorld.RemoveActorAsync(long mapId, long characterId) => RemoveActorAsync(mapId, characterId);

    internal static async Task RemoveActorAsync(long mapId, long characterId)
    {
        if (mapId <= 0) return;
        try { await MapPresenceService.Live.RemoveAsync(SessionRegistry.OnMap(mapId), characterId); }
        catch (Exception ex) { Program.LogDebug($"[Heroes] {characterId} could not be taken off map {mapId}: {ex.Message}"); }
    }

    public Task WalkFollowerAsync(IHeroSession master, long characterId, IReadOnlyList<int> path)
        => ((GameSession)master).Map.SendAsync(new GameMapMovementEvent(path, characterId, false));

    public void DiscoverZaap(long characterId, long mapId) => ZaapDiscoveries.Discover(characterId, mapId);

    public Task UpdateAchievementsAsync(IHeroSession master, IHeroSession follower)
    {
        long map = master.Position.MapId;
        return Achievements.ForHeroAsync((GameSession)master, (GameSession)follower, map,
            MapManager.GetMapInfo(map)?.SubAreaId ?? 0);
    }
}
