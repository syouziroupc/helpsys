from pathlib import Path


def replace_once(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        # The final taxonomy replaces the intermediate replan-then-recovery form with
        # HandleTechnicalPlanningUncertainty. That is already a completed migration.
        if 'HandleTechnicalPlanningUncertainty(' in text:
            return text
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


path = Path('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs')
text = path.read_text(encoding='utf-8')

old_mouse = '''                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    await RecoverFromObserverFailureAsync("マウス操作の結果監視で現在状態を確定できない");
'''
new_mouse = '''                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    var generation = _sessionState.Generation;
                    if (!TryQueueCurrentStateReplan("マウス操作の結果監視で現在状態を確定できない", generation))
                        await RecoverFromObserverFailureAsync("マウス操作の結果監視で現在状態を確定できない");
                }
'''
text = replace_once(
    text, old_mouse, new_mouse,
    'TryQueueCurrentStateReplan("マウス操作の結果監視で現在状態を確定できない"',
    'mouse observer replan')

old_key = '''                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    await RecoverFromObserverFailureAsync("キー操作の結果監視で現在状態を確定できない");
'''
new_key = '''                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    var generation = _sessionState.Generation;
                    if (!TryQueueCurrentStateReplan("キー操作の結果監視で現在状態を確定できない", generation))
                        await RecoverFromObserverFailureAsync("キー操作の結果監視で現在状態を確定できない");
                }
'''
text = replace_once(
    text, old_key, new_key,
    'TryQueueCurrentStateReplan("キー操作の結果監視で現在状態を確定できない"',
    'key observer replan')

old_foreground = '''                            _history.Add(new GuideHistoryItem(_stepNumber, "foreground_lost", targetName, "操作後の前面アプリを一時的に特定できないため、停止せず現在位置を再取得して復帰経路を探す。"));
                            if (_history.Count > 12) _history.RemoveAt(0);
                            ClearCurrentGuidanceV3();
                            routeRecoveryIssue = "操作後の前面アプリを一時的に特定できない";
'''
new_foreground = '''                            _history.Add(new GuideHistoryItem(_stepNumber, "foreground_lost", targetName, "操作後の前面アプリを一時的に特定できないため、現在画面を取り直して通常案内を再計画する。"));
                            if (_history.Count > 12) _history.RemoveAt(0);
                            ClearCurrentGuidanceV3();
                            replan = true;
'''
text = replace_once(
    text, old_foreground, new_foreground,
    '"foreground_lost", targetName, "操作後の前面アプリを一時的に特定できないため、現在画面を取り直して通常案内を再計画する。"',
    'foreground-lost replan')

path.write_text(text, encoding='utf-8')


contract_path = Path('tests/live-replan-owner-contract.mjs')
contract_path.write_text(r'''import fs from 'node:fs';

const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');

if (!reliability.includes('routeRecoveryIssue = "同じ操作を複数回行っても状態が変わらないため、別の安全な経路を選ぶ"'))
  throw new Error('repeated confirmed action failure must retain route recovery as the final fallback');
if (!helper.includes('_liveReplanPending = true') || !helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('ordinary replanning must delegate to the established live replan pipeline');

console.log('HelpSys live replan bootstrap passed.');
''', encoding='utf-8')

# Keep the same CI gate: the existing workflow always executes this script before focused
# contracts and the Windows build. The taxonomy patches therefore inherit the same verify-before-commit path.
for patch_name in ['tools/fix_recovery_taxonomy.py', 'tools/fix_recovery_taxonomy_followup.py']:
    patch = Path(patch_name)
    if patch.exists():
        exec(compile(patch.read_text(encoding='utf-8'), str(patch), 'exec'), {'__name__': '__main__'})
