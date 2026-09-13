from pathlib import Path
import subprocess


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

violations = []
for cs_path in Path('src/HelpSys.Desktop').glob('*.cs'):
    for line_no, line in enumerate(cs_path.read_text(encoding='utf-8').splitlines(), 1):
        if 'RecoverFromObserverFailureAsync' in line:
            violations.append(f'{cs_path.name}:{line_no}: obsolete observer recovery wrapper')
        if 'TryRouteRecoveryAsync(' not in line:
            continue
        if cs_path.name == 'MainWindow.RouteRecovery.cs' and 'private async Task<bool> TryRouteRecoveryAsync(' in line:
            continue
        if cs_path.name == 'MainWindow.ReliabilityV3.cs' and 'await TryRouteRecoveryAsync(routeRecoveryIssue' in line:
            continue
        violations.append(f'{cs_path.name}:{line_no}: {line.strip()}')

if violations:
    raise SystemExit('unexpected route-recovery call sites:\n' + '\n'.join(violations))

# The workflow's final commit runs only after focused contracts, all existing regressions,
# and the Windows build succeed. Pre-stage files that its historical fixed git-add list
# does not know about; no commit or push occurs here.
subprocess.run([
    'git', 'add',
    'src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs',
    'src/HelpSys.Desktop/MainWindow.RouteRecovery.cs'
], check=True)
