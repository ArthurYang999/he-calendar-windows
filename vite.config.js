import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [vue()],
  clearScreen: false,
  base: './',
  server: {
    strictPort: true,
    proxy: {
      // 代理小米天气 API，解决浏览器 CORS 跨域限制（纯网页预览用）
      '/api/weather': {
        target: 'https://weatherapi.market.xiaomi.com',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api\/weather/, '/wtr-v3'),
      },
    },
  },
  envPrefix: ['VITE_', 'TAURI_'],
  build: {
    target: process.env.TAURI_ENV_PLATFORM === 'windows' ? 'chrome105' : 'esnext',
    minify: !process.env.TAURI_ENV_DEBUG ? 'esbuild' : false,
    sourcemap: !!process.env.TAURI_ENV_DEBUG,
  },
})
