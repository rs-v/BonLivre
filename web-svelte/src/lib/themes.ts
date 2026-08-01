/** 阅读主题：序号与旧 Vue 前端（legado web）保持一致，6 为夜间 */
export type ReaderTheme = {
  name: string
  body: string
  content: string
  text: string
}

export const themes: ReaderTheme[] = [
  { name: '默认', body: '#ebe6d9', content: '#faf5eb', text: '#2b2b2b' },
  { name: '米黄', body: '#e5dcc3', content: '#f5eacc', text: '#3a3222' },
  { name: '淡绿', body: '#cfe0cd', content: '#e6f2e6', text: '#2b3a2b' },
  { name: '浅蓝', body: '#c9d8e4', content: '#e4f1f5', text: '#243240' },
  { name: '浅粉', body: '#ebcece', content: '#f5e4e4', text: '#3d2f24' },
  { name: '素灰', body: '#d4d4d4', content: '#e0e0e0', text: '#2b2b2b' },
  { name: '夜间', body: '#0f1112', content: '#161819', text: '#8a8a8a' },
]

export const themeAt = (index: number): ReaderTheme =>
  themes[index] ?? themes[0]

export const NIGHT_THEME_INDEX = 6

/** 预置字体：family 为字体栈，stylesheet 为网络字体样式表（fontsource CDN，unicode-range 分片按需下载） */
export type WebFont = {
  name: string
  family: string
  stylesheet?: string
}

export const fonts: WebFont[] = [
  {
    name: '雅黑',
    family:
      '"Noto Sans SC", "Microsoft YaHei", "PingFang SC", "HarmonyOS Sans SC", sans-serif',
    stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/noto-sans-sc@5/400.css',
  },
  {
    name: '宋体',
    family: '"Noto Serif SC", "SimSun", "Songti SC", "STSong", serif',
    stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/noto-serif-sc@5/400.css',
  },
  {
    name: '楷书',
    family: '"LXGW WenKai", "Kaiti SC", "STKaiti", "KaiTi", "楷体", serif',
    stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/lxgw-wenkai@5/300.css',
  },
]

// 已注入的字体样式表，避免重复插入 <link>
const injectedStylesheets = new Set<string>()

/** 按需、幂等加载网络字体样式表；CDN 不可达时字体栈自动回退系统字体 */
export const ensureFontStylesheet = (url?: string) => {
  if (!url || injectedStylesheets.has(url)) return
  injectedStylesheets.add(url)
  const link = document.createElement('link')
  link.rel = 'stylesheet'
  link.href = url
  link.crossOrigin = 'anonymous'
  document.head.appendChild(link)
}

/** 根据配置解析正文字体栈：font 为 -1 时用自定义字体名，否则用预置字体（并注入其样式表） */
export const resolveFontFamily = (font: number, customFontName: string): string => {
  if (font === -1 && customFontName) return `"${customFontName}", sans-serif`
  const preset = fonts[font] ?? fonts[0]
  ensureFontStylesheet(preset.stylesheet)
  return preset.family
}
