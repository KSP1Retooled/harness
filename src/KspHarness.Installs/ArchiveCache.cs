using System.IO.Compression;
using System.Security.Cryptography;

namespace KspHarness.Installs;

/// <summary>Downloads archives once into <c>&lt;home&gt;/cache/archives/&lt;sha256&gt;</c> and unpacks them.</summary>
public sealed class ArchiveCache
{
    private readonly string cacheDir;
    private readonly HttpClient http;

    public ArchiveCache(string cacheDir, HttpClient? http = null)
    {
        this.cacheDir = cacheDir;
        this.http = http ?? SharedHttp.Value;
    }

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KspHarness/1");
        return client;
    });

    /// <summary>
    /// Returns the cached file, downloading it first when it is missing or no longer has its hash (a damaged disk or a
    /// partly synced folder). Supports http(s) and file URLs.
    /// </summary>
    public async Task<string> GetAsync(string url, string sha256, CancellationToken cancel)
    {
        sha256 = sha256.ToLowerInvariant();
        string path = Path.Combine(cacheDir, sha256);
        if (File.Exists(path))
        {
            // Mod archives are small enough that hashing on every use costs little next to unpacking them.
            if (await HashFileAsync(path, cancel) == sha256)
            {
                return path;
            }

            File.Delete(path);
        }

        Directory.CreateDirectory(cacheDir);
        string partial = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            var uri = new Uri(url);
            if (uri.IsFile)
            {
                File.Copy(uri.LocalPath, partial);
            }
            else
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(partial);
                await response.Content.CopyToAsync(output, cancel);
            }

            string actual = await HashFileAsync(partial, cancel);
            if (actual != sha256)
            {
                throw new InstallException($"{url} has sha256 {actual}, expected {sha256}");
            }

            // Another build may have finished the same download meanwhile; its file is just as good.
            try
            {
                File.Move(partial, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            File.Delete(partial);
        }

        return path;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancel)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
    }

    /// <summary>
    /// Unpacks a zip into <paramref name="destDir"/>. When the archive has a <c>GameData</c> folder at its top, or
    /// inside its only top-level folder, only that folder's content is unpacked, as CKAN does for mods without
    /// install instructions; readme and license files beside it are left out.
    /// </summary>
    public static int Unpack(string archive, string destDir)
    {
        using var zip = ZipFile.OpenRead(archive);
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\'))
            .Select(entry => (Entry: entry, Path: entry.FullName.Replace('\\', '/').TrimStart('/')))
            .ToList();

        string prefix = FindGameDataPrefix(files.Select(file => file.Path).ToList());
        int count = 0;
        Directory.CreateDirectory(destDir);

        foreach (var (entry, path) in files)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = RelativePaths.Resolve(destDir, path[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            entry.ExtractToFile(target);
            count++;
        }

        return count;
    }

    internal static string FindGameDataPrefix(IReadOnlyList<string> paths)
    {
        static string? TopFolder(string path)
        {
            int slash = path.IndexOf('/');
            return slash < 0 ? null : path[..slash];
        }

        var topFolders = paths.Select(TopFolder).Where(folder => folder is not null).Distinct().ToList();
        string? gameData = topFolders.FirstOrDefault(folder => folder!.Equals("GameData", StringComparison.OrdinalIgnoreCase));
        if (gameData is not null)
        {
            return gameData + "/";
        }

        bool singleFolder = topFolders.Count == 1 && paths.All(path => TopFolder(path) is not null);
        if (singleFolder)
        {
            string outer = topFolders[0]! + "/";
            var inner = paths.Select(path => TopFolder(path[outer.Length..]))
                .FirstOrDefault(folder => folder is not null && folder.Equals("GameData", StringComparison.OrdinalIgnoreCase));
            if (inner is not null)
            {
                return outer + inner + "/";
            }
        }

        return "";
    }
}
