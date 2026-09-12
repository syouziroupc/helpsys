import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const xaml = read('src/HelpSys.Desktop/MainWindow.xaml');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const stable = read('src/HelpSys.Desktop/MainWindow.StableGuidance.cs');

assert(xaml.includes('Width="620" MinHeight="122"'), 'Main HelpSys surface must retain the established compact 620px width and 122px minimum height.');
assert(xaml.includes('SizeToContent="Height"'), 'Main HelpSys surface must grow only when safety/clarification content actually needs height.');

for (const [needle, message] of [
  ['<ColumnDefinition Width="58" />', 'Voice-button column width regressed.'],
  ['<ColumnDefinition Width="66" />', 'Read-aloud column width regressed.'],
  ['<ColumnDefinition Width="54" />', 'Clear-button column width regressed.'],
  ['<ColumnDefinition Width="70" />', 'Guide-button column width regressed.'],
  ['x:Name="RequestBox"', 'RequestBox is missing.'],
  ['Height="38"', 'Primary controls must retain the established 38px interaction height.'],
  ['FontSize="15"', 'Primary request text must retain readable 15px sizing.'],
  ['x:Name="CommanderButton"', 'Commander control is missing.'],
  ['MinWidth="94"', 'Commander must retain its established minimum width.']
]) assert(xaml.includes(needle), message);

const headerStart = xaml.indexOf('x:Name="DragHandle"');
const mainRowStart = xaml.indexOf('<Grid Grid.Row="1"');
const privacyIndex = xaml.indexOf('x:Name="PrivacyButton"');
assert(headerStart >= 0 && mainRowStart > headerStart && privacyIndex > headerStart && privacyIndex < mainRowStart,
  'Privacy control must live in the title/header row so it does not squeeze normal status text.');

const footerStart = xaml.indexOf('<DockPanel Grid.Row="3"');
const footerEnd = xaml.indexOf('</DockPanel>', footerStart);
const footer = footerStart >= 0 && footerEnd > footerStart ? xaml.slice(footerStart, footerEnd) : '';
assert(!footer.includes('x:Name="PrivacyButton"'), 'Privacy button must not consume footer/status width.');
assert(footer.includes('x:Name="CommanderButton"') && footer.includes('Ctrl+Alt+H'), 'Footer must retain Commander and recall hotkey affordances.');

const state = xaml.match(/<TextBlock x:Name="StateText"[\s\S]*?\/>/)?.[0] ?? '';
assert(state.includes('TextWrapping="Wrap"'), 'Long safety reasons must remain readable rather than clipped.');
assert(state.includes('MaxHeight="48"'), 'Safety status growth must remain bounded so the assistant does not become visually dominant.');
assert(!state.includes('TextTrimming='), 'Privacy and safety status reasons must not be silently ellipsized.');

assert(privacy.includes('HelpSys 統合版：秘密情報の画面では送信を停止します。'),
  'Unified idle privacy disclosure must stay concise enough for the established compact surface.');
assert(!privacy.includes('安全版：秘密情報は送信停止。音声のクラウド送信も無効です。'),
  'Separate Safe-edition startup copy must not reappear after unification.');
assert(!privacy.includes('画面情報を利用して案内します。秘密情報の画面では送信を止めます。画面解析はいつでも停止できます。'),
  'Verbose former startup copy must not reintroduce an unnecessary two-line idle state.');

const loadedStart = stable.indexOf('private void MainWindow_StableLoaded');
const loadedEnd = stable.indexOf('private void StartLiveWatcherAfterInitialRender', loadedStart);
const loadedBody = loadedStart >= 0 && loadedEnd > loadedStart ? stable.slice(loadedStart, loadedEnd) : '';
assert(loadedBody.includes('DispatcherPriority.ContextIdle'), 'Global live watcher startup must stay behind the initial WPF render.');
assert(loadedBody.includes('StartLiveWatcherAfterInitialRender'), 'Loaded must schedule the deferred live watcher startup.');
assert(!loadedBody.includes('_liveWatcher.Start();'), 'Loaded must not synchronously register the global UIA watcher before first paint.');
assert(stable.includes('private void StartLiveWatcherAfterInitialRender()') && stable.includes('try { _liveWatcher.Start(); }'),
  'Deferred watcher startup must still enable live guidance after the initial render.');

for (const method of [
  'ConfirmStableLiveChange',
  'InvalidatePlannerForLiveContextChange',
  'HasHardStableLiveChange',
  'HasSemanticLiveStateChanged',
  'SemanticLiveStateMap',
  'HasStableLiveTopologyChanged',
  'StableRelevantLiveKeys',
  'ValidateCurrentVisionTargetAsync'
]) assert(stable.includes(method), `Live guidance safeguard missing after UI/performance changes: ${method}`);

console.log('HelpSys compact unified UI surface contract passed.');
