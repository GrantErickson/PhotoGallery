// CLIP image and text embeddings for Photo Gallery (the .csproj says why this is its own process).
//
//   PhotoGallery.Embedder <vision_model.onnx> <text_model.onnx>
//
// JSON, one message a line. It first writes {"ready":true,"device":"DirectML"} (or "CPU"), then answers each request
// from stdin, in order, until stdin closes:
//   {"images":["C:\\...\\1.jpg", ...]}  → {"vectors":["<base64 float32 × 768>", null, ...]}   (null: couldn't read it)
//   {"tokens":[49406, 320, ...]}        → {"vector":"<base64 float32 × 768>"}
// Failures are answered with {"error":"..."}. Nothing else is written to stdout.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;
using Windows.Storage;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: PhotoGallery.Embedder <vision_model.onnx> <text_model.onnx>");
    return 2;
}
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; // background work; the app comes first

var (vision, device) = Open(args[0]);
InferenceSession? text = null; // only searches need it
var output = Console.Out;
output.WriteLine(JsonSerializer.Serialize(new { ready = true, device }));
output.Flush();

while (Console.In.ReadLine() is { } line)
{
    object reply;
    try
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if (request["images"] is JsonArray images)
        {
            var paths = images.Select(p => (string)p!).ToList();
            reply = new { vectors = await EmbedImagesAsync(vision, paths) };
        }
        else if (request["tokens"] is JsonArray tokens)
        {
            text ??= Open(args[1]).Session;
            reply = new { vector = EmbedText(text, tokens.Select(t => (long)t!).ToArray()) };
        }
        else reply = new { error = "Unknown request." };
    }
    catch (Exception ex)
    {
        reply = new { error = $"{ex.GetType().Name}: {ex.Message}" };
    }
    output.WriteLine(JsonSerializer.Serialize(reply));
    output.Flush();
}
return 0;

// The graphics card through DirectML when there is one that works, otherwise the CPU.
static (InferenceSession Session, string Device) Open(string model)
{
    try
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false, // required by DirectML
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        options.AppendExecutionProvider_DML(0);
        return (new InferenceSession(model, options), "DirectML");
    }
    catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
    {
        Console.Error.WriteLine($"DirectML unavailable, using the CPU: {ex.Message}");
        return (new InferenceSession(model, new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR }), "CPU");
    }
}

static async Task<string?[]> EmbedImagesAsync(InferenceSession session, List<string> paths)
{
    const int Size = 224, Plane = Size * Size;
    var pixels = await Task.WhenAll(paths.Select(async p =>
    {
        try
        {
            return await PixelsAsync(p);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Couldn't read {p}: {ex.Message}");
            return null;
        }
    }));
    var readable = Enumerable.Range(0, paths.Count).Where(i => pixels[i] is not null).ToList();
    var result = new string?[paths.Count];
    if (readable.Count == 0) return result;

    var batch = new float[readable.Count * 3 * Plane];
    for (var k = 0; k < readable.Count; k++) pixels[readable[k]]!.CopyTo(batch, k * 3 * Plane);
    var input = session.InputMetadata.First();
    var embeddings = Run(session, [Input(input.Key, input.Value, batch, [readable.Count, 3, Size, Size])], "image_embeds");
    var dims = embeddings.Length / readable.Count;
    for (var k = 0; k < readable.Count; k++) result[readable[k]] = Base64(embeddings.AsSpan(k * dims, dims));
    return result;
}

static string EmbedText(InferenceSession session, long[] tokens)
{
    var inputs = new List<NamedOnnxValue>();
    foreach (var (name, _) in session.InputMetadata)
    {
        // Only the text before the first end marker counts (the rest is padding).
        var end = Array.IndexOf(tokens, tokens[^1]);
        var values = name.Contains("mask", StringComparison.OrdinalIgnoreCase)
            ? tokens.Select((_, i) => i <= end ? 1L : 0L).ToArray()
            : tokens;
        inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, [1, tokens.Length])));
    }
    return Base64(Run(session, inputs, "text_embeds"));
}

// Float or half-precision input, as the model wants.
static NamedOnnxValue Input(string name, NodeMetadata meta, float[] data, int[] shape) =>
    meta.ElementDataType == TensorElementType.Float16
        ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(data.Select(v => (Float16)v).ToArray(), shape))
        : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, shape));

static float[] Run(InferenceSession session, List<NamedOnnxValue> inputs, string preferredOutput)
{
    using var results = session.Run(inputs);
    var value = results.FirstOrDefault(r => r.Name == preferredOutput) ?? results.First();
    return value.ElementType == TensorElementType.Float16
        ? value.AsTensor<Float16>().Select(h => (float)h).ToArray()
        : value.AsTensor<float>().ToArray();
}

static string Base64(ReadOnlySpan<float> vector) => Convert.ToBase64String(MemoryMarshal.AsBytes(vector));

// CLIP's preprocessing: shorter side to 224 (bicubic), the centre 224 × 224, normalised per channel; planes R, G, B.
static async Task<float[]> PixelsAsync(string path)
{
    const int Size = 224, Plane = Size * Size;
    float[] mean = [0.48145466f, 0.4578275f, 0.40821073f], std = [0.26862954f, 0.26130258f, 0.27577711f];
    var file = await StorageFile.GetFileFromPathAsync(path);
    using var stream = await file.OpenReadAsync();
    var decoder = await BitmapDecoder.CreateAsync(stream);
    // Thumbnails are stored upright, so the file's own orientation is ignored.
    var scale = (double)Size / Math.Min(decoder.PixelWidth, decoder.PixelHeight);
    var width = (uint)Math.Max(Size, Math.Round(decoder.PixelWidth * scale));
    var height = (uint)Math.Max(Size, Math.Round(decoder.PixelHeight * scale));
    var transform = new BitmapTransform
    {
        ScaledWidth = width,
        ScaledHeight = height,
        InterpolationMode = BitmapInterpolationMode.Cubic,
        Bounds = new BitmapBounds { X = (width - Size) / 2, Y = (height - Size) / 2, Width = Size, Height = Size },
    };
    var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
        ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
    var bgra = data.DetachPixelData();
    var result = new float[3 * Plane];
    for (var i = 0; i < Plane; i++)
    {
        result[i] = (bgra[i * 4 + 2] / 255f - mean[0]) / std[0];
        result[Plane + i] = (bgra[i * 4 + 1] / 255f - mean[1]) / std[1];
        result[2 * Plane + i] = (bgra[i * 4] / 255f - mean[2]) / std[2];
    }
    return result;
}
