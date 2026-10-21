using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>The minimum evidence for a pair, a character or an identity grows with the length of the book, never below the old constant.</summary>
public sealed class AddressingEvidenceScaleTests
{
    private static CensusSource[] Padding(int units) => [new(Guid.NewGuid(), 99, new string('空', units * 1_500))];

    [Fact]
    public void The_minimum_is_the_floor_for_a_short_book_and_grows_by_one_per_scale_units()
    {
        var settings = new AddressingSettings();
        Assert.Equal(3, AddressingAiSurvey.ScaledMinimum(3, Padding(10), settings));
        Assert.Equal(3, AddressingAiSurvey.ScaledMinimum(3, Padding(150 * 3), settings)); // 3 units of 150: not above the floor yet.
        Assert.Equal(13, AddressingAiSurvey.ScaledMinimum(3, Padding(150 * 13), settings));
        Assert.Equal(13, AddressingAiSurvey.ScaledMinimum(2, Padding(150 * 13), settings));
        Assert.Equal(3, AddressingAiSurvey.ScaledMinimum(3, Padding(150 * 13), new(EvidenceScale: 0))); // Off.
        Assert.Equal(39, AddressingAiSurvey.ScaledMinimum(3, Padding(150 * 13), new(EvidenceScale: 50)));
    }

    [Fact]
    public void Length_counts_characters_not_chapters()
    {
        var few = new[] { new CensusSource(Guid.NewGuid(), 1, new string('空', 1_500 * 150 * 8)) }; // One huge chapter.
        var many = Enumerable.Range(1, 2_000).Select(n => new CensusSource(Guid.NewGuid(), n, "空")).ToArray(); // Many tiny ones.
        Assert.Equal(8, AddressingAiSurvey.ScaledMinimum(3, few, new()));
        Assert.Equal(3, AddressingAiSurvey.ScaledMinimum(3, many, new()));
    }

    [Fact]
    public void A_long_book_asks_only_about_pairs_with_enough_lines_and_a_short_book_keeps_the_old_minimum()
    {
        var names = AddressingAiSurveyTests.Names;
        var shortBook = AddressingAiSurvey.PlanTasks(AddressingAiSurveyTests.Sources, names);
        Assert.Contains(shortBook, t => t.Kind == AddressingTaskKind.Pair); Assert.Contains(shortBook, t => t.Kind == AddressingTaskKind.Character);
        // The same text inside a book 8 scale-units long: its 6 forward lines no longer reach the minimum of 8.
        var longBook = AddressingAiSurvey.PlanTasks([.. AddressingAiSurveyTests.Sources, .. Padding(150 * 8)], names);
        Assert.DoesNotContain(longBook, t => t.Kind == AddressingTaskKind.Pair);
        var unscaled = AddressingAiSurvey.PlanTasks([.. AddressingAiSurveyTests.Sources, .. Padding(150 * 8)], names, new(EvidenceScale: 0));
        Assert.Equal(shortBook.Select(t => t.TaskId), unscaled.Select(t => t.TaskId)); // Switched off: exactly the old plan.
    }

    [Fact]
    public void Every_person_in_an_asked_pair_also_gets_a_character_task()
    {
        // Minimum 6: the pair (6 forward lines) is asked; both people must come with it even if narration mentions them less.
        var plan = AddressingAiSurvey.PlanTasks([.. AddressingAiSurveyTests.Sources, .. Padding(150 * 6)], AddressingAiSurveyTests.Names);
        var pair = Assert.Single(plan, t => t.Kind == AddressingTaskKind.Pair);
        var characters = plan.Where(t => t.Kind == AddressingTaskKind.Character).Select(t => t.Subject).ToHashSet();
        Assert.Contains(pair.Subject, characters); Assert.Contains(pair.Listener!, characters);
    }

    [Fact]
    public void Validation_rejects_a_negative_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddressingSettings(EvidenceScale: -1).Validate());
        new AddressingSettings(EvidenceScale: 0).Validate();
    }
}
