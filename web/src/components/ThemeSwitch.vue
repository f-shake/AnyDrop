<script setup lang="ts">
import { computed } from 'vue'
import { setThemeMode, themeMode, type ThemeMode } from '@/theme'

const options: ReadonlyArray<{ value: ThemeMode; label: string }> = [
  { value: 'system', label: '跟随系统' },
  { value: 'light', label: '浅色' },
  { value: 'dark', label: '深色' },
]

const currentLabel = computed(
  () => options.find((item) => item.value === themeMode.value)?.label ?? '跟随系统',
)

function onCommand(value: string | number | object): void {
  setThemeMode(value as ThemeMode)
}
</script>

<template>
  <!-- 单个图标按钮：图标随当前模式变化，点开是三个显式选项（不做盲切换，免得看不出当前是什么） -->
  <el-dropdown trigger="click" @command="onCommand">
    <el-button circle :title="`主题：${currentLabel}（点击切换）`" aria-label="切换主题">
      <svg
        v-if="themeMode === 'light'"
        viewBox="0 0 24 24"
        width="15"
        height="15"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        aria-hidden="true"
      >
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4" />
      </svg>
      <svg
        v-else-if="themeMode === 'dark'"
        viewBox="0 0 24 24"
        width="15"
        height="15"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
      </svg>
      <svg
        v-else
        viewBox="0 0 24 24"
        width="15"
        height="15"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <rect x="2.5" y="4" width="19" height="13" rx="2" />
        <path d="M8 21h8M12 17v4" />
      </svg>
    </el-button>

    <template #dropdown>
      <el-dropdown-menu>
        <el-dropdown-item v-for="item in options" :key="item.value" :command="item.value">
          {{ item.label }}<span v-if="item.value === themeMode" class="anydrop-theme-current">✓</span>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>
