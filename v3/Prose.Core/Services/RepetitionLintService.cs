using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Prose.Core.Data;

namespace Prose.Core.Services;

public record RepetitionLintReport(
    string NodeCode,
    int BeatsScanned,
    int EchoFindings,
    int PhraseFindings,
    int PetWordFindings,
    int DialogueFindings,
    int StructureFindings,
    IReadOnlyList<string> Lines);

/// <summary>
/// Deterministic prose linter — zero LLM cost, pure CPU over a book's beats in reading order.
/// Before 2026-08-28 the engine had NO mechanical repetition detection anywhere: every
/// "don't echo", "vary your phrasing" rule was a prompt-side plea with no verification.
///
/// Checks:
///  1. Echo words — a distinctive word repeated in close proximity within one beat.
///  2. Crutch phrases — a distinctive 3-4-gram recurring within a beat or across a chapter.
///  3. Pet words — a distinctive word appearing in an outsized share of the book's beats.
///  4. Dialogue attribution — long runs of consecutive quoted paragraphs with no tag or
///     action beat (the "reader must know who's speaking" rule, previously prompt-only).
///  5. Airless narration — long runs of consecutive beats with ~zero dialogue, and
///     "floating heads" beats (very high dialogue proportion in a long beat), from the
///     already-computed-but-never-consumed BeatProseMetrics.DialogueProportion.
///
/// Findings are filed under FindingCategory.CraftChecklist with a "LINT " summary prefix on
/// FilePath "node:{slug}" — the exact shape ProseWriterRouter.BuildFindingsGuidanceAsync
/// loops back into future generation (same idiom as READABILITY). Re-runs delete-and-refile
/// (idempotent); character/place names are exempted from word checks via entity names.
/// </summary>
public class RepetitionLintService
{
    private readonly IDbContextFactory<ProseDbContext> dbFactory;
    private readonly FindingsService findings;
    private readonly ILogger<RepetitionLintService> log;

    public RepetitionLintService(
        IDbContextFactory<ProseDbContext> dbFactory,
        FindingsService findings,
        ILogger<RepetitionLintService> log)
    {
        this.dbFactory = dbFactory;
        this.findings = findings;
        this.log = log;
    }

    private const string LintPrefix = "LINT ";

    // Proximity echo: same distinctive word twice within this many tokens.
    private const int EchoWindowTokens = 50;
    // A word must recur at least this many times in one beat to be an echo finding.
    private const int EchoMinOccurrences = 3;
    // Crutch phrase: n-gram length range and thresholds.
    private const int PhraseMinPerBeat = 2;
    private const int PhraseMinPerChapter = 3;
    // Pet word: appears in at least this share of the book's beats (and ≥20 beats scanned).
    private const double PetWordBeatShare = 0.30;
    // Dialogue attribution: this many consecutive quoted-only paragraphs with no tag/action.
    private const int UnattributedRunFloor = 5;
    // Airless narration: consecutive beats below this dialogue proportion.
    private const double AirlessDialogueFloor = 0.02;
    private const int AirlessRunFloor = 6;
    // Floating heads: dialogue proportion above this in a beat this long.
    private const double FloatingHeadsProportion = 0.90;
    private const int FloatingHeadsMinWords = 600;

    private static readonly Regex WordRx = new(@"\b[a-zA-Z''’]+\b", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"[“”""]", RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","that","with","this","from","they","them","their","there","then","than",
        "have","has","had","was","were","been","being","are","is","not","but","for","you",
        "your","his","her","hers","she","him","its","it's","what","when","where","which",
        "who","whom","whose","will","would","could","should","can","may","might","must",
        "into","onto","over","under","about","after","before","between","through","against",
        "because","while","until","again","also","just","only","even","still","back","down",
        "out","off","up","all","any","some","more","most","other","another","each","every",
        "very","too","how","why","here","now","one","two","like","said","says","did","does",
        "doesn't","don't","didn't","wasn't","isn't","aren't","won't","can't","couldn't",
        "wouldn't","shouldn't","around","across","away","along","behind","beneath","beside",
        "himself","herself","itself","themselves","something","nothing","anything","everything",
        "someone","anyone","everyone","never","always","once","twice","first","last","next",
        "own","same","such","both","few","many","much","those","these","our","ours","mine",
        "yours","theirs","let","lets","let's","get","got","gets","getting","made","make",
        "makes","making","went","gone","going","come","comes","coming","came","know","knows",
        "knew","known","think","thinks","thought","see","sees","saw","seen","look","looks",
        "looked","looking","say","saying","tell","tells","told","asked","asks","ask",
    };

    public async Task<RepetitionLintReport> LintAsync(
        string slugOrCode, bool dryRun = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(
            n => n.Slug == slugOrCode || (n.NodeCode != null && n.NodeCode.ToUpper() == slugOrCode.ToUpper()), ct)
            ?? throw new InvalidOperationException($"Node not found: {slugOrCode}");
        var nodeCode = node.NodeCode?.ToUpperInvariant() ?? node.Slug.ToUpperInvariant();
        var fp = $"node:{node.Slug}";

        var searchIds = await NodeWorkbenchService.GetLeafDescendantIdsAsync(db, node.Id, ct);
        var beats = await (
            from bn in db.BeatNodes.AsNoTracking()
            join b in db.Beats.AsNoTracking() on bn.BeatId equals b.Id
            join c in db.Nodes.AsNoTracking() on bn.NodeId equals c.Id
            where searchIds.Contains(bn.NodeId) && b.Text != null && b.Text != ""
            orderby c.SortKey, bn.SortKey
            select new { b.Id, b.Number, Text = b.Text!, Chapter = c.Title, ChapterId = c.Id, b.StoryPosition }
        ).ToListAsync(ct);

        // Entity-name exemption: a character/place name repeating is normal prose, not an echo.
        var entityNameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entityRows = await db.Set<Data.Entities.Entity>().AsNoTracking()
            .Where(e => e.UniverseId == node.UniverseId)
            .Select(e => new { e.Id, e.Name, e.EntityType }).ToListAsync(ct);
        foreach (var e in entityRows)
            foreach (Match m in WordRx.Matches(e.Name))
                entityNameTokens.Add(m.Value);

        if (!dryRun) findings.DeleteBySummaryPrefix(fp, LintPrefix);

        var lines = new List<string>();
        int echoCount = 0, phraseCount = 0, petCount = 0, dialogueCount = 0, structureCount = 0;

        void File(FindingSeverity sev, string summary, string? snippet = null, string? fix = null)
        {
            lines.Add(summary);
            if (!dryRun) findings.Upsert(fp, chapterId: null, FindingCategory.CraftChecklist, sev,
                LintPrefix + summary, snippet, fix);
        }

        // ── per-beat checks ────────────────────────────────────────────────────
        var beatsContainingWord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var chapterPhrases = new Dictionary<string, Dictionary<string, int>>(); // chapter -> phrase -> count

        foreach (var beat in beats)
        {
            var stripped = BeatMarkup.StripEntityTags(beat.Text);
            var tokens = WordRx.Matches(stripped).Select(m => m.Value).ToList();

            // 1. Echo words in proximity
            var positions = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Length < 5 || Stopwords.Contains(t) || entityNameTokens.Contains(t)) continue;
                if (!positions.TryGetValue(t, out var list)) positions[t] = list = new List<int>();
                list.Add(i);
            }
            foreach (var (word, pos) in positions)
            {
                if (pos.Count < EchoMinOccurrences) continue;
                var proximityPairs = 0;
                for (int i = 1; i < pos.Count; i++)
                    if (pos[i] - pos[i - 1] <= EchoWindowTokens) proximityPairs++;
                if (proximityPairs >= EchoMinOccurrences - 1)
                {
                    echoCount++;
                    File(FindingSeverity.Low,
                        $"beat #{beat.Number}: \"{word.ToLowerInvariant()}\" echoes {pos.Count}x in close proximity — vary or cut.",
                        fix: "Replace repeats with a synonym, pronoun, or restructure so the word carries once.");
                }
            }

            // 2. Crutch phrases (3-4-grams) within the beat + accumulate per chapter
            var beatPhrases = CountNgrams(tokens);
            foreach (var (phrase, count) in beatPhrases)
            {
                if (count >= PhraseMinPerBeat)
                {
                    phraseCount++;
                    File(FindingSeverity.Low,
                        $"beat #{beat.Number}: phrase \"{phrase}\" appears {count}x in one beat — crutch phrase.",
                        fix: "Keep the strongest instance; rewrite the others.");
                }
                if (!chapterPhrases.TryGetValue(beat.Chapter, out var chMap))
                    chapterPhrases[beat.Chapter] = chMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                chMap[phrase] = chMap.GetValueOrDefault(phrase) + count;
            }

            // 4. Dialogue attribution: consecutive quoted-only paragraphs without a tag/action
            var paragraphs = stripped.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int run = 0, worstRun = 0;
            foreach (var p in paragraphs)
            {
                bool quotedOnly = QuoteRx.IsMatch(p) && IsQuotedOnly(p);
                run = quotedOnly ? run + 1 : 0;
                worstRun = Math.Max(worstRun, run);
            }
            if (worstRun >= UnattributedRunFloor)
            {
                dialogueCount++;
                File(FindingSeverity.Medium,
                    $"beat #{beat.Number}: {worstRun} consecutive dialogue paragraphs with no attribution or action beat — reader loses the speaker.",
                    fix: "Break the run with an action beat or attribution every 3-4 exchanges.");
            }

            // pet-word accumulation
            foreach (var w in positions.Keys.Distinct(StringComparer.OrdinalIgnoreCase))
                beatsContainingWord[w] = beatsContainingWord.GetValueOrDefault(w) + 1;
        }

        // 2b. Chapter-level crutch phrases
        foreach (var (chapter, phrases) in chapterPhrases)
        {
            foreach (var (phrase, count) in phrases.Where(p => p.Value >= PhraseMinPerChapter).Take(5))
            {
                phraseCount++;
                File(FindingSeverity.Low,
                    $"chapter '{chapter}': phrase \"{phrase}\" appears {count}x across the chapter — crutch phrase.",
                    fix: "Keep at most one instance per chapter.");
            }
        }

        // 3. Book-level pet words
        if (beats.Count >= 20)
        {
            var petWords = beatsContainingWord
                .Where(kv => (double)kv.Value / beats.Count >= PetWordBeatShare)
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => $"{kv.Key.ToLowerInvariant()} ({kv.Value}/{beats.Count} beats)")
                .ToList();
            if (petWords.Count > 0)
            {
                petCount++;
                File(FindingSeverity.Low,
                    $"pet words — distinctive words appearing in ≥{PetWordBeatShare:P0} of beats: {string.Join(", ", petWords)}.",
                    fix: "These are the book's tics; thin them where two land close together.");
            }
        }

        // 5. Airless narration runs + floating heads (from persisted BeatProseMetrics)
        var beatIds = beats.Select(b => b.Id).ToList();
        var metricsById = await db.BeatProseMetrics.AsNoTracking()
            .Where(m => beatIds.Contains(m.BeatId))
            .ToDictionaryAsync(m => m.BeatId, ct);
        int airlessRun = 0; int airlessStartNumber = 0;
        foreach (var beat in beats)
        {
            if (!metricsById.TryGetValue(beat.Id, out var m)) { airlessRun = 0; continue; }
            if (m.DialogueProportion < AirlessDialogueFloor && m.WordCount > 150)
            {
                if (airlessRun == 0) airlessStartNumber = beat.Number;
                airlessRun++;
            }
            else
            {
                if (airlessRun >= AirlessRunFloor)
                {
                    dialogueCount++;
                    File(FindingSeverity.Low,
                        $"beats #{airlessStartNumber}–#{beat.Number}: {airlessRun} consecutive beats with almost no dialogue — airless narration run.",
                        fix: "Let characters speak — break summary/narration with scene.");
                }
                airlessRun = 0;
            }
            if (m.DialogueProportion >= FloatingHeadsProportion && m.WordCount >= FloatingHeadsMinWords)
            {
                dialogueCount++;
                File(FindingSeverity.Low,
                    $"beat #{beat.Number}: {m.DialogueProportion:P0} dialogue over {m.WordCount} words — floating heads; ground the scene in bodies and place.",
                    fix: "Interleave physical action, setting, and interiority between exchanges.");
            }
        }
        if (airlessRun >= AirlessRunFloor)
        {
            dialogueCount++;
            File(FindingSeverity.Low,
                $"beats #{airlessStartNumber}–(end): {airlessRun} consecutive beats with almost no dialogue — airless narration run.",
                fix: "Let characters speak — break summary/narration with scene.");
        }

        // ── 6. Structural checks (2026-09-05) ──────────────────────────────────
        // Every real BCODA defect on record — Ch7's fight told three times, Ch1's orphaned
        // elevator scene, Ch12's #5229 and #5346, the togishi "first time" line — was a
        // superseded draft left in the reading order after a rewrite pass. None of them was
        // caught by any instrument; all of them were caught by reading a chapter whole. These
        // four checks are the mechanical version of that read. They are flags for a reader, not
        // verdicts, and each one prints what it examined so a zero is never mistaken for "clean".
        if (beats.Count == 0)
        {
            lines.Add("[structure] COULD NOT LOOK — 0 beats with prose.");
        }
        else
        {
            structureCount += StructuralChecks(beats.Select((b, i) => new StructBeat(
                    i, b.Id, b.Number, b.Chapter, b.ChapterId, b.StoryPosition, b.Text)).ToList(),
                node.NodeOutline, entityRows.Select(e => (e.Id, e.Name, e.EntityType)).ToList(),
                entityNameTokens, File, lines);
        }

        log.LogInformation("[RepetitionLint] {Code}: {Beats} beats, {Echo} echo, {Phrase} phrase, {Pet} pet-word, {Dlg} dialogue, {Struct} structure findings",
            nodeCode, beats.Count, echoCount, phraseCount, petCount, dialogueCount, structureCount);
        return new RepetitionLintReport(nodeCode, beats.Count, echoCount, phraseCount, petCount, dialogueCount, structureCount, lines);
    }

    private sealed record StructBeat(int Index, Guid Id, int Number, string Chapter, Guid ChapterId, int? StoryPosition, string Text);

    // Alternate-scene detector thresholds. Two distinct shared 8-word runs, or one run plus two
    // shared distinctive markers (timestamps / quoted lines), between beats within this many
    // positions of each other in the same chapter. A single shared run alone is NOT enough — a
    // deliberate refrain ("CORRIDOR AUDIT COMPLETE. THE CHOICE AT 6.2% IS LOGGED." recurs three
    // times in BCODA Ch12 by design) must not read as a duplicate scene.
    private const int AltSceneWindow = 6;
    private const int AltSceneGramLen = 8;
    private const double AltSceneJaccard = 0.40;
    private const int AltSceneMinTokens = 120;
    // Entities present in this share of beats (the protagonist, his weapons) are exempt from the
    // first-time check — they are in every window by construction.
    private const double UbiquitousEntityShare = 0.30;

    private static readonly Regex ChapterNumRx = new(@"^\s*Chapter\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"\b\d{1,2}:\d\d\b", RegexOptions.Compiled);
    private static readonly Regex QuoteSpanRx = new(@"[“""]([^”""\n]{16,200})[”""]", RegexOptions.Compiled);
    private static readonly Regex OutlineChapterRx = new(@"\*\*Ch(\d+)\s*[-–—:]\s*([^*\n]{2,160})\*\*", RegexOptions.Compiled);
    private static readonly Regex OutlineBeatRefRx = new(@"\bbeats?\s*#?\s*(\d{3,5})((?:\s*[/,]\s*#?\d{3,5})*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FirstTimeRx = new(@"\b(for the first time|had never (?:spoken|met|seen)|never spoken to|never met)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidAttrRx = new(@"guid=""([0-9a-fA-F-]{36})""", RegexOptions.Compiled);
    private static readonly Regex WsRx = new(@"\s+", RegexOptions.Compiled);

    private static string Norm(string s) => WsRx.Replace(s, " ").Trim().ToLowerInvariant();

    private int StructuralChecks(
        List<StructBeat> beats, string? outline,
        List<(Guid Id, string Name, string EntityType)> entities,
        HashSet<string> entityNameTokens,
        Action<FindingSeverity, string, string?, string?> file,
        List<string> lines)
    {
        int count = 0;

        // Per-beat derived data, computed once.
        var stripped = beats.Select(b => BeatMarkup.StripEntityTags(b.Text)).ToList();
        var strippedLower = stripped.Select(Norm).ToList();
        var tokens = stripped.Select(s => WordRx.Matches(s).Select(m => m.Value.ToLowerInvariant()).ToList()).ToList();
        var grams = tokens.Select(t =>
        {
            var set = new HashSet<string>();
            for (int i = 0; i + AltSceneGramLen <= t.Count; i++)
                set.Add(string.Join(' ', t.Skip(i).Take(AltSceneGramLen)));
            return set;
        }).ToList();
        var distinct = tokens.Select(t => t.Where(w => w.Length >= 5 && !Stopwords.Contains(w) && !entityNameTokens.Contains(w)).ToHashSet()).ToList();
        var markers = stripped.Select(s =>
        {
            var set = new HashSet<string>();
            foreach (Match m in TimeRx.Matches(s)) set.Add("t:" + m.Value);
            foreach (Match m in QuoteSpanRx.Matches(s)) set.Add("q:" + Norm(m.Groups[1].Value));
            return set;
        }).ToList();

        // 6a. Alternate scene / repeated passage — same event told twice within a chapter window.
        int pairs = 0;
        var pairSeen = new HashSet<(int, int)>();
        for (int i = 0; i < beats.Count; i++)
        {
            for (int j = i + 1; j < beats.Count && j <= i + AltSceneWindow; j++)
            {
                if (beats[j].ChapterId != beats[i].ChapterId) break;
                pairs++;
                var sharedGrams = grams[i].Intersect(grams[j]).ToList();
                var sharedMarkers = markers[i].Intersect(markers[j]).ToList();
                double jaccard = 0;
                if (tokens[i].Count >= AltSceneMinTokens && tokens[j].Count >= AltSceneMinTokens)
                {
                    var inter = distinct[i].Intersect(distinct[j]).Count();
                    var union = distinct[i].Union(distinct[j]).Count();
                    jaccard = union == 0 ? 0 : (double)inter / union;
                }
                bool literal = sharedGrams.Count >= 2 || (sharedGrams.Count >= 1 && sharedMarkers.Count >= 2);
                bool lexical = jaccard >= AltSceneJaccard;
                if (!literal && !lexical) continue;
                if (!pairSeen.Add((i, j))) continue;
                count++;
                var why = literal
                    ? $"{sharedGrams.Count} shared {AltSceneGramLen}-word run(s)" + (sharedMarkers.Count > 0 ? $", {sharedMarkers.Count} shared marker(s)" : "")
                    : $"{jaccard:P0} shared distinctive vocabulary";
                var snippet = sharedGrams.FirstOrDefault() ?? sharedMarkers.FirstOrDefault()?[2..] ?? string.Join(", ", distinct[i].Intersect(distinct[j]).Take(8));
                file(literal ? FindingSeverity.High : FindingSeverity.Medium,
                    $"ALT-SCENE beats #{beats[i].Number} and #{beats[j].Number} ({beats[i].Chapter}): {why} — possibly the same scene told twice (a superseded draft left in the reading order). Read both before deciding.",
                    snippet,
                    "If one is an earlier draft of the other, delete it (or trim it to the part the survivor needs, as with Ch7 #2856). If both belong, no change.");
            }
        }
        lines.Add($"[structure] alt-scene: examined {pairs} beat pair(s) across {beats.Select(b => b.ChapterId).Distinct().Count()} chapter(s).");

        // 6b. Outline literal hooks — every **ChN - …** entry's timestamps, quoted lines and
        // beat refs must exist in chapter N's prose. Zero hits means the prose dropped it or the
        // outline is stale; either way the author decides, and the instrument must not.
        var chapterBeats = new Dictionary<int, List<int>>();
        for (int i = 0; i < beats.Count; i++)
        {
            var m = ChapterNumRx.Match(beats[i].Chapter ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                (chapterBeats.TryGetValue(n, out var l) ? l : chapterBeats[n] = new List<int>()).Add(i);
        }
        var allNumbers = beats.Select(b => b.Number).ToHashSet();
        int hooks = 0;
        if (string.IsNullOrWhiteSpace(outline))
        {
            lines.Add("[structure] outline-hook: COULD NOT LOOK — node has no outline.");
        }
        else
        {
            var o = BeatMarkup.StripEntityTags(outline);
            var entries = OutlineChapterRx.Matches(o).Cast<Match>().ToList();
            for (int k = 0; k < entries.Count; k++)
            {
                var e = entries[k];
                if (!int.TryParse(e.Groups[1].Value, out var chNum)) continue;
                var end = k + 1 < entries.Count ? entries[k + 1].Index : Math.Min(o.Length, e.Index + 1500);
                var block = o[e.Index..Math.Min(end, e.Index + 1500)];
                // An entry ends where the outline's next numbered item or bold header begins — the
                // Ch12 entry otherwise swallowed item 6 ("LOOK AT THE CENTER", which the outline
                // itself places in Ch26) and filed three false OUTLINE-HOOK findings against Ch12.
                var nextItem = Regex.Match(block[Math.Min(block.Length, e.Length)..], @"\n\s*(?:\d+\.\s+\*\*|\*\*[A-Z#])");
                if (nextItem.Success) block = block[..(e.Length + nextItem.Index)];
                // Italic parenthetical revision notes — "*(Revised 2026-09-05. The "01:14" entry
                // was cut…)*" — are the outline talking about itself, not naming things the
                // chapter must contain. Strip them before harvesting literals.
                block = Regex.Replace(block, @"\*\((?:[^()]|\([^()]*\))*\)\*", " ");
                var literals = new List<(string Kind, string Value)>();
                foreach (Match m in TimeRx.Matches(block)) literals.Add(("time", m.Value));
                foreach (Match m in QuoteSpanRx.Matches(block))
                    if (WordRx.Matches(m.Groups[1].Value).Count >= 4) literals.Add(("quote", Norm(m.Groups[1].Value)));
                foreach (Match m in OutlineBeatRefRx.Matches(block))
                {
                    // A ref the outline itself marks as cut/archived/deleted is provenance, not a
                    // claim that the beat is in the book — e.g. "(beats 4368/512/497, archived V68)".
                    var tail = block[m.Index..Math.Min(block.Length, m.Index + m.Length + 80)];
                    if (Regex.IsMatch(tail, @"\b(cut|archived|deleted|removed|retired|superseded)\b", RegexOptions.IgnoreCase)) continue;
                    literals.Add(("beat", m.Groups[1].Value));
                    foreach (Match x in Regex.Matches(m.Groups[2].Value, @"\d{3,5}")) literals.Add(("beat", x.Value));
                }
                if (literals.Count == 0) continue;
                hooks += literals.Count;

                if (!chapterBeats.TryGetValue(chNum, out var idxs) || idxs.Count == 0)
                {
                    lines.Add($"[structure] outline-hook Ch{chNum}: COULD NOT LOOK — no beats under a node titled 'Chapter {chNum}'.");
                    continue;
                }
                foreach (var (kind, value) in literals.Distinct())
                {
                    bool found = kind switch
                    {
                        "beat" => int.TryParse(value, out var bn) && allNumbers.Contains(bn),
                        "time" => idxs.Any(i => stripped[i].Contains(value)),
                        _ => idxs.Any(i => strippedLower[i].Contains(value)),
                    };
                    if (found) continue;
                    count++;
                    var shown = value.Length > 90 ? value[..90] + "…" : value;
                    file(FindingSeverity.Medium,
                        kind == "beat"
                            ? $"OUTLINE-HOOK Ch{chNum} \"{e.Groups[2].Value.Trim()}\": the outline cites beat #{value}, which is not in this book."
                            : $"OUTLINE-HOOK Ch{chNum} \"{e.Groups[2].Value.Trim()}\": the outline names {kind} \"{shown}\" for this chapter; no beat in Chapter {chNum} contains it (examined {idxs.Count} beat(s)).",
                        null,
                        "Either the prose dropped what the outline plans, or the outline is stale about what the book does. Show the author both sides; do not pick a winner here.");
                }
            }
            lines.Add($"[structure] outline-hook: examined {hooks} literal(s) across {entries.Count} outline chapter entr(y/ies).");
        }

        // 6c. "First time" that isn't — a first-meeting phrase near an entity that already
        // appeared earlier in reading order. Ubiquitous entities are exempt.
        var tagCounts = new Dictionary<Guid, int>();
        var firstTagIdx = new Dictionary<Guid, int>();
        for (int i = 0; i < beats.Count; i++)
            foreach (var g in GuidAttrRx.Matches(beats[i].Text).Cast<Match>().Select(m => Guid.TryParse(m.Groups[1].Value, out var gg) ? gg : Guid.Empty).Where(gg => gg != Guid.Empty).Distinct())
            {
                tagCounts[g] = tagCounts.GetValueOrDefault(g) + 1;
                if (!firstTagIdx.ContainsKey(g)) firstTagIdx[g] = i;
            }
        var byId = entities.ToDictionary(e => e.Id);
        string? HeadWord(string name)
        {
            foreach (Match m in WordRx.Matches(name))
            {
                var w = m.Value.ToLowerInvariant();
                if (w.Length >= 4 && !Stopwords.Contains(w)) return w;
            }
            return null;
        }
        var heads = entities.Where(e => tagCounts.ContainsKey(e.Id))
            .Select(e => (e.Id, e.Name, Head: HeadWord(e.Name)))
            .Where(x => x.Head != null)
            .ToList();
        var firstHeadIdx = new Dictionary<Guid, int>();
        foreach (var (id, _, head) in heads)
        {
            var rx = new Regex($@"\b{Regex.Escape(head!)}\b");
            for (int i = 0; i < beats.Count; i++)
                if (rx.IsMatch(strippedLower[i])) { firstHeadIdx[id] = i; break; }
        }
        int phrases = 0;
        var ftSeen = new HashSet<(int, Guid)>();
        for (int i = 0; i < beats.Count; i++)
        {
            foreach (Match m in FirstTimeRx.Matches(beats[i].Text))
            {
                phrases++;
                var lo = Math.Max(0, m.Index - 300);
                var hi = Math.Min(beats[i].Text.Length, m.Index + m.Length + 300);
                var window = beats[i].Text[lo..hi];
                var windowLower = Norm(BeatMarkup.StripEntityTags(window));
                var candidates = new HashSet<Guid>();
                foreach (Match g in GuidAttrRx.Matches(window))
                    if (Guid.TryParse(g.Groups[1].Value, out var gg)) candidates.Add(gg);
                foreach (var (id, _, head) in heads)
                    if (Regex.IsMatch(windowLower, $@"\b{Regex.Escape(head!)}\b")) candidates.Add(id);
                // One finding per (beat, phrase), naming every prior-seen entity in the window —
                // not one per entity. A crowded scene fanned out to 12 findings for one clause.
                var priors = new List<string>();
                foreach (var id in candidates)
                {
                    if (!byId.TryGetValue(id, out var ent)) continue;
                    if ((double)tagCounts.GetValueOrDefault(id) / beats.Count >= UbiquitousEntityShare) continue;
                    var first = Math.Min(firstTagIdx.GetValueOrDefault(id, int.MaxValue), firstHeadIdx.GetValueOrDefault(id, int.MaxValue));
                    if (first >= i) continue;
                    if (!ftSeen.Add((i, id))) continue;
                    priors.Add($"{ent.Name} (first at beat #{beats[first].Number}, position {first + 1})");
                }
                if (priors.Count == 0) continue;
                count++;
                // Report line only — NOT a Finding. On the first full BCODA read (2026-09-05) 33 of 34
                // FIRST-TIME flags were false positives (vocabulary entities like "Shell", a "Continu…"
                // faction, month names); filed as Findings they re-appear on every lint run (the lint
                // deletes-by-prefix and re-files, so a dismissal never sticks) and loop back into every
                // later beat's generation guidance as if they were real defects. They are read flags for
                // a human, so they stay in the printed report and the structure count, nothing more.
                lines.Add($"FIRST-TIME beat #{beats[i].Number} ({beats[i].Chapter}): \"{m.Value}\" within reach of {string.Join("; ", priors)} — each already on the page earlier. A first meeting that isn't, or a stale draft. Read the beat.");
            }
        }
        lines.Add($"[structure] first-time: examined {phrases} phrase(s) against {heads.Count} tagged entit(y/ies).");

        // 6d. Cross-batch insertion — Beat.Number is a global creation counter, so a beat whose
        // number sits far outside its chapter's cluster was created in a different batch and
        // placed here later. Catches #4368-in-a-4xx-5xx-chapter; does NOT catch same-batch
        // alternates (Ch12's #5229 sat among its 5xxx siblings) — that is what 6a is for.
        int chaptersChecked = 0;
        foreach (var grp in beats.GroupBy(b => b.ChapterId))
        {
            var nums = grp.Select(b => (double)b.Number).OrderBy(x => x).ToList();
            if (nums.Count < 5) continue;
            chaptersChecked++;
            var median = nums[nums.Count / 2];
            var mad = nums.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList()[nums.Count / 2];
            var thr = Math.Max(150, 3 * mad);
            foreach (var b in grp)
            {
                if (Math.Abs(b.Number - median) <= thr) continue;
                count++;
                // Report line only — NOT a Finding (same reasoning as FIRST-TIME above: 61 of 64 flags on
                // the first BCODA read were legitimate later-batch insertions the story needs; a
                // heuristic this loose must not feed generation guidance).
                lines.Add($"BATCH-OUTLIER beat #{b.Number} ({b.Chapter}): far from the chapter's beat-number cluster (median {median:F0}, threshold ±{thr:F0}) — created in a different generation batch and inserted here. Check that it belongs in the reading order.");
            }
        }
        lines.Add($"[structure] batch-outlier: examined {beats.Count} beat(s) across {chaptersChecked} chapter(s) with ≥5 beats.");

        return count;
    }

    /// <summary>Distinctive 3-4-grams: every token lowercased; n-grams that are all stopwords
    /// or contain an entity-name token are skipped.</summary>
    private Dictionary<string, int> CountNgrams(List<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int n = 3; n <= 4; n++)
        {
            for (int i = 0; i + n <= tokens.Count; i++)
            {
                var slice = tokens.Skip(i).Take(n).ToList();
                // Distinctive = at least two non-stopword tokens of length ≥4.
                if (slice.Count(t => t.Length >= 4 && !Stopwords.Contains(t)) < 2) continue;
                var phrase = string.Join(' ', slice.Select(t => t.ToLowerInvariant()));
                counts[phrase] = counts.GetValueOrDefault(phrase) + 1;
            }
        }
        // Only phrases that actually recur are interesting; drop singletons early.
        return counts.Where(kv => kv.Value >= 2).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static bool IsQuotedOnly(string paragraph)
    {
        // A paragraph is "quoted only" when, after removing quoted spans, almost nothing
        // remains (no attribution/action words around the quotes).
        var outside = Regex.Replace(paragraph, @"[“""][^”""]*[”""]", " ");
        var outsideWords = WordRx.Matches(outside).Count;
        return outsideWords <= 1;
    }
}
