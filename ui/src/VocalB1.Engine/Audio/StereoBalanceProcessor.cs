using System.Buffers.Binary;

namespace VocalB1.Engine.Audio;

/// <summary>
/// Post-processing for render results: expands a mono WAV to stereo and distributes energy between
/// the left and right channels according to the "pan ratio".
/// 0 = all left channel, 50 = balanced (file left untouched), 100 = all right channel.
/// The Python engine only outputs mono, so channel distribution is done on the C# side and involves
/// no engine parameters.
/// </summary>
public static class StereoBalanceProcessor
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Rewrites the stereo energy of the WAV according to the pan ratio; at 50 the file is left unchanged. Returns whether the file was rewritten.</summary>
    public static bool Apply(string path, double balance)
    {
        var ratio = Math.Clamp(balance, 0.0, 100.0) / 100.0;
        if (Math.Abs(ratio - 0.5) < 0.005)
            return false;

        var temp = path + ".balance.tmp";
        try
        {
            using (var input = File.OpenRead(path))
            {
                var layout = ParseLayout(path, input);
                if (layout.Channels > 2)
                    return false;

                var bits = layout.BitsPerSample;
                var bytesPerSample = bits / 8;
                var isFloat = layout.FormatTag == FormatFloat;

                // Gain is 1 at the balance point and decays linearly toward either side, so the overall
                // energy does not get louder or quieter as a whole when the slider moves.
                var leftGain = Math.Min(1.0, 2.0 * (1.0 - ratio));
                var rightGain = Math.Min(1.0, 2.0 * ratio);

                var frames = layout.DataLength / layout.BlockAlign;
                var dataBytes = frames * bytesPerSample * 2;

                using (var output = File.Create(temp))
                {
                    WriteHeader(output, layout.SampleRate, bits, isFloat, dataBytes);
                    input.Position = layout.DataOffset;

                    var framesPerChunk = Math.Max(1024, 65536 / layout.BlockAlign);
                    var source = new byte[framesPerChunk * layout.BlockAlign];
                    var target = new byte[framesPerChunk * 2 * bytesPerSample];
                    var remaining = frames;

                    while (remaining > 0)
                    {
                        var want = (int)Math.Min(framesPerChunk, remaining);
                        var read = input.Read(source, 0, want * layout.BlockAlign);
                        var got = read / layout.BlockAlign;
                        if (got == 0)
                            break;

                        for (var f = 0; f < got; f++)
                        {
                            var frame = source.AsSpan(f * layout.BlockAlign, layout.BlockAlign);
                            var left = DecodeSample(frame[..bytesPerSample], bits, isFloat);
                            var right = layout.Channels == 2
                                ? DecodeSample(frame.Slice(bytesPerSample, bytesPerSample), bits, isFloat)
                                : left;

                            var pair = target.AsSpan(f * 2 * bytesPerSample, 2 * bytesPerSample);
                            EncodeSample(pair[..bytesPerSample], left * leftGain, bits, isFloat);
                            EncodeSample(pair.Slice(bytesPerSample, bytesPerSample), right * rightGain, bits, isFloat);
                        }

                        output.Write(target, 0, got * 2 * bytesPerSample);
                        remaining -= got;
                    }
                }
            }

            File.Move(temp, path, true);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void WriteHeader(Stream output, int sampleRate, int bits, bool isFloat, long dataBytes)
    {
        var bytesPerSample = bits / 8;
        var blockAlign = (ushort)(bytesPerSample * 2);

        var header = new byte[44];
        WriteAscii(header, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        WriteAscii(header, 8, "WAVEfmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), isFloat ? FormatFloat : FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), (ushort)bits);
        WriteAscii(header, 36, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);
        output.Write(header);
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
            target[offset + i] = (byte)text[i];
    }

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
                return 0.0;
        }
    }

    private static void EncodeSample(Span<byte> target, double value, int bits, bool isFloat)
    {
        if (bits == 32 && isFloat)
        {
            BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits((float)value));
            return;
        }

        value = Math.Clamp(value, -1.0, 1.0);
        switch (bits)
        {
            case 8:
                target[0] = (byte)Math.Clamp((int)Math.Round(value * 128 + 128), 0, 255);
                break;
            case 16:
            {
                var v = (int)Math.Round(value * 32768);
                BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Clamp(v, short.MinValue, short.MaxValue));
                break;
            }
            case 24:
            {
                var v = Math.Clamp((int)Math.Round(value * 8388608), -8388608, 8388607);
                target[0] = (byte)(v & 0xFF);
                target[1] = (byte)((v >> 8) & 0xFF);
                target[2] = (byte)((v >> 16) & 0xFF);
                break;
            }
            case 32:
            {
                var v = Math.Clamp((long)Math.Round(value * 2147483648.0), int.MinValue, int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(target, (int)v);
                break;
            }
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
            var id = System.Text.Encoding.ASCII.GetString(chunkHeader[..4]);
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
