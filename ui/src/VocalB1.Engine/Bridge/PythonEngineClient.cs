using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VocalB1.Core.Abstractions;
using VocalB1.Core.Configuration;
using VocalB1.Core.Models;
using VocalB1.Core.Serialization;

namespace VocalB1.Engine.Bridge;

/// <summary>
/// C# client for the long-lived Python bridge process.
/// Lifecycle: started before the first render → handshake → serial renders → shutdown when the app exits.
/// Cancellation/timeout terminates the process and cleans up partial output; the next call restarts it automatically.
/// </summary>
public sealed class PythonEngineClient : IRenderEngine
{
    private readonly EngineOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _process;
    private CancellationTokenSource? _pumpCts;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private PendingRender? _pending;
    private TaskCompletionSource<bool>? _handshake;
    private bool _ready;
    private bool _disposed;

    private const int StderrTailLimit = 200;

    public PythonEngineClient(EngineOptions options) => _options = options;

    public bool IsReady => _ready;

    public string? PythonVersion { get; private set; }

    // ------------------------------------------------------------------
    // Startup and handshake
    // ------------------------------------------------------------------
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process is { HasExited: false } && _ready)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _ready)
                return;

            StopProcess();

            if (!File.Exists(_options.BridgeScriptPath))
                throw new EngineException($"找不到桥接脚本：{_options.BridgeScriptPath}");

            var python = ResolvePython();
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = _options.RepositoryRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(_options.BridgeScriptPath);
            psi.ArgumentList.Add("--serve");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            try
            {
                _process = Process.Start(psi) ?? throw new EngineException("无法启动 Python 进程。");
            }
            catch (Exception ex) when (ex is not EngineException)
            {
                throw new EngineException($"无法启动 Python：{python}\n{ex.Message}", ex);
            }

            _handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pumpCts = new CancellationTokenSource();
            _stdoutPump = Task.Run(() => PumpStdoutAsync(_process, _pumpCts.Token));
            _stderrPump = Task.Run(() => PumpStderrAsync(_process, _pumpCts.Token));

            await WriteLineAsync(new { id = "init", cmd = "ping" }, cancellationToken).ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(_options.StartupTimeoutSeconds);
            try
            {
                await _handshake.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StopProcess();
                throw new EngineException(
                    $"Python 引擎握手超时（{timeout.TotalSeconds:F0}s）。\n{StderrSummary()}");
            }
            catch
            {
                StopProcess();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------
    public async Task<RenderResult> RenderAsync(
        RenderRequest request,
        IProgress<EngineEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var issues = ProjectValidator.Validate(request.Track, request.Project);
        if (ProjectValidator.HasError(issues))
        {
            var errors = string.Join("\n", issues
                .Where(i => i.Severity == ProjectIssueSeverity.Error)
                .Select(i => "• " + i.Message));
            throw new EngineException("工程校验未通过：\n" + errors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
                throw new EngineException("引擎尚未初始化。");

            if (_process is null || _process.HasExited)
            {
                _ready = false;
                throw new EngineException("Python 进程已退出，请重试。\n" + StderrSummary());
            }

            var id = Guid.NewGuid().ToString("N");
            var pending = new PendingRender(id, request.OutputPath, progress);
            lock (_sync)
                _pending = pending;

            progress?.Report(new EngineEvent(EngineEventKind.Log, "已提交渲染请求…"));

            var payload = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["cmd"] = "render",
                ["output"] = request.OutputPath,
                ["project"] = new
                {
                    bpm = request.Project.Bpm,
                    beatsPerBar = request.Project.BeatsPerBar,
                    beatUnit = request.Project.BeatUnit,
                    segments = request.Track.Segments,
                    pitchLine = request.Track.BuildRenderPitchLine(),
                    settings = request.Track.Settings,
                },
            };
            var json = JsonSerializer.Serialize(payload, BridgeJson.RequestOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.RenderTimeoutSeconds));
            using var registration = timeoutCts.Token.Register(() =>
            {
                pending.TryCancel(timeoutCts.Token);
                StopProcess();
            });

            await WriteLineAsync(json, timeoutCts.Token).ConfigureAwait(false);

            try
            {
                return await pending.Completion.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("渲染已取消，Python 进程已终止。", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new EngineException($"渲染超时（{_options.RenderTimeoutSeconds}s），进程已终止。");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pending, pending))
                        _pending = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Output pumps
    // ------------------------------------------------------------------
    private async Task PumpStdoutAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _handshake?.TrySetException(new EngineException("读取引擎输出失败：" + ex.Message, ex));
        }

        // End of process output = the process is no longer usable
        _ready = false;
        _handshake?.TrySetException(new EngineException("Python 进程意外退出。\n" + StderrSummary()));
        lock (_sync)
        {
            _pending?.TryFail(new EngineException("Python 进程意外退出。\n" + StderrSummary()));
        }
    }

    private async Task PumpStderrAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                lock (_sync)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrTailLimit)
                        _stderrTail.Dequeue();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // stderr is for diagnostics only; read failures do not affect the main flow
        }
    }

    private void HandleLine(string line)
    {
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(line, BridgeJson.Options);
        }
        catch (JsonException)
        {
            _pending?.Progress?.Report(new EngineEvent(EngineEventKind.Log, line));
            return;
        }

        if (message is null)
            return;

        switch (message.Type)
        {
            case "boot":
                break;

            case "ready":
                PythonVersion = message.Python;
                if (!string.IsNullOrEmpty(message.ImportError))
                {
                    _ready = false;
                    var importFailure = "Python 引擎导入失败：" + message.ImportError + "\n" + StderrSummary();
                    importFailure += "\nInterpreter: " + _options.PythonPath;
                    if (message.ImportError.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase))
                    {
                        importFailure += "\nMissing dependency. Run:\n  \""
                                         + _options.PythonPath + "\" -m pip install -r requirements.txt";
                    }
                    _handshake?.TrySetException(new EngineException(importFailure));
                    break;
                }
                _ready = true;
                _handshake?.TrySetResult(true);
                break;

            case "log":
                _pending?.Progress?.Report(new EngineEvent(
                    message.Level is "warn" or "error" ? EngineEventKind.Warning : EngineEventKind.Log,
                    message.Message ?? string.Empty));
                break;

            case "progress":
                _pending?.Progress?.Report(new EngineEvent(
                    EngineEventKind.Progress,
                    message.Message ?? string.Empty,
                    message.Value));
                break;

            case "result":
            {
                var pending = _pending;
                if (pending is null)
                    break;
                pending.Progress?.Report(new EngineEvent(
                    EngineEventKind.Log,
                    $"渲染完成：{message.Output}（音频 {message.DurationSec:F2}s，用时 {message.ElapsedSec:F2}s）"));
                pending.TrySucceed(new RenderResult(
                    message.Output ?? pending.OutputPath,
                    TimeSpan.FromSeconds(message.ElapsedSec ?? 0))
                {
                    DurationSeconds = message.DurationSec ?? 0,
                });
                break;
            }

            case "error":
                var detail = message.Message ?? "未知错误";
                if (!string.IsNullOrWhiteSpace(message.Traceback))
                    detail += "\n\n" + message.Traceback;
                _pending?.TryFail(new EngineException(detail));
                if (_pending is null)
                    _handshake?.TrySetException(new EngineException(detail));
                break;
        }
    }

    private async Task WriteLineAsync(object payload, CancellationToken token)
    {
        // The protocol requires "one JSON per line", so the indented project-file format cannot be reused
        var json = payload as string ?? JsonSerializer.Serialize(payload, BridgeJson.RequestOptions);
        var process = _process;
        if (process is null || process.HasExited)
            throw new EngineException("Python 进程不可用。");

        try
        {
            // Write UTF-8 bytes directly: this bypasses StreamWriter's default encoding differences,
            // so protocol lines are always UTF-8 without BOM
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new EngineException("写入引擎请求失败：" + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------
    // Process management
    // ------------------------------------------------------------------
    private string ResolvePython()
    {
        var python = _options.PythonPath;
        if (string.IsNullOrWhiteSpace(python))
            throw new EngineException("未配置 Python 解释器路径。");

        if (Path.IsPathRooted(python) && !File.Exists(python))
            throw new EngineException($"Python 解释器不存在：{python}\n请在 ui/engine.settings.json 中修改 pythonPath。");

        return python;
    }

    private void StopProcess()
    {
        _ready = false;
        _pumpCts?.Cancel();
        var process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1200))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
            finally
            {
                process.Dispose();
            }
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _stdoutPump = null;
        _stderrPump = null;
    }

    private string StderrSummary()
    {
        lock (_sync)
        {
            if (_stderrTail.Count == 0)
                return "（无 stderr 输出）";
            var text = string.Join("\n", _stderrTail.TakeLast(20));
            return "stderr 摘要：\n" + text;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            if (_process is { HasExited: false })
            {
                try
                {
                    await WriteLineAsync(new { id = "shutdown", cmd = "shutdown" }, CancellationToken.None)
                        .ConfigureAwait(false);
                    await Task.Run(() => _process.WaitForExit(800)).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            }
        }
        finally
        {
            StopProcess();
            _gate.Dispose();
            _disposed = true;
        }
    }

    private sealed class PendingRender(string id, string outputPath, IProgress<EngineEvent>? progress)
    {
        private readonly TaskCompletionSource<RenderResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;
        public IProgress<EngineEvent>? Progress { get; } = progress;
        public string OutputPath { get; } = outputPath;
        public Task<RenderResult> Completion => _completion.Task;

        public void TrySucceed(RenderResult result) => _completion.TrySetResult(result);
        public void TryFail(Exception ex) => _completion.TrySetException(ex);
        public void TryCancel(CancellationToken token) => _completion.TrySetCanceled(token);
    }
}
