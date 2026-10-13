using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace GoForge.Editing;

/// <summary>Builds an AvaloniaEdit highlighting definition for Go, with a dark and a light palette.</summary>
public static class GoHighlighting
{
    static IHighlightingDefinition? _dark;
    static IHighlightingDefinition? _light;

    static IHighlightingDefinition? _modDark, _modLight, _sumDark, _sumLight;

    public static IHighlightingDefinition Get(bool dark)
        => dark ? (_dark ??= Build(true)) : (_light ??= Build(false));

    /// <summary>go.mod and go.work files.</summary>
    public static IHighlightingDefinition GetMod(bool dark)
        => dark ? (_modDark ??= Load(Paint(ModTemplate, true))) : (_modLight ??= Load(Paint(ModTemplate, false)));

    /// <summary>go.sum files.</summary>
    public static IHighlightingDefinition GetSum(bool dark)
        => dark ? (_sumDark ??= Load(Paint(SumTemplate, true))) : (_sumLight ??= Load(Paint(SumTemplate, false)));

    const string ModTemplate = """
<SyntaxDefinition name="GoMod" extensions=".mod;.work" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="@COMMENT@" />
  <Color name="String" foreground="@STRING@" />
  <Color name="Directive" foreground="@KEYWORD@" />
  <Color name="Version" foreground="@NUMBER@" />
  <Color name="Path" foreground="@PATH@" />
  <Color name="Operator" foreground="@FUNCTION@" />
  <RuleSet>
    <Span color="Comment" begin="//" />
    <Span color="String" multiline="true" begin="`" end="`" />
    <Span color="String">
      <Begin>"</Begin>
      <End>"</End>
    </Span>
    <Rule color="Directive">^\s*(?:module|go|toolchain|require|replace|exclude|retract|godebug|use|ignore)(?=\s|\()</Rule>
    <Rule color="Operator">=&gt;</Rule>
    <Rule color="Version">\bv\d+\.\d+\.\d+[\w.+-]*</Rule>
    <Rule color="Version">(?&lt;=\s)\d+\.\d+(?:\.\d+)?(?:rc\d+|beta\d+)?\b</Rule>
    <Rule color="Path">[A-Za-z0-9_.~-]+(?:/[A-Za-z0-9_.~-]+)+</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

    const string SumTemplate = """
<SyntaxDefinition name="GoSum" extensions=".sum" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Path" foreground="@PATH@" />
  <Color name="Version" foreground="@NUMBER@" />
  <Color name="Hash" foreground="@STRING@" />
  <Color name="Directive" foreground="@KEYWORD@" />
  <RuleSet>
    <Rule color="Path">^\S+</Rule>
    <Rule color="Directive">/go\.mod\b</Rule>
    <Rule color="Version">\bv\d+\.\d+\.\d+[\w.+-]*</Rule>
    <Rule color="Hash">h1:[A-Za-z0-9+/=]+</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

    static string Paint(string template, bool dark) => template
        .Replace("@COMMENT@", dark ? "#6A9955" : "#008000")
        .Replace("@STRING@", dark ? "#CE9178" : "#A31515")
        .Replace("@KEYWORD@", dark ? "#C586C0" : "#AF00DB")
        .Replace("@FUNCTION@", dark ? "#DCDCAA" : "#795E26")
        .Replace("@NUMBER@", dark ? "#B5CEA8" : "#098658")
        .Replace("@PATH@", dark ? "#9CDCFE" : "#001080");

    static IHighlightingDefinition Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    const string Template = """
<SyntaxDefinition name="Go" extensions=".go" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="@COMMENT@" />
  <Color name="String" foreground="@STRING@" />
  <Color name="Char" foreground="@STRING@" />
  <Color name="Keyword" foreground="@KEYWORD@" />
  <Color name="Type" foreground="@TYPE@" />
  <Color name="Builtin" foreground="@BUILTIN@" />
  <Color name="Literal" foreground="@KEYWORD@" />
  <Color name="Function" foreground="@FUNCTION@" />
  <Color name="Number" foreground="@NUMBER@" />
  <RuleSet>
    <Span color="Comment" begin="//" />
    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
    <Span color="String" multiline="true" begin="`" end="`" />
    <Span color="String">
      <Begin>"</Begin>
      <End>"</End>
      <RuleSet>
        <Span begin="\\" end="." />
      </RuleSet>
    </Span>
    <Span color="Char">
      <Begin>'</Begin>
      <End>'</End>
      <RuleSet>
        <Span begin="\\" end="." />
      </RuleSet>
    </Span>
    <Keywords color="Keyword">
      @KEYWORDS@
    </Keywords>
    <Keywords color="Type">
      @TYPES@
    </Keywords>
    <Keywords color="Builtin">
      @BUILTINS@
    </Keywords>
    <Keywords color="Literal">
      <Word>true</Word>
      <Word>false</Word>
      <Word>nil</Word>
      <Word>iota</Word>
    </Keywords>
    <Rule color="Function">\b[A-Za-z_]\w*(?=\s*\()</Rule>
    <Rule color="Number">\b(0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(\.[\d_]*)?([eE][+-]?\d+)?i?)\b</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

    static readonly string[] Keywords =
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func",
        "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var"
    };

    static readonly string[] Types =
    {
        "any", "bool", "byte", "comparable", "complex64", "complex128", "error", "float32", "float64", "int",
        "int8", "int16", "int32", "int64", "rune", "string", "uint", "uint8", "uint16", "uint32", "uint64", "uintptr"
    };

    static readonly string[] Builtins =
    {
        "append", "cap", "clear", "close", "complex", "copy", "delete", "imag", "len", "make", "max", "min",
        "new", "panic", "print", "println", "real", "recover"
    };

    static string Words(string[] list) => string.Join("\n      ", list.Select(w => $"<Word>{w}</Word>"));

    static IHighlightingDefinition Build(bool dark)
    {
        var xml = Template
            .Replace("@COMMENT@", dark ? "#6A9955" : "#008000")
            .Replace("@STRING@", dark ? "#CE9178" : "#A31515")
            .Replace("@KEYWORD@", dark ? "#569CD6" : "#0000FF")
            .Replace("@TYPE@", dark ? "#4EC9B0" : "#267F99")
            .Replace("@BUILTIN@", dark ? "#4FC1FF" : "#0070C1")
            .Replace("@FUNCTION@", dark ? "#DCDCAA" : "#795E26")
            .Replace("@NUMBER@", dark ? "#B5CEA8" : "#098658")
            .Replace("@KEYWORDS@", Words(Keywords))
            .Replace("@TYPES@", Words(Types))
            .Replace("@BUILTINS@", Words(Builtins));

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
