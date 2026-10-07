import { readFileSync } from 'node:fs'
import { effectScope } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { NARROW_MAX_HEIGHT, NARROW_MAX_WIDTH, NARROW_QUERY, useIsNarrow } from '@/viewport'

type Listener = (event: { matches: boolean }) => void

/** node 环境没有 matchMedia，用最小替身观察 matches / 监听注册与摘除。 */
function fakeMatchMedia(initialMatches: boolean) {
  const listeners = new Set<Listener>()
  const removed: Listener[] = []
  const queries: string[] = []
  const media = {
    matches: initialMatches,
    media: '',
    addEventListener: (_type: 'change', listener: Listener) => {
      listeners.add(listener)
    },
    removeEventListener: (_type: 'change', listener: Listener) => {
      listeners.delete(listener)
      removed.push(listener)
    },
  }
  const matchMedia = vi.fn((query: string) => {
    queries.push(query)
    media.media = query
    return media
  })
  return {
    matchMedia,
    media,
    queries,
    removed,
    listenerCount: () => listeners.size,
    emit: (matches: boolean) => {
      for (const listener of [...listeners]) listener({ matches })
    },
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('useIsNarrow', () => {
  it('初始值取自 matchMedia 的 matches', () => {
    vi.stubGlobal('matchMedia', fakeMatchMedia(true).matchMedia)
    expect(useIsNarrow().value).toBe(true)

    vi.stubGlobal('matchMedia', fakeMatchMedia(false).matchMedia)
    expect(useIsNarrow().value).toBe(false)
  })

  it('查询串用唯一的断点原文（宽度 + 高度两条）', () => {
    const fake = fakeMatchMedia(false)
    vi.stubGlobal('matchMedia', fake.matchMedia)

    useIsNarrow()

    expect(fake.queries[0]).toBe(NARROW_QUERY)
    // 横屏要一起算窄屏：手机横屏宽度 >720，但高度只有 300 多
    expect(NARROW_QUERY).toContain(`(max-width: ${NARROW_MAX_WIDTH}px)`)
    expect(NARROW_QUERY).toContain(`(max-height: ${NARROW_MAX_HEIGHT}px)`)
  })

  it('窗口跨过断点时跟着变', () => {
    const fake = fakeMatchMedia(false)
    vi.stubGlobal('matchMedia', fake.matchMedia)
    const isNarrow = useIsNarrow()
    expect(isNarrow.value).toBe(false)

    fake.emit(true)
    expect(isNarrow.value).toBe(true)

    fake.emit(false)
    expect(isNarrow.value).toBe(false)
  })

  it('作用域停止后摘掉监听（组件卸载不泄漏）', () => {
    const fake = fakeMatchMedia(false)
    vi.stubGlobal('matchMedia', fake.matchMedia)
    const scope = effectScope()

    scope.run(() => useIsNarrow())
    expect(fake.listenerCount()).toBe(1)

    scope.stop()

    expect(fake.removed).toHaveLength(1)
    expect(fake.listenerCount()).toBe(0)
    // 摘掉之后再发事件也不该报错
    expect(() => fake.emit(true)).not.toThrow()
  })

  it('没有 matchMedia 时按宽屏处理且不抛异常', () => {
    vi.stubGlobal('matchMedia', undefined)

    expect(useIsNarrow().value).toBe(false)
  })
})

describe('断点一致性（JS 与 CSS 必须同源）', () => {
  const css = readFileSync(new URL('../src/styles/app.css', import.meta.url), 'utf8')

  it('app.css 里的媒体查询原文与 NARROW_QUERY 逐字一致', () => {
    // 断言整条 @media 原文，而不是「文件里出现过 720 这个数字」：
    // 后者随便哪个 max-width: 720px 都能满足，保护力不够。
    expect(css.replace(/\s+/g, ' ')).toContain(`@media ${NARROW_QUERY}`)
  })
})
