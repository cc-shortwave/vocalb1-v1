using System.Buffers.Binary;
using System.Text;

namespace VocalB1.Engine.Audio;

/// <summary>
/// Mixes an imported BGM (backing track) into a rendered vocal WAV for preview playback.
/// The BGM always keeps its original speed (no scaling) and can start at an adjustable beat offset;
/// the result is a 16-bit PCM stereo WAV so MCI can play it on any output device.
/// </summary>
public static class BgmMixer
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Writes <paramref name="vocalPath"/> with <paramref name="bgmPath"/> mixed under it at
    /// <paramref name="bgmStartSeconds"/> and returns <paramref name="outputPath"/>.
    /// The output timeline starts at the vocal timeline (second 0), so the playhead stays valid.
    /// </summary>
    public static string Mix(string vocalPath, string bgmPath, double bgmStartSeconds, string outputPath)
    {
        var vocal = Decode(vocalPath);
        var bgm = Decode(bgmPath);

        var sampleRate = vocal.SampleRate;
        var offsetFrames = Math.Max(0L, (long)Math.Round(bgmStartSeconds * sampleRate));
        var ratio = bgm.SampleRate / (double)sampleRate;
        var bgmFrames = (long)Math.Ceiling(bgm.FrameCount / ratio);
        var totalFrames = Math.Max(vocal.FrameCount, offsetFrames + bgmFrames);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var output = File.Create(outputPath);
        WriteHeader(output, sampleRate, totalFrames * 4);

        const int chunkFrames = 4096;
        var buffer = new byte[chunkFrames * 4];
        long cursor = 0;
        while (cursor < totalFrames)
        {
            var count = (int)Math.Min(chunkFrames, totalFrames - cursor);
            for (var f = 0; f < count; f++)
            {
                var i = cursor + f;
                var left = i < vocal.FrameCount ? vocal.Left[i] : 0.0;
                var right = i < vocal.FrameCount ? vocal.Right[i] : 0.0;

                var j = i - offsetFrames;
                if (j >= 0 && j < bgmFrames && bgm.FrameCount > 0)
                {
                    var position = j * ratio;
                    var index0 = (long)Math.Floor(position);
                    var frac = position - index0;
                    var index1 = Math.Min(index0 + 1, bgm.FrameCount - 1);
                    left += bgm.Left[index0] + (bgm.Left[index1] - bgm.Left[index0]) * frac;
                    right += bgm.Right[index0] + (bgm.Right[index1] - bgm.Right[index0]) * frac;
                }

                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(f * 4), EncodeSample(left));
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(f * 4 + 2), EncodeSample(right));
            }

            output.Write(buffer, 0, count * 4);
            cursor += count;
        }

        return outputPath;
    }

    internal sealed record PcmBuffer(double[] Left, double[] Right, int SampleRate, long FrameCount);

    internal static PcmBuffer Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var layout = ParseLayout(path, stream);
        if (layout.Channels is < 1 or > 2)
            throw new InvalidDataException("BGM 只支持单声道或双声道 WAV：" + path);

        var bytesPerSample = layout.BitsPerSample / 8;
        var isFloat = layout.FormatTag == FormatFloat;
        var frameCount = layout.DataLength / layout.BlockAlign;
        if (frameCount > int.MaxValue)
            throw new InvalidDataException("BGM 过长，无法混音：" + path);

        var frameCount32 = (int)frameCount;
        var left = new double[frameCount32];
        var right = new double[frameCount32];
        stream.Position = layout.DataOffset;

        var framesPerChunk = Math.Max(1, 65536 / layout.BlockAlign);
        var buffer = new byte[framesPerChunk * layout.BlockAlign];
        var written = 0;
        while (written < frameCount)
        {
            var want = (int)Math.Min(framesPerChunk, frameCount - written);
            var read = stream.Read(buffer, 0, want * layout.BlockAlign);
            var got = read / layout.BlockAlign;
            if (got == 0)
                break;

            for (var f = 0; f < got; f++)
            {
                var offset = f * layout.BlockAlign;
                var valueLeft = DecodeSample(buffer.AsSpan(offset, bytesPerSample), layout.BitsPerSample, isFloat);
                var valueRight = layout.Channels == 2
                    ? DecodeSample(buffer.AsSpan(offset + bytesPerSample, bytesPerSample), layout.BitsPerSample, isFloat)
                    : valueLeft;
                left[written + f] = valueLeft;
                right[written + f] = valueRight;
            }

            written += got;
        }

        return new PcmBuffer(left, right, layout.SampleRate, frameCount);
    }

    internal static void WriteHeader(Stream output, int sampleRate, long dataBytes)
    {
        const ushort channels = 2;
        const ushort bits = 16;
        const int blockAlign = channels * bits / 8;

        var header = new byte[44];
        WriteAscii(header, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        WriteAscii(header, 8, "WAVEfmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), bits);
        WriteAscii(header, 36, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);
        output.Write(header);
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
            target[offset + i] = (byte)text[i];
    }

    internal static short EncodeSample(double value) =>
        (short)Math.Clamp((int)Math.Round(value * 32768), short.MinValue, short.MaxValue);

    private static double DecodeSample(ReadOnlySpan<byte> sample, int bits, bool isFloat)
    {
        switch (bits)
        {
            case 8:
                return (sample[0] - 128) / 128.0;
            case 16:
                return BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0;
            case 24:
            {
                var v = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                if ((v & 0x800000) != 0)
                    v |= ~0xFFFFFF;
                return v / 8388608.0;
            }
            case 32 when isFloat:
                return Math.Clamp(BitConverter.ToSingle(sample), -1f, 1f);
            case 32:
                return BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0;
            default:
                throw new InvalidDataException($"不支持 {bits} 位 WAV 样本。");
        }
    }

    private sealed record Layout(
        ushort FormatTag, int Channels, int SampleRate, int BitsPerSample, int BlockAlign, long DataOffset, long DataLength);

    private static Layout ParseLayout(string path, FileStream stream)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
            || header[8] != (byte)'W' || header[9] != (byte)'A' || header[10] != (byte)'V' || header[11] != (byte)'E')
        {
            throw new InvalidDataException("不是合法的 RIFF/WAVE 文件：" + path);
        }

        ushort formatTag = 0;
        int channels = 0, sampleRate = 0, blockAlign = 0, bits = 0;
        long dataOffset = -1, dataLength = 0;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader);
            var id = Encoding.ASCII.GetString(chunkHeader[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (id == "fmt ")
            {
                var fmt = new byte[Math.Min((int)size, 40)];
                stream.ReadExactly(fmt, 0, fmt.Length);
                if (fmt.Length < 16)
                    throw new InvalidDataException("fmt 块过短：" + path);

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
            throw new InvalidDataException("WAV 缺少 fmt/data 块或参数非法：" + path);

        return new Layout(formatTag, channels, sampleRate, bits, blockAlign, dataOffset, dataLength);
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
