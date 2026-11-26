using System.Collections.Concurrent;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>Host callers' view of the Heroes package and the remaining quest integration.</summary>
    public static class Heroes
    {
        public const int Most = 7;

        private static IHeroesGameplay? _gameplay;
        internal static IHeroesGameplay? Gameplay => Volatile.Read(ref _gameplay);
        private static IHeroGroups? Groups => Gameplay;

        internal static Action Register(IHeroesGameplay gameplay)
        {
            ArgumentNullException.ThrowIfNull(gameplay);
            if (Interlocked.CompareExchange(ref _gameplay, gameplay, null) != null)
                throw new InvalidOperationException("Heroes gameplay is already registered.");
            return () =>
            {
                if (Interlocked.CompareExchange(ref _gameplay, null, gameplay) == gameplay)
                    _questLags.Clear();
            };
        }

        public sealed class Hero
        {
            private readonly IHeroMember _member;
            internal Hero(IHeroMember member) => _member = member;
            public long CharacterId => _member.CharacterId;
            public long MasterId => _member.MasterId;
            public GameSession Session => (GameSession)_member.Session;
            public bool AutoPass => _member.AutoPass;
        }

        public static bool On => Gameplay != null;
        public static int Max => Gameplay?.Max ?? Math.Clamp(ServerSettings.Current.HeroesMax, 1, Most);
        public static IReadOnlyList<Hero> Of(long masterId)
            => Groups?.Of(masterId).Select(member => new Hero(member)).ToArray() ?? [];
        public static Hero? AsHero(long characterId) => Wrap(Groups?.Find(characterId));
        public static Hero? Named(string name) => Wrap(Groups?.Named(name));
        private static Hero? Wrap(IHeroMember? member) => member == null ? null : new(member);

        public static GameSession? SessionOf(long characterId) => (GameSession?)Groups?.Find(characterId)?.Session;
        public static long LootTo(long masterId) => Groups?.LootTo(masterId) ?? 0;
        public static bool PassesOnHisOwn(long characterId) => Groups?.Find(characterId)?.AutoPass == true;
        internal static bool SharesQuests(long characterId) => Groups?.SharesQuests(characterId) ?? true;
        public static bool SetAutoPass(long masterId, long heroId, bool on) => Gameplay?.SetAutoPass(masterId, heroId, on) == true;
        public static bool SetLootTo(long masterId, long characterId) => Gameplay?.SetLootTo(masterId, characterId) == true;

        internal static bool SetQuestSync(long masterId, long heroId, bool on)
        {
            if (Gameplay?.SetQuestSync(masterId, heroId, on) != true) return false;
            if (!on) _questLags.TryRemove(heroId, out _);
            return true;
        }

        public static string Connect(GameSession master, long heroId) => Gameplay?.Connect(master, heroId) ?? "off";
        public static bool Disconnect(long masterId, long heroId) => Gameplay?.Disconnect(masterId, heroId) == true;
        public static void DisconnectAll(long masterId) => Gameplay?.DisconnectAll(masterId);
        public static void DisconnectAll(GameSession master) => Gameplay?.MasterLeft(master);
        internal static bool SelectCharacter(long characterId, Func<bool> load)
            => Gameplay?.SelectCharacter(characterId, load) ?? load();
        public static bool AskSwitch(GameSession master, long heroId) => Gameplay?.AskSwitch(master, heroId) == true;
        public static bool SwitchPending(long accountId) => Gameplay?.SwitchPending(accountId) == true;
        public static long TakeSwitch(long accountId) => Gameplay?.TakeSwitch(accountId) ?? 0;
        public static void Regroup(GameSession master) => Gameplay?.Regroup(master);

        // ─── A hero who did not follow ───────────────────────────────────────────────────

        /// <summary>Why a hero did not follow his master's step of a quest.</summary>
        public enum QuestLagReason
        {
            /// <summary>Not on his master's map.</summary>
            Elsewhere = 1,
            /// <summary>In a fight, or his master was.</summary>
            InFight = 2,
            /// <summary>Not among the winners of the fight that ticked it.</summary>
            NotInTheFight = 3,
            /// <summary>Without the items the objective takes.</summary>
            MissingItems = 4,
            /// <summary>The reply that did it is not shared: it takes items or kamas, or moves the master.</summary>
            NotShared = 5,
            /// <summary>He does not have the quest.</summary>
            NotStarted = 6,
            /// <summary>He was not on the same step.</summary>
            Behind = 7,
            /// <summary>He does not meet the quest's conditions: his level, a quest before it.</summary>
            Requirements = 8,
        }

        /// <summary>What stopped a hero, with the item and how many of it he lacks when that is it.</summary>
        public sealed record LagCause(QuestLagReason Reason, int ItemId = 0, int ItemCount = 0);

        /// <summary>A quest a hero stayed behind in, and why.</summary>
        public sealed record QuestLag(int QuestId, int StepId, QuestLagReason Reason, int ItemId, int ItemCount);

        /// <summary>Each hero's quests he is behind his master in, by quest.</summary>
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<int, QuestLag>> _questLags = new();

        /// <summary>
        /// The quests a hero is still behind his master in. Those he has caught up with since --
        /// by doing the step himself, or his master having gone back -- are let go as they are read.
        /// </summary>
        public static IReadOnlyList<QuestLag> LagsOf(GameSession master, long heroId)
        {
            if (!_questLags.TryGetValue(heroId, out var lags) || lags.IsEmpty) return Array.Empty<QuestLag>();
            var hero = AsHero(heroId);
            if (hero == null || hero.MasterId != master.CharacterId) return Array.Empty<QuestLag>();
            foreach (int quest in lags.Keys)
            {
                if (Quests.ProgressOf(hero.Session, quest) >= Quests.ProgressOf(master, quest))
                    lags.TryRemove(quest, out _);
            }
            return lags.Values.OrderBy(l => l.QuestId).ToList();
        }

        /// <summary>
        /// Does to each hero of the current character who shares quests what the master just did,
        /// then, for the quests named, tells the master which heroes stayed behind and why.
        /// </summary>
        /// <param name="apply">The master's action, done again in each hero's session.</param>
        /// <param name="participates">Who takes part; by default the heroes on the master's map, out of a fight.</param>
        /// <param name="quests">The quests the action moved for the master: a hero left behind in one is told.</param>
        /// <param name="explain">Why a hero who took part still did not follow, when the action knows better than «not on the same step».</param>
        /// <summary>A hero who could not start the quest because of its conditions, for <see cref="ShareQuestActionAsync"/>'s explain.</summary>
        internal static LagCause? CouldNotStart(GameSession hero, int questId)
            => hero.State.Quests is { } log && !log.Active(questId) && !log.Finished(questId) && !log.CanStart(questId, out _)
                ? new LagCause(QuestLagReason.Requirements)
                : null;

        internal static async Task ShareQuestActionAsync(Func<GameSession, Task> apply,
                                                         Func<Hero, bool>? participates = null,
                                                         IReadOnlyCollection<int>? quests = null,
                                                         Func<GameSession, LagCause?>? explain = null)
        {
            var master = SessionContext.Current;
            long masterId = master.CharacterId;
            if (!On || masterId == 0 || AsHero(masterId) != null) return;

            foreach (var hero in Of(masterId))
            {
                if (!SharesQuests(hero.CharacterId) || SessionOf(hero.CharacterId) != hero.Session || hero.MasterId != masterId)
                    continue;
                bool eligible = participates?.Invoke(hero)
                    ?? (hero.Session.MapId == master.MapId && !hero.Session.State.IsInFight && !master.State.IsInFight);

                LagCause? cause = null;
                if (!eligible)
                {
                    cause = new LagCause(participates != null ? QuestLagReason.NotInTheFight
                        : hero.Session.MapId != master.MapId ? QuestLagReason.Elsewhere
                        : QuestLagReason.InFight);
                }
                else
                {
                    try
                    {
                        using (SessionContext.Push(hero.Session))
                            await apply(hero.Session);
                        if (hero.Session.State.AchievementsPending.Count > 0)
                            await Achievements.ForHeroAsync(master, hero.Session);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Heroes] Quest sharing failed for {hero.CharacterId}: {ex.Message}");
                    }
                }

                if (quests == null || quests.Count == 0) continue;
                try { await NoteLagsAsync(master, hero, quests, cause, explain); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Heroes] Could not tell whether {hero.CharacterId} followed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Each quest the hero is now behind his master in, kept with its reason; the master is told
        /// once a quest, when the hero falls behind in it, not again at each step after.
        /// </summary>
        private static async Task NoteLagsAsync(GameSession master, Hero hero, IReadOnlyCollection<int> quests,
                                                LagCause? cause, Func<GameSession, LagCause?>? explain)
        {
            var lags = _questLags.GetOrAdd(hero.CharacterId, _ => new ConcurrentDictionary<int, QuestLag>());
            foreach (int quest in quests.Distinct())
            {
                int heroAt = Quests.ProgressOf(hero.Session, quest);
                int masterAt = Quests.ProgressOf(master, quest);
                if (heroAt >= masterAt)
                {
                    lags.TryRemove(quest, out _);
                    continue;
                }
                if (lags.ContainsKey(quest)) continue;

                LagCause? explained = null;
                if (cause == null && explain != null)
                {
                    using (SessionContext.Push(hero.Session)) explained = explain(hero.Session);
                }
                LagCause why = cause ?? explained
                    ?? new LagCause(heroAt < 0 ? QuestLagReason.NotStarted : QuestLagReason.Behind);
                int step = master.State.Quests?.Run(quest)?.StepId ?? 0;
                lags[quest] = new QuestLag(quest, step, why.Reason, why.ItemId, why.ItemCount);
                await TellMasterAsync(master, hero, why);
                Console.WriteLine($"[Heroes] {hero.CharacterId} stayed behind {master.CharacterId} in quest {quest}: {why.Reason}" +
                                  (why.ItemId != 0 ? $" ({why.ItemCount}x{why.ItemId})" : ""));
            }

        }

        /// <summary>The master's warning, in his language: who stayed behind and why.</summary>
        private static async Task TellMasterAsync(GameSession master, Hero hero, LagCause why)
        {
            if (!master.IsInWorld) return;
            string language = master.State.Language;
            string name = hero.Session.State.CharacterName is { Length: > 0 } n ? n : hero.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string reason = why.Reason == QuestLagReason.MissingItems
                ? Handlers.CommandTexts.Get(language, "hero.quest.why.missingitems", why.ItemCount)
                : Handlers.CommandTexts.Get(language, "hero.quest.why." + why.Reason.ToString().ToLowerInvariant());
            string text = Handlers.CommandTexts.Get(language, "hero.quest.behind", name, reason);
            try
            {
                // Free text goes as information (type 0), red and bold as a dungeon's refusal: as a
                // warning (type 1), id 0 is an error number to the client, and it wrote «L'erreur
                // n°0 est survenue.» instead of the sentence.
                await master.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice($"<color=#FF0000><b>{text}</b></color>")));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heroes] {master.CharacterId} could not be told about {hero.CharacterId}: {ex.Message}");
            }
        }

    }
}
