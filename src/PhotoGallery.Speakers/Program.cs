// Who's speaking, for Photo Gallery's video transcripts (the .csproj says why this is its own process).
//
//   PhotoGallery.Speakers <segmentation.onnx> <embedding.onnx>
//
// JSON, one message a line. Once the models are loaded it writes {"ready":true}, then answers each request from stdin,
// in order, until stdin closes:
//   {"samples":"C:\\...\\speech.f32"}  → {"turns":[{"start":0.5,"end":4.2,"speaker":0}, ...]}
// The file holds 16 kHz mono audio as raw float32; times are seconds into it. Failures are answered with
// {"error":"..."}. Nothing else is written to stdout.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SherpaOnnx;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: PhotoGallery.Speakers <segmentation.onnx> <embedding.onnx>");
    return 2;
}
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; // background work; the app comes first

var threads = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
var config = new OfflineSpeakerDiarizationConfig();
config.Segmentation.Pyannote.Model = args[0];
config.Segmentation.NumThreads = threads;
config.Embedding.Model = args[1];
config.Embedding.NumThreads = threads;
config.Clustering.NumClusters = -1; // unknown: decided by the threshold
// How different two voices must sound to be kept apart (tuned so one person walking, driving or changing tone stays one
// speaker, while clearly different voices separate).
config.Clustering.Threshold = 0.85f;
config.MinDurationOn = 0.3f;
config.MinDurationOff = 0.5f;
using var diarization = new OfflineSpeakerDiarization(config);

var output = Console.Out;
output.WriteLine(JsonSerializer.Serialize(new { ready = true }));
output.Flush();

while (Console.In.ReadLine() is { } line)
{
    object reply;
    try
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if ((string?)request["samples"] is { } path)
        {
            var samples = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();
            reply = new { turns = diarization.Process(samples).Select(t => new { start = t.Start, end = t.End, speaker = t.Speaker }) };
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
