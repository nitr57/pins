// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;

namespace NINA.Mcp.Tools;

/// <summary>
/// Typed, validating access to a tool call's <c>arguments</c> object. Every getter throws <see cref="ToolArgumentException"/>
/// with a message naming the argument when a value is missing (required) or has the wrong type.
/// Numbers sent as strings ("2.5") are accepted, because some clients stringify them.
/// </summary>
public sealed class ToolArguments {

    private readonly JsonElement root;

    public ToolArguments(JsonElement? arguments) {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) {
            root = JsonDocument.Parse("{}").RootElement.Clone();
            return;
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object) {
            throw new ToolArgumentException("Tool arguments must be a JSON object.");
        }

        root = arguments.Value;
    }

    public static ToolArguments Empty { get; } = new(null);

    /// <summary>Names of all members of the arguments object in document order, including members whose value is null.</summary>
    public IReadOnlyList<string> Names => root.EnumerateObject().Select(property => property.Name).ToList();

    public bool Has(string name) {
        return TryGet(name, out _);
    }

    public string? GetString(string name, bool required = false) {
        if (!TryGet(name, out var value)) {
            return required ? throw Required(name) : null;
        }

        return value.ValueKind switch {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => throw WrongType(name, "a string")
        };
    }

    public double? GetDouble(string name, bool required = false) {
        if (!TryGet(name, out var value)) {
            return Missing<double>(name, required);
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) {
            return Finite(name, number);
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) {
            return Finite(name, number);
        }

        throw WrongType(name, "a number");
    }

    public int? GetInt(string name, bool required = false) {
        var number = GetDouble(name, required);

        if (number is null) {
            return null;
        }

        if (number.Value != Math.Floor(number.Value) || number.Value < int.MinValue || number.Value > int.MaxValue) {
            throw WrongType(name, "an integer");
        }

        return (int)number.Value;
    }

    public bool? GetBool(string name, bool required = false) {
        if (!TryGet(name, out var value)) {
            return Missing<bool>(name, required);
        }

        return value.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw WrongType(name, "a boolean")
        };
    }

    /// <summary>An array of objects, each wrapped as its own <see cref="ToolArguments"/>.</summary>
    public IReadOnlyList<ToolArguments>? GetObjectArray(string name, bool required = false) {
        if (!TryGet(name, out var value)) {
            return required ? throw Required(name) : null;
        }

        if (value.ValueKind != JsonValueKind.Array) {
            throw WrongType(name, "an array of objects");
        }

        var items = new List<ToolArguments>();

        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                throw WrongType(name, "an array of objects");
            }

            items.Add(new ToolArguments(item));
        }

        return items;
    }

    /// <summary>The raw element, for arguments with free-form content.</summary>
    public JsonElement? GetRaw(string name) {
        return TryGet(name, out var value) ? value : null;
    }

    private bool TryGet(string name, out JsonElement value) {
        if (root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null) {
            return true;
        }

        value = default;
        return false;
    }

    private static T? Missing<T>(string name, bool required) where T : struct {
        return required ? throw Required(name) : null;
    }

    private static ToolArgumentException Required(string name) {
        return new ToolArgumentException($"Argument '{name}' is required.");
    }

    private static double Finite(string name, double number) {
        return double.IsFinite(number) ? number : throw WrongType(name, "a finite number");
    }

    private static ToolArgumentException WrongType(string name, string expected) {
        return new ToolArgumentException($"Argument '{name}' must be {expected}.");
    }
}
