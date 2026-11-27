using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using System.Text.Json;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerSettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(1264, 713)]
    [InlineData(1904, 1001)]
    public void Maximized_cards_fit_or_scroll_without_overlap(double width, double height)
    {
        WithWindow(width, height, window =>
        {
            var cards = Find<SettingsCardsPanel>(window, "SettingsCards");
            Assert.Equal(10, cards.Children.Count);
            // Ten cards fill a full-HD screen; on 1280x720 the panel scrolls instead, never overlapping.
            bool fits = width > 1800;
            Assert.All(cards.Children, card =>
            {
                if (fits) Assert.True(card.Bounds.Bottom <= cards.ViewportHeight + 1,
                    $"Card ends at {card.Bounds.Bottom}, viewport {cards.ViewportHeight}, compact {cards.Compact}");
                Assert.True(card.Bounds.Right <= cards.Bounds.Width + 1);
                foreach (var other in cards.Children.Where(c => c != card))
                    Assert.False(card.Bounds.Intersects(other.Bounds));
            });
            if (width > 1800) Assert.False(cards.Compact);
        });
    }

    [AvaloniaFact]
    public void Narrow_window_scrolls_one_column_with_inline_hints()
    {
        WithWindow(520, 500, window =>
        {
            var cards = Find<SettingsCardsPanel>(window, "SettingsCards");
            Assert.Single(cards.Children.Select(c => c.Bounds.X).Distinct());
            Assert.True(cards.Children.Max(c => c.Bounds.Bottom) > cards.ViewportHeight);
            Assert.False(cards.Compact);
            Assert.All(cards.Hints, hint => Assert.True(hint.IsVisible));
            var scroll = Find<ScrollViewer>(window, "SettingsScroll");
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
        });
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public void Bot_and_manifest_controls_edit_settings_and_follow_toggles(Language language)
    {
        var schema = new ServerModConfiguration("sample", "Sample", "Enabled",
        [
            new ServerModSetting("Enabled", JsonSerializer.SerializeToElement(false), null, null, null, 0, 0, 0, null),
            new ServerModSetting("Minutes", JsonSerializer.SerializeToElement(15), null, null, null, 1, 120, 5, "min"),
        ]);
        var settings = new ServerSettings { SimulatedPlayers = false, ServerMods = ["sample"] };
        var view = new ServerSettingsView(language, settings, settings, () => { },
            availableMods: [], serverMods: [schema]);
        var window = new Window { Content = view };
        try
        {
            window.Show();
            window.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Sample")).IsExpanded = true;
            window.UpdateLayout();
            var botsMax = Find<NumericUpDown>(window, "BotsMax");
            var minutes = Find<NumericUpDown>(window, "sample_Minutes");
            Assert.False(botsMax.IsEffectivelyEnabled);
            Assert.False(minutes.IsEffectivelyEnabled);
            Find<ToggleSwitch>(window, "BotsOn").IsChecked = true;
            Find<ToggleSwitch>(window, "sample_Enabled").IsChecked = true;
            Assert.True(botsMax.IsEffectivelyEnabled);
            Assert.True(minutes.IsEffectivelyEnabled);
            botsMax.Value = 25;
            minutes.Value = 30;

            ServerSettings? saved = null;
            view.Editor.Save(value => saved = value.Copy());
            Assert.True(saved!.SimulatedPlayers);
            Assert.Equal(25, saved.SimulatedPlayersMax);
            Assert.True(saved.ModSettings["sample"].GetProperty("Enabled").GetBoolean());
            Assert.Equal(30, saved.ModSettings["sample"].GetProperty("Minutes").GetInt32());
            Assert.False(settings.SimulatedPlayers);
            Assert.Empty(settings.ModSettings);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Unknown_settings_and_empty_client_mod_order_round_trip()
    {
        var settings = ServerSettings.Read("""
            {"LegacyToggle":false,"Fixture.Package":{"Delay":3,"Custom":["future value"]}}
            """);
        Assert.Equal(["JondoFix.dll"], settings.ClientMods);

        var next = settings.Copy();
        next.ClientMods = [];
        next.ModSettings["fixture.package"] = JsonSerializer.SerializeToElement(new { Delay = 4 });
        Assert.Equal(3, settings.ModSettings["fixture.package"].GetProperty("Delay").GetInt32());
        Assert.Equal("future value", settings.ModSettings["fixture.package"].GetProperty("Custom")[0].GetString());

        var read = ServerSettings.Read(JsonSerializer.Serialize(next));
        Assert.Empty(read.ClientMods);
        Assert.False(read.ModSettings["legacytoggle"].GetBoolean());
        Assert.Equal(4, read.ModSettings["fixture.package"].GetProperty("Delay").GetInt32());
    }

    [AvaloniaFact]
    public void Editing_mod_order_preserves_missing_mod_settings_and_waits_for_restart()
    {
        var settings = ServerSettings.Read("""
            {"ClientMods":["JondoFix.dll","Enabled.dll"],"uninstalled":{"Enabled":false,"CustomValue":3}}
            """);
        var window = new ServerSettingsWindow(Language.En, settings, settings, ["Custom.dll", "Enabled.dll"]);
        try
        {
            window.Show();

            void Click(string name)
            {
                var button = window.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }

            Click("Move up Enabled.dll");
            Click("Remove JondoFix.dll");
            Click("Add Custom.dll");
            Assert.True(window.Editor.Dirty);
            Assert.False(window.Editor.Pending);
            Assert.True(Find<Button>(window, "SaveSettings").IsEnabled);
            Assert.Equal(["Enabled.dll", "Custom.dll"], window.Editor.Edited.ClientMods);
            Assert.Equal(["JondoFix.dll", "Enabled.dll"], settings.ClientMods);

            ServerSettings? saved = null;
            window.Editor.Save(value => saved = value);
            Assert.False(window.Editor.Dirty);
            Assert.True(window.Editor.Pending);
            Assert.True(JsonElement.DeepEquals(settings.ModSettings["uninstalled"], saved!.ModSettings["uninstalled"]));
        }
        finally { window.Close(); }
    }

    private static void WithWindow(double width, double height, Action<Window> check)
    {
        var settings = new ServerSettings();
        var settingsWindow = new ServerSettingsWindow(Language.Es, settings, settings, ["JondoFix.dll", "Enabled.dll", "Extra.dll"]);
        var content = settingsWindow.Content;
        settingsWindow.Content = null;
        // Exercise the real content at an exact client size, independently of monitor clamping.
        var window = new Window { Width = width, Height = height, Content = content };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            check(window);
        }
        finally { window.Close(); settingsWindow.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
}
