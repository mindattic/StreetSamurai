using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;
using Prose.Core.Data.Entities;

namespace Prose.Cli;

/// <summary>
/// <c>prose --character-depth-audit --universe &lt;slug&gt; [--broad] [--json]</c>
///
/// Read-only survey. Flags characters whose relational depth bridges (PsychologyTraits,
/// StatScalars, ArchetypeScores, StoryHooks, SpeechPhrases) are ALL empty — the signature seen
/// on Nadia Migizi after <c>prose --rebuild-readmodel</c> replaced her CharacterReadModels cache
/// (which had rich psychology/stats/story-hook content) with a projection built from the
/// relational bridge tables, which turned out to hold none of it for her. Kyle Ellen Corbin, by
/// contrast, came through the same rebuild fully intact — the bridges genuinely had his data.
///
/// The plain sparse-and-mentioned-in-prose test (the original 2026-09-05 version of this tool,
/// still available via <c>--broad</c>) turned out to be mostly false positives on spot-check:
/// auto-generated minor characters (War Dog, Priya Johansdóttir, "Sable Industries") that were
/// ALWAYS this sparse, never real casualties. The default mode adds a richness gate on fields
/// that live as plain scalar columns on the Character row itself (Heritage/HeightCm,
/// PsychologySecret, NarrativeFunction) — these survived the rebuild untouched, so a real
/// casualty still has non-trivial values there despite the empty bridges, while an always-sparse
/// stub has both empty. Requires at least 2 of those 3 independent signals to call a character
/// "confirmed" rather than "uncertain" (kept visible, never silently dropped, in case the signal
/// needs tuning).
///
/// Does not write anything.
/// </summary>
public static class CharacterDepthAuditCli
{
    private static readonly string[] PlaceholderHeritage = ["unknown", "n/a", "none", "tbd", ""];

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var json = args.Contains("--json");
        var broad = args.Contains("--broad");
        var dbFactory = services.GetRequiredService<IDbContextFactory<ProseDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var charIds = await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync();
        if (charIds.Count == 0)
        {
            Console.WriteLine("[character-depth-audit] No characters found in scope.");
            return 0;
        }

        var idSet = charIds.ToHashSet();

        var psychIds = await db.Set<CharacterPsychologyTrait>().AsNoTracking()
            .Select(x => x.CharacterId).Distinct().ToListAsync();
        var statIds = await db.Set<CharacterStatScalar>().AsNoTracking()
            .Select(x => x.CharacterId).Distinct().ToListAsync();
        var archIds = await db.Set<CharacterArchetypeScore>().AsNoTracking()
            .Select(x => x.CharacterId).Distinct().ToListAsync();
        var hookIds = await db.Set<CharacterStoryHook>().AsNoTracking()
            .Select(x => x.CharacterId).Distinct().ToListAsync();
        var speechIds = await db.Set<CharacterSpeechPhrase>().AsNoTracking()
            .Select(x => x.CharacterId).Distinct().ToListAsync();

        var hasPsych = psychIds.ToHashSet();
        var hasStat = statIds.ToHashSet();
        var hasArch = archIds.ToHashSet();
        var hasHook = hookIds.ToHashSet();
        var hasSpeech = speechIds.ToHashSet();

        var sparse = charIds
            .Where(id => !hasPsych.Contains(id) && !hasStat.Contains(id) && !hasArch.Contains(id)
                      && !hasHook.Contains(id) && !hasSpeech.Contains(id))
            .ToHashSet();

        var namesById = await db.Entities.AsNoTracking()
            .Where(e => idSet.Contains(e.Id))
            .Select(e => new { e.Id, e.Name })
            .ToDictionaryAsync(e => e.Id, e => e.Name);

        var mentionCounts = await db.Set<BeatEntityMention>().AsNoTracking()
            .Where(m => sparse.Contains(m.EntityId))
            .GroupBy(m => m.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.EntityId, g => g.Count);

        var scalarsById = await db.Characters.AsNoTracking()
            .Where(c => sparse.Contains(c.Id))
            .Select(c => new { c.Id, c.Heritage, c.HeightCm, c.PsychologySecret, c.NarrativeFunction })
            .ToDictionaryAsync(c => c.Id);

        var rows = sparse
            .Select(id =>
            {
                var scalars = scalarsById[id];
                var heritageReal = !PlaceholderHeritage.Contains(scalars.Heritage.Trim().ToLowerInvariant());
                var physicalSignal = scalars.HeightCm != 0 && heritageReal;
                var secretSignal = !string.IsNullOrWhiteSpace(scalars.PsychologySecret);
                var narrativeSignal = scalars.NarrativeFunction.Trim().Length > 20;
                var signalCount = (physicalSignal ? 1 : 0) + (secretSignal ? 1 : 0) + (narrativeSignal ? 1 : 0);
                return new
                {
                    Id = id,
                    Name = namesById.TryGetValue(id, out var n) ? n : "(unknown)",
                    Mentions = mentionCounts.TryGetValue(id, out var c) ? c : 0,
                    Confirmed = signalCount >= 2,
                };
            })
            .OrderByDescending(r => r.Mentions)
            .ToList();

        var used = rows.Where(r => r.Mentions > 0).ToList();
        var unused = rows.Where(r => r.Mentions == 0).ToList();
        var confirmed = used.Where(r => r.Confirmed).ToList();
        var uncertain = used.Where(r => !r.Confirmed).ToList();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                total_characters = charIds.Count,
                sparse_total = sparse.Count,
                sparse_used_in_prose = used.Count,
                sparse_never_mentioned = unused.Count,
                confirmed_casualties = confirmed.Select(r => new { id = r.Id, name = r.Name, mentions = r.Mentions }),
                uncertain = uncertain.Select(r => new { id = r.Id, name = r.Name, mentions = r.Mentions }),
                broad_used_characters = broad ? used.Select(r => new { id = r.Id, name = r.Name, mentions = r.Mentions }) : null,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"[character-depth-audit] {charIds.Count} characters in scope.");
        Console.WriteLine($"[character-depth-audit] {sparse.Count} have ZERO rows across PsychologyTraits/StatScalars/ArchetypeScores/StoryHooks/SpeechPhrases.");
        Console.WriteLine($"[character-depth-audit]   -> {used.Count} of those are actually mentioned in prose (BeatEntityMentions > 0).");
        Console.WriteLine($"[character-depth-audit]      -> {confirmed.Count} CONFIRMED real casualties (rich survivor scalars despite empty bridges).");
        Console.WriteLine($"[character-depth-audit]      -> {uncertain.Count} uncertain (sparse+mentioned, but survivor scalars are ALSO thin — likely always sparse).");
        Console.WriteLine($"[character-depth-audit]   -> {unused.Count} are never mentioned in any beat — likely inert stub rows.");

        if (confirmed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("CONFIRMED casualties, by mention count:");
            foreach (var r in confirmed)
                Console.WriteLine($"  {r.Mentions,5}  {r.Name}  ({r.Id})");
        }
        if (uncertain.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Uncertain (kept visible, not dropped — spot-check before trusting either way):");
            foreach (var r in uncertain)
                Console.WriteLine($"  {r.Mentions,5}  {r.Name}  ({r.Id})");
        }
        if (broad && used.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("--broad: full sparse-and-mentioned list (the old, mostly-false-positive signal):");
            foreach (var r in used)
                Console.WriteLine($"  {r.Mentions,5}  {r.Name}  ({r.Id})");
        }

        return 0;
    }
}
