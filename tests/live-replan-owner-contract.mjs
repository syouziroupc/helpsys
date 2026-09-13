import fs from 'node:fs';

const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');

for (const reason of [
  'マウス操作の結果監視で現在状態を確定できない',
  'キー操作の結果監視で現在状態を確定できない'
]) {
  const replan = reliability.indexOf(`TryQueueCurrentStateReplan("${reason}"`);
  const recovery = reliability.indexOf(`RecoverFromObserverFailureAsync("${reason}"`);
  if (replan < 0 || recovery < 0 || replan > recovery)
    throw new Error(`observer failure must try current-state replan before heavy recovery: ${reason}`);
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
