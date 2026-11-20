using System.Text;

namespace ProjectLuma.GameDiscovery;

/// <summary>Fault-tolerant Valve KeyValues (VDF/ACF) recursive dictionary parser.</summary>
public static class VdfParser
{
    public static Dictionary<string, object> Parse(string content)
    {
        int index = 0;
        return ReadBlock(content ?? string.Empty, ref index, isRoot: true);
    }

    public static string? GetString(Dictionary<string, object>? dict, string key)
        => dict != null && dict.TryGetValue(key, out object? value) ? value as string : null;

    public static Dictionary<string, object>? GetSubBlock(Dictionary<string, object>? dict, string key)
        => dict != null && dict.TryGetValue(key, out object? value) ? value as Dictionary<string, object> : null;

    private static Dictionary<string, object> ReadBlock(string text, ref int index, bool isRoot)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? key = NextToken(text, ref index, out bool openBrace, out bool closeBrace);
            if (key == null && !openBrace && !closeBrace) return result;
            if (closeBrace)
            {
                if (isRoot) continue;
                return result;
            }
            if (openBrace) continue;

            string? val = NextToken(text, ref index, out bool valOpen, out bool valClose);
            if (val == null && !valOpen && !valClose) return result;

            if (valOpen)
            {
                result[key!] = ReadBlock(text, ref index, isRoot: false);
            }
            else if (valClose)
            {
                if (isRoot) continue;
                return result;
            }
            else if (val != null)
            {
                result[key!] = val;
            }
        }
    }

    private static string? NextToken(string text, ref int index, out bool openBrace, out bool closeBrace)
    {
        openBrace = false;
        closeBrace = false;
        while (index < text.Length)
        {
            char c = text[index];
            if (char.IsWhiteSpace(c)) { index++; continue; }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }
            if (c == '{') { index++; openBrace = true; return null; }
            if (c == '}') { index++; closeBrace = true; return null; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                index++;
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        char next = text[index + 1];
                        sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                        index += 2;
                    }
                    else
                    {
                        sb.Append(text[index++]);
                    }
                }
                if (index < text.Length) index++;
                return sb.ToString();
            }
            index++;
        }
        return null;
    }
}
