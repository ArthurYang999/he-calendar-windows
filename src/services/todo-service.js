/**
 * 待办数据服务：
 * WinUI WebView2 → 宿主桥 SQLite
 * Tauri → plugin-sql
 * 浏览器预览 → localStorage
 */

import { bridgeInvoke, isWinUiShell } from './winui-bridge.js'

const DB_URL = 'sqlite:he_calendar.db'
const STORAGE_KEY = 'he-calendar-todos'
const TITLE_MAX = 200

let dbPromise = null

function isTauri() {
  return typeof window !== 'undefined' && !!(window.__TAURI_INTERNALS__ || window.__TAURI__)
}

function nowIso() {
  return new Date().toISOString()
}

function normalizeTitle(title) {
  const trimmed = String(title ?? '').trim()
  if (!trimmed) throw new Error('待办标题不能为空')
  if (trimmed.length > TITLE_MAX) throw new Error(`待办标题不能超过 ${TITLE_MAX} 字`)
  return trimmed
}

/** remindAt: 'HH:mm' | null → 'YYYY-MM-DD HH:mm' | null */
export function buildRemindAt(date, timeHHmm) {
  if (!timeHHmm) return null
  const t = String(timeHHmm).trim()
  if (!/^\d{2}:\d{2}$/.test(t)) return null
  return `${date} ${t}`
}

export function remindAtToTime(remindAt) {
  if (!remindAt) return ''
  const m = String(remindAt).match(/\b(\d{2}:\d{2})\b/)
  return m ? m[1] : ''
}

function mapRow(row) {
  return {
    id: row.id,
    date: row.date,
    title: row.title,
    done: !!row.done,
    remindAt: row.remindAt ?? row.remind_at ?? null,
    notified: !!row.notified,
    createdAt: row.createdAt ?? row.created_at,
    updatedAt: row.updatedAt ?? row.updated_at,
  }
}

async function getDb() {
  if (!isTauri()) return null
  if (!dbPromise) {
    dbPromise = import('@tauri-apps/plugin-sql').then(({ default: Database }) =>
      Database.load(DB_URL)
    )
  }
  return dbPromise
}

function readLocal() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]')
  } catch {
    return []
  }
}

function writeLocal(todos) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(todos))
}

export async function listByDate(date) {
  if (isWinUiShell()) {
    const rows = await bridgeInvoke('todos.list', { date })
    return (rows || []).map(mapRow)
  }
  const db = await getDb()
  if (!db) {
    return readLocal()
      .filter((t) => t.date === date)
      .sort((a, b) => String(a.createdAt).localeCompare(String(b.createdAt)))
  }
  const rows = await db.select(
    'SELECT * FROM todos WHERE date = $1 ORDER BY created_at ASC',
    [date]
  )
  return rows.map(mapRow)
}

export async function monthSummary(year, month) {
  const prefix = `${year}-${String(month).padStart(2, '0')}`
  if (isWinUiShell()) {
    return (await bridgeInvoke('todos.monthSummary', { prefix })) || {}
  }
  const db = await getDb()
  if (!db) {
    const map = {}
    for (const t of readLocal()) {
      if (!t.done && String(t.date).startsWith(prefix)) {
        map[t.date] = (map[t.date] || 0) + 1
      }
    }
    return map
  }
  const rows = await db.select(
    `SELECT date, COUNT(*) as cnt FROM todos
     WHERE done = 0 AND date LIKE $1
     GROUP BY date`,
    [`${prefix}%`]
  )
  const map = {}
  for (const row of rows) {
    map[row.date] = Number(row.cnt) || 0
  }
  return map
}

export async function createTodo({ date, title, remindAt = null }) {
  const cleanTitle = normalizeTitle(title)
  if (isWinUiShell()) {
    const item = await bridgeInvoke('todos.create', { date, title: cleanTitle, remindAt })
    return mapRow(item)
  }

  const id = crypto.randomUUID()
  const ts = nowIso()
  const item = {
    id,
    date,
    title: cleanTitle,
    done: false,
    remindAt,
    notified: false,
    createdAt: ts,
    updatedAt: ts,
  }

  const db = await getDb()
  if (!db) {
    const all = readLocal()
    all.push(item)
    writeLocal(all)
    return item
  }

  await db.execute(
    `INSERT INTO todos (id, date, title, done, remind_at, notified, created_at, updated_at)
     VALUES ($1, $2, $3, 0, $4, 0, $5, $6)`,
    [id, date, cleanTitle, remindAt, ts, ts]
  )
  return item
}

export async function updateTodo(id, patch) {
  if (isWinUiShell()) {
    const body = { todoId: id }
    if (patch.title !== undefined) body.title = normalizeTitle(patch.title)
    if (patch.done !== undefined) body.done = !!patch.done
    if (patch.remindAt !== undefined) body.remindAt = patch.remindAt
    await bridgeInvoke('todos.update', body)
    const list = await listByDate(patch.date || (await listFallbackDate(id)))
    return list.find((t) => t.id === id) || { id, ...patch }
  }

  const db = await getDb()
  const ts = nowIso()

  if (!db) {
    const all = readLocal()
    const idx = all.findIndex((t) => t.id === id)
    if (idx < 0) throw new Error('待办不存在')
    const prev = all[idx]
    const next = { ...prev, ...patch, updatedAt: ts }
    if (patch.title !== undefined) next.title = normalizeTitle(patch.title)
    if (patch.remindAt !== undefined && patch.remindAt !== prev.remindAt) {
      next.notified = false
    }
    all[idx] = next
    writeLocal(all)
    return next
  }

  const rows = await db.select('SELECT * FROM todos WHERE id = $1', [id])
  if (!rows.length) throw new Error('待办不存在')
  const prev = mapRow(rows[0])

  const title = patch.title !== undefined ? normalizeTitle(patch.title) : prev.title
  const done = patch.done !== undefined ? (patch.done ? 1 : 0) : prev.done ? 1 : 0
  let remindAt = patch.remindAt !== undefined ? patch.remindAt : prev.remindAt
  let notified = prev.notified ? 1 : 0
  if (patch.remindAt !== undefined && patch.remindAt !== prev.remindAt) {
    notified = 0
  }

  await db.execute(
    `UPDATE todos SET title = $1, done = $2, remind_at = $3, notified = $4, updated_at = $5
     WHERE id = $6`,
    [title, done, remindAt, notified, ts, id]
  )

  return {
    ...prev,
    title,
    done: !!done,
    remindAt,
    notified: !!notified,
    updatedAt: ts,
  }
}

async function listFallbackDate() {
  return new Date().toISOString().slice(0, 10)
}

export async function deleteTodo(id) {
  if (isWinUiShell()) {
    await bridgeInvoke('todos.delete', { todoId: id })
    return
  }
  const db = await getDb()
  if (!db) {
    writeLocal(readLocal().filter((t) => t.id !== id))
    return
  }
  await db.execute('DELETE FROM todos WHERE id = $1', [id])
}

export async function toggleDone(id, done) {
  return updateTodo(id, { done })
}
