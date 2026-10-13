using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GoForge.Models;

namespace GoForge.Services;

/// <summary>A small JSON-RPC / LSP client over stdio, tailored for gopls (full-text document sync).</summary>
public sealed class LspClient : IDisposable
{
    Process? _proc;
    Stream? _in;
    Stream? _out;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    int _nextId;

    public bool IsRunning { get; private set; }

    /// <summary>Raised from a background thread: (local file path, diagnostics).</summary>
    public event Action<string, List<LspDiagnostic>>? DiagnosticsPublished;
    /// <summary>Raised from a background thread with server messages (window/showMessage, stderr).</summary>
    public event Action<string>? Message;
    /// <summary>Work-done progress from gopls ("Loading packages…"); null when it finished.</summary>
    public event Action<string?>? Progress;
    public event Action? Exited;

    // ---------------------------------------------------------------- lifecycle

    public async Task<bool> StartAsync(string goplsPath, string rootPath)
    {
        try
        {
            var psi = new ProcessStartInfo(goplsPath)
            {
                WorkingDirectory = rootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            GoToolchain.ApplyPath(psi);
            _proc = Process.Start(psi);
            if (_proc == null) return false;

            _in = _proc.StandardInput.BaseStream;
            _out = new BufferedStream(_proc.StandardOutput.BaseStream, 64 * 1024);
            IsRunning = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await _proc.StandardError.ReadLineAsync()) != null)
                        if (line.Length > 0) Message?.Invoke("gopls: " + line);
                }
                catch { /* ignore */ }
            });
            _ = Task.Run(ReadLoop);

            var rootUri = PathUtil.ToUri(rootPath);
            var init = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["clientInfo"] = new JsonObject { ["name"] = "GoForge", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["didSave"] = true },
                        ["completion"] = new JsonObject
                        {
                            ["completionItem"] = new JsonObject
                            {
                                ["snippetSupport"] = false,
                                ["documentationFormat"] = new JsonArray("plaintext")
                            }
                        },
                        ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("plaintext", "markdown") },
                        ["definition"] = new JsonObject(),
                        ["references"] = new JsonObject(),
                        ["formatting"] = new JsonObject(),
                        ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = false },
                        ["publishDiagnostics"] = new JsonObject()
                    },
                    ["workspace"] = new JsonObject
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true
                    }
                },
                ["workspaceFolders"] = new JsonArray(new JsonObject
                {
                    ["uri"] = rootUri,
                    ["name"] = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                })
            };

            var res = await RequestAsync("initialize", init, 30000);
            if (res == null) { Dispose(); return false; }
            await NotifyAsync("initialized", new JsonObject());
            return true;
        }
        catch (Exception ex)
        {
            Message?.Invoke("gopls failed to start: " + ex.Message);
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch { /* ignore */ }
        try { _proc?.Dispose(); } catch { /* ignore */ }
        _proc = null;
        FailPending();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            if (IsRunning)
            {
                await RequestAsync("shutdown", null, 2000);
                await NotifyAsync("exit", null);
            }
        }
        catch { /* ignore */ }
        Dispose();
    }

    void FailPending()
    {
        foreach (var kv in _pending)
            kv.Value.TrySetResult(null);
        _pending.Clear();
    }

    // ---------------------------------------------------------------- transport

    async Task ReadLoop()
    {
        try
        {
            while (IsRunning && _out != null)
            {
                int length = await ReadHeaderLengthAsync();
                if (length < 0) break;

                var buf = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await _out.ReadAsync(buf.AsMemory(read, length - read));
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }

                JsonNode? node;
                try { node = JsonNode.Parse(Encoding.UTF8.GetString(buf)); }
                catch { continue; }
                if (node != null) HandleMessage(node);
            }
        }
        catch
        {
            // stream closed
        }
        finally
        {
            bool wasRunning = IsRunning;
            IsRunning = false;
            FailPending();
            if (wasRunning) Exited?.Invoke();
        }
    }

    async Task<int> ReadHeaderLengthAsync()
    {
        int contentLength = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await _out!.ReadAsync(one.AsMemory(0, 1));
            if (n <= 0) return -1;

            char c = (char)one[0];
            if (c == '\n')
            {
                var s = line.ToString().TrimEnd('\r');
                line.Clear();
                if (s.Length == 0)
                    return contentLength; // blank line ends headers
                if (s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(s.Substring(15).Trim(), out var len))
                    contentLength = len;
            }
            else line.Append(c);
        }
    }

    async Task SendAsync(JsonObject message)
    {
        if (_in == null) return;
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _in.WriteAsync(header);
            await _in.WriteAsync(body);
            await _in.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    Task NotifyAsync(string method, JsonNode? @params)
        => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    void Notify(string method, JsonNode? @params) => _ = NotifyAsync(method, @params);

    async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, int timeoutMs = 60000)
    {
        if (!IsRunning) return null;
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    void HandleMessage(JsonNode node)
    {
        if (node is not JsonObject obj) return;

        string? method = Str(obj["method"]);
        obj.TryGetPropertyValue("id", out var idNode);

        if (method != null && idNode != null)
        {
            // request from the server - answer so it never blocks waiting for us
            JsonNode? result = null;
            if (method == "workspace/configuration")
            {
                var arr = new JsonArray();
                int count = (obj["params"]?["items"] as JsonArray)?.Count ?? 0;
                for (int i = 0; i < count; i++) arr.Add(new JsonObject());
                result = arr;
            }
            _ = SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result });
            return;
        }

        if (method != null)
        {
            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    HandleDiagnostics(obj["params"] as JsonObject);
                    break;
                case "$/progress":
                    var pv = obj["params"]?["value"] as JsonObject;
                    var pk = Str(pv?["kind"]);
                    if (pk == "end") Progress?.Invoke(null);
                    else if (pk != null)
                    {
                        var ptxt = string.Join(" ", new[] { Str(pv?["title"]), Str(pv?["message"]) }
                            .Where(x => !string.IsNullOrEmpty(x)));
                        Progress?.Invoke(ptxt.Length > 0 ? ptxt : "working…");
                    }
                    break;
                case "window/showMessage":
                case "window/logMessage":
                    var text = Str(obj["params"]?["message"]);
                    if (!string.IsNullOrEmpty(text) && method == "window/showMessage") Message?.Invoke("gopls: " + text);
                    break;
            }
            return;
        }

        if (idNode != null && idNode is JsonValue v && v.TryGetValue<int>(out var id) && _pending.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(obj["error"] != null ? null : obj["result"]);
        }
    }

    void HandleDiagnostics(JsonObject? p)
    {
        if (p == null) return;
        var uri = Str(p["uri"]);
        if (uri == null) return;

        var list = new List<LspDiagnostic>();
        if (p["diagnostics"] is JsonArray arr)
        {
            foreach (var d in arr)
            {
                if (d is not JsonObject o || o["range"] is not JsonObject range) continue;
                var s = range["start"] as JsonObject;
                var e = range["end"] as JsonObject;
                if (s == null || e == null) continue;
                list.Add(new LspDiagnostic(
                    Int(s["line"]), Int(s["character"]), Int(e["line"]), Int(e["character"]),
                    Math.Max(1, Int(o["severity"])), Str(o["message"]) ?? "", Str(o["source"])));
            }
        }
        DiagnosticsPublished?.Invoke(PathUtil.FromUri(uri), list);
    }

    // ---------------------------------------------------------------- helpers

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static JsonObject Doc(string path) => new() { ["uri"] = PathUtil.ToUri(path) };

    static JsonObject DocPos(string path, int line, int col) => new()
    {
        ["textDocument"] = Doc(path),
        ["position"] = new JsonObject { ["line"] = line, ["character"] = col }
    };

    // ---------------------------------------------------------------- document sync

    public void DidOpen(string path, string text, int version) => Notify("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject
        {
            ["uri"] = PathUtil.ToUri(path),
            ["languageId"] = "go",
            ["version"] = version,
            ["text"] = text
        }
    });

    public void DidChange(string path, string text, int version) => Notify("textDocument/didChange", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = PathUtil.ToUri(path), ["version"] = version },
        ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text })
    });

    public void DidSave(string path) => Notify("textDocument/didSave", new JsonObject { ["textDocument"] = Doc(path) });

    public void DidClose(string path) => Notify("textDocument/didClose", new JsonObject { ["textDocument"] = Doc(path) });

    // ---------------------------------------------------------------- features

    public async Task<List<LspCompletion>> CompletionAsync(string path, int line, int col)
    {
        var res = await RequestAsync("textDocument/completion", DocPos(path, line, col));
        var items = res is JsonArray a ? a : res?["items"] as JsonArray;
        var list = new List<LspCompletion>();
        if (items == null) return list;

        foreach (var it in items)
        {
            if (it is not JsonObject o) continue;
            var label = Str(o["label"]);
            if (string.IsNullOrEmpty(label)) continue;
            var insert = Str(o["textEdit"]?["newText"]) ?? Str(o["insertText"]) ?? label;
            list.Add(new LspCompletion(label, Int(o["kind"]), Str(o["detail"]), insert, Markup(o["documentation"])));
            if (list.Count >= 400) break;
        }
        return list;
    }

    public async Task<string?> HoverAsync(string path, int line, int col)
    {
        var res = await RequestAsync("textDocument/hover", DocPos(path, line, col));
        return res == null ? null : Markup(res["contents"]);
    }

    public async Task<List<LspLocation>> DefinitionAsync(string path, int line, int col)
        => ParseLocations(await RequestAsync("textDocument/definition", DocPos(path, line, col)));

    public async Task<List<LspLocation>> ReferencesAsync(string path, int line, int col)
    {
        var p = DocPos(path, line, col);
        p["context"] = new JsonObject { ["includeDeclaration"] = true };
        return ParseLocations(await RequestAsync("textDocument/references", p, 30000));
    }

    /// <summary>Returns null on failure, an empty list when the file is already formatted.</summary>
    public async Task<List<LspTextEdit>?> FormattingAsync(string path, int tabSize)
    {
        var p = new JsonObject
        {
            ["textDocument"] = Doc(path),
            ["options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = false }
        };
        var res = await RequestAsync("textDocument/formatting", p);
        if (res == null) return null;
        var list = new List<LspTextEdit>();
        if (res is JsonArray arr)
        {
            foreach (var e in arr)
            {
                if (e is not JsonObject o || o["range"] is not JsonObject r) continue;
                var s = r["start"] as JsonObject;
                var en = r["end"] as JsonObject;
                if (s == null || en == null) continue;
                list.Add(new LspTextEdit(Int(s["line"]), Int(s["character"]), Int(en["line"]), Int(en["character"]), Str(o["newText"]) ?? ""));
            }
        }
        return list;
    }

    public async Task<List<LspSymbol>> DocumentSymbolsAsync(string path)
    {
        var res = await RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = Doc(path) });
        var list = new List<LspSymbol>();
        if (res is JsonArray arr) FlattenSymbols(arr, null, list);
        return list;
    }

    static void FlattenSymbols(JsonArray arr, string? container, List<LspSymbol> list)
    {
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var name = Str(o["name"]) ?? "?";
            int kind = Int(o["kind"]);

            if (o["location"] is JsonObject loc && loc["range"]?["start"] is JsonObject s1)
            {
                list.Add(new LspSymbol(name, kind, Str(o["containerName"]), Int(s1["line"]), Int(s1["character"])));
            }
            else
            {
                var start = (o["selectionRange"] ?? o["range"])?["start"] as JsonObject;
                if (start != null)
                    list.Add(new LspSymbol(name, kind, container, Int(start["line"]), Int(start["character"])));
                if (o["children"] is JsonArray ch) FlattenSymbols(ch, name, list);
            }
        }
    }

    static List<LspLocation> ParseLocations(JsonNode? res)
    {
        var list = new List<LspLocation>();
        if (res is JsonArray arr)
        {
            foreach (var n in arr)
            {
                var l = ParseLocation(n);
                if (l != null) list.Add(l);
            }
        }
        else
        {
            var l = ParseLocation(res);
            if (l != null) list.Add(l);
        }
        return list;
    }

    static LspLocation? ParseLocation(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var uri = Str(o["uri"]) ?? Str(o["targetUri"]);
        var range = (o["range"] ?? o["targetSelectionRange"]) as JsonObject;
        var s = range?["start"] as JsonObject;
        var e = range?["end"] as JsonObject;
        if (uri == null || s == null || e == null) return null;
        return new LspLocation(PathUtil.FromUri(uri), Int(s["line"]), Int(s["character"]), Int(e["line"]), Int(e["character"]));
    }

    /// <summary>Extracts readable text from MarkupContent / MarkedString / arrays of them.</summary>
    static string? Markup(JsonNode? n)
    {
        switch (n)
        {
            case null:
                return null;
            case JsonArray arr:
                var parts = arr.Select(Markup).Where(s => !string.IsNullOrWhiteSpace(s));
                var joined = string.Join("\n\n", parts);
                return joined.Length == 0 ? null : joined;
            case JsonObject o:
                return Str(o["value"]);
            default:
                return Str(n);
        }
    }
}
