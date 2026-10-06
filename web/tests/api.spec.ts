import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api, apiUrl, apiWithBase, basePath, humanSize, normalizeBase, toApiError } from '@/api/client'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('apiUrl', () => {
  it('base 为根时路径原样返回', () => {
    expect(basePath()).toBe('')
    expect(apiUrl('/api/admin/files')).toBe('/api/admin/files')
  })

  it('补齐缺失的前导斜杠', () => {
    expect(apiUrl('api/admin/files')).toBe('/api/admin/files')
  })

  // 线上部署前缀是 /drop/，而 vitest 里 import.meta.env.BASE_URL 恒为 '/'，
  // 所以生产前缀分支必须靠显式传 base 才能测到。
  it.each([
    ['/drop/', '/drop/api/admin/tokens'],
    ['/drop', '/drop/api/admin/tokens'],
    ['/', '/api/admin/tokens'],
    ['', '/api/admin/tokens'],
  ])('base=%s 时拼出 %s', (base, expected) => {
    expect(apiUrl('/api/admin/tokens', base)).toBe(expected)
  })

  it('normalizeBase 去掉尾部斜杠并折叠根路径', () => {
    expect(normalizeBase('/drop/')).toBe('/drop')
    expect(normalizeBase('/drop')).toBe('/drop')
    expect(normalizeBase('/')).toBe('')
    expect(normalizeBase(undefined)).toBe('')
  })

  it('带 /drop 前缀时请求打到带前缀的地址', async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ items: [] }))
    vi.stubGlobal('fetch', fetchMock)

    await apiWithBase('/api/admin/tokens', '/drop/')

    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/drop/api/admin/tokens')
  })
})

describe('api 客户端', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('把错误信封解析成 ApiError', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ error: { code: 'scope_denied', message: '该密钥无权下载文件' } }, 403)),
    )

    const error = await api('/api/admin/files').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ code: 'scope_denied', status: 403, message: '该密钥无权下载文件' })
  })

  it('写操作带上 JSON 与 CSRF 头', async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ status: 'ok' }))
    vi.stubGlobal('fetch', fetchMock)

    await api('/api/admin/tokens', { method: 'POST', body: { name: 'x' }, csrf: 'csrf-token' })

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('/api/admin/tokens')
    expect(init.method).toBe('POST')
    expect(init.body).toBe('{"name":"x"}')
    const headers = init.headers as Record<string, string>
    expect(headers['X-CSRF-Token']).toBe('csrf-token')
    expect(headers['Content-Type']).toBe('application/json')
    expect(init.credentials).toBe('same-origin')
  })

  it('GET 不带请求体也不带 CSRF', async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ items: [] }))
    vi.stubGlobal('fetch', fetchMock)

    await api('/api/admin/tokens')

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(init.body).toBeUndefined()
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBeUndefined()
  })

  it('空响应体不抛解析错误', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 200 })))

    await expect(api('/api/session')).resolves.toBeUndefined()
  })

  it('非 JSON 错误响应有兜底文案', async () => {
    const error = await toApiError(new Response('<html>bad gateway</html>', { status: 502 }))

    expect(error.code).toBe('unknown')
    expect(error.status).toBe(502)
    expect(error.message).toContain('502')
  })
})

describe('humanSize', () => {
  it.each([
    [0, '0 B'],
    [1023, '1023 B'],
    [1024, '1.00 KiB'],
    [1536, '1.50 KiB'],
    [1024 * 1024, '1.00 MiB'],
    [1024 * 1024 * 1024, '1.00 GiB'],
  ])('%i → %s', (bytes, expected) => {
    expect(humanSize(bytes)).toBe(expected)
  })
})
