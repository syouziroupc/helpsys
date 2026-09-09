import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const capture = read('src/HelpSys.Desktop/Services/ScreenCaptureService.cs');

assert(cloud.includes('valuePresent = !string.IsNullOrEmpty(x.Value)'), 'Cloud payload should expose only whether an input has content.');
assert(!cloud.includes('value = x.Password ? null : ShortValue(x.Value)'), 'Raw non-password UIA values must not be sent to the planner.');
assert(!cloud.includes('value = ShortValue(x.Value)'), 'Raw UIA values must remain local.');

assert(capture.includes('CaptureSensitiveInputBounds'), 'Screenshot privacy scan must include general populated input fields.');
assert(capture.includes('current.ControlType != ControlType.Edit && current.ControlType != ControlType.ComboBox'), 'Edit and ComboBox controls must be considered for screenshot redaction.');
assert(capture.includes('!string.IsNullOrEmpty(valuePattern.Current.Value)'), 'Populated input controls must be redacted without exporting their value.');
assert(capture.includes('if (current.IsPassword) return true;'), 'Password controls must remain unconditionally redacted.');

console.log('HelpSys privacy boundary contract passed.');
