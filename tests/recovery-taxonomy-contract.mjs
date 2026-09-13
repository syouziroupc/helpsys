import fs from 'node:fs';
import path from 'node:path';

const desktopDir = 'src/HelpSys.Desktop';
const csFiles = fs.readdirSync(desktopDir)
  .filter(name => name.endsWith('.cs'))
  .map(name => path.join(desktopDir, name));

const violations = [];
for (const file of csFiles) {
  const text = fs.readFileSync(file, 'utf8');
  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.includes('TryRouteRecoveryAsync(')) continue;
    if (file.endsWith('MainWindow.RouteRecovery.cs') && line.includes('private async Task<bool> TryRouteRecoveryAsync(')) continue;
    if (file.endsWith('MainWindow.ReliabilityV3.cs') && line.includes('await TryRouteRecoveryAsync(routeRecoveryIssue')) continue;
    violations.push(`${file}:${i + 1}: ${line.trim()}`);
  }
}

if (violations.length) {
  throw new Error(`Unexpected route-recovery call sites:\n${violations.join('\n')}`);
}

const quality = fs.readFileSync(path.join(desktopDir, 'MainWindow.QualityFirst.cs'), 'utf8');
const reliability = fs.readFileSync(path.join(desktopDir, 'MainWindow.ReliabilityV3.cs'), 'utf8');
const route = fs.readFileSync(path.join(desktopDir, 'MainWindow.RouteRecovery.cs'), 'utf8');
const policy = fs.readFileSync(path.join(desktopDir, 'MainWindow.RecoveryPolicy.cs'), 'utf8');

if (quality.includes('TryRouteRecoveryAsync('))
  throw new Error('quality planning uncertainty must not invoke route recovery directly');
if (reliability.includes('RecoverFromObserverFailureAsync'))
  throw new Error('observer failures must not use the old recovery wrapper');
if (route.includes('RecoverFromObserverFailureAsync'))
  throw new Error('obsolete observer recovery wrapper must be removed');
if (!reliability.includes('routeRecoveryIssue = "同じ操作を複数回行っても状態が変わらないため、別の安全な経路を選ぶ"'))
  throw new Error('repeated confirmed action failure must retain route recovery');
if (!policy.includes('technical_planning_uncertainty') || !policy.includes('TryQueueCurrentStateReplan(reason, generation)'))
  throw new Error('technical uncertainty must be separated from route recovery');
if (!quality.includes('StopWithGuideFailure(error)'))
  throw new Error('service failures must use service-failure handling');
if (!quality.includes('OperationCanceledException) when (!cancellationToken.IsCancellationRequested)'))
  throw new Error('privacy-gate cancellation must be distinguished from actual cancellation');

console.log('HelpSys route-recovery taxonomy contract passed.');
