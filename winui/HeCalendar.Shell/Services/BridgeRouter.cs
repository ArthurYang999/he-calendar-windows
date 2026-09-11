using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeCalendar.Shell;

public static class BridgeRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string? Handle(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) return null;
        var type = typeEl.GetString();
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

        object? data = type switch
        {
            "todos.list" => TodoStore.ListByDate(root.GetProperty("date").GetString()!),
            "todos.monthSummary" => TodoStore.MonthSummary(root.GetProperty("prefix").GetString()!),
            "todos.create" => TodoStore.Create(
                root.GetProperty("date").GetString()!,
                root.GetProperty("title").GetString()!,
                root.TryGetProperty("remindAt", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() : null),
            "todos.delete" => Delete(root),
            "todos.update" => Update(root),
            "settings.get" => TodoStore.GetSetting(root.GetProperty("key").GetString()!, root.TryGetProperty("defaultValue", out var d) ? d.GetString() ?? "" : ""),
            "settings.set" => Set(root),
            "shell.hide" => Hide(),
            "shell.exit" => true,
            "shell.setAlmanacExpanded" => true,
            "shell.syncAlmanacDate" => true,
            _ => null,
        };

        var payload = new JsonObject
        {
            ["id"] = id,
            ["ok"] = true,
            ["data"] = JsonSerializer.SerializeToNode(data, JsonOptions),
        };
        return payload.ToJsonString();
    }

    private static object Delete(JsonElement root)
    {
        TodoStore.Delete(root.GetProperty("todoId").GetString()!);
        return true;
    }

    private static object Update(JsonElement root)
    {
        string? title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        bool? done = root.TryGetProperty("done", out var d) && (d.ValueKind is JsonValueKind.True or JsonValueKind.False) ? d.GetBoolean() : null;
        var hasRemind = root.TryGetProperty("remindAt", out var r);
        string? remindAt = hasRemind && r.ValueKind == JsonValueKind.String ? r.GetString() : (hasRemind && r.ValueKind == JsonValueKind.Null ? null : null);
        var reset = hasRemind;
        TodoStore.Update(root.GetProperty("todoId").GetString()!, title, done, hasRemind ? remindAt : null, reset);
        return true;
    }

    private static object Set(JsonElement root)
    {
        TodoStore.SetSetting(root.GetProperty("key").GetString()!, root.GetProperty("value").GetString()!);
        return true;
    }

    private static object Hide()
    {
        // Caller window handles hide via separate path; acknowledge.
        return true;
    }
}
