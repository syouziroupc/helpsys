import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const guard = fs.readFileSync('src/HelpSys.Desktop/MainWindow.OutlawLoopGuard.cs', 'utf8');

const normalize = value => String(value).replace(/\s+/g, ' ').trim();
const need = (source, fragment, message) => {
  if (!normalize(source).includes(normalize(fragment))) throw new Error(message);
};

need(main,
  'if (!TryAcceptOutlawGuidance(decision, target, candidates, systemContext, target.Bounds, generation)) return;',
  'Structured Outlaw guidance must pass through the repeat guard');
need(main,
  'if (!TryAcceptOutlawGuidance(decision, null, candidates, systemContext, null, generation)) return;',
  'Keyboard Outlaw guidance must pass through the repeat guard');
need(main,
  'if (!OutlawModePolicy.Enabled) { ResetCurrentStateReplanBudget(); ResetResilienceRecovery(); }',
  'Outlaw presentation must not reset recovery budgets merely because guidance was shown');
need(quality,
  'if (!TryAcceptOutlawGuidance(visualDecision, null, candidates, systemContext, bounds, generation)) return;',
  'Vision-only Outlaw targets must pass through the repeat guard');
need(reliability,
  'RecordOutlawGuidanceOutcome( decision, _currentTarget, _stepBaseline, _stepSystemBaseline, _guidedBounds, changed: false);',
  'No-effect verification must record the failed Outlaw action/state key');
need(reliability,
  'RecordOutlawGuidanceOutcome( decision, _currentTarget, _stepBaseline, _stepSystemBaseline, _guidedBounds, changed: true);',
  'Verified progress must clear the failed Outlaw action/state key');
need(reliability,
  'var historyLimit = OutlawModePolicy.Enabled ? 64 : 12;',
  'Outlaw verification history must retain the expanded 64-entry budget');
need(guard,
  'string.Equals(key, _outlawFailedGuidanceKey, StringComparison.Ordinal)',
  'Outlaw repeat guard must compare the current action/state with the last failed action/state');
need(guard,
  'outlaw_repeat_blocked',
  'Outlaw repeat guard must write an explicit local history/log event');
need(guard,
  'ResetCurrentStateReplanBudget(); ResetResilienceRecovery();',
  'Outlaw recovery budgets must reset only after verified progress');

console.log('HelpSys Outlaw repeat-guard contract passed.');
