using Microsoft.Data.Sqlite;

namespace HeCalendar.Shell;

public static class TodoStore
{
    private static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeCalendar",
        "he_calendar.db");

    public static void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS todos (
              id TEXT PRIMARY KEY NOT NULL,
              date TEXT NOT NULL,
              title TEXT NOT NULL,
              done INTEGER NOT NULL DEFAULT 0,
              remind_at TEXT,
              notified INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_todos_date ON todos(date);
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY NOT NULL,
              value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO settings (key, value) VALUES ('reminders_enabled', 'true');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('theme', 'system-minimal');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('tray_time_format', 'HH:mm');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('tray_date_format', 'yyyy/M/d');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('tray_show_date', 'true');
            """;
        cmd.ExecuteNonQuery();
    }

    public static SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    public static List<Dictionary<string, object?>> ListByDate(string date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM todos WHERE date = $d ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$d", date);
        return ReadAll(cmd);
    }

    public static Dictionary<string, int> MonthSummary(string yearMonthPrefix)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, COUNT(*) as cnt FROM todos
            WHERE done = 0 AND date LIKE $p
            GROUP BY date
            """;
        cmd.Parameters.AddWithValue("$p", yearMonthPrefix + "%");
        var map = new Dictionary<string, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }
        return map;
    }

    public static Dictionary<string, object?> Create(string date, string title, string? remindAt)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO todos (id, date, title, done, remind_at, notified, created_at, updated_at)
            VALUES ($id, $date, $title, 0, $remind, 0, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$title", title.Trim());
        cmd.Parameters.AddWithValue("$remind", (object?)remindAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["date"] = date,
            ["title"] = title.Trim(),
            ["done"] = false,
            ["remindAt"] = remindAt,
            ["notified"] = false,
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
    }

    public static void Delete(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM todos WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void Update(string id, string? title, bool? done, string? remindAt, bool resetNotified)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE todos SET
              title = COALESCE($title, title),
              done = COALESCE($done, done),
              remind_at = CASE WHEN $hasRemind = 1 THEN $remind ELSE remind_at END,
              notified = CASE WHEN $reset = 1 THEN 0 ELSE notified END,
              updated_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$done", done.HasValue ? (done.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$hasRemind", remindAt != null || resetNotified ? 1 : 0);
        cmd.Parameters.AddWithValue("$remind", (object?)remindAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reset", resetNotified ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public static string GetSetting(string key, string defaultValue)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar()?.ToString();
        return result ?? defaultValue;
    }

    public static void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static List<Dictionary<string, object?>> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = reader["id"],
                ["date"] = reader["date"],
                ["title"] = reader["title"],
                ["done"] = Convert.ToInt32(reader["done"]) == 1,
                ["remindAt"] = reader["remind_at"] is DBNull ? null : reader["remind_at"],
                ["notified"] = Convert.ToInt32(reader["notified"]) == 1,
                ["createdAt"] = reader["created_at"],
                ["updatedAt"] = reader["updated_at"],
            });
        }
        return list;
    }
}
