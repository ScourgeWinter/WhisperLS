using System.Diagnostics;
using System.Text.Json;

namespace WhisperLS.Updater;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static int Main(string[] args)
    {
        string? installDirectory = null;
        string? appExecutable = null;
        string? backupDirectory = null;
        bool appExited = false;
        try
        {
            var options = ParseArguments(args);
            installDirectory = RequireDirectory(options, "install-dir");
            var stagingDirectory = RequireDirectory(options, "staging-dir");
            backupDirectory = Path.GetFullPath(Require(options, "backup-dir"));
            appExecutable = Require(options, "app-exe");
            var manifestPath = Path.GetFullPath(Require(options, "manifest"));
            var waitPid = int.Parse(Require(options, "wait-pid"));

            WaitForExit(waitPid);
            appExited = true;
            EnsureChildPath(installDirectory, backupDirectory);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), JsonOptions)
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
            if (appExited && installDirectory is not null && appExecutable is not null)
            {
                try
                {
                    if (backupDirectory is not null)
                    {
                        EnsureChildPath(installDirectory, backupDirectory);
                        RestoreJournal(installDirectory, backupDirectory);
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SafeCombine(installDirectory, appExecutable),
                        WorkingDirectory = installDirectory,
                        UseShellExecute = true
                    });
                }
                catch (Exception recoveryException)
                {
                    WriteFailureLog(new AggregateException(exception, recoveryException));
                }
            }
            return 1;
        }
    }

    private static void ApplyUpdate(
        string installDirectory,
        string stagingDirectory,
        string backupDirectory,
        UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null || manifest.Files.Length == 0)
        {
            throw new InvalidDataException("Versione o lista dei file assente nel manifest.");
        }
        EnsureChildPath(installDirectory, backupDirectory);
        var operations = manifest.Files.Concat(manifest.RemoveFiles ?? []).ToArray();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in operations)
        {
            var destination = SafeCombine(installDirectory, path);
            if (!destinations.Add(destination) || Directory.Exists(destination) ||
                destination.Equals(Path.GetFullPath(backupDirectory), StringComparison.OrdinalIgnoreCase) || destination.StartsWith(
                Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Percorso duplicato o riservato nel manifest.");
            }
        }
        foreach (var path in manifest.Files)
        {
            if (!File.Exists(SafeCombine(stagingDirectory, path)))
                throw new FileNotFoundException("File di aggiornamento mancante.", path);
        }
        RestoreJournal(installDirectory, backupDirectory);
        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
        Directory.CreateDirectory(backupDirectory);
        var entries = operations.Select(path => new JournalEntry(path, File.Exists(SafeCombine(installDirectory, path)))).ToArray();
        foreach (var entry in entries.Where(entry => entry.Existed))
        {
            var backup = SafeCombine(backupDirectory, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(SafeCombine(installDirectory, entry.Path), backup, overwrite: true);
        }
        var journal = new UpdateJournal(entries, []);
        SaveJournal(backupDirectory, journal);
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
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                journal = journal with { Touched = [.. journal.Touched, relativePath] };
                SaveJournal(backupDirectory, journal);
                File.Copy(source, destination, overwrite: true);
            }
            foreach (var relativePath in manifest.RemoveFiles ?? [])
            {
                var destination = SafeCombine(installDirectory, relativePath);
                if (File.Exists(destination))
                {
                    journal = journal with { Touched = [.. journal.Touched, relativePath] };
                    SaveJournal(backupDirectory, journal);
                    File.Delete(destination);
                }
            }
        }
        catch
        {
            RestoreJournal(installDirectory, backupDirectory);
            throw;
        }
    }

    private static void SaveJournal(string backupDirectory, UpdateJournal journal)
    {
        var path = Path.Combine(backupDirectory, ".update-journal.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void RestoreJournal(string installDirectory, string backupDirectory)
    {
        var path = Path.Combine(backupDirectory, ".update-journal.json");
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Journal di recupero non valido.");
        foreach (var touched in journal.Touched.Reverse())
        {
            var entry = journal.Entries.Single(entry => entry.Path == touched);
            var destination = SafeCombine(installDirectory, touched);
            if (entry.Existed)
            {
                var backup = SafeCombine(backupDirectory, touched);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backup, destination, overwrite: true);
            }
            else File.Delete(destination);
        }
        SaveJournal(backupDirectory, journal with { Touched = [] });
    }

    private sealed record JournalEntry(string Path, bool Existed);
    private sealed record UpdateJournal(JournalEntry[] Entries, string[] Touched);

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
        RestoreJournal(installDirectory, backupDirectory);
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
        try
        {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperLS",
            "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "updater-error.log"), exception.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record UpdateManifest(string Version, string[] Files, string[]? RemoveFiles);
}
