/**
 * 复制文本到剪贴板。
 *
 * 为什么要两条路：`navigator.clipboard` **只在安全上下文里存在**（HTTPS，或 localhost）。
 * 用局域网 IP + 明文 http:// 打开管理页时它是 undefined —— 这正是本项目最常见的调试/使用姿势
 * （`http://192.168.0.100:5173/drop/admin`），于是标准 API 根本不可用。
 * 已废弃的 `document.execCommand('copy')` 在不安全上下文里仍然有效，所以拿它兜底。
 *
 * 返回是否成功；失败时由调用方给出「可手动选中」的落点，不要静默失败。
 */
export async function copyText(text: string): Promise<boolean> {
  const g = globalThis as ClipboardGlobals
  if (g.isSecureContext === true && g.navigator?.clipboard?.writeText) {
    try {
      await g.navigator.clipboard.writeText(text)
      return true
    } catch {
      // 安全上下文里也可能被权限策略拒绝，继续走下面的兜底
    }
  }
  return legacyCopy(text)
}

/**
 * 非安全上下文的兜底。必须在**用户手势的同步调用链**里执行，所以这里不 await 任何东西
 * （先 await 一个失败的 Promise 再调 execCommand，手势可能已经过期）。
 *
 * 整个函数体都在 try 里：契约是「返回 boolean，绝不抛」—— 调用方不再包 try/catch，
 * 一旦这里冒出 rejected promise，用户就会看到「点了没反应」。临时节点由 finally 兜底清理。
 */
function legacyCopy(text: string): boolean {
  const document = (globalThis as ClipboardGlobals).document
  if (!document?.body) return false

  let area: HTMLTextAreaElement | null = null
  let attached = false
  try {
    area = document.createElement('textarea')
    area.value = text
    // 只读避免移动端弹键盘；不能 display:none / visibility:hidden —— 那样选不中
    area.setAttribute('readonly', '')
    area.style.position = 'fixed'
    area.style.top = '0'
    area.style.left = '0'
    area.style.opacity = '0'
    document.body.appendChild(area)
    attached = true
    area.select()
    area.setSelectionRange(0, text.length)
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    // 只有在「确实挂上去过」时才移除：对没挂上的节点调 removeChild 会抛 NotFoundError，
    // 虽然能 catch，但别拿异常当控制流。
    if (area && attached) {
      try {
        document.body.removeChild(area)
      } catch {
        // 已经被移除或 DOM 已变：都不该影响返回值
      }
    }
  }
}

/** 受测环境（vitest 跑在 node 里）没有 DOM，所以一律从 globalThis 上取，便于测试替身。 */
type ClipboardGlobals = {
  isSecureContext?: boolean
  navigator?: { clipboard?: { writeText?: (text: string) => Promise<void> } }
  document?: Document
}
