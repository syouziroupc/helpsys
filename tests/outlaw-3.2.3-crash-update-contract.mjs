import fs from 'node:fs';

const update = fs.readFileSync('src/HelpSys.Desktop/MainWindow.Update.cs', 'utf8');
const app = fs.readFileSync('src/HelpSys.Desktop/App.xaml.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const log = fs.readFileSync('src/HelpSys.Desktop/Services/LocalLogService.cs', 'utf8');
const project = fs.readFileSync('src/HelpSys.Desktop/HelpSys.Desktop.csproj', 'utf8');
const release = fs.readFileSync('.github/workflows/outlaw-release.yml', 'utf8');

const need = (source, fragment, message) => {
  if (!String(source).includes(fragment)) throw new Error(message);
};

need(update, '_activeRequest is not null || _planning || _verifyingAction || _awaitingClarification',
  'self-update must be blocked while guidance is active');
need(update, 'update_blocked_during_guidance',
  'blocked update attempts must be logged');
need(update, 'MarkExitReason("update_handoff"',
  'successful updater handoff must record an explicit exit reason');

need(main, 'MarkExitReason("user_close_button"',
  'explicit close button must record a user-close exit reason');
need(main, 'MarkExitReasonIfUnset("window_closing")',
  'window closing must have a fallback exit reason');

need(app, 'DispatcherUnhandledException += OnDispatcherUnhandledException',
  'WPF dispatcher exceptions must be observed');
need(app, 'AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException',
  'AppDomain terminating exceptions must be observed');
need(app, 'TaskScheduler.UnobservedTaskException += OnUnobservedTaskException',
  'unobserved task exceptions must be logged');
need(app, 'SessionEnding += OnSessionEnding',
  'Windows session shutdown must be distinguished from a crash');

need(log, 'public static void MarkExitReason(',
  'local logger must support explicit exit reasons');
need(log, 'public static void WriteException(',
  'local logger must persist exception details');
need(log, 'shutdown\\treason={_exitReason}',
  'shutdown log must carry the final exit reason');

need(project, '<Version>3.2.3</Version>',
  'Outlaw project version must be 3.2.3');
need(project, '<FileVersion>3.2.3.0</FileVersion>',
  'Outlaw file version must be 3.2.3.0');
need(release, "$version = '3.2.3'",
  'release workflow must publish version 3.2.3');

console.log('HelpSys Outlaw 3.2.3 crash/update regression contract passed.');
