import { getCurrentScope, onScopeDispose, ref, type Ref } from 'vue'

/**
 * 窄屏（手机）断点的**宽度**阈值，单位 px。
 * 媒体查询原文见 <see cref="NARROW_QUERY"/> —— `tests/viewport.spec.ts` 会读 `styles/app.css`
 * 原文核对，两边写岔了会直接在测试里红。
 */
export const NARROW_MAX_WIDTH = 720

/**
 * **高度**阈值：手机横屏时宽度会到 800+，但可用高度只剩 300 多，
 * 表格在那种窗口里同样没法用（横向滚动 + 固定右列压住内容），所以横屏也要走卡片列表。
 */
export const NARROW_MAX_HEIGHT = 480

/** JS 与 CSS 共用的**唯一**断点原文。改这里就必须同步改 `styles/app.css`。 */
export const NARROW_QUERY = `(max-width: ${NARROW_MAX_WIDTH}px), (max-height: ${NARROW_MAX_HEIGHT}px)`

function narrowQuery(): MediaQueryList | null {
  // matchMedia 在很老的浏览器和 node 测试环境里不存在；此时按宽屏处理，绝不影响桌面端
  if (typeof globalThis.matchMedia !== 'function') return null
  return globalThis.matchMedia(NARROW_QUERY)
}

/**
 * 响应式窄屏判定：窗口跨过断点时自动更新，组件卸载时自动摘掉监听。
 *
 * 为什么用 JS 而不是纯 CSS：窄屏要把 `el-table` 换成卡片列表 —— 两者 DOM 结构不同
 * （表格的固定右列在手机上会盖住内容），CSS 换不了结构。
 */
export function useIsNarrow(): Ref<boolean> {
  const query = narrowQuery()
  const isNarrow = ref(query?.matches ?? false)
  if (!query) return isNarrow

  const onChange = (event: MediaQueryListEvent) => {
    isNarrow.value = event.matches
  }
  query.addEventListener('change', onChange)
  // 测试里可能在 effectScope 之外直接调用，所以先确认有作用域再注册清理
  if (getCurrentScope()) onScopeDispose(() => query.removeEventListener('change', onChange))
  return isNarrow
}
