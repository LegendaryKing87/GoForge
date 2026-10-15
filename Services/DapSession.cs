using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GoForge.Services;

/// <summary>
/// Runs "dlv dap" and speaks the Debug Adapter Protocol to it over TCP.
/// All callbacks are raised on background threads.
/// </summary>
public sealed class DapSession : IDisposable
{
    Process? _dlv;
    TcpClient? _tcp;
    Stream? _stream;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject?>> _pending = new();
    int _seq;
    int _closed;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Delve's own console output (not the debuggee's).</summary>
    public event Action<string>? Log;
    /// <summary>A DAP event: name and body.</summary>
    public event Action<string, JsonNode?>? EventReceived;
    public event Action? Closed;

    static readonly Regex ListenRx = new(@"listening at:\s*(?<h>\[[^\]]+\]|[^\s:]+):(?<p>\d+)", RegexOptions.Compiled);

    public async Task<bool> StartAsync(string dlvPath, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo(dlvPath)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("dap");
            psi.ArgumentList.Add("--listen=127.0.0.1:0");
            GoToolchain.ApplyPath(psi);

            _dlv = Process.Start(psi);
            if (_dlv == null) { LastError = "could not start dlv"; return false; }

            var ready = new TaskCompletionSource<(string Host, int Port)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnLine(string? line)
            {
                if (line == null) return;
                var m = ListenRx.Match(line);
                if (m.Success && !ready.Task.IsCompleted)
                    ready.TrySetResult((m.Groups["h"].Value, int.Parse(m.Groups["p"].Value)));
                else if (line.Length > 0)
                    Log?.Invoke(line);
            }

            _dlv.OutputDataReceived += (_, e) => OnLine(e.Data);
            _dlv.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _dlv.EnableRaisingEvents = true;
            _dlv.Exited += (_, _) =>
            {
                ready.TrySetCanceled();
                Shutdown();
            };
            _dlv.BeginOutputReadLine();
            _dlv.BeginErrorReadLine();

            var done = await Task.WhenAny(ready.Task, Task.Delay(20000));
            if (done != ready.Task || ready.Task.IsCanceled)
            {
                LastError = "dlv did not start listening (is it installed correctly?)";
                Dispose();
                return false;
            }

            var (host, port) = ready.Task.Result;
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host.Trim('[', ']'), port);
            _stream = new BufferedStream(_tcp.GetStream(), 64 * 1024);
            IsRunning = true;
            _ = Task.Run(ReadLoop);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Dispose();
            return false;
        }
    }

    // ---------------------------------------------------------------- transport

    async Task ReadLoop()
    {
        try
        {
            while (IsRunning && _stream != null)
            {
                int length = await ReadHeaderLengthAsync();
                if (length < 0) break;

                var buf = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await _stream.ReadAsync(buf.AsMemory(read, length - read));
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }

                JsonNode? node;
                try { node = JsonNode.Parse(Encoding.UTF8.GetString(buf)); }
                catch { continue; }
                if (node is JsonObject obj) HandleMessage(obj);
            }
        }
        catch
        {
            // connection closed
        }
        finally
        {
            Shutdown();
        }
    }

    async Task<int> ReadHeaderLengthAsync()
    {
        int contentLength = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await _stream!.ReadAsync(one.AsMemory(0, 1));
            if (n <= 0) return -1;
            char c = (char)one[0];
            if (c == '\n')
            {
                var s = line.ToString().TrimEnd('\r');
                line.Clear();
                if (s.Length == 0) return contentLength;
                if (s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(s.Substring(15).Trim(), out var len))
                    contentLength = len;
            }
            else line.Append(c);
        }
    }

    async Task SendAsync(JsonObject message)
    {
        if (_stream == null) return;
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(body);
            await _stream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    void HandleMessage(JsonObject obj)
    {
        var type = Str(obj["type"]);
        switch (type)
        {
            case "response":
                if (_pending.TryGetValue(Int(obj["request_seq"]), out var tcs))
                    tcs.TrySetResult(obj);
                break;

            case "event":
                var name = Str(obj["event"]);
                if (name != null) EventReceived?.Invoke(name, obj["body"]);
                break;

            case "request":
                // reverse requests (runInTerminal...) are not supported
                _ = SendAsync(new JsonObject
                {
                    ["seq"] = Interlocked.Increment(ref _seq),
                    ["type"] = "response",
                    ["request_seq"] = Int(obj["seq"]),
                    ["success"] = false,
                    ["command"] = Str(obj["command"]) ?? "",
                    ["message"] = "unsupported"
                });
                break;
        }
    }

    /// <summary>
    /// Sends a request. Returns the response body (an empty object when there is none),
    /// or null on failure - see <see cref="LastError"/>.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(string command, JsonObject? args, int timeoutMs = 20000)
    {
        if (!IsRunning) return null;
        int id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject
            {
                ["seq"] = id,
                ["type"] = "request",
                ["command"] = command,
                ["arguments"] = args ?? new JsonObject()
            });

            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(null));
            var resp = await tcs.Task;
            if (resp == null)
            {
                LastError = $"{command}: no response";
                return null;
            }

            bool ok = resp["success"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            if (!ok)
            {
                LastError = Str(resp["message"]) ?? $"{command} failed";
                return null;
            }
            LastError = null;
            return resp["body"] ?? new JsonObject();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // ---------------------------------------------------------------- shutdown

    void Shutdown()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        IsRunning = false;
        foreach (var kv in _pending) kv.Value.TrySetResult(null);
        _pending.Clear();
        try { _tcp?.Close(); } catch { /* ignore */ }
        try { if (_dlv is { HasExited: false }) _dlv.Kill(true); } catch { /* ignore */ }
        Closed?.Invoke();
    }

    public void Dispose() => Shutdown();
}
