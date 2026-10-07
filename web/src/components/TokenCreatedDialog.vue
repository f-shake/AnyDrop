<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { copyText } from '@/clipboard'
import { useIsNarrow } from '@/viewport'

const props = defineProps<{ visible: boolean; tokenKey: string; tokenName: string }>()
const emit = defineEmits<{ (e: 'update:visible', value: boolean): void }>()

const copied = ref(false)
const isNarrow = useIsNarrow()
const dialogWidth = computed(() => (isNarrow.value ? '92vw' : '520px'))
const dialogVisible = computed({
  get: () => props.visible,
  set: (value: boolean) => emit('update:visible', value),
})

// 换了密钥就重置复制状态，否则第二把 key 一打开就显示「再复制一次」
watch(
  () => props.tokenKey,
  () => {
    copied.value = false
  },
)

async function copy() {
  // 局域网 http:// 下 navigator.clipboard 不存在，copyText 内部会退回 execCommand
  if (await copyText(props.tokenKey)) {
    copied.value = true
    ElMessage.success('已复制到剪贴板')
    return
  }
  // 密钥本身就常驻显示在弹窗里，所以这里给的是「手动选中」而不是一串会消失的提示
  ElMessage.warning('浏览器不允许自动复制，请在下面手动选中密钥')
}
</script>

<template>
  <el-dialog
    v-model="dialogVisible"
    title="密钥只显示这一次"
    :width="dialogWidth"
    :close-on-click-modal="false"
  >
    <el-alert type="warning" :closable="false" show-icon title="请立刻保存，关闭后无法再次查看，只能重新创建。" />
    <p class="anydrop-subtitle" style="margin: 12px 0 6px">Token：{{ tokenName }}</p>
    <div class="anydrop-key">{{ tokenKey }}</div>
    <template #footer>
      <el-button @click="copy">{{ copied ? '再复制一次' : '复制密钥' }}</el-button>
      <el-button type="primary" @click="dialogVisible = false">我已保存</el-button>
    </template>
  </el-dialog>
</template>
