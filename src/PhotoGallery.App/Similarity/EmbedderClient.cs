using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoGallery.Core;

namespace PhotoGallery.App.Similarity;

/// <summary>
/// Talks to PhotoGallery.Embedder (the "embedder" folder next to the app): CLIP on the graphics card in a process of
/// its own. One request at a time; the process is started on first use and stopped with <see cref="Dispose"/>.
/// </summary>
public sealed class EmbedderClient(string visionModel, string textModel) : IDisposable
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "embedder", "PhotoGallery.Embedder.exe");
    private readonly SemaphoreSlim _gate = new(1);
    private Process? _process;

    public static bool IsInstalled => File.Exists(Exe);

    /// <summary>"DirectML" or "CPU" once started.</summary>
    public string? Device { get; private set; }

    /// <summary>Embeddings of image files (null for any that couldn't be read), as raw CLIP vectors.</summary>
    public async Task<float[]?[]> EmbedImagesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new JsonObject { ["images"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()) }, ct);
        var vectors = reply["vectors"]?.AsArray() ?? throw new InvalidDataException("The embedder sent no vectors.");
        return vectors.Select(v => v is null ? null : Decode((string)v!)).ToArray();
    }

    /// <summary>The embedding of a text, from its CLIP token ids.</summary>
    public async Task<float[]> EmbedTextAsync(long[] tokens, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new JsonObject { ["tokens"] = new JsonArray(tokens.Select(t => (JsonNode)t).ToArray()) }, ct);
        return Decode((string?)reply["vector"] ?? throw new InvalidDataException("The embedder sent no vector."));
    }

    private static float[] Decode(string base64) => MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(base64)).ToArray();

    private async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var process = await StartAsync(ct);
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                Stop();
                throw new IOException("The embedder stopped unexpectedly.");
            }
            var reply = JsonNode.Parse(line)!.AsObject();
            if ((string?)reply["error"] is { } error) throw new InvalidOperationException(error);
            return reply;
        }
        catch (OperationCanceledException)
        {
            Stop(); // a reply may still be on its way; start clean next time
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Process> StartAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } running) return running;
        Stop();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Exe)
            {
                ArgumentList = { visionModel, textModel },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"Embedder: {e.Data}");
        };
        process.Start();
        process.BeginErrorReadLine();
        _process = process;
        // Loading the model onto the graphics card takes a few seconds.
        var ready = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromMinutes(3), ct);
        if (ready is null || JsonNode.Parse(ready) is not JsonObject hello || hello["ready"]?.GetValue<bool>() != true)
        {
            Stop();
            throw new IOException("The embedder didn't start.");
        }
        Device = (string?)hello["device"];
        Log.Info($"Embedder started on {Device}");
        return process;
    }

    private void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close(); // it exits when its input ends
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
        _process.Dispose();
        _process = null;
    }

    /// <summary>Stops the process when nothing is being asked of it (it's started again on the next request).</summary>
    public async Task StopWhenIdleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => Stop();
}
