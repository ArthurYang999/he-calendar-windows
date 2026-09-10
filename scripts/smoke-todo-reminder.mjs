/**
 * Smoke-check todo CRUD + reminder SQL semantics (localStorage fallback path).
 * Run: node scripts/smoke-todo-reminder.mjs
 */
import assert from 'node:assert/strict'

const store = new Map()
globalThis.localStorage = {
  getItem: (k) => (store.has(k) ? store.get(k) : null),
  setItem: (k, v) => store.set(k, String(v)),
  removeItem: (k) => store.delete(k),
}
// Force non-Tauri path
globalThis.window = { localStorage: globalThis.localStorage }

const {
  buildRemindAt,
  remindAtToTime,
  createTodo,
  listByDate,
  updateTodo,
  deleteTodo,
  toggleDone,
  monthSummary,
} = await import('../src/services/todo-service.js')

const date = '2026-09-10'

assert.equal(buildRemindAt(date, '09:30'), '2026-09-10 09:30')
assert.equal(buildRemindAt(date, ''), null)
assert.equal(remindAtToTime('2026-09-10 09:30'), '09:30')

await assert.rejects(() => createTodo({ date, title: '   ' }), /不能为空/)
await assert.rejects(
  () => createTodo({ date, title: 'x'.repeat(201) }),
  /不能超过/
)

const a = await createTodo({
  date,
  title: '开会',
  remindAt: buildRemindAt(date, '08:00'),
})
assert.equal(a.done, false)
assert.equal(a.notified, false)
assert.equal(a.remindAt, '2026-09-10 08:00')

const listed = await listByDate(date)
assert.equal(listed.length, 1)
assert.equal(listed[0].title, '开会')

const summary = await monthSummary(2026, 9)
assert.equal(summary[date], 1)

const updated = await updateTodo(a.id, { remindAt: buildRemindAt(date, '10:00') })
assert.equal(updated.remindAt, '2026-09-10 10:00')
assert.equal(updated.notified, false)

// Simulate "already notified" then change time → reset
await updateTodo(a.id, { remindAt: buildRemindAt(date, '11:00') })
{
  // force notified via localStorage mutation
  const raw = JSON.parse(localStorage.getItem('he-calendar-todos'))
  raw[0].notified = true
  localStorage.setItem('he-calendar-todos', JSON.stringify(raw))
}
const reset = await updateTodo(a.id, { remindAt: buildRemindAt(date, '12:00') })
assert.equal(reset.notified, false)

await toggleDone(a.id, true)
const afterDone = await listByDate(date)
assert.equal(afterDone[0].done, true)
const summaryDone = await monthSummary(2026, 9)
assert.equal(summaryDone[date], undefined)

await deleteTodo(a.id)
assert.equal((await listByDate(date)).length, 0)

/** Reminder scanner SQL semantics (mirrors Rust query). */
function dueTodos(rows, now) {
  return rows.filter(
    (t) =>
      !t.done &&
      t.remindAt != null &&
      t.remindAt <= now &&
      !t.notified
  )
}

const sample = [
  { id: '1', done: false, remindAt: '2026-09-10 09:00', notified: false, title: 'a' },
  { id: '2', done: true, remindAt: '2026-09-10 08:00', notified: false, title: 'b' },
  { id: '3', done: false, remindAt: '2026-09-10 10:00', notified: false, title: 'c' },
  { id: '4', done: false, remindAt: '2026-09-10 08:30', notified: true, title: 'd' },
]
const due = dueTodos(sample, '2026-09-10 09:30')
assert.deepEqual(
  due.map((t) => t.id),
  ['1']
)

console.log('smoke-todo-reminder: OK')
