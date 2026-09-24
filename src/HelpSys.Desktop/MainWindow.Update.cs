using System.Windows;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly bool UpdateLoadedHandlerRegistered = RegisterUpdateLoadedHandler();
    private readonly UpdateService _updateService = new();
    private UpdateInfo? _availableUpdate;
    private int _updateBusy;
    private int _updateInitialCheckScheduled;

    private static bool RegisterUpdateLoadedHandler()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_UpdateClassLoaded),
            true);
        return true;
    }

    private static void MainWindow_UpdateClassLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window) window.ScheduleInitialUpdateCheck();
    }

    private void ScheduleInitialUpdateCheck()
    {
        _ = UpdateLoadedHandlerRegistered;
        if (Interlocked.Exchange(ref _updateInitialCheckScheduled, 1) != 0) return;
        if (UpdateButton is null) return;

        UpdateButton.ToolTip = $"最新版を確認します（現在: {_updateService.CurrentBuildId}）";
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Task.Delay(1800);
                await CheckForUpdatesAsync(announceWhenCurrent: false);
            }
            catch { }
        }));
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRequest is not null ||
            _planning ||
            _verifyingAction ||
            _awaitingClarification ||
            _sessionCts is { IsCancellationRequested: false })
        {
            ProcessDiagnostics.Log("update_blocked_during_guidance", new
            {
                activeRequest = _activeRequest is not null,
                planning = _planning,
                verifying = _verifyingAction,
                clarifying = _awaitingClarification
            });
            SetState("案内中はHelpSysを更新できません。「消す」で案内を終了してから更新してください。", speak: false);
            return;
        }

        if (Interlocked.CompareExchange(ref _updateBusy, 0, 0) != 0)
        {
            SetState("更新情報を確認中です。確認完了後にボタン表示が切り替わります。", speak: false);
            return;
        }

        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(announceWhenCurrent: true);
            return;
        }

        var update = _availableUpdate;
        if (Interlocked.Exchange(ref _updateBusy, 1) != 0) return;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "更新中";
        SetState($"HelpSys {update.BuildId} を安全に取得して検証しています…", speak: false);

        try
        {
            var prepared = await _updateService.PrepareAsync(update);
            SetState("更新を検証しました。HelpSysを再起動して入れ替えます…", speak: false);
            UpdateService.LaunchPreparedUpdate(prepared);
            ProcessDiagnostics.MarkExitReason("update_handoff", $"build={update.BuildId}", overwrite: true);
            ProcessDiagnostics.Log("update_launcher_started", new { update.BuildId, prepared.ScriptPath });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            UpdateButton.Content = "更新確認";
            UpdateButton.IsEnabled = true;
            SetState($"更新できませんでした: {ShortUpdateError(ex.Message)}", speak: false);
            Interlocked.Exchange(ref _updateBusy, 0);
        }
    }

    private async Task CheckForUpdatesAsync(bool announceWhenCurrent)
    {
        if (Interlocked.Exchange(ref _updateBusy, 1) != 0) return;
        UpdateButton.IsEnabled = false;
        if (announceWhenCurrent)
        {
            UpdateButton.Content = "確認中";
            SetState("HelpSysの最新版を確認しています…", speak: false);
        }

        try
        {
            var update = await _updateService.CheckAsync().WaitAsync(TimeSpan.FromSeconds(15));
            _availableUpdate = update;
            if (update is null)
            {
                UpdateButton.Content = "最新版";
                UpdateButton.ToolTip = $"現在のHelpSysは最新版です（{_updateService.CurrentBuildId}）";
                if (announceWhenCurrent) SetState("HelpSysは最新版です。", speak: false);
                return;
            }

            UpdateButton.Content = "更新する";
            UpdateButton.ToolTip = $"HelpSys {update.BuildId} へ更新します";
            if (announceWhenCurrent) SetState("新しいHelpSysがあります。「更新する」を押すと自動で入れ替えて再起動します。", speak: false);
        }
        catch (TimeoutException)
        {
            _availableUpdate = null;
            UpdateButton.Content = "更新確認";
            UpdateButton.ToolTip = "最新版をもう一度確認します";
            if (announceWhenCurrent) SetState("更新確認が15秒以内に完了しませんでした。通信状態を確認してもう一度実行してください。", speak: false);
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            UpdateButton.Content = "更新確認";
            UpdateButton.ToolTip = "最新版をもう一度確認します";
            if (announceWhenCurrent)
                SetState($"更新確認に失敗しました: {ShortUpdateError(ex.Message)}", speak: false);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            Interlocked.Exchange(ref _updateBusy, 0);
        }
    }

    private static string ShortUpdateError(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "不明なエラー";
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..160];
    }
}
