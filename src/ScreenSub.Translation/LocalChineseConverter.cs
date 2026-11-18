using System.Runtime.InteropServices;
using System.Text;

namespace ScreenSub.Translation;

/// <summary>
/// High performance offline converter for Traditional and Simplified Chinese characters.
/// </summary>
public static class LocalChineseConverter
{
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        StringBuilder? lpDestStr,
        int cchDest,
        nint lpVersionInformation,
        nint lpReserved,
        nint sortHandle);
    private static readonly Dictionary<char, char> TradToSimp = new()
    {
        ['嗎'] = '吗', ['體'] = '体', ['點'] = '点', ['經'] = '经', ['國'] = '国',
        ['說'] = '说', ['時'] = '时', ['間'] = '间', ['們'] = '们', ['這'] = '这',
        ['樣'] = '样', ['會'] = '会', ['個'] = '个', ['為'] = '为', ['對'] = '对',
        ['現'] = '现', ['發'] = '发', ['學'] = '学', ['開'] = '开', ['關'] = '关',
        ['機'] = '机', ['動'] = '动', ['電'] = '电', ['話'] = '话', ['頭'] = '头',
        ['買'] = '买', ['賣'] = '卖', ['東'] = '东', ['西'] = '西', ['車'] = '车',
        ['書'] = '书', ['報'] = '报', ['紙'] = '纸', ['筆'] = '笔', ['畫'] = '画',
        ['寫'] = '写', ['問'] = '问', ['題'] = '题', ['見'] = '见', ['親'] = '亲',
        ['愛'] = '爱', ['錢'] = '钱', ['門'] = '门', ['長'] = '长', ['張'] = '张',
        ['歡'] = '欢', ['樂'] = '乐', ['過'] = '过', ['進'] = '进', ['遠'] = '远',
        ['邊'] = '边', ['還'] = '还', ['從'] = '从', ['來'] = '来', ['聽'] = '听',
        ['認'] = '认', ['識'] = '识', ['謝'] = '谢', ['請'] = '请', ['誰'] = '谁',
        ['讓'] = '让', ['幾'] = '几', ['後'] = '后', ['裏'] = '里', ['麵'] = '面',
        ['簡'] = '简', ['華'] = '华', ['語'] = '语', ['文'] = '文',
        ['參'] = '参', ['試'] = '试', ['議'] = '议', ['軟'] = '软', ['實'] = '实',
        ['氣'] = '气', ['務'] = '务', ['離'] = '离', ['辦'] = '办', ['閉'] = '闭',
        ['腦'] = '脑', ['師'] = '师', ['視'] = '视', ['頻'] = '频', ['聲'] = '声',
        ['譯'] = '译', ['網'] = '网', ['絡'] = '络', ['數'] = '数', ['據'] = '据',
        ['庫'] = '库', ['統'] = '统', ['檢'] = '检', ['測'] = '测', ['迎'] = '迎',
        ['員'] = '员', ['連'] = '连', ['線'] = '线', ['設'] = '设', ['備'] = '备',
        ['處'] = '处', ['理'] = '理', ['顯'] = '显', ['錄'] = '录', ['音'] = '音'
    };

    private static readonly Dictionary<char, char> SimpToTrad = TradToSimp
        .GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(TradToSimp.TryGetValue(c, out char s) ? s : c);
        }
        string dictMapped = sb.ToString();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var outSb = new StringBuilder(dictMapped.Length);
                int len = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, dictMapped, dictMapped.Length, outSb, dictMapped.Length, nint.Zero, nint.Zero, nint.Zero);
                if (len > 0) return outSb.ToString(0, len);
            }
            catch { }
        }
        return dictMapped;
    }

    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(SimpToTrad.TryGetValue(c, out char t) ? t : c);
        }
        string dictMapped = sb.ToString();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var outSb = new StringBuilder(dictMapped.Length);
                int len = LCMapStringEx("zh-TW", LCMAP_TRADITIONAL_CHINESE, dictMapped, dictMapped.Length, outSb, dictMapped.Length, nint.Zero, nint.Zero, nint.Zero);
                if (len > 0) return outSb.ToString(0, len);
            }
            catch { }
        }
        return dictMapped;
    }
}
