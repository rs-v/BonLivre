using System.Text;
using System.Text.RegularExpressions;

namespace BonLivre.Services;

/// <summary>
/// TXT 章节切分结果：标题、原文区间、分卷标记，以及与阅读进度同口径的正文长度
/// （char 计数，在切分时一次性算好，读章时不再重算）。
///
/// <see cref="TxtChapterSplitter.Split"/> 产出时 Start/Length 是解码后整本 string 内的
/// **char 索引**；LocalBookService 随后把它们重放翻译成文件内的**绝对字节偏移**（已含 BOM）
/// 再缓存，读章时按字节范围 seek。两个阶段共用这一个类型，口径见各自调用点的注释。
/// </summary>
internal readonly record struct TxtChapterSpan(
    string Title,
    long Start,
    long Length,
    bool IsVolume,
    int ContentLength);

/// <summary>
/// TXT 章节切分。目录、正文、书内搜索和全书进度共用这里产出的区间。
///
/// 设计要点：**一本书里可以同时存在多种标题格式**（第X章 + 番外三 + 51. 后续），
/// 所以不再用「一条巨型正则 + 单一主模式回填」，而是：
///
///  A. 逐行分族收集候选（每族一条小正则，编号解析成数值）；
///  B. 强族直接接受；每个弱族**独立**按「编号递增 / 多为 +1 / 起始编号小 / 章间距够大」
///     校验，通过则整族接受——多族可同时通过，这就是混排支持的来源；
///  C. 合并排序并按强弱去重，丢掉贴着上一个标题的副标记；
///  D. 大间隙救援：异常大的空档里重新接纳被 B 拒绝的候选（覆盖中途换格式的书）；
///  E. 兜底链：放宽阈值 → 水平分隔线 → 孤立短行 → 整本单章「正文」；
///  F. 收尾：首个标题前的正文存为「前言」，非卷空章合并。
///
/// 无外部依赖（只用到 <see cref="ReaderTextMetrics"/>），便于单独喂样本回归。
/// </summary>
internal static partial class TxtChapterSplitter
{
    // ===== 阈值 =====
    /// <summary>整行即标题时的最大长度；超过就只可能是「标题 + 正文同行」。</summary>
    private const int MaxHeadingLine = 60;
    /// <summary>「标题 + 正文同行」允许的最长物理行，再长就不像标题行了。</summary>
    private const int MaxInlineHeadingLine = 400;
    private const int MaxTitleChars = 40;

    // 弱族校验（反误切）：正文里的 1. 2. 3. 列举靠「章间距」被挡掉。
    private const int MinWeakHits = 3;
    private const double MinAscendingRatio = 0.8;
    private const double MinConsecutiveRatio = 0.6;
    private const long MaxFirstNumber = 5;
    private const long MaxContinuationNumber = 200;
    private const double ContinuationRatio = 0.9;
    private const int MinMedianGap = 150;
    private const int SmallGap = 80;
    private const double MaxSmallGapRatio = 0.25;
    private const double RelaxedSmallGapRatio = 0.5;
    /// <summary>无编号族的标题重复度上限：重复率高说明那是章内的装饰分割行，不是章节标题。</summary>
    private const double MinDistinctTitleRatio = 0.5;

    /// <summary>相邻标题去重距离：更近的两个候选只留强的那个（副标题 / 装饰行）。</summary>
    private const int MinHeadingDistance = 50;

    // 大间隙救援
    private const int RescueGapFactor = 4;
    private const int RescueMinGap = 20000;

    // 兜底
    private const int MaxBareTitleChars = 30;
    private const int MinFallbackHits = 3;

    private enum Family
    {
        // 强族：形态足够特异，单条命中即可信。
        DiChapter, DiVolume, VolumePrefix, EnChapter, EnVolume, Fan, Word, Wrapped,
        // 弱族：形态与正文里的编号列举同形，必须整族过校验。
        ChineseNumPunct, ArabicDot, ArabicSpace, ArabicBare, ChineseBare, Bracket, Decorated,
        // 兜底族：只在前面都没切出章节时启用。
        Separator, Standalone,
    }

    private static bool IsStrong(Family f) => f <= Family.Wrapped;
    private static bool IsWeak(Family f) => f is >= Family.ChineseNumPunct and <= Family.Decorated;

    /// <summary>去重时的优先级：强族 &gt; 弱族 &gt; 兜底族。</summary>
    private static int Strength(Family f) => IsStrong(f) ? 2 : IsWeak(f) ? 1 : 0;

    private readonly record struct Candidate(
        int LineStart, string Title, Family Family, long Number, bool IsVolume,
        /// <summary>区间标题（第1-4章）的终止编号；非区间候选为 -1。</summary>
        long RangeEnd = -1);

    /// <summary>
    /// 把 TXT 全文按章节标题切分为若干区间（char 口径）。
    /// 返回的区间按 Start 升序、互不重叠、完整覆盖全文——
    /// 调用方的 char→byte 重放映射依赖这三条不变量。
    /// </summary>
    internal static List<TxtChapterSpan> Split(string content)
    {
        var candidates = CollectCandidates(content);

        var accepted = new List<Candidate>();
        var rejected = new List<Candidate>();
        SelectFamilies(candidates, accepted, rejected, relaxed: false);

        var hits = Dedupe(accepted);

        RescueLargeGaps(content, hits, rejected);
        ApplyFallbacks(content, candidates, hits, rejected);

        return BuildSpans(content, hits);
    }

    // ================= Pass A：逐行分族收集候选 =================

    private static List<Candidate> CollectCandidates(string content)
    {
        var list = new List<Candidate>();
        var pos = 0;
        while (pos <= content.Length)
        {
            var nl = content.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? content.Length : nl;
            var raw = content.AsSpan(pos, lineEnd - pos).Trim();

            // 首字符快速过滤：绝大多数正文行在这里就被排除，不必跑十几条正则。
            if (raw.Length > 0 && raw.Length <= MaxInlineHeadingLine && CouldStartHeading(raw[0]))
            {
                if (TryClassify(raw.ToString(), out var c))
                {
                    list.Add(c with { LineStart = pos });
                }
                else if (IsSeparatorLine(raw))
                {
                    var title = NextNonEmptyLineTitle(content, nl < 0 ? content.Length : nl + 1, "分隔线");
                    list.Add(new Candidate(pos, title, Family.Separator, -1, false));
                }
            }

            if (nl < 0) break;
            pos = nl + 1;
        }
        return list;
    }

    private const string DecorationChars = "☆★✦✧✩✪✫✬✭✮✡✯✰◆◇▲△▼▽■□●○♠♣♥♦※";
    private const string BracketOpeners = "(（[［〔〖【{｛";
    private const string ChineseDigits = "零〇○一二三四五六七八九十百千万萬亿億两兩廿卅卌壹贰貳叁參肆伍陆陸柒捌玖拾佰仟";
    // 短词族的首字，用于首字符快速过滤。
    private const string WordFirstChars = "序楔前引正后後尾终終结結番外附跋简簡文内內作导導完大卷部篇册冊第";

    private static bool CouldStartHeading(char c) =>
        char.IsAsciiDigit(c) || char.IsAsciiLetter(c) ||
        (c >= '０' && c <= '９') ||
        WordFirstChars.Contains(c) || ChineseDigits.Contains(c) ||
        BracketOpeners.Contains(c) || DecorationChars.Contains(c) ||
        c is '◎' or '=' or '＝' or '-' or '─' or '═' or '*' or '＊' or '~' or '～' or '_' or '＿' or '·' or '•' or '▔';

    /// <summary>
    /// 分族识别。先按原行分类（这样 <c>【1】</c> 归 Bracket、与 <c>【1】标题</c> 一致），
    /// 失败再剥掉包装 / 装饰重试（<c>★第三章★</c> → DiChapter），
    /// 仍失败且确实有包装或装饰、内容又短，则归 Wrapped / Decorated。
    /// </summary>
    private static bool TryClassify(string line, out Candidate candidate)
    {
        if (ClassifyCore(line, out candidate)) return true;

        var core = Unwrap(line, out var hadWrapper, out var hadDecoration);
        if ((hadWrapper || hadDecoration) && core.Length > 0)
        {
            if (ClassifyCore(core, out candidate)) return true;
            if (core.Length <= MaxHeadingLine)
            {
                candidate = new Candidate(
                    0, Collapse(core), hadWrapper ? Family.Wrapped : Family.Decorated, -1, false);
                return true;
            }
        }
        candidate = default;
        return false;
    }

    private static bool ClassifyCore(string line, out Candidate candidate)
    {
        candidate = default;

        // --- 强族 ---
        var m = DiChapterRegex().Match(line);
        if (m.Success && InlineOk(line, m))
        {
            var unit = m.Groups["unit"].Value[0];
            var isVolume = unit is '卷' or '部' or '篇' or '册' or '冊';
            candidate = Make(line, m, isVolume ? Family.DiVolume : Family.DiChapter,
                ParseNumber(m.Groups["num"].ValueSpan), isVolume, RangeEndOf(m));
            return true;
        }

        if (line.Length <= MaxHeadingLine)
        {
            m = VolumePrefixRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.VolumePrefix, ParseNumber(m.Groups["num"].ValueSpan), true);
                return true;
            }
        }

        m = EnChapterRegex().Match(line);
        if (m.Success && InlineOk(line, m))
        {
            var isVolume = !m.Groups["kind"].ValueSpan.Equals("chapter", StringComparison.OrdinalIgnoreCase);
            candidate = Make(line, m, isVolume ? Family.EnVolume : Family.EnChapter,
                ParseNumber(m.Groups["num"].ValueSpan), isVolume);
            return true;
        }

        m = FanRegex().Match(line);
        if (m.Success && InlineOk(line, m))
        {
            candidate = Make(line, m, Family.Fan, ParseNumber(m.Groups["num"].ValueSpan), false);
            return true;
        }

        if (line.Length <= MaxHeadingLine)
        {
            m = WordRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.Word, -1, false);
                return true;
            }

            // --- 弱族（形态与正文列举同形，交给 Pass B 整族校验） ---
            m = BracketRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.Bracket, ParseNumber(m.Groups["num"].ValueSpan),
                    false, RangeEndOf(m));
                return true;
            }

            m = ChineseNumPunctRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.ChineseNumPunct, ParseNumber(m.Groups["num"].ValueSpan), false);
                return true;
            }

            m = ArabicDotRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.ArabicDot, ParseNumber(m.Groups["num"].ValueSpan), false);
                return true;
            }

            m = ArabicSpaceRegex().Match(line);
            if (m.Success)
            {
                candidate = Make(line, m, Family.ArabicSpace, ParseNumber(m.Groups["num"].ValueSpan), false);
                return true;
            }

            m = BareNumberRegex().Match(line);
            if (m.Success)
            {
                var num = ParseNumber(m.Groups["num"].ValueSpan);
                if (num >= 0)
                {
                    candidate = Make(line, m, char.IsAsciiDigit(line[0]) || line[0] is >= '０' and <= '９'
                        ? Family.ArabicBare : Family.ChineseBare, num, false);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 「标题 + 正文同行」的准入：编号后要么直接到行尾，要么跟着分隔符，
    /// 要么整行本来就短。这样 <c>第一章 开端　那一天……</c> 能切出来，
    /// 而正文里的 <c>第一章讲的是……</c> 不会。
    /// </summary>
    private static bool InlineOk(string line, Match m)
    {
        var rest = line.AsSpan(m.Groups["head"].Index + m.Groups["head"].Length);
        if (rest.Length == 0) return true;
        var c = rest[0];
        if (char.IsWhiteSpace(c) || ":：、.．·-—–|｜".Contains(c)) return true;
        return line.Length <= MaxHeadingLine;
    }

    private static Candidate Make(string line, Match m, Family family, long number, bool isVolume, long rangeEnd = -1)
    {
        var headLength = m.Groups["head"].Index + m.Groups["head"].Length;
        return new(0, MakeTitle(line, headLength), family, number, isVolume, rangeEnd);
    }

    /// <summary>取匹配里的 end 组（区间终点），无则 -1。</summary>
    private static long RangeEndOf(Match m)
    {
        var e = m.Groups["end"];
        return e.Success ? ParseNumber(e.ValueSpan) : -1;
    }

    private static readonly char[] SentencePunctuation = ['，', '。', '！', '？', '；', ',', '.', '!', '?', ';'];

    /// <summary>
    /// 标题文本：短行整行即标题（保留原貌，含标题里的逗号）；
    /// 长行说明正文与标题同行，在首个句读处截断，兜底截到 <see cref="MaxTitleChars"/>。
    /// </summary>
    private static string MakeTitle(string line, int headLength)
    {
        if (line.Length <= MaxHeadingLine) return Collapse(line);

        headLength = Math.Min(headLength, line.Length);
        var rest = line.AsSpan(headLength);
        var cut = rest.IndexOfAny(SentencePunctuation);
        var limit = cut >= 0 ? Math.Min(cut, MaxTitleChars) : Math.Min(rest.Length, MaxTitleChars);
        return Collapse(string.Concat(line.AsSpan(0, headLength), rest[..SafeCut(rest, limit)]));
    }

    /// <summary>把截断位置往前挪到不劈开代理对的地方：劈开会留下孤立代理，序列化成 JSON 时变成乱码。</summary>
    private static int SafeCut(ReadOnlySpan<char> s, int length)
    {
        if (length <= 0 || length >= s.Length) return Math.Clamp(length, 0, s.Length);
        return char.IsHighSurrogate(s[length - 1]) ? length - 1 : length;
    }

    /// <summary>把空白（含全角空格）压成单个半角空格并 trim，避免目录里出现长串缩进。</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>剥掉整行包装（【】〖〗〔〕［］、◎…◎、=== … ===）与首尾装饰符号，可叠加。</summary>
    private static string Unwrap(string line, out bool hadWrapper, out bool hadDecoration)
    {
        hadWrapper = false;
        hadDecoration = false;
        var span = line.AsSpan();
        for (var round = 0; round < 4; round++)
        {
            var before = span.Length;

            span = span.Trim();
            var start = 0;
            var end = span.Length;
            while (start < end && (DecorationChars.Contains(span[start]) || char.IsWhiteSpace(span[start]))) start++;
            while (end > start && (DecorationChars.Contains(span[end - 1]) || char.IsWhiteSpace(span[end - 1]))) end--;
            if (start > 0 || end < span.Length) { hadDecoration = true; span = span[start..end]; }

            span = span.Trim();
            if (span.Length >= 2)
            {
                var open = span[0];
                var close = span[^1];
                var paired =
                    (open == '【' && close == '】') || (open == '〖' && close == '〗') ||
                    (open == '〔' && close == '〕') || (open == '［' && close == '］') ||
                    (open == '◎' && close == '◎');
                if (paired) { hadWrapper = true; span = span[1..^1]; }
                else
                {
                    var lead = 0;
                    while (lead < span.Length && (span[lead] is '=' or '＝')) lead++;
                    var trail = 0;
                    while (trail < span.Length - lead && (span[^(trail + 1)] is '=' or '＝')) trail++;
                    if (lead >= 3 && trail >= 3) { hadWrapper = true; span = span[lead..^trail]; }
                }
            }

            if (span.Length == before) break;
        }
        return span.Trim().ToString();
    }
}
