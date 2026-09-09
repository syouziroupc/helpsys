using System.IO;
using System.Text.Json;

namespace HelpSys.Education;

public sealed class LessonProgress
{
    public bool EducationCompleted { get; set; }
    public bool PracticeCompleted { get; set; }
    public bool TestPassed { get; set; }
    public int QuizAttempts { get; set; }
    public int BestQuizScore { get; set; }
    public int HintRequests { get; set; }
    public DateTime? EducationCompletedAtUtc { get; set; }
    public DateTime? PracticeCompletedAtUtc { get; set; }
    public DateTime? TestPassedAtUtc { get; set; }
}

public sealed class ProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _backupPath;
    private Dictionary<string, LessonProgress> _items = new(StringComparer.OrdinalIgnoreCase);

    public ProgressStore() : this(null) { }

    internal ProgressStore(string? rootOverride)
    {
        var root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpSys.Education");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "progress.json");
        _backupPath = Path.Combine(root, "progress.backup.json");
        Load();
    }

    internal string PrimaryPathForSelfTest => _path;
    internal string BackupPathForSelfTest => _backupPath;

    public LessonProgress Get(string lessonId)
    {
        if (!_items.TryGetValue(lessonId, out var progress))
        {
            progress = new LessonProgress();
            _items[lessonId] = progress;
        }
        return progress;
    }

    public int CompletedLessons(IReadOnlyList<LessonDefinition> lessons)
        => lessons.Count(x => Get(x.Id).TestPassed);

    public int CompletedStages(IReadOnlyList<LessonDefinition> lessons)
        => lessons.Sum(x =>
        {
            var p = Get(x.Id);
            return (p.EducationCompleted ? 1 : 0) + (p.PracticeCompleted ? 1 : 0) + (p.TestPassed ? 1 : 0);
        });

    public void MarkEducationCompleted(string lessonId)
    {
        var p = Get(lessonId);
        p.EducationCompleted = true;
        p.EducationCompletedAtUtc ??= DateTime.UtcNow;
        Save();
    }

    public void MarkPracticeCompleted(string lessonId)
    {
        var p = Get(lessonId);
        p.PracticeCompleted = true;
        p.PracticeCompletedAtUtc ??= DateTime.UtcNow;
        Save();
    }

    public void RecordHint(string lessonId)
    {
        Get(lessonId).HintRequests++;
        Save();
    }

    public void RecordQuizResult(string lessonId, int score)
    {
        var p = Get(lessonId);
        p.QuizAttempts++;
        p.BestQuizScore = Math.Max(p.BestQuizScore, score);
        if (score >= 100)
        {
            p.TestPassed = true;
            p.TestPassedAtUtc ??= DateTime.UtcNow;
        }
        Save();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("進捗保存先を確認できません。");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"progress.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(_items, JsonOptions);

        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(tempPath, _path, _backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(_path, _backupPath, overwrite: true);
                    File.Move(tempPath, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, _path);
                File.Copy(_path, _backupPath, overwrite: true);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private void Load()
    {
        if (TryLoad(_path, out var primary))
        {
            _items = primary;
            return;
        }

        if (TryLoad(_backupPath, out var backup))
        {
            _items = backup;
            // Do not call Save() here: File.Replace would rotate the damaged primary into
            // the backup slot and destroy the known-good recovery copy. Restore primary
            // independently while leaving the valid backup untouched.
            try { RestorePrimaryFromBackup(); }
            catch { }
            return;
        }

        _items = new Dictionary<string, LessonProgress>(StringComparer.OrdinalIgnoreCase);
    }

    private void RestorePrimaryFromBackup()
    {
        if (!File.Exists(_backupPath)) return;
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("進捗保存先を確認できません。");
        var tempPath = Path.Combine(directory, $"progress.recover.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(_backupPath, tempPath, overwrite: true);
            // Temp and destination are on the same volume. The rename/replace cannot expose
            // a partially written JSON file to the next process.
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool TryLoad(string path, out Dictionary<string, LessonProgress> loaded)
    {
        loaded = new Dictionary<string, LessonProgress>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, LessonProgress>>(File.ReadAllText(path));
            if (parsed is null) return false;
            loaded = new Dictionary<string, LessonProgress>(parsed, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
