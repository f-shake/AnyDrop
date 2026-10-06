import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import {
  UploadAbortedError,
  isUploadAborted,
  uploadErrorOf,
  uploadFile,
  uploadPath,
} from '@/api/upload'

type ProgressHandler = (event: { lengthComputable: boolean; loaded: number; total: number }) => void

/** node 环境没有 XMLHttpRequest，用最小替身把整条上传链路跑起来。 */
class FakeXhr {
  static instances: FakeXhr[] = []

  static get last(): FakeXhr {
    return FakeXhr.instances[FakeXhr.instances.length - 1]!
  }

  method = ''
  url = ''
  isAsync = false
  withCredentials = false
  headers: Record<string, string> = {}
  status = 0
  responseText = ''
  sentBody: unknown = null
  aborted = false
  upload: { onprogress?: ProgressHandler } = {}
  onload?: () => void
  onerror?: () => void
  onabort?: () => void
  ontimeout?: () => void

  constructor() {
    FakeXhr.instances.push(this)
  }

  open(method: string, url: string, isAsync: boolean): void {
    this.method = method
    this.url = url
    this.isAsync = isAsync
  }

  setRequestHeader(name: string, value: string): void {
    this.headers[name] = value
  }

  send(body: unknown): void {
    this.sentBody = body
  }

  abort(): void {
    this.aborted = true
    this.onabort?.()
  }

  emitProgress(loaded: number, total: number, lengthComputable = true): void {
    this.upload.onprogress?.({ lengthComputable, loaded, total })
  }

  finish(status: number, body: string): void {
    this.status = status
    this.responseText = body
    this.onload?.()
  }
}

const SAMPLE = {
  id: 'HGR02CNQ734ZQH038ARVMJ098M',
  url: 'http://192.168.0.100:8790/drop/f/HGR02CNQ734ZQH038ARVMJ098M',
  sha256: 'aa11',
  size: 3,
  expiresAt: '2026-11-05T13:06:32Z',
}

function makeFile(name = 'a.txt', type = 'text/plain'): File {
  return new File([new Uint8Array([1, 2, 3])], name, { type })
}

beforeEach(() => {
  FakeXhr.instances = []
  vi.stubGlobal('XMLHttpRequest', FakeXhr as unknown as typeof XMLHttpRequest)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('uploadPath', () => {
  it('文件名走 name 查询参数并做 URL 编码', () => {
    expect(uploadPath('a.txt', '/drop/')).toBe('/drop/api/admin/files?name=a.txt')
    expect(uploadPath('季度报告 2026.txt', '/drop/')).toBe(
      `/drop/api/admin/files?name=${encodeURIComponent('季度报告 2026.txt')}`,
    )
  })

  // 部署前缀可能被 ANYDROP_WEB_BASE 改掉，断言不依赖 vitest 下 BASE_URL 恒为 '/'
  it('不传 base 时用 vite 的 BASE_URL，显式传入时拼上部署前缀', () => {
    expect(uploadPath('a.txt')).toBe('/api/admin/files?name=a.txt')
    expect(uploadPath('a.txt', '/')).toBe('/api/admin/files?name=a.txt')
    expect(uploadPath('a.txt', '/drop')).toBe('/drop/api/admin/files?name=a.txt')
  })
})

describe('uploadFile', () => {
  it('发 POST、带 cookie 与 CSRF，正文就是文件本身', async () => {
    const file = makeFile()

    const task = uploadFile(file, { csrf: 'csrf-1', base: '/drop/' })
    const xhr = FakeXhr.last

    expect(xhr.method).toBe('POST')
    expect(xhr.url).toBe('/drop/api/admin/files?name=a.txt')
    expect(xhr.isAsync).toBe(true)
    expect(xhr.withCredentials).toBe(true)
    expect(xhr.headers['X-CSRF-Token']).toBe('csrf-1')
    expect(xhr.headers['Content-Type']).toBe('text/plain')
    expect(xhr.sentBody).toBe(file)

    xhr.finish(201, JSON.stringify(SAMPLE))
    await expect(task.done).resolves.toEqual(SAMPLE)
  })

  it('文件没有 MIME 类型时退回 application/octet-stream', () => {
    uploadFile(makeFile('a.bin', ''), { csrf: 'c' })

    expect(FakeXhr.last.headers['Content-Type']).toBe('application/octet-stream')
  })

  it('进度换算成整数百分比', async () => {
    const seen: number[] = []

    const task = uploadFile(makeFile(), { csrf: 'c', onProgress: (percent) => seen.push(percent) })
    const xhr = FakeXhr.last
    xhr.emitProgress(1, 4)
    xhr.emitProgress(3, 8)
    xhr.emitProgress(8, 8)

    expect(seen).toEqual([25, 37, 100])

    xhr.finish(201, JSON.stringify(SAMPLE))
    await task.done
  })

  it('长度不可知时不回报进度', async () => {
    const seen: number[] = []

    const task = uploadFile(makeFile(), { csrf: 'c', onProgress: (percent) => seen.push(percent) })
    const xhr = FakeXhr.last
    xhr.emitProgress(10, 0, false)

    expect(seen).toEqual([])

    xhr.finish(201, JSON.stringify(SAMPLE))
    await task.done
  })

  it('非 2xx 用服务端错误信封构造 ApiError', async () => {
    const task = uploadFile(makeFile(), { csrf: 'c' })

    FakeXhr.last.finish(403, JSON.stringify({ error: { code: 'csrf_failed', message: 'CSRF 校验失败，请刷新页面后重试' } }))

    await expect(task.done).rejects.toMatchObject({
      code: 'csrf_failed',
      status: 403,
      message: 'CSRF 校验失败，请刷新页面后重试',
    })
  })

  it('反向代理返回非 JSON 时走兜底文案', async () => {
    const task = uploadFile(makeFile(), { csrf: 'c' })

    FakeXhr.last.finish(413, '<html>Request Entity Too Large</html>')

    await expect(task.done).rejects.toMatchObject({ code: 'file_too_large', status: 413 })
  })

  it('2xx 但响应体不是 JSON 时给出明确错误', async () => {
    const task = uploadFile(makeFile(), { csrf: 'c' })

    FakeXhr.last.finish(201, 'not json')

    await expect(task.done).rejects.toMatchObject({ status: 201, message: '服务端返回了无法解析的响应' })
  })

  it('网络中断与超时各自有文案', async () => {
    const interrupted = uploadFile(makeFile(), { csrf: 'c' })
    FakeXhr.last.onerror?.()
    await expect(interrupted.done).rejects.toMatchObject({ code: 'network', message: '网络中断，上传失败' })

    const timedOut = uploadFile(makeFile(), { csrf: 'c' })
    FakeXhr.last.ontimeout?.()
    await expect(timedOut.done).rejects.toMatchObject({ code: 'timeout', message: '上传超时' })
  })

  it('abort 抛 UploadAbortedError，可被 isUploadAborted 识别', async () => {
    const task = uploadFile(makeFile(), { csrf: 'c' })

    task.abort()

    expect(FakeXhr.last.aborted).toBe(true)
    const error = await task.done.catch((e: unknown) => e)
    expect(error).toBeInstanceOf(UploadAbortedError)
    expect(isUploadAborted(error)).toBe(true)
    expect(isUploadAborted(new ApiError('x', 'y', 500))).toBe(false)
  })
})

describe('uploadErrorOf', () => {
  it('优先用错误信封里的 code 与文案', () => {
    const error = uploadErrorOf(507, JSON.stringify({ error: { code: 'disk_low', message: '服务器空间不足，暂时拒绝写入' } }))

    expect(error.code).toBe('disk_low')
    expect(error.message).toBe('服务器空间不足，暂时拒绝写入')
    expect(error.status).toBe(507)
  })

  it.each([
    [401, 'invalid_token', '管理会话已失效，请重新登录'],
    [403, 'csrf_failed', 'CSRF 校验失败，请刷新页面后重试'],
    [413, 'file_too_large', '文件超过服务端上限'],
  ])('非 JSON 的 %i 走兜底文案', (status, code, message) => {
    const error = uploadErrorOf(status, 'plain text')

    expect(error.code).toBe(code)
    expect(error.message).toBe(message)
  })

  it('未知状态码带出 HTTP 码', () => {
    expect(uploadErrorOf(500, 'plain text').message).toBe('上传失败（HTTP 500）')
  })
})
