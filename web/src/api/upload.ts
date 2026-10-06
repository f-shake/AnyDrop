import { ApiError, apiUrl } from './client'
import type { ApiErrorBody, UploadResponse } from './types'

/** 用户主动取消（xhr.abort()）：与真正的失败区分开，UI 显示「已取消」而不是报错。 */
export class UploadAbortedError extends Error {
  constructor() {
    super('已取消')
    this.name = 'UploadAbortedError'
  }
}

export function isUploadAborted(error: unknown): boolean {
  return error instanceof UploadAbortedError
}

export interface UploadOptions {
  /** 管理会话的 CSRF 令牌，写操作必须带。 */
  csrf: string
  /** 部署前缀；不传则取 vite 的 BASE_URL。显式传入便于测试与非默认前缀场景。 */
  base?: string
  onProgress?: (percent: number) => void
}

export interface UploadTask {
  done: Promise<UploadResponse>
  abort: () => void
}

/**
 * 上传地址：文件名走 ?name=（URL 编码）。
 * 不能用 X-Filename 头——XHR 的请求头值必须是 ByteString，中文文件名会直接抛异常。
 */
export function uploadPath(fileName: string, base?: string): string {
  return apiUrl(`/api/admin/files?name=${encodeURIComponent(fileName)}`, base)
}

/** 非 2xx 响应体解析成 ApiError，与服务端错误信封保持一致（复用同一套 UI 文案）。 */
export function uploadErrorOf(status: number, body: string): ApiError {
  try {
    const parsed = JSON.parse(body) as ApiErrorBody
    if (parsed?.error?.code) return new ApiError(parsed.error.code, parsed.error.message, status)
  } catch {
    // 不是 JSON（反向代理常直接返回 HTML），走下面的兜底文案
  }
  if (status === 401) return new ApiError('invalid_token', '管理会话已失效，请重新登录', status)
  if (status === 403) return new ApiError('csrf_failed', 'CSRF 校验失败，请刷新页面后重试', status)
  if (status === 413) return new ApiError('file_too_large', '文件超过服务端上限', status)
  return new ApiError('unknown', `上传失败（HTTP ${status}）`, status)
}

/**
 * 用 XHR 而不是 fetch：只有 XHR 的 upload.onprogress 能拿到上传百分比，
 * 256 MiB 的单请求上传必须有进度反馈。
 */
export function uploadFile(file: File, options: UploadOptions): UploadTask {
  const xhr = new XMLHttpRequest()

  const done = new Promise<UploadResponse>((resolve, reject) => {
    xhr.open('POST', uploadPath(file.name, options.base), true)
    xhr.withCredentials = true
    xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
    xhr.setRequestHeader('X-CSRF-Token', options.csrf)

    xhr.upload.onprogress = (event: ProgressEvent) => {
      if (!event.lengthComputable || !options.onProgress) return
      options.onProgress(Math.floor((event.loaded / event.total) * 100))
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as UploadResponse)
        } catch {
          reject(new ApiError('unknown', '服务端返回了无法解析的响应', xhr.status))
        }
        return
      }
      reject(uploadErrorOf(xhr.status, xhr.responseText))
    }
    xhr.onerror = () => reject(new ApiError('network', '网络中断，上传失败', 0))
    xhr.ontimeout = () => reject(new ApiError('timeout', '上传超时', 0))
    xhr.onabort = () => reject(new UploadAbortedError())

    xhr.send(file)
  })

  return { done, abort: () => xhr.abort() }
}
