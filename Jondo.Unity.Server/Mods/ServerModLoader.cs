using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal sealed class ServerModLoader(Action<string> log) : IDisposable
{
    private const string ManifestFile = "manifest.json";
    private static readonly Regex ModId = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    private readonly Stack<Host> _started = new();
    private int _stopped;

    internal ServerModControlRoutes ControlRoutes { get; } = new();

    public ClientModCatalog.Asset[] ClientAssets => _started.Reverse().Select(host => host.ClientAsset)
        .OfType<ClientModCatalog.Asset>().ToArray();

    public static ServerModLoader Load(string directory, string[] enabledIds, Action<string> log)
    {
        var loader = new ServerModLoader(log);
        if (enabledIds.Length == 0) return loader;

        try
        {
            var packages = Discover(directory, enabledIds[0], log);
            foreach (string id in enabledIds.Distinct(StringComparer.OrdinalIgnoreCase))
                loader.StartPackage(directory, packages, id);
            return loader;
        }
        catch
        {
            loader.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        while (_started.TryPop(out var host))
            host.Stop();
    }

    internal void StartGameplay()
    {
        foreach (var host in _started.Reverse())
            host.Mod.StartGameplay();
    }

    private static ILookup<string, (string Directory, JsonElement Manifest)> Discover(
        string directory, string firstEnabledId, Action<string> log)
    {
        string[] packages;
        try
        {
            packages = Directory.GetDirectories(Path.GetFullPath(directory));
        }
        catch (DirectoryNotFoundException)
        {
            packages = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StartupError(firstEnabledId, ex);
        }

        var manifests = new List<(string Id, string Directory, JsonElement Manifest)>();
        foreach (string package in packages.Order(StringComparer.Ordinal))
        {
            try
            {
                RejectLink(package);
                using var file = File.OpenRead(RequirePackageFile(package, ManifestFile, ManifestFile));
                using var document = JsonDocument.Parse(file);
                var manifest = document.RootElement;
                if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("id", out var property) ||
                    property.ValueKind != JsonValueKind.String || property.GetString() is not { } id || !ModId.IsMatch(id))
                    throw new InvalidDataException("Invalid or missing 'id'.");
                manifests.Add((id, package, manifest.Clone()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                ReportError(log, Path.GetFileName(package), ex);
            }
        }

        return manifests.ToLookup(entry => entry.Id, entry => (entry.Directory, entry.Manifest),
            StringComparer.OrdinalIgnoreCase);
    }

    private void StartPackage(string directory, ILookup<string, (string Directory, JsonElement Manifest)> packages, string id)
    {
        Host host = null;
        try
        {
            if (id == null || !ModId.IsMatch(id))
                throw new InvalidDataException("Invalid ID in the ServerMods setting.");
            var matches = packages[id].ToArray();
            if (matches.Length == 0)
                throw new InvalidDataException($"No package with this ID was found in '{directory}'.");
            if (matches.Length > 1)
                throw new InvalidDataException($"Duplicate mod ID in packages: {string.Join(", ", matches.Select(match => Path.GetFileName(match.Directory)))}.");

            var (package, manifestJson) = matches[0];
            var manifest = manifestJson.Deserialize<Manifest>();
            string label = $"{Path.GetFileName(package)} ({manifest.Id})";
            host = new Host(log, label, ControlRoutes);
            var mod = LoadPackage(package, manifest, host, out string? clientPath);
            mod.Initialize(host);
            host.Mod = mod;
            if (ClientModManifest.IsDllName(Path.GetFileName(clientPath)))
            {
                string root = Path.GetDirectoryName(package);
                host.ClientAsset = new ClientModCatalog.Asset(root, Path.GetRelativePath(root, clientPath));
            }
            log($"[Server mods] {label} initialized (version {manifest.Version}).");
            _started.Push(host);
        }
        catch (Exception ex)
        {
            host?.Stop();
            throw StartupError(id, ex);
        }
    }

    private static IServerMod LoadPackage(string package, Manifest manifest, Host host, out string? clientPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Missing 'version'.");
        if (manifest.ServerApiVersion != ServerModApi.Version)
            throw new InvalidDataException($"Expected 'serverApiVersion' {ServerModApi.Version}, got {manifest.ServerApiVersion}.");

        string assemblyPath = RequirePackageFile(package, manifest.ServerAssembly, "serverAssembly");
        clientPath = manifest.ClientAsset == null ? null : RequirePackageFile(package, manifest.ClientAsset, "clientAsset");
        if (string.Equals(clientPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("'clientAsset' cannot be the server assembly.");

        RejectLink(Path.ChangeExtension(assemblyPath, ".deps.json"));
        var context = new PackageLoadContext(package, assemblyPath, clientPath);
        host.RegisterCleanup(context.Unload);
        var entryPoints = context.LoadPackageAssembly(assemblyPath).GetExportedTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters &&
                           typeof(IServerMod).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
            .ToArray();
        if (entryPoints.Length != 1)
            throw new InvalidDataException("Expected exactly one public IServerMod implementation with a public parameterless constructor.");

        return (IServerMod)Activator.CreateInstance(entryPoints[0]);
    }

    private static string RequirePackageFile(string package, string relativePath, string field)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"Missing '{field}'.");

        string path = package;
        foreach (string part in relativePath.Split('/', '\\'))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':'))
                throw new InvalidDataException($"'{field}' must be a package-relative file path: '{relativePath}'.");
            path = Path.Combine(path, part);
            RejectLink(path);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"'{field}' file '{relativePath}' was not found.", path);
        return path;
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Links are not supported in server mod packages: '{path}'.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static Exception StartupError(string id, Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } cause })
            error = cause;
        return new InvalidOperationException($"Server mod '{id ?? "(null)"}' failed to start: {error.Message}", error);
    }

    private static void ReportError(Action<string> log, string package, Exception error)
        => log($"[Server mods] {package} error: {error.Message}");

    private sealed record Manifest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("serverApiVersion")] int ServerApiVersion,
        [property: JsonPropertyName("serverAssembly")] string ServerAssembly,
        [property: JsonPropertyName("clientAsset")] string ClientAsset);

    private sealed class Host(Action<string> log, string label, ServerModControlRoutes controlRoutes)
        : IServerModHost, IHuntModHost
    {
        private readonly Stack<Action> _cleanup = new();
        private bool _stopping;
        internal IServerMod Mod { get; set; }
        internal ClientModCatalog.Asset? ClientAsset { get; set; }

        public void Log(string message) => log($"[Server mods] {label}: {message}");

        public void RegisterAuthenticatedControlRoute(string path, Func<AuthenticatedControlRequest, ServerModControlResponse> handler)
        {
            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                controlRoutes.Add(path, handler);
                _cleanup.Push(() => controlRoutes.Remove(path));
            }
        }

        IHuntWorld IHuntModHost.World { get; } = new HuntWorld();

        void IHuntModHost.RegisterHuntGameplay(IHuntGameplay gameplay)
        {
            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                _cleanup.Push(HuntGameplay.Register(gameplay));
            }
        }

        public void RegisterCleanup(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            lock (_cleanup)
            {
                if (_stopping) throw new InvalidOperationException("The server mod is stopping.");
                _cleanup.Push(cleanup);
            }
        }

        internal void Stop()
        {
            lock (_cleanup) _stopping = true;
            if (Mod is { } mod) RunCleanup(mod.Shutdown);
            Mod = null;
            while (_cleanup.TryPop(out var cleanup))
                RunCleanup(cleanup);
        }

        private void RunCleanup(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex) { ReportError(log, label, ex); }
        }
    }

    private sealed class PackageLoadContext(string package, string assemblyPath, string? clientPath) : AssemblyLoadContext(isCollectible: true)
    {
        private static readonly Assembly ContractAssembly = typeof(IServerMod).Assembly;
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        public Assembly LoadPackageAssembly(string path)
        {
            // Path-based loading holds the package DLL open on Windows.
            using var stream = File.OpenRead(path);
            return LoadFromStream(stream);
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (string.Equals(name.Name, ContractAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return ContractAssembly;

            string path = _resolver.ResolveAssemblyToPath(name);
            if (path == null && string.IsNullOrEmpty(name.CultureName))
            {
                string adjacent = Path.Combine(Path.GetDirectoryName(assemblyPath), name.Name + ".dll");
                if (Path.Exists(adjacent))
                    path = adjacent;
            }

            if (path == null) return null;
            path = RequirePackageFile(package, Path.GetRelativePath(package, path), $"dependency {name.Name}");
            if (string.Equals(path, clientPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("'clientAsset' cannot be a private server dependency.");
            return LoadPackageAssembly(path);
        }
    }
}
