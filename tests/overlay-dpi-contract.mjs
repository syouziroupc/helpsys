import fs from 'node:fs';

const instruction = fs.readFileSync('src/HelpSys.Desktop/InstructionWindow.xaml.cs', 'utf8');
const overlay = fs.readFileSync('src/HelpSys.Desktop/OverlayWindow.xaml.cs', 'utf8');
const manifest = fs.readFileSync('src/HelpSys.Desktop/app.manifest', 'utf8');
const placement = fs.readFileSync('src/HelpSys.Desktop/Services/MonitorPlacementService.cs', 'utf8');
const project = fs.readFileSync('src/HelpSys.Desktop/HelpSys.Desktop.csproj', 'utf8');
const release = fs.readFileSync('.github/workflows/outlaw-release.yml', 'utf8');

for (const source of [instruction, overlay]) {
  if (!source.includes('MonitorPlacementService.GetDpiScaleForWindow(hwnd)'))
    throw new Error('overlay surfaces must query DPI only after their HWND has been handed to the target monitor');
}
if (!placement.includes('GetDpiForWindow') || !placement.includes('dpi / 96d'))
  throw new Error('PerMonitorV2 overlay DPI must use the DPI-aware GetDpiForWindow API with the Windows 96-DPI baseline');
if (placement.includes('GetDpiForMonitor'))
  throw new Error('PerMonitorV2 overlay path must not use GetDpiForMonitor, which Microsoft documents as not DPI-aware');
if (!overlay.includes('overlay_placement') || !instruction.includes('instruction_placement'))
  throw new Error('native overlay placement must emit requested/actual geometry diagnostics');
if (!overlay.includes('TryGetWindowBounds') || !instruction.includes('TryGetWindowBounds'))
  throw new Error('overlay placement diagnostics must verify native window bounds after SetWindowPos');

if (!instruction.includes('380 * scale') || !instruction.includes('76 * scale'))
  throw new Error('instruction window width and height must scale with per-monitor DPI');
if (!overlay.includes('7 * dpiScale') || !overlay.includes('initialPositioned'))
  throw new Error('target overlay padding must scale with per-monitor DPI');
if (!manifest.includes('PerMonitorV2'))
  throw new Error('application must remain PerMonitorV2 aware');

console.log('HelpSys per-monitor overlay DPI contract passed.');

if (!project.includes('<Version>3.1.6</Version>') || !project.includes('<FileVersion>3.1.6.0</FileVersion>'))
  throw new Error('Outlaw executable version metadata must identify the 3.1.6 build line');
if (!release.includes("$version = '3.1.6'") || !release.includes('Version: 3.1.6'))
  throw new Error('Outlaw release ZIP and VERSION.txt must stay synchronized with executable version metadata');
