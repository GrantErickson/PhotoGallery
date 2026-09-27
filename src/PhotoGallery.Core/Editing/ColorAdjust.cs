namespace PhotoGallery.Core.Editing;

/// <summary>
/// The colour part of an edit as one affine map on RGB (0–1): white balance (warmth, tint) → saturation →
/// contrast and brightness. Exposure is applied before it, by the renderer.
/// </summary>
public static class ColorAdjust
{
    /// <summary>Rec. 709 luma weights.</summary>
    public const double Lr = 0.2126, Lg = 0.7152, Lb = 0.0722;

    /// <summary>Channel gain at full warmth or tint, in log space (±1 → about ×1.35 / ×0.74).</summary>
    private const double K = 0.3;

    /// <summary>
    /// Per-channel gains for warmth (+ amber, − blue) and tint (+ magenta, − green). Their log-mean is 0, so white
    /// balance doesn't change the overall brightness.
    /// </summary>
    public static (double R, double G, double B) Gains(double temperature, double tint)
    {
        var t = Math.Clamp(temperature, -1, 1) * K;
        var g = Math.Clamp(tint, -1, 1) * K;
        return (Math.Exp(t + g / 2), Math.Exp(-g), Math.Exp(-t + g / 2));
    }

    /// <summary>The warmth and tint (unclamped) whose gains turn a colour with these channel means grey.</summary>
    public static (double Temperature, double Tint) Neutralise(double r, double g, double b)
    {
        // Wanted log gains are −log of each mean; with the gains above, lr − lb = 2t and (lr + lb) / 2 − lg = 1.5g.
        double lr = -Math.Log(r), lg = -Math.Log(g), lb = -Math.Log(b);
        return ((lr - lb) / (2 * K), ((lr + lb) / 2 - lg) / (1.5 * K));
    }

    /// <summary>The map as out = M · rgb + offset: M[output, input], the same offset for each channel.</summary>
    public static (double[,] M, double Offset) Matrix(EditOperations ops)
    {
        var (gr, gg, gb) = Gains(ops.Temperature, ops.Tint);
        double[] gains = [gr, gg, gb], luma = [Lr, Lg, Lb];
        var s = 1 + Math.Clamp(ops.Saturation, -1, 1);          // 0 = grey, 1 = unchanged, 2 = double
        var c = 1 + Math.Clamp(ops.Contrast, -1, 1) * 0.8;       // around mid-grey
        var m = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                m[row, col] = c * (luma[col] * (1 - s) + (row == col ? s : 0)) * gains[col];
        return (m, 0.5 - 0.5 * c + Math.Clamp(ops.Brightness, -1, 1) * 0.4);
    }

    /// <summary>Applies <see cref="Matrix"/> to one colour, clamped to 0–1 like the renderer.</summary>
    public static (double R, double G, double B) Apply((double[,] M, double Offset) map, double r, double g, double b)
    {
        var (m, o) = map;
        return (Math.Clamp(m[0, 0] * r + m[0, 1] * g + m[0, 2] * b + o, 0, 1),
                Math.Clamp(m[1, 0] * r + m[1, 1] * g + m[1, 2] * b + o, 0, 1),
                Math.Clamp(m[2, 0] * r + m[2, 1] * g + m[2, 2] * b + o, 0, 1));
    }
}
