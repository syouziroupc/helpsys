import fs from 'node:fs';

const instruction = fs.readFileSync('src/HelpSys.Desktop/InstructionWindow.xaml.cs', 'utf8');
const overlay = fs.readFileSync('src/HelpSys.Desktop/OverlayWindow.xaml.cs', 'utf8');
const manifest = fs.readFileSync('src/HelpSys.Desktop/app.manifest', 'utf8');

for (const source of [instruction, overlay]) {
  if (!source.includes('GetDpiForWindow'))
    throw new Error('overlay surfaces must derive native-pixel geometry from current monitor DPI');
  if (!source.includes('/ 96d'))
    throw new Error('overlay DPI scaling must use the Windows 96-DPI baseline');
}

if (!instruction.includes('380 * dpiScale') || !instruction.includes('76 * dpiScale'))
  throw new Error('instruction window width and height must scale with per-monitor DPI');
if (!overlay.includes('7 * dpiScale'))
  throw new Error('target overlay padding must scale with per-monitor DPI');
if (!manifest.includes('PerMonitorV2'))
  throw new Error('application must remain PerMonitorV2 aware');

console.log('HelpSys per-monitor overlay DPI contract passed.');
