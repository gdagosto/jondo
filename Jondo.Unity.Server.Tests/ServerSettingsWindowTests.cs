using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using System.Text.Json;
using Jondo.Unity.Launcher.UI;
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
    public void Bot_and_hunt_controls_edit_settings_and_follow_toggles(Language language)
    {
        var settings = new ServerSettings { SimulatedPlayers = false, HeadHunters = false };
        var window = new ServerSettingsWindow(language, settings, settings, ["Cazacabezas.dll", "Heroes.dll"]);
        try
        {
            window.Show();
            window.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Cazacabezas")).IsExpanded = true;
            window.UpdateLayout();
            var botsMax = Find<NumericUpDown>(window, "BotsMax");
            var hunt = Find<NumericUpDown>(window, "HuntMinutes");
            var rest = Find<NumericUpDown>(window, "RestMinutes");
            Assert.False(botsMax.IsEffectivelyEnabled);
            Assert.False(hunt.IsEffectivelyEnabled);
            Assert.False(rest.IsEffectivelyEnabled);
            Find<ToggleSwitch>(window, "BotsOn").IsChecked = true;
            Find<ToggleSwitch>(window, "HuntersOn").IsChecked = true;
            Assert.True(botsMax.IsEffectivelyEnabled);
            Assert.True(hunt.IsEffectivelyEnabled);
            Assert.True(rest.IsEffectivelyEnabled);
            botsMax.Value = 25; hunt.Value = 30; rest.Value = 45;
            ServerSettings? saved = null;
            window.Editor.Save(value => saved = value.Copy());
            Assert.True(saved!.SimulatedPlayers);
            Assert.True(saved.HeadHunters);
            Assert.Equal(25, saved.SimulatedPlayersMax);
            Assert.Equal(30, saved.HeadHuntWindowMinutes);
            Assert.Equal(45, saved.HeadHuntRestMinutes);
            Assert.False(settings.SimulatedPlayers);
            Assert.False(settings.HeadHunters);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Legacy_mod_settings_migrate_and_empty_mod_order_round_trips()
    {
        var settings = ServerSettings.Read("""
            {
                "heroMode": false, "HeroesMax": 3, "HeroSwitchReloadsMap": true,
                "HeadHunters": true, "HeadHuntWindowMinutes": 40, "HeadHuntRestMinutes": 0
            }
            """);
        Assert.False(settings.Heroes.Enabled);
        Assert.Equal(3, settings.Heroes.MaxHeroes);
        Assert.True(settings.Heroes.ReloadMapOnSwitch);
        Assert.True(settings.Cazacabezas.Enabled);
        Assert.Equal(40, settings.Cazacabezas.HuntWindowMinutes);
        Assert.Equal(0, settings.Cazacabezas.RestMinutes);
        Assert.Contains("Heroes.dll", settings.ClientMods);

        var next = settings.Copy();
        next.ClientMods = [];
        next.Cazacabezas.RestMinutes = 10;
        Assert.Equal(0, settings.Cazacabezas.RestMinutes);
        string json = JsonSerializer.Serialize(next);
        Assert.DoesNotContain("\"HeadHunters\"", json);
        var read = ServerSettings.Read(json);
        Assert.Empty(read.ClientMods);
        Assert.Equal(next.Heroes, read.Heroes);
        Assert.Equal(next.Cazacabezas, read.Cazacabezas);
    }

    [AvaloniaFact]
    public void Editing_mod_order_preserves_missing_mod_settings_and_waits_for_restart()
    {
        var settings = new ServerSettings
        {
            ClientMods = ["JondoFix.dll", "Heroes.dll"],
            HeroMode = false,
            HeroesMax = 3,
            HeadHunters = true,
            HeadHuntWindowMinutes = 49,
        };
        var window = new ServerSettingsWindow(Language.En, settings, settings, ["Custom.dll", "Heroes.dll"]);
        try
        {
            window.Show();
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Expander>(), e => Equals(e.Header, "Cazacabezas"));

            void Click(string name)
            {
                var button = window.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }

            Click("Move up Heroes.dll");
            Click("Remove JondoFix.dll");
            Click("Add Custom.dll");
            Assert.True(window.Editor.Dirty);
            Assert.False(window.Editor.Pending);
            Assert.True(Find<Button>(window, "SaveSettings").IsEnabled);
            Assert.Equal(["Heroes.dll", "Custom.dll"], window.Editor.Edited.ClientMods);
            Assert.Equal(["JondoFix.dll", "Heroes.dll"], settings.ClientMods);

            ServerSettings? saved = null;
            window.Editor.Save(value => saved = value);
            Assert.False(window.Editor.Dirty);
            Assert.True(window.Editor.Pending);
            Assert.Equal(settings.Heroes, saved!.Heroes);
            Assert.Equal(settings.Cazacabezas, saved.Cazacabezas);
        }
        finally { window.Close(); }
    }

    private static void WithWindow(double width, double height, Action<Window> check)
    {
        var settings = new ServerSettings();
        var settingsWindow = new ServerSettingsWindow(Language.Es, settings, settings, ["JondoFix.dll", "Heroes.dll", "Cazacabezas.dll"]);
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
