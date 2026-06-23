using System.Globalization;
using System.Text;

namespace Bbs.Import.Bpq;

/// <summary>
/// A parsed libconfig group: scalar <c>key = value</c> assignments plus nested sub-groups.
/// Lookups are ordinal case-sensitive (libconfig keys are case-sensitive).
/// </summary>
internal sealed class ConfigGroup
{
    private readonly Dictionary<string, string> _scalars = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, ConfigGroup>> _subGroups = [];

    /// <summary>The nested sub-groups, in file order (duplicate names are all retained).</summary>
    public IReadOnlyList<KeyValuePair<string, ConfigGroup>> SubGroups => _subGroups;

    internal void SetScalar(string key, string value) => _scalars[key] = value;

    internal void AddSubGroup(string name, ConfigGroup group) => _subGroups.Add(new(name, group));

    /// <summary>The scalar assignments, in insertion order is not guaranteed; suitable for the BBSUsers map.</summary>
    public IEnumerable<KeyValuePair<string, string>> Scalars() => _scalars;

    /// <summary>The first sub-group with the given name, or null.</summary>
    public ConfigGroup? Group(string name)
    {
        foreach (KeyValuePair<string, ConfigGroup> kv in _subGroups)
        {
            if (string.Equals(kv.Key, name, StringComparison.Ordinal))
            {
                return kv.Value;
            }
        }

        return null;
    }

    /// <summary>A raw scalar string value (quotes already removed), or null.</summary>
    public string? String(string key) => _scalars.TryGetValue(key, out string? v) ? v : null;

    /// <summary>An integer scalar (tolerates a trailing libconfig <c>L</c> int64 suffix), or null.</summary>
    public int? Int(string key)
    {
        string? raw = String(key);
        if (raw is null)
        {
            return null;
        }

        raw = raw.Trim().TrimEnd('L', 'l');
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }
}

/// <summary>
/// A minimal, tolerant libconfig text parser covering the subset BPQMail's <c>linmail.cfg</c> emits:
/// nested <c>name : { ... };</c> groups and <c>key = value;</c> scalar assignments (quoted string,
/// integer with optional <c>L</c>, float, or bare token). Handles <c>//</c>, <c>#</c> and the
/// non-standard <c>;</c>-prefixed comment lines seen in BPQ seed files. String escapes
/// (<c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\r</c>, <c>\t</c>) are decoded. Adjacent string literals
/// are concatenated (libconfig allows this for long lines).
/// </summary>
internal static class LibConfig
{
    /// <summary>Parses libconfig text into a root <see cref="ConfigGroup"/>.</summary>
    public static ConfigGroup Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var p = new Cursor(text);
        var root = new ConfigGroup();
        ParseBody(p, root);
        return root;
    }

    private static void ParseBody(Cursor p, ConfigGroup group)
    {
        while (true)
        {
            p.SkipTrivia();
            if (p.AtEnd || p.Peek == '}')
            {
                return;
            }

            string name = p.ReadName();
            if (name.Length == 0)
            {
                // Could not make progress on a name; skip a char to avoid an infinite loop.
                if (!p.AtEnd)
                {
                    p.Advance();
                }

                continue;
            }

            p.SkipTrivia();
            char sep = p.AtEnd ? '\0' : p.Peek;

            if (sep == ':' || sep == '{')
            {
                if (sep == ':')
                {
                    p.Advance(); // ':'
                    p.SkipTrivia();
                }

                if (!p.AtEnd && p.Peek == '{')
                {
                    p.Advance(); // '{'
                    var child = new ConfigGroup();
                    ParseBody(p, child);
                    p.SkipTrivia();
                    if (!p.AtEnd && p.Peek == '}')
                    {
                        p.Advance();
                    }

                    group.AddSubGroup(name, child);
                    p.SkipTrivia();
                    if (!p.AtEnd && p.Peek == ';')
                    {
                        p.Advance();
                    }
                }
            }
            else if (sep == '=')
            {
                p.Advance(); // '='
                p.SkipTrivia();
                string value = p.ReadValue();
                group.SetScalar(name, value);
                p.SkipTrivia();
                if (!p.AtEnd && p.Peek == ';')
                {
                    p.Advance();
                }
            }
            else
            {
                // Unexpected; skip to the next ';' or '}' to resynchronise.
                p.SkipToDelimiter();
            }
        }
    }

    private sealed class Cursor(string text)
    {
        private readonly string _s = text;
        private int _i;

        public bool AtEnd => _i >= _s.Length;

        public char Peek => _s[_i];

        public void Advance() => _i++;

        public void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                }
                else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    SkipLine();
                }
                else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    _i += 2;
                    while (_i + 1 < _s.Length && !(_s[_i] == '*' && _s[_i + 1] == '/'))
                    {
                        _i++;
                    }

                    _i = Math.Min(_i + 2, _s.Length);
                }
                else if (c == '#' || c == ';')
                {
                    // '#' line comment, and the non-standard ';'-prefixed comment lines in BPQ seeds.
                    // A bare ';' that terminates a statement is consumed by the caller, not here — this
                    // only triggers when ';' appears where a statement/name is expected (i.e. trivia).
                    SkipLine();
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipLine()
        {
            while (_i < _s.Length && _s[_i] != '\n')
            {
                _i++;
            }
        }

        public void SkipToDelimiter()
        {
            while (_i < _s.Length && _s[_i] != ';' && _s[_i] != '}')
            {
                _i++;
            }

            if (_i < _s.Length && _s[_i] == ';')
            {
                _i++;
            }
        }

        public string ReadName()
        {
            int start = _i;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '*' || c == '.')
                {
                    _i++;
                }
                else
                {
                    break;
                }
            }

            return _s[start.._i];
        }

        public string ReadValue()
        {
            if (_i < _s.Length && _s[_i] == '"')
            {
                return ReadQuotedString();
            }

            // Bare token (number / boolean / unquoted) up to a ';', ',', '}' or newline.
            int start = _i;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ';' || c == ',' || c == '}' || c == '\n' || c == '\r')
                {
                    break;
                }

                _i++;
            }

            return _s[start.._i].Trim();
        }

        private string ReadQuotedString()
        {
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] == '"')
            {
                _i++; // opening quote
                while (_i < _s.Length && _s[_i] != '"')
                {
                    char c = _s[_i];
                    if (c == '\\' && _i + 1 < _s.Length)
                    {
                        char esc = _s[_i + 1];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '"' => '"',
                            '\\' => '\\',
                            _ => esc,
                        });
                        _i += 2;
                    }
                    else
                    {
                        sb.Append(c);
                        _i++;
                    }
                }

                if (_i < _s.Length && _s[_i] == '"')
                {
                    _i++; // closing quote
                }

                // Allow adjacent string-literal concatenation across whitespace/newlines.
                int save = _i;
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }

                if (_i >= _s.Length || _s[_i] != '"')
                {
                    _i = save;
                    break;
                }
            }

            return sb.ToString();
        }
    }
}
