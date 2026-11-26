using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups(IHeroesWorld world, Action<string> log) : IHeroesGameplay
{
    private sealed class Member(IHeroSession master, IHeroSession session) : IHeroMember
    {
        public IHeroSession Owner { get; } = master;
        public long CharacterId => Session.CharacterId;
        public long MasterId { get; set; } = master.CharacterId;
        public IHeroSession Session { get; } = session;
        public bool AutoPass { get; set; }
    }

    private sealed record Group(long MasterId, (long Id, bool AutoPass)[] Heroes, long LootTo, DateTime LeftAt);

    private readonly object _gate = new();
    private readonly Dictionary<long, List<Member>> _byMaster = new();
    private readonly Dictionary<long, Member> _byHero = new();
    private readonly Dictionary<long, long> _lootTo = new();
    private readonly HashSet<long> _questSyncOff = new();
    private readonly HashSet<long> _partyHeld = new();
    private readonly HashSet<long> _transitioning = new();
    private readonly Dictionary<long, (long Hero, DateTime At)> _switchTo = new();
    private readonly Dictionary<long, Group> _leftBehind = new();
    private bool _stopping;
    private static readonly TimeSpan SwitchWaits = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GroupWaits = TimeSpan.FromMinutes(3);

    public int Max => Math.Clamp(world.MaxHeroes, 1, 7);

    public IReadOnlyList<IHeroMember> Of(long masterId)
    {
        lock (_gate) return _byMaster.TryGetValue(masterId, out var list) ? list.ToArray() : [];
    }

    public IHeroMember? Find(long characterId)
    {
        lock (_gate) return _byHero.GetValueOrDefault(characterId);
    }

    public IHeroMember? Named(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
            return _byHero.Values.FirstOrDefault(h => string.Equals(h.Session.CharacterName, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public long LootTo(long masterId)
    {
        lock (_gate) return _lootTo.GetValueOrDefault(masterId);
    }

    public bool SetLootTo(long masterId, long characterId)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            if (characterId == 0 || LootTo(masterId) == characterId)
                _lootTo.Remove(masterId);
            else if (characterId == masterId || Find(characterId)?.MasterId == masterId)
                _lootTo[masterId] = characterId;
            else
                return false;
            return true;
        }
    }

    public bool SetAutoPass(long masterId, long heroId, bool on)
    {
        lock (_gate)
        {
            if (!_byHero.TryGetValue(heroId, out var hero) || hero.MasterId != masterId) return false;
            hero.AutoPass = on;
            return true;
        }
    }

    public bool SharesQuests(long characterId)
    {
        lock (_gate) return !_questSyncOff.Contains(characterId);
    }

    public bool SetQuestSync(long masterId, long heroId, bool on)
    {
        lock (_gate)
        {
            if (Find(heroId)?.MasterId != masterId) return false;
            if (on) _questSyncOff.Remove(heroId);
            else _questSyncOff.Add(heroId);
            return true;
        }
    }

    public string Connect(IHeroSession master, long heroId)
    {
        long masterId = master.CharacterId;
        if (heroId == masterId) return "self";
        if (master.IsInFight) return "fight";

        Member hero;
        lock (_gate)
        {
            if (_stopping) return "off";
            if (!master.IsInWorld || _byHero.ContainsKey(masterId)) return "world";
            if (!world.OwnsCharacter(master, heroId)) return "account";
            if (_transitioning.Contains(heroId) || world.IsClientOnline(heroId)) return "online";
            if (_byHero.ContainsKey(heroId)) return "already";
            _byMaster.TryGetValue(masterId, out var list);
            if (list != null)
            {
                if (list[0].Owner != master) return "world";
                if (list.Count >= Max) return "full";
            }

            var session = world.LoadFollower(master, heroId);
            if (session == null) return "missing";
            hero = new Member(master, session);
            if (list == null) _byMaster[masterId] = list = [];
            list.Add(hero);
            _byHero.Add(heroId, hero);
            _transitioning.Add(heroId);
        }

        try
        {
            log($"{master.CharacterName} connects {hero.Session.CharacterName} ({heroId}) as a hero.");
            if (master.HasClient) Run(() => ArriveAsync(master, hero), heroId, "appear");
            ChangeParty(() => world.JoinMasterPartyAsync(master, heroId), heroId);
            world.NotifyPresence(heroId);
            return "ok";
        }
        finally { EndTransition(heroId); }
    }

    public bool Disconnect(long masterId, long heroId)
    {
        Member hero;
        lock (_gate)
        {
            if (_transitioning.Contains(heroId) || !_byHero.TryGetValue(heroId, out var found)
                || found.MasterId != masterId || found.Session.IsInFight)
                return false;
            hero = found;
            Detach(hero);
        }
        SaveDetached(hero);
        return true;
    }

    public bool SelectCharacter(long characterId, Func<bool> load)
    {
        Member? hero;
        lock (_gate)
        {
            // Selection reads the saved sheet. Wait for an activation or a save of this
            // character to finish before letting the real session load it.
            while (_transitioning.Contains(characterId)) Monitor.Wait(_gate);
            _transitioning.Add(characterId);
            _byHero.TryGetValue(characterId, out hero);
            if (hero != null) Detach(hero);
        }
        try
        {
            if (hero != null) Save(hero);
            return load();
        }
        finally { EndTransition(characterId); }
    }

    private void EndTransition(long characterId)
    {
        lock (_gate)
        {
            _transitioning.Remove(characterId);
            Monitor.PulseAll(_gate);
        }
    }

    public void MasterLeft(IHeroSession master) => DisconnectGroup(master.CharacterId, master);

    public void DisconnectAll(long masterId) => DisconnectGroup(masterId, null);

    private void DisconnectGroup(long masterId, IHeroSession? owner)
    {
        Member[] gone;
        lock (_gate)
        {
            while (true)
            {
                if (!_byMaster.TryGetValue(masterId, out var list))
                {
                    _lootTo.Remove(masterId);
                    return;
                }
                // A late disconnect can arrive after a new connection regrouped on this character.
                if (owner != null && list[0].Owner != owner) return;
                if (!list.Any(h => _transitioning.Contains(h.CharacterId)))
                {
                    gone = list.ToArray();
                    break;
                }
                Monitor.Wait(_gate);
            }

            long lootTo = LootTo(masterId);
            _lootTo.Remove(masterId);
            if (!_stopping && owner is { AccountId: > 0 })
                _leftBehind[owner.AccountId] = new Group(masterId, gone.Select(h => (h.CharacterId, h.AutoPass)).ToArray(),
                    lootTo, DateTime.UtcNow);
            foreach (var hero in gone) Detach(hero);
        }
        foreach (var hero in gone) SaveDetached(hero);
    }

    public void Regroup(IHeroSession master)
    {
        Group? group;
        lock (_gate)
        {
            if (_stopping || !_leftBehind.Remove(master.AccountId, out group)) return;
        }
        if (DateTime.UtcNow - group.LeftAt > GroupWaits || master.IsInFight) return;
        long id = master.CharacterId;
        foreach (var (member, autoPass) in group.Heroes.Prepend((group.MasterId, false)))
            if (member != id && Connect(master, member) == "ok" && autoPass) SetAutoPass(id, member, true);
        if (group.LootTo != 0) SetLootTo(id, group.LootTo);
    }

    public bool AskSwitch(IHeroSession master, long heroId)
    {
        lock (_gate)
        {
            var hero = Find(heroId);
            if (_stopping || !master.IsInWorld || hero?.MasterId != master.CharacterId ||
                master.IsInFight || hero.Session.IsInFight) return false;
            _switchTo[master.AccountId] = (heroId, DateTime.UtcNow);
            return true;
        }
    }

    public bool SwitchPending(long accountId)
    {
        lock (_gate)
        {
            if (!_switchTo.TryGetValue(accountId, out var ask)) return false;
            if (DateTime.UtcNow - ask.At <= SwitchWaits) return true;
            _switchTo.Remove(accountId);
            return false;
        }
    }

    public long TakeSwitch(long accountId)
    {
        lock (_gate)
            return _switchTo.Remove(accountId, out var ask) && DateTime.UtcNow - ask.At <= SwitchWaits ? ask.Hero : 0;
    }

    private void Detach(Member hero)
    {
        _transitioning.Add(hero.CharacterId);
        long masterId = hero.MasterId;
        _byHero.Remove(hero.CharacterId);
        if (LootTo(masterId) == hero.CharacterId) _lootTo.Remove(masterId);
        var list = _byMaster[masterId];
        list.Remove(hero);
        if (list.Count == 0) _byMaster.Remove(masterId);
    }

    private void SaveDetached(Member hero)
    {
        try { Save(hero); }
        finally { EndTransition(hero.CharacterId); }
    }

    private void Save(Member hero)
    {
        long id = hero.CharacterId;
        Run(() => world.RemoveActorAsync(hero.Session.Position.MapId, id), id, "leave the map");
        ChangeParty(() => world.RemovePartyMemberAsync(id), id);
        try { world.SaveFollower(hero.Session); }
        catch (Exception ex) { log($"{id} could not save: {ex.Message}"); }
        world.NotifyPresence(id);
    }

    private void ChangeParty(Func<Task> change, long heroId)
    {
        lock (_gate)
            if (_partyHeld.Contains(heroId)) return;
        Run(change, heroId, "update the party");
    }

    private void Run(Func<Task> operation, long heroId, string action)
    {
        try { operation().GetAwaiter().GetResult(); }
        catch (Exception ex) { log($"{heroId} could not {action}: {ex.Message}"); }
    }

    public void Stop()
    {
        Member[] gone;
        lock (_gate)
        {
            _stopping = true;
            while (_transitioning.Count > 0) Monitor.Wait(_gate);
            gone = _byHero.Values.ToArray();
            foreach (var hero in gone) Detach(hero);
            _lootTo.Clear();
            _partyHeld.Clear();
            _questSyncOff.Clear();
            _switchTo.Clear();
            _leftBehind.Clear();
        }
        foreach (var hero in gone) SaveDetached(hero);
    }
}
