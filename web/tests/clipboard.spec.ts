import { afterEach, describe, expect, it, vi } from 'vitest'
import { copyText } from '@/clipboard'

/** node 环境没有 DOM，用最小替身观察 createElement / appendChild / removeChild / execCommand 的调用。 */
type FakeArea = {
  tagName: string
  value: string
  style: Record<string, string>
  setAttribute: ReturnType<typeof vi.fn>
  select: ReturnType<typeof vi.fn>
  setSelectionRange: ReturnType<typeof vi.fn>
}

function fakeDocument(execCommand: (command: string) => boolean) {
  const appended: FakeArea[] = []
  const removed: FakeArea[] = []
  const document = {
    body: {
      appendChild: (node: FakeArea) => {
        appended.push(node)
        return node
      },
      removeChild: (node: FakeArea) => {
        removed.push(node)
        return node
      },
    },
    execCommand,
    createElement: (tagName: string): FakeArea => ({
      tagName,
      value: '',
      style: {},
      setAttribute: vi.fn(),
      select: vi.fn(),
      setSelectionRange: vi.fn(),
    }),
  }
  return { document: document as unknown as Document, appended, removed }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('copyText', () => {
  it('安全上下文下用标准 API，且不碰 DOM', async () => {
    const writeText = vi.fn(async () => {})
    const createElement = vi.fn()
    vi.stubGlobal('isSecureContext', true)
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubGlobal('document', { createElement } as unknown as Document)

    await expect(copyText('https://example.com/x')).resolves.toBe(true)

    expect(writeText).toHaveBeenCalledWith('https://example.com/x')
    expect(createElement).not.toHaveBeenCalled()
  })

  it('非安全上下文（局域网 http）直接用 execCommand，不看 navigator.clipboard', async () => {
    const execCommand = vi.fn(() => true)
    const { document, appended, removed } = fakeDocument(execCommand)
    vi.stubGlobal('isSecureContext', false)
    // 真实浏览器在非安全上下文里就是没有 clipboard 这个属性
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', document)

    await expect(copyText('直链')).resolves.toBe(true)

    expect(execCommand).toHaveBeenCalledWith('copy')
    expect(appended).toHaveLength(1)
    expect(appended[0]!.value).toBe('直链')
    expect(removed).toHaveLength(1)
  })

  it('安全上下文里标准 API 被拒绝时退回 execCommand', async () => {
    const execCommand = vi.fn(() => true)
    const { document } = fakeDocument(execCommand)
    vi.stubGlobal('isSecureContext', true)
    vi.stubGlobal('navigator', {
      clipboard: {
        writeText: vi.fn(async () => {
          throw new Error('NotAllowedError')
        }),
      },
    })
    vi.stubGlobal('document', document)

    await expect(copyText('x')).resolves.toBe(true)

    expect(execCommand).toHaveBeenCalledWith('copy')
  })

  it('两条路都失败时返回 false，由调用方给出可手动复制的落点', async () => {
    const { document } = fakeDocument(() => false)
    vi.stubGlobal('isSecureContext', false)
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', document)

    await expect(copyText('x')).resolves.toBe(false)
  })

  it('execCommand 抛异常时返回 false，且临时节点照样被移除', async () => {
    const { document, removed } = fakeDocument(() => {
      throw new Error('boom')
    })
    vi.stubGlobal('isSecureContext', false)
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', document)

    await expect(copyText('x')).resolves.toBe(false)

    expect(removed).toHaveLength(1)
  })

  it('拿不到 document 时返回 false 而不是抛异常', async () => {
    vi.stubGlobal('isSecureContext', false)
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', undefined)

    await expect(copyText('x')).resolves.toBe(false)
  })

  // 契约是「返回 boolean，绝不抛」：调用方（FileTable/UploadPanel/TokenCreatedDialog）都不再包 try/catch，
  // 一旦这里冒出 rejected promise，用户看到的就是「点了没反应」。
  it('createElement 抛异常时返回 false', async () => {
    vi.stubGlobal('isSecureContext', false)
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', {
      body: { appendChild: vi.fn(), removeChild: vi.fn() },
      createElement: () => {
        throw new Error('boom')
      },
      execCommand: vi.fn(() => true),
    } as unknown as Document)

    await expect(copyText('x')).resolves.toBe(false)
  })

  it('appendChild 抛异常时也返回 false，且不会去移除没挂上的节点', async () => {
    const removeChild = vi.fn()
    vi.stubGlobal('isSecureContext', false)
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', {
      body: {
        appendChild: () => {
          throw new Error('boom')
        },
        removeChild,
      },
      createElement: () => ({
        value: '',
        style: {},
        setAttribute: vi.fn(),
        select: vi.fn(),
        setSelectionRange: vi.fn(),
      }),
      execCommand: vi.fn(() => true),
    } as unknown as Document)

    await expect(copyText('x')).resolves.toBe(false)
    expect(removeChild).not.toHaveBeenCalled()
  })
})
