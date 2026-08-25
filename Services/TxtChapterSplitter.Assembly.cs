namespace BonLivre.Services;

/// <summary>
/// Pass B～F：按族校验、去重、大间隙救援、兜底链，以及最终区间组装。
/// </summary>
internal static partial class TxtChapterSplitter
{
    // ================= Pass B：按族校验 =================

    /// <summary>
    /// 强族直接接受；每个弱族**独立**校验，通过则整族接受。
    /// 多个弱族可以同时通过——一本书里混排多种标题格式时，各格式各自成立、互不排斥。
    /// 兜底族（分隔线）一律先放进 rejected，只有 <see cref="ApplyFallbacks"/> 才会取用。
    /// </summary>
    private static void SelectFamilies(
        List<Candidate> candidates, List<Candidate> accepted, List<Candidate> rejected, bool relaxed)
    {
        accepted.Clear();
        rejected.Clear();

        var byFamily = new Dictionary<Family, List<Candidate>>();
        foreach (var c in candidates)
        {
            if (IsStrong(c.Family)) { accepted.Add(c); continue; }
            if (!IsWeak(c.Family)) { rejected.Add(c); continue; }
            if (!byFamily.TryGetValue(c.Family, out var list))
            {
                list = [];
                byFamily[c.Family] = list;
            }
            list.Add(c);
        }

        foreach (var (_, hits) in byFamily)
        {
            (LooksLikeChapterSeries(hits, relaxed) ? accepted : rejected).AddRange(hits);
        }

        accepted.Sort(static (a, b) => a.LineStart.CompareTo(b.LineStart));
        rejected.Sort(static (a, b) => a.LineStart.CompareTo(b.LineStart));
    }

    /// <summary>
    /// 弱族反误切校验：正文里的「1. 准备材料 / 2. 开火 / 3. 出锅」与真正的章节序列同形，
    /// 区别只在于**编号是否像章号**、以及**两次出现之间是否隔着一整章的正文**。
    ///
    ///  - 编号基本递增，且多数是 +1（列举被打断、重新从 1 开始的会在这里挂掉）；
    ///  - 起始编号要小；例外是「中途换格式」的续接序列（第 1~50 章之后改用 51.），
    ///    它起始编号大但连号率极高，单独放行；
    ///  - 章间距中位数要够大，且过小的间距占比要低——这一条挡住绝大多数正文列举。
    /// </summary>
    private static bool LooksLikeChapterSeries(List<Candidate> hits, bool relaxed)
    {
        var n = hits.Count;
        if (n < MinWeakHits) return false;

        // 装饰行族没有编号，只能靠标题长度、标题重复度与间距判断。
        if (hits[0].Number < 0)
        {
            var distinct = new HashSet<string>(hits.Count, StringComparer.Ordinal);
            foreach (var h in hits)
            {
                if (h.Title.Length > MaxBareTitleChars) return false;
                distinct.Add(h.Title);
            }
            // 章节标题各不相同；章内的装饰分割行（☆☆☆ 分割线 ☆☆☆）则一遍遍重复同一句。
            // 没有编号可比时，这是区分二者最可靠的信号。
            if ((double)distinct.Count / hits.Count <= MinDistinctTitleRatio) return false;
            return GapsLookLikeChapters(hits, relaxed);
        }

        long ascending = 0, consecutive = 0, pairs = 0;
        for (var i = 0; i + 1 < n; i++)
        {
            if (hits[i].Number < 0 || hits[i + 1].Number < 0) continue;
            pairs++;
            if (hits[i + 1].Number > hits[i].Number) ascending++;
            // 区间标题按终点衔接：(1-4) 之后 (5-8) 算 +1 连号。
            var prevEnd = hits[i].RangeEnd >= 0 ? hits[i].RangeEnd : hits[i].Number;
            if (hits[i + 1].Number == prevEnd + 1) consecutive++;
        }
        if (pairs == 0) return false;

        var ascendingRatio = (double)ascending / pairs;
        var consecutiveRatio = (double)consecutive / pairs;
        if (ascendingRatio < (relaxed ? 0.6 : MinAscendingRatio)) return false;
        if (consecutiveRatio < (relaxed ? 0.3 : MinConsecutiveRatio)) return false;

        var first = hits[0].Number;
        var startsLikeChapters =
            first <= MaxFirstNumber ||
            (first <= MaxContinuationNumber && consecutiveRatio >= ContinuationRatio);
        if (!relaxed && !startsLikeChapters) return false;

        return GapsLookLikeChapters(hits, relaxed);
    }

    /// <summary>章间距校验：中位数够大，且「短到不可能是一章」的间距占比够低。
    /// 真章节至少隔着几百字正文，正文里的编号列举只隔几行——两者相差一个数量级，
    /// 阈值取在中间即可，不必也不该按具体书调参。</summary>
    private static bool GapsLookLikeChapters(List<Candidate> hits, bool relaxed)
    {
        var gaps = new int[hits.Count - 1];
        var small = 0;
        for (var i = 0; i + 1 < hits.Count; i++)
        {
            gaps[i] = hits[i + 1].LineStart - hits[i].LineStart;
            if (gaps[i] < SmallGap) small++;
        }
        Array.Sort(gaps);
        if (gaps[gaps.Length / 2] < MinMedianGap) return false;
        return (double)small / gaps.Length <= (relaxed ? RelaxedSmallGapRatio : MaxSmallGapRatio);
    }

    // ================= Pass C：合并去重 =================

    /// <summary>
    /// 贴得太近的两个候选只留强的那个：章节标题下面紧跟一行小编号 / 装饰行时，
    /// 若不在这里去掉，后面的「空章合并」会反过来删掉真标题、留下那行小编号。
    ///
    /// 两个都是强族时不去重——卷标题后面紧跟本卷第一章、楔子后面紧跟第一章，
    /// 都是正常排版，两条都得留下。唯一的例外是元信息短词（正文/后记/尾声…）：
    /// 「章标题下一行裸『正文』标记行」是常见排版，若把它当章节保留，
    /// 它会把真标题的正文切成零长碎片，空章合并便反过来删光全部真标题。
    /// </summary>
    private static List<Candidate> Dedupe(List<Candidate> sorted)
    {
        var kept = new List<Candidate>(sorted.Count);
        foreach (var c in sorted)
        {
            if (kept.Count > 0 && c.LineStart - kept[^1].LineStart < MinHeadingDistance)
            {
                // 与上一条候选之间连一行正文都放不下：这个元信息词只是标记行。
                if (c.Family == Family.Word) continue;
                if (!(IsStrong(c.Family) && IsStrong(kept[^1].Family)))
                {
                    if (Strength(c.Family) > Strength(kept[^1].Family)) kept[^1] = c;
                    continue;
                }
            }
            kept.Add(c);
        }
        return kept;
    }

    // ================= Pass D：大间隙救援 =================

    /// <summary>
    /// 已接受章节之间出现异常大的空档时，在该空档内重新接纳被 Pass B 拒绝的候选。
    /// 覆盖「前半部用 第X章、后半部改用 51. 标题」这类中途换格式的书——
    /// 那半本书的候选整族看起来不合格，但它落在一个明显不正常的大空档里。
    /// </summary>
    private static void RescueLargeGaps(string content, List<Candidate> hits, List<Candidate> rejected)
    {
        if (hits.Count < 2 || rejected.Count == 0) return;

        var bounds = new List<int>(hits.Count + 2) { 0 };
        foreach (var h in hits) bounds.Add(h.LineStart);
        bounds.Add(content.Length);

        var gaps = new int[bounds.Count - 1];
        for (var i = 0; i + 1 < bounds.Count; i++) gaps[i] = bounds[i + 1] - bounds[i];
        var sortedGaps = (int[])gaps.Clone();
        Array.Sort(sortedGaps);
        var threshold = Math.Max(RescueGapFactor * sortedGaps[sortedGaps.Length / 2], RescueMinGap);

        var added = new List<Candidate>();
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            if (gaps[i] <= threshold) continue;
            foreach (var c in rejected)
            {
                if (c.LineStart <= bounds[i] || c.LineStart >= bounds[i + 1]) continue;
                if (!IsWeak(c.Family)) continue;
                added.Add(c);
            }
        }
        if (added.Count == 0) return;

        hits.AddRange(added);
        hits.Sort(static (a, b) => a.LineStart.CompareTo(b.LineStart));
        var deduped = Dedupe(hits);
        hits.Clear();
        hits.AddRange(deduped);
    }

    // ================= Pass E：兜底链 =================

    /// <summary>
    /// 一章都没切出来时逐级放宽：放宽弱族阈值 → 水平分隔线 → 孤立短行。
    /// 每一级都只在上一级仍然切不出章节时才启用，避免误伤正常的书。
    /// </summary>
    private static void ApplyFallbacks(
        string content, List<Candidate> candidates, List<Candidate> hits, List<Candidate> rejected)
    {
        if (hits.Count < 2)
        {
            var accepted = new List<Candidate>();
            var stillRejected = new List<Candidate>();
            SelectFamilies(candidates, accepted, stillRejected, relaxed: true);
            var relaxedHits = Dedupe(accepted);
            if (relaxedHits.Count > hits.Count)
            {
                hits.Clear();
                hits.AddRange(relaxedHits);
                rejected.Clear();
                rejected.AddRange(stillRejected);
            }
        }

        if (hits.Count < 3)
        {
            AddFallback(hits, rejected.Where(static c => c.Family == Family.Separator));
        }

        if (hits.Count < 2)
        {
            AddFallback(hits, CollectStandaloneLines(content));
        }
    }

    private static void AddFallback(List<Candidate> hits, IEnumerable<Candidate> extra)
    {
        var before = hits.Count;
        hits.AddRange(extra);
        if (hits.Count == before) return;
        hits.Sort(static (a, b) => a.LineStart.CompareTo(b.LineStart));
        var deduped = Dedupe(hits);
        hits.Clear();
        hits.AddRange(deduped);
    }

    private static readonly char[] TrailingSentencePunctuation =
        ['，', '。', '！', '？', '；', '：', '、', ',', '.', '!', '?', ';', ':'];

    /// <summary>
    /// 最后一级兜底：前后都是空行、短、且不像句子的独立行当作章节标题。
    /// 只在整本书一个标题都识别不出时启用——此时的替代方案是「整本一章」，
    /// 误判的代价远小于收益。命中数太少则不采用（说明这本书确实没有标题行）。
    /// </summary>
    private static List<Candidate> CollectStandaloneLines(string content)
    {
        var result = new List<Candidate>();
        var pos = 0;
        var previousBlank = true;
        var pendingStart = 0;
        string? pendingTitle = null;

        while (pos <= content.Length)
        {
            var nl = content.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? content.Length : nl;
            var line = content.AsSpan(pos, lineEnd - pos).Trim();
            var blank = line.IsEmpty;

            if (pendingTitle != null && blank)
            {
                result.Add(new Candidate(pendingStart, pendingTitle, Family.Standalone, -1, false));
            }
            pendingTitle = null;

            if (blank)
            {
                previousBlank = true;
            }
            else
            {
                if (previousBlank && LooksLikeStandaloneTitle(line))
                {
                    pendingStart = pos;
                    pendingTitle = Collapse(line.ToString());
                }
                previousBlank = false;
            }

            if (nl < 0) break;
            pos = nl + 1;
        }

        // 文件末尾的孤立行没有「后随空行」，按结尾处理。
        if (pendingTitle != null)
        {
            result.Add(new Candidate(pendingStart, pendingTitle, Family.Standalone, -1, false));
        }

        return result.Count >= MinFallbackHits ? result : [];
    }

    private static bool LooksLikeStandaloneTitle(ReadOnlySpan<char> line)
    {
        if (line.Length == 0 || line.Length > MaxBareTitleChars) return false;
        if ("“「『\"'（(".Contains(line[0])) return false;
        return Array.IndexOf(TrailingSentencePunctuation, line[^1]) < 0;
    }

    // 水平分隔线字符集（半角与全角形式）。
    private const string SeparatorChars = "=*~-_─═▔·•＝＊～＿－";

    /// <summary>
    /// 判定一行是否为「水平分隔线」：trim 后非空白字符 ≥3，其中分隔符占比 ≥50%，
    /// 且首尾非空白字符都是分隔符。覆盖 ===== / ------- / === 标题 === / · · · 等形态，
    /// 同时排除「正文---」「3.14」这类混杂行。
    /// </summary>
    private static bool IsSeparatorLine(ReadOnlySpan<char> line)
    {
        var t = line.Trim();
        if (t.Length < 3) return false;
        int sep = 0, nonws = 0;
        char first = '\0', last = '\0';
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            if (ch == ' ' || ch == '\t' || ch == '　') continue;
            nonws++;
            if (SeparatorChars.Contains(ch)) sep++;
            if (first == '\0') first = ch;
            last = ch;
        }
        if (nonws < 3) return false;
        if (sep * 100 < nonws * 50) return false;
        if (sep < 3) return false;
        return SeparatorChars.Contains(first) && SeparatorChars.Contains(last);
    }

    /// <summary>取从 startAt 开始的第一个非空行（trim 后），截断为 ≤40 字作章节标题；无则回退 fallback。</summary>
    private static string NextNonEmptyLineTitle(string content, int startAt, string fallback)
    {
        var pos = startAt;
        while (pos < content.Length)
        {
            var nl = content.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? content.Length : nl;
            var t = content.AsSpan(pos, lineEnd - pos).Trim();
            if (!t.IsEmpty) return t[..SafeCut(t, Math.Min(MaxTitleChars, t.Length))].ToString();
            if (nl < 0) break;
            pos = nl + 1;
        }
        return fallback;
    }

    // ================= Pass F：区间组装 =================

    private static List<TxtChapterSpan> BuildSpans(string content, List<Candidate> hits)
    {
        if (hits.Count == 0) return [WholeBook(content)];

        var spans = new List<TxtChapterSpan>(hits.Count + 1);

        // 首个标题之前的正文（序言/前言等），非空则保留为一章。
        var firstStart = hits[0].LineStart;
        if (content.AsSpan(0, firstStart).Trim().Length > 0)
        {
            spans.Add(new TxtChapterSpan(
                "前言", 0, firstStart, false,
                ReaderTextMetrics.CalculateContentLength(content.AsSpan(0, firstStart))));
        }

        for (var i = 0; i < hits.Count; i++)
        {
            var start = hits[i].LineStart;
            var end = i + 1 < hits.Count ? hits[i + 1].LineStart : content.Length;
            var body = content.AsSpan(start, end - start);
            var contentLength = ReaderTextMetrics.CalculateContentLength(body);
            // 空章判定：标题行之后是否还有非空正文。不能用 contentLength 与 Title.Length 比较——
            // Title 是清洗过的（不含 【】/☆/◎ 等装饰、空白已压缩），而 contentLength 按整行可见字数计，
            // 装饰标题会让阈值偏低，把「只有标题一行」的章误判为有正文，空章不被合并。
            // 直接取标题行之后的余段计长：有任意非空行才算有正文。
            var firstNl = body.IndexOf('\n');
            var afterTitle = firstNl < 0 ? ReadOnlySpan<char>.Empty : body[(firstNl + 1)..];
            var hasBody = ReaderTextMetrics.CalculateContentLength(afterTitle) > 0;
            spans.Add(new TxtChapterSpan(
                hits[i].Title, start, end - start, hits[i].IsVolume, hasBody ? contentLength : 0));
        }

        // 空章合并：非卷空区间删除，避免目录里出现点进去只有标题的项。
        // 卷标题空区间保留（前端按 IsVolume 折叠/样式区分，seekMap 会跳过 length<=0 的章节）。
        for (var i = 0; i < spans.Count;)
        {
            if (!spans[i].IsVolume && spans[i].ContentLength == 0) spans.RemoveAt(i);
            else i++;
        }

        if (spans.Count == 0) return [WholeBook(content)];

        // 删空章后区间不再首尾相接，这里补回「覆盖全文 + 互不重叠」的不变量：
        // 每一章延伸到下一章的起点，首章从 0 开始。调用方的 char→byte 重放依赖这一点。
        for (var i = 0; i < spans.Count; i++)
        {
            var start = i == 0 ? 0 : spans[i].Start;
            var end = i + 1 < spans.Count ? spans[i + 1].Start : content.Length;
            spans[i] = spans[i] with { Start = start, Length = end - start };
        }
        return spans;
    }

    private static TxtChapterSpan WholeBook(string content) =>
        new("正文", 0, content.Length, false, ReaderTextMetrics.CalculateContentLength(content.AsSpan()));
}
