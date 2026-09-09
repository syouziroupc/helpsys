using System.IO;
using System.Text.Json;
using System.Windows;

namespace HelpSys.Education;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Curriculum.Validate();
        CurriculumProgression.Validate();

        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunProgressRecoverySelfTest();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void RunProgressRecoverySelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HelpSys.Education.SelfTest.{Environment.ProcessId}.{Guid.NewGuid():N}");
        try
        {
            var store = new ProgressStore(root);
            store.MarkEducationCompleted("self-test");
            store.MarkPracticeCompleted("self-test");
            store.RecordQuizResult("self-test", 100);
            // Rotate the final valid state into the backup as well, then deliberately
            // damage only the primary file to verify backup recovery.
            store.Save();

            if (!File.Exists(store.BackupPathForSelfTest))
                throw new InvalidOperationException("進捗バックアップが作成されませんでした。");

            File.WriteAllText(store.PrimaryPathForSelfTest, "{ deliberately broken json");
            var recovered = new ProgressStore(root);
            var progress = recovered.Get("self-test");
            if (!progress.EducationCompleted || !progress.PracticeCompleted || !progress.TestPassed || progress.BestQuizScore != 100)
                throw new InvalidOperationException("破損した進捗ファイルをバックアップから復旧できませんでした。");

            using var _ = JsonDocument.Parse(File.ReadAllText(recovered.PrimaryPathForSelfTest));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }
}
