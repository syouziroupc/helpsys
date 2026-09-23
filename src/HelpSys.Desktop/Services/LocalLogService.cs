using System.IO;
using System.Diagnostics;
using System.Text;

namespace HelpSys.Services;

public static class LocalLogService
{
    private static readonly object Gate = new();
    private static TextWriterTraceListener? _listener;
    private static StreamWriter? _writer;
    private static string? _path;

    public static string? CurrentPath
    {
        get { lock (Gate) return _path; }
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

            _path = Path.Combine(
                root,
                $"helpsys-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

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

    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                if (_listener is not null)
                {
                    _writer?.WriteLine($"{DateTimeOffset.Now:O}\tshutdown");
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
            }
        }
    }
}
