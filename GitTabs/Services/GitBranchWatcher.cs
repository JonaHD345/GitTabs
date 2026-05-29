using System;
using System.IO;
using System.Timers;

namespace GitTabs.Services
{
    /// <summary>
    /// Watches the .git/HEAD file for branch changes using a FileSystemWatcher.
    /// Fires the <see cref="BranchChanged"/> event when a branch switch is detected,
    /// with built-in debouncing to avoid duplicate events from git operations.
    /// </summary>
    public sealed class GitBranchWatcher : IDisposable
    {
        private const double DebounceIntervalMs = 400;
        private const string HeadPrefix = "ref: refs/heads/";

        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private string _currentBranch = string.Empty;
        private string _gitDir = string.Empty;
        private bool _disposed;

        /// <summary>
        /// Raised when the active git branch changes.
        /// </summary>
        public event Action<string, string>? BranchChanged;

        /// <summary>
        /// Gets the name of the currently active git branch.
        /// </summary>
        public string CurrentBranch => _currentBranch;

        /// <summary>
        /// Starts watching the git repository located at the given solution directory.
        /// </summary>
        /// <param name="solutionDirectory">The directory containing the solution file.</param>
        /// <returns>True if a git repository was found and watching started; false otherwise.</returns>
        public bool Start(string solutionDirectory)
        {
            if (string.IsNullOrEmpty(solutionDirectory))
                return false;

            _gitDir = FindGitDir(solutionDirectory);
            if (string.IsNullOrEmpty(_gitDir))
                return false;

            _currentBranch = ReadBranchName(_gitDir);

            // Set up debounce timer
            _debounceTimer = new Timer(DebounceIntervalMs)
            {
                AutoReset = false
            };
            _debounceTimer.Elapsed += OnDebounceElapsed;

            // Watch .git/HEAD for changes
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_gitDir + Path.DirectorySeparatorChar)!)
            {
                Filter = "HEAD",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnHeadFileChanged;

            return true;
        }

        /// <summary>
        /// Stops watching for branch changes and releases resources.
        /// </summary>
        public void Stop()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnHeadFileChanged;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Elapsed -= OnDebounceElapsed;
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
        }

        /// <summary>
        /// Handles the FileSystemWatcher Changed event by resetting the debounce timer.
        /// </summary>
        private void OnHeadFileChanged(object sender, FileSystemEventArgs e)
        {
            // Reset debounce timer on each event
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        /// <summary>
        /// Called after the debounce interval. Reads the new branch name and fires
        /// <see cref="BranchChanged"/> if the branch actually changed.
        /// </summary>
        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                string newBranch = ReadBranchName(_gitDir);
                if (string.IsNullOrEmpty(newBranch))
                    return;

                string oldBranch = _currentBranch;
                if (string.Equals(oldBranch, newBranch, StringComparison.Ordinal))
                    return;

                _currentBranch = newBranch;
                BranchChanged?.Invoke(oldBranch, newBranch);
            }
            catch (Exception)
            {
                // Swallow exceptions from file read race conditions with git.
            }
        }

        /// <summary>
        /// Reads the current branch name from .git/HEAD.
        /// Returns the branch name, or a shortened commit hash for detached HEAD state.
        /// </summary>
        public static string ReadBranchName(string gitDir)
        {
            try
            {
                string headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath))
                    return string.Empty;

                string content = File.ReadAllText(headPath).Trim();

                // Normal branch: "ref: refs/heads/main"
                if (content.StartsWith(HeadPrefix, StringComparison.Ordinal))
                    return content.Substring(HeadPrefix.Length);

                // Detached HEAD: raw commit hash – return first 8 chars
                if (content.Length >= 8)
                    return "detached-" + content.Substring(0, 8);

                return content;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Finds the .git directory by walking up from the given directory.
        /// Supports both standard repositories and git worktrees (where .git is a file
        /// containing "gitdir: /path/to/actual/.git").
        /// </summary>
        public static string FindGitDir(string startDir)
        {
            string? dir = startDir;

            while (!string.IsNullOrEmpty(dir))
            {
                string gitPath = Path.Combine(dir, ".git");

                if (Directory.Exists(gitPath))
                {
                    // Standard git repository
                    return gitPath;
                }

                if (File.Exists(gitPath))
                {
                    // Git worktree: .git is a file with "gitdir: <path>"
                    try
                    {
                        string content = File.ReadAllText(gitPath).Trim();
                        if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                        {
                            string worktreeGitDir = content.Substring("gitdir:".Length).Trim();
                            if (!Path.IsPathRooted(worktreeGitDir))
                                worktreeGitDir = Path.GetFullPath(Path.Combine(dir, worktreeGitDir));

                            if (Directory.Exists(worktreeGitDir))
                                return worktreeGitDir;
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore malformed .git file
                    }
                }

                dir = Directory.GetParent(dir)?.FullName;
            }

            return string.Empty;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}
