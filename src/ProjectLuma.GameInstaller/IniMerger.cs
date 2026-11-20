using System.Text;

namespace ProjectLuma.GameInstaller;

/// <summary>Non-destructive INI editor that preserves comments, formatting, and user-defined keys.</summary>
public static class IniMerger
{
    public static string MergeSections(string existingContent, Dictionary<string, Dictionary<string, string>> sections)
    {
        var lines = string.IsNullOrEmpty(existingContent)
            ? new List<string>()
            : existingContent.Replace("\r\n", "\n").Split('\n').ToList();

        foreach (var (sectionName, keys) in sections)
        {
            int sectionHeaderIdx = FindSectionHeader(lines, sectionName);
            if (sectionHeaderIdx < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                    lines.Add(string.Empty);
                lines.Add($"[{sectionName}]");
                foreach (var (k, v) in keys)
                    lines.Add($"{k}={v}");
                continue;
            }

            int sectionEndIdx = FindSectionEnd(lines, sectionHeaderIdx);
            foreach (var (k, v) in keys)
            {
                int keyIdx = FindKeyInSection(lines, sectionHeaderIdx + 1, sectionEndIdx, k);
                if (keyIdx >= 0)
                {
                    lines[keyIdx] = $"{k}={v}";
                }
                else
                {
                    lines.Insert(sectionEndIdx, $"{k}={v}");
                    sectionEndIdx++;
                }
            }
        }

        string newline = existingContent.Contains("\r\n", StringComparison.Ordinal) || string.IsNullOrEmpty(existingContent)
            ? "\r\n"
            : "\n";
        return string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
    }

    private static int FindSectionHeader(List<string> lines, string section)
    {
        string target = $"[{section}]";
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Equals(target, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int FindSectionEnd(List<string> lines, int headerIdx)
    {
        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                return i;
        }
        return lines.Count;
    }

    private static int FindKeyInSection(List<string> lines, int start, int end, string key)
    {
        for (int i = start; i < end; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || string.IsNullOrEmpty(trimmed)) continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            string currentKey = trimmed[..eq].Trim();
            if (currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
