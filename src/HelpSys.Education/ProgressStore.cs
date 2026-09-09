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
    private readonly string _path;
    private Dictionary<string, LessonProgress> _items = new(StringComparer.OrdinalIgnoreCase);

    public ProgressStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpSys.Education");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "progress.json");
        Load();
    }

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

    public void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LessonProgress>>(File.ReadAllText(_path));
            if (loaded is not null) _items = new Dictionary<string, LessonProgress>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _items = new Dictionary<string, LessonProgress>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
