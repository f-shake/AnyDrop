import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { applyState, login, logout, refreshSession, session } from '@/session'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('会话状态', () => {
  beforeEach(() => {
    session.authenticated = false
    session.csrf = ''
    session.username = ''
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('登录成功后保存 csrf 与用户名', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ csrfToken: 'csrf-1', username: 'admin', expiresAt: '2026-01-01T00:00:00Z' })),
    )

    await login('admin', 'password-0123456789')

    expect(session.authenticated).toBe(true)
    expect(session.csrf).toBe('csrf-1')
    expect(session.username).toBe('admin')
  })

  it('登录失败时保持未登录', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse({ error: { code: 'invalid_credentials', message: '用户名或密码错误' } }, 401)),
    )

    await expect(login('admin', 'wrong')).rejects.toMatchObject({ code: 'invalid_credentials' })
    expect(session.authenticated).toBe(false)
    expect(session.csrf).toBe('')
  })

  it('刷新会话反映未登录状态', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ authenticated: false })))

    await refreshSession()

    expect(session.authenticated).toBe(false)
    expect(session.csrf).toBe('')
  })

  it('刷新会话在已登录时取回 csrf', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ authenticated: true, csrfToken: 'csrf-2' })))

    await refreshSession()

    expect(session.authenticated).toBe(true)
    expect(session.csrf).toBe('csrf-2')
  })

  it('退出登录调用后端并清空状态', async () => {
    session.authenticated = true
    session.csrf = 'csrf-1'
    const fetchMock = vi.fn(async () => jsonResponse({ status: 'logged_out' }))
    vi.stubGlobal('fetch', fetchMock)

    await logout()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBe('csrf-1')
    expect(session.authenticated).toBe(false)
    expect(session.csrf).toBe('')
  })

  it('未登录时退出不发请求', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    await logout()

    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('applyState 容忍字段缺失', () => {
    applyState({ authenticated: true })

    expect(session.authenticated).toBe(true)
    expect(session.csrf).toBe('')
  })
})
