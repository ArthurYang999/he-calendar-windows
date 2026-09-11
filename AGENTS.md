# AGENTS

本文件用于记录面向代理/自动化编码助手的仓库级约束，避免后续会话重复踩坑。

## uTools / Electron 兼容性约束

- `uTools` 内置 Electron / WebView 对部分新 CSS 特性支持不稳定，尤其是 `color-mix()`。
- 如果关键界面（尤其右侧黄历面板）依赖 `color-mix()` 生成边框、浅底色、高亮色、阴影，可能出现：网页版正常，但 uTools 插件内样式失效或明显退化。
- 对关键 UI 不要直接依赖运行时 `color-mix()`；优先在 JS 中预计算派生色，再通过 CSS 变量写回，例如在 `applyTheme()` 中生成 `--almanac-line`、`--almanac-gold`、`--almanac-board-bg` 等变量。
- 如果必须使用新 CSS 语法，先提供兼容回退，再做渐进增强。
- 每次涉及主题色、黄历右栏、uTools 专属视图的样式调整后，都必须同时验证：
  1. 普通浏览器 dev 页面
  2. uTools 插件开发模式页面
  3. `npm run build` 构建结果

## API / 运行时双环境约束

- 当前桌面版无外部天气等 CORS 敏感 API；若后续重新引入外部 HTTP API，须在封装层做运行时环境检测：
  `window.__TAURI__` / 桌面 WebView → 可直连；普通浏览器 → 走本地/边缘代理。
- 改动 API 层后需验证桌面端与（如保留）浏览器预览端。

## 版本发布检查清单

升级版本号时需同步以下 **5 个文件**，缺一不可：
- `package.json` → `version`
- `src/config.js` → `version`
- `版本说明.txt` → 新增版本条目
- `应用说明.txt` → 新功能描述（如功能有增减）
- `README.md` → 核心特色 / 技术栈 / 关键词（如功能有增减）

说明文档中应持续保留对原项目「合社日历」原作者 **阿裕Addyu** 的致谢与链接：
https://github.com/scutken/he-calendar

## 单文件分发（牛马日历.exe）

- WinUI + WebView2 **无法**打成真正零依赖的单 PE；对外「一个 exe」靠 `winui/NiuMa.Launcher` 嵌入 `payload.zip`，首次解压到 `%LocalAppData%\HeCalendar\NiuMaApp\`。
- 更新内嵌包后务必提高 `Program.cs` 中的 `PayloadVersion`，否则用户不会重新解压。
- 打 zip 前排除 `*.WebView2` / `EBWebView` 等运行时用户数据，避免锁文件导致打包失败。
