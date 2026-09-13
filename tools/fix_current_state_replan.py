from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


# Add one bounded lightweight current-state replan before the heavy route-recovery planner.
helper_path = Path('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs')
if not helper_path.exists():
    helper_path.write_text(r'''namespace HelpSys;

public partial class MainWindow
{
    private const int MaximumAutomaticCurrentStateReplans = 1;
    private int _automaticCurrentStateReplans;

    private bool TryQueueCurrentStateReplan(string reason, long generation)
    {
        if (_activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested ||
            !_sessionState.IsCurrent(generation))
            return false;

        if (_automaticCurrentStateReplans >= MaximumAutomaticCurrentStateReplans)
            return false;

        _automaticCurrentStateReplans++;
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "current_state_replan",
            "現在の画面",
            $"一時的な画面変化を検出したため、復帰経路ではなく現在状態を再取得する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = true;
        SetState("画面が変わったため、今の画面を確認し直しています…", speak: false);
        Dispatcher.BeginInvoke(new Action(() => _ = RunQueuedCurrentStateReplanAsync()));
        return true;
    }

    private async Task RunQueuedCurrentStateReplanAsync()
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;
        try
        {
            await Task.Delay(280, _sessionCts.Token);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void ResetCurrentStateReplanBudget() => _automaticCurrentStateReplans = 0;
}
''', encoding='utf-8')


quality_path = Path('src/HelpSys.Desktop/MainWindow.QualityFirst.cs')
quality = quality_path.read_text(encoding='utf-8')

quality = replace_once(quality,
'''            if (!HasUsableForeground(systemContext))
            {
                await TryRouteRecoveryAsync("前面ウィンドウを一時的に特定できない", generation, cancellationToken);
                return;
            }
''',
'''            if (!HasUsableForeground(systemContext))
            {
                if (TryQueueCurrentStateReplan("前面ウィンドウを一時的に特定できない", generation)) return;
                await TryRouteRecoveryAsync("再確認しても前面ウィンドウを特定できない", generation, cancellationToken);
                return;
            }
''', 'foreground replan')

quality = replace_once(quality,
'''            if (!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext))
            {
                await TryRouteRecoveryAsync("確認中に画面が切り替わった", generation, cancellationToken);
                return;
            }
''',
'''            if (!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext))
            {
                if (TryQueueCurrentStateReplan("確認中に画面が切り替わった", generation)) return;
                await TryRouteRecoveryAsync("再確認後も確認中の画面切替が続いている", generation, cancellationToken);
                return;
            }
''', 'post-capture replan')

quality = replace_once(quality,
'''            catch (GuideServiceException error)
            {
                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                if (_sessionState.IsCurrent(generation))
                    await TryRouteRecoveryAsync($"通常計画を継続できない: {error.Kind}", generation, cancellationToken);
                return;
            }
''',
'''            catch (GuideServiceException error)
            {
                if (error.Kind == GuideFailureKind.ContextChanged &&
                    TryQueueCurrentStateReplan("通常計画中に画面状態が変化した", generation)) return;
                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                if (_sessionState.IsCurrent(generation))
                    await TryRouteRecoveryAsync($"通常計画を継続できない: {error.Kind}", generation, cancellationToken);
                return;
            }
''', 'context-changed service replan')

quality = replace_once(quality,
'''            if (!HasSameCaptureIdentity(systemContext, postPlanContext) || HasSystemTransitionV3(systemContext, postPlanContext))
            {
                await TryRouteRecoveryAsync("判断中に画面が変化した", generation, cancellationToken);
                return;
            }
''',
'''            if (!HasSameCaptureIdentity(systemContext, postPlanContext) || HasSystemTransitionV3(systemContext, postPlanContext))
            {
                if (TryQueueCurrentStateReplan("判断中に画面が変化した", generation)) return;
                await TryRouteRecoveryAsync("再確認後も判断中の画面変化が続いている", generation, cancellationToken);
                return;
            }
''', 'post-plan replan')

quality = replace_once(quality,
'''            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("選ばれた対象が現在は操作できない", generation, cancellationToken);
                return;
            }
''',
'''            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                if (TryQueueCurrentStateReplan("選ばれた対象が現在は操作できない", generation)) return;
                await TryRouteRecoveryAsync("再確認しても選ばれた対象を操作できない", generation, cancellationToken);
                return;
            }
''', 'invalid target replan')

quality = replace_once(quality,
'''            if (!HasSameCaptureIdentity(systemContext, prePresentContext) || HasSystemTransitionV3(systemContext, prePresentContext))
            {
                await TryRouteRecoveryAsync("案内表示の直前に画面が変わった", generation, cancellationToken);
                return;
            }
''',
'''            if (!HasSameCaptureIdentity(systemContext, prePresentContext) || HasSystemTransitionV3(systemContext, prePresentContext))
            {
                if (TryQueueCurrentStateReplan("案内表示の直前に画面が変わった", generation)) return;
                await TryRouteRecoveryAsync("再確認後も案内表示直前の画面変化が続いている", generation, cancellationToken);
                return;
            }
''', 'pre-present replan')

quality = replace_once(quality,
'''            if (freshTarget is null)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                await TryRouteRecoveryAsync("案内対象が表示直前に消えた", generation, cancellationToken);
                return;
            }
''',
'''            if (freshTarget is null)
            {
                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;
                if (TryQueueCurrentStateReplan("案内対象が表示直前に消えた", generation)) return;
                await TryRouteRecoveryAsync("再確認しても案内対象を確定できない", generation, cancellationToken);
                return;
            }
''', 'fresh target replan')

quality = replace_once(quality,
'''        if (!HasSameCaptureIdentity(systemContext, currentContext) || HasSystemTransitionV3(systemContext, currentContext))
        {
            await TryRouteRecoveryAsync("画像上の候補を確認中に画面が変わった", generation, cancellationToken);
            return;
        }
''',
'''        if (!HasSameCaptureIdentity(systemContext, currentContext) || HasSystemTransitionV3(systemContext, currentContext))
        {
            if (TryQueueCurrentStateReplan("画像上の候補を確認中に画面が変わった", generation)) return;
            await TryRouteRecoveryAsync("再確認後も画像候補確認中の画面変化が続いている", generation, cancellationToken);
            return;
        }
''', 'visual target context replan')

quality = replace_once(quality,
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _currentDecision = new GuideDecision("target", "vision-target", visualAction, instruction, null, null, quality.Confidence);
''',
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        ResetCurrentStateReplanBudget();
        _currentDecision = new GuideDecision("target", "vision-target", visualAction, instruction, null, null, quality.Confidence);
''', 'visual success budget reset')

quality_path.write_text(quality, encoding='utf-8')


main_path = Path('src/HelpSys.Desktop/MainWindow.xaml.cs')
main = main_path.read_text(encoding='utf-8')
main = replace_once(main,
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        _currentDecision = decision;
''',
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();
        _currentDecision = decision;
''', 'structured success budget reset')

# The second identical presenting block is ShowKeyboardGuide; replace the remaining one.
main = replace_once(main,
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        _currentDecision = decision;
''',
'''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();
        _currentDecision = decision;
''', 'keyboard success budget reset')

main = replace_once(main,
'''        _technicalClarificationRetries = 0;
        if (generation.HasValue)
''',
'''        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();
        if (generation.HasValue)
''', 'clarification budget reset')

main = replace_once(main,
'''        _technicalClarificationRetries = 0;
        _forceVisionNext = false;
''',
'''        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();
        _forceVisionNext = false;
''', 'session budget reset')
main_path.write_text(main, encoding='utf-8')


contract_path = Path('tests/current-state-replan-contract.mjs')
contract_path.write_text(r'''import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');

if (!helper.includes('MaximumAutomaticCurrentStateReplans = 1'))
  throw new Error('automatic current-state replan must be strictly bounded to one attempt');
if (!helper.includes('_liveReplanPending = true'))
  throw new Error('current-state replan must use the established live replan pipeline');
if (!helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('current-state replan must delegate execution to the live replan owner');
if (helper.includes('await AdvanceGuideAsync()'))
  throw new Error('current-state replan helper must not recursively start the planner directly');

for (const reason of [
  '前面ウィンドウを一時的に特定できない',
  '確認中に画面が切り替わった',
  '判断中に画面が変化した',
  '選ばれた対象が現在は操作できない',
  '案内表示の直前に画面が変わった',
  '案内対象が表示直前に消えた',
  '画像上の候補を確認中に画面が変わった'
]) {
  if (!quality.includes(`TryQueueCurrentStateReplan("${reason}"`))
    throw new Error(`transient UI drift must replan before recovery: ${reason}`);
}

if (!quality.includes('error.Kind == GuideFailureKind.ContextChanged'))
  throw new Error('only context-change service failures should enter the lightweight replan path');
if (!quality.includes('再確認しても前面ウィンドウを特定できない') ||
    !quality.includes('再確認しても案内対象を確定できない'))
  throw new Error('heavy route recovery must remain as a bounded fallback after replan failure');
if ((main.match(/ResetCurrentStateReplanBudget\(\);/g) || []).length < 3)
  throw new Error('successful target/keyboard/clarification states must reset the replan budget');
if (!quality.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful visual target guidance must reset the replan budget');

console.log('HelpSys bounded current-state replan contract passed.');
''', encoding='utf-8')
