using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>
/// Addressing Ledger step 2b with a mock provider only: CPU scene building, scene prompt, per-turn verification and
/// verified turns feeding pair planning. Romance (quotes without speech verbs) and hard-wrapped xianxia styles.
/// </summary>
public sealed class AddressingSceneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-scene-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly string[] Romantic = ["宁夕", "陆霆骁"];
    // Urban romance: one narration paragraph, then quotes standing alone; the CPU attributes none of them.
    private const string Romance =
        "    宁夕推开门，看到陆霆骁坐在沙发上。\n" +
        "    “你怎么来了？”\n" +
        "    “来看你。”\n" +
        "    “我很好。”\n" +
        "    陆霆骁皱眉：“宁夕，你发烧了。”\n" +
        "    “没有。”\n" +
        "    夜深了，窗外下起了雨。\n" +
        "    宁夕一个人坐在窗前，想着白天发生的事情。\n" +
        "    “宁夕。”\n" +
        "    “嗯？”\n" +
        "    陆霆骁说道：“早点睡。”\n";
    // Who really says each quote, as a careful reader (or a good model) would answer.
    private static readonly Dictionary<string, (string Speaker, string Listener)> RomanceTruth = new()
    {
        ["你怎么来了？"] = ("宁夕", "陆霆骁"), ["来看你。"] = ("陆霆骁", "宁夕"), ["我很好。"] = ("宁夕", "陆霆骁"), ["宁夕，你发烧了。"] = ("陆霆骁", "宁夕"),
        ["没有。"] = ("宁夕", "陆霆骁"), ["宁夕。"] = ("陆霆骁", "宁夕"), ["嗯？"] = ("宁夕", "陆霆骁"), ["早点睡。"] = ("陆霆骁", "宁夕")
    };

    private static readonly string[] Cultivators = ["萧炎", "林动"];
    // Xianxia, hard-wrapped every few characters with indented paragraphs; a soft wrap splits the first quote.
    private const string Xianxia =
        "    萧炎目光一凝，看着林动，沉声道：“你找\n死！”\n" +
        "    “就凭你？”\n" +
        "    “哼，林动，今日便是你的死\n期。”\n" +
        "    “萧炎，你太狂了。”\n" +
        "    林动冷笑道：“来吧。”\n";
    private static readonly Dictionary<string, (string Speaker, string Listener)> XianxiaTruth = new()
    {
        ["你找死！"] = ("萧炎", "林动"), ["就凭你？"] = ("林动", "萧炎"), ["哼，林动，今日便是你的死期。"] = ("萧炎", "林动"),
        ["萧炎，你太狂了。"] = ("林动", "萧炎"), ["来吧。"] = ("林动", "萧炎")
    };

    private static CensusSource[] Book(string chapter, int chapters = 1) =>
        Enumerable.Range(1, chapters).Select(n => new CensusSource(Guid.NewGuid(), n, chapter)).ToArray();

    /// <summary>A scene reply from a quote → (speaker, listener) table; <paramref name="edit"/> may corrupt single turns.</summary>
    internal static JsonObject SceneAnswer(AddressingRequestPlan request, IReadOnlyDictionary<string, (string Speaker, string Listener)> truth,
        Action<AddressingTask, JsonArray>? edit = null)
    {
        var scenes = new JsonArray();
        foreach (var task in request.Tasks.Where(t => t.Kind == AddressingTaskKind.Scene))
        {
            var turns = new JsonArray(task.Samples.Select(s =>
            {
                var (speaker, listener) = truth.TryGetValue(s.Text, out var who) ? who : ("", "");
                return (JsonNode)new JsonObject
                {
                    ["quote_id"] = s.SampleId, ["speaker"] = speaker, ["listener"] = listener,
                    ["self_zh"] = new JsonArray(s.Text.Contains('我') ? ["我"] : Array.Empty<JsonNode>()), ["address_zh"] = new JsonArray()
                };
            }).ToArray());
            edit?.Invoke(task, turns);
            scenes.Add(new JsonObject { ["task_id"] = task.TaskId, ["turns"] = turns, ["reason"] = "Theo lượt thoại." });
        }
        return new JsonObject { ["pairs"] = new JsonArray(), ["characters"] = new JsonArray(), ["scenes"] = scenes };
    }

    private static AddressingRequestPlan Request(params AddressingTask[] tasks) => AddressingAiSurvey.PlanRequests(tasks, "mock-scene").Single();

    [Fact]
    public void Scenes_join_quotes_across_one_short_narration_paragraph_and_break_at_longer_narration()
    {
        var scenes = AddressingDialogueSurvey.Scenes(Book(Romance), Romantic);
        Assert.Equal([5, 3], scenes.Select(s => s.Quotes.Count));
        var first = scenes[0].Quotes;
        Assert.Equal("宁夕推开门，看到陆霆骁坐在沙发上。\n", first[0].Before); // Context, indentation removed.
        Assert.Equal("\n", first[1].Before); // A paragraph break alone: a turn change the model must see.
        Assert.Equal("\n陆霆骁皱眉：", first[3].Before);
        Assert.Contains("夜深了", scenes[1].Quotes[0].Before); Assert.DoesNotContain("没有", scenes[1].Quotes[0].Before); // Never an earlier quote.
        Assert.Equal(["宁夕", "陆霆骁"], scenes[0].Names);
        foreach (var q in scenes.SelectMany(s => s.Quotes)) Assert.Equal(q.Line.Quote, Romance.Substring(q.Line.QuoteStartUtf16, q.Line.QuoteLengthUtf16));
        Assert.Equal([3, 2, 3], AddressingDialogueSurvey.Scenes(Book(Romance), Romantic, maximumQuotes: 3).Select(s => s.Quotes.Count)); // Size limit.
    }

    [Fact]
    public void Scene_tasks_list_participants_number_every_quote_and_keep_explicit_cpu_speakers()
    {
        var romance = AddressingAiSurvey.PlanScenes(Book(Romance), Romantic);
        Assert.Equal(2, romance.Count); // Scene 2 still has two open quotes besides the explicit 陆霆骁说道.
        Assert.Equal(["宁夕", "陆霆骁"], romance[0].Participants);
        Assert.Equal(["Q1", "Q2", "Q3", "Q4", "Q5"], romance[0].Samples.Select(s => s.SampleId));
        Assert.Equal("陆霆骁", romance[1].Samples[2].CpuSpeaker); Assert.Null(romance[1].Samples[0].CpuSpeaker);
        string prompt = Request(romance[0]).Prompt;
        Assert.Contains("PARTICIPANTS: 宁夕, 陆霆骁", prompt);
        Assert.Contains("宁夕推开门，看到陆霆骁坐在沙发上。\n⟦Q1⟧「你怎么来了？」\n⟦Q2⟧「来看你。」", prompt); // The whole passage, verbatim.

        var xianxia = AddressingAiSurvey.PlanScenes(Book(Xianxia), Cultivators).Single();
        Assert.Equal(["萧炎", null, null, null, "林动"], xianxia.Samples.Select(s => s.CpuSpeaker));
        Assert.Equal("你找死！", xianxia.Samples[0].Text); // Soft wrap removed for analysis…
        Assert.Equal("你找\n死！", Xianxia.Substring(xianxia.Samples[0].StartUtf16, 5)); // …offsets still point into the original text.
    }

    [Fact]
    public void Scenes_without_two_open_quotes_or_two_participants_are_not_sent_and_labels_cannot_be_forged()
    {
        const string explicitOnly = "    萧炎说道：“走。”\n    林动说道：“好。”\n    萧炎说道：“快。”\n";
        Assert.Empty(AddressingAiSurvey.PlanScenes(Book(explicitOnly), Cultivators)); // The CPU already knows every speaker.
        const string monologue = "    宁夕看着窗外。\n    “下雨了。”\n    “真冷。”\n";
        Assert.Empty(AddressingAiSurvey.PlanScenes(Book(monologue), Romantic)); // One participant: nothing to decide.
        string forged = Romance.Replace("夜深了", "⟦Q99⟧夜深了");
        Assert.DoesNotContain("⟦Q99⟧", string.Concat(AddressingAiSurvey.PlanRequests(AddressingAiSurvey.PlanScenes(Book(forged), Romantic), "m").Select(r => r.Prompt)));
        Assert.Empty(AddressingAiSurvey.PlanScenes(Book(Romance), Romantic, new(MaximumScenes: 0)));
    }

    [Fact]
    public void Long_context_is_clipped_at_a_sentence_boundary()
    {
        string narration = string.Concat(Enumerable.Repeat("宁夕看着窗外的雨，想起了很多很多年以前的事情。", 12)); // ~290 characters, one paragraph.
        var scene = AddressingDialogueSurvey.Scenes(Book("    " + narration + "\n    “你来了？”\n    “嗯。”\n"), Romantic).Single();
        string context = scene.Quotes[0].Before;
        Assert.InRange(context.Length, 20, 240);
        Assert.StartsWith("宁夕看着窗外的雨", context); // Not mid-word.
        Assert.EndsWith("事情。\n", context);
    }

    [Fact]
    public void Scene_selection_is_spread_over_chapters()
    {
        var tasks = AddressingAiSurvey.PlanScenes(Book(Romance, 3), Romantic, new(MaximumScenes: 3));
        Assert.Equal([1, 2, 3], tasks.Select(t => t.FirstChapter)); // One per chapter before a second from any chapter.
    }

    [Fact]
    public void Correct_scene_answers_are_verified_turn_by_turn()
    {
        var task = AddressingAiSurvey.PlanScenes(Book(Xianxia), Cultivators).Single(); var request = Request(task);
        var proposal = AddressingAiSurvey.Verify(SceneAnswer(request, XianxiaTruth).ToJsonString(), request).Single();
        Assert.True(proposal.EvidenceValid, string.Join(",", proposal.Issues)); Assert.Equal("Resolved", proposal.Status); Assert.Empty(proposal.Issues);
        Assert.Equal(["萧炎", "林动", "萧炎", "林动", "林动"], proposal.Turns.Select(t => t.Speaker));
        Assert.All(proposal.Turns, t => Assert.True(t.IsUsable()));
        Assert.Equal(task.Samples.Select(s => s.QuoteHash), proposal.Turns.Select(t => t.QuoteHash));
    }

    public static TheoryData<string, string> SingleTurnFaults => new()
    {
        { "invented-name", "UnlistedParticipant" }, { "speaker-addressed-in-own-quote", "SpeakerNamedInOwnQuote" },
        { "speaker-is-listener", "SpeakerIsListener" }, { "listener-not-the-one-addressed", "ListenerConflictsWithVocative" },
        { "unquoted-term", "DroppedUnquotedTerm" }, { "duplicate-quote", "DuplicateQuote" }, { "unknown-quote", "UnknownQuote" }
    };

    [Theory]
    [MemberData(nameof(SingleTurnFaults))]
    public void One_wrong_turn_is_rejected_with_its_code_without_sinking_the_scene(string fault, string code)
    {
        var task = AddressingAiSurvey.PlanScenes(Book(Romance), Romantic)[0]; var request = Request(task);
        var answer = SceneAnswer(request, RomanceTruth, (_, turns) =>
        {
            // Q4 is “宁夕，你发烧了。” said by 陆霆骁 to 宁夕.
            switch (fault)
            {
                case "invented-name": turns[1]!["speaker"] = "陆总"; break;
                case "speaker-addressed-in-own-quote": turns[3]!["speaker"] = "宁夕"; turns[3]!["listener"] = ""; break;
                case "speaker-is-listener": turns[1]!["listener"] = "陆霆骁"; break;
                case "listener-not-the-one-addressed": turns[3]!["speaker"] = ""; turns[3]!["listener"] = "陆霆骁"; break;
                case "unquoted-term": turns[4]!["address_zh"] = new JsonArray("老公"); break;
                case "duplicate-quote": turns.Add(turns[0]!.DeepClone()); break;
                case "unknown-quote": turns.Add(turns[0]!.DeepClone().AsObject().Also(n => n["quote_id"] = "Q9")); break;
            }
        });
        var proposal = AddressingAiSurvey.Verify(answer.ToJsonString(), request).Single();
        Assert.Contains(code, proposal.Issues);
        Assert.True(proposal.EvidenceValid, string.Join(",", proposal.Issues)); // The other turns stand.
        Assert.InRange(proposal.Turns.Count(t => t.IsUsable()), 3, 5);
        Assert.DoesNotContain(proposal.Turns, t => t.IsUsable() && t.Issues.Count > 0);
    }

    [Fact]
    public void An_invented_chinese_term_is_dropped_from_a_correct_turn_but_still_rejects_a_wrong_one()
    {
        // Live Gemini case: right speaker, but self_zh 我 added to a quote that has no 我.
        var task = AddressingAiSurvey.PlanScenes(Book(Xianxia), Cultivators).Single(); var request = Request(task);
        var answer = SceneAnswer(request, XianxiaTruth, (_, t) =>
        {
            t[1]!["self_zh"] = new JsonArray("我"); t[1]!["address_zh"] = new JsonArray("你");     // Q2 就凭你？: 我 invented, 你 verbatim.
            t[2]!["speaker"] = "林动"; t[2]!["address_zh"] = new JsonArray("老夫");                // Q3: wrong speaker and invented term.
        });
        var proposal = AddressingAiSurvey.Verify(answer.ToJsonString(), request).Single();
        var kept = proposal.Turns[1];
        Assert.True(kept.IsUsable()); Assert.Equal("林动", kept.Speaker);
        Assert.Empty(kept.SelfZh); Assert.Equal(["你"], kept.AddressZh); Assert.Equal(["我"], kept.DroppedTerms);
        var rejected = proposal.Turns[2];
        Assert.False(rejected.IsUsable()); Assert.Contains("UnquotedChineseTerm", rejected.Issues); Assert.Contains("SpeakerNamedInOwnQuote", rejected.Issues);
        Assert.Contains("DroppedUnquotedTerm", proposal.Issues); Assert.True(proposal.EvidenceValid);
    }

    [Fact]
    public void Contradicting_an_explicit_speech_verb_is_rejected_and_a_mostly_wrong_scene_is_rejected_whole()
    {
        var task = AddressingAiSurvey.PlanScenes(Book(Xianxia), Cultivators).Single(); var request = Request(task);
        var flipped = AddressingAiSurvey.Verify(SceneAnswer(request, XianxiaTruth, (_, t) => { t[0]!["speaker"] = "林动"; t[0]!["listener"] = "萧炎"; }).ToJsonString(), request).Single();
        Assert.Contains("ConflictsWithCpuSpeaker", flipped.Turns[0].Issues); Assert.True(flipped.EvidenceValid);

        var swapped = XianxiaTruth.ToDictionary(x => x.Key, x => (x.Value.Listener, x.Value.Speaker)); // Every speaker swapped.
        var wrong = AddressingAiSurvey.Verify(SceneAnswer(request, swapped).ToJsonString(), request).Single();
        Assert.False(wrong.EvidenceValid); Assert.Equal("Ambiguous", wrong.Status); Assert.Contains("TooManyRejectedTurns", wrong.Issues);

        var unsure = AddressingAiSurvey.Verify(SceneAnswer(request, new Dictionary<string, (string, string)>()).ToJsonString(), request).Single();
        Assert.False(unsure.EvidenceValid); Assert.Empty(unsure.Issues); // "" everywhere is honest, not an error, but proves nothing.
    }

    [Fact]
    public void Scene_reply_structure_is_strict()
    {
        var request = Request(AddressingAiSurvey.PlanScenes(Book(Xianxia), Cultivators).Single());
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            n => n.Remove("scenes"), n => n["scenes"]![0]!["turns"]![0]!["confidence"] = "high", n => n["scenes"]![0]!["turns"]![0]!.AsObject().Remove("listener"),
            n => n["scenes"]![0]!["task_id"] = "scene:unknown", n => n["scenes"]![0]!["turns"]![0]!["speaker"] = 1 })
        {
            var node = SceneAnswer(request, XianxiaTruth); change(node);
            Assert.Throws<InvalidDataException>(() => AddressingAiSurvey.Verify(node.ToJsonString(), request));
        }
    }

    private sealed class Mock(Func<string, string> reply) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal(AddressingAiSurvey.Schema.GetRawText(), schema.GetRawText());
            return Task.FromResult(new OllamaCompletion(reply(prompt), true, "stop", new GenerationMetrics { InputTokens = 1200, OutputTokens = 400 }));
        }
    }

    [Fact]
    public async Task Verified_turns_unlock_pair_tasks_the_cpu_could_not_build_and_never_survive_a_source_edit()
    {
        var sources = Book(Romance, 2);
        Assert.DoesNotContain(AddressingAiSurvey.PlanTasks(sources, Romantic), t => t.Kind == AddressingTaskKind.Pair); // CPU alone: no pair.
        var scenes = AddressingAiSurvey.PlanScenes(sources, Romantic);
        var client = new Mock(prompt =>
        {
            var ids = Regex.Matches(prompt, @"(?m)^TASK (\S+) ").Select(m => m.Groups[1].Value).ToHashSet();
            return SceneAnswer(AddressingAiSurvey.PlanRequests(scenes.Where(t => ids.Contains(t.TaskId)).ToArray(), AddressingAiSurveyTests.Options.Model).Single(), RomanceTruth).ToJsonString();
        });
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, "p"), "Synthetic scenes");
        var store = new AddressingLedgerStore(session);
        var run = await new AddressingAiEngine(client).RunAsync(store, scenes, AddressingAiSurveyTests.Options, maximumRequests: 5);
        Assert.Equal(1, client.Calls); Assert.All(run.State.Proposals, p => Assert.True(p.EvidenceValid));
        Assert.Equal(0, (await new AddressingAiEngine(client).RunAsync(store, scenes, AddressingAiSurveyTests.Options, 5)).Planned); // Resolved: never resent.
        // A name discovered later (常莉 in the real book) changes the participant list: only that scene is asked again.
        var widened = scenes.Select((t, i) => i == 0 ? t with { Subject = t.Subject + "、常莉", Participants = [.. t.Participants, "常莉"] } : t).ToArray();
        var state = await store.LoadAsync();
        Assert.False(state.Done(widened[0])); Assert.All(widened.Skip(1), t => Assert.True(state.Done(t)));

        var turns = (await store.LoadAsync()).SceneTurns(); // Survives the JSON round trip.
        Assert.Equal(16, turns.Count);
        var tasks = AddressingAiSurvey.PlanTasks(sources, Romantic, sceneTurns: turns);
        var heroine = tasks.Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕" && t.Listener == "陆霆骁");
        Assert.Equal(8, heroine.Samples.Count); Assert.All(heroine.Samples, s => Assert.True(s.FromScene));
        Assert.Contains(" FROM-SCENE LEAD:", AddressingAiSurvey.PlanRequests([heroine], "m").Single().Prompt);
        Assert.Contains(tasks, t => t.Kind == AddressingTaskKind.Pair && t.Subject == "陆霆骁" && t.Listener == "宁夕");

        // Same offsets, different words: the old answer must not attribute the edited quote.
        var edited = new[] { sources[0] with { OriginalText = Romance.Replace("我很好。", "我没事。") }, sources[1] };
        var after = AddressingAiSurvey.PlanTasks(edited, Romantic, sceneTurns: turns)
            .Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕" && t.Listener == "陆霆骁");
        Assert.Equal(7, after.Samples.Count); Assert.DoesNotContain(after.Samples, s => s.Text == "我没事。");
    }

    [Fact]
    public void A_turn_never_overrides_an_explicit_cpu_speaker_or_a_vocative_listener()
    {
        var sources = Book(Xianxia, 3);
        var turns = AddressingAiSurvey.PlanScenes(sources, Cultivators).SelectMany(task => task.Samples.Select(s =>
            new AddressingTurn(s.SampleId, s.ChapterNumber, s.StartUtf16, s.QuoteHash, "林动", "萧炎", [], [], []))).ToArray(); // Unverified, all 林动.
        var tasks = AddressingAiSurvey.PlanTasks(sources, Cultivators, sceneTurns: turns);
        var xiao = tasks.Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "萧炎");
        Assert.Equal(3, xiao.Samples.Count); Assert.All(xiao.Samples, s => Assert.Equal("你找死！", s.Text)); // Explicit 萧炎 lines stay 萧炎.
        Assert.DoesNotContain(tasks, t => t.Kind == AddressingTaskKind.Pair && t.Subject == "林动" && t.Listener == "林动");
    }
}
