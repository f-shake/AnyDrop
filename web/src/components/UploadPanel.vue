<script setup lang="ts">
import { onUnmounted, ref } from 'vue'
import { ApiError, humanSize, messageOf } from '@/api/client'
import { isUploadAborted, uploadFile, type UploadTask } from '@/api/upload'
import { copyText } from '@/clipboard'
import type { UploadResponse } from '@/api/types'
import { session } from '@/session'

type ItemStatus = 'pending' | 'uploading' | 'done' | 'failed' | 'canceled'

interface QueueItem {
  key: number
  file: File
  status: ItemStatus
  percent: number
  error: string
  result?: UploadResponse
  abort?: () => void
}

const emit = defineEmits<{ uploaded: []; unauthorized: [] }>()

const items = ref<QueueItem[]>([])
const dragging = ref(false)
const input = ref<HTMLInputElement | null>(null)

let seq = 0
let pumping = false
let stopped = false

onUnmounted(() => {
  // 组件销毁（例如会话失效后 AdminView 切回登录表单）必须收手：否则 pump 的循环会
  // 继续用已卸载组件的状态发起新上传——用户看不到任何界面，上行带宽却还在被占。
  stopped = true
  for (const item of items.value) item.abort?.()
})

const statusLabels: Record<ItemStatus, string> = {
  pending: '等待',
  uploading: '上传中',
  done: '已完成',
  failed: '失败',
  canceled: '已取消',
}

const statusTags: Record<ItemStatus, 'info' | 'primary' | 'success' | 'danger' | 'warning'> = {
  pending: 'info',
  uploading: 'primary',
  done: 'success',
  failed: 'danger',
  canceled: 'warning',
}

function pick(): void {
  input.value?.click()
}

function onPick(event: Event): void {
  const target = event.target as HTMLInputElement
  if (target.files) enqueue(Array.from(target.files))
  // 清空后，同一个文件再选一次也能触发 change
  target.value = ''
}

function onDrop(event: DragEvent): void {
  dragging.value = false
  if (event.dataTransfer?.files) enqueue(Array.from(event.dataTransfer.files))
}

function enqueue(files: File[]): void {
  for (const file of files) {
    items.value.push({ key: ++seq, file, status: 'pending', percent: 0, error: '' })
  }
  void pump()
}

/**
 * 串行上传：服务端只留了 4 个上传槽，多文件并发会互相挤；
 * 而且串行时进度条的顺序和人选文件的顺序一致，更好读。
 * 循环每轮重新找 pending，所以上传途中新加入的文件也会被这一轮带走。
 */
async function pump(): Promise<void> {
  if (pumping) return
  pumping = true
  let uploadedAny = false
  try {
    for (;;) {
      if (stopped) break
      const next = items.value.find((item) => item.status === 'pending')
      if (!next) break
      if (await runOne(next)) uploadedAny = true
    }
  } finally {
    pumping = false
    if (uploadedAny && !stopped) emit('uploaded')
  }
}

async function runOne(item: QueueItem): Promise<boolean> {
  item.status = 'uploading'
  item.percent = 0
  item.error = ''
  let task: UploadTask
  try {
    task = uploadFile(item.file, {
      csrf: session.csrf,
      onProgress: (percent) => {
        item.percent = percent
      },
    })
  } catch (error) {
    // uploadFile 同步抛错（运行环境没有 XMLHttpRequest 之类）也必须让这一项落地成失败：
    // 否则它会永远停在「上传中」，异常还会冲出 pump 让整个队列静默停摆。
    item.status = 'failed'
    item.error = messageOf(error)
    return false
  }
  item.abort = task.abort
  try {
    item.result = await task.done
    item.percent = 100
    item.status = 'done'
    return true
  } catch (error) {
    if (isUploadAborted(error)) {
      item.status = 'canceled'
    } else {
      item.status = 'failed'
      item.error = messageOf(error)
      if (error instanceof ApiError && error.status === 401) emit('unauthorized')
    }
    return false
  } finally {
    item.abort = undefined
  }
}

function cancel(item: QueueItem): void {
  item.abort?.()
}

function retry(item: QueueItem): void {
  item.status = 'pending'
  item.error = ''
  void pump()
}

function clearFinished(): void {
  items.value = items.value.filter((item) => item.status === 'pending' || item.status === 'uploading')
}

async function copyLink(item: QueueItem): Promise<void> {
  if (!item.result) return
  if (await copyText(item.result.url)) {
    ElMessage.success('直链已复制')
    return
  }
  // 这一条下面常驻显示着同一个 url，所以文案可以说「下面」；链接不会随 toast 消失
  ElMessage.warning('浏览器不允许自动复制，请手动复制下面显示的直链')
}
</script>

<template>
  <div class="anydrop-card">
    <div class="anydrop-toolbar">
      <strong>上传文件</strong>
      <span class="anydrop-hint">可多选；超出服务端上限的文件会被拒绝并给出上限值</span>
      <el-button v-if="items.length > 0" size="small" text @click="clearFinished">清理已结束</el-button>
    </div>

    <div
      class="anydrop-dropzone"
      :class="{ 'anydrop-dropzone--active': dragging }"
      role="button"
      tabindex="0"
      @click="pick"
      @keydown.enter.prevent="pick"
      @keydown.space.prevent="pick"
      @dragover.prevent="dragging = true"
      @dragleave.prevent="dragging = false"
      @drop.prevent="onDrop"
    >
      拖拽文件到这里，或点击选择
    </div>
    <input ref="input" type="file" multiple hidden @change="onPick" />

    <ul v-if="items.length > 0" class="anydrop-uploads">
      <li v-for="item in items" :key="item.key" class="anydrop-upload">
        <div class="anydrop-upload-row">
          <span class="anydrop-upload-name" :title="item.file.name">{{ item.file.name }}</span>
          <span class="anydrop-upload-size">{{ humanSize(item.file.size) }}</span>
          <el-tag :type="statusTags[item.status]" size="small">{{ statusLabels[item.status] }}</el-tag>
          <el-button v-if="item.status === 'uploading'" size="small" text @click="cancel(item)">取消</el-button>
          <el-button
            v-else-if="item.status === 'failed' || item.status === 'canceled'"
            size="small"
            text
            @click="retry(item)"
          >
            重试
          </el-button>
          <el-button v-else-if="item.status === 'done'" size="small" text @click="copyLink(item)">
            复制直链
          </el-button>
        </div>
        <el-progress v-if="item.status === 'uploading'" :percentage="item.percent" :stroke-width="6" />
        <p v-if="item.error" class="anydrop-upload-error">{{ item.error }}</p>
        <p v-if="item.result" class="anydrop-upload-link anydrop-mono">{{ item.result.url }}</p>
      </li>
    </ul>
  </div>
</template>
