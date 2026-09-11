# 合社日历 · Windows 浮层版

> 有温度的中国风日历🍵 — Win11 任务栏日期浮层（WinUI 3）
>
> 日历界面与农历/黄历能力基于开源项目 **[合社日历](https://github.com/scutken/he-calendar)**（原作者 **阿裕Addyu**）。
> 网页版样式参考：[https://cal.heshe.tech](https://cal.heshe.tech)

## 致谢

本仓库是合社日历在 Windows 上的任务栏浮层适配与增强，**特别感谢原作者 阿裕Addyu（Kayu Tse）** 开源的精美日历与黄历体验，以及社区贡献者（如 manymango 的月份切换动画等）。

- 原项目：[scutken/he-calendar](https://github.com/scutken/he-calendar)
- 本仓库（Windows）：[ArthurYang999/he-calendar-windows](https://github.com/ArthurYang999/he-calendar-windows)

## 应用介绍

在 Win11 上以**任务栏日期浮层**呈现：点击系统日期时，尽量盖住系统日历并显示月历 / 黄历 / 待办。默认「系统简约」黑白主题，更贴近系统观感。分发包可使用单文件启动器（对外常用名「牛马日历」）。

### 核心特色

- **Win11 日期浮层**：WinUI 3 壳 + WebView2，视觉替换系统日历面板
- **独立黄历窗**：黄历可向左侧滑出为独立窗口，主月历位置不变
- **系统简约主题**：默认黑白简约，可切换节气色主题
- **托盘右键**：主题、设置、退出；也可从托盘唤出浮层
- **按日待办**：选中日期增删改待办，格子显示未完成标记
- **待办提醒**：可设当天提醒时刻（壳层通知）
- **农历与黄历**：节气、传统节日、宜忌；头部显示当月工作日数
- **单文件分发**：`dist-desktop/牛马日历.exe` 首次运行解压到本地后启动（WinUI/WebView2 运行时仍需多文件）

### 开发与运行

前置：Node 20+、.NET 8 SDK、Windows 10/11 SDK（WinUI）。

```bash
npm install
npm run build
cd winui/HeCalendar.Shell
dotnet build -c Release -r win-x64 -p:Platform=x64
# 运行输出目录下的 HeCalendar.Shell.exe
```

试跑包目录：`dist-desktop/winui-flyout/`  
单文件启动器：`dist-desktop/牛马日历.exe`（内嵌应用包，首次解压到 `%LocalAppData%\HeCalendar\NiuMaApp\`）

纯前端预览（浮层模式）：`npm run dev` 后打开 `http://localhost:5173/?mode=flyout`

### 说明

- Win11 **没有**官方 API 真正替换系统日历；本方案为监听后移出系统面板并显示自有浮层（视觉替换）。
- 系统大版本后窗口识别规则可能需适配；钩子失败时仍可用托盘打开浮层。
- 旧 Tauri 桌面壳不再作为主交付路径。

## 技术栈

- Vue 3 + Vite（日历前端）
- WinUI 3 + WebView2（任务栏浮层壳）
- tyme4ts（农历 / 节气 / 黄历，6tail）
- Day.js、Lucide Vue Next

## 开源协议

MIT。请保留原作者与本仓库的版权与许可证声明。

## 版本说明

详见 [版本说明.txt](版本说明.txt) · [应用说明.txt](应用说明.txt)
