from pathlib import Path
import ast
import base64

path = Path(__file__).with_name('apply_tiered_safety_fix.py')
text = path.read_text(encoding='utf-8')

# Repair two Japanese payloads that were damaged while the patch helper was transported.
replacements = {
    'ICAgICAgICAgICAgaWYgKHN0b3JhZ2UgfHwgKGRldlRvb2xzICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU2FmZSkpCiAgICAgICAgICAgICAgICByZXR1cm4gUmVtZW1iZXIoQmxvY2soImJyb3dzZXJfc2VjcmV0X3N0b3JhZ2UiLCAiQ29va2ll44O7U3RvcmFnZeetieOCkuaJseOBhueUu+mdoiOBp+OBr+OBr+OBhOOCieOCr+ODqeOCpuODieeUu+mdouino+aekOOCkuWBnOatouOBl+OBvuOBmeOAgiIpKTs=':
    'ICAgICAgICAgICAgaWYgKHN0b3JhZ2UgfHwgKGRldlRvb2xzICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU2FmZSkpCiAgICAgICAgICAgICAgICByZXR1cm4gUmVtZW1iZXIoQmxvY2soImJyb3dzZXJfc2VjcmV0X3N0b3JhZ2UiLCAiQ29va2ll44O7U3RvcmFnZeetieOCkuaJseOBhueUu+mdouOBp+OBr+OCr+ODqeOCpuODieeUu+mdouino+aekOOCkuWBnOatouOBl+OBvuOBmeOAgiIpKTs=',
    'ICAgICAgICAgICAgaWYgKHN0b3JhZ2UgfHwgKGRldlRvb2xzICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU3RyaWN0KSkKICAgICAgICAgICAgICAgIHJldHVybiBSZW1lbWJlcihCbG9jaygiYnJvd3Nlcl9zZWNyZXRfc3RvcmFnZSIsICJDb29raWXjg7tTdG9yYWdl562J44KS5omx44GG55S76Z2i44Gn44Gv44Kv44Op44Km44OJ55S76Z2i6Kej5p6Q44KS5YGc5q2i44GX44G+44GZ44CCIikpOw==':
    'ICAgICAgICAgICAgaWYgKHN0b3JhZ2UgfHwgKGRldlRvb2xzICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU3RyaWN0KSkKICAgICAgICAgICAgICAgIHJldHVybiBSZW1lbWJlcihCbG9jaygiYnJvd3Nlcl9zZWNyZXRfc3RvcmFnZSIsICJDb29raWXjg7tTdG9yYWdl562J44KS5omx44GG55S76Z2i44Gn44Gv44Kv44Op44Km44OJ55S76Z2i6Kej5p6Q44KS5YGc5q2i44GX44G+44GZ44CCIikpOw==',
    'ICAgICAgICAgICAgaWYgKChmaW5hbmNlICYmIGZpbmFuY2VBY3Rpb24pIHx8IChmaW5hbmNlICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU2FmZSkpCiAgICAgICAgICAgICAgICByZXR1cm4gUmVtZW1iZXIoQmxvY2soImZpbmFuY2lhbF9zZXJ2aWNlIiwgIumHkeiejeODu+iov+WIu+OBruiqjeiov+OBvuOBn+OBr+WPluW8leeUu+mdouOBp+OBryLjgq/jg6njgqbjg4nnlLvpnaLop6PmnpDjgpLlgZzmraLjgZfjgb7jgZnjgIIiKSk7':
    'ICAgICAgICAgICAgaWYgKChmaW5hbmNlICYmIGZpbmFuY2VBY3Rpb24pIHx8IChmaW5hbmNlICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU2FmZSkpCiAgICAgICAgICAgICAgICByZXR1cm4gUmVtZW1iZXIoQmxvY2soImZpbmFuY2lhbF9zZXJ2aWNlIiwgIumHkeiejeODu+iovOWIuOOBruiqjeiovOOBvuOBn+OBr+WPluW8leeUu+mdouOBp+OBr+OCr+ODqeOCpuODieeUu+mdouino+aekOOCkuWBnOatouOBl+OBvuOBmeOAgiIpKTs=',
    'ICAgICAgICAgICAgaWYgKChmaW5hbmNlICYmIGZpbmFuY2VBY3Rpb24pIHx8IChmaW5hbmNlICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU3RyaWN0KSkKICAgICAgICAgICAgICAgIHJldHVybiBSZW1lbWJlcihCbG9jaygiZmluYW5jaWFsX3NlcnZpY2UiLCAi6YeR6J6N44O76Ki85Yi444Gu6KqN6Ki844G+44Gf44Gv5Y+W5byV55S76Z2i44Gn44Gv44Kv44Op44Km44OJ55S76Z2i6Kej5p6Q44KS5YGc5q2i44GX44G+44GZ44CCIikpOw==':
    'ICAgICAgICAgICAgaWYgKChmaW5hbmNlICYmIGZpbmFuY2VBY3Rpb24pIHx8IChmaW5hbmNlICYmIFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU3RyaWN0KSkKICAgICAgICAgICAgICAgIHJldHVybiBSZW1lbWJlcihCbG9jaygiZmluYW5jaWFsX3NlcnZpY2UiLCAi6YeR6J6N44O76Ki85Yi444Gu6KqN6Ki844G+44Gf44Gv5Y+W5byV55S76Z2i44Gn44Gv44Kv44Op44Km44OJ55S76Z2i6Kej5p6Q44KS5YGc5q2i44GX44G+44GZ44CCIikpOw=='
}
for old, new in replacements.items():
    if old not in text:
        raise RuntimeError('Expected damaged Japanese payload was not found')
    text = text.replace(old, new, 1)

# Replace the malformed Standard-mode input-redaction payload as a complete call.
input_old = 'ICAgIHByaXZhdGUgc3RhdGljIGJvb2wgU2hvdWxkUmVkYWN0SW5wdXQoQXV0b21hdGlvbkVsZW1lbnQuQXV0b21hdGlvbkVsZW1lbnRJbmZvcm1hdGlvbiBjdXJyZW50KQogICAgewogICAgICAgIGlmIChjdXJyZW50LklzUGFzc3dvcmQpIHJldHVybiB0cnVlOwogICAgICAgIHJldHVybiBjdXJyZW50LkNvbnRyb2xUeXBlID09IENvbnRyb2xUeXBlLkVkaXQgfHwgY3VycmVudC5Db250cm9sVHlwZSA9PSBDb250cm9sVHlwZS5Db21ib0JveDsKICAgIH0='
input_new = 'ICAgIHByaXZhdGUgYm9vbCBTaG91bGRSZWRhY3RJbnB1dChBdXRvbWF0aW9uRWxlbWVudC5BdXRvbWF0aW9uRWxlbWVudEluZm9ybWF0aW9uIGN1cnJlbnQpCiAgICB7CiAgICAgICAgaWYgKGN1cnJlbnQuSXNQYXNzd29yZCkgcmV0dXJuIHRydWU7CiAgICAgICAgaWYgKGN1cnJlbnQuQ29udHJvbFR5cGUgIT0gQ29udHJvbFR5cGUuRWRpdCAmJiBjdXJyZW50LkNvbnRyb2xUeXBlICE9IENvbnRyb2xUeXBlLkNvbWJvQm94KSByZXR1cm4gZmFsc2U7CiAgICAgICAgaWYgKFByb2ZpbGUgPT0gUHJpdmFjeVBvbGljeVByb2ZpbGUuU3RyaWN0KSByZXR1cm4gdHJ1ZTsKCiAgICAgICAgdmFyIGhpbnQgPSBzdHJpbmcuSm9pbigiICIsIGN1cnJlbnQuTmFtZSA/PyBzdHJpbmcuRW1wdHksIGN1cnJlbnQuQXV0b21hdGlvbklkID8/IHN0cmluZy5FbXB0eSwgY3VycmVudC5DbGFzc05hbWUgPz8gc3RyaW5nLkVtcHR5KTsKICAgICAgICBzdHJpbmdbXSBzZW5zaXRpdmVIaW50cyA9CiAgICAgICAgWwogICAgICAgICAgICAicGFzc3dvcmQiLCAicGFzc3dkIiwgInBhc3Njb2RlIiwgInBpbiIsICJvdHAiLCAidG90cCIsICIyZmEiLCAibWZhIiwKICAgICAgICAgICAgImN2diIsICJjdmMiLCAiY2FyZCBudW1iZXIiLCAiY3JlZGl0IGNhcmQiLCAiYXBpIGtleSIsICJhcGlrZXkiLCAidG9rZW4iLCAic2VjcmV0IiwKICAgICAgICAgICAgImVtYWlsIiwgImUtbWFpbCIsICJwaG9uZSIsICJ0ZWxlcGhvbmUiLCAicG9zdGFsIiwgInppcCBjb2RlIiwgImFjY291bnQgbnVtYmVyIiwgInNzbiIsCiAgICAgICAgICAgICLjg5Hjgrnjg6/jg7zjg4kiLCAi5pqX6Ki8IiwgIuiqjeiovOOCs+ODvOODiSIsICLnorroqo3jgrPjg7zjg4kiLCAi44Kr44O844OJ55Wq5Y+3IiwgIuOCu+OCreODpeODquODhuOCo+OCs+ODvOODiSIsCiAgICAgICAgICAgICJBUEnjgq3jg7wiLCAi44OI44O844Kv44OzIiwgIuenmOWvhiIsICLjg6Hjg7zjg6siLCAi6Zu76KmxIiwgIumDteS+v+eVquWPtyIsICLlj6Pluqfnlarlj7ciCiAgICAgICAgXTsKICAgICAgICByZXR1cm4gc2Vuc2l0aXZlSGludHMuQW55KHRlcm0gPT4gaGludC5Db250YWlucyh0ZXJtLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSk7CiAgICB9'
lines = text.splitlines()
matched = 0
for i, line in enumerate(lines):
    if "replace_exact('src/HelpSys.Desktop/Services/ScreenCaptureService.cs'" in line and input_old in line:
        lines[i] = f"replace_exact('src/HelpSys.Desktop/Services/ScreenCaptureService.cs', '{input_old}', '{input_new}', 1)"
        matched += 1
if matched != 1:
    raise RuntimeError(f'Expected one input-redaction patch call, got {matched}')
text = '\n'.join(lines) + '\n'

# Validate every Base64 old/new payload before the patch can touch source files.
tree = ast.parse(text)
errors = []
for node in ast.walk(tree):
    if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name) or node.func.id != 'replace_exact':
        continue
    if len(node.args) < 3:
        continue
    for label, arg in [('old', node.args[1]), ('new', node.args[2])]:
        if not isinstance(arg, ast.Constant) or not isinstance(arg.value, str):
            errors.append(f'line {node.lineno}: {label} payload is not a string constant')
            continue
        try:
            base64.b64decode(arg.value, validate=True).decode('utf-8')
        except Exception as exc:
            errors.append(f'line {node.lineno}: invalid {label} payload: {exc}')
if errors:
    raise RuntimeError('\n'.join(errors))

path.write_text(text, encoding='utf-8', newline='\n')
print('tiered patch payloads repaired and validated')
