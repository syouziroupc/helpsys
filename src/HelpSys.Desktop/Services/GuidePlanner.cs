using HelpSys.Models;

namespace HelpSys.Services;

public sealed class GuidePlanner
{
    public GuidePlan CreateFirstStep(string request)
    {
        var text = request.Trim();

        if (ContainsAny(text, "youtube", "ユーチューブ", "動画を見", "ブラウザ", "インターネット"))
        {
            return new GuidePlan(
                "ブラウザを開きます。矢印の場所を左クリックしてください。",
                ["Google Chrome", "Chrome", "Microsoft Edge", "Edge", "Firefox"]);
        }

        if (ContainsAny(text, "wifi", "wi-fi", "ワイファイ", "ネットにつな", "ネット接続"))
        {
            return new GuidePlan(
                "ネットワーク設定を開きます。矢印の場所を左クリックしてください。",
                ["クイック設定", "Quick Settings", "ネットワーク", "Network", "Wi-Fi"]);
        }

        if (ContainsAny(text, "音量", "ボリューム", "音を大き", "音を小さ"))
        {
            return new GuidePlan(
                "音量の設定を開きます。矢印の場所を左クリックしてください。",
                ["スピーカー", "Speakers", "音量", "Volume", "Quick Settings", "クイック設定"]);
        }

        var terms = text
            .Split([' ', '　', '、', '。', ',', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 2)
            .Take(6)
            .ToArray();

        return new GuidePlan(
            "画面上から関係する項目を探しました。矢印の場所を確認してください。",
            terms.Length > 0 ? terms : [text]);
    }

    private static bool ContainsAny(string source, params string[] terms) =>
        terms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
}
