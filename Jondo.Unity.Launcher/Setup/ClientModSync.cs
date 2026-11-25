using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Launcher.Setup;

internal static class ClientModSync
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal sealed record Plan(string ManifestPath, bool UsesManifest, string NewApproval = "");

    /// <summary>Verifies a server's ordered manifest before any of its DLLs can be used.</summary>
    internal static Plan Prepare(string host, bool hasMelonLoader, bool requiresRedirectMod,
        Func<string, ClientModManifest, string, bool>? approve)
    {
        var server = new UriBuilder(Uri.UriSchemeHttp, host, Contract.Puerto).Uri;
        string cache = Path.Combine(UserDirectories.Data, "Jondo", "client-mods");
        Plan plan = Prepare(Http, server, cache, LauncherPreferences.TrustedModManifest(host),
            hasMelonLoader, requiresRedirectMod, approve);
        if (plan.NewApproval.Length != 0) LauncherPreferences.TrustModManifest(host, plan.NewApproval);
        return plan;
    }

    internal static Plan Prepare(HttpClient http, Uri server, string cache, string trusted,
        bool hasMelonLoader, bool requiresRedirectMod, Func<string, ClientModManifest, string, bool>? approve)
    {
        var manifestUri = new Uri(server, ClientModManifest.Route);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound && trusted.Length == 0)
            return new Plan("", UsesManifest: false);
        response.EnsureSuccessStatusCode();

        using var contents = new MemoryStream();
        CopyBounded(response.Content, contents, ClientModManifest.MaxManifestBytes, deadline.Token);
        byte[] json = contents.ToArray();
        ClientModManifest manifest = JsonSerializer.Deserialize<ClientModManifest>(json)
            ?? throw new InvalidDataException("The server returned an empty client mod manifest.");
        Validate(manifest);
        if (requiresRedirectMod &&
            !manifest.Mods.Any(mod => mod.Name.Equals("JondoFix.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This server must include JondoFix.dll in its client mod list to support the Windows client.");

        bool bundledOnly = manifest.Mods.All(mod => Matches(BundledPath(mod), mod));
        if (!hasMelonLoader)
        {
            if (requiresRedirectMod || !bundledOnly)
                throw new InvalidOperationException("This server requires client mods. Install MelonLoader to join it.");
            return new Plan("", UsesManifest: false);
        }

        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(json));
        bool approvalNeeded = (trusted.Length != 0 || !bundledOnly) &&
            !fingerprint.Equals(trusted, StringComparison.OrdinalIgnoreCase);
        if (approvalNeeded)
        {
            string host = server.Host;
            if (approve == null)
                throw new InvalidOperationException($"The mod manifest for {host} requires approval. SHA-256 {fingerprint}.");
            if (!approve(host, manifest, fingerprint)) throw new ApprovalDeclinedException();
        }

        string newApproval = approvalNeeded ? fingerprint : "";
        if (manifest.Mods.Length == 0)
            return new Plan("", UsesManifest: true, newApproval);
        string blobs = Path.Combine(cache, "blobs");
        Directory.CreateDirectory(blobs);
        foreach (ClientModEntry mod in manifest.Mods)
        {
            string target = Path.Combine(blobs, mod.Sha256.ToLowerInvariant() + ".dll");
            if (Matches(target, mod)) continue;
            string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string? bundled = BundledPath(mod);
                if (Matches(bundled, mod))
                    File.Copy(bundled!, staged);
                else
                    Download(http, new Uri(manifestUri.AbsoluteUri + "/" + Uri.EscapeDataString(mod.Name)), mod, staged);
                if (!Matches(staged, mod)) throw new InvalidDataException($"The DLL for {mod.Name} does not match the approved manifest.");
                File.Move(staged, target, overwrite: true);
            }
            finally
            {
                File.Delete(staged);
            }
        }

        string manifestDirectory = Path.Combine(cache, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        string path = Path.Combine(manifestDirectory, fingerprint + ".json");
        if (Matches(path, json.Length, fingerprint)) return new Plan(path, UsesManifest: true, newApproval);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(pending, json);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
        return new Plan(path, UsesManifest: true, newApproval);
    }

    internal sealed class ApprovalDeclinedException : Exception { }

    internal static void Validate(ClientModManifest manifest)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Mods is not { Length: <= ClientModManifest.MaxEntries })
            throw new InvalidDataException("The server returned an invalid client mod list.");
        foreach (ClientModEntry mod in manifest.Mods)
        {
            if (mod == null || !ClientModManifest.IsDllName(mod.Name) ||
                !names.Add(mod.Name) || !ClientModManifest.IsDllSize(mod.Size) || mod.Sha256 is not { Length: 64 } ||
                !mod.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("The server returned an invalid client mod entry.");
        }
    }

    internal static void Activate(ClientInstallation client, Plan plan, System.Diagnostics.ProcessStartInfo start)
    {
        if (!plan.UsesManifest) return;
        string clientRoot = client.Root;
        if (plan.ManifestPath.Length > 0)
        {
            string melonLoader = Path.Combine(clientRoot, "MelonLoader", "net6", "MelonLoader.dll");
            if (!File.Exists(melonLoader) ||
                System.Reflection.AssemblyName.GetAssemblyName(melonLoader).Version < new Version(0, 7, 3, 0))
                throw new InvalidOperationException("Server mod loading requires MelonLoader 0.7.3 or newer.");
            string bootstrap = Path.Combine(Paths.Root, "JondoModLoader", ClientModManifest.BootstrapFileName);
            if (!File.Exists(bootstrap)) throw new FileNotFoundException("The Jondo mod loader is missing from this launcher installation.", bootstrap);
            string plugins = Path.Combine(clientRoot, "Plugins");
            Directory.CreateDirectory(plugins);
            string installed = Path.Combine(plugins, ClientModManifest.BootstrapFileName);
            if (!File.Exists(installed) || !LauncherService.SameContent(installed, bootstrap))
            {
                string staged = installed + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(bootstrap, staged);
                    ArchiveExtractor.ReplaceFile(staged, installed);
                }
                finally
                {
                    File.Delete(staged);
                }
            }
            start.Environment[ClientModManifest.LaunchManifestEnvironmentVariable] = WineClient.Runs(client)
                ? WineClient.WindowsPath(plan.ManifestPath)
                : plan.ManifestPath;
        }

        // Older launcher versions and first-run setup placed these in MelonLoader's default scan folder.
        // Set them aside so only the current manifest supplies these assemblies for this launch.
        foreach (string mod in LauncherService.ShippedMods)
        {
            string legacy = Path.Combine(clientRoot, "Mods", mod + ".dll");
            if (File.Exists(legacy)) File.Move(legacy, legacy + ".jondo-legacy-" + Guid.NewGuid().ToString("N"));
        }
    }

    private static string? BundledPath(ClientModEntry mod)
    {
        string? bundled = LauncherService.ShippedMods.FirstOrDefault(
            name => string.Equals(name + ".dll", mod.Name, StringComparison.OrdinalIgnoreCase));
        return bundled == null ? null : LauncherService.ShippedModPath(Paths.Root, bundled);
    }

    private static bool Matches(string? path, ClientModEntry mod) => Matches(path, mod.Size, mod.Sha256);

    private static bool Matches(string? path, long size, string sha256)
    {
        if (path == null || !File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return file.Length == size &&
            Convert.ToHexStringLower(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void Download(HttpClient http, Uri uri, ClientModEntry mod, string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var file = File.Create(target);
        CopyBounded(response.Content, file, mod.Size, deadline.Token);
    }

    private static void CopyBounded(HttpContent content, Stream target, long limit, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("The server response exceeds its allowed size.");
        using var source = content.ReadAsStream(cancel);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = source.ReadAsync(buffer.AsMemory(), cancel).AsTask().GetAwaiter().GetResult()) != 0)
        {
            if (read > limit - target.Length) throw new InvalidDataException("The server response exceeds its allowed size.");
            target.Write(buffer, 0, read);
        }
    }
}
