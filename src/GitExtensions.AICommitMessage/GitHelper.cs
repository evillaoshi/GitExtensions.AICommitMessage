using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitExtensions.AICommitMessage
{
    /// <summary>One entry of <c>git status --porcelain=v1 -z</c>.</summary>
    internal sealed class StatusEntry
    {
        public StatusEntry(string path, string status)
        {
            Path = path;
            Status = status;
        }

        /// <summary>Repository-relative path (the new path for a rename).</summary>
        public string Path { get; }

        /// <summary>The two porcelain status characters: index column + work tree column.</summary>
        public string Status { get; }

        public bool IsUntracked => Status == "??";

        /// <summary>True when the file is gone from the work tree, so staging must remove it.</summary>
        public bool IsDeletedInWorkTree => !IsUntracked && Status.Length > 1 && Status[1] == 'D';

        /// <summary>True when the work tree differs from the index, so the change is not staged yet.</summary>
        public bool HasUnstagedChanges => IsUntracked || (Status.Length > 1 && Status[1] != ' ');

        /// <summary>True when the index differs from HEAD, so something is staged for this path.</summary>
        public bool HasStagedChanges => Status.Length > 0 && Status[0] != ' ' && Status[0] != '?';

        /// <summary>Short status label, e.g. "M", "A", "R", "untracked".</summary>
        public string Describe() => IsUntracked ? "untracked" : Status.Trim();
    }

    /// <summary>The text sent to the model plus the real paths it is allowed to choose from.</summary>
    internal sealed class ChangeSet
    {
        public ChangeSet(string summary, IReadOnlyList<StatusEntry> unstagedEntries, IReadOnlyList<string> stagedPaths)
        {
            Summary = summary;
            UnstagedEntries = unstagedEntries;
            UnstagedPaths = unstagedEntries.Select(entry => entry.Path).ToList();
            StagedPaths = stagedPaths;
        }

        /// <summary>Size-bounded, human-readable description of every change.</summary>
        public string Summary { get; }

        /// <summary>Full status of every candidate path, needed to stage deletions correctly.</summary>
        public IReadOnlyList<StatusEntry> UnstagedEntries { get; }

        /// <summary>Paths whose work tree version differs from the index (the candidate set).</summary>
        public IReadOnlyList<string> UnstagedPaths { get; }

        /// <summary>Paths already staged, listed for the model but excluded from the candidate set.</summary>
        public IReadOnlyList<string> StagedPaths { get; }
    }

    internal static class GitHelper
    {
        private const int GitTimeoutMs = 30000;
        private const int MaxExcerptFiles = 25;
        private const int MaxExcerptLines = 40;
        private const int MaxScannedFileLines = 500;
        private const string TruncatedMarker = "\n\n[summary truncated to fit the configured limit]";

        /// <summary>
        /// Returns the staged diff (<c>git diff --cached</c>) for <paramref name="workingDir"/>,
        /// or an empty string if nothing is staged. Only staged content is read, so files excluded
        /// by .gitignore are never included.
        /// </summary>
        public static string GetStagedDiff(string workingDir)
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "--no-pager diff --cached --no-color",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Could not start 'git'. Ensure Git is installed and on PATH.");
            }

            // Read both pipes asynchronously: a synchronous read would block past the timeout, and the
            // exit code cannot be read at all once a timed-out process is still running.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(GitTimeoutMs))
            {
                KillProcess(process);
                throw new InvalidOperationException(
                    $"'git diff --cached' timed out after {GitTimeoutMs / 1000} seconds.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 && string.IsNullOrEmpty(output))
            {
                throw new InvalidOperationException("'git diff --cached' failed: " + error);
            }

            return output;
        }

        /// <summary>
        /// Returns every changed path with its porcelain status. Untracked files are included, so the
        /// caller can offer them for staging; ignored files are never listed.
        /// </summary>
        public static IReadOnlyList<StatusEntry> GetChangedFiles(string workingDir)
        {
            // -uall expands untracked directories into their files: update-index --stdin does not
            // recurse into a directory path, so candidates must be real files.
            string output = RunGit(workingDir, "status", "--porcelain=v1", "-z", "-uall");
            List<string> fields = SplitNul(output);
            List<StatusEntry> entries = new();

            for (int i = 0; i < fields.Count; i++)
            {
                string field = fields[i];
                if (field.Length < 4)
                {
                    continue;
                }

                string status = field.Substring(0, 2);
                entries.Add(new StatusEntry(field.Substring(3), status));

                // For renames and copies git prints "<status> <new path>\0<original path>\0", so the
                // original path arrives as its own field and must be skipped.
                if (IsRenameOrCopy(status) && i + 1 < fields.Count)
                {
                    i++;
                }
            }

            return entries;
        }

        /// <summary>
        /// Builds the summary of the current changes (staged + unstaged + untracked, with a short diff
        /// excerpt per unstaged file) that lets the model pick one related group. The text never exceeds
        /// <paramref name="maxBytes"/> UTF-8 bytes; 0 means no limit.
        /// </summary>
        public static ChangeSet GetChangeSet(string workingDir, int maxBytes)
        {
            IReadOnlyList<StatusEntry> entries = GetChangedFiles(workingDir);
            List<StatusEntry> staged = entries.Where(entry => entry.HasStagedChanges).ToList();
            List<StatusEntry> unstaged = entries.Where(entry => entry.HasUnstagedChanges).ToList();

            Dictionary<string, string> unstagedStats = ParseNumstat(RunGit(workingDir, "diff", "--numstat", "--no-color"));
            Dictionary<string, string> stagedStats = ParseNumstat(RunGit(workingDir, "diff", "--cached", "--numstat", "--no-color"));

            StringBuilder summary = new();
            summary.AppendLine("STAGED (already in the index - never pick these):");
            if (staged.Count == 0)
            {
                summary.AppendLine("- (none)");
            }
            else
            {
                foreach (StatusEntry entry in staged)
                {
                    summary.AppendLine($"- {entry.Path} [{entry.Describe()}]{Stats(stagedStats, entry.Path)}");
                }
            }

            summary.AppendLine();
            summary.AppendLine("UNSTAGED (pick one related group from here only):");
            if (unstaged.Count == 0)
            {
                summary.AppendLine("- (none)");
            }
            else
            {
                int excerptCount = 0;
                foreach (StatusEntry entry in unstaged)
                {
                    summary.AppendLine($"- {entry.Path} [{entry.Describe()}]{Stats(unstagedStats, entry.Path)}");

                    // Excerpts make grouping much better, but a huge change list must not blow up the
                    // prompt, so only the first files get one.
                    if (excerptCount < MaxExcerptFiles)
                    {
                        string excerpt = GetExcerpt(workingDir, entry);
                        if (excerpt.Length > 0)
                        {
                            summary.AppendLine("  --- excerpt ---");
                            summary.AppendLine(Indent(excerpt));
                        }

                        excerptCount++;
                    }
                }
            }

            string text = summary.ToString();
            if (maxBytes > 0 && Encoding.UTF8.GetByteCount(text) > maxBytes)
            {
                // The note is part of the budget: reserve its bytes so the result never exceeds maxBytes.
                int markerBytes = Encoding.UTF8.GetByteCount(TruncatedMarker);
                text = markerBytes < maxBytes
                    ? TruncateToUtf8Bytes(text, maxBytes - markerBytes) + TruncatedMarker
                    : TruncateToUtf8Bytes(text, maxBytes);
            }

            return new ChangeSet(
                text,
                unstaged,
                staged.Select(entry => entry.Path).ToList());
        }

        /// <summary>
        /// Cuts the text so the UTF-8 byte count fits the budget, without splitting a surrogate pair.
        /// The cut is the longest prefix that still fits, so one more character would exceed the budget.
        /// </summary>
        public static string TruncateToUtf8Bytes(string text, int maxBytes)
        {
            if (maxBytes <= 0 || Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = low + ((high - low + 1) / 2);
                if (Encoding.UTF8.GetByteCount(text, 0, mid) <= maxBytes)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (low > 0 && char.IsHighSurrogate(text[low - 1]))
            {
                low--;
            }

            return text.Substring(0, low);
        }

        private static string GetExcerpt(string workingDir, StatusEntry entry)
        {
            if (entry.IsUntracked)
            {
                return ReadFileHead(workingDir, entry.Path);
            }

            try
            {
                string diff = RunGit(workingDir, "diff", "-U2", "--no-color", "--", entry.Path);
                return TakeFirstLines(diff, MaxExcerptLines);
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        private static string ReadFileHead(string workingDir, string path)
        {
            try
            {
                string fullPath = Path.Combine(workingDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    return string.Empty;
                }

                List<string> lines = new();
                int count = 0;
                bool truncated = false;
                foreach (string line in File.ReadLines(fullPath))
                {
                    count++;
                    if (lines.Count < MaxExcerptLines)
                    {
                        lines.Add(line);
                    }

                    if (count >= MaxScannedFileLines)
                    {
                        truncated = true;
                        break;
                    }
                }

                return $"(untracked new file, {count}{(truncated ? "+" : string.Empty)} lines)"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, lines);
            }
            catch (Exception)
            {
                // Unreadable files (binary, permissions) simply get no excerpt.
                return string.Empty;
            }
        }

        private static string TakeFirstLines(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.Take(maxLines));
        }

        private static string Indent(string text)
            => string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').Select(line => "  " + line));

        private static string Stats(IReadOnlyDictionary<string, string> stats, string path)
            => stats.TryGetValue(path, out string? value) ? $" (+{value})" : string.Empty;

        private static Dictionary<string, string> ParseNumstat(string output)
        {
            Dictionary<string, string> stats = new(StringComparer.Ordinal);
            foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
            {
                int firstTab = line.IndexOf('\t');
                if (firstTab <= 0)
                {
                    continue;
                }

                int secondTab = line.IndexOf('\t', firstTab + 1);
                if (secondTab <= 0)
                {
                    continue;
                }

                string added = line.Substring(0, firstTab);
                string deleted = line.Substring(firstTab + 1, secondTab - firstTab - 1);
                stats[line.Substring(secondTab + 1)] = $"{added}/{deleted}";
            }

            return stats;
        }

        private static bool IsRenameOrCopy(string status)
            => status.Length > 1 && (status[0] is 'R' or 'C' || status[1] is 'R' or 'C');

        private static List<string> SplitNul(string text)
            => text.Split('\0').Where(field => field.Length > 0).ToList();

        private static string RunGit(string workingDir, params string[] arguments)
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // ArgumentList keeps paths with spaces or quotes intact without any shell involvement.
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Could not start 'git'. Ensure Git is installed and on PATH.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(GitTimeoutMs))
            {
                KillProcess(process);
                throw new InvalidOperationException(
                    $"'git {string.Join(' ', arguments)}' timed out after {GitTimeoutMs / 1000} seconds.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 && string.IsNullOrEmpty(output))
            {
                throw new InvalidOperationException($"'git {string.Join(' ', arguments)}' failed: {error}");
            }

            return output;
        }

        // A timed-out child must not keep running: Dispose alone would leave it behind.
        private static void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // It exited between the checks; nothing left to do.
            }
        }
    }
}
