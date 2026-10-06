<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, humanSize, messageOf } from '@/api/client'
import { session } from '@/session'
import type { CreateTokenResponse, TokenDto, TokenListResponse } from '@/api/types'
import TokenCreatedDialog from './TokenCreatedDialog.vue'

const items = ref<TokenDto[]>([])
const loading = ref(false)
const createVisible = ref(false)
const createdKey = ref('')
const createdName = ref('')
const createdVisible = ref(false)

const form = ref({
  name: '',
  preset: 'ai-write',
  namespace: 'default',
  ttlDays: 0,
  quotaGiB: 20,
  maxFileMiB: 256,
})

const presets = [
  { value: 'ai-write', label: 'ai-write：只写（AI 上传用）' },
  { value: 'me-read', label: 'me-read：只读（你自己取件）' },
  { value: 'nas-pull', label: 'nas-pull：读 + 删（NAS 按 id 取件后自删）' },
]

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
        preset: form.value.preset,
        namespace: form.value.namespace.trim(),
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
    await ElMessageBox.confirm(`撤销「${row.name}」后，用它上传或下载都会立刻失败。`, '撤销确认', {
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
      <strong>密钥</strong>
      <el-button type="primary" size="small" @click="createVisible = true">新建密钥</el-button>
    </div>

    <el-table v-loading="loading" :data="items" size="small" empty-text="还没有密钥">
      <el-table-column label="名称" min-width="140" prop="name" />
      <el-table-column label="前缀" width="110">
        <template #default="{ row }"><span class="anydrop-mono">{{ row.keyPrefix }}…</span></template>
      </el-table-column>
      <el-table-column label="命名空间" width="110" prop="namespace" />
      <el-table-column label="能力" width="150">
        <template #default="{ row }">
          <el-tag v-if="row.canUpload" size="small" type="success">写</el-tag>
          <el-tag v-if="row.canRead" size="small">读</el-tag>
          <el-tag v-if="row.canDelete" size="small" type="danger">删</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="用量 / 配额" width="170">
        <template #default="{ row }">{{ humanSize(row.usedBytes) }} / {{ humanSize(row.quotaBytes) }}</template>
      </el-table-column>
      <el-table-column label="单文件上限" width="110">
        <template #default="{ row }">{{ humanSize(row.maxFileBytes) }}</template>
      </el-table-column>
      <el-table-column label="最后使用" width="160">
        <template #default="{ row }">{{ row.lastUsedAt ?? '—' }}</template>
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

    <el-dialog v-model="createVisible" title="新建密钥" width="460px">
      <el-form label-position="top">
        <el-form-item label="名称（便于识别）">
          <el-input v-model="form.name" placeholder="例如 nas-ai-upload" />
        </el-form-item>
        <el-form-item label="预设能力">
          <el-select v-model="form.preset" style="width: 100%">
            <el-option v-for="item in presets" :key="item.value" :label="item.label" :value="item.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="命名空间（小写字母/数字/._-，最长 32）">
          <el-input v-model="form.namespace" />
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

