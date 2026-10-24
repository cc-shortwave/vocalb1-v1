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

        var open = mciSendString($"open \"{wavPath}\" type waveaudio alias {Alias}", null, 0, IntPtr.Zero);
        if (open != 0)
            throw new InvalidOperationException($"系统无法打开该 WAV（MCI 错误码 {open}）。");

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
        if (!_opened)
            return;

        _opened = false;
        mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        Close();
        PlaybackStopped = null;
    }
}
