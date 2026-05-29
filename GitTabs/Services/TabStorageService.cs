using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GitTabs.Models;
using Newtonsoft.Json;

namespace GitTabs.Services
{
    /// <summary>
    /// Persists tab state per solution and branch as JSON files in %LocalAppData%\GitTabs\.
    /// </summary>
    public sealed class TabStorageService
    {
        private static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitTabs");

        /// <summary>
        /// Saves the tab state for a specific branch of a solution.
        /// </summary>
        /// <param name="solutionName">The solution name used as folder key.</param>
        /// <param name="data">The branch tab data to persist.</param>
        public async Task SaveAsync(string solutionName, BranchTabData data)
        {
            if (string.IsNullOrEmpty(solutionName) || data == null)
                return;

            string filePath = GetFilePath(solutionName, data.BranchName);
            string directory = Path.GetDirectoryName(filePath);

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Loads the saved tab state for a specific branch of a solution.
        /// Returns null if no saved state exists.
        /// </summary>
        /// <param name="solutionName">The solution name used as folder key.</param>
        /// <param name="branchName">The branch name to load tabs for.</param>
        public async Task<BranchTabData?> LoadAsync(string solutionName, string branchName)
        {
            if (string.IsNullOrEmpty(solutionName) || string.IsNullOrEmpty(branchName))
                return null;

            string filePath = GetFilePath(solutionName, branchName);

            if (!File.Exists(filePath))
                return null;

            try
            {
                byte[] bytes;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 4096, useAsync: true))
                {
                    bytes = new byte[stream.Length];
                    await stream.ReadAsync(bytes, 0, bytes.Length);
                }

                string json = Encoding.UTF8.GetString(bytes);
                return JsonConvert.DeserializeObject<BranchTabData>(json);
            }
            catch (Exception)
            {
                // If the file is corrupted or unreadable, return null gracefully.
                return null;
            }
        }

        /// <summary>
        /// Builds the file path for a branch's tab state JSON file.
        /// </summary>
        private static string GetFilePath(string solutionName, string branchName)
        {
            string safeSolutionName = SanitizeFileName(solutionName);
            string safeBranchName = SanitizeFileName(branchName);
            return Path.Combine(BaseDirectory, safeSolutionName, safeBranchName + ".json");
        }

        /// <summary>
        /// Sanitizes a string for use as a file or directory name by replacing invalid characters.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            return sb.ToString();
        }
    }
}
