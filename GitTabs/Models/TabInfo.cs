using System;
using System.Collections.Generic;

namespace GitTabs.Models
{
    /// <summary>
    /// Represents a single open document tab.
    /// </summary>
    public sealed class TabInfo
    {
        /// <summary>
        /// Full file path of the open document.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Whether this tab is the currently active/focused document.
        /// </summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Represents the saved tab state for a specific git branch.
    /// </summary>
    public sealed class BranchTabData
    {
        /// <summary>
        /// Name of the git branch.
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp when the tabs were last saved.
        /// </summary>
        public DateTime SavedAt { get; set; }

        /// <summary>
        /// List of open tabs for this branch.
        /// </summary>
        public List<TabInfo> Tabs { get; set; } = new List<TabInfo>();
    }
}
