using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using GitTabs.Models;
using Microsoft.VisualStudio.Shell;

namespace GitTabs.Services
{
    /// <summary>
    /// Manages reading, saving, and restoring open document tabs in Visual Studio
    /// using the DTE2 automation model.
    /// </summary>
    public sealed class TabManager
    {
        private readonly DTE2 _dte;
        private readonly TabStorageService _storage;
        private readonly Microsoft.VisualStudio.Shell.IAsyncServiceProvider _serviceProvider;

        /// <summary>
        /// Creates a new TabManager instance.
        /// </summary>
        /// <param name="dte">The DTE2 automation object.</param>
        /// <param name="storage">The storage service for persisting tab state.</param>
        /// <param name="serviceProvider">The async service provider for resolving VS services.</param>
        public TabManager(DTE2 dte, TabStorageService storage, Microsoft.VisualStudio.Shell.IAsyncServiceProvider serviceProvider)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        /// <summary>
        /// Gets the list of currently open document tabs.
        /// Must be called on the UI thread.
        /// </summary>
        public List<TabInfo> GetOpenTabs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var tabs = new List<TabInfo>();
            string? activeDocPath = null;

            try
            {
                activeDocPath = _dte.ActiveDocument?.FullName;
            }
            catch (Exception)
            {
                // No active document
            }

            try
            {
                foreach (Document doc in _dte.Documents)
                {
                    try
                    {
                        string fullName = doc.FullName;
                        if (string.IsNullOrEmpty(fullName))
                            continue;

                        tabs.Add(new TabInfo
                        {
                            FilePath = fullName,
                            IsActive = string.Equals(fullName, activeDocPath, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    catch (Exception)
                    {
                        // Skip documents that can't be accessed (e.g., designer documents)
                    }
                }
            }
            catch (Exception)
            {
                // Documents collection might not be available
            }

            return tabs;
        }

        /// <summary>
        /// Saves the currently open tabs for a given branch.
        /// Must be called on the UI thread.
        /// </summary>
        /// <param name="solutionName">The name of the current solution.</param>
        /// <param name="branchName">The branch to save tabs for.</param>
        public async Task SaveCurrentTabsAsync(string solutionName, string branchName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var tabs = GetOpenTabs();

            // Don't save empty tab sets – this avoids overwriting saved state
            // when VS is in a transitional state
            if (tabs.Count == 0)
                return;

            var data = new BranchTabData
            {
                BranchName = branchName,
                SavedAt = DateTime.UtcNow,
                Tabs = tabs
            };

            await _storage.SaveAsync(solutionName, data);
        }

        /// <summary>
        /// Restores the saved tabs for a given branch.
        /// Closes all currently open tabs first, then opens the saved ones.
        /// Must be called on the UI thread.
        /// </summary>
        /// <param name="solutionName">The name of the current solution.</param>
        /// <param name="branchName">The branch to restore tabs for.</param>
        /// <returns>True if tabs were restored; false if no saved state existed.</returns>
        public async Task<bool> RestoreTabsAsync(string solutionName, string branchName)
        {
            var data = await _storage.LoadAsync(solutionName, branchName);
            if (data == null || data.Tabs.Count == 0)
                return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            CloseAllDocuments();

            string? activeFilePath = null;

            foreach (var tab in data.Tabs)
            {
                if (string.IsNullOrEmpty(tab.FilePath))
                    continue;

                if (!File.Exists(tab.FilePath))
                {
                    WriteToOutputWindow($"GitTabs: File not found, skipping: {tab.FilePath}");
                    continue;
                }

                try
                {
                    _dte.ItemOperations.OpenFile(tab.FilePath);

                    if (tab.IsActive)
                        activeFilePath = tab.FilePath;
                }
                catch (Exception ex)
                {
                    WriteToOutputWindow($"GitTabs: Failed to open {tab.FilePath}: {ex.Message}");
                }
            }

            // Activate the previously active document
            if (!string.IsNullOrEmpty(activeFilePath))
            {
                try
                {
                    foreach (Document doc in _dte.Documents)
                    {
                        if (string.Equals(doc.FullName, activeFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            doc.Activate();
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Non-critical – the tab is open, just not focused
                }
            }

            return true;
        }

        /// <summary>
        /// Closes all open document windows.
        /// Must be called on the UI thread.
        /// </summary>
        private void CloseAllDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _dte.ExecuteCommand("Window.CloseAllDocuments");
            }
            catch (Exception)
            {
                // Command might not be available in some VS states
                try
                {
                    // Fallback: close documents individually
                    foreach (Document doc in _dte.Documents)
                    {
                        try
                        {
                            doc.Close(vsSaveChanges.vsSaveChangesPrompt);
                        }
                        catch (Exception)
                        {
                            // Skip documents that refuse to close
                        }
                    }
                }
                catch (Exception)
                {
                    // Documents collection not available
                }
            }
        }

        /// <summary>
        /// Writes a message to the VS Output Window (GitTabs pane).
        /// </summary>
        private void WriteToOutputWindow(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var outputWindow = _dte.ToolWindows.OutputWindow;
                OutputWindowPane? pane = null;

                // Try to find existing GitTabs pane
                foreach (OutputWindowPane existingPane in outputWindow.OutputWindowPanes)
                {
                    if (existingPane.Name == "GitTabs")
                    {
                        pane = existingPane;
                        break;
                    }
                }

                // Create pane if it doesn't exist
                pane = pane ?? outputWindow.OutputWindowPanes.Add("GitTabs");
                pane.OutputString(message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Output window not available – silently ignore
            }
        }
    }
}
