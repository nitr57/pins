// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NINA.Mcp.Tools;

internal enum ArgumentType {
    Number,
    Integer,
    Boolean,
    String,
    Enum,
    RightAscension,
    Declination,
    ObjectArray
}

/// <summary>
/// Declares a tool's arguments once. Produces the JSON schema listed to clients (<see cref="ToJson"/>) and validates calls
/// against the same declarations (<see cref="Validate"/>), so that schema and runtime checks cannot drift apart.
/// The protocol layer does not validate arguments against schemas; this is where unknown, missing, mistyped and
/// out-of-range arguments are rejected.
/// </summary>
internal sealed class ToolSchema {

    private readonly List<ArgumentSpec> arguments = [];

    public IReadOnlyList<ArgumentSpec> Arguments => arguments;

    public bool Contains(string name) {
        return arguments.Any(argument => argument.Name == name);
    }

    public ArgumentSpec? Find(string name) {
        return arguments.FirstOrDefault(argument => argument.Name == name);
    }

    public ToolSchema Number(
        string name,
        string description,
        double? minimum = null,
        double? maximum = null,
        bool required = false,
        double? defaultValue = null,
        bool exclusiveMaximum = false) {
        return Add(new ArgumentSpec(name, ArgumentType.Number, description, required) {
            Minimum = minimum,
            Maximum = maximum,
            ExclusiveMaximum = exclusiveMaximum,
            Default = defaultValue is null ? null : JsonValue.Create(defaultValue.Value)
        });
    }

    public ToolSchema Integer(
        string name,
        string description,
        int? minimum = null,
        int? maximum = null,
        bool required = false,
        int? defaultValue = null) {
        return Add(new ArgumentSpec(name, ArgumentType.Integer, description, required) {
            Minimum = minimum,
            Maximum = maximum,
            Default = defaultValue is null ? null : JsonValue.Create(defaultValue.Value)
        });
    }

    public ToolSchema Boolean(string name, string description, bool? defaultValue = null, bool required = false) {
        return Add(new ArgumentSpec(name, ArgumentType.Boolean, description, required) {
            Default = defaultValue is null ? null : JsonValue.Create(defaultValue.Value)
        });
    }

    public ToolSchema String(
        string name,
        string description,
        bool required = false,
        int maxLength = 200,
        string? pattern = null) {
        return Add(new ArgumentSpec(name, ArgumentType.String, description, required) {
            MaxLength = maxLength,
            Pattern = pattern
        });
    }

    public ToolSchema Enum(string name, string description, IReadOnlyList<string> values, bool required = false) {
        return Add(new ArgumentSpec(name, ArgumentType.Enum, description, required) {
            Values = values
        });
    }

    /// <summary>Right ascension in hours: number or sexagesimal string, parsed by <see cref="CoordinateParser"/>.</summary>
    public ToolSchema RightAscension(string name, string description, bool required = false) {
        return Add(new ArgumentSpec(name, ArgumentType.RightAscension, description, required) {
            Minimum = 0,
            Maximum = 24,
            ExclusiveMaximum = true
        });
    }

    /// <summary>Declination in degrees: number or sexagesimal string, parsed by <see cref="CoordinateParser"/>.</summary>
    public ToolSchema Declination(string name, string description, bool required = false) {
        return Add(new ArgumentSpec(name, ArgumentType.Declination, description, required) {
            Minimum = -90,
            Maximum = 90
        });
    }

    public ToolSchema ObjectArray(
        string name,
        string description,
        ToolSchema items,
        int minItems,
        int maxItems,
        bool required = false) {
        return Add(new ArgumentSpec(name, ArgumentType.ObjectArray, description, required) {
            Items = items,
            MinItems = minItems,
            MaxItems = maxItems
        });
    }

    /// <summary>The optional <c>wait_seconds</c> argument of tools that start or wait for background jobs.</summary>
    public ToolSchema WaitSeconds(double defaultSeconds, string description) {
        return Number(
            "wait_seconds",
            description,
            minimum: 0,
            maximum: JobPayload.MaxWaitSeconds,
            defaultValue: defaultSeconds);
    }

    public JsonObject ToJson() {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var argument in arguments) {
            properties[argument.Name] = argument.ToJson();

            if (argument.Required) {
                required.Add(argument.Name);
            }
        }

        var schema = new JsonObject {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0) {
            schema["required"] = required;
        }

        schema["additionalProperties"] = false;

        return schema;
    }

    /// <summary>
    /// Throws <see cref="ToolArgumentException"/> for unknown arguments, missing required ones, wrong types and values out of
    /// range. <paramref name="path"/> prefixes messages for nested objects (e.g. "exposures[1]").
    /// </summary>
    public void Validate(ToolArguments values, string? path = null) {
        foreach (var name in values.Names) {
            if (Contains(name)) {
                continue;
            }

            var allowed = arguments.Count == 0
                ? "This tool takes no arguments."
                : $"Allowed: {string.Join(", ", arguments.Select(argument => argument.Name))}.";

            throw new ToolArgumentException($"Unknown argument '{ArgumentSpec.Qualify(path, name)}'. {allowed}");
        }

        foreach (var argument in arguments) {
            argument.Validate(values, path);
        }
    }

    private ToolSchema Add(ArgumentSpec spec) {
        if (Contains(spec.Name)) {
            throw new InvalidOperationException($"Argument '{spec.Name}' is declared twice.");
        }

        arguments.Add(spec);

        return this;
    }
}

/// <summary>One declared argument. Built by <see cref="ToolSchema"/>.</summary>
internal sealed class ArgumentSpec {

    public ArgumentSpec(string name, ArgumentType type, string description, bool required) {
        Name = name;
        Type = type;
        Description = description;
        Required = required;
    }

    public string Name { get; }

    public ArgumentType Type { get; }

    public string Description { get; }

    public bool Required { get; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public bool ExclusiveMaximum { get; init; }

    public JsonNode? Default { get; init; }

    public int MaxLength { get; init; }

    public string? Pattern { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    public ToolSchema? Items { get; init; }

    public int MinItems { get; init; }

    public int MaxItems { get; init; }

    public static string Qualify(string? path, string name) {
        return path is null ? name : $"{path}.{name}";
    }

    /// <summary>Lower case without '_', '-' and spaces: "Filter Wheel", "filterwheel" and "filter_wheel" are the same enum value.</summary>
    public static string NormalizeEnumValue(string value) {
        return new string(value
            .Where(character => character is not ('_' or '-' or ' '))
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private bool IsCoordinate => Type is ArgumentType.RightAscension or ArgumentType.Declination;

    private JsonObject CoordinateNumberSchema() {
        var number = new JsonObject {
            ["type"] = "number"
        };

        AddRange(number);

        return number;
    }

    private void AddRange(JsonObject json) {
        if (Minimum is not null) {
            json["minimum"] = Minimum.Value;
        }

        if (Maximum is not null) {
            json[ExclusiveMaximum ? "exclusiveMaximum" : "maximum"] = Maximum.Value;
        }
    }

    public JsonObject ToJson() {
        var json = new JsonObject();

        switch (Type) {
            case ArgumentType.Number:
                json["type"] = "number";
                break;

            case ArgumentType.Integer:
                json["type"] = "integer";
                break;

            case ArgumentType.Boolean:
                json["type"] = "boolean";
                break;

            case ArgumentType.String:
                json["type"] = "string";
                break;

            case ArgumentType.Enum:
                json["type"] = "string";
                json["enum"] = new JsonArray(Values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                break;

            case ArgumentType.RightAscension:
            case ArgumentType.Declination:
                // anyOf instead of a type array: several MCP clients reject "type": [..]. Ranges apply to the number form.
                json["anyOf"] = new JsonArray(CoordinateNumberSchema(), new JsonObject {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = 40
                });
                break;

            case ArgumentType.ObjectArray:
                json["type"] = "array";
                break;
        }

        json["description"] = Description;

        if (!IsCoordinate) {
            AddRange(json);
        }

        if (Type == ArgumentType.String) {
            json["minLength"] = 1;
            json["maxLength"] = MaxLength;

            if (Pattern is not null) {
                json["pattern"] = Pattern;
            }
        }

        if (Type == ArgumentType.ObjectArray) {
            json["minItems"] = MinItems;
            json["maxItems"] = MaxItems;
            json["items"] = Items!.ToJson();
        }

        if (Default is not null) {
            json["default"] = Default.DeepClone();
        }

        return json;
    }

    public void Validate(ToolArguments values, string? path) {
        var qualified = Qualify(path, Name);

        if (!values.Has(Name)) {
            if (Required) {
                throw new ToolArgumentException($"Argument '{qualified}' is required.");
            }

            return;
        }

        switch (Type) {
            case ArgumentType.Number:
                CheckRange(qualified, Read(path, () => values.GetDouble(Name))!.Value);
                break;

            case ArgumentType.Integer:
                CheckRange(qualified, Read(path, () => values.GetInt(Name))!.Value);
                break;

            case ArgumentType.Boolean:
                Read(path, () => values.GetBool(Name));
                break;

            case ArgumentType.String:
                CheckString(qualified, Read(path, () => values.GetString(Name))!);
                break;

            case ArgumentType.Enum:
                CheckEnum(qualified, Read(path, () => values.GetString(Name))!);
                break;

            case ArgumentType.RightAscension:
                CoordinateParser.ReadRightAscension(values, Name, qualified);
                break;

            case ArgumentType.Declination:
                CoordinateParser.ReadDeclination(values, Name, qualified);
                break;

            case ArgumentType.ObjectArray:
                CheckItems(qualified, Read(path, () => values.GetObjectArray(Name))!);
                break;
        }
    }

    /// <summary>Runs a <see cref="ToolArguments"/> getter and prefixes its error message with the nesting path.</summary>
    private static T Read<T>(string? path, Func<T> getter) {
        try {
            return getter();
        } catch (ToolArgumentException ex) when (path is not null) {
            throw new ToolArgumentException($"{path}: {ex.Message}");
        }
    }

    private void CheckRange(string qualified, double value) {
        var belowMinimum = Minimum is not null && value < Minimum.Value;
        var aboveMaximum = Maximum is not null && (ExclusiveMaximum ? value >= Maximum.Value : value > Maximum.Value);

        if (!belowMinimum && !aboveMaximum) {
            return;
        }

        throw new ToolArgumentException($"Argument '{qualified}' must be {DescribeRange()} (got {Format(value)}).");
    }

    private string DescribeRange() {
        if (Minimum is not null && Maximum is not null) {
            var upper = ExclusiveMaximum ? "below" : "at most";

            return $"at least {Format(Minimum.Value)} and {upper} {Format(Maximum.Value)}";
        }

        if (Minimum is not null) {
            return $"at least {Format(Minimum.Value)}";
        }

        return ExclusiveMaximum ? $"below {Format(Maximum!.Value)}" : $"at most {Format(Maximum!.Value)}";
    }

    private void CheckString(string qualified, string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ToolArgumentException($"Argument '{qualified}' must not be empty.");
        }

        if (value.Length > MaxLength) {
            throw new ToolArgumentException($"Argument '{qualified}' must be at most {MaxLength} characters long.");
        }

        if (Pattern is not null && !Regex.IsMatch(value, Pattern, RegexOptions.CultureInvariant)) {
            throw new ToolArgumentException($"Argument '{qualified}' has invalid characters (allowed pattern: {Pattern}).");
        }
    }

    private void CheckEnum(string qualified, string value) {
        var normalized = NormalizeEnumValue(value);

        if (Values.Any(allowed => NormalizeEnumValue(allowed) == normalized)) {
            return;
        }

        throw new ToolArgumentException($"Argument '{qualified}' must be one of: {string.Join(", ", Values)} (got \"{value}\").");
    }

    private void CheckItems(string qualified, IReadOnlyList<ToolArguments> items) {
        if (items.Count < MinItems || items.Count > MaxItems) {
            throw new ToolArgumentException(
                $"Argument '{qualified}' must contain {MinItems} to {MaxItems} entries (got {items.Count}).");
        }

        for (var i = 0; i < items.Count; i++) {
            Items!.Validate(items[i], $"{qualified}[{i}]");
        }
    }

    private static string Format(double value) {
        return value.ToString("0.#########", CultureInfo.InvariantCulture);
    }
}
