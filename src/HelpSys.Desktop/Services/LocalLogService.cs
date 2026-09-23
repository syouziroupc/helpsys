using System.Diagnostics;
using System.Text;

namespace HelpSys.Services;

public static class LocalLogService
{
    private static readonly object Gate = new();
    private static TextWriterTraceListener? _listener;
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
            _listener = new TextWriterTraceListener(writer, "HelpSysLocalFile");
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;
            Trace.WriteLine($"{DateTimeOffset.Now:O}\tstartup\tlog={_path}");
        }
    }

    public static void Write(string category, string? message)
    {
        try
        {
            var normalized = (message ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            Trace.WriteLine($"{DateTimeOffset.Now:O}\t{category}\t{normalized}");
            Trace.Flush();
        }
        catch
        {
            // Logging must never block guidance.
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
                    Trace.WriteLine($"{DateTimeOffset.Now:O}\tshutdown");
                    Trace.Flush();
                    Trace.Listeners.Remove(_listener);
                    _listener.Flush();
                    _listener.Close();
                }
            }
            catch { }
            finally
            {
                _listener = null;
            }
        }
    }
}
