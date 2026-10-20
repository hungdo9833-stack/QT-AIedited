using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Addressing Ledger step 2 with a mock provider only: planning, schema, verification, checkpoint/resume.</summary>
public sealed class AddressingAiSurveyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-ai-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    internal static readonly string[] Names = ["王秀兰", "林青"];
    // Hard-wrapped, indented paragraphs like the real web novel; 秀兰姐/小兰 are variants of 王秀兰.
    private const string Block =
        "    王秀兰看着林青说道：「林青，姐姐给你做饭。」\n" +
        "    林青看着王秀兰说道：「秀兰姐，我不饿。」\n" +
        "    王秀兰点点头，她转身走了。小兰真是好人，林青心想，他笑了。\n";
    internal static readonly CensusSource[] Sources = [new(Guid.NewGuid(), 1, string.Concat(Enumerable.Repeat(Block, 3))), new(Guid.NewGuid(), 2, string.Concat(Enumerable.Repeat(Block, 3)))];
    internal static readonly ModelOptions Options = new() { Model = "mock-addressing", ContextSize = 32768, MaximumOutputTokens = 4096 };

    private static IReadOnlyList<AddressingTask> Tasks() => AddressingAiSurvey.PlanTasks(Sources, Names);
    private static AddressingTask Pair(IReadOnlyList<AddressingTask> tasks, string speaker, string listener) => tasks.Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == speaker && t.Listener == listener);
    private static AddressingTask Character(IReadOnlyList<AddressingTask> tasks, string name) => tasks.Single(t => t.Kind == AddressingTaskKind.Character && t.Subject == name);

    /// <summary>A well-formed reply that cites the task's own samples and quoted terms.</summary>
    internal static JsonObject Answer(AddressingRequestPlan request, Func<AddressingTask, JsonObject?>? overrideTask = null)
    {
        var pairs = new JsonArray(); var characters = new JsonArray(); var scenes = new JsonArray();
        foreach (var task in request.Tasks)
        {
            var custom = overrideTask?.Invoke(task);
            if (task.Kind == AddressingTaskKind.Scene)
            {
                scenes.Add(custom ?? new JsonObject { ["task_id"] = task.TaskId, ["turns"] = new JsonArray(), ["reason"] = "Không rõ." });
                continue;
            }
            if (task.Kind == AddressingTaskKind.Pair)
            {
                bool sister = task.Subject == "王秀兰";
                pairs.Add(custom ?? new JsonObject
                {
                    ["task_id"] = task.TaskId, ["status"] = "Consistent", ["relationship"] = sister ? "chị → em" : "em → chị",
                    ["phases"] = new JsonArray(new JsonObject
                    {
                        ["from_chapter"] = 1, ["to_chapter"] = 0, ["self_vi"] = sister ? "chị" : "em", ["address_vi"] = sister ? "em" : "chị",
                        ["self_zh"] = new JsonArray(sister ? "姐姐" : "我"), ["address_zh"] = new JsonArray(sister ? "林青" : "秀兰姐"),
                        ["sample_ids"] = new JsonArray(task.Samples.Select(s => (JsonNode)s.SampleId).ToArray())
                    }),
                    ["reason"] = "Lời thoại trực tiếp."
                });
            }
            else
            {
                bool female = task.Subject == "王秀兰";
                characters.Add(custom ?? new JsonObject
                {
                    ["task_id"] = task.TaskId, ["status"] = "Consistent", ["gender"] = female ? "female" : "male",
                    ["name_forms"] = new JsonArray(task.NameCandidates.Select(v => (JsonNode)new JsonObject { ["zh"] = v.Form, ["vi"] = v.Form == "小兰" ? "Tiểu Lan" : "Tú Lan" })
                        .Append(new JsonObject { ["zh"] = task.Subject, ["vi"] = female ? "Vương Tú Lan" : "Lâm Thanh" }).ToArray()),
                    ["phases"] = new JsonArray(new JsonObject
                    {
                        ["from_chapter"] = 1, ["to_chapter"] = 0, ["narrative_vi"] = female ? "nàng" : "hắn", ["secondary_vi"] = "",
                        ["sample_ids"] = new JsonArray(task.Samples.Select(s => (JsonNode)s.SampleId).ToArray())
                    }),
                    ["reason"] = "Đại từ sau tên."
                });
            }
        }
        return new JsonObject { ["pairs"] = pairs, ["characters"] = characters, ["scenes"] = scenes };
    }

    [Fact]
    public void Plans_pair_and_character_tasks_with_variants_merged_to_their_single_owner_and_verbatim_samples()
    {
        var tasks = Tasks();
        var sister = Pair(tasks, "王秀兰", "林青"); var brother = Pair(tasks, "林青", "王秀兰"); // 秀兰姐 vocative counted for 王秀兰.
        Assert.Equal(6, sister.Samples.Count); Assert.Equal(6, brother.Samples.Count);
        Assert.Contains(sister.Samples, s => s.ChapterNumber == 1); Assert.Contains(sister.Samples, s => s.ChapterNumber == 2); // Spread over the story.
        Assert.Contains(brother.NameCandidates, v => v.Form == "秀兰姐");
        var heroine = Character(tasks, "王秀兰"); var hero = Character(tasks, "林青");
        Assert.Equal("female", heroine.SourceGender); Assert.Equal("male", hero.SourceGender);
        Assert.Contains(heroine.NameCandidates, v => v.Form == "秀兰姐"); Assert.Contains(heroine.NameCandidates, v => v.Form == "小兰");
        Assert.DoesNotContain(tasks, t => t.Subject is "秀兰姐" or "小兰"); // No separate identity for a variant.
        foreach (var sample in tasks.SelectMany(t => t.Samples))
        {
            var normalized = Sources.Single(s => s.ChapterNumber == sample.ChapterNumber).OriginalText.Replace("\n", "");
            Assert.Contains(sample.Text, normalized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pair_samples_prefer_vocative_or_clause_listeners_over_scene_guesses()
    {
        // 6 strong lines (listener named in the lead clause) and many scene-only lines in later chapters.
        string strong = string.Concat(Enumerable.Repeat("    王秀兰看着林青说道：「林青，吃饭了。」\n", 6));
        string scene = string.Concat(Enumerable.Repeat("    林青在一旁。王秀兰叹气道：「唉，算了。」\n", 4));
        var sources = new CensusSource[] { new(Guid.NewGuid(), 1, strong), new(Guid.NewGuid(), 2, scene), new(Guid.NewGuid(), 3, scene) };
        var task = AddressingAiSurvey.PlanTasks(sources, Names, new(SamplesPerTask: 6)).Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "王秀兰");
        Assert.All(task.Samples, s => Assert.Equal(1, s.ChapterNumber)); // Scene guesses only fill a shortfall.
        var mixed = AddressingAiSurvey.PlanTasks(sources, Names, new(SamplesPerTask: 8)).Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "王秀兰");
        Assert.Equal(6, mixed.Samples.Count(s => s.ChapterNumber == 1)); Assert.Equal(2, mixed.Samples.Count(s => s.ChapterNumber > 1));
    }

    [Fact]
    public void Schema_is_gemini_safe_and_prompt_lists_tasks_samples_candidates_and_rules()
    {
        var keywords = new HashSet<string>();
        void Walk(JsonElement n) { foreach (var p in n.EnumerateObject()) keywords.Add(p.Name); if (n.TryGetProperty("properties", out var ps)) foreach (var p in ps.EnumerateObject()) Walk(p.Value); if (n.TryGetProperty("items", out var i)) Walk(i); }
        Walk(AddressingAiSurvey.Schema);
        Assert.Subset(new HashSet<string> { "type", "properties", "required", "items", "enum" }, keywords);
        var requests = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model, new(TasksPerRequest: 2));
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests.Select(r => r.Id), AddressingAiSurvey.PlanRequests(Tasks(), Options.Model, new(TasksPerRequest: 2)).Select(r => r.Id)); // Deterministic.
        string all = string.Concat(requests.Select(r => r.Prompt));
        Assert.Equal(Tasks().Select(t => t.TaskId).Order(), requests.SelectMany(r => AddressingAiSurvey.TasksInPrompt(r.Prompt, Tasks())).Select(t => t.TaskId).Order());
        Assert.All(requests, r => Assert.Matches(@"(?m)^TASK T1 ", r.Prompt)); Assert.DoesNotContain("TASK pair:", all); // Short ids only.
        Assert.Contains("NAME CANDIDATES:", all, StringComparison.Ordinal); Assert.Contains("CPU NAME HYPOTHESIS", all, StringComparison.Ordinal);
        Assert.Contains("narrative_vi", all, StringComparison.Ordinal); Assert.Contains("Never add a form", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_reply_produces_verified_unconfirmed_proposals_for_every_task()
    {
        var request = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model).Single();
        var proposals = AddressingAiSurvey.Verify(Answer(request).ToJsonString(), request);
        Assert.Equal(request.Tasks.Count, proposals.Count);
        Assert.All(proposals, p => Assert.True(p.EvidenceValid, p.TaskId + ":" + string.Join(",", p.Issues)));
        var heroine = proposals.Single(p => p.Kind == AddressingTaskKind.Character && p.Subject == "王秀兰");
        Assert.Equal("nàng", heroine.Phases.Single().NarrativeVi); Assert.Contains(heroine.NameForms, f => f.Zh == "小兰" && f.Vi == "Tiểu Lan");
    }

    [Fact]
    public void Root_damage_rejects_the_whole_reply()
    {
        var request = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model).Single();
        foreach (Action<JsonObject> change in new Action<JsonObject>[] { n => n.Remove("characters"), n => n["extra"] = 1, n => n["pairs"] = "none" })
        {
            var node = Answer(request); change(node);
            Assert.Throws<InvalidDataException>(() => AddressingAiSurvey.Verify(node.ToJsonString(), request));
        }
        Assert.Throws<InvalidDataException>(() => AddressingAiSurvey.Verify("```json\n{}\n```", request));
    }

    public static TheoryData<string, string, string> ItemFaults => new()
    {
        // fault, expected issue code on the damaged task ("" = item ignored), expected JSON path in SchemaIssues
        { "missing-field", "SchemaViolation", "$.pairs[0]: missing reason" },
        { "extra-field", "SchemaViolation", "$.pairs[0].confirmed" },
        { "unknown-enum", "SchemaViolation", "$.characters[0].gender: unknown enum value" },
        { "wrong-type", "SchemaViolation", "$.pairs[0].phases[0].from_chapter: expected integer" },
        { "answered-twice", "DuplicateTaskAnswer", "task answered twice" },
        { "unknown-task", "", "$.pairs[0].task_id: unknown task" }
    };

    [Theory]
    [MemberData(nameof(ItemFaults))]
    public void A_malformed_task_item_invalidates_only_that_task_and_records_where(string fault, string code, string path)
    {
        // Live Gemini: 3 of 9 replies were lost whole for one malformed item. Only the damaged task may be lost.
        var request = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model).Single();
        var node = Answer(request);
        string damaged = (fault == "unknown-enum" ? node["characters"]! : node["pairs"]!)[0]!["task_id"]!.GetValue<string>();
        switch (fault)
        {
            case "missing-field": node["pairs"]![0]!.AsObject().Remove("reason"); break;
            case "extra-field": node["pairs"]![0]!["confirmed"] = true; break;
            case "unknown-enum": node["characters"]![0]!["gender"] = "girl"; break;
            case "wrong-type": node["pairs"]![0]!["phases"]![0]!["from_chapter"] = "1"; break;
            case "answered-twice": ((JsonArray)node["pairs"]!).Add(node["pairs"]![0]!.DeepClone()); break;
            case "unknown-task": node["pairs"]![0]!["task_id"] = "pair:unknown"; break;
        }
        var result = AddressingAiSurvey.VerifyReply(node.ToJsonString(), request);
        Assert.Contains(result.SchemaIssues, x => x.Contains(path, StringComparison.Ordinal));
        if (code.Length == 0) Assert.DoesNotContain(result.Proposals, p => p.TaskId == damaged); // Unanswered: resent later.
        else Assert.Equal([code], result.Proposals.Single(p => p.TaskId == damaged).Issues);
        Assert.DoesNotContain(result.Proposals, p => p.TaskId == damaged && p.EvidenceValid);
        Assert.Equal(request.Tasks.Count - 1, result.Proposals.Count(p => p.TaskId != damaged && p.EvidenceValid)); // The rest stand.
    }

    [Fact]
    public void Replies_use_the_short_task_ids_of_the_prompt()
    {
        var request = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model).Single();
        var node = Answer(request);
        foreach (var item in new[] { "pairs", "characters" }.SelectMany(k => node[k]!.AsArray()))
            item!["task_id"] = "T" + (request.Tasks.ToList().FindIndex(t => t.TaskId == item["task_id"]!.GetValue<string>()) + 1);
        var short_ = AddressingAiSurvey.VerifyReply(node.ToJsonString(), request);
        Assert.Empty(short_.SchemaIssues); Assert.Equal(request.Tasks.Count, short_.Proposals.Count(p => p.EvidenceValid));
        Assert.Equal(request.Tasks.Select(t => t.TaskId).Order(), short_.Proposals.Select(p => p.TaskId).Order()); // Mapped back to stable ids.

        var mixed = node.DeepClone().AsObject(); // Short id and full id of one task: answered twice, neither trusted.
        var first = mixed["pairs"]![0]!; var copy = first.DeepClone();
        copy["task_id"] = request.Tasks[int.Parse(first["task_id"]!.GetValue<string>()[1..]) - 1].TaskId; ((JsonArray)mixed["pairs"]!).Add(copy);
        Assert.Contains("DuplicateTaskAnswer", AddressingAiSurvey.Verify(mixed.ToJsonString(), request).SelectMany(p => p.Issues));
        var wrongArray = node.DeepClone().AsObject(); ((JsonArray)wrongArray["characters"]!).Add(wrongArray["pairs"]![0]!.DeepClone());
        var issues = AddressingAiSurvey.VerifyReply(wrongArray.ToJsonString(), request).SchemaIssues;
        Assert.Contains(issues, x => x.Contains("$.characters[") && x.EndsWith("task in the wrong array"));
        var miscopied = node.DeepClone().AsObject(); miscopied["pairs"]![0]!["task_id"] = "T 1";
        Assert.Contains(AddressingAiSurvey.VerifyReply(miscopied.ToJsonString(), request).SchemaIssues, x => x.EndsWith("unknown task"));
    }

    [Fact]
    public async Task Engine_records_schema_fault_locations_on_the_attempt_without_source_text()
    {
        var tasks = Tasks(); var store = await Store();
        var client = new Fake(prompt =>
        {
            var node = Answer(AddressingAiSurvey.PlanRequests(tasks, Options.Model).Single()); node["pairs"]![0]!.AsObject().Remove("reason"); return node.ToJsonString();
        });
        var run = await new AddressingAiEngine(client).RunAsync(store, tasks, Options, maximumRequests: 1);
        var attempt = run.State.Attempts.Single();
        Assert.True(attempt.Succeeded); Assert.Equal(["Addressing schema mismatch at $.pairs[0]: missing reason"], attempt.SchemaIssues);
        Assert.Equal(tasks.Count - 1, run.State.Proposals.Count(p => p.EvidenceValid));
        var whole = new Fake(_ => """{"pairs":[],"characters":[]}""");
        var failed = await new AddressingAiEngine(whole).RunAsync(await Store(), tasks, Options, maximumRequests: 1);
        Assert.Equal("SemanticSchemaViolation", failed.State.Attempts.Single().ErrorCode);
        Assert.Equal(["Addressing schema mismatch at $: missing scenes"], failed.State.Attempts.Single().SchemaIssues); // Root fault: whole reply, still located.
        Assert.DoesNotContain("王秀兰看着", await File.ReadAllTextAsync(store.PathName));
    }

    [Fact]
    public void Evidence_checks_flag_unquoted_terms_foreign_samples_ranges_gender_and_unlisted_names()
    {
        var request = AddressingAiSurvey.PlanRequests(Tasks(), Options.Model).Single();
        var sister = request.Tasks.Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "王秀兰");
        var heroine = request.Tasks.Single(t => t.Kind == AddressingTaskKind.Character && t.Subject == "王秀兰");
        string Issues(Func<AddressingTask, JsonObject?> edit, AddressingTask target) =>
            string.Join(",", AddressingAiSurvey.Verify(Answer(request, edit).ToJsonString(), request).Single(p => p.TaskId == target.TaskId).Issues);
        JsonObject PairWith(AddressingTask t, Action<JsonObject> phase)
        {
            var node = Answer(request)["pairs"]!.AsArray().Single(x => x!["task_id"]!.GetValue<string>() == t.TaskId)!.DeepClone().AsObject();
            phase(node["phases"]![0]!.AsObject()); return node;
        }
        JsonObject CharacterWith(AddressingTask t, Action<JsonObject> edit)
        {
            var node = Answer(request)["characters"]!.AsArray().Single(x => x!["task_id"]!.GetValue<string>() == t.TaskId)!.DeepClone().AsObject();
            edit(node); return node;
        }
        Assert.Contains("UnquotedChineseTerm", Issues(t => t == sister ? PairWith(t, p => p["address_zh"] = new JsonArray("哥哥")) : null, sister));
        Assert.Contains("MissingOrForeignSample", Issues(t => t == sister ? PairWith(t, p => p["sample_ids"] = new JsonArray("N1")) : null, sister));
        Assert.Contains("SampleOutsidePhase", Issues(t => t == sister ? PairWith(t, p => p["to_chapter"] = 1) : null, sister));
        Assert.Contains("InvalidChapterRange", Issues(t => t == sister ? PairWith(t, p => p["from_chapter"] = 9) : null, sister));
        Assert.Contains("MissingOrOverlongVietnameseTerm", Issues(t => t == sister ? PairWith(t, p => p["self_vi"] = "") : null, sister));
        Assert.Contains("GenderConflictsWithSource", Issues(t => t == heroine ? CharacterWith(t, c => c["gender"] = "male") : null, heroine));
        Assert.Contains("UnlistedOrDuplicateNameForm", Issues(t => t == heroine ? CharacterWith(t, c => ((JsonArray)c["name_forms"]!).Add(new JsonObject { ["zh"] = "兰花", ["vi"] = "Lan Hoa" })) : null, heroine));
        Assert.Contains("PhaseCountMismatch", Issues(t => t == heroine ? CharacterWith(t, c => c["status"] = "Changes") : null, heroine));
        // Live Gemini case: address_vi "Lục先生". Every Vietnamese field must be Vietnamese only.
        const string han = "ChineseInVietnameseField";
        Assert.Contains(han, Issues(t => t == sister ? PairWith(t, p => p["address_vi"] = "Lục先生") : null, sister));
        Assert.Contains(han, Issues(t => t == sister ? PairWith(t, p => p["self_vi"] = "姐") : null, sister));
        Assert.Contains(han, Issues(t => t == sister ? PairWith(t, _ => { }).Also(n => n["relationship"] = "王秀兰 → 林青") : null, sister));
        Assert.Contains(han, Issues(t => t == heroine ? CharacterWith(t, c => c["phases"]![0]!["narrative_vi"] = "她") : null, heroine));
        Assert.Contains(han, Issues(t => t == heroine ? CharacterWith(t, c => c["phases"]![0]!["secondary_vi"] = "nàng/她") : null, heroine));
        Assert.Contains(han, Issues(t => t == heroine ? CharacterWith(t, c => c["name_forms"]![0]!["vi"] = "Vương 秀兰") : null, heroine));
        Assert.DoesNotContain(han, Issues(t => t == sister ? PairWith(t, p => p["address_vi"] = "Lục tiên sinh") : null, sister)); // Diacritics are fine.
        Assert.True(AddressingAiSurvey.HasHan("𠀀")); Assert.False(AddressingAiSurvey.HasHan("Đình Kiêu → chị/em, ông, bà"));
        // Ambiguous is a valid answer but never a rule.
        var ambiguous = AddressingAiSurvey.Verify(Answer(request, t => t == sister ? PairWith(t, _ => { }).Also(n => n["status"] = "Ambiguous") : null).ToJsonString(), request)
            .Single(p => p.TaskId == sister.TaskId);
        Assert.False(ambiguous.EvidenceValid); Assert.Empty(ambiguous.Issues);
    }

    private sealed class Fake(Func<string, string> reply) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Func<int, Exception?> Fail { get; set; } = _ => null;
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal(AddressingAiSurvey.Schema.GetRawText(), schema.GetRawText());
            if (Fail(Calls) is { } error) throw error;
            return Task.FromResult(new OllamaCompletion(reply(prompt), true, "stop", new GenerationMetrics { InputTokens = 900, OutputTokens = 300 }));
        }
    }
    private async Task<AddressingLedgerStore> Store()
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic addressing");
        return new AddressingLedgerStore(session);
    }
    private static Func<string, string> Replies(IReadOnlyList<AddressingTask> tasks, AddressingSettings settings) => prompt =>
    {
        var request = AddressingAiSurvey.PlanRequests(AddressingAiSurvey.TasksInPrompt(prompt, tasks), Options.Model, settings).Single();
        return Answer(request).ToJsonString();
    };

    [Fact]
    public async Task Engine_checkpoints_each_request_resumes_without_resend_and_honours_the_request_budget()
    {
        var tasks = Tasks(); var settings = new AddressingSettings(TasksPerRequest: 2); var store = await Store();
        var client = new Fake(Replies(tasks, settings));
        var first = await new AddressingAiEngine(client).RunAsync(store, tasks, Options, maximumRequests: 1, settings);
        Assert.Equal(1, client.Calls); Assert.Equal("RequestBudget", first.StoppedBy); Assert.Equal(2, first.Planned);
        Assert.Equal(2, first.State.Proposals.Count); Assert.All(first.State.Proposals, p => Assert.True(p.EvidenceValid));
        var rest = await new AddressingAiEngine(client).RunAsync(store, tasks, Options, maximumRequests: 5, settings);
        Assert.Equal(2, client.Calls); Assert.Equal(1, rest.Planned); Assert.Null(rest.StoppedBy);
        var again = await new AddressingAiEngine(client).RunAsync(store, tasks, Options, maximumRequests: 5, settings);
        Assert.Equal(2, client.Calls); Assert.Equal(0, again.Planned); // Everything verified: nothing is resent.
        var reloaded = await store.LoadAsync();
        Assert.Equal(4, reloaded.Proposals.Count); Assert.Equal(2, reloaded.Attempts.Count); Assert.All(reloaded.Attempts, a => Assert.True(a.Succeeded));
        Assert.DoesNotContain("TASK ", await File.ReadAllTextAsync(store.PathName)); // No prompt or source text is persisted.
    }

    [Fact]
    public async Task Service_failure_stops_the_run_and_is_retried_only_by_a_later_resume_refusal_is_never_resent()
    {
        var tasks = Tasks(); var settings = new AddressingSettings(TasksPerRequest: 2); var store = await Store();
        var outage = new Fake(Replies(tasks, settings)) { Fail = n => new SemanticTransportFailureException(System.Net.HttpStatusCode.ServiceUnavailable) };
        var failed = await new AddressingAiEngine(outage).RunAsync(store, tasks, Options, maximumRequests: 5, settings);
        Assert.Equal(1, outage.Calls); Assert.Equal("Http503", failed.StoppedBy); Assert.Empty(failed.State.Proposals); // No hidden retry, no next batch.
        var refusal = new Fake(Replies(tasks, settings)) { Fail = n => n == 1 ? new GeminiSurveyBlockedException("SAFETY") : null };
        var blocked = await new AddressingAiEngine(refusal).RunAsync(store, tasks, Options, maximumRequests: 5, settings);
        Assert.Equal("ProviderRefusal", blocked.StoppedBy); Assert.Equal(1, refusal.Calls);
        var resume = new Fake(Replies(tasks, settings));
        var done = await new AddressingAiEngine(resume).RunAsync(store, tasks, Options, maximumRequests: 5, settings);
        Assert.Equal(1, resume.Calls); // Only the non-refused request; the refused tasks stay for manual review.
        var refusedTasks = done.State.Attempts.Single(a => a.ErrorCode == "ProviderRefusal").TaskIds;
        Assert.All(refusedTasks, id => Assert.DoesNotContain(done.State.Proposals, p => p.TaskId == id));
    }

    [Fact]
    public async Task Invalid_answers_are_kept_and_retried_once_then_left_for_review()
    {
        var tasks = Tasks().Where(t => t.Kind == AddressingTaskKind.Pair).Take(1).ToArray(); var store = await Store();
        var client = new Fake(prompt =>
        {
            var request = AddressingAiSurvey.PlanRequests(tasks, Options.Model).Single();
            var node = Answer(request); node["pairs"]![0]!["phases"]![0]!["address_zh"] = new JsonArray("哥哥"); return node.ToJsonString();
        });
        for (int i = 0; i < 3; i++) await new AddressingAiEngine(client).RunAsync(store, tasks, Options, maximumRequests: 5);
        Assert.Equal(2, client.Calls); // Answered twice without a verified proposal: no endless resend.
        var state = await store.LoadAsync(); Assert.All(state.Proposals, p => Assert.False(p.EvidenceValid));
    }
}

internal static class JsonNodeTestExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> edit) { edit(node); return node; }
}
