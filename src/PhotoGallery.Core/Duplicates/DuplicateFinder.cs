using Dapper;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Media;

namespace PhotoGallery.Core.Duplicates;

public enum DuplicateKind
{
    /// <summary>Byte-identical files (same size and content hash).</summary>
    Exact,
    /// <summary>The same photo taken at the same moment, visually near-identical (re-exports, resized copies, bursts).</summary>
    Similar,
}

public sealed class DuplicateMember
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long DateTaken { get; init; }
    public MediaKind Kind { get; init; }
    /// <summary>The copy the finder suggests keeping.</summary>
    public bool IsSuggestedKeep { get; set; }

    public DateTime TakenLocal => DateTime.UnixEpoch.AddSeconds(DateTaken);
}

public sealed record DuplicateGroup(DuplicateKind Kind, IReadOnlyList<DuplicateMember> Members)
{
    /// <summary>Bytes freed by keeping only the suggested copy.</summary>
    public long ReclaimableBytes => Members.Where(m => !m.IsSuggestedKeep).Sum(m => m.FileSize);
}

public sealed record DuplicateScanProgress(string Stage, int Done, int Total);

/// <summary>
/// Finds exact duplicates (equal size → quick content hash) and similar photos (same capture second →
/// perceptual hash of the thumbnail within <see cref="SimilarThreshold"/> bits). Hashes are cached in the
/// Media table and cleared when a file changes, so repeat scans only hash new files.
/// </summary>
public sealed class DuplicateFinder(GalleryDatabase database, Func<long, string, CancellationToken, Task<string?>> thumbnailFor)
{
    public const int SimilarThreshold = 6;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IProgress<DuplicateScanProgress>? progress = null, CancellationToken ct = default)
    {
        var exact = await FindExactAsync(progress, ct);
        var similar = await FindSimilarAsync(exact, progress, ct);
        return exact.Concat(similar)
            .OrderByDescending(g => g.Members.Max(m => m.DateTaken))
            .ToList();
    }

    private sealed class Candidate
    {
        public long Id { get; init; }
        public string Path { get; init; } = "";
        public long FileSize { get; init; }
        public string? QuickHash { get; init; }
        public long? PerceptualHash { get; init; }
        public long DateTaken { get; init; }
    }

    private async Task<List<DuplicateGroup>> FindExactAsync(IProgress<DuplicateScanProgress>? progress, CancellationToken ct)
    {
        List<Candidate> candidates;
        using (var db = database.Open())
            candidates = db.Query<Candidate>(
                """
                SELECT Id, Path, FileSize, QuickHash, PerceptualHash, DateTaken FROM Media
                WHERE IsHidden = 0 AND FileSize IN (SELECT FileSize FROM Media WHERE IsHidden = 0 GROUP BY FileSize HAVING count(*) > 1)
                """).AsList();

        var missing = candidates.Where(c => c.QuickHash is null).ToList();
        var hashes = await ComputeAsync(missing, "Comparing file contents", progress, ct, c =>
            Task.FromResult<object?>(File.Exists(c.Path) ? ContentHashes.QuickHash(c.Path) : null));
        Store("QuickHash", hashes);

        var groups = candidates
            .Select(c => (c.Id, c.FileSize, Hash: c.QuickHash ?? hashes.GetValueOrDefault(c.Id) as string))
            .Where(c => c.Hash is not null)
            .GroupBy(c => (c.FileSize, c.Hash))
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(c => c.Id).ToList())
            .ToList();
        return BuildGroups(DuplicateKind.Exact, groups);
    }

    private async Task<List<DuplicateGroup>> FindSimilarAsync(List<DuplicateGroup> exact, IProgress<DuplicateScanProgress>? progress, CancellationToken ct)
    {
        List<Candidate> candidates;
        using (var db = database.Open())
            candidates = db.Query<Candidate>(
                """
                SELECT Id, Path, FileSize, QuickHash, PerceptualHash, DateTaken FROM Media
                WHERE IsHidden = 0 AND Kind IN (1, 3) AND DateSource IN (2, 3)
                  AND DateTaken IN (SELECT DateTaken FROM Media WHERE IsHidden = 0 AND Kind IN (1, 3) AND DateSource IN (2, 3)
                                    GROUP BY DateTaken HAVING count(*) > 1)
                """).AsList();

        var missing = candidates.Where(c => c.PerceptualHash is null).ToList();
        var hashes = await ComputeAsync(missing, "Comparing similar photos", progress, ct, async c =>
            await thumbnailFor(c.Id, c.Path, ct) is { } thumb ? await ContentHashes.PerceptualHashAsync(thumb) : null);
        Store("PerceptualHash", hashes);

        // Files already reported as exact copies of each other aren't "similar" to each other.
        var exactGroupOf = new Dictionary<long, int>();
        for (var i = 0; i < exact.Count; i++)
            foreach (var m in exact[i].Members) exactGroupOf[m.Id] = i;

        var clusters = new List<List<long>>();
        foreach (var moment in candidates.GroupBy(c => c.DateTaken))
        {
            var items = moment
                .Select(c => (c.Id, Hash: c.PerceptualHash ?? hashes.GetValueOrDefault(c.Id) as long?))
                .Where(x => x.Hash is not null)
                .Select(x => (x.Id, Hash: x.Hash!.Value))
                .ToList();
            foreach (var cluster in Cluster(items, SimilarThreshold))
            {
                var distinctCopies = cluster.Select(id => exactGroupOf.TryGetValue(id, out var g) ? -1 - g : id).Distinct().Count();
                if (distinctCopies > 1) clusters.Add(cluster);
            }
        }
        return BuildGroups(DuplicateKind.Similar, clusters);
    }

    /// <summary>Single-linkage clusters (union-find) of items whose hashes are within the threshold.</summary>
    internal static List<List<long>> Cluster(IReadOnlyList<(long Id, long Hash)> items, int threshold)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (ContentHashes.Distance(items[i].Hash, items[j].Hash) <= threshold)
                    parent[Find(i)] = Find(j);
        return Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(i => items[i].Id).ToList())
            .ToList();
    }

    private static async Task<Dictionary<long, object?>> ComputeAsync(List<Candidate> items, string stage, IProgress<DuplicateScanProgress>? progress,
        CancellationToken ct, Func<Candidate, Task<object?>> compute)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<long, object?>();
        var done = 0;
        progress?.Report(new DuplicateScanProgress(stage, 0, items.Count));
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (c, _) =>
        {
            try
            {
                results[c.Id] = await compute(c);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                results[c.Id] = null; // unreadable: left out of the comparison
            }
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0 || n == items.Count) progress?.Report(new DuplicateScanProgress(stage, n, items.Count));
        });
        return new Dictionary<long, object?>(results);
    }

    private void Store(string column, Dictionary<long, object?> values)
    {
        if (values.Count == 0) return;
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        foreach (var (id, value) in values)
            if (value is not null)
                db.Execute($"UPDATE Media SET {column} = @value WHERE Id = @id", new { id, value }, tx);
        tx.Commit();
    }

    private List<DuplicateGroup> BuildGroups(DuplicateKind kind, List<List<long>> idGroups)
    {
        if (idGroups.Count == 0) return [];
        using var db = database.Open();
        var ids = idGroups.SelectMany(g => g).ToList();
        var rows = new Dictionary<long, DuplicateMember>();
        foreach (var chunk in ids.Chunk(900))
            foreach (var m in db.Query<DuplicateMember>(
                         "SELECT Id, Path, FileName, FileSize, Width, Height, DateTaken, Kind FROM Media WHERE Id IN @chunk", new { chunk }))
                rows[m.Id] = m;

        var groups = new List<DuplicateGroup>();
        foreach (var g in idGroups)
        {
            var members = g.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
            if (members.Count < 2) continue;
            SuggestKeep(members);
            groups.Add(new DuplicateGroup(kind, members.OrderByDescending(m => m.IsSuggestedKeep).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return groups;
    }

    /// <summary>Keep the highest resolution, then a name without a "(1)" copy suffix, then the larger file, then the shorter path.</summary>
    internal static void SuggestKeep(IReadOnlyList<DuplicateMember> members)
    {
        var best = members
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenBy(m => IsCopyName(m.FileName) ? 1 : 0)
            .ThenByDescending(m => m.FileSize)
            .ThenBy(m => m.Path.Length)
            .First();
        foreach (var m in members) m.IsSuggestedKeep = ReferenceEquals(m, best);
    }

    internal static bool IsCopyName(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"(\(\d+\)| - Copy| copy)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
