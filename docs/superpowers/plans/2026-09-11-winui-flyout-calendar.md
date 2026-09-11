# WinUI 3 任务栏浮层日历 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 WinUI 3 壳实现 Win11 任务栏日期浮层视觉替换，WebView2 承载现有 Vue 日历，默认「系统简约」黑白主题。

**Architecture:** WinUI 监听系统日历窗并移出屏幕，在时钟锚点显示无边框浮层；Vue 经 WebView2 渲染；待办/设置经 WebMessage 桥接 SQLite。Tauri 不再作为交付路径。

**Tech Stack:** WinUI 3、Windows App SDK、WebView2、Vue 3、SQLite、Win32 `SetWinEventHook`

**Spec:** `docs/superpowers/specs/2026-09-11-winui-flyout-calendar-design.md`

---

## File Structure

| Path | Responsibility |
|------|----------------|
| `winui/HeCalendar.Shell/` | WinUI 3 应用：浮层、钩子、托盘、通知、WebView2 |
| `winui/HeCalendar.Shell/Services/CalendarHook.cs` | 检测系统日历窗并移出屏幕 |
| `winui/HeCalendar.Shell/Services/TodoDb.cs` | SQLite 访问 |
| `winui/HeCalendar.Shell/Services/Bridge.cs` | WebView2 宿主 API |
| `src/Calendar/themes/system-minimal.js` 或主题表扩展 | 系统简约黑白主题 |
| `src/services/*-service.js` | 检测 WinUI bridge，优先走宿主 |
| `package.json` / 版本五件套 | 1.7.0，文档改为 WinUI 浮层交付 |

---

### Task 1: 安装 .NET 8 + WinUI 模板

- [ ] 安装 .NET 8 SDK
- [ ] `dotnet new install Microsoft.WindowsAppSDK.ProjectTemplates`
- [ ] 验证 `dotnet new winui3 -h` 可用

### Task 2: 创建 WinUI 壳项目

- [ ] 在 `winui/HeCalendar.Shell` 创建 WinUI 3 应用
- [ ] 无边框浮层窗口 ~360×560，启动不显示主窗，只建托盘
- [ ] WebView2 加载 `../dist/index.html`（开发可指 `http://localhost:5173`）

### Task 3: 系统日历钩子

- [ ] `SetWinEventHook` + 窗口类/标题匹配表（可配置）
- [ ] 检测到面板 → `SetWindowPos` 移出屏幕 → `ShowFlyout` 锚定时钟
- [ ] 失败降级：仅托盘可开浮层

### Task 4: SQLite + WebView Bridge

- [ ] 迁移/复用 todos、settings schema
- [ ] JS：`window.chrome.webview.postMessage` / 宿主对象
- [ ] 提醒定时器 + 通知

### Task 5: Vue「系统简约」主题 + 浮层布局

- [ ] 新增 `system-minimal` 主题，默认启用
- [ ] 跟随系统浅/深色；收紧浮层内边距
- [ ] `npm run build` 验证

### Task 6: 文档与版本 1.7.0

- [ ] 更新 README / 应用说明 / 版本说明 / config / package
- [ ] 标明 Tauri 路径废弃，交付改为 WinUI

### Task 7: 打包试跑

- [ ] `dotnet build` / 打包 MSIX 或自包含 exe
- [ ] 本机点任务栏日期验证浮层行为

---

## Execution Notes

- 用户已指示「执行」：本会话 Inline Execution。
- 分支：`feature/winui-flyout`
- 提交仅在用户要求时创建。
