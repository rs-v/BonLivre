import body_0 from '../assets/imgs/themes/body_0.png'
import content_0 from '../assets/imgs/themes/content_0.png'
import popup_0 from '../assets/imgs/themes/popup_0.png'
import body_1 from '../assets/imgs/themes/body_1.png'
import content_1 from '../assets/imgs/themes/content_1.png'
import popup_1 from '../assets/imgs/themes/popup_1.png'
import body_2 from '../assets/imgs/themes/body_2.png'
import content_2 from '../assets/imgs/themes/content_2.png'
import popup_2 from '../assets/imgs/themes/popup_2.png'
import body_3 from '../assets/imgs/themes/body_3.png'
import content_3 from '../assets/imgs/themes/content_3.png'
import popup_3 from '../assets/imgs/themes/popup_3.png'
import body_5 from '../assets/imgs/themes/body_5.png'
import content_5 from '../assets/imgs/themes/content_5.png'
import popup_5 from '../assets/imgs/themes/popup_5.png'
import body_6 from '../assets/imgs/themes/body_6.png'
import content_6 from '../assets/imgs/themes/content_6.png'
import popup_6 from '../assets/imgs/themes/popup_6.png'

/** 预置字体：family 为应用到文本的字体栈，stylesheet 为该字体的网络样式表 URL（可选） */
export type WebFont = {
  family: string
  stylesheet?: string
}

// 已注入的字体样式表 URL，避免重复插入 <link>
const injectedFontStylesheets = new Set<string>()

/**
 * 按需、幂等地加载网络字体样式表。
 * 仅在用户选中某款字体时注入对应 <link>，配合 fontsource 的 unicode-range 分片，
 * 浏览器只会下载当前页面实际用到的字形分片。CDN 不可达时字体栈会自动回退到系统字体。
 */
export const ensureFontStylesheet = (url?: string) => {
  if (!url || injectedFontStylesheets.has(url)) return
  injectedFontStylesheets.add(url)
  const link = document.createElement('link')
  link.rel = 'stylesheet'
  link.href = url
  link.crossOrigin = 'anonymous'
  link.addEventListener('error', () => {
    // 加载失败（如断网/CDN 不可达）时移除标记，允许后续重试，并回退到系统字体
    injectedFontStylesheets.delete(url)
    console.warn('[themeConfig] 网络字体加载失败，回退系统字体:', url)
  })
  document.head.appendChild(link)
}

const settings = {
  themes: [
    {
      body: '#ede7da url(' + body_0 + ') repeat',
      content: '#ede7da url(' + content_0 + ') repeat',
      popup: '#ede7da url(' + popup_0 + ') repeat',
    },
    {
      body: '#ede7da url(' + body_1 + ') repeat',
      content: '#ede7da url(' + content_1 + ') repeat',
      popup: '#ede7da url(' + popup_1 + ') repeat',
    },
    {
      body: '#ede7da url(' + body_2 + ') repeat',
      content: '#ede7da url(' + content_2 + ') repeat',
      popup: '#ede7da url(' + popup_2 + ') repeat',
    },
    {
      body: '#ede7da url(' + body_3 + ') repeat',
      content: '#ede7da url(' + content_3 + ') repeat',
      popup: '#ede7da url(' + popup_3 + ') repeat',
    },
    {
      body: '#ebcece repeat',
      content: '#f5e4e4 repeat',
      popup: '#faeceb repeat',
    },
    {
      body: '#ede7da url(' + body_5 + ') repeat',
      content: '#ede7da url(' + content_5 + ') repeat',
      popup: '#ede7da url(' + popup_5 + ') repeat',
    },
    {
      body: '#ede7da url(' + body_6 + ') repeat',
      content: '#ede7da url(' + content_6 + ') repeat',
      popup: '#ede7da url(' + popup_6 + ') repeat',
    },
  ],
  // 三款预置字体统一使用网络字体（fontsource CDN，按 unicode-range 分片，单页只下载用到的字形），
  // family 中网络字体名打头、系统字体兜底，断网/CDN 不可达时自动回退到本地系统字体。
  fonts: [
    // 黑体：思源黑体 Noto Sans SC
    {
      family:
        '"Noto Sans SC", "Microsoft YaHei", "PingFang SC", "HarmonyOS Sans SC", sans-serif',
      stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/noto-sans-sc@5/400.css',
    },
    // 宋体：思源宋体 Noto Serif SC
    {
      family: '"Noto Serif SC", "SimSun", "Songti SC", "STSong", serif',
      stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/noto-serif-sc@5/400.css',
    },
    // 楷书：霞鹜文楷 LXGW WenKai（该包无 400 字重，使用 300）
    {
      family: '"LXGW WenKai", "Kaiti SC", "STKaiti", "KaiTi", "楷体", serif',
      stylesheet: 'https://cdn.jsdelivr.net/npm/@fontsource/lxgw-wenkai@5/300.css',
    },
  ] as WebFont[],
}
export default settings
