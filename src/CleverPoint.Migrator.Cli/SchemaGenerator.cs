using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CleverPoint.Migrator.Cli;

/// <summary>
/// Builds a JSON Schema from the job classes: property names, types, defaults and the text of their
/// Description attributes. The classes are what the tool actually reads, so the schema, the agent guide
/// and the behavior cannot drift apart.
/// </summary>
public static class SchemaGenerator
{
    public static JsonElement For(Type type, string title)
    {
        var schema = Object(type);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = title;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject Object(Type type)
    {
        var defaults = Activator.CreateInstance(type);
        var properties = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var node = Node(property.PropertyType);
            if (property.GetCustomAttribute<DescriptionAttribute>() is { } description) node["description"] = description.Description;
            if (DefaultOf(property.GetValue(defaults)) is { } value) node["default"] = value;
            properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = node;
        }
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
    }

    private static JsonObject Node(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(DateTime)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type == typeof(JsonElement)) return new JsonObject();      // any value: its meaning depends on something else in the file
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = Node(type.GetGenericArguments()[1]) };
        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            return new JsonObject { ["type"] = "array", ["items"] = Node(type.GetGenericArguments()[0]) };
        return Object(type);
    }

    /// <summary>Only simple, meaningful defaults are worth showing: not empty strings, lists or nested objects.</summary>
    private static JsonNode? DefaultOf(object? value) => value switch
    {
        string s when s.Length > 0 => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        _ => null,
    };

    /// <summary>The same schema as indented plain text, for the agent guide: one line per setting.</summary>
    public static string Describe(Type type)
    {
        var sb = new StringBuilder();
        Describe(type, sb, 0);
        return sb.ToString();
    }

    private static void Describe(Type type, StringBuilder sb, int depth)
    {
        var defaults = Activator.CreateInstance(type);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var nested = Nested(propertyType);
            var kind = TypeName(propertyType);
            var fallback = DefaultOf(property.GetValue(defaults)) is { } value ? $", default {value.ToJsonString()}" : "";
            var text = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            sb.Append(' ', depth * 2).AppendLine($"- {JsonNamingPolicy.CamelCase.ConvertName(property.Name)} ({kind}{fallback}): {text}");
            if (nested is not null) Describe(nested, sb, depth + 1);
        }
    }

    private static Type? Nested(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) type = type.GetGenericArguments()[0];
        return type.IsClass && type != typeof(string) && !type.IsGenericType ? type : null;
    }

    private static string TypeName(Type type)
    {
        if (type == typeof(string)) return "text";
        if (type == typeof(bool)) return "true/false";
        if (type == typeof(int) || type == typeof(long)) return "number";
        if (type == typeof(DateTime)) return "date-time";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return type.GetGenericArguments()[1] == typeof(JsonElement) ? "object" : "object of text";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var item = type.GetGenericArguments()[0];
            return item.IsClass && item != typeof(string) ? "list of objects" : $"list of {TypeName(item)}";
        }
        return "object";
    }
}
