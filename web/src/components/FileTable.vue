<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, apiUrl, humanSize, messageOf } from '@/api/client'
import { session } from '@/session'
import type { FileInfoDto, FileListResponse } from '@/api/types'

const items = ref<FileInfoDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(20)
const query = ref('')
const loading = ref(false)

const expiryVisible = ref(false)
const expiryDays = ref(30)
const expiryTarget = ref<FileInfoDto | null>(null)

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams({ page: String(page.value), size: String(size.value) })
    if (query.value.trim().length > 0) params.set('q', query.value.trim())
    const result = await api<FileListResponse>(`/api/admin/files?${params.toString()}`)
    items.value = result.items
    total.value = result.total
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

function download(row: FileInfoDto) {
  window.open(apiUrl(`/v1/blobs/${row.id}`), '_blank', 'noopener')
}

async function copyLink(row: FileInfoDto) {
  try {
    // 非安全上下文（局域网 http://）下 navigator.clipboard 不存在，直接抛异常走兜底
    await navigator.clipboard.writeText(row.url)
    ElMessage.success('直链已复制')
  } catch {
    // 兜底不能用 ElMessage：它默认 3 秒就销毁（只有 hover 才暂停），用户来不及选中，
    // 触屏上更是拿不到链接。改成占住屏幕、链接是可选中纯文本的弹窗。
    try {
      await ElMessageBox.alert(row.url, '请手动复制直链', { confirmButtonText: '知道了' })
    } catch {
      // 关掉弹窗即可，无需额外处理
    }
  }
}

async function remove(row: FileInfoDto) {
  try {
    await ElMessageBox.confirm(`确定删除「${row.name ?? row.id}」？删除后无法恢复。`, '删除确认', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }
  try {
    await api(`/api/admin/files/${row.id}`, { method: 'DELETE', csrf: session.csrf })
    ElMessage.success('已删除')
    await load()
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

async function togglePin(row: FileInfoDto) {
  try {
    await api(`/api/admin/files/${row.id}/pin`, {
      method: 'POST',
      csrf: session.csrf,
      body: { pinned: !row.pinned },
    })
    row.pinned = !row.pinned
    ElMessage.success(row.pinned ? '已设为不过期' : '已取消不过期')
  } catch (error) {
    ElMessage.error(messageOf(error))
  }
}

function openExpiry(row: FileInfoDto) {
  expiryTarget.value = row
  expiryDays.value = 30
  expiryVisible.value = true
}

async function submitExpiry() {
  if (expiryTarget.value === null) return
  try {
    await api(`/api/admin/files/${expiryTarget.value.id}/expiry`, {
      method: 'POST',
      csrf: session.csrf,
      body: { days: expiryDays.value },
    })
    ElMessage.success('已更新过期时间')
    expiryVisible.value = false
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
      <el-input v-model="query" placeholder="按文件名或 id 搜索" clearable style="width: 240px" @keyup.enter="reload" />
      <el-button @click="reload">搜索</el-button>
      <span class="anydrop-subtitle">共 {{ total }} 个文件</span>
    </div>

    <el-table v-loading="loading" :data="items" size="small" empty-text="还没有文件">
      <el-table-column label="文件名" min-width="200">
        <template #default="{ row }">
          <div>{{ row.name ?? '(未命名)' }}</div>
          <div class="anydrop-mono">{{ row.id }}</div>
        </template>
      </el-table-column>
      <el-table-column label="大小" width="100">
        <template #default="{ row }">{{ humanSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="上传时间" width="160" prop="createdAt" />
      <el-table-column label="过期时间" width="160">
        <template #default="{ row }">
          <span v-if="row.pinned">不过期</span>
          <span v-else>{{ row.expiresAt }}</span>
        </template>
      </el-table-column>
      <el-table-column label="下载" width="70" prop="downloadCount" />
      <el-table-column label="操作" width="310" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="copyLink(row as FileInfoDto)">复制直链</el-button>
          <el-button link type="primary" @click="download(row as FileInfoDto)">下载</el-button>
          <el-button link type="primary" @click="openExpiry(row as FileInfoDto)">改期</el-button>
          <el-button link type="primary" @click="togglePin(row as FileInfoDto)">{{ row.pinned ? '取消保留' : '保留' }}</el-button>
          <el-button link type="danger" @click="remove(row as FileInfoDto)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      v-model:page-size="size"
      :total="total"
      :page-sizes="[20, 50, 100]"
      layout="total, sizes, prev, pager, next"
      style="margin-top: 12px; justify-content: flex-end"
      @current-change="reload"
      @size-change="reload"
    />

    <el-dialog v-model="expiryVisible" title="修改过期时间" width="360px">
      <el-form label-position="top">
        <el-form-item label="从今天起保留天数（1-3650）">
          <el-input-number v-model="expiryDays" :min="1" :max="3650" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="expiryVisible = false">取消</el-button>
        <el-button type="primary" @click="submitExpiry">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

