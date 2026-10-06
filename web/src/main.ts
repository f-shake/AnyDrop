import { createApp } from 'vue'
import App from './App.vue'
import { router } from './router'
// Element Plus 的暗色变量表：靠 <html class="dark"> 生效。
// 必须显式引入——按需引入只带各组件的样式，不带这套全局暗色变量。
import 'element-plus/theme-chalk/dark/css-vars.css'
import './styles/app.css'
import { initTheme } from './theme'

// 挂载前先定主题，尽量减少刷新时的白屏闪烁
initTheme()

createApp(App).use(router).mount('#app')
