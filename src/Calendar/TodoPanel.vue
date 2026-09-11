<script setup>
import { ref, watch, computed } from 'vue'
import { Check, Trash2, Bell, BellOff } from 'lucide-vue-next'
import {
  listByDate,
  createTodo,
  updateTodo,
  deleteTodo,
  buildRemindAt,
  remindAtToTime,
} from '../services/todo-service.js'

const props = defineProps({
  date: { type: String, required: true },
})

const emit = defineEmits(['changed'])

const todos = ref([])
const draft = ref('')
const draftRemind = ref('')
const loading = ref(false)
const errorMsg = ref('')
const editingId = ref(null)
const editingTitle = ref('')
const pendingDeleteId = ref(null)

const unfinishedCount = computed(() => todos.value.filter((t) => !t.done).length)

async function refresh() {
  loading.value = true
  errorMsg.value = ''
  try {
    todos.value = await listByDate(props.date)
  } catch (e) {
    errorMsg.value = e.message || '加载待办失败'
  } finally {
    loading.value = false
  }
}

watch(
  () => props.date,
  () => {
    pendingDeleteId.value = null
    editingId.value = null
    refresh()
  },
  { immediate: true }
)

async function addTodo() {
  errorMsg.value = ''
  try {
    await createTodo({
      date: props.date,
      title: draft.value,
      remindAt: buildRemindAt(props.date, draftRemind.value || null),
    })
    draft.value = ''
    draftRemind.value = ''
    await refresh()
    emit('changed')
  } catch (e) {
    errorMsg.value = e.message || '添加失败'
  }
}

async function onToggle(todo) {
  try {
    await updateTodo(todo.id, { done: !todo.done })
    await refresh()
    emit('changed')
  } catch (e) {
    errorMsg.value = e.message || '更新失败'
  }
}

function startEdit(todo) {
  editingId.value = todo.id
  editingTitle.value = todo.title
}

async function commitEdit(todo) {
  if (editingId.value !== todo.id) return
  try {
    await updateTodo(todo.id, { title: editingTitle.value })
    editingId.value = null
    await refresh()
    emit('changed')
  } catch (e) {
    errorMsg.value = e.message || '保存失败'
  }
}

async function onRemindChange(todo, timeValue) {
  try {
    await updateTodo(todo.id, {
      remindAt: buildRemindAt(props.date, timeValue || null),
    })
    await refresh()
    emit('changed')
  } catch (e) {
    errorMsg.value = e.message || '提醒更新失败'
  }
}

async function confirmDelete(todo) {
  if (pendingDeleteId.value !== todo.id) {
    pendingDeleteId.value = todo.id
    return
  }
  try {
    await deleteTodo(todo.id)
    pendingDeleteId.value = null
    await refresh()
    emit('changed')
  } catch (e) {
    errorMsg.value = e.message || '删除失败'
  }
}
</script>

<template>
  <section class="todo-panel">
    <div class="todo-header">
      <span class="todo-title">待办</span>
      <span v-if="unfinishedCount" class="todo-badge">{{ unfinishedCount }}</span>
    </div>

    <form class="todo-compose" @submit.prevent="addTodo">
      <input
        v-model="draft"
        class="todo-input"
        type="text"
        maxlength="200"
        placeholder="添加待办，回车确认"
      />
      <input
        v-model="draftRemind"
        class="todo-time"
        type="time"
        title="提醒时刻（可选）"
      />
      <button class="todo-add-btn" type="submit">添加</button>
    </form>

    <p v-if="errorMsg" class="todo-error">{{ errorMsg }}</p>
    <p v-else-if="loading" class="todo-hint">加载中…</p>
    <p v-else-if="!todos.length" class="todo-hint">这一天还没有待办</p>

    <ul class="todo-list">
      <li
        v-for="todo in todos"
        :key="todo.id"
        class="todo-item"
        :class="{ done: todo.done }"
      >
        <button class="todo-check" type="button" @click="onToggle(todo)" :title="todo.done ? '标为未完成' : '完成'">
          <Check v-if="todo.done" :size="14" />
        </button>

        <div class="todo-main">
          <input
            v-if="editingId === todo.id"
            v-model="editingTitle"
            class="todo-edit"
            maxlength="200"
            @keydown.enter.prevent="commitEdit(todo)"
            @blur="commitEdit(todo)"
          />
          <button
            v-else
            type="button"
            class="todo-text"
            @click="startEdit(todo)"
          >{{ todo.title }}</button>

          <div class="todo-meta">
            <label class="todo-remind" :title="todo.remindAt ? '已设提醒' : '设置提醒'">
              <Bell v-if="todo.remindAt" :size="12" />
              <BellOff v-else :size="12" />
              <input
                class="todo-time-inline"
                type="time"
                :value="remindAtToTime(todo.remindAt)"
                @change="onRemindChange(todo, $event.target.value)"
              />
            </label>
          </div>
        </div>

        <button
          class="todo-delete"
          type="button"
          :class="{ confirm: pendingDeleteId === todo.id }"
          @click="confirmDelete(todo)"
        >
          <Trash2 :size="14" />
          <span v-if="pendingDeleteId === todo.id">确认</span>
        </button>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.todo-panel {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--almanac-soft-line, var(--border-color));
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
  max-height: 42%;
}

.todo-header {
  display: flex;
  align-items: center;
  gap: 8px;
}

.todo-title {
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--almanac-gold, var(--accent-color));
  letter-spacing: 0.08em;
}

.todo-badge {
  font-size: 0.7rem;
  min-width: 18px;
  height: 18px;
  padding: 0 6px;
  border-radius: 9px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--almanac-strong-tint, var(--hover-bg));
  color: var(--primary-color);
}

.todo-compose {
  display: grid;
  grid-template-columns: 1fr auto auto;
  gap: 6px;
}

.todo-input,
.todo-time,
.todo-edit,
.todo-time-inline {
  border: 1px solid var(--almanac-line, var(--border-color));
  background: var(--almanac-board-bg, var(--panel-bg));
  color: var(--text-color);
  border-radius: 6px;
  padding: 6px 8px;
  font-size: 0.8rem;
}

.todo-input:focus,
.todo-time:focus,
.todo-edit:focus,
.todo-time-inline:focus {
  outline: none;
  border-color: var(--primary-color);
}

.todo-add-btn,
.todo-delete,
.todo-check {
  border: none;
  cursor: pointer;
  border-radius: 6px;
}

.todo-add-btn {
  padding: 0 10px;
  background: var(--primary-color);
  color: #fff;
  font-size: 0.75rem;
}

.todo-hint,
.todo-error {
  margin: 0;
  font-size: 0.75rem;
  color: var(--secondary-text, #888);
}

.todo-error {
  color: #ef4444;
}

.todo-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.todo-item {
  display: grid;
  grid-template-columns: 22px 1fr auto;
  gap: 8px;
  align-items: start;
  padding: 8px;
  border-radius: 8px;
  background: var(--almanac-detail-bg, var(--hover-bg));
}

.todo-item.done .todo-text {
  text-decoration: line-through;
  opacity: 0.55;
}

.todo-check {
  width: 22px;
  height: 22px;
  border: 1px solid var(--almanac-line, var(--border-color));
  background: transparent;
  color: var(--primary-color);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  margin-top: 1px;
}

.todo-main {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.todo-text {
  text-align: left;
  background: transparent;
  border: none;
  padding: 0;
  color: var(--text-color);
  font-size: 0.82rem;
  cursor: text;
  word-break: break-word;
}

.todo-meta {
  display: flex;
  align-items: center;
  gap: 6px;
}

.todo-remind {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--almanac-gold-soft, var(--accent-color));
  font-size: 0.7rem;
}

.todo-time-inline {
  padding: 2px 4px;
  font-size: 0.7rem;
}

.todo-delete {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 6px;
  background: transparent;
  color: var(--secondary-text, #888);
  font-size: 0.7rem;
}

.todo-delete.confirm {
  background: rgba(239, 68, 68, 0.12);
  color: #ef4444;
}
</style>
