import { afterEach, describe, expect, it, vi } from 'vitest'
import { normalizeMode, resolveIsDark } from '@/theme'

type SystemListener = (event: { matches: boolean }) => void

/** node 环境下没有 window/document/localStorage，这里造一份最小的替身。 */
function stubBrowser(options: { stored?: string | null; systemDark?: boolean } = {}) {
  const classes = new Set<string>()
  const stored = new Map<string, string>()
  if (options.stored) stored.set('anydrop.theme', options.stored)
  const listeners: SystemListener[] = []
  let systemDark = options.systemDark ?? false

  const media = {
    get matches() {
      return systemDark
    },
    addEventListener: (_type: string, listener: SystemListener) => {
      listeners.push(listener)
    },
  }

  vi.stubGlobal('window', { matchMedia: () => media })
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => {
      stored.set(key, value)
    },
    removeItem: (key: string) => {
      stored.delete(key)
    },
  })
  vi.stubGlobal('document', {
    documentElement: {
      classList: {
        toggle: (name: string, force?: boolean) => {
          if (force) classes.add(name)
          else classes.delete(name)
        },
      },
    },
  })

  return {
    classes,
    stored,
    listeners,
    setSystemDark(next: boolean) {
      systemDark = next
      for (const listener of listeners) listener({ matches: next })
    },
  }
}

/** theme.ts 是模块级单例，测状态必须拿全新实例。 */
async function freshTheme() {
  vi.resetModules()
  return await import('@/theme')
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('normalizeMode', () => {
  it.each([
    ['system', 'system'],
    ['light', 'light'],
    ['dark', 'dark'],
  ] as const)('保留合法值 %s', (raw, expected) => {
    expect(normalizeMode(raw)).toBe(expected)
  })

  it.each([[null], [undefined], [''], ['chartreuse'], ['DARK'], [1], [{}]])(
    '非法值 %s 一律退回跟随系统',
    (raw) => {
      expect(normalizeMode(raw)).toBe('system')
    },
  )
})

describe('resolveIsDark', () => {
  it.each([
    ['system', true, true],
    ['system', false, false],
    ['dark', true, true],
    ['dark', false, true],
    ['light', true, false],
    ['light', false, false],
  ] as const)('mode=%s 系统深色=%s → %s', (mode, systemDark, expected) => {
    expect(resolveIsDark(mode, systemDark)).toBe(expected)
  })
})

describe('initTheme', () => {
  it('默认跟随系统：系统为深色就挂 dark 类', async () => {
    const env = stubBrowser({ systemDark: true })
    const theme = await freshTheme()

    theme.initTheme()

    expect(theme.themeMode.value).toBe('system')
    expect(theme.isDark.value).toBe(true)
    expect(env.classes.has('dark')).toBe(true)
  })

  it('默认跟随系统：系统为浅色就不挂 dark 类', async () => {
    const env = stubBrowser({ systemDark: false })
    const theme = await freshTheme()

    theme.initTheme()

    expect(theme.isDark.value).toBe(false)
    expect(env.classes.has('dark')).toBe(false)
  })

  it('记住用户选择：存储 dark 时忽略系统浅色', async () => {
    const env = stubBrowser({ stored: 'dark', systemDark: false })
    const theme = await freshTheme()

    theme.initTheme()

    expect(theme.themeMode.value).toBe('dark')
    expect(theme.isDark.value).toBe(true)
    expect(env.classes.has('dark')).toBe(true)
  })

  it('存储被写脏时退回跟随系统', async () => {
    const env = stubBrowser({ stored: 'chartreuse', systemDark: true })
    const theme = await freshTheme()

    theme.initTheme()

    expect(theme.themeMode.value).toBe('system')
    expect(theme.isDark.value).toBe(true)
    expect(env.classes.has('dark')).toBe(true)
  })

  it('跟随系统时，系统主题变化会实时同步到 dark 类', async () => {
    const env = stubBrowser({ systemDark: false })
    const theme = await freshTheme()
    theme.initTheme()

    env.setSystemDark(true)

    expect(theme.isDark.value).toBe(true)
    expect(env.classes.has('dark')).toBe(true)
  })

  it('用户显式选了浅色时，系统转深色也不跟随', async () => {
    const env = stubBrowser({ stored: 'light', systemDark: false })
    const theme = await freshTheme()
    theme.initTheme()

    env.setSystemDark(true)

    expect(theme.isDark.value).toBe(false)
    expect(env.classes.has('dark')).toBe(false)
  })

  it('幂等：重复调用只绑定一次系统监听', async () => {
    const env = stubBrowser({ systemDark: false })
    const theme = await freshTheme()

    theme.initTheme()
    theme.initTheme()

    expect(env.listeners).toHaveLength(1)
  })
})

describe('setThemeMode', () => {
  it('切到深色：写存储并挂 dark 类', async () => {
    const env = stubBrowser({ systemDark: false })
    const theme = await freshTheme()
    theme.initTheme()

    theme.setThemeMode('dark')

    expect(theme.isDark.value).toBe(true)
    expect(env.classes.has('dark')).toBe(true)
    expect(env.stored.get('anydrop.theme')).toBe('dark')
  })

  it('切到浅色：写存储并摘掉 dark 类', async () => {
    const env = stubBrowser({ stored: 'dark', systemDark: true })
    const theme = await freshTheme()
    theme.initTheme()

    theme.setThemeMode('light')

    expect(theme.isDark.value).toBe(false)
    expect(env.classes.has('dark')).toBe(false)
    expect(env.stored.get('anydrop.theme')).toBe('light')
  })

  it('切回跟随系统：清掉存储，改由系统偏好决定', async () => {
    const env = stubBrowser({ stored: 'dark', systemDark: true })
    const theme = await freshTheme()
    theme.initTheme()

    theme.setThemeMode('system')

    expect(env.stored.has('anydrop.theme')).toBe(false)
    expect(theme.isDark.value).toBe(true)

    env.setSystemDark(false)
    expect(theme.isDark.value).toBe(false)
  })

  it('传入非法值等同跟随系统', async () => {
    const env = stubBrowser({ systemDark: false })
    const theme = await freshTheme()
    theme.initTheme()

    theme.setThemeMode('neon' as never)

    expect(theme.themeMode.value).toBe('system')
    expect(env.stored.has('anydrop.theme')).toBe(false)
  })
})
