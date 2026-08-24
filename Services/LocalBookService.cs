using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text;
using System.Linq;
using HtmlAgilityPack;
using VersOne.Epub;
using BonLivre.Models;
using static BonLivre.Services.ReaderTextMetrics;

namespace BonLivre.Services;

public partial class LocalBookService
{
    private readonly string _booksDir;
    // 缓存以 (LastWriteTimeUtc, Length) 做失效判断：books/ 目录下的文件可能被用户替换，
    // 旧缓存若不失效会一直读到替换前的内容。
    // EpubBook 会把整本（含全部图片）读进内存，所以缓存必须有上限：超过 MaxCachedEpubs
    // 就淘汰最久未访问的条目，否则书架上每多一本 EPUB 就永久多占几十 MB。
    private const int MaxCachedEpubs = 8;
    private static readonly ConcurrentDictionary<string, EpubCacheEntry> _epubCache = new();
    private static readonly ConcurrentDictionary<string, TxtBookCache> _txtCache = new();
    // 书架元数据缓存：只存书名/作者/简介/章数/末章名，一条几百字节，不随书数量吃内存。
    // 有了它 GetLocalBooks 就不必为每本 EPUB 触碰 _epubCache，书架加载与 EPUB 缓存上限互不干扰。
    private static readonly ConcurrentDictionary<string, ShelfMetaCache> _shelfMetaCache = new();
    private static long _epubAccessCounter;
    private readonly Dictionary<string, UploadLock> _uploadLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _uploadLocksLock = new();

    private sealed class EpubCacheEntry
    {
        public required DateTime LastWriteUtc { get; init; }
        public required long Length { get; init; }
        public required EpubBook Book { get; init; }
        /// <summary>单调递增的访问序号，仅用于挑选淘汰对象。</summary>
        public long LastAccess;
    }

    /// <summary>书架条目的元数据快照，按 (LastWriteTimeUtc, Length) 失效。</summary>
    private sealed record ShelfMetaCache(
        DateTime LastWriteUtc,
        long Length,
        string Name,
        string Author,
        string? Intro,
        int TotalChapters,
        string LatestChapter);

    private sealed class UploadLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    public LocalBookService()
    {
        _booksDir = Path.Combine(Directory.GetCurrentDirectory(), "books");
        if (!Directory.Exists(_booksDir)) Directory.CreateDirectory(_booksDir);
    }

    private EpubBook GetOrAddEpub(string filePath)
    {
        var info = new FileInfo(filePath);
        if (_epubCache.TryGetValue(filePath, out var cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            cached.LastAccess = Interlocked.Increment(ref _epubAccessCounter);
            return cached.Book;
        }
        var book = EpubReader.ReadBook(filePath);
        _epubCache[filePath] = new EpubCacheEntry
        {
            LastWriteUtc = info.LastWriteTimeUtc,
            Length = info.Length,
            Book = book,
            LastAccess = Interlocked.Increment(ref _epubAccessCounter),
        };
        TrimEpubCache();
        return book;
    }

    /// <summary>缓存超限时淘汰最久未访问的条目。并发下允许短暂略微超限，不值得为此加全局锁。</summary>
    private static void TrimEpubCache()
    {
        while (_epubCache.Count > MaxCachedEpubs)
        {
            string? oldestKey = null;
            var oldestAccess = long.MaxValue;
            foreach (var (key, entry) in _epubCache)
            {
                if (entry.LastAccess >= oldestAccess) continue;
                oldestAccess = entry.LastAccess;
                oldestKey = key;
            }
            if (oldestKey == null || !_epubCache.TryRemove(oldestKey, out _)) break;
        }
    }

    /// <summary>
    /// TXT 章节切分结果（缓存条目）。不缓存整本解码后的 string——只缓存编码与按字节偏移组织的章节区间，
    /// 读章正文时按 Span 的字节范围 seek + Decoder 流式解码，避免几十 MB 的整本 string 常驻内存。
    /// </summary>
    private sealed record TxtBookCache(
        DateTime LastWriteUtc,
        long FileLength,
        Encoding Encoding,
        int BomLength,
        List<TxtChapterSpan> Spans);

    private static TxtBookCache GetOrAddTxt(string filePath)
    {
        var info = new FileInfo(filePath);
        if (_txtCache.TryGetValue(filePath, out var cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc && cached.FileLength == info.Length)
        {
            return cached;
        }
        var bytes = File.ReadAllBytes(filePath);
        var (encoding, bomLength, content) = DecodeTextAutoEncoding(bytes);
        // 切分在解码后的整本 string 上做（启发式需全文扫描，无法不读），
        // 但完成后不再保留整本 string——只把 char 索引翻译成字节偏移存进 Span。
        var spans = BuildTxtChaptersWithByteOffsets(content, bytes, encoding, bomLength);
        var entry = new TxtBookCache(info.LastWriteTimeUtc, info.Length, encoding, bomLength, spans);
        _txtCache[filePath] = entry;
        return entry;
    }

    // 严格 UTF-8（无 BOM 输出、非法字节抛异常），用于编码探测。
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    // GB18030 需要 CodePagesEncodingProvider（Program.cs 启动时注册），懒取一次。
    private static readonly Lazy<Encoding?> Gb18030 = new(() =>
    {
        try
        {
            return Encoding.GetEncoding("GB18030",
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch { return null; }
    });

    /// <summary>
    /// 给定探测出的编码，返回一个用于「读章正文」的副本：解码采用宽松回退（ReplacementFallback），
    /// 避免末章残缺的多字节序列或文件局部损坏在 flush 时抛 DecoderFallbackException 导致 500。
    /// 探测阶段仍用严格回退的实例（StrictUtf8 / Gb18030）来判断编码，二者职责分离。
    /// </summary>
    private static Encoding ForReading(Encoding encoding) => encoding switch
    {
        UTF8Encoding => new UTF8Encoding(false, throwOnInvalidBytes: false),
        _ when ReferenceEquals(encoding, Gb18030.Value) || encoding.CodePage == Gb18030.Value?.CodePage
            => Encoding.GetEncoding("GB18030", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback),
        _ => (Encoding)encoding.Clone(),
    };

    /// <summary>
    /// 识别文本编码并解码：先看 BOM（UTF-8/UTF-16LE/BE），无 BOM 时尝试严格 UTF-8
    /// 解码，失败则按 GB18030（兼容 GBK/GB2312，中文 TXT 最常见的非 UTF-8 编码）解码。
    /// 都不行时回退宽松 UTF-8，乱码总好过 500。
    /// 返回 (解码用 Encoding, BOM 长度, 解码后的整本 string)。
    /// 返回的 Encoding 只用于标识编码种类；实际解码一律走 <see cref="ForReading"/> 的宽松副本，
    /// 保证「探测/切分/读章」三处对同一批字节得到完全一致的字符流。
    /// </summary>
    private static (Encoding Encoding, int BomLength, string Content) DecodeTextAutoEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (StrictUtf8, 3, Decode(StrictUtf8, bytes, 3));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2, Decode(Encoding.Unicode, bytes, 2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2, Decode(Encoding.BigEndianUnicode, bytes, 2));

        try
        {
            return (StrictUtf8, 0, StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            var gb = Gb18030.Value;
            if (gb != null)
            {
                try { return (gb, 0, gb.GetString(bytes)); }
                catch (DecoderFallbackException) { }
            }
            return (Encoding.UTF8, 0, Decode(Encoding.UTF8, bytes, 0));
        }

        static string Decode(Encoding encoding, byte[] bytes, int bomLength) =>
            ForReading(encoding).GetString(bytes, bomLength, bytes.Length - bomLength);
    }

    /// <summary>
    /// 把一组升序的 char 索引翻译成文件内的绝对字节偏移（含 BOM）。
    ///
    /// 不能靠「把解码后的字符串重新编码」来算：宽松回退下非法字节被替换成 U+FFFD，
    /// 再编码回去字节数不同（例如 1 字节的坏字节变成 3 字节），偏移会从损坏点起整体漂移，
    /// 之后每一章都会 seek 到错误位置、章首被截断或乱码。
    ///
    /// 这里改为重放一次解码：用与解码时完全相同的 Decoder，把输出缓冲的容量限制成
    /// 「到下一个目标 char 为止」，Convert 填满输出缓冲就会停下，此时报告的 bytesUsed
    /// 正是这段 char 精确对应的字节数。逐目标推进即可得到精确映射。
    /// </summary>
    private static long[] MapCharIndicesToByteOffsets(
        byte[] bytes, int bomLength, Encoding encoding, List<int> charIndices)
    {
        var offsets = new long[charIndices.Count];
        var decoder = ForReading(encoding).GetDecoder();
        const int MaxChunkChars = 8192;
        var scratch = new char[MaxChunkChars];
        var bytePos = Math.Min(bomLength, bytes.Length);
        var charPos = 0;

        for (var i = 0; i < charIndices.Count; i++)
        {
            // charIndices 升序；用 Max 兜住「上一轮为跨过代理对而多解了一个 char」的情况。
            var target = Math.Max(charPos, charIndices[i]);
            while (charPos < target && bytePos < bytes.Length)
            {
                var want = Math.Min(MaxChunkChars, target - charPos);
                decoder.Convert(bytes, bytePos, bytes.Length - bytePos,
                    scratch, 0, want, flush: false,
                    out var bytesUsed, out var charsUsed, out _);
                if (bytesUsed == 0 && charsUsed == 0)
                {
                    // 目标恰好落在一个代理对中间：Convert 不肯拆开代理对，放宽一格让它整对写出，
                    // 越过边界后继续（此时 charPos 会比 target 多 1，由上面的 Max 吸收）。
                    if (want >= MaxChunkChars) break;
                    decoder.Convert(bytes, bytePos, bytes.Length - bytePos,
                        scratch, 0, want + 1, flush: false,
                        out bytesUsed, out charsUsed, out _);
                    if (bytesUsed == 0 && charsUsed == 0) break;
                }
                bytePos += bytesUsed;
                charPos += charsUsed;
            }
            offsets[i] = bytePos;
        }
        return offsets;
    }

    /// <summary>
    /// 调用 <see cref="TxtChapterSplitter.Split"/> 得到 char 口径的章节区间，
    /// 再一次线性重放解码把每个区间的起止 char 索引翻译成文件内的绝对字节偏移。
    ///
    /// 切分本身在解码后的整本 string 上做（启发式需全文扫描），但本方法返回后整本 string
    /// 即由调用方丢弃：缓存里只留字节区间，读章正文时按字节范围 seek 流式解码。
    /// </summary>
    private static List<TxtChapterSpan> BuildTxtChaptersWithByteOffsets(
        string content, byte[] bytes, Encoding encoding, int bomLength)
    {
        var charSpans = TxtChapterSplitter.Split(content);
        if (charSpans.Count == 0) return charSpans;

        // 收集每章的起止 char 索引，一次线性重放解码把它们全部翻译成字节偏移。
        // Split 保证区间按 Start 升序、互不重叠且覆盖全文，故这个列表天然非递减，
        // 满足 MapCharIndicesToByteOffsets 对升序的要求。
        var indices = new List<int>(charSpans.Count * 2);
        foreach (var s in charSpans)
        {
            indices.Add((int)s.Start);
            indices.Add((int)(s.Start + s.Length));
        }
        var offsets = MapCharIndicesToByteOffsets(bytes, bomLength, encoding, indices);

        var result = new List<TxtChapterSpan>(charSpans.Count);
        for (var i = 0; i < charSpans.Count; i++)
        {
            var byteStart = offsets[i * 2];
            var byteEnd = offsets[i * 2 + 1];
            result.Add(charSpans[i] with { Start = byteStart, Length = byteEnd - byteStart });
        }
        return result;
    }

    /// <summary>
    /// 从 local:// URL 解析出 books/ 目录内的真实文件路径，并阻止路径穿越。
    /// 取 '#' 之前的文件名部分，与 _booksDir 拼接并用 GetFullPath 规范化；
    /// 若规范化结果逃逸出 _booksDir（如 ../ 穿越），返回 null。所有本地文件访问都应经此解析。
    /// 这里不能做 URL 解码：BookUrl 由原始文件名生成，查询参数又已被 ASP.NET 解码过一次，
    /// 再解码会破坏含 '+' 或 %xx 形态字符的文件名（'+' 会变成空格）。
    /// </summary>
    private string? ResolveLocalPath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var fileName = url.Replace("local://", "").Split('#')[0];
        if (string.IsNullOrEmpty(fileName)) return null;

        var baseDir = Path.GetFullPath(_booksDir);
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, fileName));

        var baseWithSep = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)) return null;

        return fullPath;
    }

    public List<Book> GetLocalBooks()
    {
        var bookshelf = new List<Book>();
        var localFiles = Directory.GetFiles(_booksDir, "*.*")
            .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var file in localFiles)
        {
            var isEpub = file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase);
            var info = new FileInfo(file);
            var meta = GetOrAddShelfMeta(file, info, isEpub);

            var book = new Book(
                Name: meta.Name,
                Author: meta.Author,
                BookUrl: $"local://{Path.GetFileName(file)}",
                TocUrl: $"local://{Path.GetFileName(file)}",
                Origin: "local",
                OriginName: isEpub ? "EPUB" : "本地导入",
                Intro: meta.Intro,
                TotalChapterNum: meta.TotalChapters,
                LatestChapterTitle: meta.LatestChapter,
                ImportedAt: new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
            );
            bookshelf.Add(book);
        }
        return bookshelf;
    }

    /// <summary>
    /// 取书架条目的元数据，按 (LastWriteTimeUtc, Length) 缓存。
    /// EPUB 需要整本读入 + 解析末章 HTML，TXT 需要全文切分，两者都不该在每次刷新书架时重做；
    /// 更重要的是这样 GetLocalBooks 不会把书架上每一本 EPUB 都钉进 _epubCache。
    /// </summary>
    private ShelfMetaCache GetOrAddShelfMeta(string file, FileInfo info, bool isEpub)
    {
        if (_shelfMetaCache.TryGetValue(file, out var cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            return cached;
        }

        var fileName = Path.GetFileNameWithoutExtension(file);
        string name = fileName;
        string author = isEpub ? "EPUB" : "本地作者";
        string? intro = "";
        int totalChapters = 0;
        string latestChapter = "未更新";

        if (isEpub)
        {
            // 优先用 EPUB 元数据里的书名/作者/简介；解析失败回退文件名。
            try
            {
                var epub = GetOrAddEpub(file);
                if (!string.IsNullOrWhiteSpace(epub.Title)) name = epub.Title.Trim();
                var epubAuthor = epub.Author;
                if (!string.IsNullOrWhiteSpace(epubAuthor)) author = epubAuthor.Trim();
                intro = epub.Description;
                totalChapters = epub.ReadingOrder.Count;
                if (totalChapters > 0)
                {
                    var lastFile = epub.ReadingOrder[totalChapters - 1];
                    latestChapter = GetEpubChapterTitle(epub, lastFile.FilePath, totalChapters);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetLocalBooks] EPUB metadata error: {file}: {ex.Message}");
            }
        }
        else
        {
            // TXT 从文件名提取书名/作者，支持常见命名：《书名》作者、书名 - 作者、书名 作者：xxx。
            (name, author) = ParseTxtFileName(fileName);
            // 超大 TXT：整本读入做启发式切分代价高。书架扫描只需总章数与末章标题，
            // 延迟到首次打开目录时再解析（GetChapterList 触发 GetOrAddTxt）。
            // 这里按文件大小保守判定，避免书架加载卡顿。
            const long LargeTxtBytes = 16L * 1024 * 1024;
            if (info.Length <= LargeTxtBytes)
            {
                try
                {
                    var spans = GetOrAddTxt(file).Spans;
                    totalChapters = spans.Count;
                    if (spans.Count > 0) latestChapter = spans[^1].Title;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GetLocalBooks] TXT parse error: {file}: {ex.Message}");
                }
            }
        }

        var meta = new ShelfMetaCache(
            info.LastWriteTimeUtc, info.Length, name, author, intro, totalChapters, latestChapter);
        _shelfMetaCache[file] = meta;
        return meta;
    }

    // 《书名》后跟作者；或「书名 - 作者」；或「书名 作者：xxx」。
    [GeneratedRegex(@"^《(?<name>[^》]+)》\s*(?:作者[：:]\s*)?(?<author>.*)$")]
    private static partial Regex BracketTitleRegex();
    [GeneratedRegex(@"^(?<name>.+?)\s*(?:-|—|_)\s*(?:作者[：:]\s*)?(?<author>[^-—_]+)$")]
    private static partial Regex DashAuthorRegex();
    [GeneratedRegex(@"^(?<name>.+?)\s+作者[：:]\s*(?<author>.+)$")]
    private static partial Regex AuthorMarkerRegex();

    /// <summary>
    /// 从 TXT 文件名（不含扩展名）尽力解析出书名与作者。
    /// 无法识别时书名取整个文件名、作者回退「本地作者」，与旧行为一致。
    /// </summary>
    internal static (string Name, string Author) ParseTxtFileName(string fileName)
    {
        fileName = fileName.Trim();

        var m = BracketTitleRegex().Match(fileName);
        if (m.Success)
        {
            var author = m.Groups["author"].Value.Trim();
            return (m.Groups["name"].Value.Trim(), author.Length > 0 ? author : "本地作者");
        }

        m = AuthorMarkerRegex().Match(fileName);
        if (m.Success)
        {
            return (m.Groups["name"].Value.Trim(), m.Groups["author"].Value.Trim());
        }

        m = DashAuthorRegex().Match(fileName);
        // 「- 作者」式命名里作者通常较短；过长的尾段更可能是副标题，不当作者处理。
        if (m.Success && m.Groups["author"].Value.Trim().Length <= 12)
        {
            return (m.Groups["name"].Value.Trim(), m.Groups["author"].Value.Trim());
        }

        return (fileName, "本地作者");
    }

    private static string GetEpubChapterTitle(EpubBook book, string filePath, int chapterNumber)
    {
        // 1. 优先用 nav 匹配 reading-order 文件路径，拿到真实章节标题。
        //    nav 可能含嵌套；VersOne.Epub 把所有层级平铺到 Navigation，逐个比对 ContentFilePath。
        //    仅匹配 ContentFilePath（不含 Anchor）并要求有非空 Title，避免 Anchor-only 链接误匹配。
        if (book.Navigation != null)
        {
            foreach (var n in book.Navigation)
            {
                var link = n.Link?.ContentFilePath;
                if (!string.IsNullOrEmpty(link) && link == filePath && !string.IsNullOrWhiteSpace(n.Title))
                    return n.Title.Trim();
            }
        }

        // 2. nav 缺失时，回退到正文里首个标题标签（h1~h6）的文本；
        //    再不行取正文首个非空文本段（如 calibre 用 <p><span>章节名</span></p> 包裹标题）。
        var textFile = book.ReadingOrder.FirstOrDefault(f => f.FilePath == filePath);
        if (textFile != null)
        {
            var heading = FirstHeadingTitle(textFile.Content);
            if (!string.IsNullOrWhiteSpace(heading)) return heading.Trim();

            var firstLine = FirstNonEmptyTextLine(textFile.Content);
            if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine.Trim();
        }

        return $"章节 {chapterNumber}";
    }

    /// <summary>从 HTML 片段里取首个 h1~h6 标题的纯文本；无则返回 null。</summary>
    private static string? FirstHeadingTitle(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        for (int level = 1; level <= 6; level++)
        {
            var node = doc.DocumentNode.SelectSingleNode($"//h{level}");
            if (node != null)
            {
                var t = node.InnerText;
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        return null;
    }

    /// <summary>取正文首个非空文本段（trim 后），用于无标题标签时尽力给出章节名。
    /// 跳过明显的封面/标题页占位词（Cover/封面/标题页），避免这些被误当章节名。
    /// 跳过过长的串（>80 字，多为简介/版权页）。
    /// 若整章无像章节标题的串，回退首个非套话短行。</summary>
    private static string? FirstNonEmptyTextLine(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        string? first = null;
        string? Probe(string t)
        {
            if (t.Length == 0 || t.Length > 80) return null;
            if (IsPlaceholderPageText(t)) return null;
            if (IsBoilerplateLine(t)) return null;
            return t;
        }
        foreach (var p in body.Descendants("p"))
        {
            var t = Probe(HtmlEntity.DeEntitize(p.InnerText).Trim());
            if (t == null) continue;
            first ??= t;
            if (LooksLikeChapterTitle(t)) return t;
        }
        if (first == null)
        {
            foreach (var blk in body.Descendants().Where(n => n.Name is "div" or "blockquote" or "span"))
            {
                if (blk.HasChildNodes && blk.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Text))
                {
                    var t = Probe(HtmlEntity.DeEntitize(blk.InnerText).Trim());
                    if (t == null) continue;
                    first ??= t;
                    if (LooksLikeChapterTitle(t)) return t;
                }
            }
        }
        return first;
    }

    private static bool IsPlaceholderPageText(string t)
    {
        var lower = t.ToLowerInvariant();
        return lower is "cover" or "封面" or "标题页" or "title page" or "image"
            || lower.StartsWith("cover", StringComparison.Ordinal);
    }

    // 版权页/署名行：以「译」「著」结尾、含「出版集团」「丛书」「系列」等。
    private static bool IsBoilerplateLine(string t)
    {
        if (t.EndsWith("著", StringComparison.Ordinal) || t.EndsWith("译", StringComparison.Ordinal)) return true;
        if (t.Contains("出版集团") || t.Contains("出版社")) return true;
        if (t.Contains("丛书") || t.Contains("系列")) return true;
        return false;
    }

    // 像章节标题：第N章/序章/楔子/前言/后记/番外，或纯短词（无标点、≤12 字）。
    [GeneratedRegex(@"^(第[零〇○一二三四五六七八九十百千万萬0-9]+[章节回集话幕]|序章?|楔子|前言|后记|后序|尾声|终章|结语|番外|外传|附录|跋|简介|文案|内容提要|内容简介|作者的话|导读|Book|Part|Volume|Chapter)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ChapterLikeTitleRegex();
    private static bool LooksLikeChapterTitle(string t) =>
        ChapterLikeTitleRegex().IsMatch(t) || (t.Length <= 12 && !t.Contains('，') && !t.Contains('。'));

    public List<BookChapter>? GetChapterList(string url)
    {
        var filePath = ResolveLocalPath(url);
        if (filePath == null || !File.Exists(filePath)) return null;

        if (filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            var epubChapters = new List<BookChapter>();
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                int idx = 0;
                foreach (var textFile in book.ReadingOrder)
                {
                    string title = GetEpubChapterTitle(book, textFile.FilePath, idx + 1);
                    // 与 TXT 一样给出 contentLength，供阅读器 seekMap / 全书进度条使用。
                    // 解析成本与 getBookContent 同阶；EPUB 单章 HTML 通常不大，且结果随 Epub 缓存复用。
                    var body = ExtractEpubChapterText(textFile.Content, textFile.FilePath);
                    var contentLength = CalculateContentLength(body.AsSpan());
                    epubChapters.Add(new BookChapter(
                        title,
                        $"{url}#epub#{textFile.FilePath}",
                        idx++,
                        ContentLength: contentLength));
                }
                return epubChapters;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EPUB TOC Error: {ex.Message}");
                return null;
            }
        }

        var txt = GetOrAddTxt(filePath);
        var spans = txt.Spans;
        var chapters = new List<BookChapter>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            chapters.Add(new BookChapter(
                spans[i].Title,
                $"{url}#{i}",
                i,
                IsVolume: spans[i].IsVolume,
                ContentLength: spans[i].ContentLength));
        }
        return chapters;
    }

    /// <summary>
    /// 流式写出章节正文（JSON 字符串片段形式）到 chunkWriter：把章正文按块解码，
    /// 逐块回调（调用方负责 JSON 转义与 flush）。支持 EPUB 与 TXT：
    ///  - TXT：按 Span 字节范围 seek + Decoder 流式解码，不读整本、不拼整章 string；
    ///  - EPUB：解析出整章 string 后切块回调（单章 HTML 解析本就不大）。
    /// 返回 false 表示无内容或解析失败（端点按 legado 兼容回退模拟正文）。
    /// </summary>
    public async Task<bool> StreamChapterContentAsync(
        string url, int index, Func<ReadOnlyMemory<char>, CancellationToken, Task> chunkWriter, CancellationToken ct)
    {
        var filePath = ResolveLocalPath(url);
        if (filePath == null || !File.Exists(filePath)) return false;

        if (filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var epubParts = url.Split("#epub#", StringSplitOptions.RemoveEmptyEntries);
                var targetFile = epubParts.Length > 1 ? epubParts[1] : null;
                var book = GetOrAddEpub(filePath);
                var textFile = targetFile == null
                    ? index >= 0 && index < book.ReadingOrder.Count ? book.ReadingOrder[index] : null
                    : book.ReadingOrder.FirstOrDefault(file => file.FilePath == targetFile);
                if (textFile == null) return false;
                var text = ExtractEpubChapterText(textFile.Content, textFile.FilePath).AsMemory();
                const int ChunkChars = 8192;
                for (int off = 0; off < text.Length;)
                {
                    ct.ThrowIfCancellationRequested();
                    var len = Math.Min(ChunkChars, text.Length - off);
                    // 避免在代理对中间切开，否则下游 JSON UTF-8 编码会把 emoji 拆坏。
                    if (len > 0 && len < text.Length - off &&
                        char.IsHighSurrogate(text.Span[off + len - 1]))
                    {
                        len--;
                    }
                    if (len <= 0) len = Math.Min(ChunkChars, text.Length - off);
                    await chunkWriter(text.Slice(off, len), ct).ConfigureAwait(false);
                    off += len;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"EPUB Content Error: {ex.Message}");
                return false;
            }
        }

        if (!filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        var txt = GetOrAddTxt(filePath);
        if (index < 0 || index >= txt.Spans.Count) return false;
        await ReadChapterSpanAsync(filePath, txt, txt.Spans[index], chunkWriter, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>按整本共享的读取参数打开 TXT，供逐章 seek 读取复用同一个文件句柄。</summary>
    private static FileStream OpenTxtForReading(string filePath) =>
        new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: TxtByteChunk, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    private const int TxtByteChunk = 65536;

    /// <summary>
    /// 按 Span 的字节范围从文件 seek 读取，用 Decoder 分块解码，逐块回调 chunkWriter。
    /// Decoder 跨块保留状态，正确处理 GB18030/UTF-16 多字节序列跨块边界。
    /// Span.Start 是含 BOM 的文件绝对偏移，故直接 seek。
    /// 解码用 ForReading(encoding) 的宽松回退副本，避免末章残缺多字节序列抛异常。
    /// </summary>
    private static async Task ReadChapterSpanAsync(
        string filePath, TxtBookCache txt, TxtChapterSpan span,
        Func<ReadOnlyMemory<char>, CancellationToken, Task> chunkWriter, CancellationToken ct)
    {
        // 章节区间可能为 0（卷标题空区间被保留时），直接返回空。
        if (span.Length <= 0) return;

        var fs = OpenTxtForReading(filePath);
        try
        {
            await ReadChapterSpanAsync(fs, txt, span, chunkWriter, ct).ConfigureAwait(false);
        }
        finally
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>在调用方持有的 FileStream 上读取一章，供整本扫描（如全文搜索）复用文件句柄。</summary>
    private static async Task ReadChapterSpanAsync(
        FileStream fs, TxtBookCache txt, TxtChapterSpan span,
        Func<ReadOnlyMemory<char>, CancellationToken, Task> chunkWriter, CancellationToken ct)
    {
        if (span.Length <= 0) return;

        fs.Seek(span.Start, SeekOrigin.Begin);
        var decoder = ForReading(txt.Encoding).GetDecoder();
        var byteBuf = new byte[TxtByteChunk];
        // 最坏 4 字节/char；给足空间避免 Convert 因输出缓冲不足而多次往返。
        var charBuf = new char[TxtByteChunk];
        var remaining = span.Length;
        var last = false;

        while (remaining > 0 && !last)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(byteBuf.Length, remaining);
            var read = await fs.ReadAsync(byteBuf.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read <= 0) break;
            remaining -= read;
            last = remaining <= 0;

            var bytePos = 0;
            while (bytePos < read)
            {
                decoder.Convert(byteBuf, bytePos, read - bytePos,
                    charBuf, 0, charBuf.Length,
                    flush: last, out var bytesUsed, out var charsUsed, out _);
                if (charsUsed > 0)
                    await chunkWriter(charBuf.AsMemory(0, charsUsed), ct).ConfigureAwait(false);
                bytePos += bytesUsed;
                // 极端情况下既没消费字节也没产出字符：避免死循环。
                if (bytesUsed == 0 && charsUsed == 0) break;
            }
        }

        // flush 收尾：Decoder 可能还有未完成的多字节序列需要冲刷。最后一轮 Convert 已传 flush=last，
        // 但若末块因输出缓冲边界未冲完，这里再补一次空输入 flush。宽松回退下残缺序列替换为 U+FFFD，
        // 不抛异常。
        decoder.Convert(byteBuf, 0, 0, charBuf, 0, charBuf.Length,
            flush: true, out _, out var tailChars, out _);
        if (tailChars > 0)
            await chunkWriter(charBuf.AsMemory(0, tailChars), ct).ConfigureAwait(false);
    }

    /// <summary>搜索本地书籍已渲染的正文，返回可直接用于阅读器跳转的章内位置。</summary>
    public async Task<List<BookContentSearchResult>?> SearchBookContentAsync(
        string url, string key, int maxResults, CancellationToken ct)
    {
        var filePath = ResolveLocalPath(url);
        if (filePath == null || !File.Exists(filePath)) return null;

        try
        {
            var results = new List<BookContentSearchResult>();
            if (filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            {
                var book = GetOrAddEpub(filePath);
                for (var index = 0; index < book.ReadingOrder.Count && results.Count < maxResults; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var textFile = book.ReadingOrder[index];
                    // 与目录同源的标题解析，避免搜索结果里的章节名和目录对不上。
                    var title = GetEpubChapterTitle(book, textFile.FilePath, index + 1);
                    AddChapterMatches(results, index, title,
                        ExtractEpubChapterText(textFile.Content, textFile.FilePath), key, maxResults);
                }
                return results;
            }

            if (!filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
            var txt = GetOrAddTxt(filePath);
            // 整本扫描复用同一个 FileStream：长篇动辄几千章，逐章开关文件是几千次系统调用。
            var fs = OpenTxtForReading(filePath);
            try
            {
                var sb = new StringBuilder();
                for (var index = 0; index < txt.Spans.Count && results.Count < maxResults; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var span = txt.Spans[index];
                    sb.Clear();
                    await ReadChapterSpanAsync(fs, txt, span,
                        (mem, _) => { sb.Append(mem.Span); return Task.CompletedTask; }, ct)
                        .ConfigureAwait(false);
                    AddChapterMatches(results, index, span.Title, sb.ToString(), key, maxResults);
                }
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"Book content search error: {ex.Message}");
            return null;
        }
    }

    private string ExtractEpubChapterText(string? htmlContent, string filePath)
    {
        if (string.IsNullOrEmpty(htmlContent)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);
        // 移除脚本/样式/head 元数据，避免正文混入无关内容。
        doc.DocumentNode.Descendants("script").ToList().ForEach(node => node.Remove());
        doc.DocumentNode.Descendants("style").ToList().ForEach(node => node.Remove());
        doc.DocumentNode.Descendants("head").ToList().ForEach(node => node.Remove());

        var currentDir = (Path.GetDirectoryName(filePath) ?? "").Replace("\\", "/");
        var sb = new StringBuilder();
        ExtractTextWithImages(doc.DocumentNode, sb, currentDir);
        var text = sb.ToString();
        // 占位标记里的 &lt; 等 HTML 实体一并解码；标记字符本身不在 DeEntitize 处理范围。
        return HtmlEntity.DeEntitize(text);
    }

    private static void AddChapterMatches(
        List<BookContentSearchResult> results, int chapterIndex, string chapterTitle,
        string content, string key, int maxResults)
    {
        var position = -1;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // 进度口径：按含标记原文计可见字数（与阅读器 visibleLength / CalculateContentLength 一致）。
            // 图片 / hr 行也推进 position，但不参与关键字匹配。
            position += CountVisibleChars(line) + 1;
            if (IsMarkerLine(line)) continue;

            // 搜索匹配与快照都在剥离私有标记后的纯文本上进行，
            // 避免标记字符打断关键字、或偏移与可见文本不一致。
            var plain = StripInlineMarkers(line);
            if (plain.Length == 0) continue;

            var start = 0;
            while (results.Count < maxResults)
            {
                var matchIndex = plain.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0) break;

                results.Add(new BookContentSearchResult(
                    chapterIndex, chapterTitle, position,
                    CreateSnippet(plain, matchIndex, key.Length)));
                start = matchIndex + Math.Max(key.Length, 1);
            }
            if (results.Count >= maxResults) return;
        }
    }

    private static bool IsImageMarker(string line) =>
        line.StartsWith("<img src=\"", StringComparison.Ordinal) && line.EndsWith("\">", StringComparison.Ordinal);

    private static string CreateSnippet(string line, int matchIndex, int matchLength)
    {
        const int contextLength = 36;
        var start = Math.Max(0, matchIndex - contextLength);
        var end = Math.Min(line.Length, matchIndex + matchLength + contextLength);
        // 入参通常已是纯文本；仍做一次剥离，防止调用方传入带标记串。
        var snippet = StripInlineMarkers(line[start..end]);
        return $"{(start > 0 ? "…" : "")}{snippet}{(end < line.Length ? "…" : "")}";
    }

    /// <summary>是否为阅读器专用的非文本标记行（图片占位、水平分隔线）。</summary>
    private static bool IsMarkerLine(ReadOnlySpan<char> line) =>
        IsImageMarker(line.ToString()) || line.SequenceEqual("<hr>");

    // 块级元素：遇到这些标签前后插入换行，保证段落切分正确。
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "section", "article", "blockquote", "li", "ul", "ol",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "table", "tr", "figcaption",
    };

    private void ExtractTextWithImages(HtmlNode node, StringBuilder sb, string currentDir)
    {
        if (node.Name == "img")
        {
            var src = node.GetAttributeValue("src", "");
            if (!string.IsNullOrEmpty(src))
            {
                // 处理相对路径，转换为 EPUB 内部的“绝对”路径
                string absolutePath = src;
                if (!src.StartsWith("http") && !src.StartsWith("data:"))
                {
                    try
                    {
                        // 简单的路径拼接和规范化
                        var combined = string.IsNullOrEmpty(currentDir) ? src : $"{currentDir}/{src}";
                        var uri = new Uri("http://epub/" + combined);
                        absolutePath = uri.AbsolutePath.TrimStart('/'); // 得到 OEBPS/Images/1.jpg
                    }
                    catch { }
                }
                sb.Append($"\n<img src=\"{absolutePath}\">\n");
            }
            return;
        }

        if (node.Name == "hr")
        {
            // 水平分隔线：作为独立段落输出，阅读器按分隔线样式渲染。
            sb.Append("\n<hr>\n");
            return;
        }

        // 块级语义包装：blockquote/li 渲染为带语义标记的段落，阅读器据此加引用/列表样式。
        // 用与内联强调同源的私有标记（成对字符），不污染纯文本进度口径（CountVisibleChars 剔除）。
        var blockSemantics = node.Name switch
        {
            "blockquote" => (Open: BlockQuoteStart, Close: BlockQuoteEnd),
            "li" => (Open: ListItemStart, Close: ListItemEnd),
            _ => (Open: '\0', Close: '\0'),
        };
        if (blockSemantics.Open != '\0') sb.Append(blockSemantics.Open);

        // 行内强调：用私有占位标记包裹子树文本，阅读器据此还原 <em>/<strong>。
        // 不直接输出 HTML 标签——避免污染纯文本段落与搜索快照。
        var inlineTag = node.Name switch
        {
            "em" or "i" => (Open: InlineEmStart, Close: InlineEmEnd),
            "strong" or "b" => (Open: InlineStrongStart, Close: InlineStrongEnd),
            _ => (Open: '\0', Close: '\0'),
        };
        if (inlineTag.Open != '\0') sb.Append(inlineTag.Open);

        if (BlockElements.Contains(node.Name))
        {
            sb.Append('\n');
        }

        if (node.HasChildNodes)
        {
            foreach (var child in node.ChildNodes)
            {
                ExtractTextWithImages(child, sb, currentDir);
            }
        }
        else if (node.NodeType == HtmlNodeType.Text)
        {
            sb.Append(node.InnerText);
        }

        if (BlockElements.Contains(node.Name))
        {
            sb.Append('\n');
        }

        if (inlineTag.Close != '\0') sb.Append(inlineTag.Close);
        if (blockSemantics.Close != '\0') sb.Append(blockSemantics.Close);
    }

    public (byte[] Content, string MimeType)? GetEpubResource(string bookUrl, string resourcePath)
    {
        try
        {
            var filePath = ResolveLocalPath(bookUrl);
            if (filePath == null || !File.Exists(filePath))
            {
                Console.WriteLine($"[EPUB Resource] File not found or invalid path for: {bookUrl}");
                return null;
            }

            EpubBook book = GetOrAddEpub(filePath);

            // 规范化目标路径。与 ResolveLocalPath 不同，这里必须解码：正文里的 img src
            // 由 ExtractTextWithImages 用 Uri.AbsolutePath 生成，是 percent-escaped 的。
            // 用 UnescapeDataString 而非 WebUtility.UrlDecode：后者会把 '+' 当成空格，
            // 文件名里含 '+' 的图片（a+b.jpg → a b.jpg）会匹配不上而 404。
            var targetPath = UnescapePath(resourcePath).Replace("\\", "/").Trim().TrimStart('/');
            // 处理路径中的 ../
            if (targetPath.Contains("../"))
            {
                var parts = targetPath.Split('/');
                var stack = new Stack<string>();
                foreach (var part in parts)
                {
                    if (part == "..") { if (stack.Count > 0) stack.Pop(); }
                    else if (part != "." && !string.IsNullOrEmpty(part)) stack.Push(part);
                }
                var resolvedParts = stack.ToArray();
                Array.Reverse(resolvedParts);
                targetPath = string.Join("/", resolvedParts);
            }
            var targetFileName = Path.GetFileName(targetPath);

            // 1. 优先从图片目录查找 (VersOne.Epub 自动提取的图片资源)
            if (book.Content?.Images?.Local != null)
            {
                var imageMatch = book.Content.Images.Local.FirstOrDefault(f =>
                {
                    if (string.IsNullOrEmpty(f.FilePath)) return false;
                    var fPath = f.FilePath.Replace("\\", "/").Trim().TrimStart('/');
                    // 匹配逻辑：全匹配、后缀匹配、包含匹配、文件名匹配
                    return fPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                           fPath.EndsWith("/" + targetPath, StringComparison.OrdinalIgnoreCase) ||
                           targetPath.EndsWith("/" + fPath, StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(fPath).Equals(targetFileName, StringComparison.OrdinalIgnoreCase);
                });

                if (imageMatch != null && imageMatch.Content != null)
                {
                    return (imageMatch.Content, imageMatch.ContentMimeType ?? GetMimeType(targetFileName));
                }
            }

            // 2. 备选方案：从所有本地文件查找 (处理一些未标注为 Image 的资源)
            var allFiles = book.Content?.AllFiles?.Local;
            if (allFiles != null)
            {
                var match = allFiles.FirstOrDefault(f =>
                {
                    if (string.IsNullOrEmpty(f.FilePath)) return false;
                    var fPath = f.FilePath.Replace("\\", "/").Trim().TrimStart('/');
                    return fPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
                           fPath.EndsWith("/" + targetPath, StringComparison.OrdinalIgnoreCase) ||
                           targetPath.EndsWith("/" + fPath, StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(fPath).Equals(targetFileName, StringComparison.OrdinalIgnoreCase);
                });

                if (match != null)
                {
                    if (match is EpubLocalByteContentFile byteFile && byteFile.Content != null)
                    {
                        return (byteFile.Content, byteFile.ContentMimeType ?? GetMimeType(targetFileName));
                    }

                    if (match is EpubLocalTextContentFile textFile && !string.IsNullOrEmpty(textFile.Content))
                    {
                        return (Encoding.UTF8.GetBytes(textFile.Content), textFile.ContentMimeType ?? "text/plain");
                    }
                }
            }

            Console.WriteLine($"[EPUB Resource Miss] Target: {targetPath}, FileName: {targetFileName}");
            if (allFiles != null)
            {
                var samples = allFiles.Take(10).Select(f => f.FilePath).ToList();
                Console.WriteLine($"[EPUB Resource Debug] Available files (sample): {string.Join(", ", samples)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EPUB Resource Exception] url={bookUrl} path={resourcePath} ex={ex}");
        }
        return null;
    }

    /// <summary>解 percent-escape；序列非法时原样返回，不让一个坏路径把 /image 打成 500。</summary>
    private static string UnescapePath(string path)
    {
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }

    private string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    /// <summary>按文件头魔数识别图片 MIME 类型，识别不出时回退 image/jpeg。</summary>
    public static string DetectImageMime(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            return "image/gif";
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";
        return "image/jpeg";
    }

    public byte[]? GetEpubCover(string path)
    {
        var filePath = ResolveLocalPath(path);
        if (filePath != null && File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                return book.CoverImage;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EPUB Cover Error] path={path} ex={ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// 本地书籍（EPUB 或 TXT）的显示标题，用于生成占位封面：
    /// EPUB 取元数据标题，TXT 取文件名解析出的书名。
    /// </summary>
    public string GetLocalBookTitle(string url)
    {
        if (url.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) return GetEpubTitle(url);
        var rawName = url.Replace("local://", "").Split('#')[0];
        return ParseTxtFileName(Path.GetFileNameWithoutExtension(rawName)).Name;
    }

    /// <summary>
    /// EPUB 元数据中的标题；取不到时回退到文件名（不含扩展名）。
    /// </summary>
    public string GetEpubTitle(string path)
    {
        var filePath = ResolveLocalPath(path);
        if (filePath != null && File.Exists(filePath) && filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EpubBook book = GetOrAddEpub(filePath);
                if (!string.IsNullOrWhiteSpace(book.Title)) return book.Title.Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EPUB Title Error] path={path} ex={ex.Message}");
            }
        }
        // 回退到文件名（不含扩展名）；解析失败时从原始 URL 尽力提取。
        var rawName = path.Replace("local://", "").Split('#')[0];
        return Path.GetFileNameWithoutExtension(rawName);
    }

    /// <summary>
    /// 用标题文字生成一张 SVG 占位封面。Native AOT 下不依赖 System.Drawing 等原生图像库，
    /// 直接拼字符串即可，矢量渲染在任意分辨率都清晰。
    /// </summary>
    public static byte[] GenerateTitleCoverSvg(string title)
    {
        const int width = 300;
        const int height = 400;

        // 稳定地由标题派生一个背景色，让不同书籍的占位封面有区分度。
        // 用掩码取正而非 Math.Abs：hash 恰为 int.MinValue 时 Math.Abs 会抛 OverflowException。
        int hash = 0;
        foreach (var ch in title) hash = unchecked(hash * 31 + ch);
        int hue = (hash & 0x7FFFFFFF) % 360;

        var lines = WrapTitle(title, 8, 5);
        // 垂直居中排布文本块。
        const int lineHeight = 42;
        double blockHeight = lines.Count * lineHeight;
        double startY = (height - blockHeight) / 2 + lineHeight * 0.75;

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(width)
          .Append("\" height=\"").Append(height)
          .Append("\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\">");
        sb.Append("<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">")
          .Append("<stop offset=\"0\" stop-color=\"hsl(").Append(hue).Append(",55%,42%)\"/>")
          .Append("<stop offset=\"1\" stop-color=\"hsl(").Append((hue + 40) % 360).Append(",60%,28%)\"/>")
          .Append("</linearGradient></defs>");
        sb.Append("<rect width=\"").Append(width).Append("\" height=\"").Append(height).Append("\" fill=\"url(#g)\"/>");
        sb.Append("<rect x=\"12\" y=\"12\" width=\"").Append(width - 24).Append("\" height=\"").Append(height - 24)
          .Append("\" fill=\"none\" stroke=\"rgba(255,255,255,0.35)\" stroke-width=\"2\"/>");
        sb.Append("<text x=\"").Append(width / 2)
          .Append("\" text-anchor=\"middle\" fill=\"#ffffff\" font-family=\"serif\" font-size=\"32\" font-weight=\"bold\">");
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append("<tspan x=\"").Append(width / 2)
              .Append("\" y=\"").Append((int)(startY + i * lineHeight)).Append("\">")
              .Append(EscapeXml(lines[i])).Append("</tspan>");
        }
        sb.Append("</text></svg>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static List<string> WrapTitle(string title, int charsPerLine, int maxLines)
    {
        title = title.Trim();
        var lines = new List<string>();
        for (int i = 0; i < title.Length && lines.Count < maxLines; i += charsPerLine)
        {
            lines.Add(title.Substring(i, Math.Min(charsPerLine, title.Length - i)));
        }
        // 文本被截断时在末行加省略号。
        if (lines.Count == maxLines && maxLines * charsPerLine < title.Length)
        {
            var last = lines[maxLines - 1];
            lines[maxLines - 1] = (last.Length > charsPerLine - 1 ? last.Substring(0, charsPerLine - 1) : last) + "…";
        }
        if (lines.Count == 0) lines.Add(title);
        return lines;
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>
    /// 「删除」本地书籍：不硬删，移入 books/.trash/ 回收站（同名时加序号），
    /// 用户可手动恢复。前端在「搜索书拒绝入库」流程也会调 deleteBook，
    /// 硬删会让一次误点直接毁掉书籍文件。返回是否实际移动了文件。
    /// </summary>
    public bool DeleteLocalBook(string bookUrl)
    {
        var filePath = ResolveLocalPath(bookUrl);
        if (filePath == null || !File.Exists(filePath)) return false;

        var trashDir = Path.Combine(_booksDir, ".trash");
        Directory.CreateDirectory(trashDir);

        var target = Path.Combine(trashDir, Path.GetFileName(filePath));
        for (int i = 1; File.Exists(target); i++)
        {
            target = Path.Combine(trashDir,
                $"{Path.GetFileNameWithoutExtension(filePath)}({i}){Path.GetExtension(filePath)}");
        }
        File.Move(filePath, target);

        // 让缓存立即失效，避免已删除的书还能翻页。
        _epubCache.TryRemove(filePath, out _);
        _txtCache.TryRemove(filePath, out _);
        _shelfMetaCache.TryRemove(filePath, out _);
        Console.WriteLine($"[DeleteLocalBook] moved to trash: {Path.GetFileName(filePath)}");
        return true;
    }

    /// <summary>
    /// 保存上传的书籍文件到 books/。仅接受 .txt/.epub 扩展名，文件名剥离目录部分防穿越；
    /// 目标已存在且未指定覆盖时返回失败。返回 (是否成功, 消息, 新书 BookUrl)。
    /// </summary>
    public async Task<(bool Ok, string Message, string BookUrl)> SaveUploadedBookAsync(
        string fileName, Stream content, bool overwrite)
    {
        fileName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrEmpty(fileName)) return (false, "缺少文件名", "");

        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "仅支持 .txt 与 .epub 文件", "");
        }

        var bookUrl = $"local://{fileName}";
        var filePath = ResolveLocalPath(bookUrl);
        if (filePath == null) return (false, "非法文件名", "");

        var uploadLock = AcquireUploadLock(filePath);
        await uploadLock.Gate.WaitAsync();
        try
        {
            // 在同名上传串行锁内检查，避免两个非覆盖请求同时通过检查。
            if (File.Exists(filePath) && !overwrite)
                return (false, "同名书籍已存在（可用 overwrite=true 覆盖）", "");

            // 临时文件必须唯一且与目标同目录：既避免并发相互覆盖，又保证移动在同一文件系统内。
            var tmpPath = $"{filePath}.{Guid.NewGuid():N}.uploading";
            try
            {
                await using (var fs = File.Create(tmpPath))
                {
                    await content.CopyToAsync(fs);
                }
                File.Move(tmpPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }

            _epubCache.TryRemove(filePath, out _);
            _txtCache.TryRemove(filePath, out _);
            _shelfMetaCache.TryRemove(filePath, out _);
            Console.WriteLine($"[UploadBook] saved: {fileName}");
            return (true, "", bookUrl);
        }
        finally
        {
            uploadLock.Gate.Release();
            ReleaseUploadLock(filePath, uploadLock);
        }
    }

    private UploadLock AcquireUploadLock(string filePath)
    {
        lock (_uploadLocksLock)
        {
            if (!_uploadLocks.TryGetValue(filePath, out var uploadLock))
            {
                uploadLock = new UploadLock();
                _uploadLocks.Add(filePath, uploadLock);
            }
            uploadLock.ReferenceCount++;
            return uploadLock;
        }
    }

    private void ReleaseUploadLock(string filePath, UploadLock uploadLock)
    {
        lock (_uploadLocksLock)
        {
            uploadLock.ReferenceCount--;
            if (uploadLock.ReferenceCount == 0)
            {
                _uploadLocks.Remove(filePath);
                uploadLock.Gate.Dispose();
            }
        }
    }
}
