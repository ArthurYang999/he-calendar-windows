# Windows Tauri 日历 + 待办 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将合社日历改造为 Tauri 2 Windows 桌面应用：保留参考版日历功能与样式，新增按日待办、系统托盘、到点 Windows 通知。

**Architecture:** Vue 3 前端覆盖改造 `src/Calendar`；Rust/Tauri 负责窗口、托盘、开机自启与提醒扫描；SQLite（`tauri-plugin-sql`）存待办与设置。网页/uTools 不再作为交付路径。

**Tech Stack:** Tauri 2、Vue 3、Vite、dayjs、tyme4ts、`@tauri-apps/plugin-sql`、`@tauri-apps/plugin-notification`、tray-icon、SQLite

**Spec:** `docs/superpowers/specs/2026-09-10-windows-tauri-calendar-design.md`

---

## File Structure

| Path | Responsibility |
|------|----------------|
| `src-tauri/` | Tauri 应用、托盘、提醒定时器、capabilities |
| `src-tauri/migrations/0001_init.sql` | `todos` / `settings` 表 |
| `src/services/todo-service.js` | 前端待办 API 封装（load/CRUD/月摘要） |
| `src/services/settings-service.js` | 提醒开关、开机自启读写 |
| `src/Calendar/TodoPanel.vue` | 右侧当日待办 UI |
| `src/Calendar/index.vue` | 接入待办标记与 TodoPanel；弱化 uTools |
| `src/App.vue` | 桌面窗口布局；移除 uTools 路由 |
| `package.json` / `vite.config.js` | `tauri dev/build` 脚本；可移除 uTools preload 构建 |

---

### Task 1: 安装 Rust 与 Windows 构建依赖

**Files:** 环境（无仓库文件）

- [ ] **Step 1: 安装 rustup（若 `cargo` 不存在）**

```powershell
winget install --id Rustlang.Rustup -e --accept-package-agreements --accept-source-agreements
# 或: Invoke-WebRequest https://win.rustup.rs/x86_64 -OutFile rustup-init.exe; .\rustup-init.exe -y
```

Expected: 新开 shell 后 `cargo --version` 与 `rustc --version` 有输出。

- [ ] **Step 2: 确认 MSVC 构建工具**

若 `tauri build` 报 link 错误，安装 “Desktop development with C++”（Visual Studio Build Tools）。

- [ ] **Step 3: 验证**

```powershell
cargo --version
node -v
```

---

### Task 2: 在现有 Vite 项目接入 Tauri 2

**Files:**
- Create: `src-tauri/**`（`tauri init` 生成）
- Modify: `package.json`
- Modify: `vite.config.js`（保留 `base: './'`；可移除 `bundlePreload`）

- [ ] **Step 1: 安装 CLI 并初始化**

```powershell
npm install -D @tauri-apps/cli@latest
npm install @tauri-apps/api@latest
npx tauri init --ci --app-name "合社日历" --window-title "合社日历" --dev-url http://localhost:5173 --before-dev-command "npm run dev" --before-build-command "npm run build" --frontend-dist ../dist
```

- [ ] **Step 2: 更新 scripts**

在 `package.json` 增加：

```json
"tauri": "tauri",
"desktop:dev": "tauri dev",
"desktop:build": "tauri build"
```

- [ ] **Step 3: 配置窗口尺寸（对齐参考高度）**

`src-tauri/tauri.conf.json` 中 window 约 `width: 1100`, `height: 720`，`resizable: true`。

- [ ] **Step 4: 试跑**

```powershell
npm run desktop:dev
```

Expected: 打开原生窗口并加载现有日历 UI。

---

### Task 3: SQLite 插件与 schema

**Files:**
- Create: `src-tauri/migrations/0001_init.sql`
- Modify: `src-tauri/Cargo.toml`, `src-tauri/src/lib.rs`, capabilities
- Create: `src/services/todo-service.js`, `src/services/settings-service.js`

- [ ] **Step 1: 添加插件**

```powershell
npm run tauri add sql
npm run tauri add notification
npm run tauri add autostart
npm install @tauri-apps/plugin-sql @tauri-apps/plugin-notification @tauri-apps/plugin-autostart
```

Cargo feature 启用 sqlite；`lib.rs` 注册插件与迁移：

```rust
.plugin(
  tauri_plugin_sql::Builder::default()
    .add_migrations("sqlite:he_calendar.db", migrations)
    .build(),
)
```

- [ ] **Step 2: 迁移 SQL**

```sql
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
```

- [ ] **Step 3: 前端 `todo-service.js`**

实现：`ensureDb()`、`listByDate(date)`、`monthSummary(year, month)`、`createTodo({date,title,remindAt})`、`updateTodo`、`deleteTodo`、`toggleDone`。标题 trim；空串抛错；长度 >200 抛错；新建时 `id` 用 `crypto.randomUUID()`。

- [ ] **Step 4: 手动验证**

在 `desktop:dev` 控制台调用 create/list，确认落库。

---

### Task 4: TodoPanel UI + 日历格子标记

**Files:**
- Create: `src/Calendar/TodoPanel.vue`
- Modify: `src/Calendar/index.vue`（右侧 `almanac-panel` 内、`almanac-body` 前或后插入；格子渲染加标记）

- [ ] **Step 1: 实现 `TodoPanel.vue`**

Props: `date`（`YYYY-MM-DD`）。功能：列表、勾选完成、编辑标题、删除确认、提醒时刻 `<input type="time">`、回车新增。样式用现有 CSS 变量（`--primary-color`、`--panel-bg`、`--border-color`、`--almanac-*`）。

- [ ] **Step 2: 挂到黄历侧栏**

在 `aside.almanac-panel` 的 `almanac-body` 之后插入：

```vue
<TodoPanel :date="selectedDate.format('YYYY-MM-DD')" />
```

- [ ] **Step 3: 月摘要标记**

`watch(currentMonth)` 拉取 `monthSummary`，在日历 cell 上显示未完成圆点/数量。

- [ ] **Step 4: 视觉回归**

对照参考版：主题色、黄历排版不被待办挤坏（待办区可滚动）。

---

### Task 5: 系统托盘与关闭进托盘

**Files:**
- Modify: `src-tauri/src/lib.rs`, `Cargo.toml`（`tray-icon`）, `tauri.conf.json`
- Asset: 托盘图标（可用 `public/logo.png` 复制到 `src-tauri/icons`）

- [ ] **Step 1: TrayIconBuilder**

左键切换主窗口显示；右键菜单：打开日历、今日待办（显示窗口）、提醒开关（发事件给前端或写 settings）、退出。

- [ ] **Step 2: 关闭拦截**

`on_window_event` CloseRequested → `api.prevent_close()` + `window.hide()`。

- [ ] **Step 3: 验证**

关窗后进程仍在；托盘可再打开；退出菜单结束进程。

---

### Task 6: 提醒扫描与系统通知

**Files:**
- Modify: `src-tauri/src/lib.rs`（或 `reminder.rs`）
- Modify: `src/services/settings-service.js`、设置 UI（可挂现有 Settings 面板）

- [ ] **Step 1: 后台定时（约 60s）**

用 `tokio`/`std::thread` 或 Tauri async：查询到期待办 → `NotificationExt` 发送 → `UPDATE todos SET notified=1`。尊重 `settings.reminders_enabled`。

- [ ] **Step 2: 修改 remind_at 时 `notified=0`**（在 `todo-service.updateTodo`）

- [ ] **Step 3: 设置项**

全局提醒开关 + 开机自启（`plugin-autostart`）。通知权限失败时设置页提示。

- [ ] **Step 4: 验证**

创建 1 分钟内提醒的待办，到期收到通知且不重复；改时间后可再提醒；完成后不提醒。

---

### Task 7: 桌面化清理与文档

**Files:**
- Modify: `src/App.vue`（桌面全铺，去掉大屏卡片居中或保留均可，以窗口内满版为准）
- Modify: `src/Calendar/index.vue`（存储：非 Tauri 时仍可用 localStorage 便于纯 Vite 预览；Tauri 下待办走 SQLite）
- Modify: `README.md`、`应用说明.txt`、`版本说明.txt`、`package.json`/`src/config.js` version bump
- Optional remove from delivery path: `public/plugin.json` 标注废弃或保留仅参考

- [ ] **Step 1: `isTauri` 检测** `!!window.__TAURI_INTERNALS__` 或 `@tauri-apps/api/core` `isTauri`

- [ ] **Step 2: 天气**：桌面端直连小米 API（无 CORS）；失败降级提示

- [ ] **Step 3: `npm run desktop:build` 产出安装包**

- [ ] **Step 4: 更新说明文档为 Windows 桌面版**

---

## Spec Coverage Check

| Spec 项 | Task |
|---------|------|
| Tauri 2 壳 | 2 |
| SQLite todos/settings | 3 |
| 按日待办 UI + 格子标记 | 4 |
| 托盘 + 关闭进托盘 | 5 |
| 通知 + notified 规则 | 6 |
| 对齐参考日历 / 去掉 uTools 交付 | 4, 7 |
| 不做桌面挂件 | （刻意不做） |

## Execution Notes

- 用户已指示「执行」：本会话 **Inline Execution**。
- 不在 `main` 上改：分支 `feature/windows-tauri-todos`。
- 提交仅在用户要求时创建（默认不自动 commit）。
