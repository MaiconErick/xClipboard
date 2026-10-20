using System.Globalization;
using System.Text.RegularExpressions;

namespace xClipboard;

public enum RefundField { OrderId, ProductName, Price, Date, Status, Reason }

/// <summary>
/// Guesses which refund-sheet field a copied piece of text belongs to. Strong shapes first (labels, order IDs,
/// dates, prices, statuses), then a product-name vs reason heuristic for free text.
/// </summary>
public static class RefundParser
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Regex LabelRx = new(
        @"^\s*(?<label>order\s*(?:id|number|no\.?|#)|invoice\s*(?:id|number|#)|transaction\s*id|product(?:\s*name)?|item(?:\s*name)?|asset(?:\s*name)?|price|amount|total|cost|date\s*of\s*purchase|purchase\s*date|order\s*date|date|refund\s*status|status|reason(?:\s*for\s*refund)?|refund\s*reason)\s*[:#\-–]\s*(?<value>.+?)\s*$", I);

    static readonly Regex OrderRx = new(@"^#?[A-Z0-9][A-Z0-9\-_]{6,39}$", I);

    const string Month = @"(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?";
    static readonly Regex NumericDateRx = new(@"^(?:[a-z]{3,9},?\s+)?(?<d>\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4})(?:[\sT,]+\d{1,2}:\d{2}(?::\d{2})?\s*(?:am|pm)?(?:\s*[a-z]{2,5})?)?$", I);
    static readonly Regex IsoDateRx = new(@"^(?<d>\d{4}[/.\-]\d{1,2}[/.\-]\d{1,2})(?:[\sT,]+\d{1,2}:\d{2}.*)?$", I);
    static readonly Regex WordDateRx = new(
        $@"^(?:[a-z]{{3,9}},?\s+)?(?<d>{Month}\s+\d{{1,2}}(?:st|nd|rd|th)?,?\s+\d{{4}}|\d{{1,2}}(?:st|nd|rd|th)?\s+(?:of\s+)?{Month},?\s+\d{{4}})(?:,?\s+(?:at\s+)?\d{{1,2}}:\d{{2}}.*)?$", I);

    static readonly Regex PriceRx = new(
        @"^(?:(?<code>[A-Z]{3})\s*)?(?<sym>US\$|R\$|C\$|A\$|NZ\$|HK\$|[$€£¥₩₹₽₺])?\s*-?\s*\d{1,3}(?:[,.\s]?\d{3})*(?:[.,]\d{1,2})?\s*(?<code2>[A-Z]{3})?$", RegexOptions.CultureInvariant);
    static readonly Regex DecimalRx = new(@"^\d{1,6}[.,]\d{2}$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Currencies = ["USD", "EUR", "GBP", "BRL", "JPY", "KRW", "CAD", "AUD", "NZD", "CNY", "INR", "RUB", "TRY", "MXN", "CHF", "SEK", "NOK", "DKK", "PLN", "HKD", "SGD"];

    static readonly Regex StatusRx = new(
        @"\b(refunded|refund(?:ed)?\s+(?:issued|approved|denied|declined|pending|requested|completed|processed|rejected)|approved|pending|denied|declined|rejected|completed|complete|processing|processed|requested|cancell?ed|accepted|resolved|closed|open|eligible|ineligible|in\s+review|under\s+review|on\s+hold|escalated|partial(?:ly)?(?:\s+refunded)?|not\s+refunded|charged\s*back|chargeback|disputed|awaiting\s+\w+|no\s+refund)\b", I);

    static readonly Regex ReasonWordsRx = new(
        @"\b(i|i'm|i've|i'd|my|me|we|our|us|you|it|it's|this|that|didn't|doesn't|don't|can't|cannot|won't|isn't|wasn't|not|no|because|since|but|wrong|mistake|mistakenly|accident|accidentally|bought|purchased|purchase|ordered|want|wanted|need|needed|work|works|working|broken|crash|crashes|bug|bugs|missing|expected|described|compatible|compatibility|duplicate|twice|instead|already|refund|please|thanks|thank|customer|unable|issue|problem|quality|was|were|is|are|does|did|had|have|has)\b", I);
    static readonly Regex ProductWordsRx = new(
        @"\b(pack|asset|assets|kit|bundle|template|plugin|tool|tools|toolkit|system|collection|edition|series|set|library|framework|shader|shaders|vfx|sfx|ui|gui|hud|icons|sprites|textures|materials|models|characters|environment|low\s*poly|stylized|ultimate|pro|lite|deluxe|premium|complete|mega|vol\.?|volume|v\d+(?:\.\d+)*|\d+d)\b", I);
    /// <summary>Typical short refund reasons ("Wrong item", "Duplicate purchase", "Changed my mind"…).</summary>
    static readonly Regex ReasonPhraseRx = new(
        @"^(wrong|duplicate|double|accidental(ly)?|accident|mistake|mistaken(ly)?|by\s+mistake|changed\s+(my\s+)?mind|no\s+longer|not\s|doesn'?t|didn'?t|don'?t|does\s+not|did\s+not|can'?t|cannot|unable|incompatible|missing|broken|corrupt(ed)?|unauthori[sz]ed|fraud(ulent)?|bought|purchased|ordered|charged|quality|poor|bad|defective|outdated|deprecated|misleading)\b", I);

    static readonly HashSet<string> SmallWords = ["a", "an", "the", "of", "for", "and", "or", "with", "in", "on", "to", "by", "at", "from", "&", "-", "–", "|", "+", "x"];

    /// <summary>Classify a clipboard text. Several results when it holds several recognisable pieces (lines/tabs).</summary>
    public static List<(RefundField field, string value)> Classify(string raw)
    {
        var result = new List<(RefundField, string)>();
        var text = raw.Replace("\r", "").Trim();
        if (text.Length == 0 || text.Length > 4000) return result;

        var pieces = text.Split(['\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).ToList();
        if (pieces.Count > 1)
        {
            var strong = pieces.Select(p => (p, f: Strong(p))).ToList();
            // A block/row of recognisable values (e.g. a copied table row, or a labelled block) is split up.
            if (strong.Count(s => s.f != null) * 2 >= pieces.Count)
            {
                foreach (var (p, f) in strong)
                    result.Add(f ?? (LooksLikeReason(p) ? RefundField.Reason : RefundField.ProductName, p));
                return result.Select(r => (r.Item1, Clean(r.Item1, r.Item2))).ToList();
            }
            // Otherwise a multi-line copy is a reason/note.
            result.Add((RefundField.Reason, text));
            return result;
        }

        var single = Strong(text) ?? (LooksLikeReason(text) ? RefundField.Reason : RefundField.ProductName, text);
        result.Add((single.Item1, Clean(single.Item1, single.Item2)));
        return result;
    }

    /// <summary>Recognisable shapes; null for free text.</summary>
    static (RefundField, string)? Strong(string s)
    {
        s = s.Trim();
        var lm = LabelRx.Match(s);
        if (lm.Success)
        {
            var label = Regex.Replace(lm.Groups["label"].Value.ToLowerInvariant(), @"\s+", " ");
            var value = lm.Groups["value"].Value.Trim();
            RefundField? f =
                label.Contains("status") ? RefundField.Status :
                label.Contains("reason") ? RefundField.Reason :
                label.Contains("date") ? RefundField.Date :
                label is "price" or "amount" or "total" or "cost" ? RefundField.Price :
                label.StartsWith("product") || label.StartsWith("item") || label.StartsWith("asset") ? RefundField.ProductName :
                label.StartsWith("order") || label.StartsWith("invoice") || label.StartsWith("transaction") ? RefundField.OrderId : null;
            if (f != null && value.Length > 0) return (f.Value, value);
        }

        if (s.Length <= 48 && (NumericDateRx.IsMatch(s) || IsoDateRx.IsMatch(s) || WordDateRx.IsMatch(s))) return (RefundField.Date, s);
        if (IsPrice(s)) return (RefundField.Price, s);
        if (IsOrderId(s)) return (RefundField.OrderId, s);
        if (s.Length <= 50 && Words(s).Count <= 6 && StatusRx.IsMatch(s)) return (RefundField.Status, s);
        return null;
    }

    static bool IsOrderId(string s)
    {
        if (!OrderRx.IsMatch(s)) return false;
        var core = s.TrimStart('#');
        int digits = core.Count(char.IsDigit), letters = core.Count(char.IsLetter);
        if (digits == 0) return false;
        // A2607212249255253, 1234567890, ORD-2026-000123 — mostly digits, or a long mixed code.
        return digits >= core.Length * 0.6 || (digits >= 4 && core.Length >= 10 && letters <= core.Length * 0.6);
    }

    static bool IsPrice(string s)
    {
        if (s.Length > 30) return false;
        if (DecimalRx.IsMatch(s)) return true;
        var m = PriceRx.Match(s);
        if (!m.Success) return false;
        bool sym = m.Groups["sym"].Success;
        bool code = (m.Groups["code"].Success && Currencies.Contains(m.Groups["code"].Value)) ||
                    (m.Groups["code2"].Success && Currencies.Contains(m.Groups["code2"].Value));
        return sym || code;
    }

    static List<string> Words(string s) => s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Free text: a sentence-like complaint is the reason, a Title Cased name is a product.</summary>
    public static bool LooksLikeReason(string s)
    {
        var words = Words(s);
        if (words.Count == 0) return false;
        int score = 0;
        if (ReasonPhraseRx.IsMatch(s.Trim())) score += 3;
        if (Regex.IsMatch(s, @"[.!?]\s*$")) score += 2;
        if (words.Count > 14) score += 2; else if (words.Count > 9) score += 1;
        score += Math.Min(3, ReasonWordsRx.Matches(s).Count);

        var real = words.Where(w => w.Any(char.IsLetter) && !SmallWords.Contains(w.ToLowerInvariant())).ToList();
        if (real.Count > 0)
        {
            double capital = real.Count(w => char.IsUpper(w.First(char.IsLetter))) / (double)real.Count;
            if (capital >= 0.7) score -= 3;
            else if (capital < 0.4) score += 2;
        }
        if (Regex.IsMatch(s, @"\s[-–|:]\s") || ProductWordsRx.IsMatch(s)) score -= 2;
        if (words.Count <= 2 && !Regex.IsMatch(s, @"[.!?]$")) score -= 1;
        return score >= 2;
    }

    /// <summary>Tidy a value for its field (dates become MM/dd/yyyy when unambiguous).</summary>
    static string Clean(RefundField f, string v)
    {
        v = Regex.Replace(v.Trim(), @"[ \t]+", " ");
        if (f == RefundField.Date) return CleanDate(v);
        if (f == RefundField.OrderId) return v.TrimStart('#').Trim();
        return v;
    }

    static string CleanDate(string v)
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        var m = NumericDateRx.Match(v);
        if (m.Success) return m.Groups["d"].Value; // keep the site's own numeric order (07/21/2026)
        m = IsoDateRx.Match(v);
        if (m.Success && DateTime.TryParse(m.Groups["d"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso.ToString("MM/dd/yyyy", en);
        m = WordDateRx.Match(v);
        if (m.Success)
        {
            var d = Regex.Replace(m.Groups["d"].Value, @"(\d)(st|nd|rd|th)", "$1", I).Replace(" of ", " ").Replace(".", "");
            d = Regex.Replace(d, @"\bSept\b", "Sep", I);
            if (DateTime.TryParse(d, en, DateTimeStyles.AllowWhiteSpaces, out var w)) return w.ToString("MM/dd/yyyy", en);
        }
        return v;
    }
}
