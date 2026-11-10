// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NINA.Mcp.Logs;

/// <summary>Turns tool arguments, answers and job texts into short single-line text for the pins log.</summary>
internal static class LogText {

    public const int MaxArgumentsLength = 500;

    public const int MaxTextLength = 300;

    private const string Redacted = "***";

    private static readonly string[] SecretWords = [
        "password", "passwd", "pass", "pwd", "passphrase", "token", "secret", "key", "apikey", "credential", "credentials"
    ];

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    /// <summary>
    /// <paramref name="text"/> on one line: whitespace, line and paragraph separators and control characters (which
    /// journalctl would show as blob data) become spaces, runs of them one. Longer text is cut to
    /// <paramref name="maxLength"/> characters plus a "…".
    /// </summary>
    public static string Compact(string? text, int maxLength = MaxTextLength) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        maxLength = Math.Max(0, maxLength);

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        bool pendingSpace = false;
        bool cut = false;

        foreach (char c in text) {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) {
                pendingSpace = builder.Length > 0;
                continue;
            }

            int needed = pendingSpace ? 2 : 1;

            if (builder.Length + needed > maxLength) {
                cut = true;
                break;
            }

            if (pendingSpace) {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        if (!cut) {
            return builder.ToString();
        }

        // A cut between the two halves of a surrogate pair would leave a lone high surrogate, and maybe a space before it.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) {
            builder.Length--;

            if (builder.Length > 0 && builder[^1] == ' ') {
                builder.Length--;
            }
        }

        return builder.Append('…').ToString();
    }

    /// <summary>
    /// The tool arguments as compact JSON, with the values of keys that look like secrets (password, passwd, pass, pwd,
    /// passphrase, token, secret, key, apikey, credential(s)) replaced by "***", on one line and cut to
    /// <see cref="MaxArgumentsLength"/> characters. Never throws: the log line must not keep a tool from running.
    /// </summary>
    public static string Arguments(JsonElement? arguments) {
        try {
            if (arguments is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) {
                return "{}";
            }

            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
                Write(writer, element);
            }

            return Compact(Encoding.UTF8.GetString(buffer.ToArray()), MaxArgumentsLength);
        } catch (Exception ex) {
            return $"(arguments not readable: {ex.GetType().Name})";
        }
    }

    /// <summary>Copies <paramref name="element"/>, redacting secret values; duplicate property names are kept as sent.</summary>
    private static void Write(Utf8JsonWriter writer, JsonElement element) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject()) {
                    writer.WritePropertyName(property.Name);

                    if (LooksSecret(property.Name)) {
                        writer.WriteStringValue(Redacted);
                    } else {
                        Write(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray()) {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool LooksSecret(string name) {
        // Whole words only ("api_key", "wifiPass", "APIKey"), so that "keyframe" or "compass" stay readable; a word also
        // counts without trailing digits and a plural "s" ("tokens", "password2").
        return SplitWords(name).Any(word => IsSecretWord(word) || IsSecretWord(Singular(word.TrimEnd("0123456789".ToCharArray()))));
    }

    private static bool IsSecretWord(string word) {
        return SecretWords.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    private static string Singular(string word) {
        return word.Length > 1 && word.EndsWith('s') ? word[..^1] : word;
    }

    /// <summary>Splits on '_', '-', '.' and spaces and at camel-case boundaries, keeping acronyms ("SSHPassword": SSH, Password).</summary>
    private static List<string> SplitWords(string name) {
        var words = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < name.Length; i++) {
            char c = name[i];

            if (c is '_' or '-' or '.' or ' ') {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0) {
                char previous = current[^1];
                bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                if (!char.IsUpper(previous) || nextIsLower) {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();

        return words;

        void Flush() {
            if (current.Length > 0) {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
