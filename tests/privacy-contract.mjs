import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const capture = read('src/HelpSys.Desktop/Services/ScreenCaptureService.cs');

assert(cloud.includes('InputPresentSentinel = "<input-present>"'), 'Input presence must use a fixed non-sensitive sentinel.');
assert(cloud.includes('value = x.Password || string.IsNullOrEmpty(x.Value) ? null : InputPresentSentinel'), 'Cloud payload should expose only whether an input has content through the existing value channel.');
assert(!cloud.includes('ShortValue(x.Value)'), 'Raw UIA input values must never be copied into the planner payload.');
assert(!cloud.includes('value = x.Password ? null : ShortValue(x.Value)'), 'Raw non-password UIA values must not be sent to the planner.');

assert(cloud.includes('SanitizeSystemContextForCloud(systemContext, request)'),
  'Browser context must pass through a cloud-only sanitization boundary.');
assert(cloud.includes('uri.IdnHost.ToLowerInvariant()'),
  'Cloud browser context should preserve only normalized host identity.');
assert(cloud.includes('Uri.EscapeDataString(knownSiteSearchMarker)'),
  'Known-site search detection may preserve only the fixed canonical site marker, never the real query string.');
assert(!cloud.includes('systemContext = systemContext'),
  'Planner payload must not explicitly serialize the raw local system context.');

assert(capture.includes('CaptureSensitiveInputBounds'), 'Screenshot privacy scan must independently inspect visible input controls.');
assert(capture.includes('current.IsPassword || current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox'),
  'Password, Edit, and ComboBox controls must be redacted regardless of ValuePattern readability.');
assert(!capture.includes('return !string.IsNullOrEmpty(valuePattern.Current.Value)'),
  'Screenshot privacy must not depend on successfully reading an input value first.');
assert(capture.includes('fail closed instead of sending a partial image'),
  'Privacy scan failure must remain fail-closed.');

console.log('HelpSys privacy boundary contract passed.');
