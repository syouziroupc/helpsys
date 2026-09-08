# HelpSys Reliability Audit v2

This audit fixes reproducible failure paths discovered after the stable-guidance update.

- Live UI watcher no longer leaves orphaned semaphore waits when heartbeat wins.
- Watcher shutdown waits for its pump before disposing synchronization state.
- Character-by-character typing noise remains ignored, but real app/browser switches during typing invalidate stale guidance.
- Browser URL detection no longer treats generic webpage search fields as the browser address bar.
- Structured final validation covers the same 420 UI candidates as the planner.
- Address-bar-style guidance is rejected if the selected browser Edit lacks an address-bar accessibility hint.
- Deliberate stale-plan cancellation is normalized separately from actual network failure.
- Vision screenshots independently discover password fields before pixel capture and fail closed if privacy validation fails.
- Vision coordinates are never spoken or drawn unless reconciled to a current interactable accessibility element.
- Focus movement alone is no longer accepted as proof that an ordinary click succeeded.
- Planning budget allows structured + vision phases without the former 24-second combined deadline.
- New sessions cannot overlap a still-unwinding planner after clear/cancel.
- Preview ZIP is renamed to `HelpSys-Reliability-v2-win-x64.zip`; stale HelpSys ZIP assets are removed on release update.
