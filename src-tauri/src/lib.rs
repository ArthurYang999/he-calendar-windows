use chrono::Local;
use rusqlite::Connection;
use std::path::PathBuf;
use std::thread;
use std::time::Duration;
use tauri::{
  menu::{Menu, MenuItem},
  tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
  AppHandle, Manager, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;
use tauri_plugin_notification::NotificationExt;
use tauri_plugin_sql::{Migration, MigrationKind};

fn db_path(app: &AppHandle) -> Option<PathBuf> {
  app
    .path()
    .app_config_dir()
    .ok()
    .map(|dir| dir.join("he_calendar.db"))
}

fn reminders_enabled(conn: &Connection) -> bool {
  conn
    .query_row(
      "SELECT value FROM settings WHERE key = 'reminders_enabled'",
      [],
      |row| row.get::<_, String>(0),
    )
    .map(|v| v != "false" && v != "0")
    .unwrap_or(true)
}

fn scan_and_notify(app: &AppHandle) {
  let Some(path) = db_path(app) else {
    return;
  };
  if !path.exists() {
    return;
  }

  let Ok(conn) = Connection::open(&path) else {
    return;
  };

  if !reminders_enabled(&conn) {
    return;
  }

  let now = Local::now().format("%Y-%m-%d %H:%M").to_string();
  let Ok(mut stmt) = conn.prepare(
    "SELECT id, title, date FROM todos
     WHERE done = 0
       AND remind_at IS NOT NULL
       AND remind_at <= ?1
       AND notified = 0",
  ) else {
    return;
  };

  let Ok(rows) = stmt.query_map([&now], |row| {
    Ok((
      row.get::<_, String>(0)?,
      row.get::<_, String>(1)?,
      row.get::<_, String>(2)?,
    ))
  }) else {
    return;
  };

  let mut due = Vec::new();
  for row in rows.flatten() {
    due.push(row);
  }
  drop(stmt);

  for (id, title, date) in due {
    let body = format!("{} · {}", date, title);
    let _ = app
      .notification()
      .builder()
      .title("合社日历 · 待办提醒")
      .body(&body)
      .show();

    let _ = conn.execute("UPDATE todos SET notified = 1 WHERE id = ?1", [&id]);
  }
}

fn spawn_reminder_loop(app: AppHandle) {
  thread::spawn(move || loop {
    scan_and_notify(&app);
    thread::sleep(Duration::from_secs(60));
  });
}

fn toggle_main_window(app: &AppHandle) {
  if let Some(window) = app.get_webview_window("main") {
    if window.is_visible().unwrap_or(false) {
      let _ = window.hide();
    } else {
      let _ = window.show();
      let _ = window.set_focus();
    }
  }
}

fn show_main_window(app: &AppHandle) {
  if let Some(window) = app.get_webview_window("main") {
    let _ = window.show();
    let _ = window.set_focus();
  }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  let migrations = vec![Migration {
    version: 1,
    description: "create_todos_and_settings",
    sql: r#"
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
CREATE INDEX IF NOT EXISTS idx_todos_remind ON todos(done, notified, remind_at);

CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL
);
INSERT OR IGNORE INTO settings (key, value) VALUES ('reminders_enabled', 'true');
INSERT OR IGNORE INTO settings (key, value) VALUES ('launch_at_login', 'false');
"#,
    kind: MigrationKind::Up,
  }];

  tauri::Builder::default()
    .plugin(
      tauri_plugin_sql::Builder::default()
        .add_migrations("sqlite:he_calendar.db", migrations)
        .build(),
    )
    .plugin(tauri_plugin_notification::init())
    .plugin(tauri_plugin_autostart::init(
      MacosLauncher::LaunchAgent,
      Some(vec![]),
    ))
    .setup(|app| {
      #[cfg(debug_assertions)]
      {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }

      let show_i = MenuItem::with_id(app, "show", "打开日历", true, None::<&str>)?;
      let today_i = MenuItem::with_id(app, "today", "今日待办", true, None::<&str>)?;
      let quit_i = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
      let menu = Menu::with_items(app, &[&show_i, &today_i, &quit_i])?;

      let _tray = TrayIconBuilder::new()
        .icon(app.default_window_icon().unwrap().clone())
        .menu(&menu)
        .show_menu_on_left_click(false)
        .tooltip("合社日历")
        .on_menu_event(|app, event| match event.id.as_ref() {
          "show" | "today" => show_main_window(app),
          "quit" => {
            app.exit(0);
          }
          _ => {}
        })
        .on_tray_icon_event(|tray, event| {
          if let TrayIconEvent::Click {
            button: MouseButton::Left,
            button_state: MouseButtonState::Up,
            ..
          } = event
          {
            toggle_main_window(tray.app_handle());
          }
        })
        .build(app)?;

      spawn_reminder_loop(app.handle().clone());
      Ok(())
    })
    .on_window_event(|window, event| {
      if let WindowEvent::CloseRequested { api, .. } = event {
        api.prevent_close();
        let _ = window.hide();
      }
    })
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}
