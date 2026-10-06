<script setup lang="ts">
// Element Plus 的组件内置文案（分页的“共 N 条 / N 条每页”、对话框关闭按钮的 aria-label 等）
// 取自 locale，默认是英文，必须显式注入中文，否则会混出 "Total 1" 这类英文。
// 用 ElConfigProvider 注入而不是 app.use(ElementPlus, { locale })：后者会全量注册组件，
// 丢掉按需引入。
import zhCn from 'element-plus/es/locale/lang/zh-cn'
</script>

<template>
  <el-config-provider :locale="zhCn">
    <router-view />
  </el-config-provider>
</template>
