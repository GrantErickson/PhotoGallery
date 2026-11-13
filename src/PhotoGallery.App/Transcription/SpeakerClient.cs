using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PhotoGallery.Core;
using PhotoGallery.Core.Transcripts;

namespace PhotoGallery.App.Transcription;

/// <summary>
/// Talks to PhotoGallery.Speakers (the "speakers" folder next to the app): speaker diarization in a process of its own,
/// so its ONNX Runtime never meets the one Windows loads into the app, and a crash in it costs one video its speaker
/// labels instead of taking the app down. One request at a time; the process is started on first use and stopped with
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class SpeakerClient(string segmentationModel, string embeddingModel) : IDisposable
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "speakers", "PhotoGallery.Speakers.exe");
    private Process? _process;

    /// <summary>Who speaks when in 16 kHz mono audio; times are seconds into it.</summary>
    public async Task<List<SpeakerTurn>> FindTurnsAsync(float[] samples, int sampleRate, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"PhotoGallery-speech-{Guid.NewGuid():N}.f32");
        try
        {
            using (var stream = File.Create(file)) stream.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
            var process = await StartAsync(ct);
            await process.StandardInput.WriteLineAsync(new JsonObject { ["samples"] = file }.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            // CPU work: a few seconds a minute of speech. Far longer means it's stuck.
            var limit = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(samples.Length / (double)sampleRate);
            var line = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(limit, ct);
            if (line is null) throw new IOException($"The speaker detector stopped unexpectedly{ExitCode(process)}.");
            var reply = JsonNode.Parse(line)!.AsObject();
            if ((string?)reply["error"] is { } error) throw new InvalidOperationException(error);
            return (reply["turns"]?.AsArray() ?? throw new InvalidDataException("The speaker detector sent no turns."))
                .Select(t => new SpeakerTurn((double)t!["start"]!, (double)t["end"]!, (int)t["speaker"]!))
                .ToList();
        }
        catch
        {
            Stop(); // crashed, stuck or cancelled with a reply on its way: start clean next time
            throw;
        }
        finally
        {
            File.Delete(file);
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
                ArgumentList = { segmentationModel, embeddingModel },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"Speakers: {e.Data}");
        };
        process.Start();
        process.BeginErrorReadLine();
        _process = process;
        var ready = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromMinutes(1), ct);
        if (ready is null || JsonNode.Parse(ready) is not JsonObject hello || hello["ready"]?.GetValue<bool>() != true)
            throw new IOException($"The speaker detector didn't start{ExitCode(process)}.");
        return process;
    }

    private static string ExitCode(Process process) =>
        process.WaitForExit(1000) ? $" (exit code 0x{process.ExitCode:X8})" : "";

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

    public void Dispose() => Stop();
}
