using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HelpSys.Education;

public partial class MainWindow : Window
{
    private readonly ProgressStore _progress = new();
    private readonly EducationGuideService _educationGuide = new();
    private readonly List<RadioButton> _quizRadios = [];
    private readonly HashSet<Key> _keyboardKeys = [];

    private int _practiceHintLevel = 1;
    private bool _practiceReady;
    private bool _mouseSingleDone;
    private bool _mouseDoubleDone;
    private bool _mouseScrollDone;
    private int _quizIndex;
    private int _quizCorrect;
    private bool _quizAwaitingAdvance;
    private bool _quizFinished;

    private LessonDefinition? CurrentLesson => LessonList.SelectedItem as LessonDefinition;

    public MainWindow()
    {
        Curriculum.Validate();
        InitializeComponent();
        LessonList.ItemsSource = Curriculum.Lessons;
        SelectResumeLesson();
        UpdateEducationAiStatus();
        RefreshLesson();
        ShowStage(Stage.Education);
    }

    private void SelectResumeLesson()
    {
        var selected = 0;
        for (var i = 0; i < Curriculum.Lessons.Count; i++)
        {
            if (!_progress.Get(Curriculum.Lessons[i].Id).TestPassed)
            {
                selected = i;
                break;
            }
        }
        LessonList.SelectedIndex = selected;
    }

    private void LessonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _practiceHintLevel = 1;
        PracticeHintBox.Visibility = Visibility.Collapsed;
        RefreshLesson();
        ShowStage(Stage.Education);
    }

    private void RefreshLesson()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;

        LessonSection.Text = lesson.Section;
        LessonTitle.Text = lesson.Title;
        LessonSummary.Text = lesson.Summary;
        EducationText.Text = lesson.EducationText;
        EducationObjectivesList.ItemsSource = lesson.Objectives;
        PracticeText.Text = lesson.PracticeText;
        PracticeStepsList.ItemsSource = lesson.PracticeSteps;
        TestText.Text = lesson.TestText;

        if (!string.IsNullOrWhiteSpace(lesson.SafetyNote))
        {
            SafetyNoteText.Text = lesson.SafetyNote;
            SafetyNoteBox.Visibility = Visibility.Visible;
        }
        else
        {
            SafetyNoteBox.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(lesson.PracticeUrl))
        {
            OpenPracticeButton.Visibility = Visibility.Visible;
            OpenPracticeButton.IsEnabled = true;
            OpenPracticeButton.Content = lesson.PracticeLabel ?? "外部教材を開く";
            PracticeLinkNote.Text = "外部教材は既定のWebブラウザで開きます。パスワードや認証コードをHelpSysへ入力する必要はありません。";
        }
        else
        {
            OpenPracticeButton.Visibility = Visibility.Collapsed;
            PracticeLinkNote.Text = string.Empty;
        }

        BuildPracticeLab();
        RefreshProgress();
        UpdateEducationAiStatus();
    }

    private void ShowEducation_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Education);
    private void ShowPractice_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Practice);
    private void ShowTest_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Test);

    private void ShowStage(Stage stage)
    {
        EducationPanel.Visibility = stage == Stage.Education ? Visibility.Visible : Visibility.Collapsed;
        PracticePanel.Visibility = stage == Stage.Practice ? Visibility.Visible : Visibility.Collapsed;
        TestPanel.Visibility = stage == Stage.Test ? Visibility.Visible : Visibility.Collapsed;
        StageStatusText.Text = stage switch
        {
            Stage.Education => "現在: 教育",
            Stage.Practice => "現在: 練習",
            Stage.Test => "現在: テスト",
            _ => string.Empty
        };

        if (stage == Stage.Practice) BuildPracticeLab();
        if (stage == Stage.Test) StartQuiz();
    }

    private void MarkEducationDone_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        _progress.MarkEducationCompleted(lesson.Id);
        RefreshProgress();
        ShowStage(Stage.Practice);
    }

    private void BuildPracticeLab()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;

        PracticeLabHost.Children.Clear();
        _practiceReady = false;
        _mouseSingleDone = false;
        _mouseDoubleDone = false;
        _mouseScrollDone = false;
        _keyboardKeys.Clear();

        switch (lesson.PracticeKind)
        {
            case PracticeKind.Mouse:
                BuildMouseLab();
                break;
            case PracticeKind.Keyboard:
                BuildKeyboardLab();
                break;
            case PracticeKind.Typing:
                BuildTypingLab(lesson.TypingTarget ?? "HelpSys 2026");
                break;
            case PracticeKind.External:
            case PracticeKind.Checklist:
            default:
                BuildChecklistLab(lesson.PracticeSteps);
                break;
        }

        if (_progress.Get(lesson.Id).PracticeCompleted)
        {
            _practiceReady = true;
            MarkPracticeDoneButton.IsEnabled = true;
            MarkPracticeDoneButton.Content = "練習済み → テストへ";
            PracticeLabStatusText.Text = "この単元の練習は完了済みです。必要なら何度でも復習できます。";
        }
        else
        {
            MarkPracticeDoneButton.Content = "練習完了 → テストへ";
            UpdatePracticeButtonState();
        }
    }

    private void BuildChecklistLab(string[] steps)
    {
        var checks = new List<CheckBox>();
        PracticeLabStatusText.Text = "各項目を実際に確認したらチェックしてください。全項目の確認後に練習完了へ進めます。";
        foreach (var step in steps)
        {
            var check = new CheckBox { Content = step, Margin = new Thickness(0, 4, 0, 4), FontSize = 14 };
            checks.Add(check);
            check.Checked += (_, _) =>
            {
                _practiceReady = checks.All(x => x.IsChecked == true);
                PracticeLabStatusText.Text = _practiceReady ? "全項目を確認しました。練習完了へ進めます。" : "残りの項目も確認してください。";
                UpdatePracticeButtonState();
            };
            check.Unchecked += (_, _) =>
            {
                _practiceReady = false;
                UpdatePracticeButtonState();
            };
            PracticeLabHost.Children.Add(check);
        }
    }

    private void BuildMouseLab()
    {
        PracticeLabStatusText.Text = "3種類の操作をすべて成功させてください。";

        var single = new Button { Content = "① このボタンを左で1回押す", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        single.Click += (_, _) => { _mouseSingleDone = true; single.Content = "① 1回押し ✓"; UpdateMouseLab(); };
        PracticeLabHost.Children.Add(single);

        var doubleText = new TextBlock { Text = "② この枠の中を素早く2回押す", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var doubleBox = new Border { Height = 70, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = doubleText, Margin = new Thickness(0, 2, 0, 10), Background = Brushes.White };
        doubleBox.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            _mouseDoubleDone = true;
            doubleText.Text = "② 素早く2回押し ✓";
            UpdateMouseLab();
        };
        PracticeLabHost.Children.Add(doubleBox);

        var scrollStack = new StackPanel();
        scrollStack.Children.Add(new TextBlock { Text = "③ ホイール等で下まで移動してください", Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap });
        scrollStack.Children.Add(new Border { Height = 170 });
        var bottom = new Button { Content = "下まで移動できた", Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left };
        bottom.Click += (_, _) => { _mouseScrollDone = true; bottom.Content = "③ スクロール ✓"; UpdateMouseLab(); };
        scrollStack.Children.Add(bottom);
        PracticeLabHost.Children.Add(new ScrollViewer { Height = 135, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = scrollStack, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) });
    }

    private void UpdateMouseLab()
    {
        _practiceReady = _mouseSingleDone && _mouseDoubleDone && _mouseScrollDone;
        PracticeLabStatusText.Text = $"1回押し {Mark(_mouseSingleDone)}　2回押し {Mark(_mouseDoubleDone)}　スクロール {Mark(_mouseScrollDone)}";
        UpdatePracticeButtonState();
    }

    private void BuildKeyboardLab()
    {
        PracticeLabStatusText.Text = "下の入力欄を1回押してから、Enter・Backspace・左右矢印のどちらか・Shiftを順不同で押してください。";
        var box = new TextBox { MinHeight = 42, FontSize = 16, Padding = new Thickness(8), Text = "ここを選んでキー練習" };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Back or Key.Left or Key.Right or Key.LeftShift or Key.RightShift) _keyboardKeys.Add(e.Key);
            var enter = _keyboardKeys.Contains(Key.Enter);
            var back = _keyboardKeys.Contains(Key.Back);
            var arrow = _keyboardKeys.Contains(Key.Left) || _keyboardKeys.Contains(Key.Right);
            var shift = _keyboardKeys.Contains(Key.LeftShift) || _keyboardKeys.Contains(Key.RightShift);
            _practiceReady = enter && back && arrow && shift;
            PracticeLabStatusText.Text = $"Enter {Mark(enter)}　Backspace {Mark(back)}　矢印 {Mark(arrow)}　Shift {Mark(shift)}";
            UpdatePracticeButtonState();
        };
        PracticeLabHost.Children.Add(box);
    }

    private void BuildTypingLab(string target)
    {
        PracticeLabStatusText.Text = "見本と完全に同じ文字列を入力してください。速さは採点しません。";
        PracticeLabHost.Children.Add(new TextBlock { Text = $"見本: {target}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 7) });
        var box = new TextBox { MinHeight = 42, FontSize = 17, Padding = new Thickness(8) };
        box.TextChanged += (_, _) =>
        {
            _practiceReady = string.Equals(box.Text, target, StringComparison.Ordinal);
            PracticeLabStatusText.Text = _practiceReady ? "正確に入力できました ✓" : "見本と同じになるまで、間違いを修正してください。";
            UpdatePracticeButtonState();
        };
        PracticeLabHost.Children.Add(box);
    }

    private void UpdatePracticeButtonState()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        MarkPracticeDoneButton.IsEnabled = _practiceReady && _progress.Get(lesson.Id).EducationCompleted;
    }

    private static string Mark(bool value) => value ? "✓" : "—";

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
            _progress.RecordHint(lesson.Id);
            _practiceHintLevel = Math.Clamp(response.NextHintLevel ?? (_practiceHintLevel + 1), 1, 3);
            PracticeHintButton.Content = _practiceHintLevel >= 3 ? "最も具体的なヒントを見る" : "次のヒントをもらう";
            EducationAiStatusText.Text = $"ヒント段階 {_practiceHintLevel}/3。まず自分で試し、必要なときだけ次へ進みます。";
            RefreshProgress();
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

    private void UpdateEducationAiStatus()
    {
        if (EducationAiStatusText is null || PracticeHintButton is null) return;
        EducationAiStatusText.Text = _educationGuide.IsConfigured
            ? "教育AIは接続済みです。練習時だけ、段階的なヒントを利用できます。"
            : "教育AIは未接続です。教材・安全練習・テスト・進捗はオフラインでも利用できます。";
        PracticeHintButton.IsEnabled = _educationGuide.IsConfigured;
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
        if (!_practiceReady && !progress.PracticeCompleted)
        {
            MessageBox.Show(this, "安全な練習エリアの課題をすべて完了してください。", "HelpSys Education", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _progress.MarkPracticeCompleted(lesson.Id);
        RefreshProgress();
        ShowStage(Stage.Test);
    }

    private void StartQuiz()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var progress = _progress.Get(lesson.Id);
        QuizFeedbackBox.Visibility = Visibility.Collapsed;

        if (!progress.PracticeCompleted)
        {
            QuizProgressText.Text = "テストはまだ開始できません。";
            QuizQuestionText.Text = "先に教育と練習を完了してください。";
            QuizOptionsPanel.Children.Clear();
            CheckQuizButton.IsEnabled = false;
            return;
        }

        _quizIndex = 0;
        _quizCorrect = 0;
        _quizAwaitingAdvance = false;
        _quizFinished = false;
        CheckQuizButton.IsEnabled = true;
        LoadQuizQuestion();
    }

    private void LoadQuizQuestion()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var question = lesson.Quiz[_quizIndex];

        QuizProgressText.Text = $"問題 {_quizIndex + 1} / {lesson.Quiz.Length}　合格条件: 全問正解";
        QuizQuestionText.Text = question.Prompt;
        QuizFeedbackBox.Visibility = Visibility.Collapsed;
        QuizOptionsPanel.Children.Clear();
        _quizRadios.Clear();
        _quizAwaitingAdvance = false;
        CheckQuizButton.Content = "回答を確認";

        for (var i = 0; i < question.Choices.Length; i++)
        {
            var radio = new RadioButton
            {
                Content = question.Choices[i],
                Tag = i,
                GroupName = "KnowledgeCheck",
                FontSize = 15,
                Margin = new Thickness(0, 5, 0, 5)
            };
            _quizRadios.Add(radio);
            QuizOptionsPanel.Children.Add(radio);
        }
    }

    private void CheckQuiz_Click(object sender, RoutedEventArgs e)
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;

        if (_quizFinished)
        {
            if (_progress.Get(lesson.Id).TestPassed) MoveToNextLesson();
            else StartQuiz();
            return;
        }

        if (_quizAwaitingAdvance)
        {
            if (_quizIndex + 1 < lesson.Quiz.Length)
            {
                _quizIndex++;
                LoadQuizQuestion();
            }
            else
            {
                FinishQuiz();
            }
            return;
        }

        var selected = _quizRadios.FirstOrDefault(x => x.IsChecked == true);
        if (selected is null)
        {
            QuizFeedbackText.Text = "回答を1つ選んでください。";
            QuizFeedbackBox.Visibility = Visibility.Visible;
            return;
        }

        var question = lesson.Quiz[_quizIndex];
        var selectedIndex = (int)selected.Tag;
        var correct = selectedIndex == question.CorrectIndex;
        if (correct) _quizCorrect++;

        foreach (var radio in _quizRadios) radio.IsEnabled = false;
        QuizFeedbackText.Text = (correct ? "正解です。 " : $"不正解です。正解は「{question.Choices[question.CorrectIndex]}」です。 ") + question.Explanation;
        QuizFeedbackBox.Visibility = Visibility.Visible;
        _quizAwaitingAdvance = true;
        CheckQuizButton.Content = _quizIndex + 1 < lesson.Quiz.Length ? "次の問題" : "結果を見る";
    }

    private void FinishQuiz()
    {
        var lesson = CurrentLesson;
        if (lesson is null) return;
        var score = (int)Math.Round(_quizCorrect * 100d / lesson.Quiz.Length);
        _progress.RecordQuizResult(lesson.Id, score);
        _quizFinished = true;
        _quizAwaitingAdvance = false;
        QuizOptionsPanel.Children.Clear();
        QuizProgressText.Text = $"今回の得点: {score}%";

        if (score >= 100)
        {
            QuizQuestionText.Text = "合格です。基礎知識を全問確認できました。";
            QuizFeedbackText.Text = "次の単元へ進めます。必要なら後から何度でも復習できます。";
            CheckQuizButton.Content = "次の単元へ";
        }
        else
        {
            QuizQuestionText.Text = "まだ合格ではありません。100%になるまで復習します。";
            QuizFeedbackText.Text = "間違えた内容を教育画面で確認してから、もう一度テストできます。";
            CheckQuizButton.Content = "もう一度テストする";
        }
        QuizFeedbackBox.Visibility = Visibility.Visible;
        RefreshProgress();
    }

    private void MoveToNextLesson()
    {
        var index = LessonList.SelectedIndex;
        if (index >= 0 && index + 1 < LessonList.Items.Count)
        {
            LessonList.SelectedIndex = index + 1;
            return;
        }

        MessageBox.Show(this, "基礎課程の最後まで到達しました。全体進捗で未合格単元がないか確認してください。", "HelpSys Education", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshProgress()
    {
        var lesson = CurrentLesson;
        if (lesson is not null)
        {
            var p = _progress.Get(lesson.Id);
            ProgressText.Text = $"教育 {Mark(p.EducationCompleted)}   練習 {Mark(p.PracticeCompleted)}   テスト {Mark(p.TestPassed)}   最高 {p.BestQuizScore}%   受験 {p.QuizAttempts}回";
        }

        var completed = _progress.CompletedLessons(Curriculum.Lessons);
        var stages = _progress.CompletedStages(Curriculum.Lessons);
        var totalStages = Curriculum.Lessons.Count * 3;
        var percent = totalStages == 0 ? 0 : stages * 100d / totalStages;
        OverallProgressBar.Value = percent;
        OverallProgressText.Text = $"合格 {completed}/{Curriculum.Lessons.Count}単元　全工程 {percent:0}%";
    }

    private void ShowProgress_Click(object sender, RoutedEventArgs e)
    {
        var completed = _progress.CompletedLessons(Curriculum.Lessons);
        var text = new StringBuilder();
        text.AppendLine($"HelpSys Education 基礎課程　合格 {completed}/{Curriculum.Lessons.Count}単元");
        text.AppendLine();
        string? section = null;
        foreach (var lesson in Curriculum.Lessons)
        {
            if (!string.Equals(section, lesson.Section, StringComparison.Ordinal))
            {
                section = lesson.Section;
                text.AppendLine($"【{section}】");
            }
            var p = _progress.Get(lesson.Id);
            text.AppendLine($"{(p.TestPassed ? "✓" : "—")} {lesson.Title}　教育{Mark(p.EducationCompleted)} 練習{Mark(p.PracticeCompleted)} テスト{Mark(p.TestPassed)} 最高{p.BestQuizScore}%");
        }

        ShowTextWindow("全体進捗", text.ToString());
    }

    private void ShowGlossary_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "HelpSys Education 用語集",
            Width = 720,
            Height = 620,
            MinWidth = 520,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var search = new TextBox { FontSize = 16, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10), ToolTip = "用語を検索" };
        var list = new ListBox { FontSize = 14 };
        Grid.SetRow(list, 1);
        grid.Children.Add(search);
        grid.Children.Add(list);

        void RefreshGlossary()
        {
            var q = search.Text.Trim();
            list.ItemsSource = Curriculum.Glossary
                .Where(x => q.Length == 0 || x.Term.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Meaning.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Term}\n  {x.Meaning}")
                .ToArray();
        }

        search.TextChanged += (_, _) => RefreshGlossary();
        RefreshGlossary();
        window.Content = grid;
        window.ShowDialog();
    }

    private void ShowTextWindow(string title, string content)
    {
        var box = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            Padding = new Thickness(14)
        };
        new Window
        {
            Title = title,
            Width = 760,
            Height = 650,
            MinWidth = 520,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box
        }.ShowDialog();
    }

    private enum Stage { Education, Practice, Test }
}
