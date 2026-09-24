import fs from 'node:fs';

const instruction = fs.readFileSync('src/HelpSys.Desktop/InstructionWindow.xaml.cs', 'utf8');
const overlay = fs.readFileSync('src/HelpSys.Desktop/OverlayWindow.xaml.cs', 'utf8');
const manifest = fs.readFileSync('src/HelpSys.Desktop/app.manifest', 'utf8');
const placement = fs.readFileSync('src/HelpSys.Desktop/Services/MonitorPlacementService.cs', 'utf8');

for (const source of [instruction, overlay]) {
  if (!source.includes('MonitorPlacementService.GetDpiScaleForBounds(physicalBounds)'))
    throw new Error('overlay surfaces must derive geometry from the target monitor rather than their pre-move HWND monitor');
}
if (!placement.includes('GetDpiForMonitor') || !placement.includes('dpiX / 96d'))
  throw new Error('target-monitor DPI resolver must use effective monitor DPI with the Windows 96-DPI baseline');
if (!overlay.includes('overlay_placement') || !instruction.includes('instruction_placement'))
  throw new Error('native overlay placement must emit requested/actual geometry diagnostics');
if (!overlay.includes('TryGetWindowBounds') || !instruction.includes('TryGetWindowBounds'))
  throw new Error('overlay placement diagnostics must verify native window bounds after SetWindowPos');

if (!instruction.includes('380 * dpiScale') || !instruction.includes('76 * dpiScale'))
  throw new Error('instruction window width and height must scale with per-monitor DPI');
if (!overlay.includes('7 * dpiScale'))
  throw new Error('target overlay padding must scale with per-monitor DPI');
if (!manifest.includes('PerMonitorV2'))
  throw new Error('application must remain PerMonitorV2 aware');

console.log('HelpSys per-monitor overlay DPI contract passed.');
