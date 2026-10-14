using System.Reflection;
using System.Text.Json;
using FasterWhisperGui.Services;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var program = Assembly.Load("WhisperLS.Updater").GetType("WhisperLS.Updater.Program")!;
var type = program.GetNestedType("UpdateManifest", BindingFlags.NonPublic)!;
var options = (JsonSerializerOptions)program.GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
var manifest = JsonSerializer.Deserialize("{\"version\":\"1.1.2\",\"files\":[\"new.bin\"],\"removeFiles\":[]}", type, options)!;
Check(type.GetProperty("Version")!.GetValue(manifest)?.ToString() == "1.1.2", "lowercase version");
Check(((string[])type.GetProperty("Files")!.GetValue(manifest)!).Length == 1, "lowercase files");
Check(!QueueRecovery.CanDiscard(1, null, ["in attesa"]), "retain failed startup");
Check(!QueueRecovery.CanDiscard(0, "batch", ["in attesa"]), "retain incomplete states");
Check(!QueueRecovery.CanDiscard(3, "batch", ["completata", "fallita"]), "retain failed job");
Check(QueueRecovery.CanDiscard(0, "batch", ["completata"]), "discard completed batch");
Check(QueueRecovery.CanDiscard(3, "batch", ["annullata"]), "discard acknowledged cancellations");
var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "integration-fixtures", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var install = Path.Combine(root, "install"); var stage = Path.Combine(root, "stage"); var backup = Path.Combine(install, ".backup");
Directory.CreateDirectory(install); Directory.CreateDirectory(stage);
File.WriteAllText(Path.Combine(install, "old.bin"), "OLD");
File.WriteAllText(Path.Combine(stage, "new.bin"), "NEW");
object Manifest(string[] files, string[] removals) => Activator.CreateInstance(type, new object[] { "1.1.2", files, removals })!;
var apply = program.GetMethod("ApplyUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
var recover = program.GetMethod("RestoreJournal", BindingFlags.Static | BindingFlags.NonPublic)!;
try { apply.Invoke(null, [install, stage, backup, Manifest(["new.bin", "missing.bin"], [])]); throw new Exception("missing file accepted"); }
catch (TargetInvocationException) { }
Check(!File.Exists(Path.Combine(install, "new.bin")), "preflight must prevent partial install");
try { apply.Invoke(null, [install, stage, backup, Manifest(["new.bin"], ["old.bin", "../outside.bin"])]); throw new Exception("unsafe path accepted"); }
catch (TargetInvocationException) { }
Check(File.ReadAllText(Path.Combine(install, "old.bin")) == "OLD", "preflight must prevent removal");
apply.Invoke(null, [install, stage, backup, Manifest(["new.bin"], ["old.bin"])]);
Check(File.Exists(Path.Combine(install, "new.bin")) && !File.Exists(Path.Combine(install, "old.bin")), "apply additions/removals");
recover.Invoke(null, [install, backup]);
Check(!File.Exists(Path.Combine(install, "new.bin")) && File.ReadAllText(Path.Combine(install, "old.bin")) == "OLD", "recover additions/removals after interruption");
recover.Invoke(null, [install, backup]);
Check(File.ReadAllText(Path.Combine(install, "old.bin")) == "OLD", "recovery idempotent");
File.WriteAllText(Path.Combine(install, "locked.bin"), "LOCKED");
using (var locked = new FileStream(Path.Combine(install, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
  try { apply.Invoke(null, [install, stage, backup, Manifest(["new.bin"], ["old.bin", "locked.bin"])]); throw new Exception("locked removal accepted"); }
  catch (TargetInvocationException) { }
}
recover.Invoke(null, [install, backup]);
Check(!File.Exists(Path.Combine(install, "new.bin")) && File.ReadAllText(Path.Combine(install, "old.bin")) == "OLD", "recover after real IO failure");
var checkpointPath = Path.Combine(root, "checkpoint.json");
File.WriteAllText(checkpointPath, JsonSerializer.Serialize(new { sources = new[] { new { path = Path.Combine(root, "a.mp3") } } }));
Check(QueueRecovery.CheckpointMatches(checkpointPath, [Path.Combine(root, "a.mp3")]), "match checkpoint sources");
Check(!QueueRecovery.CheckpointMatches(checkpointPath, [Path.Combine(root, "b.mp3")]), "reject other checkpoint sources");
Console.WriteLine($"Integration checks passed: {checks}");
