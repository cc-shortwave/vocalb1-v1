using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocalB1.Core.Configuration;
using VocalB1.Core.Diagnostics;

namespace VocalB1.Core.Scripting;

/// <summary>
/// Runs third-party .py files as node-graph compute tools inside a locked-down worker process.
///
/// Security model:
/// - the script only receives the five numeric inputs of its node and may only return one finite number;
/// - the worker is an isolated CPython (<c>-I -S -B</c>), started in a throw-away empty directory with a
///   scrubbed environment, so no project or user data is reachable by default;
/// - a PEP 578 audit hook blocks every filesystem, network, process, import and interpreter capability;
/// - the user module gets a whitelisted builtin set (no import / open / eval / exec / print …);
/// - the response must be exactly <c>{"id": …, "value": number}</c>; anything else is a protocol violation.
///
/// Any violation (blocked capability, malformed or oversized response, timeout) terminates the worker
/// immediately and hands the script to <see cref="NodeScriptQuarantine"/>, which compresses and seals it
/// and blocks its SHA-256 for the current and future sessions.
/// </summary>
public static class PythonNodeSandbox
{
    /// <summary>Number of scalar inputs a script node always receives.</summary>
    public const int InputCount = 5;

    private const int MaxScriptBytes = 256 * 1024;
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxResponseChars = 16 * 1024;
    private const int CallTimeoutMs = 2000;
    private const int CacheLimit = 100_000;
    private const double InputMagnitudeLimit = 1e12;

    private static readonly object Sync = new();
    private static readonly Dictionary<(string Sha, double A, double B, double C, double D, double E), double> Results = new();
    private static readonly ConcurrentDictionary<string, ScriptSource> Scripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> StderrTail = new();

    private static Process? _worker;
    private static string _workDirectory = string.Empty;
    private static string _pythonPath = string.Empty;
    private static string _repositoryRoot = string.Empty;
    private static string? _sampleAveragePath;

    /// <summary>Details of a script that was just sealed after a sandbox violation.</summary>
    public sealed record SealedScript(string ScriptPath, string Reason, string Archive);

    /// <summary>
    /// Raised right after a violating script has been compressed and sealed. The UI subscribes to offer
    /// the user the choice of deleting the original script file as well. Handlers must not throw.
    /// </summary>
    public static event Action<SealedScript>? ScriptSealed;

    /// <summary>
    /// Shipped sample script (five inputs → average). New script nodes start with this file so the node
    /// works immediately; an empty string means the sample is missing from this installation.
    /// </summary>
    public static string SampleAveragePath
    {
        get
        {
            if (_sampleAveragePath is not null)
                return _sampleAveragePath;

            var root = string.IsNullOrWhiteSpace(_repositoryRoot)
                ? EngineOptions.Discover().RepositoryRoot
                : _repositoryRoot;
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "scripts", "node_scripts", "average5.py"),
                         Path.Combine(root, "node_scripts", "average5.py"),
                         Path.Combine(root, "average5.py"),
                     })
            {
                if (File.Exists(candidate))
                    return _sampleAveragePath = candidate;
            }

            return _sampleAveragePath = string.Empty;
        }
    }

    /// <summary>Overrides the interpreter and repository root; used by the app (loaded settings) and tests.</summary>
    public static void Configure(string pythonPath, string repositoryRoot)
    {
        lock (Sync)
        {
            _pythonPath = pythonPath ?? string.Empty;
            _repositoryRoot = repositoryRoot ?? string.Empty;
            _sampleAveragePath = null;
            Results.Clear();
            Scripts.Clear();
            StopWorker();
        }
    }

    /// <summary>Evaluates one script node for the given inputs.</summary>
    public static double Evaluate(string? scriptPath, IReadOnlyList<double> inputs)
    {
        if (inputs.Count != InputCount)
            throw new StageException(nameof(PythonNodeSandbox), "inputs",
                $"外部脚本节点需要 {InputCount} 个输入，实际 {inputs.Count} 个。");

        var values = new double[InputCount];
        for (var i = 0; i < InputCount; i++)
        {
            var value = inputs[i];
            if (!double.IsFinite(value) || Math.Abs(value) > InputMagnitudeLimit)
                throw new StageException(nameof(PythonNodeSandbox), "inputs",
                    $"外部脚本第 {i + 1} 个输入不是有限数值（{value}）。");
            values[i] = value;
        }

        var fullPath = ResolvePath(scriptPath);
        var script = LoadScript(fullPath);

        if (NodeScriptQuarantine.IsBlocked(script.Sha256, out var archive, out var blockedReason))
        {
            throw new StageException(nameof(PythonNodeSandbox), "quarantine",
                $"外部脚本已被封存隔离，拒绝执行：{fullPath}\n原因：{blockedReason}\n封存文件：{archive}");
        }

        var key = (script.Sha256, values[0], values[1], values[2], values[3], values[4]);
        lock (Sync)
        {
            if (Results.TryGetValue(key, out var cached))
                return cached;

            var value = Invoke(fullPath, script, values);
            if (Results.Count >= CacheLimit)
                Results.Clear();
            Results[key] = value;
            return value;
        }
    }

    /// <summary>Terminates the worker (the next evaluation starts a fresh one).</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            Results.Clear();
            StopWorker();
        }
    }

    // ------------------------------------------------------------------
    // Script loading
    // ------------------------------------------------------------------
    private static string ResolvePath(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            throw new StageException(nameof(PythonNodeSandbox), "path",
                "外部脚本节点没有指定脚本文件。");

        var path = scriptPath.Trim();
        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(_repositoryRoot))
            path = Path.Combine(_repositoryRoot, path);
        path = Path.GetFullPath(path);

        if (!string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase))
            throw new StageException(nameof(PythonNodeSandbox), "path",
                $"外部脚本必须是 .py 文件：{path}");
        if (!File.Exists(path))
            throw new StageException(nameof(PythonNodeSandbox), "path",
                $"找不到外部脚本：{path}");

        return path;
    }

    private readonly record struct ScriptSource(string Sha256, string Text, DateTime Modified, long Length);

    private static ScriptSource LoadScript(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > MaxScriptBytes)
            throw new StageException(nameof(PythonNodeSandbox), "path",
                $"外部脚本超过 {MaxScriptBytes / 1024} KB 上限：{fullPath}");

        if (Scripts.TryGetValue(fullPath, out var cached)
            && cached.Length == info.Length
            && cached.Modified == info.LastWriteTimeUtc)
        {
            return cached;
        }

        var bytes = File.ReadAllBytes(fullPath);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var script = new ScriptSource(sha, text, info.LastWriteTimeUtc, info.Length);
        Scripts[fullPath] = script;
        return script;
    }

    // ------------------------------------------------------------------
    // Worker process
    // ------------------------------------------------------------------
    private static double Invoke(string fullPath, ScriptSource script, double[] values)
    {
        var worker = EnsureWorker();

        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new { id, source = script.Text, values });
        if (Encoding.UTF8.GetByteCount(request) > MaxRequestBytes)
            throw new StageException(nameof(PythonNodeSandbox), "request", "外部脚本源码过大。");

        try
        {
            var input = worker.StandardInput;
            input.Write(request);
            input.Write('\n');
            input.Flush();
        }
        catch (Exception ex)
        {
            StopWorker();
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                "无法向外部脚本沙箱发送请求：" + ex.Message, ex);
        }

        string? line;
        try
        {
            var read = worker.StandardOutput.ReadLineAsync();
            if (!read.Wait(CallTimeoutMs))
                throw Violation(fullPath, script.Sha256, "执行超时（疑似死循环或阻塞调用）");
            line = read.Result;
        }
        catch (StageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StopWorker();
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                "读取外部脚本沙箱响应失败：" + ex.Message + Environment.NewLine + StderrSummary(), ex);
        }

        if (line is null)
        {
            var stderr = StderrSummary();
            StopWorker();
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                $"外部脚本沙箱进程已退出。{Environment.NewLine}{stderr}");
        }

        return ParseResponse(fullPath, script.Sha256, id, line);
    }

    private static double ParseResponse(string fullPath, string sha, string id, string line)
    {
        if (line.Length > MaxResponseChars)
            throw Violation(fullPath, sha, "响应过长");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw Violation(fullPath, sha, "响应不是合法 JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Violation(fullPath, sha, "响应不是对象");

            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                fields.Add(property.Name);

            if (!fields.Contains("id")
                || !root.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !string.Equals(idElement.GetString(), id, StringComparison.Ordinal))
            {
                throw Violation(fullPath, sha, "响应 id 不匹配");
            }

            if (fields.Contains("violation"))
            {
                var reason = root.TryGetProperty("violation", out var violation)
                    && violation.ValueKind == JsonValueKind.String
                    ? violation.GetString() ?? string.Empty
                    : string.Empty;
                throw Violation(fullPath, sha, "越权操作：" + SafeTrim(reason));
            }

            if (fields.Contains("error"))
            {
                var message = root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? string.Empty
                    : string.Empty;
                throw new StageException(nameof(PythonNodeSandbox), "script",
                    $"外部脚本执行失败：{SafeTrim(message)}{Environment.NewLine}脚本：{fullPath}");
            }

            if (fields.Count != 2
                || !fields.Contains("value")
                || !root.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Number
                || !valueElement.TryGetDouble(out var value)
                || !double.IsFinite(value)
                || Math.Abs(value) > InputMagnitudeLimit)
            {
                throw Violation(fullPath, sha, "返回值不符合协议（必须是单个有限数值）");
            }

            return value;
        }
    }

    private static Process EnsureWorker()
    {
        if (_worker is { HasExited: false })
            return _worker;

        StopWorker();

        var python = _pythonPath;
        if (string.IsNullOrWhiteSpace(python))
            python = EngineOptions.Discover().PythonPath;
        if (string.IsNullOrWhiteSpace(python))
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                "未配置 Python 解释器，无法运行外部脚本。");
        if (Path.IsPathRooted(python) && !File.Exists(python))
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                $"Python 解释器不存在：{python}");

        _workDirectory = Path.Combine(Path.GetTempPath(), "VocalB1Sandbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        while (StderrTail.TryDequeue(out _))
        {
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = _workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        // Only the bare minimum environment: nothing about the project, voicebank or user profile leaks in.
        psi.Environment.Clear();
        psi.Environment["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT") ?? @"C:\Windows";
        psi.Environment["TEMP"] = _workDirectory;
        psi.Environment["TMP"] = _workDirectory;

        // -I isolated, -S no site, -B no bytecode, -X utf8 deterministic text I/O.
        foreach (var argument in new[] { "-I", "-S", "-B", "-X", "utf8", "-c", WorkerSource })
            psi.ArgumentList.Add(argument);

        try
        {
            _worker = Process.Start(psi)
                ?? throw new StageException(nameof(PythonNodeSandbox), "worker", "无法启动外部脚本沙箱进程。");
        }
        catch (StageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StageException(nameof(PythonNodeSandbox), "worker",
                $"无法启动外部脚本沙箱：{python}{Environment.NewLine}{ex.Message}", ex);
        }

        var process = _worker;
        var reader = new Thread(() =>
        {
            try
            {
                while (process.StandardError.ReadLine() is { } line)
                {
                    StderrTail.Enqueue(SafeTrim(line));
                    while (StderrTail.Count > 50)
                        StderrTail.TryDequeue(out _);
                }
            }
            catch
            {
                // stderr is diagnostics only
            }
        })
        {
            IsBackground = true,
        };
        reader.Start();

        return _worker;
    }

    private static void StopWorker()
    {
        var process = _worker;
        _worker = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(_workDirectory) && Directory.Exists(_workDirectory))
                Directory.Delete(_workDirectory, true);
        }
        catch
        {
            // temp cleanup is best effort
        }
        _workDirectory = string.Empty;
    }

    /// <summary>Seals the offending script, terminates the worker and returns the failure to raise.</summary>
    private static StageException Violation(string fullPath, string sha, string reason)
    {
        var archive = NodeScriptQuarantine.Seal(fullPath, sha, reason, null);
        try
        {
            ScriptSealed?.Invoke(new SealedScript(fullPath, reason, archive));
        }
        catch
        {
            // A UI handler must never break the isolation path.
        }
        StopWorker();
        return new StageException(nameof(PythonNodeSandbox), "violation",
            $"外部脚本越权，已终止并压缩封存隔离：{fullPath}{Environment.NewLine}"
            + $"原因：{reason}{Environment.NewLine}"
            + $"封存文件：{(string.IsNullOrEmpty(archive) ? NodeScriptQuarantine.Root : archive)}");
    }

    private static string StderrSummary()
    {
        if (StderrTail.IsEmpty)
            return "（无 stderr 输出）";
        return "stderr：" + string.Join(" | ", StderrTail.ToArray());
    }

    private static string SafeTrim(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";

    /// <summary>
    /// Sandbox worker (CPython). Protocol: one JSON request per stdin line, exactly one JSON response per
    /// stdout line. The audit hook and the restricted builtin table are the only gate between user code and
    /// the operating system.
    /// </summary>
    private const string WorkerSource = """
import sys, json, math, ast

class SandboxViolation(Exception):
    pass

class ProtocolError(Exception):
    pass

DENIED_EVENTS = {
    "open", "import", "os", "socket", "subprocess", "ctypes", "shutil", "resource", "pty",
    "mmap", "signal", "syslog", "builtins.input", "builtins.eval", "builtins.exec",
    "builtins.compile", "builtins.breakpoint", "builtins.open", "cpython.run_file",
    "cpython.run_module", "cpython.run_command",
}

DENIED_PREFIXES = (
    "os.", "socket.", "subprocess.", "ctypes.", "shutil.", "importlib", "zipimport",
    "pickle", "marshal", "shelve", "dbm", "sqlite3", "tempfile.", "pathlib.", "glob.",
    "mmap.", "resource.", "pty.", "winreg", "msvcrt", "urllib.", "http.", "ftplib.",
    "smtplib.", "telnetlib.", "webbrowser.", "syslog.", "fcntl.", "fcntl.", "termios.",
    "tarfile.", "zipfile.", "gzip.", "bz2.", "lzma.", "sys.", "code.", "gc.",
)

def audit(event, args):
    if event in ("exec", "compile", "sys.addaudithook"):
        return
    if event in DENIED_EVENTS:
        raise SandboxViolation("blocked audit event: " + event)
    for prefix in DENIED_PREFIXES:
        if event.startswith(prefix):
            raise SandboxViolation("blocked audit event: " + event)

def deny(name):
    def blocked(*args, **kwargs):
        raise SandboxViolation("blocked builtin: " + name)
    return blocked

class DunderGuard(ast.NodeTransformer):
    # Object-graph traversal (.__class__.__base__.__subclasses__) is the classic sandbox escape.
    # Scripts are pure functions, so any dunder access is treated as an escalation attempt.
    def visit_Attribute(self, node):
        if node.attr.startswith("__") and node.attr.endswith("__"):
            raise SandboxViolation("blocked dunder attribute: " + node.attr)
        return self.generic_visit(node)

    def visit_Name(self, node):
        if node.id.startswith("__"):
            raise SandboxViolation("blocked dunder name: " + node.id)
        return self.generic_visit(node)

SAFE_BUILTINS = {
    "abs": abs, "all": all, "any": any, "bool": bool, "dict": dict, "divmod": divmod,
    "enumerate": enumerate, "filter": filter, "float": float, "int": int, "len": len,
    "list": list, "map": map, "max": max, "min": min, "pow": pow, "range": range,
    "reversed": reversed, "round": round, "set": set, "slice": slice, "sorted": sorted,
    "str": str, "sum": sum, "tuple": tuple, "zip": zip, "isinstance": isinstance,
    "issubclass": issubclass, "iter": iter, "next": next, "object": object,
    "__build_class__": __build_class__,
    "Exception": Exception, "ValueError": ValueError, "TypeError": TypeError,
    "ArithmeticError": ArithmeticError, "ZeroDivisionError": ZeroDivisionError,
    "OverflowError": OverflowError, "IndexError": IndexError, "KeyError": KeyError,
    "AttributeError": AttributeError, "RuntimeError": RuntimeError,
    "StopIteration": StopIteration,
}

for _name in ("__import__", "eval", "exec", "compile", "input", "open", "print", "globals",
              "locals", "vars", "dir", "getattr", "setattr", "delattr", "breakpoint", "help",
              "memoryview", "exit", "quit", "super", "type", "classmethod", "staticmethod",
              "property"):
    SAFE_BUILTINS[_name] = deny(_name)

def send(payload):
    sys.stdout.write(json.dumps(payload, ensure_ascii=True, separators=(",", ":")) + "\n")
    sys.stdout.flush()

def run_script(source, values):
    tree = compile(source, "<vocalb1-node>", "exec", ast.PyCF_ONLY_AST, dont_inherit=True)
    tree = DunderGuard().visit(tree)
    ast.fix_missing_locations(tree)
    code = compile(tree, "<vocalb1-node>", "exec", dont_inherit=True)
    namespace = {
        "__builtins__": dict(SAFE_BUILTINS),
        "__name__": "__vocalb1_node__",
        "math": math,
        "values": list(values),
    }
    exec(code, namespace)
    compute = namespace.get("compute")
    if compute is None or not callable(compute):
        raise ProtocolError("script must define compute(values)")
    result = compute(list(values))
    if isinstance(result, bool) or not isinstance(result, (int, float)):
        raise SandboxViolation("compute must return a single number")
    number = float(result)
    if not math.isfinite(number) or abs(number) > 1e12:
        raise SandboxViolation("compute returned a non-finite or out-of-range value")
    return number

def main():
    sys.addaudithook(audit)
    while True:
        line = sys.stdin.readline()
        if not line:
            return
        request_id = None
        try:
            request = json.loads(line)
            if not isinstance(request, dict) or set(request.keys()) != {"id", "source", "values"}:
                raise ProtocolError("malformed request")
            request_id = request["id"]
            if not isinstance(request_id, str):
                raise ProtocolError("malformed request id")
            source = request["source"]
            values = request["values"]
            if not isinstance(source, str) or len(source) > 262144:
                raise ProtocolError("source too large")
            if not isinstance(values, list) or len(values) != 5:
                raise ProtocolError("values must be five numbers")
            if not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in values):
                raise ProtocolError("values must be five numbers")
            numbers = [float(v) for v in values]
            if not all(math.isfinite(v) and abs(v) <= 1e12 for v in numbers):
                raise ProtocolError("values out of range")
            number = run_script(source, numbers)
            send({"id": request_id, "value": number})
        except SandboxViolation as exc:
            send({"id": request_id, "violation": str(exc)[:400]})
            sys.exit(70)
        except ProtocolError as exc:
            send({"id": request_id, "error": str(exc)[:400]})
        except Exception as exc:
            send({"id": request_id, "error": (type(exc).__name__ + ": " + str(exc))[:400]})

main()
""";
}
