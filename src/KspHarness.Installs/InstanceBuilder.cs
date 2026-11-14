using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KspHarness.Installs;

public sealed class InstanceBuildOptions
{
    /// <summary>The CKAN executable; null for <c>CKAN_EXE</c> or the PATH.</summary>
    public string? CkanExecutable { get; init; }

    public LockMode LockMode { get; init; } = LockMode.Auto;

    /// <summary>Hard-link the large read-only files when the volume allows it; false always copies.</summary>
    public bool UseHardLinks { get; init; } = true;

    public TimeSpan CkanTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Progress messages, one line each.</summary>
    public Action<string>? Log { get; init; }

    public HttpClient? Http { get; init; }
}

/// <summary>
/// Builds instances: copies of a starting install with a definition's steps applied, kept under
/// <c>&lt;home&gt;/instances/&lt;definition&gt;-&lt;hash12&gt;</c> and reused while the hash matches.
/// </summary>
public static class InstanceBuilder
{
    /// <summary>Game versions CKAN should accept for a Retooled install, which reports itself as 1.13.0.</summary>
    public static readonly IReadOnlyList<string> RetooledCompatibleVersions = ["1.12", "1.12.5"];

    private const string BuildingFolder = ".building";

    private const string OwnerSuffix = ".owner";

    // A build folder without an owner file names no process to check, so only its age can show that the build was
    // abandoned; six hours is far longer than any build takes.
    private static readonly TimeSpan UnownedBuildAge = TimeSpan.FromHours(6);

    public static async Task<Instance> BuildAsync(
        InstallDefinition definition,
        StartingInstall start,
        HarnessHome home,
        string? lockFile,
        InstanceBuildOptions? options = null,
        CancellationToken cancel = default)
    {
        options ??= new InstanceBuildOptions();
        var plan = InstallPlan.Create(definition);
        var locked = LockedLayers(plan, start, lockFile, options.LockMode);
        RemoveAbandonedBuilds(home, options);

        Instance? parent = null;
        var lockLayers = new List<LockLayer>();
        for (int i = 0; i < plan.Layers.Count; i++)
        {
            var (instance, lockLayer) = await BuildLayerAsync(plan.Layers, i, lockLayers, start, parent, locked[i],
                home, options, cancel);
            lockLayers.Add(lockLayer);
            parent = instance;
        }

        if (lockFile is not null)
        {
            new LockFile { Start = start.Kind, Layers = lockLayers }.Save(lockFile);
        }

        return Instance.Open(parent!.Root, lockFile);
    }

    /// <summary>The lock entry to install from for each layer, or null where CKAN has to resolve it.</summary>
    private static LockLayer?[] LockedLayers(InstallPlan plan, StartingInstall start, string? lockFile, LockMode mode)
    {
        var result = new LockLayer?[plan.Layers.Count];
        if (lockFile is null)
        {
            return mode == LockMode.Frozen
                ? throw new InstallException("LockMode.Frozen needs a lock file")
                : result;
        }

        var existing = mode == LockMode.Update ? null : LockFile.Load(lockFile);
        if (existing is not null && existing.Start == start.Kind)
        {
            // A layer's modules depend on what the layers below it installed, so a mismatch invalidates the rest.
            for (int i = 0; i < plan.Layers.Count && i < existing.Layers.Count; i++)
            {
                if (!existing.Layers[i].Matches(plan.Layers[i]))
                {
                    break;
                }

                result[i] = existing.Layers[i];
            }
        }

        if (mode == LockMode.Frozen && result.Any(layer => layer is null))
        {
            string why = existing is null ? "is missing"
                : existing.Start != start.Kind ? $"was resolved for a {existing.Start} install, not {start.Kind}"
                : "does not match the definition";
            throw new InstallException($"lock file {lockFile} {why}; build once with LockMode.Update to rewrite it");
        }

        return result;
    }

    /// <summary>
    /// Builds the instance of one layer from the starting install, applying every lower layer's steps again with
    /// their locked modules before its own. The lower layer's instance is not copied: instances are played in place,
    /// so it holds saves, logs, caches and settings that the game wrote and the hash does not cover, and a running
    /// game may hold its files open.
    /// </summary>
    private static async Task<(Instance Instance, LockLayer Lock)> BuildLayerAsync(
        IReadOnlyList<InstallLayer> layers,
        int index,
        IReadOnlyList<LockLayer> lowerLocks,
        StartingInstall start,
        Instance? parent,
        LockLayer? locked,
        HarnessHome home,
        InstanceBuildOptions options,
        CancellationToken cancel)
    {
        var layer = layers[index];
        string name = SafeName(layer.Definition.Name);
        var archives = layer.ArchiveSteps.Select(step => new LockedArchive(step.Name, step.Url, step.Sha256)).ToList();
        if (locked is null && !layer.CkanSteps.Any())
        {
            locked = new LockLayer { Definition = layer.DefinitionId, Archives = archives };
        }

        if (locked is not null)
        {
            string hash = Hash(layer, start, parent, locked.Ckan);
            string ready = Path.Combine(home.InstancesDir, $"{name}-{hash[..12]}");
            if (Instance.IsReady(ready))
            {
                options.Log?.Invoke($"{name}: reusing {ready}");
                return (Instance.Open(ready), locked);
            }
        }

        string buildId = $"{name}-{Guid.NewGuid().ToString("N")[..8]}";
        string temp = Path.Combine(home.InstancesDir, BuildingFolder, buildId);
        string owner = temp + OwnerSuffix;
        string ckanLog = Path.Combine(home.BuildLogsDir, buildId + ".ckan.log");

        // Written before the folder exists, so that a folder without one is never a build in progress.
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(owner, BuildOwner.Current());

        try
        {
            options.Log?.Invoke($"{name}: copying {start.Root}");
            var copier = new TreeCopier(start.Layout.DataDirRelative, options.UseHardLinks);
            var stats = copier.Copy(start.Root, temp, new HashSet<string> { Instance.HarnessFolder }, cancel);
            options.Log?.Invoke(
                $"{name}: {stats.Linked} files linked, {stats.Copied} copied ({stats.CopiedBytes >> 20} MiB)");
            if (stats.Refused > 0)
            {
                options.Log?.Invoke($"{name}: {stats.Refused} files could not be linked and were copied (no write "
                    + $"access to them in {start.Root}, or at the link limit)");
            }

            var ckan = new CkanRun(start, temp, ckanLog, options);
            for (int i = 0; i < index; i++)
            {
                options.Log?.Invoke($"{name}: applying {layers[i].Definition.Name}");
                await ApplyLayerAsync(layers[i], lowerLocks[i], temp, ckan, home, options, cancel);
            }

            var modules = await ApplyLayerAsync(layer, locked, temp, ckan, home, options, cancel);

            locked ??= new LockLayer
            {
                Definition = layer.DefinitionId,
                Requested = layer.CkanSteps.Select(step => step.Spec).ToList(),
                Ckan = modules,
                Archives = archives,
            };

            string hash = Hash(layer, start, parent, locked.Ckan);
            string final = Path.Combine(home.InstancesDir, $"{name}-{hash[..12]}");
            if (Instance.IsReady(final))
            {
                options.Log?.Invoke($"{name}: an identical instance exists, reusing {final}");
                IoRetry.DeleteTree(temp);
                File.Delete(ckanLog);
                return (Instance.Open(final), locked);
            }

            string harness = Path.Combine(temp, Instance.HarnessFolder);
            Directory.CreateDirectory(harness);
            if (File.Exists(ckanLog))
            {
                File.Move(ckanLog, Path.Combine(harness, "ckan.log"));
            }

            var record = new InstanceRecord
            {
                Name = name,
                Definition = layer.DefinitionId,
                Hash = hash,
                Parent = parent?.Hash,
                StartRoot = start.Root,
                StartKind = start.Kind,
                StartIdentity = start.IdentityHash,
                Steps = [.. layer.Fingerprints, layer.Popups.Fingerprint],
                Ckan = locked.Ckan,
                CreatedUtc = DateTime.UtcNow,
            };
            File.WriteAllText(Path.Combine(harness, Instance.RecordFileName),
                JsonSerializer.Serialize(record, InstanceRecord.JsonOptions));

            if (!MoveIntoPlace(temp, final))
            {
                options.Log?.Invoke($"{name}: another build finished {final} first, reusing it");
                IoRetry.DeleteTree(temp);
                return (Instance.Open(final), locked);
            }

            options.Log?.Invoke($"{name}: built {final}");
            return (Instance.Open(final), locked);
        }
        catch
        {
            try
            {
                IoRetry.DeleteTree(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                options.Log?.Invoke($"{name}: could not delete {temp}: {e.Message}");
            }

            throw;
        }
        finally
        {
            File.Delete(owner);
        }
    }

    /// <summary>
    /// Renames a finished build into place; false when another build of the same instance got there first, whose
    /// result is just as good. The rename is atomic, so a folder at the final path is always finished unless someone
    /// broke it by hand.
    /// </summary>
    internal static bool MoveIntoPlace(string temp, string final)
    {
        try
        {
            return IoRetry.Run(() => Directory.Move(temp, final), settled: () => Instance.IsReady(final));
        }
        catch (IOException e) when (Directory.Exists(final))
        {
            throw new InstallException($"{final} exists but is not a finished instance; delete it and build again", e);
        }
    }

    /// <summary>Applies one layer's steps to a game folder and returns the CKAN modules new in that layer.</summary>
    private static async Task<List<LockedModule>> ApplyLayerAsync(
        InstallLayer layer,
        LockLayer? locked,
        string gameDir,
        CkanRun ckan,
        HarnessHome home,
        InstanceBuildOptions options,
        CancellationToken cancel)
    {
        var modules = await ApplyCkanAsync(layer, locked, ckan, options, cancel);
        await ApplyArchivesAsync(layer, gameDir, home, options, cancel);
        ApplyKnownPopups(layer, ckan.Start, gameDir, options);
        ApplyDirectories(layer, gameDir);
        ApplySettings(layer, gameDir);
        return modules;
    }

    private static async Task<List<LockedModule>> ApplyCkanAsync(
        InstallLayer layer,
        LockLayer? locked,
        CkanRun ckan,
        InstanceBuildOptions options,
        CancellationToken cancel)
    {
        var ckanSteps = layer.CkanSteps.ToList();
        var compat = layer.Steps.OfType<CompatibleVersionStep>().Select(step => step.Version).ToList();
        if (ckanSteps.Count == 0 && compat.Count == 0)
        {
            return [];
        }

        if (ckan.Start.IsRetooled)
        {
            compat.AddRange(RetooledCompatibleVersions);
        }

        await ckan.AddCompatibleVersionsAsync(compat, cancel);
        if (ckanSteps.Count == 0)
        {
            return [];
        }

        await ckan.UpdateOnceAsync(layer.Definition.Name, cancel);

        var before = CkanClient.ReadInstalled(ckan.GameDir);
        var installedVersions = before.ToDictionary(module => module.Identifier, module => module.Version,
            StringComparer.OrdinalIgnoreCase);
        var install = new List<string>();
        var upgrade = new List<string>();

        List<(string Identifier, string? Version)> wanted = locked is not null
            ? locked.Ckan.Select(module => (module.Identifier, (string?)module.Version)).ToList()
            : ckanSteps.Select(step => (step.Identifier, step.Version)).ToList();

        foreach (var (identifier, version) in wanted)
        {
            string spec = version is null ? identifier : $"{identifier}={version}";
            if (!installedVersions.TryGetValue(identifier, out string? current))
            {
                install.Add(spec);
            }
            else if (version is not null && version != current)
            {
                upgrade.Add(spec);
            }
        }

        if (install.Count > 0)
        {
            options.Log?.Invoke($"{layer.Definition.Name}: ckan install {string.Join(' ', install)}");
            await ckan.Client.InstallAsync(install, cancel);
        }

        if (upgrade.Count > 0)
        {
            options.Log?.Invoke($"{layer.Definition.Name}: ckan upgrade {string.Join(' ', upgrade)}");
            await ckan.Client.UpgradeAsync(upgrade, cancel);
        }

        // CKAN skips a module that is incompatible with the instance's game versions, says "Nothing to install" and
        // still exits with 0, so success is judged by what the registry holds afterwards.
        var after = CkanClient.ReadInstalled(ckan.GameDir);
        var afterVersions = after.ToDictionary(module => module.Identifier, module => module.Version,
            StringComparer.OrdinalIgnoreCase);
        var missing = wanted
            .Where(module => !afterVersions.TryGetValue(module.Identifier, out string? version)
                || (module.Version is not null && module.Version != version))
            .Select(module => module.Version is null ? module.Identifier : $"{module.Identifier}={module.Version}")
            .ToList();

        if (missing.Count > 0)
        {
            throw new InstallException(
                $"CKAN did not install {string.Join(", ", missing)} (incompatible with the instance's game versions? "
                + $"add CompatibleVersion steps). Log: {ckan.LogPath}");
        }

        var added = after.Except(before).ToList();
        if (locked is not null)
        {
            RequireLockedModules(layer.Definition.Name, locked, added, ckan.LogPath);
        }

        return added;
    }

    /// <summary>
    /// The instance is stored under a hash of the locked modules, so the layer must add exactly those. CKAN can add
    /// others when a locked version's metadata changed its dependencies, or when another module now provides one.
    /// </summary>
    internal static void RequireLockedModules(string layerName, LockLayer locked, IReadOnlyList<LockedModule> added,
        string logPath)
    {
        var extra = added.Except(locked.Ckan).Select(module => module.Spec).ToList();
        var absent = locked.Ckan.Except(added).Select(module => module.Spec).ToList();
        if (extra.Count == 0 && absent.Count == 0)
        {
            return;
        }

        var problems = new List<string>();
        if (extra.Count > 0)
        {
            problems.Add($"it also installed {string.Join(", ", extra)}");
        }

        if (absent.Count > 0)
        {
            problems.Add($"{string.Join(", ", absent)} came from a lower layer");
        }

        throw new InstallException(
            $"{layerName}: CKAN's result does not match the lock file: {string.Join("; ", problems)}. Build with "
            + $"LockMode.Update to resolve the layer again. Log: {logPath}");
    }

    private static async Task ApplyArchivesAsync(InstallLayer layer, string gameDir, HarnessHome home,
        InstanceBuildOptions options, CancellationToken cancel)
    {
        var cache = new ArchiveCache(home.ArchiveCacheDir, options.Http);
        foreach (var step in layer.ArchiveSteps)
        {
            string archive = await cache.GetAsync(step.Url, step.Sha256, cancel);
            string dest = Path.Combine(gameDir, "GameData");
            if (step.Subfolder is not null)
            {
                dest = RelativePaths.Resolve(dest, step.Subfolder);
            }

            int files = ArchiveCache.Unpack(archive, dest);
            options.Log?.Invoke($"{layer.Definition.Name}: unpacked {step.Name} ({files} files)");
        }
    }

    // After the mods are in and before the definition's own files, which may hold other answers.
    private static void ApplyKnownPopups(InstallLayer layer, StartingInstall start, string gameDir,
        InstanceBuildOptions options)
    {
        var applied = KnownPopups.Apply(gameDir, layer.Popups.KnownAnswers, start);
        if (applied.Count > 0)
        {
            options.Log?.Invoke($"{layer.Definition.Name}: answered known popups: {string.Join(", ", applied)}");
        }
    }

    private static void ApplyDirectories(InstallLayer layer, string gameDir)
    {
        foreach (var step in layer.Steps.OfType<DirectoryStep>())
        {
            if (DirectoryStep.HashContent(step.SourceDir) != step.ContentHash)
            {
                throw new InstallException($"{step.SourceDir} changed while the instance was being built");
            }

            TreeCopier.Overlay(step.SourceDir, RelativePaths.Resolve(gameDir, step.DestRelative));
        }
    }

    private static void ApplySettings(InstallLayer layer, string gameDir)
    {
        var settings = layer.Steps.OfType<SettingStep>().ToList();
        if (settings.Count > 0)
        {
            SettingsFile.Apply(Path.Combine(gameDir, SettingsFile.FileName),
                settings.Select(step => KeyValuePair.Create(step.Key, step.Value)));
        }
    }

    /// <summary>
    /// The instance hash: what it was copied from, the definition's steps, its popup answers and the CKAN module
    /// versions. Steps are applied by kind (compat, CKAN, archives, known popup answers, folders, settings), so their
    /// order only matters within a kind.
    /// </summary>
    internal static string Hash(InstallLayer layer, StartingInstall start, Instance? parent,
        IReadOnlyList<LockedModule> modules)
    {
        var text = new StringBuilder();
        text.Append("ksp-harness instance 1\n");
        text.Append("start ").Append(start.IdentityHash).Append('\n');
        text.Append("parent ").Append(parent?.Hash ?? "none").Append('\n');
        text.Append("definition ").Append(layer.DefinitionId).Append('\n');
        foreach (string fingerprint in layer.Fingerprints)
        {
            text.Append("step ").Append(fingerprint).Append('\n');
        }

        text.Append(layer.Popups.Fingerprint).Append('\n');

        foreach (var module in modules.OrderBy(module => module.Identifier, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("module ").Append(module.Spec).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
    }

    /// <summary>
    /// Deletes build folders whose build is gone, killed before it could clean up; each holds a copy of most of an
    /// install. Deleting a hard link removes only that name, so the starting install keeps its file.
    /// </summary>
    internal static void RemoveAbandonedBuilds(HarnessHome home, InstanceBuildOptions options)
    {
        string building = Path.Combine(home.InstancesDir, BuildingFolder);
        if (!Directory.Exists(building))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(building))
        {
            string owner = folder + OwnerSuffix;
            bool abandoned = File.Exists(owner)
                ? !BuildOwner.IsAlive(File.ReadAllText(owner))
                : DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > UnownedBuildAge;
            if (!abandoned)
            {
                continue;
            }

            try
            {
                options.Log?.Invoke($"removing abandoned build {folder}");
                IoRetry.DeleteTree(folder);
                File.Delete(owner);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                options.Log?.Invoke($"could not remove {folder}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The CKAN client of one build. The lower layers' steps run in the same folder before the layer's own, so the
    /// repository data is refreshed once and each compatible version is added once.
    /// </summary>
    private sealed class CkanRun(StartingInstall start, string gameDir, string logPath, InstanceBuildOptions options)
    {
        private readonly HashSet<string> compatible = new(StringComparer.Ordinal);
        private CkanClient? client;
        private bool updated;

        public StartingInstall Start => start;

        public string GameDir => gameDir;

        public string LogPath => logPath;

        public CkanClient Client => client ??= new CkanClient(CkanClient.FindExecutable(options.CkanExecutable),
            gameDir, logPath, options.CkanTimeout);

        public async Task AddCompatibleVersionsAsync(IEnumerable<string> versions, CancellationToken cancel)
        {
            var added = versions.Where(compatible.Add).ToList();
            if (added.Count > 0)
            {
                await Client.AddCompatibleVersionsAsync(added, cancel);
            }
        }

        public async Task UpdateOnceAsync(string layerName, CancellationToken cancel)
        {
            if (updated)
            {
                return;
            }

            options.Log?.Invoke($"{layerName}: updating CKAN metadata");
            await Client.UpdateAsync(cancel);
            updated = true;
        }
    }
}
