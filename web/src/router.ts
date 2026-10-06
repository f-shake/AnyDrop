import { createRouter, createWebHistory, createMemoryHistory, type Router, type RouterHistory } from 'vue-router'

export const routes = [
  { path: '/', redirect: '/admin' },
  { path: '/admin', name: 'admin', component: () => import('@/views/AdminView.vue') },
  { path: '/:pathMatch(.*)*', redirect: '/admin' },
]

/** 应用挂在 /drop/ 下；非浏览器环境（单测）退回内存历史。 */
export function defaultHistory(): RouterHistory {
  return typeof window === 'undefined' ? createMemoryHistory() : createWebHistory('/drop/')
}

export function createAppRouter(history: RouterHistory = defaultHistory()): Router {
  return createRouter({ history, routes })
}

export const router = createAppRouter()
