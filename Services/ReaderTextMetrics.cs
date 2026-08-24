using System.Text;

namespace BonLivre.Services;

/// <summary>
/// 阅读器口径的文本度量与私有占位标记。
///
/// 「可见字数」必须在后端与前端保持同一口径：章内进度（chapterPos）、seekMap、全书进度条、
/// 书内搜索定位都按它计算，任何一处偏移都会让进度条与实际位置错位。
/// 这里集中定义，供 <see cref="LocalBookService"/>（EPUB 正文）与
/// <see cref="TxtChapterSplitter"/>（TXT 切分）共用，避免两套实现各自漂移。
/// </summary>
internal static class ReaderTextMetrics
{
    // 行内格式标签 -> 不破坏段落切分的占位标记。阅读器据此把段内片段包成 <em>/<strong>，
    // 块级语义（blockquote/li）同理标记后还原。用成对 U+E000 区私有字符，不会出现在正文中，
    // 也不在 JSON/HTML 需转义的范围内。
    internal const char InlineEmStart = '';
    internal const char InlineEmEnd = '';
    internal const char InlineStrongStart = '';
    internal const char InlineStrongEnd = '';
    internal const char BlockQuoteStart = '';
    internal const char BlockQuoteEnd = '';
    internal const char ListItemStart = '';
    internal const char ListItemEnd = '';

    private static readonly char[] PrivateMarkers =
    [
        InlineEmStart, InlineEmEnd, InlineStrongStart, InlineStrongEnd,
        BlockQuoteStart, BlockQuoteEnd, ListItemStart, ListItemEnd,
    ];

    internal static bool IsPrivateMarker(char ch) =>
        ch is InlineEmStart or InlineEmEnd or InlineStrongStart or InlineStrongEnd
            or BlockQuoteStart or BlockQuoteEnd or ListItemStart or ListItemEnd;

    /// <summary>剥离私有占位标记（内联强调 + 块级语义），供搜索快照等只读纯文本场景使用。</summary>
    internal static string StripInlineMarkers(string s)
    {
        if (s.IndexOfAny(PrivateMarkers) < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (IsPrivateMarker(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>统计阅读器可见的字符数：剔除私有占位标记（内联强调 + 块级语义）。</summary>
    internal static int CountVisibleChars(ReadOnlySpan<char> line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (IsPrivateMarker(ch)) continue;
            count++;
        }
        return count;
    }

    /// <summary>按阅读器的段落规则计算章内进度范围：trim、忽略空行，每段长度加一。
    /// 图片行 / &lt;hr&gt; 也计入（与 Reader.svelte splitContent 一致），否则 seekMap
    /// 与搜索定位会相对章末偏移。</summary>
    internal static int CalculateContentLength(ReadOnlySpan<char> content)
    {
        var total = 0;
        while (true)
        {
            int newline = content.IndexOf('\n');
            var line = (newline >= 0 ? content[..newline] : content).Trim();
            // 与前端 visibleLength 一致：剥离私有占位标记后的可见字数 +1（换行）。
            // 图片 / hr 标记行本身也是段落（有 endPos），必须计入。
            if (!line.IsEmpty)
            {
                total = checked(total + CountVisibleChars(line) + 1);
            }

            if (newline < 0) return total;
            content = content[(newline + 1)..];
        }
    }
}
