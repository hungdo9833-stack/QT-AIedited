using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Real Gemini runs quoted correctly but reported wrong UTF-16 offsets or LF instead of CRLF.</summary>
public sealed class SemanticSpanAnchoringTests
{
    private const string Crlf = "柳轻锋又有了那种熟悉的感觉，他知道自己又\r\n在做梦了。林青站在门口。\r\n柳轻锋没有回答。";

    private static (SemanticFixture S, SemanticSurveyRequest R) Single(string text = Crlf) { var s = new SemanticFixture(text); return (s, s.Plan().Single()); }
    private static SemanticAuditedFinding Audit(SemanticFixture s, SemanticSurveyRequest r, SemanticFinding f) =>
        SemanticQualityAuditor.Audit(SemanticFixture.Response(r, f), r, s.Sources).Findings.Single();
    private static SemanticFinding WithSpan(SemanticFinding f, SemanticSurveyRequest r, int start, int length, string quote) =>
        f with { SupportSpans = [new(r.Evidence.Id, start, length, quote)] };
    private static string Source(SemanticFixture s, SemanticSupport span) => s.Sources.Single().OriginalText.Substring(span.StartUtf16, span.LengthUtf16);

    [Fact]
    public void Wrong_offset_with_verbatim_quote_is_relocated_inside_evidence_and_stored_without_text()
    {
        var (s, r) = Single(); var finding = WithSpan(s.Name(r, "林青"), r, 296 % Crlf.Length, 2, "林青");
        var audited = Audit(s, r, finding);
        Assert.True(audited.EvidenceValid); Assert.DoesNotContain("SupportSpanMismatch", audited.Issues);
        var span = Assert.Single(audited.Finding.SupportSpans);
        Assert.Equal("", span.Text); // Never persist source quotes.
        Assert.Equal(Crlf.IndexOf("林青", StringComparison.Ordinal), span.StartUtf16); Assert.Equal("林青", Source(s, span));
    }

    [Fact]
    public void Repeated_name_takes_the_occurrence_nearest_the_reported_offset()
    {
        var (s, r) = Single(); int second = Crlf.LastIndexOf("柳轻锋", StringComparison.Ordinal);
        var near = Audit(s, r, WithSpan(s.Name(r, "柳轻锋"), r, second - 3, 3, "柳轻锋"));
        Assert.True(near.EvidenceValid); Assert.Equal(second, near.Finding.SupportSpans[0].StartUtf16);
        var first = Audit(s, r, WithSpan(s.Name(r, "柳轻锋"), r, 1, 3, "柳轻锋"));
        Assert.Equal(0, first.Finding.SupportSpans[0].StartUtf16);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void Line_break_style_in_quote_is_tolerated_and_range_uses_the_source_crlf(string modelBreak)
    {
        var (s, r) = Single(); string quote = "他知道自己又" + modelBreak + "在做梦了";
        var finding = WithSpan(s.Name(r, "柳轻锋"), r, 12, quote.Length, quote) with
        {
            FindingType = SemanticFindingType.CharacterIdentification, Value = "柳轻锋",
            SupportSpans = [new(r.Evidence.Id, 0, 3, "柳轻锋"), new(r.Evidence.Id, 12, quote.Length, quote)]
        };
        var audited = Audit(s, r, finding);
        Assert.True(audited.EvidenceValid, string.Join(",", audited.Issues));
        var span = audited.Finding.SupportSpans[1];
        Assert.Equal("他知道自己又\r\n在做梦了", Source(s, span)); Assert.Equal(Crlf.IndexOf("他知道", StringComparison.Ordinal), span.StartUtf16);
    }

    [Theory]
    [InlineData("林清")]             // One character changed: fabricated quote.
    [InlineData("林 青")]            // Whitespace is NOT tolerated, only line breaks.
    [InlineData("他知道自己又 在做梦了")] // A space in place of the source line break is not a line break.
    [InlineData("林\n青")]           // A line break the quote invents must exist in the source.
    [InlineData("他知道自己在做梦了")]  // One character dropped near the line break.
    [InlineData("王秀兰")]           // Not in the source at all.
    public void Non_verbatim_quotes_still_fail_with_support_span_mismatch(string quote)
    {
        var (s, r) = Single();
        var audited = Audit(s, r, WithSpan(s.Name(r, "林青"), r, Crlf.IndexOf("林青", StringComparison.Ordinal), quote.Length, quote));
        Assert.False(audited.EvidenceValid); Assert.Contains("SupportSpanMismatch", audited.Issues); Assert.False(audited.CanPromote);
    }

    [Theory]
    [InlineData("他知道自己又在做梦了")]       // Real Gemini case: hard-wrapped CRLF omitted mid-sentence.
    [InlineData("自己又在做梦")]
    public void Quote_may_omit_a_source_line_break_between_two_quoted_characters(string quote)
    {
        var (s, r) = Single();
        var finding = s.Name(r, "柳轻锋") with { SupportSpans = [new(r.Evidence.Id, 0, 3, "柳轻锋"), new(r.Evidence.Id, 30, 8, quote)] };
        var audited = Audit(s, r, finding);
        Assert.True(audited.EvidenceValid, string.Join(",", audited.Issues));
        var span = audited.Finding.SupportSpans[1];
        Assert.Equal(quote.Replace("又在", "又\r\n在", StringComparison.Ordinal), Source(s, span)); // Stored range covers the real CRLF.
    }

    [Fact]
    public void Omitted_blank_line_paragraph_break_is_tolerated_but_not_at_quote_edges()
    {
        const string text = "林青说完了。\r\n\r\n周宁点头。";
        var (s, r) = Single(text);
        var joined = Audit(s, r, WithSpan(s.Name(r, "林青"), r, 99, 11, "林青说完了。周宁点头。"));
        Assert.True(joined.EvidenceValid, string.Join(",", joined.Issues)); Assert.Equal(text, Source(s, joined.Finding.SupportSpans[0]));
        // The located range never starts or ends on a line break the quote did not contain.
        var edge = Audit(s, r, WithSpan(s.Name(r, "林青"), r, 0, 2, "林青")).Finding.SupportSpans[0];
        Assert.Equal("林青", Source(s, edge));
        Assert.Equal("(?:\r\n|\n|\r)*", SemanticQualityAuditor.QuotePattern("ab")[1..^1]);
    }

    [Fact]
    public void Quote_is_never_located_in_another_evidence_than_the_declared_one()
    {
        var s = new SemanticFixture(AdaptiveSemanticTests.Window("林青说道：我叫林青。") + AdaptiveSemanticTests.Window("周宁来了。周宁点头。"));
        var r = AdaptiveSemanticTests.Plan(s, 2).Single(); Assert.Equal(2, r.Units.Count);
        var first = r.Units[0]; var second = r.Units[1];
        // "周宁" exists only in the second evidence; declared evidence is the first one.
        var finding = AdaptiveSemanticTests.Name(first) with { Value = "周宁", SupportSpans = [new(first.Evidence.Id, first.Evidence.StartUtf16, 2, "周宁")] };
        var audit = SemanticQualityAuditor.Audit(AdaptiveSemanticTests.Response(r, finding), r, s.Sources).Findings.Single();
        Assert.Contains("SupportSpanMismatch", audit.Issues); Assert.False(audit.EvidenceValid);
        // The same quote declared against the right evidence is located there (absolute offset).
        var ok = AdaptiveSemanticTests.Name(second, "周宁") with { SupportSpans = [new(second.Evidence.Id, 296, 2, "周宁")] };
        var located = SemanticQualityAuditor.Audit(AdaptiveSemanticTests.Response(r, ok), r, s.Sources).Findings.Single();
        Assert.True(located.EvidenceValid, string.Join(",", located.Issues));
        Assert.Equal(second.Evidence.StartUtf16 + second.EvidenceText.IndexOf("周宁", StringComparison.Ordinal), located.Finding.SupportSpans[0].StartUtf16);
    }

    [Fact]
    public void Exact_spans_are_unchanged_and_relocated_spans_revalidate_identically_after_rehydration()
    {
        var (s, r) = Single(); var exact = s.Name(r, "林青");
        Assert.Equal(exact.SupportSpans[0] with { Text = "" }, Audit(s, r, exact).Finding.SupportSpans[0]);
        var relocated = Audit(s, r, WithSpan(s.Name(r, "林青"), r, 0, 2, "林青")).Finding;
        // Review/promotion rehydrates quotes from stored offsets and audits again: must stay valid and stable.
        var rehydrated = relocated with { SupportSpans = relocated.SupportSpans.Select(x => x with { Text = Source(s, x) }).ToArray() };
        var again = Audit(s, r, rehydrated);
        Assert.True(again.EvidenceValid); Assert.Equal(relocated.SupportSpans, again.Finding.SupportSpans);
    }
}
