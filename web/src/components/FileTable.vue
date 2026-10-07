<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, apiUrl, formatTime, humanSize, messageOf } from '@/api/client'
import { copyText } from '@/clipboard'
import { session } from '@/session'
import type { FileInfoDto, FileListResponse } from '@/api/types'
import { useIsNarrow } from '@/viewport'

const items = ref<FileInfoDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(20)
const query = ref('')
const loading = ref(false)
const isNarrow = useIsNarrow()

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
  if (await copyText(row.url)) {
    ElMessage.success('直链已复制')
    return
  }
  // 连 execCommand 都用不了（极端权限策略）才走到这里。兜底不能用 ElMessage：
  // 它默认 3 秒就销毁（只有 hover 才暂停），用户来不及选中，触屏上更拿不到链接。
  // 改成占住屏幕、链接是可选中纯文本的弹窗。
  try {
    await ElMessageBox.alert(row.url, '请手动复制直链', { confirmButtonText: '知道了' })
  } catch {
    // 关掉弹窗即可，无需额外处理
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
      <el-input v-model="query" class="anydrop-search" placeholder="按文件名或 id 搜索" clearable @keyup.enter="reload" />
      <el-button @click="reload">搜索</el-button>
      <span class="anydrop-subtitle">共 {{ total }} 个文件</span>
    </div>

    <!-- 窄屏用卡片列表：el-table 的固定右列在手机上会盖住内容，操作按钮也挤不下 -->
    <ul v-if="isNarrow" v-loading="loading" class="anydrop-list">
      <li v-for="row in items" :key="row.id" class="anydrop-item">
        <div class="anydrop-item-head">
          <span class="anydrop-item-name" :title="row.name ?? row.id">{{ row.name ?? '(未命名)' }}</span>
          <span class="anydrop-subtitle">{{ humanSize(row.size) }}</span>
        </div>
        <!-- 手机卡片直接显示完整直链：这里是剪贴板最脆弱的场景（部分内核 execCommand 返回 true
             但剪贴板为空），必须留一个能手动长按选中的落点，不能只靠复制按钮 -->
        <div class="anydrop-mono anydrop-item-id" :title="row.url">{{ row.url }}</div>
        <div class="anydrop-item-meta">
          <span :title="row.createdAt">上传 {{ formatTime(row.createdAt) }}</span>
          <span :title="row.pinned ? undefined : row.expiresAt">
            {{ row.pinned ? '不过期' : `过期 ${formatTime(row.expiresAt)}` }}
          </span>
          <span>下载 {{ row.downloadCount }}</span>
        </div>
        <div class="anydrop-item-actions">
          <el-button size="small" @click="copyLink(row)">复制直链</el-button>
          <el-button size="small" @click="download(row)">下载</el-button>
          <el-button size="small" @click="openExpiry(row)">改期</el-button>
          <el-button size="small" @click="togglePin(row)">{{ row.pinned ? '取消保留' : '保留' }}</el-button>
          <el-button size="small" type="danger" plain @click="remove(row)">删除</el-button>
        </div>
      </li>
      <li v-if="items.length === 0" class="anydrop-empty">还没有文件</li>
    </ul>

    <el-table v-else v-loading="loading" :data="items" size="small" empty-text="还没有文件">
      <el-table-column label="文件名" min-width="200">
        <template #default="{ row }">
          <div>{{ row.name ?? '(未命名)' }}</div>
          <!-- 桌面端行里保留紧凑的 id（完整直链会把行撑高两三倍），完整直链在 title 里，
               复制失败时还有弹窗落点；手机卡片那边则直接显示完整直链。 -->
          <div class="anydrop-mono" :title="row.url">{{ row.id }}</div>
        </template>
      </el-table-column>
      <el-table-column label="大小" width="100">
        <template #default="{ row }">{{ humanSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="上传时间" width="160">
        <template #default="{ row }">
          <span :title="row.createdAt">{{ formatTime(row.createdAt) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="过期时间" width="160">
        <template #default="{ row }">
          <span v-if="row.pinned">不过期</span>
          <span v-else :title="row.expiresAt">{{ formatTime(row.expiresAt) }}</span>
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
      :layout="isNarrow ? 'prev, pager, next' : 'total, sizes, prev, pager, next'"
      :small="isNarrow"
      class="anydrop-pagination"
      @current-change="reload"
      @size-change="reload"
    />

    <el-dialog v-model="expiryVisible" title="修改过期时间" :width="isNarrow ? '92vw' : '360px'">
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

