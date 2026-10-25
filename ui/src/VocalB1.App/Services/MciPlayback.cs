using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VocalB1.Core.Abstractions;

namespace VocalB1.App.Services;

/// <summary>
/// WAV playback based on MCI (winmm): supports reading the current position and starting from an arbitrary time offset.
/// WPF MediaPlayer is not used because reopening the same file may never raise the ready event again.
/// </summary>
public sealed class MciPlayback : IAudioPlayback, IDisposable
{
    private const string Alias = "vocalb1";

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr hwnd);

    private bool _opened;
    private string? _tempWavPath;

    public bool IsPlaying => _opened && Query("mode") == "playing";

    public TimeSpan Position
    {
        get
        {
            if (!_opened)
                return TimeSpan.Zero;

            var text = Query("position");
            return long.TryParse(text, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    public event EventHandler? PlaybackStopped;

    public void Play(string wavPath, TimeSpan start)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("找不到音频文件。", wavPath);

        Close();

        // Many wave output devices reject 24-bit or float WAVs with MCIERR_WAVE_OUTPUTSUNSUITABLE (304),
        // so play a 16-bit PCM copy while leaving the exported file untouched.
        var playbackPath = wavPath;
        try
        {
            _tempWavPath = TryCreatePcm16Copy(wavPath);
            if (_tempWavPath is not null)
                playbackPath = _tempWavPath;
        }
        catch
        {
            DeleteTemp();
        }

        var open = mciSendString($"open \"{playbackPath}\" type waveaudio alias {Alias}", null, 0, IntPtr.Zero);
        if (open != 0)
        {
            DeleteTemp();
            throw new InvalidOperationException($"系统无法打开该 WAV（MCI 错误码 {open}）。");
        }

        _opened = true;

        var from = Math.Max(0, (long)start.TotalMilliseconds);
        var play = mciSendString($"play {Alias} from {from}", null, 0, IntPtr.Zero);
        if (play != 0)
        {
            Close();
            throw new InvalidOperationException($"系统无法播放该 WAV（MCI 错误码 {play}）。");
        }
    }

    public void Stop()
    {
        if (!_opened)
            return;

        Close();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private string Query(string what)
    {
        if (!_opened)
            return string.Empty;

        var buffer = new StringBuilder(64);
        var code = mciSendString($"status {Alias} {what}", buffer, buffer.Capacity, IntPtr.Zero);
        return code == 0 ? buffer.ToString().Trim() : string.Empty;
    }

    private void Close()
    {
        if (_opened)
        {
            _opened = false;
            mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        }

        DeleteTemp();
    }

    private void DeleteTemp()
    {
        if (_tempWavPath is null)
            return;

        try { File.Delete(_tempWavPath); } catch { /* best effort cleanup */ }
        _tempWavPath = null;
    }

    /// <summary>
    /// Returns a temporary 16-bit PCM copy for formats MCI devices commonly reject (24-bit, 32-bit, float),
    /// or null when the original file is already 16-bit PCM or cannot be converted.
    /// </summary>
    private static string? TryCreatePcm16Copy(string wavPath)
    {
        const ushort formatPcm = 1;
        const ushort formatFloat = 3;
        const ushort formatExtensible = 0xFFFE;

        using var source = File.OpenRead(wavPath);
        Span<byte> riff = stackalloc byte[12];
        source.ReadExactly(riff);
        if (riff[0] != (byte)'R' || riff[1] != (byte)'I' || riff[2] != (byte)'F' || riff[3] != (byte)'F'
            || riff[8] != (byte)'W' || riff[9] != (byte)'A' || riff[10] != (byte)'V' || riff[11] != (byte)'E')
            return null;

        ushort formatTag = 0;
        short channels = 0;
        var sampleRate = 0;
        short blockAlign = 0;
        short bits = 0;
        long dataOffset = -1;
        long dataLength = 0;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (source.Position + 8 <= source.Length)
        {
            source.ReadExactly(chunkHeader);
            var id = Encoding.ASCII.GetString(chunkHeader[..4]);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (id == "fmt ")
            {
                var fmt = new byte[Math.Min(size, 40)];
                source.ReadExactly(fmt);
                if (fmt.Length < 16)
                    return null;

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(12));
                bits = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(14));

                if (formatTag == formatExtensible && fmt.Length >= 26)
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));

                if (size > fmt.Length)
                    source.Seek(size - fmt.Length, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                dataOffset = source.Position;
                dataLength = Math.Min(size, source.Length - dataOffset);
                break;
            }
            else
            {
                source.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || blockAlign <= 0 || bits <= 0)
            return null;
        if (formatTag == formatPcm && bits == 16)
            return null;
        if (formatTag != formatPcm && formatTag != formatFloat)
            return null;

        var bytesPerSample = bits / 8;
        if (bytesPerSample <= 0)
            return null;

        var frames = dataLength / blockAlign;
        var dataBytes = frames * channels * 2L;
        if (dataBytes > int.MaxValue - 36)
            return null;

        var temp = Path.Combine(Path.GetTempPath(), "vocalb1-play-" + Guid.NewGuid().ToString("N") + ".wav");
        using var output = File.Create(temp);
        using var writer = new BinaryWriter(output);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write(formatPcm);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((int)dataBytes);

        source.Position = dataOffset;
        var isFloat = formatTag == formatFloat;
        var framesPerChunk = Math.Max(1, 16384 / Math.Max(1, (int)blockAlign));
        var buffer = new byte[framesPerChunk * blockAlign];

        long written = 0;
        while (written < frames)
        {
            var want = (int)Math.Min(framesPerChunk, frames - written);
            var read = source.Read(buffer, 0, want * blockAlign);
            var got = read / blockAlign;
            if (got == 0)
                break;

            for (var f = 0; f < got; f++)
            {
                var offset = f * blockAlign;
                for (var ch = 0; ch < channels; ch++)
                {
                    var sample = DecodeSample(buffer.AsSpan(offset + ch * bytesPerSample, bytesPerSample), bits, isFloat);
                    writer.Write((short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue));
                }
            }

            written += got;
        }

        return temp;
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
                var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                if ((value & 0x800000) != 0)
                    value |= ~0xFFFFFF;
                return value / 8388608f;
            }
            case 32 when isFloat:
                return Math.Clamp(BitConverter.ToSingle(sample), -1f, 1f);
            case 32:
                return (float)(BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0);
            default:
                return 0f;
        }
    }

    public void Dispose()
    {
        Close();
        PlaybackStopped = null;
    }
}
