from pathlib import Path


guards_path = Path('src/HelpSys.Desktop/MainWindow.DeepAuditGuards.cs')
guards = guards_path.read_text(encoding='utf-8')

# Remove the position-only off-route detector. A click outside the guide rectangle is not,
# by itself, proof that the user left the valid route. StableGuidance already observes actual
# screen/context changes and replans after those changes settle.
guards = guards.replace('    private int _offRouteRecoveryInFlight;\n', '')
guards = guards.replace('        _actionObserver.LeftClick += ObserveOffRouteClickDeepAudit;\n', '')
guards = guards.replace('        _actionObserver.LeftClick -= ObserveOffRouteClickDeepAudit;\n', '')
guards = guards.replace('        Interlocked.Exchange(ref _offRouteRecoveryInFlight, 0);\n', '')

start = guards.find('    private async void ObserveOffRouteClickDeepAudit(Point point)\n')
end = guards.find('    private void ObserveTypeTextSubmitDeepAudit(KeyObservation observation)\n', start)
if start >= 0:
    if end < 0:
        raise SystemExit('off-route handler end anchor not found')
    guards = guards[:start] + guards[end:]
elif 'ObserveOffRouteClickDeepAudit' in guards:
    raise SystemExit('unexpected remaining off-route handler reference')

# Remove the self-window hit-test helper that existed only for the deleted off-route detector.
helper = guards.find('    private bool IsPointInsideHelpSysWindow(Point screenPoint)\n')
if helper >= 0:
    # It is the final method in this partial class.
    class_end = guards.rfind('\n}')
    if class_end <= helper:
        raise SystemExit('off-route helper class-end anchor not found')
    guards = guards[:helper] + guards[class_end:]

guards_path.write_text(guards, encoding='utf-8')


contract_path = Path('tests/deep-audit-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
old = '''assert(guards.includes('off_route_click'), 'Off-route clicks must be recorded as route-deviation evidence.');\nassert(guards.includes('TryRouteRecoveryAsync("案内枠以外の場所が操作された"'), 'Off-route clicks must immediately trigger route recovery.');\nassert(guards.includes('IsPointInsideHelpSysWindow'), 'HelpSys self-interaction must not be mistaken for route deviation.');\n'''
new = '''assert(!guards.includes('ObserveOffRouteClickDeepAudit'), 'Click position alone must not trigger route recovery.');\nassert(!guards.includes('off_route_click'), 'Off-guide clicks must not be classified as route deviation before the resulting UI state is observed.');\nassert(!guards.includes('TryRouteRecoveryAsync("案内枠以外の場所が操作された"'), 'Off-guide clicks must not immediately invoke the heavy recovery planner.');\nassert(!guards.includes('_actionObserver.LeftClick += ObserveOffRouteClickDeepAudit;'), 'The deep-audit guard must leave ordinary click observation to the normal verifier/live watcher.');\n'''
if old in contract:
    contract = contract.replace(old, new, 1)
elif "assert(!guards.includes('ObserveOffRouteClickDeepAudit')" not in contract:
    raise SystemExit('deep-audit off-route contract anchor not found')

# Explicitly lock in the intended owner of real UI drift.
anchor = "assert(stable.includes('AttachDeepAuditGuards();'), 'Deep-audit interaction guards must be attached with the live watcher.');\n"
addition = anchor + "assert(stable.includes('ConfirmStableLiveChange'), 'Actual stable screen changes, not click coordinates, must drive stale-guidance invalidation.');\n"
if "Actual stable screen changes, not click coordinates" not in contract:
    if anchor not in contract:
        raise SystemExit('stable watcher contract anchor not found')
    contract = contract.replace(anchor, addition, 1)

contract_path.write_text(contract, encoding='utf-8')
