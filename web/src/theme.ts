import { computed, ref } from 'vue'

/**
 * 主题模式：默认跟随系统，用户可显式锁定浅色/深色。
 * 状态放在模块级（全局单例），组件只读 themeMode / isDark，通过 setThemeMode 修改。
 */
export type ThemeMode = 'system' | 'light' | 'dark'

export const THEME_STORAGE_KEY = 'anydrop.theme'
export const DARK_CLASS = 'dark'

const mode = ref<ThemeMode>('system')
const systemPrefersDark = ref(false)
let systemListenerBound = false

/** 任何非法值（存储被写脏、老版本残留）都退回「跟随系统」。 */
export function normalizeMode(raw: unknown): ThemeMode {
  return raw === 'light' || raw === 'dark' ? raw : 'system'
}

/** 「跟随系统」看系统偏好，其余情况只看用户选择。 */
export function resolveIsDark(value: ThemeMode, systemDark: boolean): boolean {
  return value === 'system' ? systemDark : value === 'dark'
}

export const themeMode = computed<ThemeMode>(() => mode.value)
export const isDark = computed(() => resolveIsDark(mode.value, systemPrefersDark.value))

function hasMatchMedia(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
}

function querySystemDark(): boolean {
  return hasMatchMedia() ? window.matchMedia('(prefers-color-scheme: dark)').matches : false
}

function readStored(): ThemeMode {
  try {
    return typeof localStorage === 'undefined'
      ? 'system'
      : normalizeMode(localStorage.getItem(THEME_STORAGE_KEY))
  } catch {
    return 'system'
  }
}

function persist(value: ThemeMode): void {
  try {
    if (typeof localStorage === 'undefined') return
    // 「跟随系统」就是默认值，不留存储痕迹：以后换默认值不会被旧记录钉住
    if (value === 'system') localStorage.removeItem(THEME_STORAGE_KEY)
    else localStorage.setItem(THEME_STORAGE_KEY, value)
  } catch {
    // 隐私模式下 localStorage 可能直接抛异常：忽略，本次会话仍然生效
  }
}

function applyToDocument(): void {
  if (typeof document === 'undefined') return
  document.documentElement.classList.toggle(DARK_CLASS, isDark.value)
}

export function setThemeMode(next: ThemeMode): void {
  mode.value = normalizeMode(next)
  persist(mode.value)
  applyToDocument()
}

/** 由 main.ts 调用一次：读偏好、跟随系统变化、写 <html>。可重复调用（幂等）。 */
export function initTheme(): void {
  mode.value = readStored()
  systemPrefersDark.value = querySystemDark()
  applyToDocument()

  if (systemListenerBound || !hasMatchMedia()) return
  systemListenerBound = true
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (event) => {
    systemPrefersDark.value = event.matches
    applyToDocument()
  })
}
