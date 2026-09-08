using System.Globalization;
using System.Speech.Synthesis;

namespace HelpSys.Services;

public sealed class SpeechOutputService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();

    public SpeechOutputService()
    {
        _synthesizer.Volume = 100;
        _synthesizer.Rate = -1;
        TrySelectJapaneseVoice();
    }

    public void Speak(string? text)
    {
        var value = Prepare(text);
        if (string.IsNullOrWhiteSpace(value)) return;
        lock (_gate)
        {
            try
            {
                _synthesizer.SpeakAsyncCancelAll();
                _synthesizer.SpeakAsync(value);
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _synthesizer.SpeakAsyncCancelAll(); } catch { }
        }
    }

    private void TrySelectJapaneseVoice()
    {
        try
        {
            var japanese = _synthesizer.GetInstalledVoices()
                .Where(x => x.Enabled)
                .Select(x => x.VoiceInfo)
                .FirstOrDefault(x => x.Culture.Name.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) || x.Culture.TwoLetterISOLanguageName == "ja");
            if (japanese is not null) _synthesizer.SelectVoice(japanese.Name);
        }
        catch { }
    }

    private static string Prepare(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim()
            .Replace("Ctrl", "コントロール", StringComparison.OrdinalIgnoreCase)
            .Replace("Alt", "オルト", StringComparison.OrdinalIgnoreCase)
            .Replace("Enter", "エンター", StringComparison.OrdinalIgnoreCase)
            .Replace("Windows", "ウィンドウズ", StringComparison.OrdinalIgnoreCase)
            .Replace("+", "、を押したまま、", StringComparison.Ordinal)
            .Replace("URL", "ホームページのアドレス", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Stop();
        _synthesizer.Dispose();
    }
}
