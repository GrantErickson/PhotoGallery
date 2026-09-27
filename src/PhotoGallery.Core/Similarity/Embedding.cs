using System.Numerics;

namespace PhotoGallery.Core.Similarity;

/// <summary>
/// CLIP embeddings as unit vectors stored in 8 bits a dimension (×127): a quarter of the space of floats, and a dot
/// product of two of them (÷127²) is their cosine similarity to about ±0.01.
/// </summary>
public static class Embedding
{
    /// <summary>CLIP ViT-L/14's embedding size.</summary>
    public const int Dimensions = 768;

    public static sbyte[] Quantize(ReadOnlySpan<float> vector)
    {
        double norm = 0;
        foreach (var v in vector) norm += v * v;
        norm = Math.Sqrt(norm);
        var result = new sbyte[vector.Length];
        if (norm <= 0) return result;
        for (var i = 0; i < vector.Length; i++) result[i] = (sbyte)Math.Clamp(Math.Round(vector[i] / norm * 127), -127, 127);
        return result;
    }

    /// <summary>Cosine similarity of two quantized vectors.</summary>
    public static double Similarity(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => Dot(a, b) / (127.0 * 127.0);

    internal static int Dot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        var sum = 0;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<sbyte>.Count)
        {
            var acc = Vector<int>.Zero;
            for (; i <= a.Length - Vector<sbyte>.Count; i += Vector<sbyte>.Count)
            {
                Vector.Widen(new Vector<sbyte>(a[i..]), out var a0, out var a1);
                Vector.Widen(new Vector<sbyte>(b[i..]), out var b0, out var b1);
                Vector.Widen(a0 * b0, out var p0, out var p1);
                Vector.Widen(a1 * b1, out var p2, out var p3);
                acc += p0 + p1 + p2 + p3;
            }
            sum = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>
/// Every photo's embedding in memory (about 170 MB for 225,000 photos), searched by brute force on all cores: a query
/// against the whole library takes tens of milliseconds. New embeddings can be added while it's searched.
/// </summary>
public sealed class SimilarityIndex
{
    private readonly Lock _gate = new();
    private long[] _ids;
    private sbyte[] _vectors;
    private int _count;
    private readonly Dictionary<long, int> _rows;

    public SimilarityIndex(long[] ids, sbyte[] vectors)
    {
        if (vectors.Length != ids.Length * Embedding.Dimensions) throw new ArgumentException("One vector per id is needed.", nameof(vectors));
        (_ids, _vectors, _count) = (ids, vectors, ids.Length);
        _rows = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++) _rows[ids[i]] = i;
    }

    public static SimilarityIndex Empty => new([], []);

    public int Count => _count;

    public bool Contains(long id)
    {
        lock (_gate) return _rows.ContainsKey(id);
    }

    public ReadOnlySpan<sbyte> VectorOf(long id)
    {
        lock (_gate)
            return _rows.TryGetValue(id, out var row) ? _vectors.AsSpan(row * Embedding.Dimensions, Embedding.Dimensions) : default;
    }

    /// <summary>Adds an item's embedding, or replaces it.</summary>
    public void Add(long id, sbyte[] vector)
    {
        if (vector.Length != Embedding.Dimensions) throw new ArgumentException("Wrong size.", nameof(vector));
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row))
            {
                if (_count == _ids.Length)
                {
                    // Searches in progress keep reading the old arrays, which stay valid for the rows they know.
                    var capacity = Math.Max(1024, _ids.Length * 2);
                    Array.Resize(ref _ids, capacity);
                    Array.Resize(ref _vectors, capacity * Embedding.Dimensions);
                }
                row = _count;
                _ids[row] = id;
                _rows[id] = row;
                vector.CopyTo(_vectors, row * Embedding.Dimensions);
                _count++;
                return;
            }
            vector.CopyTo(_vectors, row * Embedding.Dimensions);
        }
    }

    /// <summary>Drops an item (its row is filled with the last one).</summary>
    public void Remove(long id)
    {
        lock (_gate)
        {
            if (!_rows.Remove(id, out var row)) return;
            var last = _count - 1;
            if (row != last)
            {
                _ids[row] = _ids[last];
                Array.Copy(_vectors, last * Embedding.Dimensions, _vectors, row * Embedding.Dimensions, Embedding.Dimensions);
                _rows[_ids[row]] = row;
            }
            _count--;
        }
    }

    /// <summary>The most similar items, best first, at least <paramref name="minSimilarity"/>, leaving out <paramref name="except"/>.</summary>
    public List<(long Id, double Similarity)> Search(ReadOnlySpan<sbyte> query, int count, double minSimilarity = -1, long? except = null)
    {
        long[] ids;
        sbyte[] vectors;
        int rows;
        lock (_gate) (ids, vectors, rows) = (_ids, _vectors, _count);
        if (query.Length != Embedding.Dimensions || rows == 0 || count <= 0) return [];
        var q = query.ToArray();
        var scores = new int[rows];
        Parallel.For(0, (rows + 4095) / 4096, chunk =>
        {
            var end = Math.Min(rows, (chunk + 1) * 4096);
            for (var row = chunk * 4096; row < end; row++)
                scores[row] = Embedding.Dot(q, vectors.AsSpan(row * Embedding.Dimensions, Embedding.Dimensions));
        });
        var threshold = (int)Math.Ceiling(minSimilarity * 127 * 127);
        var best = new PriorityQueue<int, int>(count + 1); // min-heap of the top rows
        for (var row = 0; row < rows; row++)
        {
            if (scores[row] < threshold || ids[row] == except) continue;
            if (best.Count < count) best.Enqueue(row, scores[row]);
            else if (best.TryPeek(out _, out var worst) && scores[row] > worst) best.EnqueueDequeue(row, scores[row]);
        }
        var result = new List<(long, double)>(best.Count);
        while (best.TryDequeue(out var row, out var score)) result.Add((ids[row], score / (127.0 * 127.0)));
        result.Reverse();
        return result;
    }
}
