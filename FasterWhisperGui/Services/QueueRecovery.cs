namespace FasterWhisperGui.Services;

internal static class QueueRecovery
{
    public static bool CanDiscard(int exitCode, string? operation, IEnumerable<string> states)
    {
        var jobs = states.ToArray();
        return (exitCode == 0 || exitCode == 3) && operation == "batch" && jobs.Length > 0 &&
            jobs.All(state => state is "completata" or "annullata");
    }

    public static bool CheckpointMatches(string path, IEnumerable<string> sources)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var saved = json.RootElement.GetProperty("sources").EnumerateArray()
                .Select(source => Path.GetFullPath(source.GetProperty("path").GetString()!));
            return saved.SequenceEqual(sources.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
