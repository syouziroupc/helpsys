from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


path = Path('src/HelpSys.Desktop/MainWindow.DeepAuditGuards.cs')
text = path.read_text(encoding='utf-8')
old = '''    private async Task RecoverTypeTextFocusDeviationAsync(long generation)
    {
        try
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || !_sessionState.IsCurrent(generation)) return;
            await TryRouteRecoveryAsync("入力確定時に案内対象の入力欄からフォーカスが外れている", generation, _sessionCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try { await RecoverFromObserverFailureAsync("入力欄の再確認で現在状態を確定できない"); }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
        }
    }
'''
new = '''    private Task RecoverTypeTextFocusDeviationAsync(long generation)
    {
        try
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || !_sessionState.IsCurrent(generation))
                return Task.CompletedTask;
            HandleTechnicalPlanningUncertainty("入力確定時に案内対象の入力欄からフォーカスが外れている", generation);
        }
        catch (ObjectDisposedException) { }
        catch
        {
            try { HandleTechnicalPlanningUncertainty("入力欄の再確認で現在状態を確定できない", generation); }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _typeTextFocusRecoveryInFlight, 0);
        }
        return Task.CompletedTask;
    }
'''
text = replace_once(text, old, new, 'type_text focus deviation taxonomy')
path.write_text(text, encoding='utf-8')
