<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, formatTime, humanSize, messageOf } from '@/api/client'
import { session } from '@/session'
import type { CreateTokenResponse, TokenDto, TokenListResponse } from '@/api/types'
import { useIsNarrow } from '@/viewport'
import TokenCreatedDialog from './TokenCreatedDialog.vue'

const items = ref<TokenDto[]>([])
const loading = ref(false)
const isNarrow = useIsNarrow()
const createVisible = ref(false)
const createdKey = ref('')
const createdName = ref('')
const createdVisible = ref(false)

const form = ref({
  name: '',
  ttlDays: 0,
  quotaGiB: 20,
  maxFileMiB: 256,
})

async function load() {
  loading.value = true
  try {
    const result = await api<TokenListResponse>('/api/admin/tokens')
    items.value = result.items
  } catch (error) {
    ElMessage.error(messageOf(error))
  } finally {
    loading.value = false
  }
}

function reload() {
  void load()
}

onMounted(load)

async function create() {
  if (form.value.name.trim().length === 0) {
    ElMessage.warning('请填写名称')
    return
  }
  try {
    const result = await api<CreateTokenResponse>('/api/admin/tokens', {
      method: 'POST',
      csrf: session.csrf,
      body: {
        name: form.value.name.trim(),
        ttlDays: form.value.ttlDays,
        quotaBytes: Math.round(form.value.quotaGiB * 1024 * 1024 * 1024),
        maxFileBytes: Math.round(form.value.maxFileMiB * 1024 * 1024),
      },
    })
    createdKey.value = result.key
    createdName.value = result.token.name
    createdVisible.value = true
    createVisible.value = false
    form.value.name = ''
    await load()
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

async function revoke(row: TokenDto) {
  try {
    await ElMessageBox.confirm(`撤销「${row.name}」后，用它上传会立刻失败。`, '撤销确认', {
      type: 'warning',
      confirmButtonText: '撤销',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }
  try {
    await api(`/api/admin/tokens/${row.id}/revoke`, { method: 'POST', csrf: session.csrf })
    ElMessage.success('已撤销')
    await load()
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

defineExpose({ reload })
</script>

<template>
  <div class="anydrop-card">
    <div class="anydrop-toolbar">
      <strong>上传密钥</strong>
      <el-button type="primary" size="small" @click="createVisible = true">新建上传密钥</el-button>
    </div>

    <!-- 窄屏用卡片列表：与 FileTable 同一个理由（表格固定右列在手机上盖住内容） -->
    <ul v-if="isNarrow" v-loading="loading" class="anydrop-list">
      <li v-for="row in items" :key="row.id" class="anydrop-item">
        <div class="anydrop-item-head">
          <span class="anydrop-item-name" :title="row.name">{{ row.name }}</span>
          <el-tag v-if="row.revokedAt" type="info" size="small">已撤销</el-tag>
          <el-tag v-else type="success" size="small">有效</el-tag>
        </div>
        <div class="anydrop-mono anydrop-item-id">{{ row.keyPrefix }}…</div>
        <div class="anydrop-item-meta">
          <span>用量 {{ humanSize(row.usedBytes) }} / {{ humanSize(row.quotaBytes) }}</span>
          <span>单文件 ≤ {{ humanSize(row.maxFileBytes) }}</span>
          <span :title="row.lastUsedAt ?? undefined">最后使用 {{ formatTime(row.lastUsedAt) }}</span>
        </div>
        <div class="anydrop-item-actions">
          <el-button size="small" type="danger" plain :disabled="!!row.revokedAt" @click="revoke(row)">撤销</el-button>
        </div>
      </li>
      <li v-if="items.length === 0" class="anydrop-empty">还没有上传密钥</li>
    </ul>

    <el-table v-else v-loading="loading" :data="items" size="small" empty-text="还没有上传密钥">
      <el-table-column label="名称" min-width="140" prop="name" />
      <el-table-column label="前缀" width="110">
        <template #default="{ row }"><span class="anydrop-mono">{{ row.keyPrefix }}…</span></template>
      </el-table-column>
      <el-table-column label="用量 / 配额" width="170">
        <template #default="{ row }">{{ humanSize(row.usedBytes) }} / {{ humanSize(row.quotaBytes) }}</template>
      </el-table-column>
      <el-table-column label="单文件上限" width="110">
        <template #default="{ row }">{{ humanSize(row.maxFileBytes) }}</template>
      </el-table-column>
      <el-table-column label="最后使用" width="160">
        <template #default="{ row }">
          <span :title="row.lastUsedAt ?? undefined">{{ formatTime(row.lastUsedAt) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag v-if="row.revokedAt" type="info" size="small">已撤销</el-tag>
          <el-tag v-else type="success" size="small">有效</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="90" fixed="right">
        <template #default="{ row }">
          <el-button link type="danger" :disabled="!!row.revokedAt" @click="revoke(row as TokenDto)">撤销</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="createVisible" title="新建上传密钥" :width="isNarrow ? '92vw' : '460px'">
      <el-form label-position="top">
        <el-form-item label="名称（便于识别）">
          <el-input v-model="form.name" placeholder="例如 nas-ai-upload" />
        </el-form-item>
        <el-form-item label="有效期（天，0 表示长期有效）">
          <el-input-number v-model="form.ttlDays" :min="0" :max="3650" />
        </el-form-item>
        <el-form-item label="配额（GiB）">
          <el-input-number v-model="form.quotaGiB" :min="1" :max="10240" />
        </el-form-item>
        <el-form-item label="单文件上限（MiB）">
          <el-input-number v-model="form.maxFileMiB" :min="1" :max="256" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" @click="create">创建</el-button>
      </template>
    </el-dialog>

    <TokenCreatedDialog v-model:visible="createdVisible" :token-key="createdKey" :token-name="createdName" />
  </div>
</template>

