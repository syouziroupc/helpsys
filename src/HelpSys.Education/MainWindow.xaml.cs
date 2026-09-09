using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace HelpSys.Education;

public partial class MainWindow : Window
{
    private readonly ProgressStore _progress = new();
    private readonly EducationGuideService _educationGuide = new();
    private int _practiceHintLevel = 1;
    private LessonDefinition? CurrentLesson => LessonList.SelectedItem as LessonDefinition;

    public MainWindow()
    {
        InitializeComponent();
        LessonList.ItemsSource = Curriculum.Lessons;
        LessonList.SelectedIndex = 0;
        EducationAiStatusText.Text = _educationGuide.IsConfigured
            ? "教育AIは接続されています。最初は答えではなく軽いヒントから出します。"
            : "教育AIは未接続です。外部教材と通常の練習はそのまま利用できます。";
        PracticeHintButton.IsEnabled = _educationGuide.IsConfigured;
        ShowStage(Stage.Education);
    }

    private void LessonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _practiceHintLevel = 1;
        PracticeHintBox.Visibility = Visibility.Collapsed;
        RefreshLesson();
        ShowStage(Stage.Education);
    }

    private void RefreshLesson()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        LessonTitle.Text = lesson.Title;
        LessonSummary.Text = lesson.Summary;
        EducationText.Text = lesson.EducationText;
        PracticeText.Text = lesson.PracticeText;
        TestText.Text = lesson.TestText;

        if (!string.IsNullOrWhiteSpace(lesson.PracticeUrl))
        {
            OpenPracticeButton.IsEnabled = true;
            OpenPracticeButton.Content = lesson.PracticeLabel ?? "練習サイトを開く";
            PracticeLinkNote.Text = "外部教材を既定のWebブラウザで開きます。HelpSys Educationは外部サイトのパスワード等を要求しません。";
        }
        else
        {
            OpenPracticeButton.IsEnabled = false;
            OpenPracticeButton.Content = "専用練習画面は準備中";
            PracticeLinkNote.Text = "この単元は今後、HelpSys Education内の安全な専用練習画面を追加します。";
        }
        RefreshProgress();
    }

    private void ShowEducation_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Education);
    private void ShowPractice_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Practice);
    private void ShowTest_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Test);

    private void ShowStage(Stage stage)
    {
        EducationPanel.Visibility = stage == Stage.Education ? Visibility.Visible : Visibility.Collapsed;
        PracticePanel.Visibility = stage == Stage.Practice ? Visibility.Visible : Visibility.Collapsed;
        TestPanel.Visibility = stage == Stage.Test ? Visibility.Visible : Visibility.Collapsed;
        StageStatusText.Text = stage switch { Stage.Education => "現在: 教育", Stage.Practice => "現在: 練習", Stage.Test => "現在: テスト", _ => string.Empty };
    }

    private void MarkEducationDone_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        _progress.Get(lesson.Id).EducationCompleted = true;
        _progress.Save();
        RefreshProgress();
        ShowStage(Stage.Practice);
    }

    private void OpenPractice_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson?.PracticeUrl is null) return;
        try
        {
            var uri = new Uri(lesson.PracticeUrl, UriKind.Absolute);
            if (uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("対応していないURLです。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"練習サイトを開けませんでした。\n{ex.Message}", "HelpSys Education", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PracticeHint_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null || !_educationGuide.IsConfigured) return;
        PracticeHintButton.IsEnabled = false;
        EducationAiStatusText.Text = "ヒントを考えています…";
        try
        {
            var response = await _educationGuide.GetPracticeHintAsync(lesson, _practiceHintLevel);
            PracticeHintText.Text = response.Message;
            PracticeHintBox.Visibility = Visibility.Visible;
            _practiceHintLevel = Math.Clamp(response.NextHintLevel ?? (_practiceHintLevel + 1), 1, 3);
            PracticeHintButton.Content = _practiceHintLevel >= 3 ? "もう少し具体的なヒント" : "次のヒントをもらう";
            EducationAiStatusText.Text = $"ヒント段階 {_practiceHintLevel}/3。必要なときだけ次のヒントを使ってください。";
        }
        catch (Exception ex)
        {
            EducationAiStatusText.Text = $"教育AIを利用できません: {ex.Message}";
        }
        finally
        {
            PracticeHintButton.IsEnabled = _educationGuide.IsConfigured;
        }
    }

    private void MarkPracticeDone_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var progress = _progress.Get(lesson.Id);
        if (!progress.EducationCompleted)
        {
            MessageBox.Show(this, "先に教育画面の説明を完了してください。", "HelpSys Education", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowStage(Stage.Education);
            return;
        }
        progress.PracticeCompleted = true;
        _progress.Save();
        RefreshProgress();
        ShowStage(Stage.Test);
    }

    private void MarkTestPassed_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var progress = _progress.Get(lesson.Id);
        if (!progress.PracticeCompleted)
        {
            MessageBox.Show(this, "先に練習を完了してください。", "HelpSys Education", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowStage(Stage.Practice);
            return;
        }
        progress.TestPassed = true;
        _progress.Save();
        RefreshProgress();
        var index = LessonList.SelectedIndex;
        if (index >= 0 && index + 1 < LessonList.Items.Count) LessonList.SelectedIndex = index + 1;
    }

    private void RefreshProgress()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var p = _progress.Get(lesson.Id);
        ProgressText.Text = $"教育 {(p.EducationCompleted ? "✓" : "—")}   練習 {(p.PracticeCompleted ? "✓" : "—")}   テスト {(p.TestPassed ? "✓" : "—")}";
    }

    private enum Stage { Education, Practice, Test }
}
