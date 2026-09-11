<script lang="ts" setup>
import { onMounted, ref } from 'vue';
import Calendar from './Calendar/index.vue'
import { isTauri } from './services/settings-service.js'

const route = ref('calendar')
const enterAction = ref({})
const isUtools = ref(false)
const isDesktop = ref(false)
const isFlyout = ref(false)

function detectFlyoutMode() {
  const params = new URLSearchParams(window.location.search)
  if (params.get('mode') === 'flyout') return true
  if (params.get('mode') === 'almanac') return true
  if (document.documentElement.dataset.shell === 'winui-flyout') return true
  if (document.documentElement.dataset.shell === 'winui-almanac') return true
  if (window.chrome?.webview) return true
  if (window.heCalendarShell?.isFlyout) return true
  return false
}

onMounted(() => {
  isFlyout.value = detectFlyoutMode()
  isDesktop.value = isTauri() || isFlyout.value

  if (isFlyout.value) {
    document.body.classList.add('is-flyout')
    document.documentElement.classList.add('is-flyout')
    const mode = new URLSearchParams(window.location.search).get('mode')
    if (mode === 'almanac' || document.documentElement.dataset.shell === 'winui-almanac') {
      document.body.classList.add('is-almanac-shell')
      document.documentElement.classList.add('is-almanac-shell')
      document.documentElement.dataset.shell = 'winui-almanac'
    } else {
      document.documentElement.dataset.shell = 'winui-flyout'
    }
  }
  if (isDesktop.value) {
    document.body.classList.add('is-desktop')
  }

  if (window.utools) {
    isUtools.value = true
    document.body.classList.add('is-utools')
    window.utools.onPluginEnter((action) => {
      route.value = action.code || 'calendar'
      enterAction.value = action
    })
  }
})
</script>

<template>
  <div
    class="app-container"
    :class="{ 'is-utools': isUtools, 'is-desktop': isDesktop, 'is-flyout': isFlyout }"
  >
    <Calendar :enterAction="enterAction"></Calendar>
  </div>
</template>

<style>
.app-container {
  width: 100vw;
  height: 100vh;
  overflow: hidden;
}

.app-container.is-desktop,
.app-container.is-flyout {
  display: block;
  padding: 0;
  background: var(--bg-color, #1c1c1c);
}

.app-container.is-desktop > *,
.app-container.is-flyout > * {
  max-width: none;
  max-height: none;
  width: 100%;
  height: 100%;
  border-radius: 0;
  box-shadow: none;
}

html.is-flyout,
body.is-flyout {
  margin: 0;
  padding: 0;
  background: var(--bg-color, #202020) !important;
  background-image: none !important;
}

.app-container.is-flyout {
  background: var(--bg-color, #202020);
}

.app-container.is-flyout > * {
  max-width: none;
  max-height: none;
  width: 100%;
  height: 100%;
  border-radius: 0 !important;
  box-shadow: none !important;
}

.app-container.is-flyout .calendar-header {
  padding: 4px 8px;
}

.app-container.is-flyout .year-month {
  font-size: clamp(0.65rem, 2.4vw, 0.82rem);
}

.app-container.is-flyout .day-cell {
  padding: 1px 0;
  border-radius: 3px;
}

.app-container.is-flyout .solar-day {
  font-size: 0.78rem;
  margin-bottom: 0;
}

.app-container.is-flyout .lunar-day {
  font-size: 0.5rem;
  line-height: 1.1;
}

.app-container.is-flyout .week-day {
  font-size: 0.62rem;
}

.app-container.is-flyout .holiday-tag {
  font-size: 0.5rem;
  top: 2px;
  right: 2px;
  padding: 0 2px;
}

.app-container.is-flyout .todo-dot {
  bottom: 2px;
  min-width: 6px;
  height: 6px;
  font-size: 0.45rem;
  line-height: 6px;
}

.app-container.is-flyout .todo-panel {
  max-height: none;
  flex: 1 1 auto;
  min-height: 120px;
}

.app-container.is-flyout .calendar-container {
  border-radius: 0;
  box-shadow: none;
  background-color: var(--bg-color);
}

@media (min-width: 1024px) {
  .app-container:not(.is-utools):not(.is-desktop):not(.is-flyout) {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 24px;
    box-sizing: border-box;
  }

  .app-container:not(.is-utools):not(.is-desktop):not(.is-flyout) > * {
    max-width: 1200px;
    max-height: 800px;
    width: 100%;
    height: 100%;
    border-radius: 16px;
    box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.25);
    overflow: hidden;
  }
}

@media (min-width: 768px) and (max-width: 1023px) {
  .app-container:not(.is-utools):not(.is-desktop):not(.is-flyout) {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 16px;
    box-sizing: border-box;
  }

  .app-container:not(.is-utools):not(.is-desktop):not(.is-flyout) > * {
    max-width: 95%;
    max-height: 95%;
    border-radius: 12px;
    box-shadow: 0 20px 40px -10px rgba(0, 0, 0, 0.2);
    overflow: hidden;
  }
}
</style>
