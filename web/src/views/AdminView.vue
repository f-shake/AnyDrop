<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { messageOf } from '@/api/client'
import { login, logout, refreshSession, session } from '@/session'
import FileTable from '@/components/FileTable.vue'
import TokenPanel from '@/components/TokenPanel.vue'

const username = ref('admin')
const password = ref('')
const busy = ref(false)
const ready = ref(false)
const fileTable = ref<{ reload: () => void } | null>(null)
const tokenPanel = ref<{ reload: () => void } | null>(null)

onMounted(async () => {
  try {
    await refreshSession()
  } catch (error) {
    ElMessage.error(messageOf(error))
  } finally {
    ready.value = true
  }
})

async function submit() {
  if (password.value.length === 0) {
    ElMessage.warning('请输入密码')
    return
  }
  busy.value = true
  try {
    await login(username.value, password.value)
    password.value = ''
    ElMessage.success('已登录')
    fileTable.value?.reload()
    tokenPanel.value?.reload()
  } catch (error) {
    ElMessage.error(messageOf(error))
  } finally {
    busy.value = false
  }
}

async function signOut() {
  try {
    await logout()
    ElMessage.success('已退出')
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

function refreshAll() {
  fileTable.value?.reload()
  tokenPanel.value?.reload()
}
</script>

<template>
  <div class="anydrop-shell">
    <div v-if="!ready" class="anydrop-card">加载中…</div>

    <div v-else-if="!session.authenticated" class="anydrop-card anydrop-login">
      <h1 class="anydrop-title">AnyDrop 管理</h1>
      <p class="anydrop-subtitle">公网文件交换服务</p>
      <el-form label-position="top" @submit.prevent="submit">
        <el-form-item label="用户名">
          <el-input v-model="username" autocomplete="username" />
        </el-form-item>
        <el-form-item label="密码">
          <el-input
            v-model="password"
            type="password"
            show-password
            autocomplete="current-password"
            @keyup.enter="submit"
          />
        </el-form-item>
        <el-button type="primary" :loading="busy" style="width: 100%" @click="submit">登录</el-button>
      </el-form>
    </div>

    <template v-else>
      <div class="anydrop-header">
        <div>
          <h1 class="anydrop-title">AnyDrop 管理</h1>
          <p class="anydrop-subtitle">当前用户：{{ session.username || 'admin' }}</p>
        </div>
        <div>
          <el-button @click="refreshAll">刷新</el-button>
          <el-button type="danger" plain @click="signOut">退出</el-button>
        </div>
      </div>
      <FileTable ref="fileTable" />
      <TokenPanel ref="tokenPanel" />
    </template>
  </div>
</template>
