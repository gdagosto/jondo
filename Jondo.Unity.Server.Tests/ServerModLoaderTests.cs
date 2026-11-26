using System.Runtime.Loader;
using System.Text.Json;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.ServerMods;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerModLoaderTests : IDisposable
{
    private const string AssemblyFile = "Fixture.ServerMod.dll";
    private const string DependencyFile = "Fixture.ServerModDependency.dll";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();
    private readonly List<string> _messages = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitializesExternalAssemblyWithSharedContractAndPrivateDependency(bool includeDepsFile)
    {
        var package = CreatePackage("external", serverAssembly: $"lib/{AssemblyFile}", clientAsset: "client.dat");
        string lib = Directory.CreateDirectory(Path.Combine(package, "lib")).FullName;
        CopyFixture(AssemblyFile, lib);
        CopyFixture(DependencyFile, lib);
        if (includeDepsFile)
            CopyFixture("Fixture.ServerMod.deps.json", lib);
        string hostContract = typeof(IServerMod).Assembly.Location;
        File.Copy(hostContract, Path.Combine(lib, Path.GetFileName(hostContract)));
        File.WriteAllText(Path.Combine(package, "client.dat"), "fixture client asset");
        CreatePackage("broken");

        using var mods = ServerModLoader.Load(_root.FullName, ["Fixture.External"], _messages.Add);

        Assert.Contains("[Server mods] external (fixture.external): fixture initialized through its private dependency", _messages);
        Assert.Contains("[Server mods] external (fixture.external) initialized (version 1.0.0).", _messages);
        Assert.DoesNotContain(_messages, message => message.Contains("fixture.broken"));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name is "Fixture.ServerMod" or "Fixture.ServerModDependency");
        Assert.DoesNotContain(typeof(ServerModLoader).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "Fixture.ServerMod" or "Fixture.ServerModDependency" or "Cazacabezas" or "Heroes");
    }

    [Fact]
    public void RejectsAllPackagesWithTheSameIdIgnoringCase()
    {
        CreatePackage("first", id: "fixture.duplicate");
        CreatePackage("second", id: "Fixture.Duplicate");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.duplicate"], _messages.Add));
        Assert.Contains("Server mod 'fixture.duplicate' failed to start: Duplicate mod ID", failure.Message);
        Assert.Contains("first, second", failure.Message);
    }

    [Theory]
    [InlineData(2, AssemblyFile, null, "Expected 'serverApiVersion' 1, got 2")]
    [InlineData(1, "missing.dll", null, "'serverAssembly' file 'missing.dll' was not found")]
    [InlineData(1, AssemblyFile, "missing.dat", "'clientAsset' file 'missing.dat' was not found")]
    [InlineData(1, "../outside.dll", null, "'serverAssembly' must be a package-relative file path")]
    [InlineData(1, "C:\\outside.dll", null, "'serverAssembly' must be a package-relative file path")]
    [InlineData(1, AssemblyFile, "..\\outside.dat", "'clientAsset' must be a package-relative file path")]
    public void RejectsInvalidPackages(int api, string serverAssembly, string? clientAsset, string error)
    {
        string package = CreatePackage("bad", api: api, serverAssembly: serverAssembly, clientAsset: clientAsset);
        CopyFixture(AssemblyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.bad"], _messages.Add));
        Assert.StartsWith($"Server mod 'fixture.bad' failed to start: {error}", failure.Message);
    }

    [Fact]
    public void ReportsMalformedManifestAndMissingEntryPoint()
    {
        string malformed = Directory.CreateDirectory(Path.Combine(_root.FullName, "bad-json")).FullName;
        File.WriteAllText(Path.Combine(malformed, "manifest.json"), "{");
        string package = CreatePackage("no-entry", serverAssembly: DependencyFile);
        CopyFixture(DependencyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.no-entry"], _messages.Add));
        Assert.StartsWith("[Server mods] bad-json error:", Assert.Single(_messages));
        Assert.Contains("Server mod 'fixture.no-entry' failed to start: Expected exactly one public IServerMod", failure.Message);
    }

    [Fact]
    public void ReportsMissingPrivateDependencyWithModId()
    {
        string package = CreatePackage("missing-dependency");
        CopyFixture(AssemblyFile, package);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(_root.FullName, ["fixture.missing-dependency"], _messages.Add));
        Assert.StartsWith("Server mod 'fixture.missing-dependency' failed to start:", failure.Message);
        Assert.Contains("Fixture.ServerModDependency", failure.Message);
    }

    [Fact]
    public async Task EmptySettingsStartWithNoPackageDirectory()
    {
        string missingRoot = Path.Combine(_root.FullName, "server-mods");
        await Program.RunEmulationAsync(missingRoot, ServerSettings.Read("{}").ServerMods,
            new TaskCompletionSource().Task, () =>
            {
                _messages.Add("ready");
                return Task.CompletedTask;
            }, () => _messages.Add("host stopped"), _messages.Add);

        Assert.Equal(["ready", "host stopped"], _messages);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ServerModLoader.Load(missingRoot, ["fixture.uninstalled"], _messages.Add));
        Assert.Contains("Server mod 'fixture.uninstalled' failed to start: No package with this ID", failure.Message);
    }

    [Fact]
    public async Task EnabledPackagesStartBeforeAdmissionAndStopOnceBeforeHostTeardown()
    {
        InstallFixture("z-first");
        InstallFixture("a-second");
        InstallFixture("disabled");
        var settings = ServerSettings.Read("""{"ServerMods":["fixture.z-first","FIXTURE.A-SECOND"]}""");
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = Program.RunEmulationAsync(_root.FullName, settings.ServerMods, shutdown.Task, () =>
        {
            _messages.Add("ready");
            return shutdown.Task;
        }, () => _messages.Add("host stopped"), _messages.Add);

        Assert.Equal([
            "[Server mods] z-first (fixture.z-first): fixture initialized through its private dependency",
            "[Server mods] z-first (fixture.z-first) initialized (version 1.0.0).",
            "[Server mods] a-second (fixture.a-second): fixture initialized through its private dependency",
            "[Server mods] a-second (fixture.a-second) initialized (version 1.0.0).",
            "ready"
        ], _messages);

        Parallel.For(0, 8, _ => shutdown.TrySetResult());
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([
            "[Server mods] a-second (fixture.a-second): fixture stopped",
            "[Server mods] a-second (fixture.a-second): second resource released",
            "[Server mods] a-second (fixture.a-second): first resource released",
            "[Server mods] z-first (fixture.z-first): fixture stopped",
            "[Server mods] z-first (fixture.z-first): second resource released",
            "[Server mods] z-first (fixture.z-first): first resource released",
            "host stopped"
        ], _messages.Skip(5));
    }

    [Fact]
    public async Task PartialStartupRollsBackBeforeHostTeardownAndUsesStartupFailureFormat()
    {
        InstallFixture("first");
        string broken = CreatePackage("broken");
        CopyFixture(AssemblyFile, broken);
        InstallFixture("later");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunEmulationAsync(_root.FullName, ["fixture.first", "fixture.broken", "fixture.later"],
                new TaskCompletionSource().Task, () => throw new Xunit.Sdk.XunitException("Listeners started"),
                () => _messages.Add("host stopped"), _messages.Add));

        string failure = Program.StartupFailure(exception);
        Assert.Contains("Fatal error while starting Jondo Server", failure);
        Assert.Contains("Server mod 'fixture.broken' failed to start:", failure);
        Assert.Contains("Fixture.ServerModDependency", failure);
        Assert.Equal([
            "[Server mods] first (fixture.first): fixture initialized through its private dependency",
            "[Server mods] first (fixture.first) initialized (version 1.0.0).",
            "[Server mods] broken (fixture.broken): second resource released",
            "[Server mods] broken (fixture.broken): first resource released",
            "[Server mods] first (fixture.first): fixture stopped",
            "[Server mods] first (fixture.first): second resource released",
            "[Server mods] first (fixture.first): first resource released",
            "host stopped"
        ], _messages);
    }

    [Fact]
    public async Task ShutdownDuringPluginInitializationSkipsAdmissionAndCleansUp()
    {
        InstallFixture("first");
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Program.RunEmulationAsync(_root.FullName, ["fixture.first"], shutdown.Task,
            () => throw new Xunit.Sdk.XunitException("Listeners started"), () => _messages.Add("host stopped"),
            message =>
            {
                _messages.Add(message);
                if (message.Contains("private dependency")) shutdown.TrySetResult();
            });

        Assert.Equal("[Server mods] first (fixture.first): fixture stopped", _messages[2]);
        Assert.Equal("host stopped", _messages[^1]);
    }

    [Fact]
    public void SettingsCopyAndSavePreserveEnabledIdsAndTrackDifferences()
    {
        Assert.Empty(ServerSettings.Read("""{"ServerMods":null}""").ServerMods);
        var running = ServerSettings.Read("""{"ServerMods":["fixture.running"]}""");
        var edited = running.Copy();
        edited.ServerMods[0] = "fixture.next";
        Assert.Equal(["fixture.running"], running.ServerMods);
        Assert.Equal(["fixture.next"], ServerSettings.Read(JsonSerializer.Serialize(edited)).ServerMods);
        Assert.True(edited.DiffersFrom(running));
    }

    private void InstallFixture(string folder)
    {
        string package = CreatePackage(folder);
        CopyFixture(AssemblyFile, package);
        CopyFixture(DependencyFile, package);
    }

    private string CreatePackage(string name, string? id = null, int api = ServerModApi.Version,
        string serverAssembly = AssemblyFile, string? clientAsset = null)
    {
        var package = Directory.CreateDirectory(Path.Combine(_root.FullName, name));
        File.WriteAllText(Path.Combine(package.FullName, "manifest.json"), JsonSerializer.Serialize(new
        {
            id = id ?? $"fixture.{name}",
            version = "1.0.0",
            serverApiVersion = api,
            serverAssembly,
            clientAsset
        }));
        return package.FullName;
    }

    private static void CopyFixture(string file, string directory) =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ServerModFixture", file), Path.Combine(directory, file));

    public void Dispose() => _root.Delete(recursive: true);
}
