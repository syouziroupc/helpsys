import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const xaml = read('src/HelpSys.Desktop/MainWindow.xaml');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');

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
assert(!state.includes('TextTrimming='), 'Safety reasons must not be silently ellipsized.');

assert(privacy.includes('画面を見て案内します。秘密情報の画面では送信を停止します。'),
  'Normal idle privacy disclosure must stay concise enough for the established compact surface.');
assert(privacy.includes('安全版：秘密情報の画面は送信停止。クラウド音声は無効です。'),
  'Safe idle privacy disclosure must stay concise while retaining the key restriction.');
assert(!privacy.includes('画面情報を利用して案内します。秘密情報の画面では送信を止めます。画面解析はいつでも停止できます。'),
  'Verbose former startup copy must not reintroduce an unnecessary two-line normal idle state.');

console.log('HelpSys compact UI/surface-regression contract passed.');