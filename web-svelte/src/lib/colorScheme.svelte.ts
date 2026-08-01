/**
 * 应用配色方案（书架等 MD3 界面，与阅读器正文主题相互独立）：
 * 'auto' 跟随系统 prefers-color-scheme，'light'/'dark' 手动指定。
 * 通过 html[data-color-scheme] 属性驱动 app.css 里的令牌切换。
 */

export type ColorScheme = 'auto' | 'light' | 'dark'

const STORAGE_KEY = 'colorScheme'

const stored = localStorage.getItem(STORAGE_KEY)
const initial: ColorScheme =
  stored === 'light' || stored === 'dark' ? stored : 'auto'

export const colorScheme = $state<{ mode: ColorScheme }>({ mode: initial })

const systemDark = window.matchMedia('(prefers-color-scheme: dark)')

/** 当前实际生效的是否深色（考虑 auto 跟随系统） */
export const isDark = (): boolean =>
  colorScheme.mode === 'dark' || (colorScheme.mode === 'auto' && systemDark.matches)

const apply = () => {
  document.documentElement.setAttribute('data-color-scheme', colorScheme.mode)
  // 手机浏览器状态栏配色跟随（app.css 令牌：浅 #f7fbf1 / 深 #101410）
  document
    .querySelector('meta[name="theme-color"]')
    ?.setAttribute('content', isDark() ? '#101410' : '#f7fbf1')
}

/** 三态循环：auto → dark → light → auto */
export const cycleColorScheme = () => {
  const order: ColorScheme[] = ['auto', 'dark', 'light']
  colorScheme.mode = order[(order.indexOf(colorScheme.mode) + 1) % order.length]
  if (colorScheme.mode === 'auto') localStorage.removeItem(STORAGE_KEY)
  else localStorage.setItem(STORAGE_KEY, colorScheme.mode)
  apply()
}

// 初始化 + auto 模式下跟随系统切换实时更新状态栏色
apply()
systemDark.addEventListener('change', apply)
