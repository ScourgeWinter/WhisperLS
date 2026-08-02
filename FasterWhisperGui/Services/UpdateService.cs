using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FasterWhisperGui.Services;

internal sealed class UpdateService
{
    private const string Repository = "ScourgeWinter/WhisperLS";
    private const string LatestReleaseApi = "https://api.github.com/repos/ScourgeWinter/WhisperLS/releases/latest";
    private readonly HttpClient httpClient;
    private readonly string dataDirectory;
    private readonly string statePath;
    private readonly string distributionVariant;

    public UpdateService()
    {
        dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS");
        statePath = Path.Combine(dataDirectory, "updater-state.json");
        distributionVariant = ReadDistributionVariant();
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("WhisperLS", InstalledVersion.ToString()));
    }

    public static Version InstalledVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateCandidate?> CheckAsync(bool manual, CancellationToken token)
    {
        using var response = await httpClient.GetAsync(LatestReleaseApi, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V')
            ?? throw new InvalidDataException("La release non contiene una versione.");
        if (!Version.TryParse(tag, out var latest) || latest <= InstalledVersion)
        {
            return null;
        }
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var variantSlug = distributionVariant.ToLowerInvariant();
        var manifestAsset = FindAsset(assets, $"update-manifest-{variantSlug}.json");
        var payloadAsset = assets.FirstOrDefault(asset =>
            (asset.GetProperty("name").GetString() ?? string.Empty)
                .Equals(
                    $"WhisperLS-Update-{tag}-{distributionVariant}-x64.zip",
                    StringComparison.OrdinalIgnoreCase));
        if (manifestAsset.ValueKind == JsonValueKind.Undefined || payloadAsset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("La release non contiene gli asset necessari all'aggiornamento.");
        }
        var state = await LoadStateAsync(token);
        if (!manual && string.Equals(state.IgnoredVersion, tag, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new UpdateCandidate(
            tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            manifestAsset.GetProperty("browser_download_url").GetString()!,
            payloadAsset.GetProperty("browser_download_url").GetString()!,
            payloadAsset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            state.CancelledVersion == tag ? state.CancelCount : 0);
    }

    public async Task RegisterCancellationAsync(string version, bool ignore, CancellationToken token)
    {
        var state = await LoadStateAsync(token);
        var count = string.Equals(state.CancelledVersion, version, StringComparison.OrdinalIgnoreCase)
            ? state.CancelCount + 1
            : 1;
        await SaveStateAsync(
            state with
            {
                CancelledVersion = version,
                CancelCount = count,
                IgnoredVersion = ignore ? version : state.IgnoredVersion
            },
            token);
    }

    public async Task<PreparedUpdate> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(dataDirectory);
        var updateRoot = Path.Combine(dataDirectory, "Updates", candidate.Version);
        if (Directory.Exists(updateRoot))
        {
            Directory.Delete(updateRoot, recursive: true);
        }
        Directory.CreateDirectory(updateRoot);
        var manifestPath = Path.Combine(updateRoot, "update-manifest.json");
        var payloadPath = Path.Combine(updateRoot, "payload.zip");
        await DownloadFileAsync(candidate.ManifestUrl, manifestPath, null, token);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(
            await File.ReadAllTextAsync(manifestPath, token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Manifest aggiornamento non valido.");
        if (!string.Equals(manifest.Version, candidate.Version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Repository, Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Il manifest non appartiene alla release richiesta.");
        }
        await DownloadFileAsync(candidate.PayloadUrl, payloadPath, progress, token);
        await using var payloadStream = File.OpenRead(payloadPath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(payloadStream, token));
        if (!string.Equals(actualHash, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(payloadPath);
            throw new InvalidDataException("Verifica SHA-256 fallita: il pacchetto è stato scartato.");
        }
        var stagingDirectory = Path.Combine(updateRoot, "staging");
        Directory.CreateDirectory(stagingDirectory);
        ExtractSafely(payloadPath, stagingDirectory);
        foreach (var relativePath in manifest.Files)
        {
            if (!File.Exists(SafeCombine(stagingDirectory, relativePath)))
            {
                throw new InvalidDataException($"File dichiarato ma assente: {relativePath}");
            }
        }
        return new PreparedUpdate(candidate.Version, stagingDirectory, manifestPath);
    }

    public void ApplyAndRestart(PreparedUpdate update)
    {
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var updaterSource = Path.Combine(installDirectory, "WhisperLS.Updater.exe");
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("Componente updater non trovato.", updaterSource);
        }
        var helperCopy = Path.Combine(Path.GetTempPath(), $"WhisperLS.Updater.{Guid.NewGuid():N}.exe");
        File.Copy(updaterSource, helperCopy, overwrite: true);
        var backupDirectory = Path.Combine(installDirectory, ".whisperls-backup");
        var startInfo = new ProcessStartInfo
        {
            FileName = helperCopy,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installDirectory
        };
        AddArgument(startInfo, "--wait-pid", Environment.ProcessId.ToString());
        AddArgument(startInfo, "--install-dir", installDirectory);
        AddArgument(startInfo, "--staging-dir", update.StagingDirectory);
        AddArgument(startInfo, "--backup-dir", backupDirectory);
        AddArgument(startInfo, "--app-exe", Path.GetFileName(Environment.ProcessPath ?? "FasterWhisperGui.exe"));
        AddArgument(startInfo, "--manifest", update.ManifestPath);
        Process.Start(startInfo);
        App.MainWindow?.Close();
    }

    public static void ConfirmSuccessfulStartup()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS");
        var marker = Path.Combine(dataDirectory, "update-pending-confirmation.json");
        if (!File.Exists(marker))
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var backup = document.RootElement.GetProperty("backupDirectory").GetString();
            if (!string.IsNullOrWhiteSpace(backup) && Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            File.Delete(marker);
        }
        catch
        {
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            downloaded += read;
            progress?.Report(new UpdateDownloadProgress(downloaded, total));
        }
    }

    private async Task<UpdaterState> LoadStateAsync(CancellationToken token)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdaterState>(await File.ReadAllTextAsync(statePath, token))
                ?? new UpdaterState();
        }
        catch (IOException)
        {
            return new UpdaterState();
        }
        catch (JsonException)
        {
            return new UpdaterState();
        }
    }

    private async Task SaveStateAsync(UpdaterState state, CancellationToken token)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), token);
        File.Move(temporary, statePath, overwrite: true);
    }

    private static JsonElement FindAsset(JsonElement[] assets, string name) =>
        assets.FirstOrDefault(asset => string.Equals(
            asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));

    private static string ReadDistributionVariant()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "distribution.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var value = document.RootElement.GetProperty("variant").GetString();
            return string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Full";
        }
        catch
        {
            return "Full";
        }
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }
            var target = SafeCombine(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!result.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Il pacchetto contiene un percorso non sicuro.");
        }
        return result;
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private sealed record UpdaterState(
        string? CancelledVersion = null,
        int CancelCount = 0,
        string? IgnoredVersion = null);
}

internal sealed record UpdateCandidate(
    string Version,
    string Changelog,
    string ManifestUrl,
    string PayloadUrl,
    long PayloadSize,
    int CancelCount);

internal sealed record UpdateDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : DownloadedBytes * 100d / TotalBytes;
}

internal sealed record PreparedUpdate(string Version, string StagingDirectory, string ManifestPath);

internal sealed record UpdateManifest(
    string Version,
    string Repository,
    string PayloadSha256,
    string[] Files,
    string[]? RemoveFiles);
