import fs from 'node:fs';

const update = fs.readFileSync('src/HelpSys.Desktop/Services/UpdateService.cs', 'utf8');
const ui = fs.readFileSync('src/HelpSys.Desktop/MainWindow.Update.cs', 'utf8');
const project = fs.readFileSync('src/HelpSys.Desktop/HelpSys.Desktop.csproj', 'utf8');
const workflow = fs.readFileSync('.github/workflows/outlaw-release.yml', 'utf8');

if (!update.includes('NormalizeBuildId(latest.BuildId)') ||
    !update.includes('NormalizeBuildId(currentBuild)'))
  throw new Error('Outlaw updater must compare normalized release and executable build IDs');

if (!update.includes('normalized.StartsWith("outlaw-", StringComparison.Ordinal)'))
  throw new Error('Outlaw updater must strip the executable outlaw- build prefix before comparison');

if (!ui.includes('Task.Delay(OutlawModePolicy.Enabled ? 250 : 1800)'))
  throw new Error('Outlaw updater should check the latest channel immediately after startup');

if (!update.includes('releases/tags/outlaw-latest'))
  throw new Error('Outlaw updater must remain pinned to the outlaw-latest release channel');

if (!project.includes('HelpSysUpdateChannel') || !project.includes('outlaw-latest'))
  throw new Error('Outlaw binary must embed the outlaw-latest update channel');
if (!update.includes('GetCurrentUpdateChannel()') || !update.includes('Channel: outlaw-latest'))
  throw new Error('Outlaw updater must verify both executable and package channel metadata');

if (!update.includes('target_commitish') || !update.includes('TryGetReleaseTargetBuildId(root)'))
  throw new Error('Outlaw updater must bind the selected package to the outlaw-latest target commit');
if (!update.includes('string.Equals(candidate.BuildId, releaseTargetBuild'))
  throw new Error('Outlaw updater must prefer the package whose build ID matches the release target commit');
if (!update.includes('created_at') || !update.includes('createdAt <= latestCreatedAt'))
  throw new Error('Outlaw updater fallback must resolve equal semantic versions by asset creation time');
if (!update.includes('packageBuildMatch') || !update.includes('更新パッケージのBuild ID'))
  throw new Error('Outlaw updater must verify VERSION.txt build identity before replacement');

if (!ui.includes('SHA {sha}') || !ui.includes('build_identity'))
  throw new Error('HelpSys GUI and logs must expose the currently running build SHA');

if (!project.includes('<Version>3.2.5</Version>') ||
    !project.includes('<AssemblyVersion>3.2.5.0</AssemblyVersion>') ||
    !workflow.includes("$version = '3.2.5'"))
  throw new Error('Updater repair must ship as 3.2.5 so broken 3.2.4 clients can detect it');

if (!update.includes('Version.TryParse(match.Groups["version"].Value, out var candidateVersion)'))
  throw new Error('Outlaw updater must parse semantic versions from release assets');
if (update.includes('(?:\\d+\\.\\d+\\.\\d+-)?'))
  throw new Error('Outlaw updater must not accept unversioned legacy Outlaw assets');

console.log('HelpSys Outlaw updater contract passed.');
