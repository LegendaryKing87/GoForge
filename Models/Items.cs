using Avalonia.Media;

namespace GoForge.Models;

/// <summary>One line in the Output panel. File/Line/Column are set when the line looks like "file.go:12:3".</summary>
public sealed class OutputLine
{
    public string Text { get; init; } = "";
    public IBrush Brush { get; init; } = Brushes.Gray;
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class ProblemItem
{
    public string FilePath { get; init; } = "";
    public int Line { get; init; }      // 1-based
    public int Column { get; init; }    // 1-based
    public int Severity { get; init; }  // 1 error, 2 warning, 3 info, 4 hint
    public string Message { get; init; } = "";
    public string Source { get; init; } = "";

    public string Icon => Severity switch { 1 => "✖", 2 => "⚠", 3 => "ℹ", _ => "•" };

    public string Display =>
        $"{Icon}  {System.IO.Path.GetFileName(FilePath)}:{Line}:{Column}   {Message}" +
        (string.IsNullOrEmpty(Source) ? "" : $"   [{Source}]");
}

public sealed class ResultItem
{
    public string Display { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }   // 1-based
    public int Column { get; init; } // 1-based
}

public sealed class SymbolItem
{
    public string Display { get; init; } = "";
    public int Line { get; init; }   // 1-based
    public int Column { get; init; } // 1-based
}

// ---- LSP data (all positions are 0-based, as in the protocol) ----

public sealed record LspDiagnostic(int Line, int Col, int EndLine, int EndCol, int Severity, string Message, string? Source, string? Raw = null);

public sealed record LspLocation(string FilePath, int Line, int Col, int EndLine, int EndCol);

public sealed record LspTextEdit(int Line, int Col, int EndLine, int EndCol, string NewText);

public sealed record LspCompletion(string Label, int Kind, string? Detail, string InsertText, string? Doc);

public sealed record LspSymbol(string Name, int Kind, string? Container, int Line, int Col);

/// <summary>A code action or command offered by gopls (quick fix, organize imports, refactor...).</summary>
public sealed record LspCodeAction(
    string Title,
    string? Kind,
    Dictionary<string, List<LspTextEdit>>? Edit,
    string? Command,
    System.Text.Json.Nodes.JsonNode? CommandArgs,
    bool Preferred);

public sealed record LspSignature(string Label, string? Doc, int ActiveParam, List<(int Start, int End)> Params);

/// <summary>One frame of the call stack while debugging.</summary>
public sealed class StackFrameItem
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public string Display { get; init; } = "";
}
