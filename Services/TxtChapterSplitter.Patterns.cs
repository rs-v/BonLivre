using System.Text.RegularExpressions;

namespace BonLivre.Services;

/// <summary>
/// 分族识别用的正则与编号解析。每族一条小正则（取代此前那条约 2000 字符的巨型正则）：
/// 单条可读、可单测，且 <c>head</c> 组给出「编号部分」的长度，供标题与正文同行时截断。
/// </summary>
internal static partial class TxtChapterSplitter
{
    // 第X章/节/回/集/话/幕（普通章）与 第X卷/部/篇/册（分卷）。编号与单位间允许空格。
    // 也允许一段区间（第1-4章）：num 取起点参与编号校验，end 为终点。
    [GeneratedRegex(
        @"^(?<head>第[ \t　]*(?<num>[零〇○一二三四五六七八九十百千万萬亿億两兩廿卅卌壹贰貳叁參肆伍陆陸柒捌玖拾佰仟0-9０-９IVXLCDM]{1,16})(?:[ \t　]*[-–—－~～〜至到][ \t　]*(?<end>[零〇○一二三四五六七八九十百千万萬亿億两兩廿卅卌壹贰貳叁參肆伍陆陸柒捌玖拾佰仟0-9０-９IVXLCDM]{1,16}))?[ \t　]*(?<unit>[章节節回集话話幕卷部篇册冊]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex DiChapterRegex();

    // 卷X / 部X / 篇X / 册X（前置单位的分卷写法）。
    [GeneratedRegex(
        @"^(?<head>(?<unit>[卷部篇册冊])[ \t　]*(?<num>[零〇○一二三四五六七八九十百千万萬两兩廿卅卌壹贰貳叁參肆伍陆陸柒捌玖拾佰仟0-9０-９]{1,16}))",
        RegexOptions.CultureInvariant)]
    private static partial Regex VolumePrefixRegex();

    // Chapter N / Book N / Part N / Volume N，编号可为阿拉伯数字、罗马数字或英文序数词。
    [GeneratedRegex(
        @"^(?<head>(?<kind>Chapter|Book|Part|Volume)[ \t　]+(?<num>[0-9０-９]{1,5}|[IVXLCDM]{1,12}|One|Two|Three|Four|Five|Six|Seven|Eight|Nine|Ten|Eleven|Twelve|Thirteen|Fourteen|Fifteen|Sixteen|Seventeen|Eighteen|Nineteen|Twenty|First|Second|Third|Fourth|Fifth|Sixth|Seventh|Eighth|Ninth|Tenth|Eleventh|Twelfth|Thirteenth|Fourteenth|Fifteenth|Sixteenth|Seventeenth|Eighteenth|Nineteenth|Twentieth))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnChapterRegex();

    // 番外 / 番外篇 / 番外三 / 番一。「番」后必须是「外」或数字，避免吃掉「番茄」这类正文行。
    [GeneratedRegex(
        @"^(?<head>番(?:外篇?|(?=[零〇○一二三四五六七八九十百千万两0-9０-９]))[ \t　]*(?<num>[零〇○一二三四五六七八九十百千万两0-9０-９]{1,8})?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FanRegex();

    // 元信息短词。后缀必须由分隔符引出，否则「正文内容」「序言里写道」会被误当标题。
    [GeneratedRegex(
        @"^(?<head>完本感言|作品相关|内容提要|内容简介|作者的话|大结局|序章|序言|序幕|楔子|前言|引子|导言|引言|正文|后记|後記|后序|尾声|尾聲|终章|終章|结语|結語|外传|外傳|附录|附錄|简介|簡介|文案|导读|導讀|序|跋|Introduction|Prologue|Epilogue|Preface|Afterword|Appendix)(?:(?:[ \t　]+|[:：、.．·\-—–]+)[^\r\n]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    // （一）/ (1) / [一] / 〔2〕 / 【3】 等括号编号。
    // 含区间写法 (1-4)、（一至三）：num 取起点。
    [GeneratedRegex(
        @"^(?<head>[\(（\[［〔〖【][ \t　]*(?<num>[零〇○一二三四五六七八九十百千万两0-9０-９]{1,8})(?:[ \t　]*[-–—－~～〜至到][ \t　]*(?<end>[零〇○一二三四五六七八九十百千万两0-9０-９]{1,8}))?[ \t　]*[\)）\]］〕〗】])",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketRegex();

    // 一、标题 / 三．标题
    [GeneratedRegex(
        @"^(?<head>(?<num>[零〇○一二三四五六七八九十百千万两廿卅卌]{1,8})[、．.·:：])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChineseNumPunctRegex();

    // 1. 标题 / 1、标题。点后紧跟数字不算（3.14、版本号 1.2.3）。
    [GeneratedRegex(
        @"^(?<head>(?<num>[0-9０-９]{1,5})[、．.·:：](?![0-9０-９]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArabicDotRegex();

    // 1 标题 / 1｜标题。标题不以数字起首，避免吃掉「1 2 3」这类表格行。
    [GeneratedRegex(
        @"^(?<head>(?<num>[0-9０-９]{1,5}))(?:[ \t　]+(?![0-9０-９])|[ \t　]*[｜|][ \t　]*)[^\r\n]{1,80}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArabicSpaceRegex();

    // 整行只有一个编号：123 或 三十七。
    [GeneratedRegex(
        @"^(?<head>(?<num>[0-9０-９]{1,5}|[零〇○一二三四五六七八九十百千万两廿卅卌]{1,8}))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BareNumberRegex();

    // ===== 编号解析 =====

    /// <summary>
    /// 把标题里的编号解析成数值，供「是否递增 / 是否连号」的反误切校验使用。
    /// 支持半角与全角阿拉伯数字、中文小写与大写数字（含 两/廿/卅/卌）、罗马数字、英文序数词。
    /// 解析不出时返回 -1（该候选不参与编号类校验）。
    /// </summary>
    private static long ParseNumber(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        if (s.IsEmpty) return -1;

        if (TryParseDigits(s, out var digits)) return digits;
        if (TryParseChinese(s, out var chinese)) return chinese;
        if (TryParseRoman(s, out var roman)) return roman;
        return ParseEnglishWord(s);
    }

    private static bool TryParseDigits(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        foreach (var ch in s)
        {
            var d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= '０' and <= '９' => ch - '０',
                _ => -1,
            };
            if (d < 0) { value = 0; return false; }
            if (value > 100_000_000) { value = 100_000_000; return true; }
            value = value * 10 + d;
        }
        return true;
    }

    private static int ChineseDigitValue(char ch) => ch switch
    {
        '零' or '〇' or '○' => 0,
        '一' or '壹' => 1,
        '二' or '贰' or '貳' or '两' or '兩' => 2,
        '三' or '叁' or '參' => 3,
        '四' or '肆' => 4,
        '五' or '伍' => 5,
        '六' or '陆' or '陸' => 6,
        '七' or '柒' => 7,
        '八' or '捌' => 8,
        '九' or '玖' => 9,
        _ => -1,
    };

    private static int ChineseUnitValue(char ch) => ch switch
    {
        '十' or '拾' => 10,
        '百' or '佰' => 100,
        '千' or '仟' => 1000,
        '万' or '萬' => 10_000,
        '亿' or '億' => 100_000_000,
        _ => -1,
    };

    private static bool TryParseChinese(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        long total = 0, section = 0, number = 0;
        var sawAny = false;
        var allDigits = true;   // 形如「二〇二四」的逐位读法

        foreach (var ch in s)
        {
            if (ch is '廿' or '卅' or '卌')
            {
                section += ch == '廿' ? 20 : ch == '卅' ? 30 : 40;
                number = 0;
                sawAny = true;
                allDigits = false;
                continue;
            }

            var d = ChineseDigitValue(ch);
            if (d >= 0) { number = d; sawAny = true; continue; }

            var u = ChineseUnitValue(ch);
            if (u < 0) return false;
            allDigits = false;
            sawAny = true;

            if (u >= 10_000)
            {
                total = (total + section + number) * u;
                section = 0;
                number = 0;
            }
            else
            {
                if (number == 0 && u == 10) number = 1;   // 十五 → 15
                section += number * u;
                number = 0;
            }
        }

        if (!sawAny) return false;

        if (allDigits && s.Length > 1)
        {
            // 「二〇二四」按位拼读，而不是 2+0+2+4。
            value = 0;
            foreach (var ch in s)
            {
                if (value > 100_000_000) break;
                value = value * 10 + ChineseDigitValue(ch);
            }
            return true;
        }

        value = total + section + number;
        return true;
    }

    private static bool TryParseRoman(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        var prev = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var v = char.ToUpperInvariant(s[i]) switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                'D' => 500,
                'M' => 1000,
                _ => -1,
            };
            if (v < 0) { value = 0; return false; }
            value += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return value > 0;
    }

    private static readonly string[] EnglishOrdinals =
    [
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
        "eighteen", "nineteen", "twenty",
    ];

    private static readonly string[] EnglishOrdinalsAlt =
    [
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
        "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth",
        "eighteenth", "nineteenth", "twentieth",
    ];

    private static long ParseEnglishWord(ReadOnlySpan<char> s)
    {
        for (var i = 0; i < EnglishOrdinals.Length; i++)
        {
            if (s.Equals(EnglishOrdinals[i], StringComparison.OrdinalIgnoreCase) ||
                s.Equals(EnglishOrdinalsAlt[i], StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }
        return -1;
    }
}
