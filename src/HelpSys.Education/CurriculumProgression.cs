using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HelpSys.Education;

public static class CurriculumProgression
{
    public const int MaxDifficulty = 5;

    public static int DifficultyFor(LessonDefinition lesson)
    {
        if (lesson.Id.Equals("final", StringComparison.OrdinalIgnoreCase)) return 5;
        var first = lesson.Section.TrimStart().FirstOrDefault();
        return first switch
        {
            '0' or '1' => 1,
            '2' => 2,
            '3' or '4' => 3,
            '5' or '6' or '7' => 4,
            '8' => 5,
            _ => throw new InvalidOperationException($"難易度を判定できないセクションです: {lesson.Section}")
        };
    }

    public static string DifficultyLabel(int difficulty) => difficulty switch
    {
        1 => "はじめて",
        2 => "Windows基本",
        3 => "ファイル・Web",
        4 => "実用・安全",
        5 => "総合マスター",
        _ => "未定義"
    };

    public static string DifficultyText(LessonDefinition lesson)
    {
        var difficulty = DifficultyFor(lesson);
        return $"難易度 {difficulty}/{MaxDifficulty}・{DifficultyLabel(difficulty)}";
    }

    public static bool IsUnlocked(int lessonIndex, ProgressStore progress)
    {
        if (lessonIndex < 0 || lessonIndex >= Curriculum.Lessons.Count) return false;
        if (lessonIndex == 0) return true;

        var lesson = Curriculum.Lessons[lessonIndex];
        if (progress.Get(lesson.Id).TestPassed) return true; // 合格済み単元はいつでも復習できる。
        return progress.Get(Curriculum.Lessons[lessonIndex - 1].Id).TestPassed;
    }

    public static int FirstCurrentLessonIndex(ProgressStore progress)
    {
        for (var i = 0; i < Curriculum.Lessons.Count; i++)
            if (!progress.Get(Curriculum.Lessons[i].Id).TestPassed && IsUnlocked(i, progress)) return i;
        return Math.Max(0, Curriculum.Lessons.Count - 1);
    }

    public static int HighestUnlockedDifficulty(ProgressStore progress)
    {
        var highest = 1;
        for (var i = 0; i < Curriculum.Lessons.Count; i++)
        {
            if (!IsUnlocked(i, progress)) break;
            highest = Math.Max(highest, DifficultyFor(Curriculum.Lessons[i]));
        }
        return highest;
    }

    public static bool IsCourseMastered(ProgressStore progress) =>
        Curriculum.Lessons.All(x => progress.Get(x.Id).TestPassed);

    public static void Validate()
    {
        if (Curriculum.Lessons.Count == 0) throw new InvalidOperationException("教育課程が空です。");
        if (DifficultyFor(Curriculum.Lessons[0]) != 1) throw new InvalidOperationException("教育課程は難易度1から開始する必要があります。");
        if (DifficultyFor(Curriculum.Lessons[^1]) != 5) throw new InvalidOperationException("最終単元は難易度5である必要があります。");

        var represented = new HashSet<int>();
        var previous = 1;
        foreach (var lesson in Curriculum.Lessons)
        {
            var level = DifficultyFor(lesson);
            if (level < 1 || level > MaxDifficulty) throw new InvalidOperationException($"難易度が範囲外です: {lesson.Id}");
            if (level < previous) throw new InvalidOperationException($"難易度が途中で下がっています: {lesson.Id}");
            represented.Add(level);
            previous = level;
        }

        for (var level = 1; level <= MaxDifficulty; level++)
            if (!represented.Contains(level)) throw new InvalidOperationException($"難易度{level}の単元がありません。");
    }
}

/// <summary>
/// LessonListに段階解放を付ける。既存のMainWindowロジックへ侵入せず、
/// 合格済みの復習と「次の1単元」だけを選択可能にする。
/// </summary>
public static class LessonProgressionBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(LessonProgressionBehavior), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly ConditionalWeakTable<ListBox, State> States = new();

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        if ((bool)e.NewValue)
        {
            list.Loaded += OnLoaded;
            list.SelectionChanged += OnSelectionChanged;
            list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            list.PreviewKeyDown += OnPreviewKeyDown;
            list.ItemContainerGenerator.StatusChanged += (_, _) => RefreshLocks(list);
        }
        else
        {
            list.Loaded -= OnLoaded;
            list.SelectionChanged -= OnSelectionChanged;
            list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            list.PreviewKeyDown -= OnPreviewKeyDown;
        }
    }

    private static bool IsLessonList(ListBox list) =>
        list.Items.Count > 0 && list.Items.Cast<object>().Any(x => x is LessonDefinition);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || !IsLessonList(list)) return;
        var state = States.GetOrCreateValue(list);
        var progress = new ProgressStore();
        var selected = list.SelectedIndex;
        if (selected < 0 || !CurriculumProgression.IsUnlocked(selected, progress))
        {
            state.Suppress = true;
            list.SelectedIndex = CurriculumProgression.FirstCurrentLessonIndex(progress);
            state.Suppress = false;
        }
        state.LastAllowedIndex = Math.Max(0, list.SelectedIndex);
        RefreshLocks(list);
        ScheduleDifficultyDisplay(list);
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || !IsLessonList(list)) return;
        var state = States.GetOrCreateValue(list);
        if (state.Suppress || list.SelectedItem is not LessonDefinition) return;

        var progress = new ProgressStore();
        if (!CurriculumProgression.IsUnlocked(list.SelectedIndex, progress))
        {
            var requested = list.SelectedItem as LessonDefinition;
            var fallback = state.LastAllowedIndex;
            if (fallback < 0 || fallback >= list.Items.Count || !CurriculumProgression.IsUnlocked(fallback, progress))
                fallback = CurriculumProgression.FirstCurrentLessonIndex(progress);

            state.Suppress = true;
            list.SelectedIndex = fallback;
            state.Suppress = false;
            ShowLockedMessage(list, requested);
        }
        else
        {
            state.LastAllowedIndex = list.SelectedIndex;
        }

        RefreshLocks(list);
        ScheduleDifficultyDisplay(list);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || !IsLessonList(list)) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container is null) return;
        var index = list.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return;

        var progress = new ProgressStore();
        if (CurriculumProgression.IsUnlocked(index, progress)) return;
        e.Handled = true;
        ShowLockedMessage(list, list.Items[index] as LessonDefinition);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || !IsLessonList(list)) return;
        var target = e.Key switch
        {
            Key.Down => list.SelectedIndex + 1,
            Key.PageDown or Key.End => list.Items.Count - 1,
            _ => -1
        };
        if (target < 0 || target >= list.Items.Count) return;

        var progress = new ProgressStore();
        if (CurriculumProgression.IsUnlocked(target, progress)) return;
        e.Handled = true;
        ShowLockedMessage(list, list.Items[target] as LessonDefinition);
    }

    private static void RefreshLocks(ListBox list)
    {
        if (!list.IsLoaded || !IsLessonList(list)) return;
        var progress = new ProgressStore();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            var unlocked = CurriculumProgression.IsUnlocked(i, progress);
            container.IsEnabled = unlocked;
            container.Opacity = unlocked ? 1.0 : 0.46;
            container.ToolTip = unlocked
                ? CurriculumProgression.DifficultyText((LessonDefinition)list.Items[i])
                : "前の単元のテストに合格すると解放されます。";
        }
    }

    private static void ScheduleDifficultyDisplay(ListBox list)
    {
        list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (list.SelectedItem is not LessonDefinition lesson) return;
            var window = Window.GetWindow(list);
            if (window?.FindName("LessonSection") is TextBlock section)
                section.Text = $"{lesson.Section}　{CurriculumProgression.DifficultyText(lesson)}";

            if (window?.FindName("OverallProgressText") is TextBlock overall)
            {
                var progress = new ProgressStore();
                if (CurriculumProgression.IsCourseMastered(progress))
                {
                    if (!overall.Text.Contains("基本操作マスター", StringComparison.Ordinal))
                        overall.Text += "　基本操作マスター ✓";
                }
                else
                {
                    var level = CurriculumProgression.HighestUnlockedDifficulty(progress);
                    var marker = $"現在レベル {level}/5・{CurriculumProgression.DifficultyLabel(level)}";
                    if (!overall.Text.Contains("現在レベル", StringComparison.Ordinal))
                        overall.Text += $"　{marker}";
                }
            }
        }));
    }

    private static void ShowLockedMessage(ListBox list, LessonDefinition? lesson)
    {
        var level = lesson is null ? string.Empty : $"（{CurriculumProgression.DifficultyText(lesson)}）";
        MessageBox.Show(Window.GetWindow(list),
            $"この単元はまだ解放されていません{level}。\n前の単元を『教育 → 練習 → テスト100%』まで完了すると進めます。",
            "HelpSys Education",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed class State
    {
        public int LastAllowedIndex { get; set; }
        public bool Suppress { get; set; }
    }
}
