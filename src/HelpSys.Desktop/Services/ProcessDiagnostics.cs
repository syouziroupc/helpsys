using System.Reflection;
using System.Text;
using System.Text.Json;

namespace HelpSys.Services;

public static class ProcessDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string SessionStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HelpSys",
        "Logs");
    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        $"helpsys-process-{SessionStamp}-{Environment.ProcessId}.jsonl");

    private static int _initialized;
    private static string _exitReason = "unknown";

    public static string ExitReason
    {
        get
        {
            lock (Gate) return _exitReason;
        }
    }

    public static string CurrentLogPath => LogPath;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Log("startup", new
        {
            processId = Environment.ProcessId,
            executable = Environment.ProcessPath,
            os = Environment.OSVersion.VersionString,
            framework = Environment.Version.ToString(),
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            buildId = GetBuildId(),
            logPath = LogPath
        });
    }

    public static void MarkExitReasonIfUnset(string reason, string? detail = null)
        => MarkExitReason(reason, detail, overwrite: false);

    public static void MarkExitReason(string reason, string? detail = null, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;

        string storedReason;
        lock (Gate)
        {
            if (overwrite || string.Equals(_exitReason, "unknown", StringComparison.Ordinal))
                _exitReason = reason.Trim();
            storedReason = _exitReason;
        }

        Log("exit_reason", new
        {
            requestedReason = reason,
            storedReason,
            detail
        });
    }

    public static void LogException(string eventName, Exception exception)
    {
        if (exception is null) return;

        Log(eventName, new
        {
            exceptionType = exception.GetType().FullName,
            exception.Message,
            exception.StackTrace,
            innerType = exception.InnerException?.GetType().FullName,
            innerMessage = exception.InnerException?.Message
        });
    }

    public static void Log(string eventName, object? data = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var record = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                eventName,
                processId = Environment.ProcessId,
                exitReason = ExitReason,
                data
            });

            lock (Gate)
            {
                File.AppendAllText(LogPath, record + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never become a new crash source.
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        MarkExitReason("appdomain_unhandled_exception", overwrite: true);
        if (e.ExceptionObject is Exception exception)
            LogException("appdomain_unhandled_exception", exception);
        else
            Log("appdomain_unhandled_exception", new { value = e.ExceptionObject?.ToString(), e.IsTerminating });
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("unobserved_task_exception", e.Exception);
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        Log("process_exit", new { exitReason = ExitReason });
    }

    private static string GetBuildId()
    {
        var metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals("HelpSysBuildId", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(metadata?.Value) ? "dev" : metadata.Value.Trim();
    }
}
