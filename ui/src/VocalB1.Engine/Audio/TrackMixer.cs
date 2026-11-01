using System.Buffers.Binary;

namespace VocalB1.Engine.Audio;

/// <summary>
/// Sums the rendered WAVs of several tracks into one 16-bit PCM stereo WAV.
/// All tracks share the project timeline, so the files are aligned at frame 0; shorter tracks are
/// padded with silence, and a track rendered at another sample rate is resampled linearly.
/// </summary>
public static class TrackMixer
{
    /// <summary>Merges <paramref name="trackPaths"/> into <paramref name="outputPath"/> and returns that path.</summary>
    public static string Mix(IReadOnlyList<string> trackPaths, string outputPath)
    {
        if (trackPaths.Count == 0)
            throw new ArgumentException("At least one track WAV is required.", nameof(trackPaths));

        var inputs = new List<Source>(trackPaths.Count);
        foreach (var path in trackPaths)
        {
            var pcm = BgmMixer.Decode(path);
            inputs.Add(new Source(pcm, pcm.SampleRate));
        }

        var sampleRate = inputs[0].Pcm.SampleRate;
        long totalFrames = 0;
        foreach (var input in inputs)
            totalFrames = Math.Max(totalFrames, input.FrameCount);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var output = File.Create(outputPath);
        BgmMixer.WriteHeader(output, sampleRate, totalFrames * 4);

        const int chunkFrames = 4096;
        var buffer = new byte[chunkFrames * 4];
        long cursor = 0;
        while (cursor < totalFrames)
        {
            var count = (int)Math.Min(chunkFrames, totalFrames - cursor);
            for (var f = 0; f < count; f++)
            {
                var frame = cursor + f;
                double left = 0.0, right = 0.0;
                for (var k = 0; k < inputs.Count; k++)
                {
                    var source = inputs[k];
                    var position = frame * source.Ratio;
                    if (position >= source.FrameCount)
                        continue;

                    var index0 = (long)Math.Floor(position);
                    var frac = position - index0;
                    var index1 = Math.Min(index0 + 1, source.FrameCount - 1);
                    left += source.Pcm.Left[index0] + (source.Pcm.Left[index1] - source.Pcm.Left[index0]) * frac;
                    right += source.Pcm.Right[index0] + (source.Pcm.Right[index1] - source.Pcm.Right[index0]) * frac;
                }

                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(f * 4), BgmMixer.EncodeSample(left));
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(f * 4 + 2), BgmMixer.EncodeSample(right));
            }

            output.Write(buffer, 0, count * 4);
            cursor += count;
        }

        return outputPath;
    }

    /// <summary>A decoded track plus its resampling ratio relative to the mix sample rate.</summary>
    private readonly struct Source
    {
        public Source(BgmMixer.PcmBuffer pcm, int mixSampleRate)
        {
            Pcm = pcm;
            Ratio = pcm.SampleRate / (double)mixSampleRate;
            FrameCount = (long)Math.Ceiling(pcm.FrameCount / Ratio);
        }

        public BgmMixer.PcmBuffer Pcm { get; }
        public double Ratio { get; }
        public long FrameCount { get; }
    }
}
