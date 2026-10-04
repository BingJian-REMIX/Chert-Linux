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
