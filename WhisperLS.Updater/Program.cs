using System.Diagnostics;
using System.Text.Json;

namespace WhisperLS.Updater;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var installDirectory = RequireDirectory(options, "install-dir");
            var stagingDirectory = RequireDirectory(options, "staging-dir");
            var backupDirectory = Path.GetFullPath(Require(options, "backup-dir"));
            var appExecutable = Require(options, "app-exe");
            var manifestPath = Path.GetFullPath(Require(options, "manifest"));
            var waitPid = int.Parse(Require(options, "wait-pid"));

            WaitForExit(waitPid);
            EnsureChildPath(installDirectory, backupDirectory);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidOperationException("Manifest aggiornamento non valido.");
            ApplyUpdate(installDirectory, stagingDirectory, backupDirectory, manifest);
            WriteConfirmationMarker(backupDirectory, manifest.Version);
            var restartedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(installDirectory, appExecutable),
                WorkingDirectory = installDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("WhisperLS non è stato riavviato.");
            VerifyRestartOrRollback(
                restartedProcess,
                installDirectory,
                backupDirectory,
                manifest,
                appExecutable);
            return 0;
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            return 1;
        }
    }

    private static void ApplyUpdate(
        string installDirectory,
        string stagingDirectory,
        string backupDirectory,
        UpdateManifest manifest)
    {
        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
        Directory.CreateDirectory(backupDirectory);
        var replaced = new List<string>();
        try
        {
            foreach (var relativePath in manifest.Files)
            {
                var source = SafeCombine(stagingDirectory, relativePath);
                var destination = SafeCombine(installDirectory, relativePath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("File di aggiornamento mancante.", source);
                }
                if (File.Exists(destination))
                {
                    var backup = SafeCombine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                replaced.Add(relativePath);
            }
            foreach (var relativePath in manifest.RemoveFiles ?? [])
            {
                var destination = SafeCombine(installDirectory, relativePath);
                if (File.Exists(destination))
                {
                    var backup = SafeCombine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                    File.Delete(destination);
                }
            }
        }
        catch
        {
            foreach (var relativePath in replaced)
            {
                var backup = SafeCombine(backupDirectory, relativePath);
                var destination = SafeCombine(installDirectory, relativePath);
                if (File.Exists(backup))
                {
                    File.Copy(backup, destination, overwrite: true);
                }
            }
            throw;
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(60_000);
            if (!process.HasExited)
            {
                throw new TimeoutException("WhisperLS non si è chiuso entro 60 secondi.");
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static void WriteConfirmationMarker(string backupDirectory, string version)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, "update-pending-confirmation.json"),
            JsonSerializer.Serialize(new { version, backupDirectory }, JsonOptions));
    }

    private static void VerifyRestartOrRollback(
        Process restartedProcess,
        string installDirectory,
        string backupDirectory,
        UpdateManifest manifest,
        string appExecutable)
    {
        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS",
            "update-pending-confirmation.json");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(marker))
            {
                return;
            }
            if (restartedProcess.HasExited)
            {
                break;
            }
            Thread.Sleep(500);
        }
        if (!File.Exists(marker) || !restartedProcess.HasExited)
        {
            return;
        }
        foreach (var relativePath in manifest.Files)
        {
            var backup = SafeCombine(backupDirectory, relativePath);
            var destination = SafeCombine(installDirectory, relativePath);
            if (File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backup, destination, overwrite: true);
            }
            else
            {
                File.Delete(destination);
            }
        }
        foreach (var relativePath in manifest.RemoveFiles ?? [])
        {
            var backup = SafeCombine(backupDirectory, relativePath);
            var destination = SafeCombine(installDirectory, relativePath);
            if (File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backup, destination, overwrite: true);
            }
        }
        File.Delete(marker);
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(installDirectory, appExecutable),
            WorkingDirectory = installDirectory,
            UseShellExecute = true
        });
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!result.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Il manifest contiene un percorso non sicuro.");
        }
        return result;
    }

    private static void EnsureChildPath(string installDirectory, string path)
    {
        var install = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(install, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La cartella di backup deve trovarsi nella directory di installazione.");
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            result[args[index].TrimStart('-')] = args[index + 1];
        }
        return result;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Parametro --{name} mancante.");

    private static string RequireDirectory(Dictionary<string, string> options, string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException(path);
    }

    private static void WriteFailureLog(Exception exception)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS",
            "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "updater-error.log"), exception.ToString());
    }

    private sealed record UpdateManifest(string Version, string[] Files, string[]? RemoveFiles);
}
