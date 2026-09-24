import fs from 'node:fs';

const update = fs.readFileSync('src/HelpSys.Desktop/Services/UpdateService.cs', 'utf8');
const ui = fs.readFileSync('src/HelpSys.Desktop/MainWindow.Update.cs', 'utf8');

if (!update.includes('NormalizeBuildId(latest.BuildId)') ||
    !update.includes('NormalizeBuildId(CurrentBuildId)'))
  throw new Error('Outlaw updater must compare normalized release and executable build IDs');

if (!update.includes('normalized.StartsWith("outlaw-", StringComparison.Ordinal)'))
  throw new Error('Outlaw updater must strip the executable outlaw- build prefix before comparison');

if (!ui.includes('Task.Delay(OutlawModePolicy.Enabled ? 250 : 1800)'))
  throw new Error('Outlaw updater should check the latest channel immediately after startup');

if (!update.includes('releases/tags/outlaw-latest'))
  throw new Error('Outlaw updater must remain pinned to the outlaw-latest release channel');

console.log('HelpSys Outlaw updater contract passed.');
