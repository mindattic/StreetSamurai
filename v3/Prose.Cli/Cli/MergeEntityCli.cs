using Microsoft.Extensions.DependencyInjection;
using Prose.Core.Services;

namespace Prose.Cli;

/// <summary>
/// prose --merge-entity --winner &lt;guid&gt; --loser &lt;guid&gt; [--universe &lt;slug&gt;]
///
/// IMPORTANT: pass <c>--universe &lt;slug&gt;</c> whenever winner/loser live outside the default
/// universe (GLMZ) — <see cref="UniverseScope"/>'s global query filter otherwise scopes the
/// lookup to the default universe and the merge fails with "entity not found" even though both
/// rows genuinely exist (found live 2026-08-18 merging SCRY duplicates).
///
/// The execution half of the report-only duplicate-scan tools (<see cref="DuplicateEntityScanCli"/>,
/// <see cref="DuplicateEntityScanBroadCli"/>) and Phase 0 book-entity reconciliation
/// (<see cref="ReconcileBookEntitiesCli"/>) — none of which ever merge anything themselves by
/// design. This is the one place a human, having confirmed from real book/prose knowledge that
/// two rows are the same identity, actually executes
/// <see cref="DuplicateEntityScanService.MergeAsync"/>: every real FK reference to the loser is
/// relinked to the winner, then the loser's own Entities row is hard-deleted. Recoverable via
/// Entities_History (<c>prose --restore-entity</c>) or the AutoCorrect undo ledger.
///
/// Deliberately takes only two GUIDs — no name/slug lookup, no fuzzy matching, no LLM call.
/// The identity judgment must already be made by the caller before this runs.
/// </summary>
public static class MergeEntityCli
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var winnerArg = Flag(args, "--winner");
        var loserArg = Flag(args, "--loser");

        if (!Guid.TryParse(winnerArg, out var winnerId) || !Guid.TryParse(loserArg, out var loserId))
        {
            Console.Error.WriteLine("Usage: prose --merge-entity --winner <guid> --loser <guid> [--universe <slug>]");
            return 2;
        }

        var svc = services.GetRequiredService<DuplicateEntityScanService>();
        try
        {
            var result = await svc.MergeAsync(winnerId, loserId);
            Console.WriteLine($"[merge-entity] Merged {loserId} into {winnerId}: " +
                $"{result.RowsRelinked} row(s) relinked, {result.RowsDeletedForCollision} row(s) deleted for 1:1 collision, " +
                $"{result.BeatsRetagged} beat text(s) + {result.OutlinesRetagged} outline(s) retagged guid loser→winner.");
            Console.WriteLine($"[merge-entity] Loser's Entities row deleted. Recoverable via " +
                $"`prose --restore-entity --id {loserId} --as-of <datetime-before-now>` or the AutoCorrect undo ledger.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[merge-entity] {ex.Message}");
            return 1;
        }
    }

    private static string? Flag(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
