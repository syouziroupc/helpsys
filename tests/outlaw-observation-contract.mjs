import fs from 'node:fs';

const broker = fs.readFileSync('src/HelpSys.Desktop/Services/ObservationBroker.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const facade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');
const capture = fs.readFileSync('src/HelpSys.Desktop/ScreenCaptureService.cs', 'utf8');
const watcher = fs.readFileSync('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const context = fs.readFileSync('src/HelpSys.Desktop/Models/SystemContextSnapshot.cs', 'utf8');

const normalize = value => String(value).replace(/\s+/g, ' ').trim();
const need = (source, fragment, message) => {
  if (!normalize(source).includes(normalize(fragment))) throw new Error(message);
};

need(broker, 'OutlawModePolicy.Enabled\n                ? _scanner.CaptureCandidatesAsync(maxCandidates, cancellationToken)',
  'Outlaw must scan the full desktop UIA tree instead of only the foreground process');
need(broker, 'if (!HasSameIdentity(before, after))',
  'Every planner observation must reject mixed UIA/system-context snapshots when foreground identity changes during the scan');
need(broker, '"outlaw_observation_discarded"',
  'Outlaw must log discarded mixed observations for field diagnostics');
if (broker.includes('if (!OutlawModePolicy.Enabled && !HasSameIdentity(before, after))'))
  throw new Error('Outlaw must not retain a mixed observation merely because it is operation-first');
need(quality, '_observationBroker.CaptureAsync(1200, cancellationToken)',
  'Outlaw quality planner must use a bounded broad observation budget that avoids stale multi-second snapshots');
need(quality, 'phase=after_screenshot;discarding planner observation because foreground identity changed',
  'Outlaw must discard an observation when foreground identity changes after screenshot capture');
need(quality, 'phase=after_planner;discarding planner result because foreground identity changed',
  'Outlaw must discard an LLM result when foreground identity changes while the planner is running');
if (quality.includes('if (!OutlawModePolicy.Enabled && !_observationBroker.IsCurrent(snapshot))'))
  throw new Error('Outlaw freshness checks must not be bypassed around screenshot or planner latency');
if (quality.includes('if (freshTarget is null) freshTarget = target'))
  throw new Error('failed target revalidation must never fall back to stale pre-plan bounds');
need(quality, '"outlaw_target_revalidation_failed"',
  'Outlaw must log and reject a stale structured target when pre-display revalidation fails');
need(scanner, 'visitedLimit = OutlawModePolicy.Enabled ? 18000 : 4500',
  'Outlaw UIA traversal must remain broad but bounded to reduce observation staleness');
need(scanner, 'elapsedLimitMs = OutlawModePolicy.Enabled ? 4500 : 1400',
  'Outlaw UIA traversal must cap one observation scan near interactive latency instead of 15 seconds');
need(scanner, 'depthLimit = OutlawModePolicy.Enabled ? 18 : 10',
  'Outlaw UIA traversal must retain deeper coverage without unbounded 24-level traversal');
need(scanner, 'ReadOutlawVisibleText',
  'Outlaw must retain TextPattern-backed semantic text extraction');
need(scanner, 'private bool IsExcludedProcess(int processId)',
  'UIA observer scans must explicitly exclude both observer and parent HelpSys process IDs');
need(scanner, '_excludedProcessId > 0 && processId == _excludedProcessId',
  'parent HelpSys PID must not leak into full-desktop Outlaw candidates');

need(facade, 'return OutlawModePolicy.Enabled ? candidates : ScopeToForegroundWindow(processId, candidates);',
  'Outlaw facade must not re-scope the expanded candidate set to the foreground window');
need(capture, 'private const int MaxImageWidth = 2560;',
  'Outlaw capture must retain higher screenshot resolution');
need(capture, 'if (OutlawModePolicy.Enabled || shellSurface) return monitorArea;',
  'Outlaw must capture the full target monitor');
need(capture, 'var all = OutlawModePolicy.Enabled\n                ? Array.Empty<Rect>()',
  'Outlaw must not redact captured pixels');
need(watcher, 'EventSystemForeground = 0x0003',
  'Outlaw watcher must subscribe to global foreground ownership changes');
need(watcher, 'if (eventType == EventSystemForeground)',
  'Foreground ownership changes must bypass the scoped object-event filter');
need(watcher, 'if (unchecked((int)pid) != scope) return;',
  'Background object/property churn must remain scoped to the current foreground process');
need(watcher, 'public DateTime? LastChangeUtc',
  'planner must be able to reject vision coordinates after same-window WinEvent changes');
need(quality, '"outlaw_visual_capture_changed"',
  'visual evidence must be discarded when the observed UI changes during screenshot capture');
need(quality, '"outlaw_stale_vision_target"',
  'vision-only coordinates must be discarded when foreground UI changes while the model is reasoning');
need(quality, 'OutlawModePolicy.Enabled || quality.Confidence >= MinimumStructuredFallbackConfidence',
  'Outlaw structured targets must not be rejected solely by the normal confidence threshold');
need(quality, '(!OutlawModePolicy.Enabled && quality.Confidence < MinimumQualityTargetConfidence)',
  'normal confidence veto must remain disabled only for Outlaw target guidance');
need(live, 'outlaw_vision_target_invalidated',
  'Outlaw must discard vision-only coordinates when live foreground UI changes after presentation');
if (live.includes('outlaw_vision_target_kept'))
  throw new Error('Outlaw must never re-show stale vision coordinates merely because UIA cannot snap them');
need(cloud, '"/v1/outlaw-plan"',
  'Outlaw must use the dedicated planner endpoint');
need(cloud, 'TimeSpan.FromSeconds(18)',
  'Outlaw must retain the bounded 18-second multimodal reasoning deadline');
need(context, 'if (HelpSys.Services.OutlawModePolicy.Enabled)',
  'Outlaw must preserve full observed browser URL context');

console.log('HelpSys Outlaw full-observation contract passed.');
