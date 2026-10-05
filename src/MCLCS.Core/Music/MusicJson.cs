using System.Text.Json;

namespace MCLCS.Core.Music;

/// <summary>
/// 在线音源 JSON 读取辅助。
///
/// <para><b>为什么需要它</b>：第三方音乐接口的字段<b>类型不稳定</b> ——
/// 同一个 <c>id</c> 字段在这个端点是数字、在另一个端点是字符串；
/// 歌手有时是 <c>["周杰伦"]</c>，有时是 <c>[{"name":"周杰伦"}]</c>。
/// 直接 <c>GetInt32()</c> 会在接口微调的瞬间全线崩溃。这里统一用
/// 「能读就读、读不到返回 null」的策略，让上层按缺省值处理。</para>
///
/// <para>所有方法都不抛异常：调用方不需要在解析层再套 try/catch。</para>
/// </summary>
internal static class MusicJson
{
    /// <summary>读字符串。数字也会被转成它的原始文本（id 常被写成数字）。</summary>
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    /// <summary>读整数；读不到或不是数字返回 0。</summary>
    public static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        if (!e.TryGetProperty(name, out var v)) return 0;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return 0;
    }

    /// <summary>读小数；读不到返回 0。</summary>
    public static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        if (!e.TryGetProperty(name, out var v)) return 0;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }

    /// <summary>
    /// 按候选字段名依次取值，取到第一个非空字符串即返回。
    /// <para>用于「同一含义在不同厂家叫不同名字」的场景：曲目标识在网易云叫 <c>id</c>、
    /// 在 QQ 音乐叫 <c>mid</c>、在酷狗叫 <c>hash</c>。写死某一个等于放弃另外几家。</para>
    /// </summary>
    public static string? Any(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Str(e, n);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// 深度优先找出第一个「像曲目/歌单列表」的数组：元素必须是对象，
    /// 且同时含有任一标识字段与任一名称字段。
    /// <para>为什么靠形状而不是靠路径：各厂家把结果放在不同层级
    /// （<c>result.songs</c> / <c>data.lists</c> / 直接就是数组），
    /// 但「对象数组 + 有 id + 有名字」这个形状是共通的。</para>
    /// </summary>
    public static JsonElement? FindRecordArray(JsonElement root, string[] idNames, string[] nameNames)
    {
        var found = SearchRecordArray(root, idNames, nameNames, depth: 0);
        return found;
    }

    private static JsonElement? SearchRecordArray(JsonElement e, string[] idNames, string[] nameNames, int depth)
    {
        if (depth > 6) return null;

        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (Any(item, idNames) is not null && Any(item, nameNames) is not null)
                        return e;      // ★ 返回整个数组，而不是命中的那一个元素
                }
                // 这个数组不像记录列表：继续往下钻它的元素
                foreach (var item in e.EnumerateArray())
                {
                    var r = SearchRecordArray(item, idNames, nameNames, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    var r = SearchRecordArray(prop.Value, idNames, nameNames, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 深度优先找第一个「像播放地址」的字符串。
    /// <para>优先取字段名就是 url/playUrl 的；没有再退而求其次找任何 http 链接 ——
    /// 各家把直链放在 <c>url</c> / <c>play_url</c> / <c>data.url</c> 里，形状不统一。</para>
    /// </summary>
    public static string? FindUrl(JsonElement e)
    {
        var named = FindNamedUrl(e, new[] { "url", "playUrl", "play_url", "playurl", "src", "link", "fileUrl" }, 0);
        if (named is not null) return named;

        return FindAnyHttp(e, 0);
    }

    private static string? FindNamedUrl(JsonElement e, string[] names, int depth)
    {
        if (depth > 6) return null;

        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in e.EnumerateObject())
            {
                if (names.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (IsHttpUrl(s)) return s;
                }
                var r = FindNamedUrl(prop.Value, names, depth + 1);
                if (r is not null) return r;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var r = FindNamedUrl(item, names, depth + 1);
                if (r is not null) return r;
            }
        }
        return null;
    }

    private static string? FindAnyHttp(JsonElement e, int depth)
    {
        if (depth > 6) return null;

        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString();
                return IsHttpUrl(s) ? s : null;

            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    var r = FindAnyHttp(prop.Value, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    var r = FindAnyHttp(item, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>只认 http(s) 开头的非空串；data URL 与相对路径不算播放地址。</summary>
    private static bool IsHttpUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>深度优先按字段名找字符串值（用于歌词、二维码图片这类「名字确定、层级不确定」的字段）。</summary>
    public static string? FindString(JsonElement e, string[] names) => FindStringCore(e, names, 0);

    private static string? FindStringCore(JsonElement e, string[] names, int depth)
    {
        if (depth > 6) return null;

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    if (names.Contains(prop.Name))
                    {
                        var v = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    var r = FindStringCore(prop.Value, names, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    var r = FindStringCore(item, names, depth + 1);
                    if (r is not null) return r;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 读名字数组（歌手列表）。元素既可以是字符串，也可以是含 <c>name</c> 的对象。
    /// 多个用「 / 」连接；一个都没有时返回 null（区别于「有但为空」）。
    /// </summary>
    public static string? Names(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;

        var names = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            var n = item.ValueKind == JsonValueKind.String ? item.GetString() : Str(item, "name");
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
        }
        return names.Count == 0 ? null : string.Join(" / ", names);
    }
}
