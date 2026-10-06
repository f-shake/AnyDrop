import type { ApiErrorBody } from './types'

export class ApiError extends Error {
  readonly code: string
  readonly status: number

  constructor(code: string, message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
  }
}

/** 把 vite 的 base（形如 '/drop/'）规范成前缀：'/' → ''，'/drop/' → '/drop'。 */
export function normalizeBase(base: string | undefined): string {
  const value = base && base.length > 0 ? base : '/'
  if (value === '/') return ''
  return value.endsWith('/') ? value.slice(0, -1) : value
}

/** 应用挂在 /drop/ 下（vite base），API 调用要带上同一前缀。 */
export function basePath(): string {
  return normalizeBase(import.meta.env.BASE_URL)
}

export function apiUrl(path: string, base: string | undefined = import.meta.env.BASE_URL): string {
  const normalized = path.startsWith('/') ? path : `/${path}`
  return `${normalizeBase(base)}${normalized}`
}

export interface RequestOptions {
  method?: string
  body?: unknown
  csrf?: string
  signal?: AbortSignal
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  return apiWithBase<T>(path, import.meta.env.BASE_URL, options)
}

/** base 显式传入的版本，便于单测覆盖 /drop 前缀分支（vitest 下 BASE_URL 恒为 '/'）。 */
export async function apiWithBase<T>(
  path: string,
  base: string | undefined,
  options: RequestOptions = {},
): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const init: RequestInit = {
    method: options.method ?? 'GET',
    credentials: 'same-origin',
    headers,
  }
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    init.body = JSON.stringify(options.body)
  }
  if (options.csrf) headers['X-CSRF-Token'] = options.csrf
  if (options.signal) init.signal = options.signal

  const response = await fetch(apiUrl(path, base), init)
  if (!response.ok) throw await toApiError(response)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text.length > 0 ? JSON.parse(text) : undefined) as T
}

export async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as ApiErrorBody
    if (body?.error?.code) return new ApiError(body.error.code, body.error.message, response.status)
  } catch {
    // 不是 JSON 错误体，走下面的兜底
  }
  return new ApiError('unknown', `请求失败（HTTP ${response.status}）`, response.status)
}

export function messageOf(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return '未知错误'
}

export function humanSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes
  let unit = -1
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(value >= 10 ? 0 : 2)} ${units[unit]}`
}
