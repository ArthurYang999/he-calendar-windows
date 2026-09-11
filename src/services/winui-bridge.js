/**
 * WinUI WebView2 宿主桥（postMessage RPC）
 */

const pending = new Map()
let seq = 0
let listening = false

export function isWinUiShell() {
  return typeof window !== 'undefined' && !!(window.chrome && window.chrome.webview)
}

function ensureListener() {
  if (listening || !isWinUiShell()) return
  listening = true
  window.chrome.webview.addEventListener('message', (event) => {
    try {
      const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data
      if (!msg || msg.id == null) return
      const entry = pending.get(String(msg.id))
      if (!entry) return
      pending.delete(String(msg.id))
      if (msg.ok === false) entry.reject(new Error(msg.error || 'bridge error'))
      else entry.resolve(msg.data)
    } catch (e) {
      console.warn('winui bridge message parse failed', e)
    }
  })
}

export function bridgeInvoke(type, payload = {}) {
  ensureListener()
  if (!isWinUiShell()) {
    return Promise.reject(new Error('not in WinUI WebView'))
  }
  const id = String(++seq)
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    window.chrome.webview.postMessage(JSON.stringify({ id, type, ...payload }))
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id)
        reject(new Error(`bridge timeout: ${type}`))
      }
    }, 8000)
  })
}
