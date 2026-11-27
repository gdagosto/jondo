using System.Text.Json;
using Jondo.Unity.Server.Mods;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.UI;

/// <summary>
/// The settings editor: the cards of <see cref="ServerSettings"/>, with Save and Save and restart.
/// It stands alone in <see cref="ServerSettingsWindow"/> and inside the dashboard's Settings page.
/// </summary>
internal sealed class ServerSettingsView : UserControl
{
    internal const int CountdownWithPlayers = 60, CountdownEmpty = 3;
    private readonly Language _language;
    private readonly Action _leave;
    private readonly bool _embedded;
    internal readonly ServerSettingsEditor Editor;
    internal IReadOnlyList<string> AvailableMods { get; }

    internal static string[] DiscoverMods() => ClientModCatalog.ListAvailable(Paths.ClientModsDirectory)
        .Concat(Mods.ServerModLoader.ListClientAssets(Paths.ServerModsDirectory))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>How Save writes the settings: the file, unless a test gives another way.</summary>
    internal Action<ServerSettings> Write { get; set; } = settings =>
    {
        // Preserve immediate language changes independently of game settings edits.
        settings.WindowLanguage = ServerSettings.Load().WindowLanguage;
        settings.Save();
        Console.WriteLine($"[Settings] Saved to {Jondo.Unity.Launcher.Paths.ServerSettingsFile}; they apply at the next start.");
    };
    private readonly TextBlock _status = ServerTheme.Text("");
    private readonly Button _save, _restart, _close;
    private readonly List<NumericUpDown> _numbers = new();
    private readonly SettingsCardsPanel _cards = new() { Name = "SettingsCards", Margin = new Thickness(24, 0, 24, 16) };
    private bool _confirming, _building = true;
    private int _restartSeconds;
    private string T(string key) => ServerSettingsTexts.Get(_language, key);
    private System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(
        _language switch { Language.En => "en-GB", Language.Fr => "fr-FR", _ => "es-ES" });

    /// <param name="saved">The settings as the file has them, which the editor starts from.</param>
    /// <param name="running">The settings the server runs with, to tell what waits for a restart.</param>
    /// <param name="leave">
    /// Called by Close, and after a restart is started: the window closes; the page throws the
    /// edits away and starts again from the file.
    /// </param>
    /// <param name="embedded">Inside the dashboard, whose page already has the title.</param>
    internal ServerSettingsView(Language language, ServerSettings saved, ServerSettings running, Action leave,
        bool embedded = false, IReadOnlyList<string>? availableMods = null,
        IReadOnlyList<ServerModConfiguration>? serverMods = null)
    {
        _language = language;
        _leave = leave;
        _embedded = embedded;
        Editor = new(saved, running);
        AvailableMods = availableMods ?? DiscoverMods();
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        if (!embedded)
        {
            var header = new StackPanel { Margin = new Thickness(24, 20), Spacing = 8 };
            header.Children.Add(ServerTheme.Text(T("Caption").ToUpperInvariant(), 24, LauncherPalette.LightGold));
            header.Children.Add(Note("Subtitle"));
            layout.Children.Add(header);
        }
        var s = Editor.Edited;
        var rates = Card("Rates",
            Number("Xp", null, s.ExperienceBonusPercent, 0, 10000, 10, "%", v => s.ExperienceBonusPercent = (int)v),
            Number("JobXp", null, s.JobExperienceBonusPercent, 0, 10000, 10, "%", v => s.JobExperienceBonusPercent = (int)v),
            Number("Drop", null, s.DropBonusPercent, 0, 10000, 10, "%", v => s.DropBonusPercent = (int)v),
            Number("Kamas", null, s.KamasBonusPercent, 0, 10000, 10, "%", v => s.KamasBonusPercent = (int)v), CardHint("RatesHint"));
        var characters = Card("Characters",
            Number("MaxAp", null, s.MaxActionPoints, 0, 30, 1, "", v => s.MaxActionPoints = (int)v),
            Number("MaxMp", "CapHint", s.MaxMovementPoints, 0, 20, 1, "", v => s.MaxMovementPoints = (int)v),
            Toggle("NoEnergy", "NoEnergyHint", s.NoEnergyLoss, v => s.NoEnergyLoss = v),
            Toggle("Hardcore", "HardcoreHint", s.Hardcore, v => s.Hardcore = v));
        var startingBonuses = Card("StartingBonuses",
            Number("StartingKamas", "StartingKamasHint", s.StartingKamas, 0, 1_000_000_000L, 1, "", v => s.StartingKamas = (long)v),
            Number("StartingScrollStats", "StartingScrollStatsHint", s.StartingScrollStats, 0, 10_000, 1, "", v => s.StartingScrollStats = (int)v),
            Toggle("StartingAdventurerSet", "StartingAdventurerSetHint", s.StartingAdventurerSet, v => s.StartingAdventurerSet = v));
        var conquests = Card("Conquests",
            Number("Weakened", "WeakenedHint", s.PrismWeakenedHours, 0, 168, 1, "h", v => s.PrismWeakenedHours = (int)v),
            Number("ConquestMax", "ConquestMaxHint", s.ConquestMaxMinutes, 20, 1440, 10, "min", v => s.ConquestMaxMinutes = (int)v),
            Number("Invulnerable", "InvulnerableHint", s.ConquestInvulnerabilityHours, 0, 168, 1, "h", v => s.ConquestInvulnerabilityHours = (int)v),
            Number("Protection", "ProtectionHint", s.PrismProtectionMinutes, 0, 1440, 10, "min", v => s.PrismProtectionMinutes = (int)v));
        var welcome = new TextBox { Name = "WelcomeMessage", Text = s.WelcomeMessage, Watermark = T("WelcomePlaceholder"), MaxLength = 300 };
        welcome.TextChanged += (_, _) => { s.WelcomeMessage = welcome.Text ?? ""; Changed(); };
        var welcomeCard = Card("Welcome", CardHint("WelcomeHint"), welcome);
        var botsMax = Number("BotsMax", "BotsMaxHint", s.SimulatedPlayersMax, 1, Bots.SimulatedPlayers.Most, 5, "", v => s.SimulatedPlayersMax = (int)v);
        var bots = Card("Bots", Toggle("BotsOn", "BotsOnHint", s.SimulatedPlayers, v => { s.SimulatedPlayers = v; botsMax.IsEnabled = v; }), botsMax);
        botsMax.IsEnabled = s.SimulatedPlayers;
        var chance = Number("Chance", "ChanceHint", (decimal)Math.Clamp(double.IsFinite(s.RandomLootChancePercent) ? s.RandomLootChancePercent : 10, 0, 100),
            0, 100, 1, "%", v => s.RandomLootChancePercent = (double)v, 2);
        var rarities = Rarities();
        var levels = CardHint("LootLevels");
        void LootState(bool on) { chance.IsEnabled = on; rarities.IsEnabled = on; levels.IsEnabled = on; }
        var loot = Toggle("RandomLoot", "RandomLootHint", s.RandomLoot, v => { s.RandomLoot = v; LootState(v); });
        LootState(s.RandomLoot);
        var lootCard = Card("Loot", loot, chance, rarities, levels);
        var mods = ModSettings(AvailableMods, serverMods ?? ServerModLoader.ListSettings(Paths.ServerModsDirectory));
        var raids = Card("Raids",
            Number("RaidMin", "RaidMinHint", s.RaidMinPlayers, 0, 16, 1, "", v => s.RaidMinPlayers = (int)v),
            Number("Matchmaking", "MatchmakingHint", s.MatchmakingGroupSize, 0, 4, 1, "", v => s.MatchmakingGroupSize = (int)v));
        var world = Card("World",
            Number("CharacterSlots", "CharacterSlotsHint", s.CharacterSlots, 1, 20, 1, "", v => s.CharacterSlots = (int)v),
            Toggle("RequireZaap", "RequireZaapHint", s.RequireZaapDiscovery, v => s.RequireZaapDiscovery = v));
        foreach (var card in new[] { rates, lootCard, characters, mods, conquests, raids, bots, welcomeCard, startingBonuses, world })
            _cards.Children.Add(card);
        var scroll = new ScrollViewer { Name = "SettingsScroll", Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) =>
        {
            _cards.ViewportHeight = Math.Max(0, scroll.Bounds.Height - _cards.Margin.Top - _cards.Margin.Bottom);
            _cards.InvalidateMeasure();
        };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(24, 12, 24, 18), Spacing = 12 };
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_status);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _close = ServerTheme.Button(T("Close"), CloseClicked);
        _save = ServerTheme.Button(T("Save"), SaveClicked);
        _restart = ServerTheme.Button(T("SaveRestart"), RestartClicked, LauncherPalette.OnlineGreen);
        _close.Name = "CloseSettings"; _save.Name = "SaveSettings"; _restart.Name = "RestartServer";
        foreach (var b in new[] { _close, _save, _restart }) { b.Margin = new Thickness(6, 0, 0, 6); buttons.Children.Add(b); }
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // In the dashboard Escape only backs out of the restart question: it must not throw
            // away a page of edits.
            if (e.Key == Key.Escape && (!_embedded || _confirming)) { CloseClicked(); e.Handled = true; }
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SaveClicked(); e.Handled = true; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _building = false;
        Changed();
    }

    private Border ModSettings(IReadOnlyList<string> available, IReadOnlyList<ServerModConfiguration> packages)
    {
        var s = Editor.Edited;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var clientIds = available.ToHashSet(comparer);
        string[] serverIds = packages.Select(package => package.Id).ToArray();
        var installed = serverIds.ToHashSet(comparer);
        var clientOrder = new StackPanel { Spacing = 6 };
        var serverOrder = new StackPanel { Spacing = 6 };
        var details = new StackPanel { Spacing = 8 };

        string? Localized(LocalizedModText? text) => _language switch
        {
            Language.En => text?.En ?? text?.Es,
            Language.Fr => text?.Fr ?? text?.En ?? text?.Es,
            _ => text?.Es ?? text?.En,
        };

        foreach (var package in packages.Where(package => package.Fields.Length > 0))
        {
            var content = new StackPanel { Spacing = 12 };
            var values = ServerModConfiguration.Resolve(s, package.Id, package);
            var dependencies = new List<Control>();
            bool enabled = package.ActivationSetting == null
                || !values.TryGetProperty(package.ActivationSetting, out var activation)
                || activation.ValueKind != JsonValueKind.False;
            void SetEnabled(bool active)
            {
                foreach (var dependency in dependencies) dependency.IsEnabled = active;
            }

            foreach (var field in package.Fields)
            {
                if (!values.TryGetProperty(field.Key, out var value)) continue;
                string name = $"{package.Id}_{field.Key}";
                string label = Localized(field.Label) ?? field.Key;
                string? hint = Localized(field.Hint);
                bool activates = field.Key == package.ActivationSetting;
                Control? setting = null;
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    setting = Toggle(name, null, value.GetBoolean(), v =>
                    {
                        ServerModConfiguration.Set(s, package, field.Key, v);
                        if (activates) SetEnabled(v);
                    }, label, hint);
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
                    && field.Minimum <= field.Maximum && field.Step > 0)
                    setting = Number(name, null, number, field.Minimum, field.Maximum,
                        field.Step, field.Unit ?? "", v => ServerModConfiguration.Set(s, package, field.Key, (int)v),
                        displayLabel: label, displayHint: hint, setInitialValue: false);

                if (setting == null) continue;
                if (!activates) dependencies.Add(setting);
                content.Children.Add(setting);
            }
            SetEnabled(enabled);
            if (content.Children.Count > 0)
                details.Children.Add(new Expander
                {
                    Header = package.Name ?? package.Id,
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
        }

        void Render(StackPanel order, bool server, string? focusedFile = null, string? focusedAction = null)
        {
            order.Children.Clear();
            var files = server ? installed : clientIds;
            Button? focus = null;
            Button ActionButton(string label, string action, string file, Action click)
            {
                var button = ServerTheme.Button(label, click);
                button.Padding = new Thickness(8, 4);
                string description = $"{T(action)} {file}";
                AutomationProperties.SetName(button, description);
                ToolTip.SetTip(button, description);
                if (file == focusedFile && action == focusedAction) focus = button;
                return button;
            }

            var configured = (server ? s.ServerMods : s.ClientMods).OfType<string>()
                .Where(file => !string.IsNullOrWhiteSpace(file)).ToArray();
            void Edit(IEnumerable<string> next, string file, string action)
            {
                if (server) s.ServerMods = next.ToArray();
                else s.ClientMods = next.ToArray();
                Render(order, server, file, action);
                Changed();
            }

            void Move(int from, int to, string action)
            {
                if (to < 0 || to >= configured.Length) return;
                (configured[from], configured[to]) = (configured[to], configured[from]);
                Edit(configured, configured[to], action);
            }

            for (int i = 0; i < configured.Length; i++)
            {
                int index = i;
                string file = configured[index];
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var up = ActionButton("↑", "ModUp", file, () => Move(index, index - 1, "ModUp"));
                var down = ActionButton("↓", "ModDown", file, () => Move(index, index + 1, "ModDown"));
                up.IsEnabled = index > 0;
                down.IsEnabled = index < configured.Length - 1;
                actions.Children.Add(up);
                actions.Children.Add(down);
                actions.Children.Add(ActionButton("−", "ModRemove", file, () =>
                    Edit(configured.Where((_, at) => at != index), file, "ModAdd")));
                order.Children.Add(ModRow($"{index + 1}. {file}" + (files.Contains(file) ? "" : $" ({T("ModMissing")})"), actions));
            }

            bool canAdd = server || configured.Count(files.Contains) < ClientModManifest.MaxEntries;
            foreach (string file in (server ? serverIds : available).Except(configured, comparer))
            {
                var add = ActionButton("+", "ModAdd", file, () => Edit(configured.Append(file), file, "ModRemove"));
                add.IsEnabled = canAdd;
                order.Children.Add(ModRow(file, add));
            }

            if (order.Children.Count == 0) order.Children.Add(Note(server ? "NoServerMods" : "NoMods"));
            if (focus?.IsEnabled == true) focus.Focus();
        }

        Render(clientOrder, server: false);
        Render(serverOrder, server: true);
        return Card("Mods", CardHint("ModsHint"), clientOrder,
            ServerTheme.Text(T("ServerMods"), 14, LauncherPalette.LightGold), serverOrder, details);
    }

    private static Grid ModRow(string label, Control actions)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = ServerTheme.Text(label);
        title.TextWrapping = TextWrapping.Wrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(title);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private TextBlock Note(string key)
    {
        var note = ServerTheme.Text(T(key), 12, LauncherPalette.MutedGold);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>
    /// A card's explanation, which goes with the rows' hints: hidden when the cards are compacted to
    /// fit the window, and then the card's tooltip.
    /// </summary>
    private TextBlock CardHint(string key)
    {
        var note = Note(key);
        _cards.Hints.Add(note);
        return note;
    }

    private Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        var heading = ServerTheme.Text(T(title), 14, LauncherPalette.LightGold);
        heading.FontWeight = FontWeight.Bold; heading.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(heading);
        foreach (var child in children) stack.Children.Add(child);
        var card = ServerTheme.Card(stack);
        var tips = children.OfType<TextBlock>().Where(_cards.Hints.Contains).Select(t => t.Text).ToList();
        if (tips.Count > 0) ToolTip.SetTip(card, string.Join(Environment.NewLine, tips));
        return card;
    }

    private Control Row(string label, string hint, Control input, string? displayLabel = null, string? displayHint = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        input.Margin = new Thickness(12, 0, 0, 0);
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var text = ServerTheme.Text(displayLabel ?? T(label)); text.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(text);
        if (displayHint != null || hint != null)
        {
            string explanation = displayHint ?? T(hint!);
            var note = ServerTheme.Text(explanation, 12, LauncherPalette.MutedGold);
            note.TextWrapping = TextWrapping.Wrap;
            labels.Children.Add(note);
            _cards.Hints.Add(note);
            ToolTip.SetTip(row, explanation);
        }
        row.Children.Add(labels); Grid.SetColumn(input, 1); row.Children.Add(input);
        return row;
    }

    private Control Number(string label, string hint, decimal value, decimal min, decimal max, decimal step,
        string unit, Action<decimal> set, int decimals = 0, string? displayLabel = null,
        string? displayHint = null, bool setInitialValue = true)
    {
        var number = new NumericUpDown
        {
            Name = label, Minimum = min, Maximum = max, Increment = step, Value = Math.Clamp(value, min, max),
            FormatString = decimals == 0 ? "0" : "0.##", Width = 114,
            NumberFormat = Culture.NumberFormat,
        };
        // Expander contents may never apply their templates. Seed their input text from the value.
        number.Text = number.Value.Value.ToString(Culture.NumberFormat);
        _numbers.Add(number);
        // Clamped legacy file values must agree with what is displayed and will be saved.
        if (setInitialValue) set(number.Value.Value);
        number.ValueChanged += (_, _) =>
        {
            if (number.Value is decimal v) set(v);
            Changed();
        };
        number.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) Changed(); };
        var input = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        input.Children.Add(number);
        if (unit.Length > 0) input.Children.Add(ServerTheme.Text(unit, 12, LauncherPalette.MutedGold));
        return Row(label, hint, input, displayLabel, displayHint);
    }

    private Control Toggle(string label, string hint, bool value, Action<bool> set,
        string? displayLabel = null, string? displayHint = null)
    {
        var toggle = new ToggleSwitch { Name = label, IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) => { set(toggle.IsChecked == true); Changed(); };
        return Row(label, hint, toggle, displayLabel, displayHint);
    }

    private Control Rarities()
    {
        var table = new StackPanel { Spacing = 6 };
        void Add(string key, double percent, uint color)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(ServerTheme.Text(T(key), 12, color));
            var value = ServerTheme.Text(percent.ToString("0.##", Culture) + " %", 12, color);
            Grid.SetColumn(value, 1); row.Children.Add(value); table.Children.Add(row);
        }
        Add("Consumables", 100 - RandomLoot.Odds.Sum(o => o.Percent), LauncherPalette.MutedGold);
        foreach (var (rarity, key, color) in new[]
        {
            (RandomLoot.Rarity.Equipment, "Equipment", LauncherPalette.CardText),
            (RandomLoot.Rarity.Cosmetic, "Cosmetics", 0xFF78BEFFu),
            (RandomLoot.Rarity.Creature, "Creatures", LauncherPalette.OnlineGreen),
            (RandomLoot.Rarity.Dofus, "Dofus", LauncherPalette.LightGold),
            (RandomLoot.Rarity.Legendary, "Legendary", 0xFFFF8C3Au),
            (RandomLoot.Rarity.Perfect, "Perfect", LauncherPalette.PurpleBorder),
        }) Add(key, RandomLoot.Odds.First(o => o.Rarity == rarity).Percent, color);
        return table;
    }

    private void Changed()
    {
        if (_building) return;
        _confirming = false;
        _restart.Content = T("SaveRestart"); _save.IsVisible = true;
        bool valid = ValidInputs();
        _save.IsEnabled = Editor.Dirty && valid;
        _restart.IsEnabled = (Editor.Dirty || Editor.Pending) && valid;
        _close.Content = T(Editor.Dirty ? "Cancel" : "Close");
        // In the dashboard there is nothing to close: Cancel throws the edits away, and only shows with some.
        if (_embedded) _close.IsVisible = Editor.Dirty;
        Status(Editor.Dirty ? T("Unsaved") : Editor.Pending ? T("Pending") : "");
    }

    private bool ValidInputs() => _numbers.All(n => n.Value != null &&
        decimal.TryParse(n.Text, System.Globalization.NumberStyles.Number, n.NumberFormat, out var parsed) &&
        parsed >= n.Minimum && parsed <= n.Maximum &&
        (n.FormatString != "0" || parsed == decimal.Truncate(parsed)));

    private void Status(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = ServerTheme.Brush(error ? LauncherPalette.Red : LauncherPalette.HighlightText);
    }

    private bool Save()
    {
        try
        {
            Editor.Save(Write);
            return true;
        }
        catch (Exception ex) { Status(T("SaveError") + ex.Message, true); return false; }
    }

    private void SaveClicked()
    {
        if (_save.IsEnabled && ValidInputs() && Editor.Dirty && Save()) Changed();
    }

    private void RestartClicked()
    {
        if (!_restart.IsEnabled || !ValidInputs()) return;
        if (ServerRestart.Counting) { Status(T("Counting"), true); return; }
        if (!_confirming)
        {
            int players = ServerRestart.PlayersInside;
            _restartSeconds = players > 0 ? CountdownWithPlayers : CountdownEmpty;
            _confirming = true; _restart.Content = T("ConfirmRestart"); _close.Content = T("Back"); _close.IsVisible = true; _save.IsVisible = false;
            Status(players > 0 ? string.Format(T("RestartAsk"), players, _restartSeconds) : string.Format(T("RestartAskEmpty"), _restartSeconds));
            return;
        }
        if (Editor.Dirty && !Save()) return;
        ServerRestart.Begin(_restartSeconds);
        _leave();
    }

    private void CloseClicked()
    {
        if (_confirming) Changed();
        else _leave();
    }
}
