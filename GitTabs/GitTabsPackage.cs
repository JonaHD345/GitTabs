using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using GitTabs.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace GitTabs
{
    /// <summary>
    /// GitTabs Visual Studio Package.
    /// Automatically saves and restores open document tabs per git branch.
    ///
    /// Uses AsyncPackage with background loading for zero impact on VS startup time.
    /// Activates only when a solution is loaded and a git repository is detected.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class GitTabsPackage : AsyncPackage
    {
        /// <summary>
        /// GitTabsPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "117e7613-69e7-427f-8e77-9e8403b56e34";

        private DTE2? _dte;
        private GitBranchWatcher? _branchWatcher;
        private TabManager? _tabManager;
        private TabStorageService? _storageService;
        private string _solutionName = string.Empty;
        private bool _isInitialized;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited.
        /// Background-loaded for zero VS startup performance impact.
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Initialize storage service on background thread (no UI needed)
            _storageService = new TabStorageService();

            // Switch to UI thread for DTE access
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Get DTE2 service
            _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (_dte == null)
                return;

            // Check if a solution is already loaded (race condition handling)
            if (!string.IsNullOrEmpty(_dte.Solution?.FullName))
            {
                await HandleSolutionOpenedAsync();
            }

            // Subscribe to solution events for future open/close
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterBackgroundSolutionLoadComplete += OnSolutionLoadComplete;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnBeforeCloseSolution += OnBeforeCloseSolution;
        }

        /// <summary>
        /// Handles the solution fully loaded event.
        /// </summary>
        private void OnSolutionLoadComplete(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await HandleSolutionOpenedAsync();
            });
        }

        /// <summary>
        /// Core logic when a solution is opened:
        /// - Determines solution name
        /// - Finds the git repository
        /// - Starts the branch watcher
        /// - Restores tabs for the current branch
        /// </summary>
        private async Task HandleSolutionOpenedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_dte == null || _storageService == null)
                return;

            // Avoid double-initialization
            if (_isInitialized)
                return;

            string? solutionPath = _dte.Solution?.FullName;
            if (string.IsNullOrEmpty(solutionPath))
                return;

            _solutionName = Path.GetFileNameWithoutExtension(solutionPath);
            string solutionDir = Path.GetDirectoryName(solutionPath)!;

            // Create TabManager
            _tabManager = new TabManager(_dte, _storageService, this);

            // Initialize branch watcher
            _branchWatcher = new GitBranchWatcher();
            bool hasGit = _branchWatcher.Start(solutionDir);

            if (!hasGit)
            {
                // No git repository found – extension stays dormant
                _branchWatcher.Dispose();
                _branchWatcher = null;
                return;
            }

            // Subscribe to branch changes
            _branchWatcher.BranchChanged += OnBranchChanged;

            _isInitialized = true;

            // Restore tabs for the current branch (if any were saved previously)
            string currentBranch = _branchWatcher.CurrentBranch;
            if (!string.IsNullOrEmpty(currentBranch))
            {
                await _tabManager.RestoreTabsAsync(_solutionName, currentBranch);
            }
        }

        /// <summary>
        /// Handles a git branch change:
        /// 1. Saves tabs for the old branch
        /// 2. Restores tabs for the new branch
        /// </summary>
        private void OnBranchChanged(string oldBranch, string newBranch)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_tabManager == null || string.IsNullOrEmpty(_solutionName))
                    return;

                // Save current tabs for the old branch
                if (!string.IsNullOrEmpty(oldBranch))
                {
                    await _tabManager.SaveCurrentTabsAsync(_solutionName, oldBranch);
                }

                // Restore tabs for the new branch
                if (!string.IsNullOrEmpty(newBranch))
                {
                    bool restored = await _tabManager.RestoreTabsAsync(_solutionName, newBranch);

                    // If no saved tabs exist for the new branch, just keep current tabs
                    // (they were already saved for the old branch)
                    if (!restored)
                    {
                        // New branch with no history – tabs stay as they are.
                        // This is intentional: the user might create a feature branch
                        // and wants to keep working on the same files.
                    }
                }
            });
        }

        /// <summary>
        /// Saves the current tab state before the solution is closed.
        /// </summary>
        private void OnBeforeCloseSolution(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_tabManager != null && _branchWatcher != null && !string.IsNullOrEmpty(_solutionName))
                {
                    string currentBranch = _branchWatcher.CurrentBranch;
                    if (!string.IsNullOrEmpty(currentBranch))
                    {
                        await _tabManager.SaveCurrentTabsAsync(_solutionName, currentBranch);
                    }
                }

                Cleanup();
            });
        }

        /// <summary>
        /// Cleans up resources when the solution is closed or the package is disposed.
        /// </summary>
        private void Cleanup()
        {
            _isInitialized = false;

            if (_branchWatcher != null)
            {
                _branchWatcher.BranchChanged -= OnBranchChanged;
                _branchWatcher.Dispose();
                _branchWatcher = null;
            }

            _tabManager = null;
            _solutionName = string.Empty;
        }

        /// <summary>
        /// Disposes the package and all held resources.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterBackgroundSolutionLoadComplete -= OnSolutionLoadComplete;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnBeforeCloseSolution -= OnBeforeCloseSolution;
                Cleanup();
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}
