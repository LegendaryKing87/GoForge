using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using GoForge.Models;

namespace GoForge.Editing;

public sealed class GoCompletionData : ICompletionData
{
    readonly string _insert;

    public GoCompletionData(LspCompletion item)
    {
        Text = item.Label;
        _insert = item.InsertText;
        var detail = string.IsNullOrWhiteSpace(item.Detail) ? "" : "    " + item.Detail;
        Content = $"{Glyph(item.Kind)}  {item.Label}{detail}";
        Description = string.IsNullOrWhiteSpace(item.Doc) ? item.Detail : item.Doc;
    }

    public IImage? Image => null;
    public string Text { get; }
    public object Content { get; }
    public object? Description { get; }
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, _insert);

    static string Glyph(int kind) => kind switch
    {
        2 or 3 or 4 => "ƒ",   // method, function, constructor
        5 => "◇",             // field
        6 => "x",             // variable
        7 or 22 => "S",       // class / struct
        8 => "I",             // interface
        9 => "M",             // module / package
        10 => "P",            // property
        14 => "k",            // keyword
        21 => "π",            // constant
        25 => "T",            // type parameter
        _ => "·"
    };
}
