/**
 * 桌面设置：提醒总开关、开机自启（Tauri）；浏览器预览用 localStorage。
 */

const STORAGE_PREFIX = 'he-calendar-setting:'

function isTauri() {
  return typeof window !== 'undefined' && !!(window.__TAURI_INTERNALS__ || window.__TAURI__)
}

async function getDb() {
  if (!isTauri()) return null
  const { default: Database } = await import('@tauri-apps/plugin-sql')
  return Database.load('sqlite:he_calendar.db')
}

export async function getSetting(key, defaultValue = '') {
  const db = await getDb()
  if (!db) {
    const v = localStorage.getItem(STORAGE_PREFIX + key)
    return v === null ? defaultValue : v
  }
  const rows = await db.select('SELECT value FROM settings WHERE key = $1', [key])
  return rows.length ? rows[0].value : defaultValue
}

export async function setSetting(key, value) {
  const db = await getDb()
  if (!db) {
    localStorage.setItem(STORAGE_PREFIX + key, String(value))
    return
  }
  await db.execute(
    `INSERT INTO settings (key, value) VALUES ($1, $2)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
    [key, String(value)]
  )
}

export async function getRemindersEnabled() {
  const v = await getSetting('reminders_enabled', 'true')
  return v !== 'false' && v !== '0'
}

export async function setRemindersEnabled(enabled) {
  await setSetting('reminders_enabled', enabled ? 'true' : 'false')
}

export async function getLaunchAtLogin() {
  if (!isTauri()) {
    const v = await getSetting('launch_at_login', 'false')
    return v === 'true' || v === '1'
  }
  try {
    const { isEnabled } = await import('@tauri-apps/plugin-autostart')
    return await isEnabled()
  } catch {
    const v = await getSetting('launch_at_login', 'false')
    return v === 'true' || v === '1'
  }
}

export async function setLaunchAtLogin(enabled) {
  await setSetting('launch_at_login', enabled ? 'true' : 'false')
  if (!isTauri()) return
  const { enable, disable } = await import('@tauri-apps/plugin-autostart')
  if (enabled) await enable()
  else await disable()
}

export { isTauri }
