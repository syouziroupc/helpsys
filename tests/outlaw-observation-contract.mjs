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

const need = (source, fragment, message) => {
  if (!source.includes(fragment)) throw new Error(message);
};

need(broker, 'OutlawModePolicy.Enabled\n                ? _scanner.CaptureCandidatesAsync(maxCandidates, cancellationToken)',
  'Outlaw must scan the full desktop UIA tree instead of only the foreground process');
need(broker, 'if (!OutlawModePolicy.Enabled && !HasSameIdentity(before, after))',
  'Outlaw must not discard a deep all-desktop observation solely because foreground changed during the scan');
need(quality, '_observationBroker.CaptureAsync(4000, cancellationToken)',
  'Outlaw quality planner must retain the expanded 4000-element observation budget');
need(scanner, 'visitedLimit = OutlawModePolicy.Enabled ? 24000 : 4500',
  'Outlaw UIA traversal must retain the 24k node budget');
need(scanner, 'elapsedLimitMs = OutlawModePolicy.Enabled ? 9000 : 1400',
  'Outlaw UIA traversal must retain the 9s observation budget');
need(scanner, 'ReadOutlawVisibleText',
  'Outlaw must retain TextPattern-backed semantic text extraction');
need(facade, 'return OutlawModePolicy.Enabled ? candidates : ScopeToForegroundWindow(processId, candidates);',
  'Outlaw facade must not re-scope the expanded candidate set to the foreground window');
need(capture, 'private const int MaxImageWidth = 2560;',
  'Outlaw capture must retain higher screenshot resolution');
need(capture, 'if (OutlawModePolicy.Enabled || shellSurface) return monitorArea;',
  'Outlaw must capture the full target monitor');
need(capture, 'var all = OutlawModePolicy.Enabled\n                ? Array.Empty<Rect>()',
  'Outlaw must not redact captured pixels');
need(watcher, 'if (!OutlawModePolicy.Enabled)',
  'Outlaw watcher must accept WinEvents from processes outside the original foreground PID');
need(live, 'outlaw_vision_target_kept',
  'Outlaw must keep grounded vision-only targets even without a UIA snap target');
need(cloud, '"/v1/outlaw-plan"',
  'Outlaw must use the dedicated planner endpoint');
need(cloud, 'TimeSpan.FromSeconds(120)',
  'Outlaw must retain the extended 120-second multimodal reasoning deadline');
need(context, 'if (HelpSys.Services.OutlawModePolicy.Enabled)',
  'Outlaw must preserve full observed browser URL context');

console.log('HelpSys Outlaw full-observation contract passed.');
