using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HelpSys.Services;

public static class LocalLogService
{
    private static readonly object Gate = new();
    private static TextWriterTraceListener? _listener;
    private static StreamWriter? _writer;
    private static string? _path;
    private static string? _artifactPrefix;
    private static int _artifactSequence;
    private static string _exitReason = "unknown";

    public static string? CurrentPath
    {
        get { lock (Gate) return _path; }
    }

    public static string ExitReason
    {
        get { lock (Gate) return _exitReason; }
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_listener is not null) return;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HelpSys",
                "logs");
            Directory.CreateDirectory(root);

            var stem = $"helpsys-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
            _path = Path.Combine(root, stem + ".log");
            _artifactPrefix = Path.Combine(root, stem);
            _artifactSequence = 0;
            _exitReason = "unknown";

            var writer = new StreamWriter(_path, append: true, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _writer = writer;
            _listener = new TextWriterTraceListener(writer, "HelpSysLocalFile");
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;
            _writer.WriteLine($"{DateTimeOffset.Now:O}\tstartup\tlog={_path}");
            _writer.Flush();
        }
    }

    public static void MarkExitReason(string reason, string? detail = null, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        lock (Gate)
        {
            try
            {
                if (overwrite || string.Equals(_exitReason, "unknown", StringComparison.Ordinal))
                    _exitReason = reason.Trim();
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\texit_reason\t{_exitReason};detail={(detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')}");
                _writer?.Flush();
            }
            catch { }
        }
    }

    public static void MarkExitReasonIfUnset(string reason, string? detail = null)
        => MarkExitReason(reason, detail, overwrite: false);

    public static void WriteException(string category, Exception exception)
    {
        if (exception is null) return;
        Write(category, $"{exception.GetType().FullName}: {exception.Message} | {exception.StackTrace} | inner={exception.InnerException?.GetType().FullName}: {exception.InnerException?.Message}");
    }

    public static void Write(string category, string? message)
    {
        lock (Gate)
        {
            try
            {
                var normalized = (message ?? string.Empty)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\t{category}\t{normalized}");
                _writer?.Flush();
            }
            catch
            {
                // Logging must never block guidance.
            }
        }
    }

    public static string? SaveDataUriImage(string kind, string? dataUri)
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_artifactPrefix) || string.IsNullOrWhiteSpace(dataUri)) return null;
                var comma = dataUri.IndexOf(',');
                if (comma <= 0 || !dataUri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;

                var header = dataUri[..comma];
                var ext = header.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
                var bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
                var path = NextArtifactPath(kind, ext);
                File.WriteAllBytes(path, bytes);
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\tartifact\t{kind}={path};bytes={bytes.Length}");
                _writer?.Flush();
                return path;
            }
            catch (Exception ex)
            {
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\tartifact_error\t{kind}:{ex.GetType().Name}");
                _writer?.Flush();
                return null;
            }
        }
    }

    public static string? SaveJson(string kind, object? value)
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_artifactPrefix)) return null;
                var path = NextArtifactPath(kind, "json");
                var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(false));
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\tartifact\t{kind}={path};chars={json.Length}");
                _writer?.Flush();
                return path;
            }
            catch (Exception ex)
            {
                _writer?.WriteLine($"{DateTimeOffset.Now:O}\tartifact_error\t{kind}:{ex.GetType().Name}");
                _writer?.Flush();
                return null;
            }
        }
    }

    private static string NextArtifactPath(string kind, string extension)
    {
        var safeKind = string.Concat((kind ?? "artifact").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var seq = ++_artifactSequence;
        return $"{_artifactPrefix}-{seq:D4}-{safeKind}.{extension}";
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                if (_listener is not null)
                {
                    _writer?.WriteLine($"{DateTimeOffset.Now:O}\tshutdown\treason={_exitReason}");
                    _writer?.Flush();
                    Trace.Listeners.Remove(_listener);
                    _listener.Flush();
                    _listener.Close();
                }
            }
            catch { }
            finally
            {
                _listener = null;
                _writer = null;
                _artifactPrefix = null;
            }
        }
    }
}
