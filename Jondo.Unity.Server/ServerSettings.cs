using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// What the server's operator can change from its Settings window: rates, caps and modes that
    /// the game itself does not decide. Kept in the emulator's own folder (config\server_settings.json).
    /// </summary>
    /// <remarks>
    /// The server reads them ONCE, when it starts (<see cref="Current"/>), and every rule reads that
    /// snapshot: a change saved from the window applies at the next start, which the window offers
    /// to do at once, telling the players first. A setting at its default leaves the game as it is.
    /// </remarks>
    public sealed class ServerSettings
    {
        /// <summary>The fewest players a guild raid starts with; 0 keeps the client's data (eight).</summary>
        public int RaidMinPlayers { get; set; }

        /// <summary>
        /// How many players the automatic dungeon search groups; 0 keeps the game's four. Lower
        /// for a server with few players.
        /// </summary>
        public int MatchmakingGroupSize { get; set; }

        /// <summary>The most action points a character can have; 0 means no cap (the official one is 12).</summary>
        public int MaxActionPoints { get; set; }

        /// <summary>The most movement points a character can have; 0 means no cap (the official one is 6).</summary>
        public int MaxMovementPoints { get; set; }

        /// <summary>Extra experience for everybody, in percent: what fights, quests and rewards give.</summary>
        public int ExperienceBonusPercent { get; set; }
        
        /// <summary>Extra job experience for everybody, in percent: what gathering, crafting and breeding give.</summary>
        public int JobExperienceBonusPercent { get; set; }

        /// <summary>Extra chance of every item a monster drops, in percent.</summary>
        public int DropBonusPercent { get; set; }

        /// <summary>Extra kamas from fights, in percent.</summary>
        public int KamasBonusPercent { get; set; }

        /// <summary>
        /// Hardcore: every monster twice its size and with three times its life and characteristics.
        /// </summary>
        public bool Hardcore { get; set; }

        /// <summary>A lost fight costs no energy.</summary>
        public bool NoEnergyLoss { get; set; }

        /// <summary>
        /// Random loot: besides its own table, a monster can drop a handful of items picked at random
        /// around its level (<see cref="Managers.RandomLoot"/>).
        /// </summary>
        public bool RandomLoot { get; set; }

        /// <summary>The chance, in percent, that a monster drops a random handful when random loot is on.</summary>
        public double RandomLootChancePercent { get; set; } = 10;

        /// <summary>A line every player reads in the chat on coming into the world; empty, none.</summary>
        public string WelcomeMessage { get; set; } = "";

        /// <summary>
        /// The hours a beaten prism stays weakened before it turns vulnerable: «la hora elegida por
        /// la defensa (con un plazo mínimo de 24 horas)», Translations 354323. The 3.6 client has no
        /// window to choose that hour in, so the server keeps the minimum; 0 turns it vulnerable at once.
        /// </summary>
        public int PrismWeakenedHours { get; set; } = 24;

        /// <summary>
        /// The longest a conquest of a vulnerable prism lasts, in minutes: if no alliance has
        /// dominated twenty minutes by then, the defence keeps its prism.
        /// </summary>
        public int ConquestMaxMinutes { get; set; } = 120;

        /// <summary>
        /// A conquered prism's hours of invulnerability, divided by the prisms its alliance holds:
        /// «inversamente proporcional al número de prismas que tenga», Translations 1149317.
        /// </summary>
        public int ConquestInvulnerabilityHours { get; set; } = 24;

        /// <summary>
        /// The minutes a new prism cannot be attacked: «No puedes atacar este prisma durante su
        /// primera hora de vida» (info 1/154). 0 lets it be attacked at once.
        /// </summary>
        public int PrismProtectionMinutes { get; set; } = 60;

        /// <summary>
        /// Simulated players: accounts the server plays itself (<see cref="Bots.SimulatedPlayers"/>),
        /// each over a connection of its own like any client's. They walk, fight monsters, gather,
        /// enrol in the Koliseo and accept every duel, so the world feels inhabited and the server
        /// carries the load of that many players.
        /// </summary>
        public bool SimulatedPlayers { get; set; }

        /// <summary>The most simulated players in the world at once; how many are in varies under it.</summary>
        public int SimulatedPlayersMax { get; set; } = 20;

        /// <summary>Client mods to serve, in their configured load order.</summary>
        public string[] ClientMods { get; set; } = ["JondoFix.dll", "Cazacabezas.dll", "Heroes.dll"];

        /// <summary>Enabled server mod manifest IDs, in startup order. Set in the file.</summary>
        public string[] ServerMods { get; set; } = [];

        public HeroesModSettings Heroes { get; set; } = new();
        public CazacabezasModSettings Cazacabezas { get; set; } = new();

        public sealed record HeroesModSettings
        {
            public bool Enabled { get; set; } = true;
            public int MaxHeroes { get; set; } = 7;
            public bool ReloadMapOnSwitch { get; set; }
        }

        public sealed record CazacabezasModSettings
        {
            public bool Enabled { get; set; }
            public int HuntWindowMinutes { get; set; } = 15;
            public int RestMinutes { get; set; } = 30;
        }

        [JsonIgnore] public bool HeroMode { get => Heroes.Enabled; set => Heroes.Enabled = value; }
        [JsonIgnore] public int HeroesMax { get => Heroes.MaxHeroes; set => Heroes.MaxHeroes = value; }
        [JsonIgnore] public bool HeroSwitchReloadsMap { get => Heroes.ReloadMapOnSwitch; set => Heroes.ReloadMapOnSwitch = value; }
        [JsonIgnore] public bool HeadHunters { get => Cazacabezas.Enabled; set => Cazacabezas.Enabled = value; }
        [JsonIgnore] public int HeadHuntWindowMinutes { get => Cazacabezas.HuntWindowMinutes; set => Cazacabezas.HuntWindowMinutes = value; }
        [JsonIgnore] public int HeadHuntRestMinutes { get => Cazacabezas.RestMinutes; set => Cazacabezas.RestMinutes = value; }

        /// <summary>
        /// How many characters an account may have per category of server: the server list's
        /// «Clásico (5/8)». Eight by default, a whole group of heroes with its master; the real
        /// server's is five. Not in the settings window, full at its smallest size: set in the file.
        /// </summary>
        public int CharacterSlots { get; set; } = 8;

        /// <summary>How many scrolled stats a player starts with.</summary>
        public int StartingScrollStats { get; set; } = Handlers.CharacterCreationHandler.ScrolledStat;

        /// <summary>How many kamas a player starts with.</summary>
        public long StartingKamas { get; set; } = 1_000_000L;

        /// <summary>Give each new character an Adventurer's Set.</summary>
        public bool StartingAdventurerSet { get; set; } = true;

        /// <summary>Require characters to visit ordinary zaaps before travelling to them. Set in the file.</summary>
        public bool RequireZaapDiscovery { get; set; } = true;

        /// <summary>The server window's language: es, en or fr.</summary>
        public string WindowLanguage { get; set; } = "es";

        /// <summary>How much bigger Hardcore makes monsters, and how much stronger.</summary>
        public const int HardcoreScale = 200;
        public const int HardcoreStatsFactor = 3;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private static ServerSettings _current;

        /// <summary>The settings this run started with.</summary>
        public static ServerSettings Current => _current ??= Load();

        /// <summary>For the tests: the settings to run with, or null for the file's.</summary>
        internal static void UseForTests(ServerSettings settings) => _current = settings;

        /// <summary>The settings as the file has them now, or the defaults.</summary>
        public static ServerSettings Load()
        {
            try
            {
                string file = Paths.ServerSettingsFile;
                if (!File.Exists(file)) return new ServerSettings();
                return Read(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] {Paths.ServerSettingsFile} could not be read ({ex.Message}); the defaults stand.");
                return new ServerSettings();
            }
        }

        internal static ServerSettings Read(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var settings = doc.RootElement.Deserialize<ServerSettings>(Json) ?? new ServerSettings();
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return settings;

            settings.ClientMods ??= new ServerSettings().ClientMods;
            settings.ServerMods ??= [];
            var keys = doc.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool migrateHeroes = settings.Heroes == null || !keys.Contains(nameof(Heroes));
            bool migrateCazacabezas = settings.Cazacabezas == null || !keys.Contains(nameof(Cazacabezas));
            if (!migrateHeroes && !migrateCazacabezas) return settings;

            var legacy = doc.RootElement.Deserialize<LegacyModSettings>(Json)!;
            if (migrateHeroes)
            {
                var heroes = new HeroesModSettings();
                heroes.Enabled = legacy.HeroMode ?? heroes.Enabled;
                heroes.MaxHeroes = legacy.HeroesMax ?? heroes.MaxHeroes;
                heroes.ReloadMapOnSwitch = legacy.HeroSwitchReloadsMap ?? heroes.ReloadMapOnSwitch;
                settings.Heroes = heroes;
            }
            if (migrateCazacabezas)
            {
                var cazacabezas = new CazacabezasModSettings();
                cazacabezas.Enabled = legacy.HeadHunters ?? cazacabezas.Enabled;
                cazacabezas.HuntWindowMinutes = legacy.HeadHuntWindowMinutes ?? cazacabezas.HuntWindowMinutes;
                cazacabezas.RestMinutes = legacy.HeadHuntRestMinutes ?? cazacabezas.RestMinutes;
                settings.Cazacabezas = cazacabezas;
            }
            return settings;
        }

        private sealed class LegacyModSettings
        {
            public LegacyModSettings() { }
            public bool? HeroMode { get; set; }
            public int? HeroesMax { get; set; }
            public bool? HeroSwitchReloadsMap { get; set; }
            public bool? HeadHunters { get; set; }
            public int? HeadHuntWindowMinutes { get; set; }
            public int? HeadHuntRestMinutes { get; set; }
        }

        /// <summary>Writes these settings to the file, for the next start.</summary>
        public void Save()
        {
            string file = Paths.ServerSettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(this, Json));
                File.Move(pending, file, overwrite: true);
            }
            finally
            {
                File.Delete(pending);
            }
        }

        /// <summary>A copy, for the window to edit.</summary>
        public ServerSettings Copy()
        {
            var copy = (ServerSettings)MemberwiseClone();
            copy.ClientMods = (string[])ClientMods.Clone();
            copy.ServerMods = (string[])ServerMods.Clone();
            copy.Heroes = Heroes with { };
            copy.Cazacabezas = Cazacabezas with { };
            return copy;
        }

        /// <summary>Whether two settings differ in anything that needs a restart.</summary>
        public bool DiffersFrom(ServerSettings other)
            => JsonSerializer.Serialize(WithoutLanguage()) != JsonSerializer.Serialize(other.WithoutLanguage());

        /// <summary>A copy without the window's language, which changes nothing in the game.</summary>
        private ServerSettings WithoutLanguage()
        {
            var copy = Copy();
            copy.WindowLanguage = "";
            return copy;
        }

        // ─── What the rules ask ────────────────────────────────────────────────

        /// <summary>An amount with a percent bonus on top.</summary>
        public static long WithBonus(long amount, int percent)
            => percent <= 0 || amount <= 0 ? amount : amount + amount * percent / 100;

        /// <summary>A chance with a percent bonus on top, never over certainty.</summary>
        public static double ChanceWithBonus(double percentChance, int percent)
            => percent <= 0 ? percentChance : Math.Min(100.0, percentChance * (100.0 + percent) / 100.0);

        /// <summary>A characteristic under a cap, where there is one.</summary>
        public static int Capped(int value, int cap) => cap > 0 ? Math.Min(value, cap) : value;
    }
}
