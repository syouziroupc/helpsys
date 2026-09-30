import fs from 'node:fs';

const broker = fs.readFileSync('src/HelpSys.Desktop/Services/ObservationBroker.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const facade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');
const capture = fs.readFileSync('src/HelpSys.Desktop/ScreenCaptureService.cs', 'utf8');
const watcher = fs.readFileSync('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const context = fs.readFileSync('src/HelpSys.Desktop/Models/SystemContextSnapshot.cs', 'utf8');

const normalize = value => String(value).replace(/\s+/g, ' ').trim();
const need = (source, fragment, message) => {
  if (!normalize(source).includes(normalize(fragment))) throw new Error(message);
};

need(broker, 'public Task<ObservationSnapshot> CaptureQuickAsync(',
  'Outlaw must expose a bounded Quick observation before Deep planning');
need(broker, '_scanner.CaptureCandidatesForProcessAsync(',
  'Planner observations must start from the current foreground UI surface');
if (broker.includes('_scanner.CaptureCandidatesAsync(maxCandidates'))
  throw new Error('Outlaw observation must not deep-scan the entire desktop and filter it afterward');
need(broker, 'var scanPhase = quick ? "observation.quick.scan" : "observation.deep.scan";',
  'Quick and Deep observation timings must be distinguishable');
need(broker, '"outlaw_observation_timing"',
  'Quick/Deep observation duration must be persisted in local diagnostics');
need(broker, 'if (!HasSameIdentity(before, after))',
  'Every planner observation must reject mixed UIA/system-context snapshots');
need(broker, 'HasSameShellSurface(before, after)',
  'Windows Start/Search host handoffs must remain one logical shell surface');
need(broker, '"outlaw_observation_discarded"',
  'Discarded mixed observations must be logged');

need(quality, '_observationBroker.CaptureQuickAsync(cancellationToken)',
  'Outlaw must run a Quick observation before Deep planning');
need(quality, '_observationBroker.CaptureAsync(4000, cancellationToken)',
  'Quick misses must escalate to the existing Deep observation budget');
need(quality, '"observation_quick"',
  'Quick observation completion must be phase-timed');
need(quality, '"observation_deep"',
  'Deep observation completion must be phase-timed');
need(quality, 'allowAbsenceBasedPaths: false',
  'Quick observation must not make absence-based application-launch decisions');
need(quality, 'allowAbsenceBasedPaths: true',
  'Deep observation may use the existing absence-based application-launch path');
need(quality, 'phase=after_screenshot;discarding planner observation because foreground identity changed',
  'Outlaw must discard an observation when foreground identity changes after screenshot capture');
need(quality, 'phase=after_planner;discarding planner result because foreground identity changed',
  'Outlaw must discard an LLM result when foreground identity changes while the planner is running');
need(quality, '"target_revalidate"',
  'Structured target revalidation must be independently phase-timed');
need(quality, '"outlaw_target_revalidation_failed"',
  'Outlaw must log stale structured targets before re-grounding');
need(quality, 'TryQueueCurrentStateReplan("案内表示直前に対象が消えたため、古い座標を使わず現在画面から再探索する"',
  'Failed revalidation must re-ground from current state instead of using stale bounds');
if (quality.includes('freshTarget = target;'))
  throw new Error('failed target revalidation must never fall back to stale pre-plan bounds');

need(scanner, 'var quickOutlaw = OutlawModePolicy.Enabled && rootProcessId is > 0 && maxCandidates <= 180;',
  'Quick UIA traversal must be explicit and process-scoped');
need(scanner, 'visitedLimit = quickOutlaw ? 3200 : OutlawModePolicy.Enabled ? 18000 : 4500',
  'Quick traversal must have a smaller node budget while preserving the Deep budget');
need(scanner, 'elapsedLimitMs = quickOutlaw ? 900 : OutlawModePolicy.Enabled ? 4500 : 1400',
  'Quick traversal must have a sub-second scan budget while preserving the Deep budget');
need(scanner, 'depthLimit = quickOutlaw ? 10 : OutlawModePolicy.Enabled ? 18 : 10',
  'Quick traversal must be shallower while Deep retains semantic coverage');
need(scanner, '? quickOutlaw',
  'Quick traversal must use the lightweight metadata path');
need(scanner, 'ReadOutlawVisibleText',
  'Deep Outlaw observation must retain TextPattern-backed semantic extraction');
need(scanner, 'EnqueueProcessSurfaceRoots(rootProcessId.Value, queue)',
  'Process-scoped scans must preserve Windows shell host fusion');
need(scanner, 'private bool IsExcludedProcess(int processId)',
  'UIA observer scans must exclude observer and parent HelpSys process IDs');

need(facade, 'return OutlawModePolicy.Enabled ? candidates : ScopeToForegroundWindow(processId, candidates);',
  'Outlaw process-scoped scans must preserve shell-host candidates from the lower scanner');
need(capture, 'private const int MaxImageWidth = 2560;',
  'Outlaw capture must retain higher screenshot resolution');
need(capture, 'if (OutlawModePolicy.Enabled || shellSurface) return monitorArea;',
  'Outlaw must capture the full target monitor');
need(watcher, 'EventSystemForeground = 0x0003',
  'Outlaw watcher must subscribe to foreground ownership changes');
need(watcher, 'if (eventType == EventSystemForeground)',
  'Foreground ownership changes must bypass the scoped object-event filter');
need(watcher, 'if (unchecked((int)pid) != scope) return;',
  'Background object/property churn must remain scoped to the current foreground process');
need(watcher, 'public DateTime? LastChangeUtc',
  'Planner must be able to reject coordinates after same-window WinEvent changes');
need(quality, '"outlaw_visual_capture_stale"',
  'Visual evidence must be discarded when the UI changes during capture');
need(quality, '_reuseLastOutlawObservationOnce',
  'Outlaw must retain one bounded same-observation alternate plan after verified no-effect');
need(quality, '"outlaw_stale_vision_target"',
  'Vision-only coordinates must be discarded after post-capture UI changes');
need(quality, 'SnapToAccessibleCandidateAsync',
  'Vision-only targets should be grounded to accessible UI when possible');
need(quality, 'IsVisualTargetOnCurrentSurface',
  'Vision-only targets must remain on the current foreground surface');
need(live, 'outlaw_vision_target_invalidated',
  'Presented vision targets must be invalidated on live foreground changes');
if (live.includes('outlaw_vision_target_kept'))
  throw new Error('Outlaw must never keep stale vision coordinates merely because UIA cannot snap them');

need(main, '"outlaw_structured_target_present"',
  'Outlaw must log the structured target entering presentation');
need(main, '"outlaw_present_transition_rejected"',
  'Rejected presentation transitions must be logged');
need(cloud, '"/v1/outlaw-plan"',
  'Outlaw must use the dedicated planner endpoint');
need(context, 'if (HelpSys.Services.OutlawModePolicy.Enabled)',
  'Outlaw must preserve full observed browser URL context');

console.log('HelpSys Outlaw progressive-observation contract passed.');
