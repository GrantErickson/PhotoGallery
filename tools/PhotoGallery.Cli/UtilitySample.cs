using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoGallery.Core;
using PhotoGallery.Core.Data;
using PhotoGallery.Core.Imaging;
using PhotoGallery.Core.Similarity;

namespace PhotoGallery.Cli;

/// <summary>
/// For tuning utility shots (<see cref="UtilityShots"/>): scores the whole library without saving anything, prints how
/// the scores spread, and writes contact sheets (HTML pages of thumbnails) of photos in each band of scores, to see
/// where records of things end and memories begin.
///   dotnet run --project tools/PhotoGallery.Cli -- utility-sample &lt;embedder exe&gt; &lt;output folder&gt;
/// </summary>
internal static class UtilitySample
{
    public static async Task RunAsync(AppPaths paths, GalleryDatabase database, MediaRepository media, ThumbnailCache thumbs, string exe, string output)
    {
        var models = Path.Combine(paths.Models, "clip-vit-large-patch14");
        var tokenizer = ClipTokenizer.Load(Path.Combine(models, "vocab.json"), Path.Combine(models, "merges.txt"));
        using var embedder = new EmbedderClient(Path.Combine(models, "vision_model_fp16.onnx"), Path.Combine(models, "text_model_fp16.onnx"), exe);
        var clock = Stopwatch.StartNew();
        async Task<List<sbyte[]>> EmbedAsync(IEnumerable<string> texts)
        {
            var vectors = new List<sbyte[]>();
            foreach (var text in texts) vectors.Add(Embedding.Quantize(await embedder.EmbedTextAsync(tokenizer.Encode(text))));
            return vectors;
        }
        var records = await EmbedAsync(UtilityShots.Records);
        var memories = await EmbedAsync(UtilityShots.Memories);
        Console.WriteLine($"Descriptions embedded in {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        var index = new EmbeddingRepository(database).LoadIndex();
        var hints = media.GetUtilityBacklog(scoredToo: true).ToDictionary(h => h.Id, h => (h.Words, h.Faces));
        var screenshots = media.Query(new MediaFilter { ScreenshotsOnly = true, IncludeScreenshots = true }).Select(s => s.Id).ToHashSet();
        Console.WriteLine($"Loaded {index.Count:N0} embeddings and hints in {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        var scores = new ConcurrentBag<(long Id, double Score, int Closest, int Words, int Faces)>();
        index.VisitAll((id, vector) =>
        {
            if (screenshots.Contains(id) || !hints.ContainsKey(id)) return; // screenshots and videos
            var (words, faces) = hints.GetValueOrDefault(id);
            scores.Add((id, UtilityShots.Score(vector, records, memories, words, faces), UtilityShots.Closest(vector, records).Index, words, faces));
        });
        var all = scores.OrderByDescending(s => s.Score).ToList();
        Console.WriteLine($"Scored {all.Count:N0} (not screenshots) in {clock.ElapsedMilliseconds} ms");

        foreach (var p in new[] { 0.5, 1, 2, 5, 10, 20, 50 })
            Console.WriteLine($"  top {p,4}%: score >= {all[(int)(all.Count * p / 100)].Score:F4}");
        foreach (var t in new[] { -0.03, -0.02, -0.01, 0, 0.01, 0.02, 0.03, 0.05 })
            Console.WriteLine($"  at {t,6:F2}: {all.Count(s => s.Score >= t),7:N0} utility shots ({100.0 * all.Count(s => s.Score >= t) / all.Count:F1}%)");
        foreach (var group in all.Where(s => s.Score >= 0).GroupBy(s => s.Closest).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),6:N0} closest to \"{UtilityShots.Records[group.Key]}\"");

        Directory.CreateDirectory(output);
        var random = new Random(7);
        (string Name, double Low, double High)[] bands =
        [
            ("a-above-0.06", 0.06, 9), ("b-0.03-0.06", 0.03, 0.06), ("c-0.01-0.03", 0.01, 0.03), ("d-0-0.01", 0, 0.01),
            ("e-minus0.01-0", -0.01, 0), ("f-minus0.02-minus0.01", -0.02, -0.01), ("g-below-minus0.02", -9, -0.02),
        ];
        foreach (var (name, low, high) in bands)
        {
            var inBand = all.Where(s => s.Score >= low && s.Score < high).OrderBy(_ => random.Next()).Take(48).OrderByDescending(s => s.Score).ToList();
            var html = new StringBuilder($"<!doctype html><meta charset=utf-8><body style='background:#111;color:#ddd;font:11px sans-serif;margin:8px'>" +
                                         $"<h3 style='margin:4px'>{name}: {all.Count(s => s.Score >= low && s.Score < high):N0} photos</h3><div style='display:grid;grid-template-columns:repeat(8,1fr);gap:4px'>");
            foreach (var s in inBand)
            {
                var thumb = thumbs.GetPath(s.Id);
                html.Append(CultureInfo.InvariantCulture,
                    $"<div><img src='file:///{thumb.Replace('\\', '/')}' style='width:100%;height:120px;object-fit:cover;display:block'>" +
                    $"<div>{s.Score:F3} · {UtilityShots.Records[s.Closest].Replace("a photo of ", "")}{(s.Words > 0 ? $" · {s.Words}w" : "")}{(s.Faces > 0 ? $" · {s.Faces}f" : "")}</div></div>");
            }
            html.Append("</div></body>");
            File.WriteAllText(Path.Combine(output, name + ".html"), html.ToString());
        }
        Console.WriteLine($"Contact sheets in {output}");
    }
}
