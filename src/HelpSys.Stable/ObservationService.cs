using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace HelpSys.Stable;

internal sealed class ObservationService
{
    private const int MaxControls = 240;
    private const int MaxVisitedNodes = 800;
    private const int MaxImageDimension = 1600;
    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromSeconds(2.5);
    private readonly SemaphoreSlim _uiaGate = new(1, 1);

    public async Task<ScreenObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            throw new PlannerException("操作中のウィンドウを取得できませんでした。");

        NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
        var pid = checked((int)rawPid);
        if (pid <= 0 || pid == Environment.ProcessId)
            throw new PlannerException("HelpSys以外の操作対象ウィンドウを前面に出してください。");

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 40 || rect.Height < 40)
            throw new PlannerException("操作中のウィンドウ領域を取得できませんでした。");

        var title = NativeMethods.GetTitle(hwnd);
        var processName = GetProcessName(pid);

        UiScanResult scan;
        try
        {
            scan = await RunUiScanAsync(hwnd, processName, rect, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new PlannerException("Windowsの画面構造取得が2.5秒以内に完了しませんでした。画像送信はしていません。", ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not PlannerException)
        {
            throw new PlannerException("Windowsの画面構造取得に失敗したため、画像送信はしていません。", ex);
        }

        SafetyGate.EnsureSafeToCapture(processName, title, scan.Controls);
        EnsureSameWindow(hwnd, pid);

        var localImage = CaptureWindow(rect);
        EnsureSameWindow(hwnd, pid);

        var outboundImage = RedactOutboundImage(
            localImage,
            rect,
            hwnd,
            pid,
            scan.SensitiveBounds);
        var safeControls = scan.Controls.Select(SanitizeControl).ToList();

        return new ScreenObservation(
            hwnd,
            pid,
            processName,
            SanitizeText(title),
            scan.BrowserDomain,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            outboundImage,
            localImage,
            safeControls);
    }

    public bool IsStillCurrent(ScreenObservation observation)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != observation.WindowHandle || hwnd == nint.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
        return checked((int)rawPid) == observation.ProcessId;
    }

    public async Task<bool> IsPlanStillApplicableAsync(
        ScreenObservation observation,
        PlanResult plan,
        CancellationToken cancellationToken)
    {
        if (!IsStillCurrent(observation)) return false;
        if (plan.Status != "target") return true;

        if (string.IsNullOrWhiteSpace(plan.TargetId))
        {
            if (!NativeMethods.GetWindowRect(observation.WindowHandle, out var visualRect) ||
                visualRect.Width != observation.Width || visualRect.Height != observation.Height)
                return false;

            try
            {
                var currentImage = await Task.Run(() => CaptureWindow(visualRect), cancellationToken);
                if (!IsStillCurrent(observation)) return false;
                return IsVisualTargetStillCurrent(observation.LocalComparisonImageDataUri, currentImage, plan);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        var original = observation.Controls.FirstOrDefault(x => x.Id == plan.TargetId);
        if (original is null || !original.Enabled) return false;
        if (!NativeMethods.GetWindowRect(observation.WindowHandle, out var rect) || rect.Width < 40 || rect.Height < 40)
            return false;

        try
        {
            var scan = await RunUiScanAsync(
                observation.WindowHandle,
                observation.ProcessName,
                rect,
                cancellationToken);
            if (!IsStillCurrent(observation)) return false;
            return scan.Controls.Any(candidate => SameControlIdentity(original, candidate));
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<UiScanResult> RunUiScanAsync(
        nint hwnd,
        string processName,
        NativeMethods.Rect windowRect,
        CancellationToken cancellationToken)
    {
        if (!await _uiaGate.WaitAsync(0, cancellationToken))
            throw new PlannerException("Windows画面構造取得がすでに実行中のため、二重実行しません。");

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "HelpSys-Stable-UiProbe-" + Guid.NewGuid().ToString("N") + ".json");
        Process? probe = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new PlannerException("Windows画面構造取得プロセスを開始できませんでした。");

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(UiProbeHost.Switch);
            startInfo.ArgumentList.Add(hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(processName);
            startInfo.ArgumentList.Add(windowRect.Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(windowRect.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(windowRect.Right.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(windowRect.Bottom.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(outputPath);

            probe = Process.Start(startInfo)
                ?? throw new PlannerException("Windows画面構造取得プロセスを開始できませんでした。");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(UiAutomationTimeout);
            try
            {
                await probe.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKillProbe(probe);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKillProbe(probe);
                throw new TimeoutException("UI Automation probe timed out.");
            }

            if (probe.ExitCode != 0 || !File.Exists(outputPath))
                throw new PlannerException("Windowsの画面構造取得プロセスが正常に完了しませんでした。");

            var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var result = System.Text.Json.JsonSerializer.Deserialize<UiScanResult>(json);
            return result ?? throw new PlannerException("Windowsの画面構造取得結果が空でした。");
        }
        finally
        {
            if (probe is not null && !probe.HasExited) TryKillProbe(probe);
            probe?.Dispose();
            try { File.Delete(outputPath); } catch { }
            try { File.Delete(outputPath + ".error"); } catch { }
            _uiaGate.Release();
        }
    }

    private static void TryKillProbe(Process probe)
    {
        try
        {
            if (!probe.HasExited) probe.Kill(entireProcessTree: true);
        }
        catch { }
    }

    internal static UiScanResult ScanUi(nint hwnd, string processName, NativeMethods.Rect windowRect)
    {
        var root = AutomationElement.FromHandle(hwnd)
                   ?? throw new InvalidOperationException("UI Automation root unavailable.");

        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();

        // Enumerate every direct child of the target window. Starting from only the first
        // child drops all of its root-level siblings (common in WinForms, Office and browsers).
        var rootChild = walker.GetFirstChild(root);
        var rootSiblings = 0;
        while (rootChild is not null && queue.Count < MaxVisitedNodes && rootSiblings < MaxVisitedNodes)
        {
            queue.Enqueue(rootChild);
            rootSiblings++;
            rootChild = walker.GetNextSibling(rootChild);
        }

        var candidates = new List<(UiControlSnapshot Control, int Priority, int Order)>(MaxVisitedNodes);
        var sensitiveBounds = new List<PixelRect>();
        string? browserDomain = null;
        var visited = 0;

        while (queue.Count > 0 && visited < MaxVisitedNodes)
        {
            var item = queue.Dequeue();
            visited++;

            try
            {
                var child = walker.GetFirstChild(item);
                var siblings = 0;
                while (child is not null && queue.Count < MaxVisitedNodes && siblings < MaxVisitedNodes)
                {
                    queue.Enqueue(child);
                    siblings++;
                    child = walker.GetNextSibling(child);
                }

                var current = item.Current;
                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) continue;

                var left = Math.Max(bounds.Left, windowRect.Left);
                var top = Math.Max(bounds.Top, windowRect.Top);
                var right = Math.Min(bounds.Right, windowRect.Right);
                var bottom = Math.Min(bounds.Bottom, windowRect.Bottom);
                if (right <= left || bottom <= top) continue;

                var name = Trim(current.Name, 180);
                var automationId = Trim(current.AutomationId, 120);
                var className = Trim(current.ClassName, 120);
                var type = Trim(current.ControlType?.ProgrammaticName, 80);

                var snapshot = new UiControlSnapshot(
                    "",
                    name,
                    automationId,
                    className,
                    type,
                    current.IsEnabled,
                    current.HasKeyboardFocus,
                    current.IsKeyboardFocusable,
                    current.IsPassword,
                    Normalize(left - windowRect.Left, windowRect.Width),
                    Normalize(top - windowRect.Top, windowRect.Height),
                    Normalize(right - left, windowRect.Width),
                    Normalize(bottom - top, windowRect.Height));

                candidates.Add((snapshot, ControlPriority(snapshot), visited));

                if (current.IsPassword || ContainsSensitiveText(name) || HasSensitiveValue(item, type))
                    sensitiveBounds.Add(new PixelRect(
                        (int)Math.Floor(left),
                        (int)Math.Floor(top),
                        (int)Math.Ceiling(right),
                        (int)Math.Ceiling(bottom)));

                if (browserDomain is null && IsBrowser(processName) && LooksLikeAddressBar(name, type))
                    browserDomain = TryReadDomain(item);
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        var controls = candidates
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Order)
            .Take(MaxControls)
            .Select((x, index) => x.Control with { Id = $"u{index + 1}" })
            .ToList();

        return new UiScanResult(controls, browserDomain, sensitiveBounds);
    }

    private static int ControlPriority(UiControlSnapshot control)
    {
        var score = 0;
        if (control.Password) score += 10_000;
        if (control.Focused) score += 5_000;
        if (control.KeyboardFocusable) score += 600;
        if (control.Enabled) score += 100;
        if (!string.IsNullOrWhiteSpace(control.AutomationId)) score += 450;
        if (!string.IsNullOrWhiteSpace(control.Name)) score += 300;

        score += control.ControlType switch
        {
            "ControlType.Edit" => 1_400,
            "ControlType.Button" => 1_300,
            "ControlType.ComboBox" => 1_250,
            "ControlType.CheckBox" or "ControlType.RadioButton" => 1_200,
            "ControlType.MenuItem" or "ControlType.Hyperlink" => 1_150,
            "ControlType.ListItem" or "ControlType.TreeItem" or "ControlType.DataItem" => 1_100,
            "ControlType.TabItem" => 1_000,
            _ => 0
        };
        return score;
    }

    private static string CaptureWindow(NativeMethods.Rect rect)
    {
        using var source = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);

        Bitmap output = source;
        var scale = Math.Min(1d, MaxImageDimension / (double)Math.Max(source.Width, source.Height));
        if (scale < 1d)
        {
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, width, height);
        }

        try
        {
            using var stream = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            output.Save(stream, encoder, parameters);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
        }
        finally
        {
            if (!ReferenceEquals(output, source)) output.Dispose();
        }
    }

    private static readonly Regex EmailRegex = new(
        @"(?<![\w.+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex JapanesePhoneRegex = new(
        @"(?<!\d)0\d{1,4}[-‐‑–—ー]?\d{1,4}[-‐‑–—ー]?\d{3,4}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PostalRegex = new(
        @"〒?\s*\d{3}[-‐‑–—ー]?\d{4}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretValueRegex = new(
        @"(?i)(?:bearer\s+[A-Za-z0-9._~+/=-]{12,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|(?:api[_ -]?key|token|secret|password|パスワード|秘密鍵|apiキー)\s*[:=]\s*\S{4,}|\b(?:\d[ -]?){13,19}\b)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool ContainsSensitiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return EmailRegex.IsMatch(value) ||
               JapanesePhoneRegex.IsMatch(value) ||
               PostalRegex.IsMatch(value) ||
               SecretValueRegex.IsMatch(value);
    }

    private static bool HasSensitiveValue(AutomationElement element, string controlType)
    {
        if (!controlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
            !controlType.Contains("Document", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                pattern is not ValuePattern valuePattern)
                return false;
            return ContainsSensitiveText(valuePattern.Current.Value);
        }
        catch
        {
            return false;
        }
    }

    internal static string SanitizeText(string? value)
    {
        var text = value ?? string.Empty;
        text = EmailRegex.Replace(text, "<email>");
        text = JapanesePhoneRegex.Replace(text, "<phone>");
        text = PostalRegex.Replace(text, "<postal-code>");
        text = SecretValueRegex.Replace(text, "<redacted-secret>");
        return text;
    }

    private static UiControlSnapshot SanitizeControl(UiControlSnapshot control)
        => control with
        {
            Name = SanitizeText(control.Name),
            AutomationId = SanitizeText(control.AutomationId)
        };

    internal static string RedactOutboundImage(
        string sourceDataUri,
        NativeMethods.Rect windowRect,
        nint targetWindow,
        int targetPid,
        IReadOnlyList<PixelRect> sensitiveBounds)
    {
        using var bitmap = DecodeDataImage(sourceDataUri);
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.Black);

        foreach (var sensitive in sensitiveBounds)
        {
            var clipped = Intersect(
                sensitive,
                new PixelRect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom));
            if (clipped is null) continue;
            FillAbsoluteRect(graphics, brush, clipped.Value, windowRect, bitmap);
        }

        var cursor = NativeMethods.GetWindow(targetWindow, NativeMethods.GwHwndPrev);
        for (var i = 0; i < 96 && cursor != nint.Zero; i++)
        {
            try
            {
                if (NativeMethods.IsWindowVisible(cursor) &&
                    NativeMethods.GetWindowRect(cursor, out var otherRect))
                {
                    NativeMethods.GetWindowThreadProcessId(cursor, out var rawPid);
                    var otherPid = checked((int)rawPid);
                    if (otherPid > 0 && otherPid != targetPid)
                    {
                        var clipped = Intersect(
                            new PixelRect(otherRect.Left, otherRect.Top, otherRect.Right, otherRect.Bottom),
                            new PixelRect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom));
                        if (clipped is not null)
                            FillAbsoluteRect(graphics, brush, clipped.Value, windowRect, bitmap);
                    }
                }
            }
            catch
            {
                // Redaction is conservative but must not crash capture if a transient window disappears.
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndPrev);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        bitmap.Save(stream, encoder, parameters);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static void FillAbsoluteRect(
        Graphics graphics,
        Brush brush,
        PixelRect rect,
        NativeMethods.Rect windowRect,
        Bitmap bitmap)
    {
        var scaleX = bitmap.Width / (double)Math.Max(1, windowRect.Width);
        var scaleY = bitmap.Height / (double)Math.Max(1, windowRect.Height);
        var x = (int)Math.Floor((rect.Left - windowRect.Left) * scaleX);
        var y = (int)Math.Floor((rect.Top - windowRect.Top) * scaleY);
        var width = (int)Math.Ceiling((rect.Right - rect.Left) * scaleX);
        var height = (int)Math.Ceiling((rect.Bottom - rect.Top) * scaleY);
        x = Math.Clamp(x, 0, Math.Max(0, bitmap.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, bitmap.Height - 1));
        width = Math.Clamp(width, 1, bitmap.Width - x);
        height = Math.Clamp(height, 1, bitmap.Height - y);
        graphics.FillRectangle(brush, x, y, width, height);
    }

    private static PixelRect? Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right, bottom) : null;
    }

    internal static bool IsVisualTargetStillCurrent(string beforeDataUri, string afterDataUri, PlanResult plan)
    {
        try
        {
            using var before = DecodeDataImage(beforeDataUri);
            using var after = DecodeDataImage(afterDataUri);
            if (before.Width != after.Width || before.Height != after.Height) return false;

            Rectangle region;
            if (plan.Width > 0 && plan.Height > 0)
            {
                var x = (int)Math.Floor(plan.X / 1000d * before.Width);
                var y = (int)Math.Floor(plan.Y / 1000d * before.Height);
                var width = Math.Max(1, (int)Math.Ceiling(plan.Width / 1000d * before.Width));
                var height = Math.Max(1, (int)Math.Ceiling(plan.Height / 1000d * before.Height));
                var marginX = Math.Max(8, width / 2);
                var marginY = Math.Max(8, height / 2);
                var left = Math.Clamp(x - marginX, 0, before.Width - 1);
                var top = Math.Clamp(y - marginY, 0, before.Height - 1);
                var right = Math.Clamp(x + width + marginX, left + 1, before.Width);
                var bottom = Math.Clamp(y + height + marginY, top + 1, before.Height);
                region = Rectangle.FromLTRB(left, top, right, bottom);
            }
            else
            {
                region = new Rectangle(0, 0, before.Width, before.Height);
            }

            var samplesX = Math.Min(40, Math.Max(4, region.Width));
            var samplesY = Math.Min(30, Math.Max(4, region.Height));
            var changed = 0;
            var total = 0;
            var totalDifference = 0d;

            for (var sy = 0; sy < samplesY; sy++)
            {
                var py = region.Top + Math.Min(region.Height - 1, (int)((sy + 0.5) * region.Height / samplesY));
                for (var sx = 0; sx < samplesX; sx++)
                {
                    var px = region.Left + Math.Min(region.Width - 1, (int)((sx + 0.5) * region.Width / samplesX));
                    var a = before.GetPixel(px, py);
                    var b = after.GetPixel(px, py);
                    var difference = (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3d;
                    totalDifference += difference;
                    total++;
                    if (difference >= 38d) changed++;
                }
            }

            if (total == 0) return false;
            var changedRatio = changed / (double)total;
            var averageDifference = totalDifference / total;
            return changedRatio <= 0.08 && averageDifference <= 18d;
        }
        catch
        {
            return false;
        }
    }

    private static Bitmap DecodeDataImage(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma < 0 || comma == dataUri.Length - 1)
            throw new FormatException("Invalid image data URI.");
        var bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    internal static bool SameControlIdentity(UiControlSnapshot a, UiControlSnapshot b)
    {
        if (!a.Enabled || !b.Enabled) return false;
        if (!a.ControlType.Equals(b.ControlType, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrWhiteSpace(a.AutomationId) && !string.IsNullOrWhiteSpace(b.AutomationId) &&
            !a.AutomationId.Equals(b.AutomationId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(b.Name) &&
            !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        // Stable AutomationId/name is not enough: a responsive layout can move the same
        // control while Gemini is answering. The overlay still uses the original snapshot,
        // so reject meaningful geometry drift rather than highlighting a stale position.
        return Math.Abs(a.X - b.X) <= 45 &&
               Math.Abs(a.Y - b.Y) <= 45 &&
               Math.Abs(a.Width - b.Width) <= 70 &&
               Math.Abs(a.Height - b.Height) <= 70;
    }

    private static void EnsureSameWindow(nint expectedHwnd, int expectedPid)
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current != expectedHwnd)
            throw new ObservationChangedException("画面取得中に前面ウィンドウが切り替わりました。");

        NativeMethods.GetWindowThreadProcessId(current, out var rawPid);
        if (checked((int)rawPid) != expectedPid)
            throw new ObservationChangedException("画面取得中に操作対象アプリが切り替わりました。");
    }

    private static string GetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return string.Empty; }
    }

    private static double Normalize(double value, double total)
        => total <= 0 ? 0 : Math.Clamp(value / total * 1000d, 0d, 1000d);

    private static string Trim(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    private static bool IsBrowser(string processName)
        => processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("brave", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeAddressBar(string name, string controlType)
    {
        if (!controlType.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("アドレス", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadDomain(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
                return null;
            return NormalizeBrowserDomain(valuePattern.Current.Value);
        }
        catch
        {
            return null;
        }
    }

    internal static string? NormalizeBrowserDomain(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var hasScheme = raw.Contains("://", StringComparison.Ordinal);
        if (!hasScheme)
        {
            var authority = raw.Split('/', '?', '#')[0];
            var hostCandidate = authority.Split(':')[0];
            var isIp = System.Net.IPAddress.TryParse(hostCandidate, out _);
            var looksLikeHost = hostCandidate.Contains('.', StringComparison.Ordinal) ||
                                hostCandidate.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                isIp;
            if (!looksLikeHost) return null;
            raw = "https://" + raw;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
               !string.IsNullOrWhiteSpace(uri.IdnHost)
            ? uri.IdnHost.ToLowerInvariant()
            : null;
    }

    internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
    internal sealed record UiScanResult(
        IReadOnlyList<UiControlSnapshot> Controls,
        string? BrowserDomain,
        IReadOnlyList<PixelRect> SensitiveBounds);
}
