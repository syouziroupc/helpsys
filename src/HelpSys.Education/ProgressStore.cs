using System.Text.Json;

namespace HelpSys.Education;

public sealed class LessonProgress
{
    public bool EducationCompleted { get; set; }
    public bool PracticeCompleted { get; set; }
    public bool TestPassed { get; set; }
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
