using System.Runtime.InteropServices.WindowsRuntime;
using PhotoGallery.Core;
using PhotoGallery.Core.Transcripts;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoGallery.App.Transcription;

/// <summary>
/// Speech to text for one video at a time, entirely on this PC: the sound track decoded to 16 kHz mono by Media
/// Foundation, speech found by Silero voice detection, and the joined speech recognised by Whisper large-v3-turbo
/// (whisper.cpp through Whisper.net; on the GPU through Vulkan when available, otherwise the CPU). The models stay
/// loaded while there's work and are released (with their GPU memory) by <see cref="Unload"/>.
/// </summary>
public sealed class SpeechTranscriber(string modelsDirectory) : IDisposable
{
    public const string ModelName = "whisper-large-v3-turbo";
    private const int SampleRate = 16000;
    /// <summary>Less speech than this and the video counts as having none.</summary>
    private const double MinSpeechSeconds = 1.0;
    private const long ModelBytes = 1_624_555_275;

    private readonly string _modelPath = Path.Combine(modelsDirectory, "ggml-large-v3-turbo.bin");
    private readonly string _vadPath = Path.Combine(modelsDirectory, "ggml-silero-vad.bin");
    private WhisperFactory? _whisper;
    private WhisperVadFactory? _vad;

    public bool ModelsReady => File.Exists(_modelPath) && File.Exists(_vadPath);

    /// <summary>Where recognition runs once a model is loaded ("Vulkan", "Cpu").</summary>
    public string? Runtime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Downloads the models from Hugging Face (about 1.6 GB, once). Progress is 0–1.</summary>
    public async Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelsDirectory);
        if (!File.Exists(_vadPath))
            await DownloadAsync(_vadPath, () => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V6_2_0, ct), 0, null, ct);
        if (!File.Exists(_modelPath))
            await DownloadAsync(_modelPath, () => WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.LargeV3Turbo, QuantizationType.NoQuantization, ct),
                ModelBytes, progress, ct);
    }

    private static async Task DownloadAsync(string path, Func<Task<Stream>> open, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        var partial = path + ".partial";
        await using (var source = await open())
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (expected > 0) progress?.Report(Math.Min(1, total / (double)expected));
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>Transcribes a video; progress is 0–1. Failures come back as a <see cref="TranscriptStatus.Failed"/> transcript.</summary>
    public async Task<Transcript> TranscribeAsync(long mediaId, string path, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var audio = await ExtractAudioAsync(path, ct);
            if (audio is null) return new Transcript(mediaId, TranscriptStatus.NoAudio, [], Model: ModelName);
            progress?.Report(0.05);

            _vad ??= WhisperVadFactory.FromPath(_vadPath);
            using var vad = _vad.CreateBuilder()
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
                // Pauses shorter than this stay in, so the recogniser hears whole sentences.
                .WithMinSilenceDuration(TimeSpan.FromSeconds(2))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(400))
                .Build();
            var regions = vad.DetectSpeech(audio).Select(r => (r.Start.TotalSeconds, r.End.TotalSeconds)).ToList();
            var stitched = SpeechStitcher.Join(audio, SampleRate, regions);
            if (stitched.SpeechSeconds < MinSpeechSeconds) return new Transcript(mediaId, TranscriptStatus.NoSpeech, [], Model: ModelName);
            progress?.Report(0.1);

            _whisper ??= WhisperFactory.FromPath(_modelPath);
            await using var processor = _whisper.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 4, 2, 8))
                .WithProgressHandler(percent => progress?.Report(0.1 + 0.9 * percent / 100.0))
                .Build();
            var segments = new List<TranscriptSegment>();
            string? language = null;
            await foreach (var s in processor.ProcessAsync(stitched.Samples, ct))
            {
                language ??= s.Language;
                segments.Add(new TranscriptSegment(Math.Round(stitched.ToOriginal(s.Start.TotalSeconds), 2),
                    Math.Round(stitched.ToOriginal(s.End.TotalSeconds), 2), s.Text));
            }
            var cleaned = TranscriptFormatter.Clean(segments);
            return cleaned.Count == 0
                ? new Transcript(mediaId, TranscriptStatus.NoSpeech, [], language, ModelName)
                : new Transcript(mediaId, TranscriptStatus.Done, cleaned, language, ModelName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Transcribing {path} failed", ex);
            return new Transcript(mediaId, TranscriptStatus.Failed, [], Model: ModelName, Error: ex.Message);
        }
    }

    /// <summary>The sound track as 16 kHz mono samples, or null if the video has none.</summary>
    private static async Task<float[]?> ExtractAudioAsync(string path, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var source = await MediaEncodingProfile.CreateFromFileAsync(file);
        if (source.Audio is null) return null;

        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);
        profile.Video = null;
        using var output = new InMemoryRandomAccessStream();
        using (var input = await file.OpenReadAsync())
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareStreamTranscodeAsync(input, output, profile);
            if (!prepared.CanTranscode)
            {
                if (prepared.FailureReason == TranscodeFailureReason.InvalidProfile) return null; // no usable sound
                throw new InvalidOperationException($"The sound track can't be decoded ({prepared.FailureReason}).");
            }
            await prepared.TranscodeAsync().AsTask(ct);
        }

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(ct);
        return ReadWav(bytes);
    }

    /// <summary>16-bit PCM samples from a WAV file's "data" chunk, as floats.</summary>
    private static float[] ReadWav(byte[] bytes)
    {
        var i = 12;
        while (i + 8 <= bytes.Length && System.Text.Encoding.ASCII.GetString(bytes, i, 4) != "data")
            i += 8 + BitConverter.ToInt32(bytes, i + 4);
        var start = Math.Min(bytes.Length, i + 8);
        var data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(start, (bytes.Length - start) & ~1));
        var samples = new float[data.Length];
        for (var k = 0; k < data.Length; k++) samples[k] = data[k] / 32768f;
        return samples;
    }

    /// <summary>Releases the models and the memory they hold (reloaded on the next transcription, ~2 s).</summary>
    public void Unload()
    {
        _whisper?.Dispose();
        _whisper = null;
        _vad?.Dispose();
        _vad = null;
    }

    public void Dispose() => Unload();
}
