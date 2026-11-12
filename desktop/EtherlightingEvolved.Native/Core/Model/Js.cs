// JavaScript value semantics for the ports of the browser workspace's logic modules (preview/*.mjs, src/*.mjs).
// JSON trees use System.Text.Json.Nodes. A missing property stands for undefined; a JSON null value stands for null.
// System.Text.Json reads both as a C# null, so code that must tell them apart checks the containing object.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>Helpers that reproduce JavaScript behavior on JSON nodes: truthiness, coercion, equality and formatting.</summary>
public static class Js
{
    // ----- Reading values -----

    /// <summary>The property of an object (obj[key]); null when absent or when the node is not an object.</summary>
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>The element of an array, or the property of an object with a numeric key.</summary>
    public static JsonNode? Get(JsonNode? node, double key) => node switch
    {
        JsonArray array when key >= 0 && key < array.Count && key == Math.Floor(key) => array[(int)key],
        JsonObject obj => Get(obj, Key(key)),
        _ => null,
    };

    /// <summary>Object.hasOwn(node, key).</summary>
    public static bool Has(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.ContainsKey(key),
        JsonArray array => IsArrayIndex(key) && long.Parse(key, CultureInfo.InvariantCulture) < array.Count,
        _ => false,
    };

    public static bool Has(JsonNode? node, double key) => Has(node, Key(key));

    /// <summary>The number held by a JSON number value, whatever .NET type it was created from.</summary>
    public static double? Num(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double d)) return d;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out long l)) return l;
        if (value.TryGetValue(out float f)) return f;
        if (value.TryGetValue(out decimal m)) return (double)m;
        if (value.TryGetValue(out short s)) return s;
        if (value.TryGetValue(out uint ui)) return ui;
        if (value.TryGetValue(out ulong ul)) return ul;
        if (value.TryGetValue(out byte b)) return b;
        if (value.TryGetValue(out sbyte sb)) return sb;
        if (value.TryGetValue(out ushort us)) return us;
        return null;
    }

    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    public static bool IsNumber(JsonNode? node) => Num(node) is not null;

    public static bool IsString(JsonNode? node) => Str(node) is not null;

    /// <summary>typeof value === 'boolean'.</summary>
    public static bool IsBoolean(JsonNode? node) => Bool(node) is not null;

    /// <summary>A plain object: not null and not an array.</summary>
    public static bool IsObject(JsonNode? node) => node is JsonObject;

    /// <summary>value === true.</summary>
    public static bool IsTrue(JsonNode? node) => Bool(node) == true;

    /// <summary>value === false.</summary>
    public static bool IsFalse(JsonNode? node) => Bool(node) == false;

    /// <summary>Number.isFinite(value): a finite number, without coercion.</summary>
    public static bool IsFinite(JsonNode? node) => Num(node) is { } number && double.IsFinite(number);

    /// <summary>Number.isInteger(value).</summary>
    public static bool IsInteger(JsonNode? node) => Num(node) is { } number && IsInteger(number);

    public static bool IsInteger(double number) => double.IsFinite(number) && Math.Floor(number) == number;

    /// <summary>Number.isSafeInteger(value).</summary>
    public static bool IsSafeInteger(JsonNode? node) => Num(node) is { } number && IsInteger(number) && Math.Abs(number) <= 9007199254740991;

    /// <summary>JavaScript truthiness.</summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null: return false;
            case JsonObject or JsonArray: return true;
        }
        if (Bool(node) is { } flag) return flag;
        if (Str(node) is { } text) return text.Length > 0;
        if (Num(node) is { } number) return number != 0 && !double.IsNaN(number);
        return true;
    }

    public static bool Truthy(double number) => number != 0 && !double.IsNaN(number);

    // ----- Coercion -----

    /// <summary>Number(value). A C# null is read as undefined (NaN).</summary>
    public static double ToNumber(JsonNode? node)
    {
        switch (node)
        {
            case null: return double.NaN;
            case JsonObject: return double.NaN;
            case JsonArray array: return StringToNumber(ToStr(array));
        }
        if (Num(node) is { } number) return number;
        if (Bool(node) is { } flag) return flag ? 1 : 0;
        if (Str(node) is { } text) return StringToNumber(text);
        return double.NaN;
    }

    /// <summary>
    /// The number an arithmetic expression reads from obj[key]: undefined (absent) is NaN and null is 0.
    /// </summary>
    public static double Arith(JsonNode? obj, string key)
    {
        if (obj is not JsonObject o || !o.TryGetPropertyValue(key, out var value)) return double.NaN;
        return value is null ? 0 : ToNumber(value);
    }

    /// <summary>The numeric value of a string as JavaScript's Number() reads it.</summary>
    public static double StringToNumber(string text)
    {
        var s = Trim(text);
        if (s.Length == 0) return 0;
        if (s.Length > 2 && s[0] == '0' && s[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            var radix = s[1] is 'x' or 'X' ? 16 : s[1] is 'o' or 'O' ? 8 : 2;
            double result = 0;
            foreach (var c in s.AsSpan(2))
            {
                var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 99;
                if (digit >= radix) return double.NaN;
                result = result * radix + digit;
            }
            return result;
        }
        var body = s[0] is '+' or '-' ? s[1..] : s;
        if (body == "Infinity") return s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        // StrDecimalLiteral: digits with an optional fraction and exponent; no hex, no "Infinity" spelled otherwise.
        var index = 0;
        var digits = 0;
        while (index < body.Length && char.IsAsciiDigit(body[index])) { index++; digits++; }
        if (index < body.Length && body[index] == '.')
        {
            index++;
            while (index < body.Length && char.IsAsciiDigit(body[index])) { index++; digits++; }
        }
        if (digits == 0) return double.NaN;
        if (index < body.Length && body[index] is 'e' or 'E')
        {
            index++;
            if (index < body.Length && body[index] is '+' or '-') index++;
            var exponentDigits = 0;
            while (index < body.Length && char.IsAsciiDigit(body[index])) { index++; exponentDigits++; }
            if (exponentDigits == 0) return double.NaN;
        }
        if (index != body.Length) return double.NaN;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>String(value). A C# null is read as undefined.</summary>
    public static string ToStr(JsonNode? node)
    {
        switch (node)
        {
            case null: return "undefined";
            case JsonObject: return "[object Object]";
            case JsonArray array:
                return string.Join(",", array.Select(item => item is null ? "" : ToStr(item)));
        }
        if (Str(node) is { } text) return text;
        if (Num(node) is { } number) return NumberToString(number);
        if (Bool(node) is { } flag) return flag ? "true" : "false";
        return node.ToJsonString();
    }

    /// <summary>The property key a number becomes (String(number)).</summary>
    public static string Key(double number) => NumberToString(number);

    /// <summary>JavaScript's Number.prototype.toString() for base 10.</summary>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (value == 0) return "0";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value < 0) return "-" + NumberToString(-value);
        if (value < 1e21 && value == Math.Floor(value) && value < 9007199254740992) return ((long)value).ToString(CultureInfo.InvariantCulture);
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }
        var point = text.IndexOf('.');
        var digits = point >= 0 ? text.Remove(point, 1) : text;
        var n = (point >= 0 ? point : text.Length) + exponent;
        var start = 0;
        while (start < digits.Length - 1 && digits[start] == '0') { start++; n--; }
        digits = digits[start..].TrimEnd('0');
        if (digits.Length == 0) return "0";
        var k = digits.Length;
        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        var sign = n - 1 < 0 ? "-" : "+";
        var magnitude = Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture);
        return k == 1 ? digits + "e" + sign + magnitude : digits[0] + "." + digits[1..] + "e" + sign + magnitude;
    }

    /// <summary>Number.prototype.toFixed(digits).</summary>
    public static string ToFixed(double value, int digits)
    {
        if (double.IsNaN(value)) return "NaN";
        if (Math.Abs(value) >= 1e21 || double.IsInfinity(value)) return NumberToString(value);
        if (value == 0) value = 0; // -0 prints without a sign
        return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>value.toLocaleString('en-US', {maximumFractionDigits: digits}).</summary>
    public static string LocaleNumber(double value, int maximumFractionDigits)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "∞";
        if (double.IsNegativeInfinity(value)) return "-∞";
        var text = ToFixed(Math.Round(value, maximumFractionDigits, MidpointRounding.AwayFromZero), maximumFractionDigits);
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        var point = text.IndexOf('.');
        var whole = point >= 0 ? text[..point] : text;
        var fraction = point >= 0 ? text[point..] : "";
        var grouped = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(whole[i]);
        }
        var result = grouped + fraction;
        return negative && result != "0" ? "-" + result : negative ? "-0" : result;
    }

    /// <summary>Math.round: halves round toward positive infinity.</summary>
    public static double Round(double value)
    {
        if (!double.IsFinite(value)) return value;
        var floor = Math.Floor(value);
        return value - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary>The ToInt32 conversion used by bitwise operators and Math.imul.</summary>
    public static int ToInt32(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var truncated = Math.Truncate(value);
        var modulo = truncated % 4294967296.0;
        if (modulo < 0) modulo += 4294967296.0;
        return unchecked((int)(uint)modulo);
    }

    /// <summary>The ToLength conversion used by Array.from({length}).</summary>
    public static int ToLength(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)Math.Truncate(value);
    }

    /// <summary>Math.expm1, computed without cancellation near zero.</summary>
    public static double Expm1(double x)
    {
        if (double.IsNaN(x)) return x;
        if (Math.Abs(x) < 1e-5) return x + x * x / 2 + x * x * x / 6;
        var u = Math.Exp(x);
        if (u == 1.0) return x;
        var um1 = u - 1.0;
        if (um1 == -1.0) return -1;
        if (double.IsInfinity(u)) return u;
        return um1 * x / Math.Log(u);
    }

    /// <summary>Math.log1p, computed without cancellation near zero.</summary>
    public static double Log1p(double x)
    {
        if (double.IsNaN(x) || x < -1) return double.NaN;
        if (x == -1) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return x;
        var u = 1.0 + x;
        if (u == 1.0) return x;
        return Math.Log(u) * x / (u - 1.0);
    }

    // JavaScript WhiteSpace and LineTerminator code points, which String.prototype.trim removes.
    static bool IsJsWhiteSpace(char c) => (int)c is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x2028 or 0x2029
        or 0x202F or 0x205F or 0x3000 or 0xFEFF or (>= 0x2000 and <= 0x200A);

    /// <summary>String.prototype.trim.</summary>
    public static string Trim(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsJsWhiteSpace(text[start])) start++;
        while (end > start && IsJsWhiteSpace(text[end - 1])) end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>encodeURIComponent.</summary>
    public static string EncodeURIComponent(string text)
    {
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')')) result.Append(c);
            else result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    /// <summary>decodeURIComponent.</summary>
    public static string DecodeURIComponent(string text) => Uri.UnescapeDataString(text);

    // ----- Objects and arrays -----

    /// <summary>A canonical array index ("0", "1", ...), which JavaScript lists before other property keys.</summary>
    public static bool IsArrayIndex(string key)
    {
        if (key.Length == 0 || key.Length > 10) return false;
        if (key == "0") return true;
        if (key[0] is < '1' or > '9') return false;
        foreach (var c in key) if (!char.IsAsciiDigit(c)) return false;
        return long.Parse(key, CultureInfo.InvariantCulture) <= 4294967294L;
    }

    /// <summary>Object.keys order: array-index keys in ascending order, then the other keys in insertion order.</summary>
    public static List<string> Keys(JsonNode? node)
    {
        if (node is JsonArray array) return Enumerable.Range(0, array.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        if (node is not JsonObject obj) return [];
        var keys = new List<string>(obj.Count);
        var indexes = new List<(long, string)>();
        foreach (var (key, _) in obj)
        {
            if (IsArrayIndex(key)) indexes.Add((long.Parse(key, CultureInfo.InvariantCulture), key));
            else keys.Add(key);
        }
        if (indexes.Count == 0) return keys;
        indexes.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return [.. indexes.Select(item => item.Item2), .. keys];
    }

    /// <summary>Object.entries in JavaScript order (the values are the live nodes).</summary>
    public static IEnumerable<KeyValuePair<string, JsonNode?>> Entries(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++) yield return new(i.ToString(CultureInfo.InvariantCulture), array[i]);
            yield break;
        }
        if (node is not JsonObject obj) yield break;
        foreach (var key in Keys(obj)) yield return new(key, obj[key]);
    }

    /// <summary>Object.values in JavaScript order (the live nodes).</summary>
    public static IEnumerable<JsonNode?> Values(JsonNode? node) => Entries(node).Select(entry => entry.Value);

    /// <summary>The elements of an array (the live nodes), or nothing when the node is not an array.</summary>
    public static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? (IEnumerable<JsonNode?>)[];

    /// <summary>The object elements of an array.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node) => Items(node).OfType<JsonObject>();

    /// <summary>The length of an array or a string; 0 otherwise.</summary>
    public static int Length(JsonNode? node) => node switch
    {
        JsonArray array => array.Count,
        JsonValue when Str(node) is { } text => text.Length,
        _ => 0,
    };

    /// <summary>structuredClone(value).</summary>
    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    public static JsonObject Clone(JsonObject node) => (JsonObject)node.DeepClone();

    public static JsonArray Clone(JsonArray node) => (JsonArray)node.DeepClone();

    /// <summary>
    /// Object spread ({...a, ...b}): own properties copied in order, later sources winning. Values are deep copies, as
    /// a JSON node can belong to only one parent. Sources that are not objects add nothing.
    /// </summary>
    public static JsonObject Spread(params JsonNode?[] sources)
    {
        var result = new JsonObject();
        foreach (var source in sources) Assign(result, source);
        return result;
    }

    /// <summary>Object.assign(target, source) with copied values.</summary>
    public static JsonObject Assign(JsonObject target, JsonNode? source)
    {
        if (source is JsonObject obj)
            foreach (var key in Keys(obj)) target[key] = Clone(obj[key]);
        else if (source is JsonArray array)
            for (var i = 0; i < array.Count; i++) target[i.ToString(CultureInfo.InvariantCulture)] = Clone(array[i]);
        return target;
    }

    /// <summary>Sets obj[key], or removes the key when the value is undefined (absent).</summary>
    public static void SetOrRemove(JsonObject obj, string key, JsonNode? value, bool present)
    {
        if (present) obj[key] = value;
        else obj.Remove(key);
    }

    public static JsonValue Number(double value) => JsonValue.Create(value);

    public static JsonArray NumberArray(IEnumerable<double> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(JsonValue.Create(value));
        return array;
    }

    public static JsonArray NumberArray(IEnumerable<int> values) => NumberArray(values.Select(value => (double)value));

    public static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(JsonValue.Create(value));
        return array;
    }

    /// <summary>An array of copies of the given nodes.</summary>
    public static JsonArray ArrayOf(IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(Clone(value));
        return array;
    }

    // ----- Equality -----

    /// <summary>Strict equality (===). Objects and arrays compare by reference; a C# null equals only null.</summary>
    public static bool StrictEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject || a is JsonArray || b is JsonObject || b is JsonArray) return ReferenceEquals(a, b);
        if (Num(a) is { } x) return Num(b) is { } y && x == y;
        if (Str(a) is { } s) return Str(b) is { } t && string.Equals(s, t, StringComparison.Ordinal);
        if (Bool(a) is { } p) return Bool(b) is { } q && p == q;
        return ReferenceEquals(a, b);
    }

    public static bool StrictEquals(JsonNode? a, string? b) => b is null ? a is null : Str(a) is { } s && string.Equals(s, b, StringComparison.Ordinal);

    public static bool StrictEquals(JsonNode? a, double b) => Num(a) is { } x && x == b;

    /// <summary>SameValueZero, used by Array.prototype.includes, Set and Map.</summary>
    public static bool SameValueZero(JsonNode? a, JsonNode? b)
    {
        if (Num(a) is { } x && Num(b) is { } y) return x == y || double.IsNaN(x) && double.IsNaN(y);
        return StrictEquals(a, b);
    }

    /// <summary>array.includes(value) for an array node; false when the node is not an array.</summary>
    public static bool Includes(JsonNode? array, JsonNode? value) => array is JsonArray items && items.Any(item => SameValueZero(item, value));

    public static bool Includes(JsonNode? array, double value) =>
        array is JsonArray items && items.Any(item => Num(item) is { } number && (number == value || double.IsNaN(number) && double.IsNaN(value)));

    public static bool Includes(JsonNode? array, string value) => array is JsonArray items && items.Any(item => Str(item) == value);

    /// <summary>assert.deepStrictEqual-style structural equality: numbers by value, object keys in any order.</summary>
    public static bool DeepEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        switch (a)
        {
            case JsonObject x:
                if (b is not JsonObject y || x.Count != y.Count) return false;
                foreach (var (key, value) in x)
                    if (!y.TryGetPropertyValue(key, out var other) || !DeepEqual(value, other)) return false;
                return true;
            case JsonArray xa:
                if (b is not JsonArray ya || xa.Count != ya.Count) return false;
                for (var i = 0; i < xa.Count; i++) if (!DeepEqual(xa[i], ya[i])) return false;
                return true;
        }
        if (b is JsonObject or JsonArray) return false;
        if (Num(a) is { } p) return Num(b) is { } q && (p == q || double.IsNaN(p) && double.IsNaN(q));
        if (Str(a) is { } s) return Str(b) == s;
        if (Bool(a) is { } f) return Bool(b) == f;
        return a.ToJsonString() == b.ToJsonString();
    }

    // ----- JSON.stringify -----

    /// <summary>JSON.stringify(value) with JavaScript key order and number formatting.</summary>
    public static string Stringify(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var key in Keys(obj))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, key);
                    builder.Append(':');
                    Write(builder, obj[key]);
                }
                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, array[i]);
                }
                builder.Append(']');
                return;
        }
        if (Str(node) is { } text) WriteString(builder, text);
        else if (Num(node) is { } number) builder.Append(double.IsFinite(number) ? NumberToString(number) : "null");
        else if (Bool(node) is { } flag) builder.Append(flag ? "true" : "false");
        else builder.Append(node.ToJsonString());
    }

    static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) builder.Append(c).Append(text[++i]);
                    else if (char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>JSON.parse; throws on invalid text as JSON.parse does.</summary>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    // ----- Sorting and comparing text -----

    /// <summary>
    /// The text comparison used where the browser workspace calls localeCompare: case is ignored first, then lowercase
    /// sorts before uppercase so equal-ignoring-case strings still have one order.
    /// </summary>
    public static int CompareText(string a, string b)
    {
        var order = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        if (order != 0) return order;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == b[i]) continue;
            if (char.IsLower(a[i]) && char.IsUpper(b[i])) return -1;
            if (char.IsUpper(a[i]) && char.IsLower(b[i])) return 1;
            return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>A stable numeric sort ((a, b) => a - b).</summary>
    public static List<double> SortNumbers(IEnumerable<double> values) => values.Order().ToList();

    /// <summary>[...new Set(values)] for numbers (SameValueZero), in first-seen order.</summary>
    public static List<double> Unique(IEnumerable<double> values)
    {
        var result = new List<double>();
        foreach (var value in values)
            if (!result.Any(item => item == value || double.IsNaN(item) && double.IsNaN(value))) result.Add(value);
        return result;
    }

    /// <summary>[...new Set(values)] for JSON values (SameValueZero), in first-seen order.</summary>
    public static List<JsonNode?> Unique(IEnumerable<JsonNode?> values)
    {
        var result = new List<JsonNode?>();
        foreach (var value in values)
            if (!result.Any(item => SameValueZero(item, value))) result.Add(value);
        return result;
    }

    /// <summary>
    /// Sorts JSON values with the comparator (a, b) => a - b. Values that compare as NaN keep their relative order.
    /// </summary>
    public static List<JsonNode?> SortNumeric(IEnumerable<JsonNode?> values)
    {
        var list = values.ToList();
        var sorted = list.Select((value, index) => (value, index, number: ToNumber(value))).ToList();
        // A stable insertion sort with the JavaScript comparator; NaN results count as equal.
        for (var i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            var j = i - 1;
            while (j >= 0 && sorted[j].number - current.number > 0)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = current;
        }
        return sorted.Select(item => item.value).ToList();
    }
}
