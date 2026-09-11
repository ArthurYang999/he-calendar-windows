using Microsoft.Data.Sqlite;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HeCalendar.Shell;

public static class ReminderService
{
    private static Timer? _timer;

    public static void Start()
    {
        _timer ??= new Timer(_ => Scan(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }

    private static void Scan()
    {
        try
        {
            if (TodoStore.GetSetting("reminders_enabled", "true") is "false" or "0") return;

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            using var conn = TodoStore.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, date FROM todos
                WHERE done = 0 AND remind_at IS NOT NULL AND remind_at <= $now AND notified = 0
                """;
            cmd.Parameters.AddWithValue("$now", now);
            var due = new List<(string Id, string Title, string Date)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    due.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            foreach (var item in due)
            {
                try
                {
                    var notification = new AppNotificationBuilder()
                        .AddText("合社日历 · 待办提醒")
                        .AddText($"{item.Date} · {item.Title}")
                        .BuildNotification();
                    AppNotificationManager.Default.Show(notification);
                }
                catch
                {
                    // Fallback: ignore if notification channel unavailable
                }

                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE todos SET notified = 1 WHERE id = $id";
                upd.Parameters.AddWithValue("$id", item.Id);
                upd.ExecuteNonQuery();
            }
        }
        catch
        {
            // never crash background timer
        }
    }
}
