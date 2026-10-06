export interface ApiErrorBody {
  error: { code: string; message: string }
}

export interface AdminLoginResponse {
  csrfToken: string
  username: string
  expiresAt: string
}

export interface SessionStateResponse {
  authenticated: boolean
  kind?: string | null
  csrfToken?: string | null
}

export interface FileInfoDto {
  id: string
  /** 直链：{publicBaseUrl}/v1/blobs/{id}。人点开即下载，AI/脚本 curl 即取到字节。 */
  url: string
  name?: string | null
  size: number
  sha256: string
  contentType?: string | null
  tokenId?: string | null
  createdAt: string
  expiresAt: string
  pinned: boolean
  downloadCount: number
}

export interface FileListResponse {
  total: number
  page: number
  size: number
  items: FileInfoDto[]
}

export interface TokenDto {
  id: string
  name: string
  keyPrefix: string
  maxFileBytes: number
  quotaBytes: number
  usedBytes: number
  createdAt: string
  expiresAt?: string | null
  revokedAt?: string | null
  lastUsedAt?: string | null
}

export interface TokenListResponse {
  items: TokenDto[]
}

export interface CreateTokenRequest {
  name: string
  ttlDays?: number | null
  quotaBytes?: number | null
  maxFileBytes?: number | null
}

export interface CreateTokenResponse {
  token: TokenDto
  key: string
}

export interface AuditDto {
  id: number
  ts: string
  action: string
  tokenId?: string | null
  blobId?: string | null
  ip?: string | null
  bytes?: number | null
  detail?: string | null
}

export interface AuditListResponse {
  items: AuditDto[]
}

export interface SimpleStatusResponse {
  status: string
  id?: string | null
}

export interface UploadResponse {
  id: string
  url: string
  sha256: string
  size: number
  expiresAt: string
}
