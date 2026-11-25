using System.Security.Cryptography;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Network;

internal static class ClientModCatalog
{
    public static string[] ListAvailable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory)) return [];

            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Select(Path.GetFileName)
                .Where(ClientModManifest.IsDllName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Control] Could not discover client mods: {ex.Message}");
            return [];
        }
    }

    public static ClientModManifest CreateManifest(string directory, IReadOnlyList<string> names)
    {
        var entries = new List<ClientModEntry>(Math.Min(names.Count, ClientModManifest.MaxEntries));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            if (!ClientModManifest.IsDllName(name) || !seen.Add(name))
                throw new InvalidDataException($"Invalid or repeated client mod filename: {name}");

            using FileStream? file = OpenFile(directory, name);
            if (file == null) continue;

            if (entries.Count == ClientModManifest.MaxEntries)
                throw new InvalidDataException($"Client mod order exceeds {ClientModManifest.MaxEntries} available DLLs.");

            entries.Add(new ClientModEntry(name, file.Length, Convert.ToHexStringLower(SHA256.HashData(file))));
        }

        return new ClientModManifest(entries.ToArray());
    }

    public static FileStream? OpenDownload(string directory, IReadOnlyList<string> names, string name)
    {
        if (!ClientModManifest.IsDllName(name) || !names.Contains(name, StringComparer.Ordinal)) return null;

        return OpenFile(directory, name);
    }

    private static FileStream? OpenFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        try
        {
            const FileAttributes DirectoryOrLink = FileAttributes.Directory | FileAttributes.ReparsePoint;
            if ((File.GetAttributes(directory) & DirectoryOrLink) != FileAttributes.Directory
                || (File.GetAttributes(path) & DirectoryOrLink) != 0) return null;

            if (!ClientModManifest.IsDllSize(new FileInfo(path).Length)) throw InvalidSize(name);

            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (ClientModManifest.IsDllSize(file.Length)) return file;

            file.Dispose();
            throw InvalidSize(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static InvalidDataException InvalidSize(string name)
        => new($"Client mod {name} must be between 1 and {ClientModManifest.MaxDllBytes} bytes.");

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
