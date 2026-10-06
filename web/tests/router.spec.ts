import { describe, expect, it, vi } from 'vitest'
import { createMemoryHistory } from 'vue-router'
import { createAppRouter } from '@/router'

// 路由单测不加载真实视图（那会牵出 Element Plus 的 CSS，纯 Node 环境处理不了）
vi.mock('@/views/AdminView.vue', () => ({ default: { name: 'AdminViewStub', render: () => null } }))

describe('路由表', () => {
  it('/admin 被解析为具名路由', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/admin')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('admin')
    expect(router.currentRoute.value.matched).toHaveLength(1)
  })

  it('根路径重定向到 /admin', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/')
    await router.isReady()

    expect(router.currentRoute.value.path).toBe('/admin')
    expect(router.currentRoute.value.name).toBe('admin')
  })

  it('未知路径回落到 /admin', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/whatever/deep/path')
    await router.isReady()

    expect(router.currentRoute.value.path).toBe('/admin')
  })

  it('带 /drop 前缀时解析出的 href 含前缀', () => {
    const router = createAppRouter(createMemoryHistory('/drop/'))

    // 不能用 push 带前缀的路径来验证：兜底路由 `/:pathMatch(.*)*` 会把未知路径重定向到 /admin，
    // 前缀生效与否 matched/name 都一样。只有 href 会带上 base。
    // 注意：不 mount 应用时不能 await isReady()（初始导航由 app.use(router) 触发，这里永远不会 resolve）。
    expect(router.resolve('/admin').href).toBe('/drop/admin')
  })

  it('不带前缀时 href 不含前缀', () => {
    const router = createAppRouter(createMemoryHistory())

    expect(router.resolve('/admin').href).toBe('/admin')
  })
})
