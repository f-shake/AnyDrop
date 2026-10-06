import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import AutoImport from 'unplugin-auto-import/vite'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'

// 开发时把 API 请求代理到本机服务；生产由服务器 nginx 反代。
const apiTarget = process.env.ANYDROP_DEV_TARGET ?? 'http://127.0.0.1:8790'

// 部署前缀必须与后端 server:pathBase 完全一致：改了 pathBase 就要用同一个值重新构建前端。
const base = process.env.ANYDROP_WEB_BASE ?? '/drop/'
const prefix = base.endsWith('/') ? base.slice(0, -1) : base

export default defineConfig({
  // 与部署形态一致：应用挂在 https://<域名>/drop/ 下
  base,
  plugins: [
    vue(),
    AutoImport({ resolvers: [ElementPlusResolver()], dts: 'src/auto-imports.d.ts' }),
    Components({ resolvers: [ElementPlusResolver()], dts: 'src/components.d.ts' }),
  ],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    // 代理键跟着 base 走：换前缀（ANYDROP_WEB_BASE）后本地 dev 才不会被代理前缀卡住
    proxy: {
      [`${prefix}/v1`]: apiTarget,
      [`${prefix}/api`]: apiTarget,
      [`${prefix}/f`]: apiTarget,
      [`${prefix}/healthz`]: apiTarget,
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  test: {
    environment: 'node',
    include: ['tests/**/*.spec.ts'],
  },
})
