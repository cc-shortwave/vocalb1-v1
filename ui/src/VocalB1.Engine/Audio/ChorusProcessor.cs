using VocalB1.Core.Chorus;
using VocalB1.Core.Models;

namespace VocalB1.Engine.Audio;

/// <summary>
/// Turns one rendered mono vocal track into the stereo contribution of the chorus singers bound to it.
/// Every singer is processed separately at its own position over time:
/// distance attenuation (1/r), frequency-dependent air absorption applied as a spectral gain (STFT), and
/// constant-power panning from the singer's X position. The results of all singers are summed.
/// </summary>
public static class ChorusProcessor
{
    /// <summary>STFT analysis/synthesis window (Hann) and hop; 4x overlap keeps the modulation smooth.</summary>
    private const int WindowSize = 1024;

    private const int HopSize = 256;

    /// <summary>
    /// Processes <paramref name="inputPath"/> for each singer and writes a PCM stereo WAV at
    /// <paramref name="bitDepth"/> (16 or 24) to <paramref name="outputPath"/>.
    /// Singers that are disabled are skipped.
    /// </summary>
    public static string Process(
        string inputPath,
        string outputPath,
        ChorusDocument chorus,
        IReadOnlyList<ChorusSinger> singers,
        double bpm,
        int bitDepth = 16)
    {
        var pcm = BgmMixer.Decode(inputPath);
        var frameCount = (int)Math.Min(pcm.FrameCount, int.MaxValue);
        var sampleRate = pcm.SampleRate;

        var mono = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var value = (pcm.Left[i] + pcm.Right[i]) * 0.5;
            mono[i] = double.IsFinite(value) ? value : 0.0;
        }

        var left = new double[frameCount];
        var right = new double[frameCount];
        var window = BuildHannWindow();

        foreach (var singer in singers)
        {
            if (!singer.Enabled)
                continue;
            RenderSinger(mono, sampleRate, chorus, singer, Math.Max(1, bpm), left, right);
        }

        // The Hann analysis/synthesis pair at 4x overlap sums to 1.5 for an interior sample; the
        // accumulated window square handles the short ramp at both ends exactly as well.
        var normalization = BuildNormalization(window, frameCount);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytesPerFrame = BgmMixer.BytesPerFrame(bitDepth);
        using var output = File.Create(outputPath);
        BgmMixer.WriteHeader(output, sampleRate, frameCount * bytesPerFrame, bitDepth);

        const int chunkFrames = 4096;
        var buffer = new byte[chunkFrames * bytesPerFrame];
        long cursor = 0;
        while (cursor < frameCount)
        {
            var count = (int)Math.Min(chunkFrames, frameCount - cursor);
            for (var f = 0; f < count; f++)
            {
                var i = cursor + f;
                var scale = normalization[i] > 1e-4 ? 1.0 / normalization[i] : 0.0;
                var frameOffset = f * bytesPerFrame;
                BgmMixer.WriteSample(
                    buffer.AsSpan(frameOffset, bytesPerFrame / 2), left[i] * scale, bitDepth);
                BgmMixer.WriteSample(
                    buffer.AsSpan(frameOffset + bytesPerFrame / 2, bytesPerFrame / 2), right[i] * scale, bitDepth);
            }

            output.Write(buffer, 0, count * bytesPerFrame);
            cursor += count;
        }

        return outputPath;
    }

    /// <summary>Adds one singer's pan/attenuated HF-filtered signal into the stereo accumulators.</summary>
    private static void RenderSinger(
        double[] mono,
        int sampleRate,
        ChorusDocument chorus,
        ChorusSinger singer,
        double bpm,
        double[] left,
        double[] right)
    {
        var frameCount = mono.Length;
        var window = BuildHannWindow();
        var fft = new Fft(WindowSize);
        var spectrumGain = BuildSpectrumGainTable(sampleRate);
        var real = new double[WindowSize];
        var imag = new double[WindowSize];

        for (var start = 0; start < frameCount; start += HopSize)
        {
            var center = start + WindowSize / 2;
            var beat = 1.0 + center * 60.0 / (sampleRate * bpm);
            var (x, y) = ChorusMath.Evaluate(singer.Path, beat);
            var distance = ChorusMath.Distance(x, y, chorus.MicX, chorus.MicY);
            var distanceGain = ChorusMath.DistanceGain(distance);
            var (panLeft, panRight) = ChorusMath.PanGains(x, chorus.StageWidth);

            for (var i = 0; i < WindowSize; i++)
            {
                var index = start + i;
                real[i] = index < frameCount ? mono[index] * window[i] : 0.0;
                imag[i] = 0.0;
            }

            fft.Forward(real, imag);

            for (var bin = 0; bin < WindowSize; bin++)
            {
                // Keep the filter symmetric around Nyquist so the real signal stays real after the IFFT.
                var frequencyBin = bin <= WindowSize / 2 ? bin : WindowSize - bin;
                var gain = distanceGain * Math.Exp(-spectrumGain[frequencyBin] * distance / 20.0);
                real[bin] *= gain;
                imag[bin] *= gain;
            }

            fft.Inverse(real, imag);

            for (var i = 0; i < WindowSize; i++)
            {
                var index = start + i;
                if (index >= frameCount)
                    break;
                var value = real[i] * window[i];
                left[index] += value * panLeft;
                right[index] += value * panRight;
            }
        }
    }

    private static double[] BuildHannWindow()
    {
        var window = new double[WindowSize];
        for (var i = 0; i < WindowSize; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1));
        return window;
    }

    private static double[] BuildNormalization(double[] window, int frameCount)
    {
        var normalization = new double[frameCount];
        for (var start = 0; start < frameCount; start += HopSize)
        {
            for (var i = 0; i < WindowSize; i++)
            {
                var index = start + i;
                if (index >= frameCount)
                    break;
                normalization[index] += window[i] * window[i];
            }
        }

        return normalization;
    }

    /// <summary>Air absorption in dB/m for every FFT bin (mirrored above Nyquist for a symmetric filter).</summary>
    private static double[] BuildSpectrumGainTable(int sampleRate)
    {
        var table = new double[WindowSize];
        for (var bin = 0; bin < WindowSize; bin++)
        {
            var frequencyBin = bin <= WindowSize / 2 ? bin : WindowSize - bin;
            var frequency = frequencyBin * sampleRate / (double)WindowSize;
            table[bin] = ChorusMath.AirAbsorptionDbPerMeter(frequency);
        }

        return table;
    }

    /// <summary>Minimal iterative radix-2 FFT (no external dependencies).</summary>
    private sealed class Fft
    {
        private readonly int _size;
        private readonly int[] _reverse;
        private readonly double[] _cos;
        private readonly double[] _sin;

        public Fft(int size)
        {
            _size = size;
            _reverse = new int[size];
            _cos = new double[size / 2];
            _sin = new double[size / 2];

            var bits = 0;
            while ((1 << bits) < size)
                bits++;
            for (var i = 0; i < size; i++)
            {
                var value = 0;
                for (var b = 0; b < bits; b++)
                {
                    if ((i & (1 << b)) != 0)
                        value |= 1 << (bits - 1 - b);
                }

                _reverse[i] = value;
            }

            for (var i = 0; i < size / 2; i++)
            {
                _cos[i] = Math.Cos(2 * Math.PI * i / size);
                _sin[i] = Math.Sin(2 * Math.PI * i / size);
            }
        }

        public void Forward(double[] real, double[] imag) => Transform(real, imag, false);

        public void Inverse(double[] real, double[] imag)
        {
            Transform(real, imag, true);
            for (var i = 0; i < _size; i++)
            {
                real[i] /= _size;
                imag[i] /= _size;
            }
        }

        private void Transform(double[] real, double[] imag, bool inverse)
        {
            for (var i = 0; i < _size; i++)
            {
                var j = _reverse[i];
                if (j <= i)
                    continue;
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }

            for (var length = 2; length <= _size; length <<= 1)
            {
                var half = length / 2;
                var step = _size / length;
                for (var start = 0; start < _size; start += length)
                {
                    for (var k = 0; k < half; k++)
                    {
                        var cos = _cos[k * step];
                        var sin = _sin[k * step];
                        var i = start + k;
                        var j = i + half;

                        var wr = cos * real[j] + (inverse ? sin : -sin) * imag[j];
                        var wi = cos * imag[j] - (inverse ? sin : -sin) * real[j];

                        real[j] = real[i] - wr;
                        imag[j] = imag[i] - wi;
                        real[i] += wr;
                        imag[i] += wi;
                    }
                }
            }
        }
    }
}
