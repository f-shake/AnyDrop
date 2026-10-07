import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { NARROW_QUERY } from '@/viewport'

/** 布局不变式只能靠读 CSS 原文来钉住：项目没有 jsdom，测不了真实排版。 */
const css = readFileSync(new URL('../src/styles/app.css', import.meta.url), 'utf8')
const normalized = css.replace(/\s+/g, ' ')

/** 取出某个选择器的规则体（第一个匹配），避免对属性顺序敏感。 */
function ruleBody(source: string, selector: string): string {
  const start = source.indexOf(`${selector} {`)
  if (start < 0) throw new Error(`app.css 里找不到规则：${selector}`)
  return source.slice(start, source.indexOf('}', start))
}

/** 窄屏那一段媒体查询（断点原文从 viewport.ts 取，不在这里再写一遍 720）。 */
const mobileBlock = css.slice(css.indexOf(`@media ${NARROW_QUERY}`))

describe('布局不变式（app.css）', () => {
  it('有全局 border-box 复位', () => {
    // 没有它时 .anydrop-login 的 width:100% 会把左右内边距算在容器宽度之外，
    // 卡片比屏幕宽 24px —— 手机上就是登录页横向溢出。
    expect(normalized).toContain('*, *::before, *::after { box-sizing: border-box; }')
  })

  it('卡片动作区不换行，并且抵消 Element Plus 的按钮左边距', () => {
    const actions = ruleBody(css, '.anydrop-item-actions')
    expect(actions).toContain('flex-wrap: nowrap')

    // EP 自带 .el-button + .el-button { margin-left: 12px }，会和 flex gap 叠加把这一排挤到换行
    expect(normalized).toContain('.anydrop-item-actions .el-button + .el-button { margin-left: 0; }')
  })

  it('搜索框宽度带父选择器（否则会被懒加载分块里的 .el-input 宽度盖掉）', () => {
    const search = ruleBody(css, '.anydrop-toolbar .anydrop-search')
    expect(search).toContain('width: 240px')
    // 同级 (0,1,0) 的 .anydrop-search 会输给 EP 后注入的 .el-input
    expect(css).not.toMatch(/\n\.anydrop-search \{/)
    expect(mobileBlock).toContain('.anydrop-toolbar .anydrop-search')
  })

  it('窄屏弹窗规则必须抬优先级（EP 的同名选择器在懒加载分块里、后加载）', () => {
    // 只写 `.el-dialog` 会输给 EP 分块里的同 (0,1,0) 规则 —— 连改 EP 变量也不行，
    // 因为 EP 把变量声明在同一个选择器上。必须靠 body 前缀抬到 (0,1,1)。
    expect(mobileBlock).toContain('body .el-dialog {')
    expect(mobileBlock).toContain('body .el-message-box {')
    expect(mobileBlock).not.toMatch(/\n\s*\.el-(dialog|message-box) \{/)
    // 这条 EP 自己没有，一定生效
    expect(mobileBlock).toContain('max-width: calc(100vw - 24px)')
    expect(mobileBlock).toContain('--el-dialog-margin-top: 8vh')
    expect(mobileBlock).toContain('--el-messagebox-width: calc(100vw - 32px)')
  })
})
