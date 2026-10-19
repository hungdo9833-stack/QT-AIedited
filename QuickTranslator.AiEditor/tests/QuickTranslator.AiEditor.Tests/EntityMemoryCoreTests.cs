using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

internal sealed class MemoryScenario
{
    public EntityMemoryState State { get; } = new() { ProjectId = Guid.NewGuid() };
    public List<CensusSource> Sources { get; } = [];
    public Guid Source(int number, string text = "甲和乙在这里。原来是他。")
    {
        var source = new CensusSource(Guid.NewGuid(), number, text); Sources.Add(source);
        return new EntityMemoryBuilder(State).Evidence(source.ChapterId, number, CorpusIntegrityAuditor.SourceHash(text), 0, text.Length);
    }
    public Guid Entity(string key, string? label = null, MemoryEntityKind kind = MemoryEntityKind.Character) =>
        new EntityMemoryBuilder(State).Entity(key, kind, label ?? key);
    public EntityMemoryFact Fact(Guid subject, MemoryFactKind kind, string value, Guid proof, MemoryTimeline time,
        Guid? target = null, KnowledgeRelationType type = KnowledgeRelationType.Unknown, MemoryOrigin origin = MemoryOrigin.User,
        string explanation = "synthetic assertion", string? key = null) => new EntityMemoryBuilder(State).Fact(new()
        { SubjectId = subject, TargetId = target, Kind = kind, Value = value, RelationType = type, EvidenceIds = [proof], Timeline = time,
            SelfReference = kind == MemoryFactKind.Addressing ? value : "", AddresseeReference = kind == MemoryFactKind.Addressing ? "bạn" : "",
            Provenance = new(origin, key ?? Guid.NewGuid().ToString(), explanation) });
    public void Confirm(Guid id, MemoryTimeline? time = null) => EntityMemoryReviewService.AppendDecision(State, Sources, id, MemoryReviewStatus.Confirmed,
        time ?? new EntityMemoryQuery(State, Sources).Timeline(id), "Human checked synthetic proof and chronology");
    public EntityMemoryQuery Query() => new(State, Sources);
}

public sealed class EntityMemoryCoreTests
{
    [Fact]
    public void Identity_true_since_one_but_revealed_at_fifty_does_not_leak_names_or_audit_metadata_at_thirty()
    {
        var s = new MemoryScenario(); Guid early = s.Source(1), reveal = s.Source(50, "蒙面人的真实身份是甲。");
        Guid identity = s.Entity("identity", "SECRET_A");
        var mask = s.Fact(identity, MemoryFactKind.Alias, "蒙面人", early, new(1, null, 1, 1), explanation: "SECRET_A is hidden audit metadata");
        var name = s.Fact(identity, MemoryFactKind.PrimaryName, "SECRET_A", reveal, new(1, null, 50, 50));
        s.Confirm(mask.Id); s.Confirm(name.Id);
        string at30 = JsonSerializer.Serialize(s.Query().AtChapter(30));
        Assert.DoesNotContain("SECRET_A", at30);
        Assert.Contains("SECRET_A", JsonSerializer.Serialize(s.Query().AtChapter(50)));
        Assert.Equal("蒙面人", Assert.Single(s.Query().AtChapter(30).Entities).Names.Single());
    }

    [Fact]
    public void Accepted_is_distinct_from_confirmed_and_unknown_reveal_never_becomes_context()
    {
        var s = new MemoryScenario(); Guid e = s.Source(1), id = s.Entity("甲");
        var fact = s.Fact(id, MemoryFactKind.PrimaryName, "甲", e, new(1));
        EntityMemoryReviewService.AppendDecision(s.State, s.Sources, fact.Id, MemoryReviewStatus.Accepted, new(1), "Reviewed, reveal still unknown");
        Assert.Equal(MemoryReviewStatus.Accepted, s.Query().Status(fact.Id)); Assert.Empty(s.Query().AtChapter(100).Entities);
        Assert.Throws<InvalidOperationException>(() => s.Confirm(fact.Id));
        Assert.Throws<InvalidDataException>(() => s.Confirm(fact.Id, new(1, null, 1, 0)));
    }

    [Theory]
    [InlineData(KnowledgeRelationType.BiologicalFather, KnowledgeRelationType.FullSibling, true)]
    [InlineData(KnowledgeRelationType.BiologicalMother, KnowledgeRelationType.FullSibling, true)]
    [InlineData(KnowledgeRelationType.BiologicalFather, KnowledgeRelationType.PaternalHalfSibling, true)]
    [InlineData(KnowledgeRelationType.BiologicalMother, KnowledgeRelationType.MaternalHalfSibling, true)]
    [InlineData(KnowledgeRelationType.BiologicalFather, KnowledgeRelationType.MaternalHalfSibling, false)]
    [InlineData(KnowledgeRelationType.BiologicalFather, KnowledgeRelationType.UnknownSibling, false)]
    [InlineData(KnowledgeRelationType.BiologicalFather, KnowledgeRelationType.SwornSibling, false)]
    [InlineData(KnowledgeRelationType.AdoptiveParent, KnowledgeRelationType.FullSibling, false)]
    public void Biological_reasoning_reuses_compatible_confirmed_inputs_only(KnowledgeRelationType parentType, KnowledgeRelationType siblingType, bool expected)
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), a = s.Entity("A"), b = s.Entity("B"), c = s.Entity("C");
        var parent = s.Fact(a, MemoryFactKind.Relationship, parentType.ToString(), proof, new(1, 30, 1, 10), b, parentType);
        var sibling = s.Fact(b, MemoryFactKind.Relationship, siblingType.ToString(), proof, new(3, 25, 2, 20), c, siblingType);
        s.Confirm(parent.Id); s.Confirm(sibling.Id); EntityMemoryReasoning.Propose(s.State, s.Sources);
        var derived = s.State.Facts.Where(x => x.Provenance.Origin == MemoryOrigin.Rule).ToArray();
        Assert.Equal(expected ? 2 : 0, derived.Length);
        foreach (var fact in derived)
        {
            Assert.Equal(new MemoryTimeline(3, 25, 2, 20), fact.Timeline);
            Assert.Equal(MemoryReviewStatus.Proposed, s.Query().Status(fact.Id)); Assert.Equal(2, fact.InputFactIds.Count);
            Assert.Equal("K01", fact.Provenance.RuleId[..3]); Assert.DoesNotContain(s.Query().AtChapter(21).Relationships, x => x.Id == fact.Id);
        }
        int count = s.State.Facts.Count; EntityMemoryReasoning.Propose(s.State, s.Sources); Assert.Equal(count, s.State.Facts.Count);
    }

    [Fact]
    public void Unreviewed_or_nonoverlapping_family_facts_do_not_produce_a_claim()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), a = s.Entity("A"), b = s.Entity("B"), c = s.Entity("C");
        var parent = s.Fact(a, MemoryFactKind.Relationship, "father", proof, new(1, 5, 1, 1), b, KnowledgeRelationType.BiologicalFather);
        var sibling = s.Fact(b, MemoryFactKind.Relationship, "siblings", proof, new(6, 10, 1, 6), c, KnowledgeRelationType.FullSibling);
        s.Confirm(parent.Id); EntityMemoryReasoning.Propose(s.State, s.Sources);
        Assert.DoesNotContain(s.State.Facts, x => x.Provenance.Origin == MemoryOrigin.Rule);
        s.Confirm(sibling.Id); EntityMemoryReasoning.Propose(s.State, s.Sources);
        Assert.DoesNotContain(s.State.Facts, x => x.Provenance.Origin == MemoryOrigin.Rule);
    }

    [Fact]
    public void Conflicting_derivation_remains_in_review_and_failed_confirmation_does_not_change_history()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), a = s.Entity("A"), b = s.Entity("B"), c = s.Entity("C");
        var p = s.Fact(a, MemoryFactKind.Relationship, "father", proof, new(1, null, 1, 1), b, KnowledgeRelationType.BiologicalFather);
        var sib = s.Fact(b, MemoryFactKind.Relationship, "siblings", proof, new(1, null, 1, 1), c, KnowledgeRelationType.FullSibling);
        var existing = s.Fact(a, MemoryFactKind.Relationship, "adoptive", proof, new(1, null, 1, 1), c, KnowledgeRelationType.AdoptiveParent);
        s.Confirm(p.Id); s.Confirm(sib.Id); s.Confirm(existing.Id); EntityMemoryReasoning.Propose(s.State, s.Sources);
        var proposal = s.State.Facts.Single(x => x.Provenance.Origin == MemoryOrigin.Rule && x.SubjectId == a);
        Assert.Equal(MemoryReviewStatus.Conflict, s.Query().Status(proposal.Id));
        string before = JsonSerializer.Serialize(s.State.Reviews);
        Assert.Throws<InvalidOperationException>(() => s.Confirm(proposal.Id)); Assert.Equal(before, JsonSerializer.Serialize(s.State.Reviews));
    }

    [Fact]
    public void Body_consciousness_homonyms_and_similar_aliases_are_not_collapsed()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), body = s.Entity("body", "same", MemoryEntityKind.Body),
            mind = s.Entity("mind", "same", MemoryEntityKind.Consciousness), a = s.Entity("小锋"), b = s.Entity("小峰");
        foreach (var (id, name) in new[] { (body, "same"), (mind, "same"), (a, "小锋"), (b, "小峰") })
            s.Confirm(s.Fact(id, MemoryFactKind.PrimaryName, name, proof, new(1, null, 1, 1)).Id);
        var link = new EntityMemoryBuilder(s.State).Fact(new() { SubjectId = body, TargetId = mind, Kind = MemoryFactKind.IdentityAssociation,
            AssociationKind = MemoryAssociationKind.Consciousness, Value = "Explicit association, not equivalence", EvidenceIds = [proof], Timeline = new(1, null, 1, 1),
            Provenance = new(MemoryOrigin.User, "body-mind", "No automatic identity union") });
        s.Confirm(link.Id); Assert.Equal(4, s.Query().AtChapter(1).Entities.Count); Assert.NotEqual(body, mind); Assert.NotEqual(a, b);
    }

    [Fact]
    public void Title_revocation_and_relationship_end_apply_without_rewriting_approved_facts()
    {
        var s = new MemoryScenario(); Guid first = s.Source(1), change = s.Source(20), later = s.Source(21), a = s.Entity("A"), b = s.Entity("B");
        var old = s.Fact(a, MemoryFactKind.Title, "队长", first, new(1, null, 1, 1));
        var newer = s.Fact(a, MemoryFactKind.Title, "将军", later, new(21, null, 21, 21));
        var rel = s.Fact(a, MemoryFactKind.Relationship, "spouse", first, new(1, null, 1, 1), b, KnowledgeRelationType.Spouse);
        s.Confirm(old.Id); s.Confirm(newer.Id); s.Confirm(rel.Id);
        var builder = new EntityMemoryBuilder(s.State);
        foreach (var pair in new[] { (old, TranslationImpactEventType.TitleGrantedOrRevoked), (rel, TranslationImpactEventType.RelationshipEnded) })
        {
            var item = builder.Event(new() { Type = pair.Item2, EntityIds = [a, b], Effect = MemoryEventEffect.End, AffectedFactIds = [pair.Item1.Id],
                EvidenceIds = [change], Timeline = new(20, 20, 20, 20), Provenance = new(MemoryOrigin.User, "event:" + pair.Item1.Id, "Human verified end") });
            s.Confirm(item.Id);
        }
        Assert.Contains(s.Query().AtChapter(19).Entities.SelectMany(x => x.Facts), x => x.Id == old.Id);
        Assert.DoesNotContain(s.Query().AtChapter(21).Entities.SelectMany(x => x.Facts), x => x.Id == old.Id);
        Assert.Contains(s.Query().AtChapter(21).Entities.SelectMany(x => x.Facts), x => x.Id == newer.Id);
        Assert.Empty(s.Query().AtChapter(21).Relationships); Assert.Null(s.State.Facts.Single(x => x.Id == old.Id).Timeline.EffectiveToChapter);
    }

    [Fact]
    public void Source_edits_deleted_chapters_and_renumbering_make_claims_stale_without_erasing_confirmation()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), a = s.Entity("A");
        var fact = s.Fact(a, MemoryFactKind.PrimaryName, "A", proof, new(1, null, 1, 1)); s.Confirm(fact.Id);
        string history = JsonSerializer.Serialize(s.State.Reviews);
        var changed = s.Sources.Select(x => x with { OriginalText = "原文已经修改。" }).ToArray();
        var query = new EntityMemoryQuery(s.State, changed); Assert.Equal(MemoryReviewStatus.Stale, query.Status(fact.Id)); Assert.Empty(query.AtChapter(5).Entities);
        Assert.Equal(MemoryReviewStatus.Stale, new EntityMemoryQuery(s.State, []).Status(fact.Id));
        Assert.Equal(MemoryReviewStatus.Stale, new EntityMemoryQuery(s.State, s.Sources.Select(x => x with { ChapterNumber = 2 }).ToArray()).Status(fact.Id));
        Assert.Equal(history, JsonSerializer.Serialize(s.State.Reviews));
    }

    [Fact]
    public void Audit_link_stays_out_of_query_even_when_reviewed_as_an_association()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), a = s.Entity("A"), alias = s.Entity("alias");
        var fact = s.Fact(alias, MemoryFactKind.IdentityAssociation, "audit link", proof, new(1, null, 1, 1), a, origin: MemoryOrigin.AuditOnly);
        s.Confirm(fact.Id); Assert.Empty(s.Query().AtChapter(1).Entities);
        Assert.DoesNotContain(s.State.Facts, x => x.Kind == MemoryFactKind.Alias);
    }

    [Fact]
    public void Addressing_change_respects_end_event_and_does_not_use_future_end_knowledge_to_resolve_ambiguity()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1), endProof = s.Source(20), a = s.Entity("A"), b = s.Entity("B");
        var old = s.Fact(a, MemoryFactKind.Addressing, "tôi", proof, new(1, null, 1, 1), b); s.Confirm(old.Id);
        var ended = new EntityMemoryBuilder(s.State).Event(new() { Type = TranslationImpactEventType.AddressingChanged,
            EntityIds = [a, b], Effect = MemoryEventEffect.End, AffectedFactIds = [old.Id], EvidenceIds = [endProof], Timeline = new(20, null, 20, 50),
            Provenance = new(MemoryOrigin.User, "address-end", "Ending is only revealed later") }); s.Confirm(ended.Id);
        var replacement = s.Fact(a, MemoryFactKind.Addressing, "con", proof, new(21, null, 21, 21), b); s.Confirm(replacement.Id);
        Assert.Empty(s.Query().AtChapter(30).Addressing); // both look possible with reader knowledge; do not force either
        Assert.Equal("con", Assert.Single(s.Query().AtChapter(50).Addressing).SelfReference);
        Assert.Equal("tôi", Assert.Single(s.Query().AtChapter(19).Addressing).SelfReference);
    }
}
