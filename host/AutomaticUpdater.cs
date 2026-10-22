using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace MalodyV.ReworkOverlay;

internal sealed record PreparedUpdate(string WorkDirectory, string PackageDirectory, string PluginRoot, UpdateManifest Manifest);
internal sealed record UpdatePlan(string PluginRoot, UpdateManifest Manifest);

internal static class AutomaticUpdater
{
    internal const string HostDirectoryName = "MalodyVReworkOverlay";
    internal const string PluginFileName = "MalodyV.ReworkOverlay.Plugin.dll";
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };
    internal static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HostDirectoryName, "updates");
    internal static string ResultPath => Path.Combine(UpdatesDirectory, "last-update.json");

    public static async Task<PreparedUpdate> PrepareAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (!UpdateChecker.IsValidManifest(manifest))
            throw new InvalidDataException("Invalid release manifest.");
        var pluginRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        ValidatePluginRoot(pluginRoot);
        var work = Path.Combine(UpdatesDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "release.zip");
            await DownloadAsync(new Uri(manifest.PackageUrl!), archive, cancellationToken);
            if (!UpdateChecker.Matches(archive, manifest.PackageSha256))
                throw new InvalidDataException("Release ZIP failed SHA-256 verification.");
            var package = Path.Combine(work, "package");
            ExtractPackage(archive, package);
            ValidatePackage(package, manifest);
            return new(work, package, pluginRoot, manifest);
        }
        catch
        {
            Directory.Delete(work, true);
            throw;
        }
    }

    private static async Task DownloadAsync(Uri uri, string target, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("Missing release redirect.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "release-assets.githubusercontent.com")
                    throw new InvalidDataException("Release redirected outside GitHub release assets.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException("Release exceeds the download limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(target);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumDownloadBytes)
                    throw new InvalidDataException("Release exceeds the download limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return;
        }
        throw new InvalidDataException("Too many release redirects.");
    }

    internal static void ExtractPackage(string archivePath, string target)
    {
        Directory.CreateDirectory(target);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 30_000)
            throw new InvalidDataException("Too many release entries.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            var segments = relative.TrimEnd('/').Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
                    || segment.EndsWith('.') || segment.EndsWith(' ')
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || (segments[0] != HostDirectoryName && relative != PluginFileName && relative != "manifest.json")
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || !paths.Add(relative.TrimEnd('/')))
                throw new InvalidDataException("Unsafe release entry.");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes)
                throw new InvalidDataException("Expanded release exceeds the size limit.");
            var destination = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release entry escapes staging.");
            if (relative.EndsWith('/'))
                Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
        }
    }

    internal static void ValidatePackage(string package, UpdateManifest expected)
    {
        var bundled = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(package, "manifest.json")));
        var host = Path.Combine(package, HostDirectoryName);
        if (bundled?.Version != expected.Version
            || !UpdateChecker.Matches(Path.Combine(package, PluginFileName), expected.PluginSha256)
            || !UpdateChecker.Matches(Path.Combine(host, "MalodyV.ReworkOverlay.exe"), expected.HostSha256)
            || !UpdateChecker.Matches(Path.Combine(host, "MalodyV.ReworkOverlay.dll"), expected.HostAssemblySha256)
            || !File.Exists(Path.Combine(host, "MalodyV.ReworkOverlay.runtimeconfig.json"))
            || !File.Exists(Path.Combine(host, "nonebot_plugin_rework", "tosu_plugin", "index.html")))
            throw new InvalidDataException("Release contents do not match the published build.");
    }

    public static void StartInstaller(PreparedUpdate update)
    {
        var planPath = Path.Combine(update.WorkDirectory, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(new UpdatePlan(update.PluginRoot, update.Manifest)));
        var executable = Path.Combine(update.PackageDirectory, HostDirectoryName, "MalodyV.ReworkOverlay.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(planPath);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
    }

    public static async Task<int> RunInstallerAsync(string planPath)
    {
        string? pluginRoot = null;
        var hostClosed = false;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath))
                ?? throw new InvalidDataException("Missing update plan.");
            pluginRoot = plan.PluginRoot;
            ValidatePluginRoot(pluginRoot);
            var package = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            ValidatePackage(package, plan.Manifest);
            var gameExe = Path.Combine(Directory.GetParent(Directory.GetParent(pluginRoot)!.FullName)!.FullName, "Malody V.exe");
            foreach (var game in FindProcesses("Malody V", gameExe))
            {
                using (game)
                    await game.WaitForExitAsync();
            }
            var hostExe = Path.Combine(pluginRoot, HostDirectoryName, "MalodyV.ReworkOverlay.exe");
            foreach (var host in FindProcesses("MalodyV.ReworkOverlay", hostExe))
            {
                using (host)
                {
                    host.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await host.WaitForExitAsync(timeout.Token);
                }
            }
            hostClosed = true;
            ApplyPackage(package, pluginRoot, plan.Manifest);
            WriteResult(true, plan.Manifest.Version, null);
            StartHost(pluginRoot);
            return 0;
        }
        catch (Exception exception)
        {
            WriteResult(false, null, exception.Message);
            if (hostClosed && pluginRoot is not null)
                StartHost(pluginRoot, true);
            return 1;
        }
    }

    internal static IEnumerable<Process> FindProcesses(string name, string executable)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            var matches = false;
            try
            {
                matches = string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            if (matches)
                yield return process;
            else
                process.Dispose();
        }
    }

    internal static void ValidatePluginRoot(string pluginRoot)
    {
        var root = Path.GetFullPath(pluginRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(root).Equals("plugins", StringComparison.OrdinalIgnoreCase)
            || !Directory.GetParent(root)!.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Automatic updates must target BepInEx/plugins.");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update destination contains a directory link.");
        }
    }

    internal static void ApplyPackage(string package, string pluginRoot, UpdateManifest manifest)
    {
        ValidatePluginRoot(pluginRoot);
        ValidatePackage(package, manifest);
        var work = Path.Combine(Directory.GetParent(pluginRoot)!.FullName, "overlay-updates", Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, "stage");
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        var moves = new List<(string Source, string Destination)>();
        var committed = false;
        try
        {
            CopyDirectory(Path.Combine(package, HostDirectoryName), Path.Combine(stage, HostDirectoryName));
            File.Copy(Path.Combine(package, PluginFileName), Path.Combine(stage, PluginFileName));
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest));
            foreach (var name in new[] { HostDirectoryName, PluginFileName, "manifest.json" })
            {
                var installed = Path.Combine(pluginRoot, name);
                if (File.Exists(installed) || Directory.Exists(installed))
                    Move(installed, Path.Combine(backup, name), moves);
                Move(Path.Combine(stage, name), installed, moves);
            }
            committed = true;
        }
        catch
        {
            foreach (var move in moves.AsEnumerable().Reverse())
                Move(move.Destination, move.Source, null);
            throw;
        }
        finally
        {
            if (committed || !Directory.EnumerateFileSystemEntries(backup).Any())
                Directory.Delete(work, true);
        }
    }

    private static void Move(string source, string destination, List<(string Source, string Destination)>? moves)
    {
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
        moves?.Add((source, destination));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update package contains a link.");
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
                CopyDirectory(entry.FullName, target);
            else
                File.Copy(entry.FullName, target);
        }
    }

    private static void WriteResult(bool success, string? version, string? error)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        File.WriteAllText(ResultPath, JsonSerializer.Serialize(new { success, version, error, time = DateTimeOffset.UtcNow }));
    }

    private static void StartHost(string pluginRoot, bool recovery = false)
    {
        var executable = Path.Combine(pluginRoot, HostDirectoryName, "MalodyV.ReworkOverlay.exe");
        if (File.Exists(executable))
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (recovery)
                start.ArgumentList.Add("--update-recovery");
            Process.Start(start);
        }
    }
}
