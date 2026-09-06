using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;

namespace Prose.Cli;

/// <summary>
/// <c>prose --rebuild-readmodel [--archived] [--force]</c> — rebuild the materialized
/// character read-model projection (CharacterReadModels) from the relational
/// source of truth. This is the one-time slow path (the 25-Include fan-out over
/// every character) that lets every subsequent full read be a single column
/// read. Backfills missing / stale-version rows and prunes orphans.
///
/// Run after a bulk import, the JSON→relational migration, or a
/// <see cref="CharacterMapper.ReadModelVersion"/> bump. The steady-state read
/// path self-heals, so day-to-day you never need this.
///
/// SAFETY (added after the 2026-09-05 incident — a --rebuild-readmodel run to fix an unrelated
/// stale-alias display bug silently destroyed real psychology/stats/story-hook/archetype
/// content for characters whose relational bridge tables never actually held it): any character
/// where the relational read would be poorer than the currently-cached row is SKIPPED, not
/// overwritten (see <see cref="CharacterMapper.IsDepthRegression"/>), and named below so a human
/// can decide what to do about it. Pass --force only after reviewing that list and confirming
/// the loss is acceptable for those specific characters.
/// </summary>
public static class RebuildReadModelCli
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        bool includeArchived = args.Contains("--archived");
        bool force = args.Contains("--force");
        var dbFactory = services.GetRequiredService<IDbContextFactory<ProseDbContext>>();

        Console.WriteLine($"[rebuild-readmodel] Rebuilding character read-models (v{CharacterMapper.ReadModelVersion}{(includeArchived ? ", incl. archived" : "")}{(force ? ", FORCE" : "")})…");
        var sw = Stopwatch.StartNew();

        await using var db = await dbFactory.CreateDbContextAsync();
        CharacterMapper.RebuildResult result;
        try
        {
            result = await CharacterMapper.RebuildAllReadModelsAsync(db, includeArchived, force);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rebuild-readmodel] FAILED: {ex.Message}");
            Console.Error.WriteLine("[rebuild-readmodel] Did you run the migration? prose --migrate-sql (create_character_readmodel_20260606.sql)");
            return 1;
        }

        sw.Stop();
        Console.WriteLine($"[rebuild-readmodel] Wrote {result.Written} read-models in {sw.Elapsed.TotalSeconds:0.#}s. Full reads now serve from the projection.");
        if (result.Skipped > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[rebuild-readmodel] SKIPPED {result.Skipped} character(s) — the relational read is missing real depth content");
            Console.WriteLine("[rebuild-readmodel] (psychology/stats/archetypes/story-hooks/speech-patterns) their cached read-model already has.");
            Console.WriteLine("[rebuild-readmodel] Their cache was left untouched. Review each one, then re-run with --force only if the loss is intentional:");
            foreach (var (id, name) in result.SkippedRows)
                Console.WriteLine($"    {name}  ({id})");
        }
        return 0;
    }
}
