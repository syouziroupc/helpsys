from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


# Central policy: technical uncertainty is not route divergence. First use the existing
# bounded current-state replan path; after that, use the existing technical-clarification
# suppression/retry budget. Route recovery is reserved for repeated confirmed action failure.
policy_path = Path('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs')
if not policy_path.exists():
    policy_path.write_text('''using HelpSys.Models;\nusing HelpSys.Services;\n\nnamespace HelpSys;\n\npublic partial class MainWindow\n{\n    private void HandleTechnicalPlanningUncertainty(string reason, long generation)\n    {\n        if (_activeRequest is null ||\n            _sessionCts is null ||\n            _sessionCts.IsCancellationRequested ||\n            !_sessionState.IsCurrent(generation))\n            return;\n\n        if (TryQueueCurrentStateReplan(reason, generation)) return;\n\n        _history.Add(new GuideHistoryItem(\n            _stepNumber,\n            "technical_planning_uncertainty",\n            "現在の画面",\n            $"経路逸脱とは判定せず、通常の画面再取得で処理する: {reason}"));\n        if (_history.Count > 12) _history.RemoveAt(0);\n\n        WaitForClarification(\n            "現在の画面を安全に自動判定できないため、画面情報を取り直して案内を続けます。",\n            generation);\n    }\n}\n''', encoding='utf-8')


quality_path = Path('src/HelpSys.Desktop/MainWindow.QualityFirst.cs')
quality = quality_path.read_text(encoding='utf-8')

quality = replace_once(quality, '''            if (!HasUsableForeground(systemContext))\n            {\n                if (TryQueueCurrentStateReplan("前面ウィンドウを一時的に特定できない", generation)) return;\n                await TryRouteRecoveryAsync("再確認しても前面ウィンドウを特定できない", generation, cancellationToken);\n                return;\n            }\n''', '''            if (!HasUsableForeground(systemContext))\n            {\n                HandleTechnicalPlanningUncertainty("再確認しても前面ウィンドウを特定できない", generation);\n                return;\n            }\n''', 'foreground uncertainty')

quality = replace_once(quality, '''            catch (OperationCanceledException)\n            {\n                throw;\n            }\n            catch\n            {\n                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                await TryRouteRecoveryAsync("画面画像を取得できないため他の情報源から現在位置を復元する", generation, cancellationToken);\n                return;\n            }\n''', '''            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)\n            {\n                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty("Privacy Gateにより画面画像を利用できない", generation);\n                return;\n            }\n            catch (OperationCanceledException)\n            {\n                throw;\n            }\n            catch\n            {\n                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty("画面画像を取得できない", generation);\n                return;\n            }\n''', 'capture failure taxonomy')

for old, new, label in [
('''            if (!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext))\n            {\n                if (TryQueueCurrentStateReplan("確認中に画面が切り替わった", generation)) return;\n                await TryRouteRecoveryAsync("再確認後も確認中の画面切替が続いている", generation, cancellationToken);\n                return;\n            }\n''', '''            if (!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext))\n            {\n                HandleTechnicalPlanningUncertainty("確認中に画面切替が続いている", generation);\n                return;\n            }\n''', 'after-capture drift'),
('''            catch (GuideServiceException error)\n            {\n                if (error.Kind == GuideFailureKind.ContextChanged &&\n                    TryQueueCurrentStateReplan("通常計画中に画面状態が変化した", generation)) return;\n                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (_sessionState.IsCurrent(generation))\n                    await TryRouteRecoveryAsync($"通常計画を継続できない: {error.Kind}", generation, cancellationToken);\n                return;\n            }\n''', '''            catch (GuideServiceException error)\n            {\n                if (error.Kind == GuideFailureKind.ContextChanged)\n                {\n                    HandleTechnicalPlanningUncertainty("通常計画中に画面状態が変化した", generation);\n                    return;\n                }\n                if (_sessionState.IsCurrent(generation) && await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);\n                return;\n            }\n''', 'guide-service failure taxonomy'),
('''            if (!HasSameCaptureIdentity(systemContext, postPlanContext) || HasSystemTransitionV3(systemContext, postPlanContext))\n            {\n                if (TryQueueCurrentStateReplan("判断中に画面が変化した", generation)) return;\n                await TryRouteRecoveryAsync("再確認後も判断中の画面変化が続いている", generation, cancellationToken);\n                return;\n            }\n''', '''            if (!HasSameCaptureIdentity(systemContext, postPlanContext) || HasSystemTransitionV3(systemContext, postPlanContext))\n            {\n                HandleTechnicalPlanningUncertainty("判断中の画面変化が続いている", generation);\n                return;\n            }\n''', 'post-plan drift'),
('''                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))\n                {\n                    await TryRouteRecoveryAsync("完了を現在状態から確認できない", generation, cancellationToken);\n                    return;\n                }\n''', '''                if (!quality.ScreenConfirmed || quality.Confidence < MinimumQualityDoneConfidence || string.IsNullOrWhiteSpace(quality.VisualEvidence))\n                {\n                    HandleTechnicalPlanningUncertainty("完了を現在状態から確認できない", generation);\n                    return;\n                }\n''', 'unconfirmed done'),
('''                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                await TryRouteRecoveryAsync(\n                    string.IsNullOrWhiteSpace(quality.Instruction)\n                        ? "通常ルート上の次操作を確定できない"\n                        : quality.Instruction,\n                    generation,\n                    cancellationToken);\n                return;\n''', '''                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty(\n                    string.IsNullOrWhiteSpace(quality.Instruction)\n                        ? "次の操作を十分な信頼度で確定できない"\n                        : "次の操作候補を安全に確定できない",\n                    generation);\n                return;\n''', 'low-confidence target'),
('''                if (!quality.ScreenConfirmed)\n                {\n                    await TryRouteRecoveryAsync("対象なしのキー操作を画面情報で確認できない", generation, cancellationToken);\n                    return;\n                }\n''', '''                if (!quality.ScreenConfirmed)\n                {\n                    HandleTechnicalPlanningUncertainty("対象なしのキー操作を画面情報で確認できない", generation);\n                    return;\n                }\n''', 'unconfirmed key'),
('''            if (string.IsNullOrWhiteSpace(decision.TargetId))\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                await TryRouteRecoveryAsync("操作内容は候補になったが対象を特定できない", generation, cancellationToken);\n                return;\n            }\n''', '''            if (string.IsNullOrWhiteSpace(decision.TargetId))\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty("操作内容は候補になったが対象を特定できない", generation);\n                return;\n            }\n''', 'missing target'),
('''            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (TryQueueCurrentStateReplan("選ばれた対象が現在は操作できない", generation)) return;\n                await TryRouteRecoveryAsync("再確認しても選ばれた対象を操作できない", generation, cancellationToken);\n                return;\n            }\n''', '''            if (target is null || !target.Interactable || !target.Enabled || target.Bounds.IsEmpty)\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty("選ばれた対象を現在画面で操作できない", generation);\n                return;\n            }\n''', 'invalid target'),
('''            if (!HasSameCaptureIdentity(systemContext, prePresentContext) || HasSystemTransitionV3(systemContext, prePresentContext))\n            {\n                if (TryQueueCurrentStateReplan("案内表示の直前に画面が変わった", generation)) return;\n                await TryRouteRecoveryAsync("再確認後も案内表示直前の画面変化が続いている", generation, cancellationToken);\n                return;\n            }\n''', '''            if (!HasSameCaptureIdentity(systemContext, prePresentContext) || HasSystemTransitionV3(systemContext, prePresentContext))\n            {\n                HandleTechnicalPlanningUncertainty("案内表示直前の画面変化が続いている", generation);\n                return;\n            }\n''', 'pre-present drift'),
('''            if (freshTarget is null)\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                if (TryQueueCurrentStateReplan("案内対象が表示直前に消えた", generation)) return;\n                await TryRouteRecoveryAsync("再確認しても案内対象を確定できない", generation, cancellationToken);\n                return;\n            }\n''', '''            if (freshTarget is null)\n            {\n                if (await TryStructuredFallbackAsync(candidates, systemContext, generation, cancellationToken)) return;\n                HandleTechnicalPlanningUncertainty("案内対象を表示直前に再確認できない", generation);\n                return;\n            }\n''', 'fresh target missing')]:
    quality = replace_once(quality, old, new, label)

quality = replace_once(quality, '''        catch (OperationCanceledException)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n            {\n                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);\n                recoveryCts.CancelAfter(TimeSpan.FromSeconds(12));\n                try { await TryRouteRecoveryAsync("通常の画面確認が時間内に完了しなかった", generation, recoveryCts.Token); }\n                catch (OperationCanceledException)\n                {\n                    if (_sessionState.IsCurrent(generation))\n                        WaitForClarification("現在位置を特定するため、今いちばん手前に見えている画面の大きな見出しを1つ教えてください。そこから案内を続けます。", generation);\n                }\n            }\n        }\n        catch (InvalidOperationException ex)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n            {\n                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);\n                recoveryCts.CancelAfter(TimeSpan.FromSeconds(12));\n                try { await TryRouteRecoveryAsync($"操作対象の構造確認に失敗: {ex.GetType().Name}", generation, recoveryCts.Token); }\n                catch { WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation); }\n            }\n        }\n        catch (Exception ex)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n            {\n                using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);\n                recoveryCts.CancelAfter(TimeSpan.FromSeconds(12));\n                try { await TryRouteRecoveryAsync($"案内処理を現在状態から再構成: {ex.GetType().Name}", generation, recoveryCts.Token); }\n                catch { WaitForClarification("現在の画面の大きな見出しか、目立つボタン名を1つ教えてください。そこから案内を続けます。", generation); }\n            }\n        }\n''', '''        catch (OperationCanceledException)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n                HandleTechnicalPlanningUncertainty("通常の画面確認が時間内に完了しなかった", generation);\n        }\n        catch (InvalidOperationException ex)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n                HandleTechnicalPlanningUncertainty($"操作対象の構造確認に失敗: {ex.GetType().Name}", generation);\n        }\n        catch (Exception ex)\n        {\n            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })\n                HandleTechnicalPlanningUncertainty($"案内処理の例外: {ex.GetType().Name}", generation);\n        }\n''', 'outer technical failures')

quality = replace_once(quality, '''        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)\n        {\n            await TryRouteRecoveryAsync("画像上の候補位置が有効な操作領域にならない", generation, cancellationToken);\n            return;\n        }\n''', '''        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)\n        {\n            HandleTechnicalPlanningUncertainty("画像上の候補位置が有効な操作領域にならない", generation);\n            return;\n        }\n''', 'invalid visual bounds')

quality = replace_once(quality, '''        if (!HasSameCaptureIdentity(systemContext, currentContext) || HasSystemTransitionV3(systemContext, currentContext))\n        {\n            if (TryQueueCurrentStateReplan("画像上の候補を確認中に画面が変わった", generation)) return;\n            await TryRouteRecoveryAsync("再確認後も画像候補確認中の画面変化が続いている", generation, cancellationToken);\n            return;\n        }\n''', '''        if (!HasSameCaptureIdentity(systemContext, currentContext) || HasSystemTransitionV3(systemContext, currentContext))\n        {\n            HandleTechnicalPlanningUncertainty("画像候補確認中の画面変化が続いている", generation);\n            return;\n        }\n''', 'visual context drift')

quality = replace_once(quality, '''        else if (quality.Confidence < MinimumVisualOnlyTargetConfidence)\n        {\n            await TryRouteRecoveryAsync("画像候補とWindows構造が一致しない", generation, cancellationToken);\n            return;\n        }\n''', '''        else if (quality.Confidence < MinimumVisualOnlyTargetConfidence)\n        {\n            HandleTechnicalPlanningUncertainty("画像候補とWindows構造が一致しない", generation);\n            return;\n        }\n''', 'visual/structure mismatch')

quality_path.write_text(quality, encoding='utf-8')


# Observer failures are sensor failures, not route divergence. Keep route recovery only after
# repeated user action failure in CompleteCurrentStepV3Async.
rel_path = Path('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs')
rel = rel_path.read_text(encoding='utf-8')
for reason in [
    'マウス操作の結果監視で現在状態を確定できない',
    'キー操作の結果監視で現在状態を確定できない'
]:
    old = f'''                    var generation = _sessionState.Generation;\n                    if (!TryQueueCurrentStateReplan("{reason}", generation))\n                        await RecoverFromObserverFailureAsync("{reason}");\n'''
    new = f'''                    var generation = _sessionState.Generation;\n                    HandleTechnicalPlanningUncertainty("{reason}", generation);\n'''
    rel = replace_once(rel, old, new, f'observer taxonomy: {reason}')
rel_path.write_text(rel, encoding='utf-8')


# Remove the obsolete observer-specific route-recovery wrapper.
route_path = Path('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs')
route = route_path.read_text(encoding='utf-8')
start = route.find('    private async Task RecoverFromObserverFailureAsync(')
end = route.find('    private async Task<bool> TryRouteRecoveryAsync(', start)
if start >= 0 and end > start:
    route = route[:start] + route[end:]
route_path.write_text(route, encoding='utf-8')


# Update earlier contracts to the stricter taxonomy.
current_contract = Path('tests/current-state-replan-contract.mjs')
current_contract.write_text(r'''import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/MainWindow.RecoveryPolicy.cs', 'utf8');

if (!helper.includes('using HelpSys.Models;') || !helper.includes('using HelpSys.Services;'))
  throw new Error('current-state replan partial must import model and session-state namespaces');
if (!helper.includes('MaximumAutomaticCurrentStateReplans = 1'))
  throw new Error('automatic current-state replan must be strictly bounded to one attempt');
if (!helper.includes('_liveReplanPending = true') || !helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('current-state replan must use the established live replan pipeline');
if (helper.includes('await AdvanceGuideAsync()'))
  throw new Error('current-state replan helper must not recursively start the planner directly');
if (!policy.includes('TryQueueCurrentStateReplan(reason, generation)'))
  throw new Error('technical uncertainty must first use bounded current-state replan');
if (!policy.includes('WaitForClarification('))
  throw new Error('technical uncertainty must fall back to bounded technical retry/stop, not route recovery');

for (const reason of [
  '再確認しても前面ウィンドウを特定できない',
  '確認中に画面切替が続いている',
  '判断中の画面変化が続いている',
  '選ばれた対象を現在画面で操作できない',
  '案内表示直前の画面変化が続いている',
  '案内対象を表示直前に再確認できない',
  '画像候補確認中の画面変化が続いている'
]) {
  if (!quality.includes(`HandleTechnicalPlanningUncertainty("${reason}"`))
    throw new Error(`technical UI uncertainty must use current-state policy: ${reason}`);
}

const structuredStart = main.indexOf('private void ShowStructuredTarget(');
const keyboardStart = main.indexOf('private void ShowKeyboardGuide(');
const visionFallbackStart = main.indexOf('private async Task<bool> TryVisionFallbackAsync', keyboardStart);
const structuredBlock = main.slice(structuredStart, keyboardStart);
const keyboardBlock = main.slice(keyboardStart, visionFallbackStart);
if (!structuredBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful structured target guidance must reset the replan budget');
if (!keyboardBlock.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful keyboard guidance must reset the replan budget');
if (!quality.includes('ResetCurrentStateReplanBudget();'))
  throw new Error('successful visual target guidance must reset the replan budget');

console.log('HelpSys bounded current-state replan contract passed.');
''', encoding='utf-8')

live_contract = Path('tests/live-replan-owner-contract.mjs')
live_contract.write_text(r'''import fs from 'node:fs';

const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');

for (const reason of [
  'マウス操作の結果監視で現在状態を確定できない',
  'キー操作の結果監視で現在状態を確定できない'
]) {
  if (!reliability.includes(`HandleTechnicalPlanningUncertainty("${reason}"`))
    throw new Error(`observer failure must use technical current-state policy: ${reason}`);
  if (reliability.includes(`RecoverFromObserverFailureAsync("${reason}"`))
    throw new Error(`observer failure must not be classified as route recovery: ${reason}`);
}

const lost = reliability.indexOf('"foreground_lost"');
const retry = reliability.indexOf('replan = true;', lost);
if (lost < 0 || retry < 0)
  throw new Error('transient foreground loss after a user action must use ordinary replanning');
const lostBlock = reliability.slice(lost, retry + 40);
if (lostBlock.includes('routeRecoveryIssue ='))
  throw new Error('transient foreground loss must not be classified as route recovery');

if (!reliability.includes('routeRecoveryIssue = "同じ操作を複数回行っても状態が変わらないため、別の安全な経路を選ぶ"'))
  throw new Error('repeated confirmed action failure must retain route recovery as the final fallback');
if (!helper.includes('_liveReplanPending = true') || !helper.includes('TryRunPendingLiveReplanAsync()'))
  throw new Error('observer replanning must still delegate to the established live replan pipeline');

console.log('HelpSys live replan ownership contract passed.');
''', encoding='utf-8')
