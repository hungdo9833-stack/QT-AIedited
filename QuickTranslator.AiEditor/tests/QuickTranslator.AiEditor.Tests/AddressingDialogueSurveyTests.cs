using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Addressing Ledger step 1: patterns taken from a real hard-wrapped web novel (synthetic text).</summary>
public sealed class AddressingDialogueSurveyTests
{
    private static readonly string[] Names = ["柳轻锋", "柳月影", "秦梦岚", "小锋"];
    private const string Wrapped =
        "    柳月影白了柳轻锋一眼说道：「我知道啦，弟弟你\n别闹了！」\n" +
        "    说着她又继续说道：「好了。」\n" +
        "    柳轻锋点点头，笑着说道：「老姐，我听你的。跟妈妈说一声。」\n" +
        "    「哼！」\n";
    private static IReadOnlyList<DialogueLine> Lines(string text, string[]? names = null) =>
        AddressingDialogueSurvey.Extract(new CensusSource(Guid.NewGuid(), 1, text), names ?? Names);

    [Fact]
    public void Explicit_speaker_clause_listener_vocative_and_soft_wrapped_quote_keep_original_offsets()
    {
        var line = Lines(Wrapped)[0];
        Assert.Equal("柳月影", line.Speaker); Assert.Equal(SpeakerEvidence.Explicit, line.SpeakerEvidence);
        Assert.Equal("柳轻锋", line.Listener); Assert.Equal(ListenerEvidence.Clause, line.ListenerEvidence);
        Assert.Equal("我知道啦，弟弟你别闹了！", line.Quote); // Soft wrap removed for analysis…
        Assert.Contains("弟弟", line.VocativeTerms); Assert.Contains("我", line.SelfTerms);
        Assert.Equal("我知道啦，弟弟你\n别闹了！", Wrapped.Substring(line.QuoteStartUtf16, line.QuoteLengthUtf16)); // …offsets stay original.
    }

    [Fact]
    public void Pronoun_subject_is_only_a_candidate_and_unmarked_lines_stay_unattributed()
    {
        var lines = Lines(Wrapped);
        Assert.Equal(4, lines.Count);
        Assert.Null(lines[1].Speaker); Assert.Equal(SpeakerEvidence.Pronoun, lines[1].SpeakerEvidence); Assert.Equal("柳月影", lines[1].SpeakerCandidate);
        Assert.Null(lines[3].Speaker); Assert.Equal(SpeakerEvidence.Unattributed, lines[3].SpeakerEvidence); Assert.Null(lines[3].Listener);
    }

    [Fact]
    public void Subject_of_an_earlier_subclause_speaks_and_vocative_differs_from_third_person_mention()
    {
        var line = Lines(Wrapped)[2];
        Assert.Equal("柳轻锋", line.Speaker); Assert.Equal(SpeakerEvidence.Explicit, line.SpeakerEvidence);
        Assert.Equal(["老姐"], line.VocativeTerms); Assert.Equal(["妈妈"], line.MentionedTerms); // "跟妈妈说" is not addressing the listener.
        Assert.Equal(1, line.SecondPerson);
    }

    [Theory]
    [InlineData("    「走吧。」柳轻锋说道。\n    好。\n    好。\n    好。\n", "柳轻锋")]
    [InlineData("    「走吧。」\n    柳轻锋说道：好。\n    好。\n    好。\n", null)]  // Next paragraph narrates, it does not attribute.
    [InlineData("    「走吧。」柳轻锋知道了。\n    好。\n    好。\n    好。\n", null)] // 知道 is not a speech verb.
    public void After_quote_attribution_only_in_the_same_paragraph_with_a_real_speech_verb(string text, string? speaker)
    {
        var line = Lines(text)[0];
        Assert.Equal(speaker, line.Speaker);
        Assert.Equal(speaker is null ? SpeakerEvidence.Unattributed : SpeakerEvidence.Explicit, line.SpeakerEvidence);
    }

    [Fact]
    public void Alias_hint_prevents_self_pairs_but_identities_are_not_merged()
    {
        const string text = "    柳轻锋看着秦梦岚说道：「小锋知道了，妈妈。」\n    好。\n    好。\n    好。\n";
        var line = Lines(text)[0];
        Assert.Equal("柳轻锋", line.Speaker); Assert.NotEqual("小锋", line.Listener); Assert.Equal("秦梦岚", line.Listener);
        // Scene guess: the only other name in the paragraph is the speaker's own short form, so no listener.
        var self = Lines("    小锋站了起来。柳轻锋说道：「走吧。」\n    好。\n    好。\n    好。\n")[0];
        Assert.Equal("柳轻锋", self.Speaker); Assert.Null(self.Listener); Assert.Equal(ListenerEvidence.Unknown, self.ListenerEvidence);
        var report = AddressingDialogueSurvey.Analyze([new(Guid.NewGuid(), 1, text)], Names);
        Assert.Equal("柳轻锋", report.AliasHints["小锋"]);
        Assert.Contains("小锋", report.CandidateNames); // Still its own surface name.
        // Two possible owners → no hint at all.
        var ambiguous = AddressingDialogueSurvey.Analyze([new(Guid.NewGuid(), 1, text)], ["柳轻锋", "王小锋", "小锋"]);
        Assert.False(ambiguous.AliasHints.ContainsKey("小锋"));
    }

    [Fact]
    public void Vocative_name_is_the_strongest_listener_evidence()
    {
        const string text = "    秦梦岚叹了口气说道：「柳月影，你过来。」\n    好。\n    好。\n    好。\n";
        var line = Lines(text)[0];
        Assert.Equal("秦梦岚", line.Speaker); Assert.Equal("柳月影", line.Listener); Assert.Equal(ListenerEvidence.Vocative, line.ListenerEvidence);
    }

    [Fact]
    public void Unindented_text_keeps_every_line_break_and_curly_and_nested_quotes_are_parsed()
    {
        const string text = "柳轻锋说道：“我在\n这里。”\n秦梦岚问道：「他说『走』了吗？」";
        var lines = Lines(text);
        Assert.Equal(2, lines.Count);
        Assert.Equal("我在\n这里。", lines[0].Quote); // Not hard-wrapped: the break is kept.
        Assert.Equal("他说『走』了吗？", lines[1].Quote); Assert.Equal("秦梦岚", lines[1].Speaker);
    }

    [Fact]
    public void Pairs_aggregate_by_direction_with_samples_and_counts()
    {
        var text = string.Concat(Enumerable.Repeat("    柳月影白了柳轻锋一眼说道：「弟弟，我走了。」\n    柳轻锋看着柳月影说道：「老姐，等等我。」\n", 3));
        var report = AddressingDialogueSurvey.Analyze([new(Guid.NewGuid(), 2, text), new(Guid.NewGuid(), 5, text)], Names, samplesPerPair: 4);
        var sister = report.Pairs.Single(p => p.Speaker == "柳月影" && p.Listener == "柳轻锋");
        var brother = report.Pairs.Single(p => p.Speaker == "柳轻锋" && p.Listener == "柳月影");
        Assert.Equal(6, sister.Lines); Assert.Equal(6, sister.ExplicitSpeakerLines); Assert.Equal(2, sister.FirstChapter); Assert.Equal(5, sister.LastChapter);
        Assert.Equal(6, sister.VocativeTerms["弟弟"]); Assert.Equal(6, brother.VocativeTerms["老姐"]);
        Assert.Equal(4, sister.Samples.Count); Assert.Equal(12, report.DialogueLines); Assert.Equal(12, report.ExplicitSpeakerLines);
    }
}
