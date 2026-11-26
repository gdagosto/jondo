using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups
{
    public async Task<string> SwitchAsync(IHeroSession master, long heroId)
    {
        using var turn = await master.EnterTurnAsync();
        long formerId = master.CharacterId;
        if (!master.IsInWorld || Find(heroId) is not { } hero || hero.MasterId != formerId)
            return "hero";
        if (master.IsInFight || hero.Session.IsInFight) return "fight";
        if (!world.OwnsCharacter(master, heroId)) return "account";

        var group = Of(formerId);
        long lootTo = LootTo(formerId);
        var position = master.Position;
        bool reload = world.ReloadMapOnSwitch;
        long[] held = world.IsInParty(formerId)
            ? group.Select(h => h.CharacterId).Prepend(formerId).ToArray()
            : [];
        lock (_gate) _partyHeld.UnionWith(held);
        int place = -1;
        try
        {
            await world.RemoveMasterForSwitchAsync(master, reload);
            place = HandOver(formerId, heroId);
            if (place < 0 || !await world.SelectMasterAsync(master, heroId, position, reload))
            {
                if (place >= 0 && master.CharacterId == formerId)
                {
                    lock (_gate)
                        if (_byMaster.TryGetValue(heroId, out var remaining)) Reassign(heroId, formerId, remaining);
                    if (Connect(master, heroId) == "ok")
                    {
                        PlaceAt(formerId, heroId, group.Count - 1 - place);
                        SetAutoPass(formerId, heroId, hero.AutoPass);
                    }
                    if (lootTo != 0) SetLootTo(formerId, lootTo);
                }
                await world.DrawFollowerAsync(master, master);
                return "hero";
            }

            return "ok";
        }
        finally
        {
            try
            {
                // Complete the group handover even if sending initial state to the client fails.
                if (place >= 0 && master.CharacterId == heroId)
                {
                    if (Connect(master, formerId) == "ok") PlaceAt(heroId, formerId, place);
                    if (lootTo != 0) SetLootTo(heroId, lootTo);
                }
            }
            finally { lock (_gate) _partyHeld.ExceptWith(held); }
            await world.RefreshPartyAfterSwitchAsync(master, formerId, held);
        }
    }

    private int HandOver(long masterId, long heroId)
    {
        Member taken;
        int place;
        lock (_gate)
        {
            if (_transitioning.Contains(heroId) || !_byHero.TryGetValue(heroId, out var hero)
                || hero.MasterId != masterId) return -1;
            taken = hero;
            var list = _byMaster[masterId];
            int at = list.IndexOf(taken);
            var others = list.Skip(at + 1).Concat(list.Take(at)).ToList();
            place = list.Count - 1 - at;
            _lootTo.Remove(masterId);
            _byHero.Remove(heroId);
            _transitioning.Add(heroId);
            Reassign(masterId, heroId, others);
        }

        SaveDetached(taken);
        return place;
    }

    private void Reassign(long from, long to, List<Member> members)
    {
        _byMaster.Remove(from);
        foreach (var other in members) other.MasterId = to;
        if (members.Count > 0) _byMaster[to] = members;
    }

    private void PlaceAt(long masterId, long heroId, int place)
    {
        lock (_gate)
        {
            if (!_byHero.TryGetValue(heroId, out var hero) || hero.MasterId != masterId) return;
            var list = _byMaster[masterId];
            list.Remove(hero);
            list.Insert(Math.Clamp(place, 0, list.Count), hero);
        }
    }

    public async Task LeavePartyAsync(int partyId, long masterId)
    {
        foreach (var hero in Of(masterId))
            if (world.PartyOf(hero.CharacterId) == partyId)
                await world.RemovePartyMemberAsync(hero.CharacterId);
    }

    public async Task JoinPartyAsync(IHeroSession master)
    {
        foreach (var hero in Of(master.CharacterId))
        {
            int? party = world.PartyOf(hero.CharacterId);
            if (party == null || party != world.PartyOf(master.CharacterId))
            {
                if (party != null) await world.RemovePartyMemberAsync(hero.CharacterId);
                await world.JoinMasterPartyAsync(master, hero.CharacterId);
            }
        }
    }

    private async Task ArriveAsync(IHeroSession master, IHeroMember hero)
    {
        var position = master.Position;
        var cells = world.WalkableCells(position.MapId);
        if (cells != null)
        {
            var taken = Of(master.CharacterId)
                .Where(h => h.CharacterId != hero.CharacterId && h.Session.Position.MapId == position.MapId)
                .Select(h => h.Session.Position.CellId).ToHashSet();
            taken.Add(position.CellId);
            position = position with { CellId = NearestFree(position.CellId, cells, taken) };
        }
        hero.Session.Position = position;
        world.DiscoverZaap(hero.CharacterId, position.MapId);
        await world.DrawFollowerAsync(master, hero.Session);
    }

    public Task MasterMovedAsync(IHeroSession master, long mapLeft)
        => MoveFollowersAsync(master, mapLeft, Of(master.CharacterId).Where(hero => !hero.Session.IsInFight));

    private async Task MoveFollowersAsync(IHeroSession master, long mapLeft, IEnumerable<IHeroMember> followers)
    {
        foreach (var hero in followers)
        {
            if (mapLeft > 0 && mapLeft != master.Position.MapId)
                await world.RemoveActorAsync(mapLeft, hero.CharacterId);
            await ArriveAsync(master, hero);
            try { await world.UpdateAchievementsAsync(master, hero.Session); }
            catch (Exception ex) { log($"{hero.CharacterId}'s achievements could not be updated: {ex.Message}"); }
        }
    }

    public async Task MasterWalkedAsync(IHeroSession master, IReadOnlyList<int> cells)
    {
        if (cells.Count == 0) return;
        var position = master.Position;
        var cellsOfMap = world.WalkableCells(position.MapId);
        if (cellsOfMap == null) return;
        var walkable = new HashSet<int>(cellsOfMap);
        int end = cells[^1];
        var taken = new HashSet<int> { end };
        foreach (var hero in Of(master.CharacterId))
        {
            var current = hero.Session.Position;
            if (hero.Session.IsInFight || current.MapId != position.MapId) continue;
            int target = NearestFree(end, walkable, taken);
            taken.Add(target);
            if (target == current.CellId) continue;
            var path = world.FindPath(current.CellId, target, walkable);
            if (path.Count < 2) path = [current.CellId, target];
            hero.Session.Position = position with { CellId = target };
            try { await world.WalkFollowerAsync(master, hero.CharacterId, path); }
            catch (Exception ex) { log($"{hero.CharacterId} could not follow: {ex.Message}"); }
        }
    }

    private int NearestFree(int cell, IEnumerable<int> walkable, HashSet<int> taken)
    {
        int best = cell, bestDistance = int.MaxValue;
        foreach (int candidate in walkable)
        {
            if (taken.Contains(candidate)) continue;
            int distance = world.CellDistance(cell, candidate);
            if (distance < 1 || distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }
}
