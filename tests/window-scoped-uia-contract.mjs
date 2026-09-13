import fs from 'node:fs';

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const capture = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StructuralCapture.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');

if (!scanner.includes('CaptureCandidatesForWindowAsync') || !scanner.includes('AutomationElement.FromHandle'))
  throw new Error('UIA scanner must support verified HWND-scoped capture');
if (!scanner.includes('rootProcessId != expectedProcessId'))
  throw new Error('HWND-scoped capture must reject a root belonging to another process');
if (!scanner.includes('CaptureCandidatesFromQueue'))
  throw new Error('process and HWND scans must share the same candidate filtering/ranking pipeline');
if (!capture.includes('CaptureCandidatesForProcessAsync') || !capture.includes('CaptureCandidatesForWindowAsync'))
  throw new Error('foreground structural capture must retain PID scan and add HWND supplementation');
if (!capture.includes('preferWindowScope || IsWeakStructuralSnapshot(processCandidates)'))
  throw new Error('normal guidance must only supplement weak PID snapshots unless the caller explicitly prefers HWND scope');
if (!capture.includes('StructuralSnapshotStrength(windowCandidates) > StructuralSnapshotStrength(processCandidates)'))
  throw new Error('weak-snapshot supplementation must keep the stronger structural snapshot');
const uses = quality.match(/CaptureForegroundCandidatesAsync\(/g)?.length ?? 0;
if (uses < 2)
  throw new Error('quality guidance and structured fallback must use the shared foreground structural capture path');

console.log('HelpSys HWND-scoped UIA fallback contract passed.');
