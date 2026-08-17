using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CrossBuy.Analyzers
{
    /// <summary>One frozen debt entry.</summary>
    internal sealed class BaselineEntry
    {
        internal string Id = string.Empty;
        internal string Controller = string.Empty;
        internal string Action = string.Empty;
        internal string Classification = string.Empty;

        internal bool IsAnonymousByDesign =>
            string.Equals(Classification, "AnonymousByDesign", StringComparison.Ordinal);
    }

    /// <summary>
    /// The parsed authorization baseline.
    ///
    /// Parsed with the small reader below rather than System.Text.Json. An analyzer is loaded into the compiler
    /// process, where a serializer dependency is a real hazard: the compiler may already have loaded a different
    /// version, and the resulting load failure makes the analyzer report NOTHING while still appearing installed.
    /// The baseline's shape is fixed and checked in, so a purpose-built reader is the smaller risk. The reader is
    /// covered by its own tests, including a malformed-input test.
    /// </summary>
    internal sealed class BaselineDocument
    {
        internal int FrozenMutating;
        internal int FrozenAttributeProtected;
        internal int FrozenInBodyProtected;
        internal int FrozenGaps;
        internal int DeclaredCount;

        internal readonly Dictionary<string, BaselineEntry> Entries =
            new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);

        internal static BaselineDocument? TryParse(string json)
        {
            try
            {
                if (!(new MiniJson(json).Parse() is Dictionary<string, object?> root)) return null;

                var document = new BaselineDocument();

                if (root.TryGetValue("frozenBaseline", out var frozenValue) &&
                    frozenValue is Dictionary<string, object?> frozen)
                {
                    document.FrozenMutating = Int(frozen, "mutating");
                    document.FrozenAttributeProtected = Int(frozen, "attributeProtected");
                    document.FrozenInBodyProtected = Int(frozen, "inBodyProtected");
                    document.FrozenGaps = Int(frozen, "gaps");
                }

                document.DeclaredCount = Int(root, "count");

                if (root.TryGetValue("entries", out var entriesValue) && entriesValue is List<object?> entries)
                {
                    foreach (var item in entries)
                    {
                        if (!(item is Dictionary<string, object?> entry)) continue;

                        var parsed = new BaselineEntry
                        {
                            Id = Str(entry, "id"),
                            Controller = Str(entry, "controller"),
                            Action = Str(entry, "action"),
                            Classification = Str(entry, "classification"),
                        };

                        if (parsed.Id.Length == 0) continue;
                        document.Entries[parsed.Id] = parsed;
                    }
                }

                return document;
            }
            catch (Exception)
            {
                // A malformed baseline must not crash the compiler. The analyzer treats "unparseable" the same as
                // "absent" and says so, rather than throwing inside a build.
                return null;
            }
        }

        private static int Int(Dictionary<string, object?> map, string key) =>
            map.TryGetValue(key, out var value) && value is double d ? (int)d : 0;

        private static string Str(Dictionary<string, object?> map, string key) =>
            map.TryGetValue(key, out var value) && value is string s ? s : string.Empty;
    }

    /// <summary>
    /// A minimal, complete JSON reader: objects, arrays, strings with escapes, numbers, true/false/null.
    /// Deliberately strict — it throws on malformed input rather than guessing, and the caller turns that into
    /// "baseline unavailable".
    /// </summary>
    internal sealed class MiniJson
    {
        private readonly string _text;
        private int _index;

        internal MiniJson(string text) => _text = text;

        internal object? Parse()
        {
            var value = ParseValue();
            SkipWhitespace();
            if (_index != _text.Length) throw new FormatException("trailing content after the JSON value");
            return value;
        }

        private object? ParseValue()
        {
            SkipWhitespace();
            if (_index >= _text.Length) throw new FormatException("unexpected end of JSON");

            var c = _text[_index];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default: return ParseNumber();
            }
        }

        private Dictionary<string, object?> ParseObject()
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            _index++;   // '{'
            SkipWhitespace();

            if (Peek() == '}') { _index++; return map; }

            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                if (Peek() != ':') throw new FormatException("expected ':'");
                _index++;
                map[key] = ParseValue();
                SkipWhitespace();

                var c = Peek();
                if (c == ',') { _index++; continue; }
                if (c == '}') { _index++; return map; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        private List<object?> ParseArray()
        {
            var list = new List<object?>();
            _index++;   // '['
            SkipWhitespace();

            if (Peek() == ']') { _index++; return list; }

            while (true)
            {
                list.Add(ParseValue());
                SkipWhitespace();

                var c = Peek();
                if (c == ',') { _index++; continue; }
                if (c == ']') { _index++; return list; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            if (Peek() != '"') throw new FormatException("expected a string");
            _index++;

            var builder = new StringBuilder();
            while (true)
            {
                if (_index >= _text.Length) throw new FormatException("unterminated string");
                var c = _text[_index++];

                if (c == '"') return builder.ToString();

                if (c != '\\') { builder.Append(c); continue; }

                if (_index >= _text.Length) throw new FormatException("unterminated escape");
                var escape = _text[_index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (_index + 4 > _text.Length) throw new FormatException("truncated \\u escape");
                        builder.Append((char)ushort.Parse(
                            _text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _index += 4;
                        break;
                    default: throw new FormatException("unknown escape '\\" + escape + "'");
                }
            }
        }

        private double ParseNumber()
        {
            var start = _index;
            if (Peek() == '-' || Peek() == '+') _index++;

            while (_index < _text.Length)
            {
                var c = _text[_index];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') { _index++; continue; }
                break;
            }

            if (_index == start) throw new FormatException("expected a number");
            return double.Parse(_text.Substring(start, _index - start), CultureInfo.InvariantCulture);
        }

        private void Expect(string literal)
        {
            if (_index + literal.Length > _text.Length ||
                string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                throw new FormatException("expected '" + literal + "'");
            _index += literal.Length;
        }

        private char Peek() => _index < _text.Length ? _text[_index] : '\0';

        private void SkipWhitespace()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++;
        }
    }
}
