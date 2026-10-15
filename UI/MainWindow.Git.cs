using Avalonia.Media;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    GitStatus? _git;
    bool _gitBusy;
    readonly Avalonia.Controls.TextBlock _stGit = new() { Text = "" };

    /// <summary>Re-reads "git status" and refreshes the status bar and the Explorer colours when something changed.</summary>
    async Task RefreshGitAsync()
    {
        if (_gitBusy) return;
        _gitBusy = true;
        try
        {
            var status = _root != null ? await GitService.GetStatusAsync(_root) : null;
            var oldSignature = _git?.Signature;
            _git = status;

            if (status == null)
            {
                _stGit.Text = "";
            }
            else
            {
                var sync = (status.Ahead > 0 ? $" ↑{status.Ahead}" : "") + (status.Behind > 0 ? $" ↓{status.Behind}" : "");
                int changes = status.Files.Count + status.UntrackedDirs.Count;
                _stGit.Text = $"⎇ {status.Branch}{sync}" + (changes > 0 ? $"  ●{changes}" : "");
            }

            if (oldSignature != status?.Signature) RefreshTree();
        }
        catch
        {
            // git is optional; never bother the user
        }
        finally
        {
            _gitBusy = false;
        }
    }

    IBrush? GitFileBrush(string path)
    {
        var state = _git?.StateOf(path);
        if (state == null) return null;
        bool dark = _s.IsDark;
        return state switch
        {
            'M' => Br(dark ? "#E2C08D" : "#895503"),
            'A' or '?' => Br(dark ? "#73C991" : "#2E7D32"),
            'R' => Br(dark ? "#73C991" : "#2E7D32"),
            'D' => Br(dark ? "#C74E39" : "#AD0707"),
            'U' => Br(dark ? "#E4676B" : "#AD0707"),
            _ => null
        };
    }

    IBrush? GitDirBrush(string path)
    {
        if (_git == null) return null;
        bool dark = _s.IsDark;
        if (_git.UntrackedDirs.Contains(path)) return Br(dark ? "#73C991" : "#2E7D32");
        if (_git.DirtyDirs.Contains(path)) return Br(dark ? "#B89F70" : "#895503");
        return null;
    }
}
