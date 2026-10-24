// SPDX-License-Identifier: MPL-2.0

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Mcp.Protocol;

/// <summary>
/// The one JSON configuration used for everything the MCP endpoint writes: protocol messages and tool payloads.
/// camelCase property names, enums as camelCase strings, nulls omitted. Relaxed escaping: the output is never embedded in
/// HTML, so "°", quotes and apostrophes stay readable for the model instead of becoming \u escapes.
/// </summary>
public static class McpJson {

    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}
