import { reactive } from 'vue'
import { api } from './api/client'
import type { AdminLoginResponse, SessionStateResponse } from './api/types'

export interface SessionState {
  authenticated: boolean
  csrf: string
  username: string
}

export const session = reactive<SessionState>({
  authenticated: false,
  csrf: '',
  username: '',
})

export function applyState(state: SessionStateResponse): SessionState {
  session.authenticated = state.authenticated === true
  session.csrf = state.csrfToken ?? ''
  return session
}

export async function refreshSession(): Promise<SessionState> {
  return applyState(await api<SessionStateResponse>('/api/admin/session'))
}

export async function login(username: string, password: string): Promise<SessionState> {
  const result = await api<AdminLoginResponse>('/api/admin/login', {
    method: 'POST',
    body: { username, password },
  })
  session.authenticated = true
  session.csrf = result.csrfToken
  session.username = result.username
  return session
}

export async function logout(): Promise<SessionState> {
  if (session.csrf) {
    await api('/api/admin/logout', { method: 'POST', csrf: session.csrf })
  }
  session.authenticated = false
  session.csrf = ''
  session.username = ''
  return session
}
