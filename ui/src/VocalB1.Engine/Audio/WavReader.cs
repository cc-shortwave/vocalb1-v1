using System.Buffers.Binary;
using VocalB1.Core.Audio;

namespace VocalB1.Engine.Audio;

public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, long FrameCount)
{
    public double DurationSeconds => SampleRate <= 0 ? 0 : FrameCount / (double)SampleRate;
}

/// <summary>
/// Lightweight RIFF/WAVE parser: it only does the two things the UI needs — duration and peak envelope.
/// Supports PCM 8/16/24/32 and IEEE float32 (including WAVE_FORMAT_EXTENSIBLE).
/// </summary>
public static class WavReader
{
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static WavInfo ReadInfo(string path)
    {
        using var stream = File.OpenRead(path);
        return ParseLayout(stream).Info;
    }

    public static WaveformData ReadPeaks(string path, int buckets = 2048)
    {
        using var stream = File.OpenRead(path);
        var layout = ParseLayout(stream);
        var info = layout.Info;

        if (info.FrameCount <= 0)
            return WaveformData.Empty;

        var count = Math.Clamp(buckets, 32, 8192);
        count = (int)Math.Min(count, info.FrameCount);

        var min = new float[count];
        var max = new float[count];
        for (var i = 0; i < count; i++)
        {
            min[i] = float.MaxValue;
            max[i] = float.MinValue;
        }

        stream.Position = layout.DataOffset;
        var blockAlign = layout.BlockAlign;
        var framesPerChunk = Math.Max(1, 16384 / Math.Max(1, blockAlign));
        var buffer = new byte[framesPerChunk * blockAlign];
        var isFloat = layout.FormatTag == FormatFloat;
        var bits = info.BitsPerSample;

        long frameIndex = 0;
        var bucketSize = info.FrameCount / (double)count;

        while (frameIndex < info.FrameCount)
        {
            var want = (int)Math.Min(framesPerChunk, info.FrameCount - frameIndex);
            var bytes = want * blockAlign;
            var read = stream.Read(buffer, 0, bytes);
            var got = read / blockAlign;
            if (got == 0)
                break;

            for (var f = 0; f < got; f++)
            {
                var offset = f * blockAlign;
                double sum = 0;
                for (var ch = 0; ch < info.Channels; ch++)
                    sum += DecodeSample(buffer.AsSpan(offset + ch * (bits / 8), bits / 8), bits, isFloat);
                var value = (float)(sum / info.Channels);

                var bucket = (int)((frameIndex + f) / bucketSize);
                if (bucket >= count)
                    bucket = count - 1;
                if (value < min[bucket]) min[bucket] = value;
                if (value > max[bucket]) max[bucket] = value;
            }

            frameIndex += got;
        }

        for (var i = 0; i < count; i++)
        {
            if (min[i] == float.MaxValue) min[i] = 0;
            if (max[i] == float.MinValue) max[i] = 0;
        }

        return new WaveformData(info.SampleRate, info.Channels, info.DurationSeconds, min, max);
    }

    private static float DecodeSample(ReadOnlySpan<byte> sample, int bits, bool isFloat)
    {
        switch (bits)
        {
            case 8:
                return (sample[0] - 128) / 128f;
            case 16:
                return BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f;
            case 24:
            {
                var v = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                if ((v & 0x800000) != 0)
                    v |= ~0xFFFFFF;
                return v / 8388608f;
            }
            case 32 when isFloat:
                return Math.Clamp(BitConverter.ToSingle(sample), -1f, 1f);
            case 32:
                return (float)(BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0);
            default:
                return 0f;
        }
    }

    private sealed record Layout(WavInfo Info, ushort FormatTag, int BlockAlign, long DataOffset, long DataLength);

    private static Layout ParseLayout(FileStream stream)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
            || header[8] != (byte)'W' || header[9] != (byte)'A' || header[10] != (byte)'V' || header[11] != (byte)'E')
        {
            throw new InvalidDataException("不是合法的 RIFF/WAVE 文件：" + stream.Name);
        }

        ushort formatTag = 0;
        int channels = 0, sampleRate = 0, blockAlign = 0, bits = 0;
        long dataOffset = -1, dataLength = 0;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader);
            var id = System.Text.Encoding.ASCII.GetString(chunkHeader[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (id == "fmt ")
            {
                var fmt = new byte[Math.Min((int)size, 40)];
                stream.ReadExactly(fmt, 0, fmt.Length);
                if (fmt.Length < 16)
                    throw new InvalidDataException("fmt 块过短：" + stream.Name);

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));

                if (formatTag == FormatExtensible && fmt.Length >= 26)
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));

                if (size - (uint)fmt.Length > 0)
                    stream.Seek((long)size - fmt.Length, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                dataOffset = stream.Position;
                dataLength = Math.Min(size, stream.Length - dataOffset);
                break;
            }
            else
            {
                stream.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }

        if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || blockAlign <= 0 || bits <= 0)
            throw new InvalidDataException("WAV 缺少 fmt/data 块或参数非法：" + stream.Name);

        var frames = dataLength / blockAlign;
        var info = new WavInfo(sampleRate, channels, bits, frames);
        return new Layout(info, formatTag, blockAlign, dataOffset, dataLength);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
                throw new EndOfStreamException("WAV 文件被截断。");
            total += read;
        }
    }
}
