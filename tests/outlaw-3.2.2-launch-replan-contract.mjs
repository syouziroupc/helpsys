import fs from 'node:fs';

const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const broker = fs.readFileSync('src/HelpSys.Desktop/Services/ObservationBroker.cs', 'utf8');

const need = (source, fragment, message) => {
  if (!source.includes(fragment)) throw new Error(message + ': missing ' + fragment);
};

need(quality, 'TryOutlawApplicationLaunchFastPath(', 'desktop application goals need a local Start fast path');
need(quality, 'application_launch_via_start', 'application Start fast path must be observable in logs');
need(quality, 'StartMenuExperienceHost', 'fast path must stop once Start is already open');
need(quality, 'TryQueueCurrentStateReplan("AI判断中に前面画面が変わったため', 'stale planner results must trigger one fresh observation');

need(reliability, 'IsLikelyApplicationLaunchActionV3(', 'action verification must identify application launches');
need(reliability, 'TimeSpan.FromMilliseconds(6500)', 'slow app launch must receive a bounded foreground wait');
need(reliability, 'outlaw_application_launch_verified', 'successful delayed launch verification must be logged');

need(broker, 'HasSameIdentity(snapshot.System, current) ||', 'current observation must preserve exact app identity');
need(broker, 'HasSameShellSurface(snapshot.System, current)', 'Windows shell PID/HWND handoff must remain current');

console.log('HelpSys Outlaw 3.2.2 launch/replan contract passed.');
