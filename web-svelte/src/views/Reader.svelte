<script lang="ts">
  import { onMount } from 'svelte'
  import * as api from '../lib/api'
  import { toast } from '../lib/toast.svelte'
  import { navigate } from '../lib/router.svelte'
  import {
    reading,
    bookmarks,
    restoreReading,
    saveProgress,
    flushProgress,
    saveConfig,
    loadConfig,
    loadBookmarks,
    createCurrentBookmark,
    removeBookmark,
  } from '../lib/reader.svelte'
  import type { BookChapter, BookContentSearchResult } from '../lib/types'
  import {
    themes,
    themeAt,
    fonts,
    resolveFontFamily,
    NIGHT_THEME_INDEX,
  } from '../lib/themes'

  type Paragraph = {
    text: string
    img?: string
    hr?: boolean
    quote?: boolean
    listItem?: boolean
    parts?: InlinePart[]
    endPos: number
  }
  type InlinePart = { text: string; em?: boolean; strong?: boolean }
  type LoadedChapter = { index: number; title: string; paragraphs: Paragraph[] }
  type DrawerTab = 'catalog' | 'bookmarks'
  type SeekSegment = { chapterIndex: number; start: number; end: number }
  type SeekMap = { prefixes: number[]; segments: SeekSegment[]; total: number }

  // 已加载章节列表：无限加载模式下会持续追加；普通模式始终只有一个元素
  let chapters = $state<LoadedChapter[]>([])
  let contentLoading = $state(true)
  let loadingMore = false
  let catalogOpen = $state(false)
  let drawerTab = $state<DrawerTab>('catalog')
  let settingsOpen = $state(false)
  let contentSearchOpen = $state(false)
  let contentSearchKey = $state('')
  let contentSearchResults = $state<BookContentSearchResult[]>([])
  let contentSearchLoading = $state(false)
  let contentSearchSubmitted = $state(false)
  let contentSearchRequestId = 0
  let navigationRequestId = 0
  let positionTrackingPaused = false
  let progressPreview = $state<number | null>(null)
  let toolbarVisible = $state(
    typeof window === 'undefined' || !window.matchMedia('(max-width: 750px)').matches,
  )
  let customFontInput = $state('')
  let isFullscreen = $state(false)
  let contentEl = $state<HTMLElement | null>(null)
  let bottomSentinel = $state<HTMLElement | null>(null)
  let catalogListEl = $state<HTMLElement | null>(null)
  let settingsSheetEl = $state<HTMLElement | null>(null)
  let settingsButtonEl = $state<HTMLElement | null>(null)
  let contentSearchSheetEl = $state<HTMLElement | null>(null)
  let contentSearchButtonEl = $state<HTMLElement | null>(null)
  let contentSearchInputEl = $state<HTMLInputElement | null>(null)
  let catalogFilterEl = $state<HTMLInputElement | null>(null)
  let catalogFilter = $state('')
  let volumesCollapsed = $state<Record<number, boolean>>({})

  const theme = $derived(themeAt(reading.config.theme))
  const fontFamily = $derived(
    resolveFontFamily(reading.config.font, reading.config.customFontName),
  )

  const buildSeekMap = (catalog: BookChapter[]): SeekMap | null => {
    if (catalog.length === 0) return null

    const prefixes: number[] = []
    const segments: SeekSegment[] = []
    let total = 0
    for (let chapterIndex = 0; chapterIndex < catalog.length; chapterIndex++) {
      const length = catalog[chapterIndex].contentLength
      if (typeof length !== 'number' || !Number.isInteger(length) || length < 0) return null
      prefixes.push(total)
      if (length > 0) {
        segments.push({ chapterIndex, start: total, end: total + length })
      }
      total += length
      if (!Number.isSafeInteger(total)) return null
    }
    return total > 0 ? { prefixes, segments, total } : null
  }

  const resolveSeekTarget = (map: SeekMap, position: number) => {
    const target = Math.max(0, Math.min(map.total - 1, Math.round(position)))
    let low = 0
    let high = map.segments.length - 1
    while (low < high) {
      const mid = Math.floor((low + high) / 2)
      if (map.segments[mid].end > target) high = mid
      else low = mid + 1
    }
    const segment = map.segments[low]
    return {
      position: target,
      chapterIndex: segment.chapterIndex,
      chapterPos: target - segment.start,
    }
  }

  const seekMap = $derived(buildSeekMap(reading.catalog))
  const currentBookPosition = $derived.by(() => {
    if (!seekMap) return 0
    const chapterIndex = Math.max(0, Math.min(reading.chapterIndex, reading.catalog.length - 1))
    const chapterLength = reading.catalog[chapterIndex]?.contentLength ?? 0
    const prefix = seekMap.prefixes[chapterIndex] ?? 0
    if (chapterLength <= 0) return Math.min(prefix, seekMap.total - 1)
    return Math.min(
      seekMap.total - 1,
      prefix + Math.max(0, Math.min(reading.chapterPos, chapterLength - 1)),
    )
  })
  const displayedBookPosition = $derived(progressPreview ?? currentBookPosition)
  const displayedSeekTarget = $derived(
    seekMap ? resolveSeekTarget(seekMap, displayedBookPosition) : null,
  )
  const displayedProgressPercent = $derived(
    seekMap ? ((displayedBookPosition + 1) / seekMap.total) * 100 : 0,
  )
  const chapterProgressPercent = $derived.by(() => {
    if (!seekMap) return 0
    const chapterIndex = Math.max(
      0,
      Math.min(reading.chapterIndex, reading.catalog.length - 1),
    )
    const chapterLength = reading.catalog[chapterIndex]?.contentLength ?? 0
    if (chapterLength <= 0) return 0
    return Math.min(
      100,
      Math.max(0, (reading.chapterPos / chapterLength) * 100),
    )
  })
  const displayedProgressTitle = $derived(
    displayedSeekTarget
      ? (reading.catalog[displayedSeekTarget.chapterIndex]?.title ?? '')
      : '',
  )

  // 段落切分。图片行（后端 ExtractTextWithImages 生成的 <img src="...">）转为图片段；
  // <hr> 转为分隔线段。私有占位标记（与后端 LocalBookService 约定一致）还原
  // <em>/<strong> 与 blockquote/li 语义。进度按累计可见字数计，+1 计换行。
  const IMG_RE = /^<img src="([^"]+)">$/
  // U+E000..U+E007 —— 与 Services/LocalBookService.cs 中的私有标记常量一一对应。
  const EM_START = ''
  const EM_END = ''
  const STRONG_START = ''
  const STRONG_END = ''
  const QUOTE_START = ''
  const QUOTE_END = ''
  const LIST_START = ''
  const LIST_END = ''
  const PRIVATE_MARKERS = new Set([
    EM_START,
    EM_END,
    STRONG_START,
    STRONG_END,
    QUOTE_START,
    QUOTE_END,
    LIST_START,
    LIST_END,
  ])

  const parseInline = (line: string): InlinePart[] => {
    const parts: InlinePart[] = []
    let buf = ''
    let em = false
    let strong = false
    const flush = () => {
      if (buf) {
        parts.push({ text: buf, em: em || undefined, strong: strong || undefined })
        buf = ''
      }
    }
    for (const ch of line) {
      if (ch === EM_START) {
        flush()
        em = true
      } else if (ch === EM_END) {
        flush()
        em = false
      } else if (ch === STRONG_START) {
        flush()
        strong = true
      } else if (ch === STRONG_END) {
        flush()
        strong = false
      } else if (PRIVATE_MARKERS.has(ch)) {
        // 块级标记由 splitContent 状态机处理；若残留则跳过，避免污染正文。
        continue
      } else {
        buf += ch
      }
    }
    flush()
    return parts.length > 0 ? parts : [{ text: line }]
  }

  const visibleLength = (line: string): number => {
    let n = 0
    for (const ch of line) {
      if (PRIVATE_MARKERS.has(ch)) continue
      n++
    }
    return n
  }

  const splitContent = (raw: string): Paragraph[] => {
    let pos = -1
    // 块级开/闭标记可能独占一行；跨行保持语义直到遇到闭标记。
    let inQuote = false
    let inList = false
    const out: Paragraph[] = []
    for (const rawLine of raw.split(/\n+/)) {
      let line = rawLine.trim()
      if (!line) continue

      // 先处理开/闭标记，再计可见字数（标记不计）。
      if (line.includes(QUOTE_START)) {
        inQuote = true
        line = line.split(QUOTE_START).join('')
      }
      if (line.includes(LIST_START)) {
        inList = true
        line = line.split(LIST_START).join('')
      }
      const closeQuote = line.includes(QUOTE_END)
      const closeList = line.includes(LIST_END)
      if (closeQuote) line = line.split(QUOTE_END).join('')
      if (closeList) line = line.split(LIST_END).join('')
      line = line.trim()

      pos += visibleLength(rawLine.trim()) + 1

      const img = IMG_RE.exec(line)?.[1]
      if (img) {
        out.push({ text: line, img, endPos: pos })
      } else if (line === '<hr>') {
        out.push({ text: line, hr: true, endPos: pos })
      } else if (line) {
        out.push({
          text: line,
          quote: inQuote || undefined,
          listItem: inList || undefined,
          parts: parseInline(line),
          endPos: pos,
        })
      }
      // 闭标记作用在本行之后结束，本行内容仍带语义。
      if (closeQuote) inQuote = false
      if (closeList) inList = false
    }
    return out
  }

  const fetchChapter = async (index: number): Promise<LoadedChapter | null> => {
    const book = reading.book
    const chapter = reading.catalog[index]
    if (!book || !chapter) return null
    const resp = await api.getBookContent(book.bookUrl, chapter.index)
    if (!resp.isSuccess) {
      toast(resp.errorMsg, 'error')
      return null
    }
    return { index, title: chapter.title, paragraphs: splitContent(resp.data) }
  }

  const resolveChapterPosition = (chapter: LoadedChapter, pos: number) => {
    if (pos <= 0 || chapter.paragraphs.length === 0) return 0

    let low = 0
    let high = chapter.paragraphs.length - 1
    while (low < high) {
      const mid = Math.floor((low + high) / 2)
      if (chapter.paragraphs[mid].endPos >= pos) high = mid
      else low = mid + 1
    }
    return chapter.paragraphs[low].endPos
  }

  const restoreScroll = (chapter: LoadedChapter, pos: number, requestId: number) => {
    requestAnimationFrame(() => {
      if (requestId !== navigationRequestId) return
      if (contentEl) {
        if (pos <= 0) {
          contentEl.scrollTop = 0
        } else {
          contentEl
            .querySelector<HTMLElement>(
              `[data-chapter="${chapter.index}"][data-pos="${pos}"]`,
            )
            ?.scrollIntoView({ block: 'start' })
        }
      }
      requestAnimationFrame(() => {
        if (requestId === navigationRequestId) positionTrackingPaused = false
      })
    })
  }

  /** 跳转到指定章节（替换已加载内容），pos 为章内累计字数。 */
  const loadChapter = async (index: number, pos = 0): Promise<boolean> => {
    const requestId = ++navigationRequestId
    contentLoading = true
    positionTrackingPaused = true
    let restoreScheduled = false

    try {
      const loaded = await fetchChapter(index)
      if (requestId !== navigationRequestId || !loaded) return false

      const resolvedPos = resolveChapterPosition(loaded, pos)
      chapters = [loaded]
      reading.chapterIndex = index
      reading.chapterPos = resolvedPos
      document.title = reading.catalog[index]?.title ?? document.title
      restoreScroll(loaded, resolvedPos, requestId)
      restoreScheduled = true
      return true
    } catch {
      if (requestId === navigationRequestId) toast('获取章节内容失败', 'error')
      return false
    } finally {
      if (requestId === navigationRequestId) {
        contentLoading = false
        if (!restoreScheduled) positionTrackingPaused = false
      }
    }
  }

  /** 无限加载：不清空已读内容，向后追加下一章。 */
  const loadMore = async () => {
    if (loadingMore || contentLoading) return
    const last = chapters.at(-1)
    if (!last || last.index + 1 >= reading.catalog.length) return

    const requestId = navigationRequestId
    const expectedLastIndex = last.index
    loadingMore = true
    try {
      const loaded = await fetchChapter(expectedLastIndex + 1)
      if (
        loaded &&
        requestId === navigationRequestId &&
        chapters.at(-1)?.index === expectedLastIndex
      ) {
        chapters = [...chapters, loaded]
      }
    } catch {
      /* 滚动重新触发时重试 */
    } finally {
      loadingMore = false
    }
  }

  // 滚动时用 IntersectionObserver 跟踪阅读位置（章节序号 + 章内字数）
  $effect(() => {
    if (!contentEl || chapters.length === 0) return
    const observer = new IntersectionObserver(
      entries => {
        if (positionTrackingPaused) return
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          const el = entry.target as HTMLElement
          const pos = Number(el.dataset.pos)
          const chapterIdx = Number(el.dataset.chapter)
          if (Number.isNaN(pos) || Number.isNaN(chapterIdx)) continue
          if (reading.chapterIndex !== chapterIdx) {
            reading.chapterIndex = chapterIdx
            document.title = reading.catalog[chapterIdx]?.title ?? document.title
          }
          reading.chapterPos = pos
          void saveProgress(60_000)
        }
      },
      { root: contentEl, rootMargin: '0px 0px -80% 0px' },
    )
    for (const el of contentEl.querySelectorAll('[data-pos]')) observer.observe(el)
    return () => observer.disconnect()
  })

  // 无限加载：底部哨兵进入视口时追加下一章
  $effect(() => {
    if (!reading.config.infiniteLoading || !bottomSentinel || !contentEl) return
    const observer = new IntersectionObserver(
      entries => {
        if (entries.some(e => e.isIntersecting)) loadMore()
      },
      { root: contentEl, rootMargin: '200px' },
    )
    observer.observe(bottomSentinel)
    return () => observer.disconnect()
  })

  const openContentSearch = () => {
    catalogOpen = false
    settingsOpen = false
    contentSearchOpen = true
    requestAnimationFrame(() => contentSearchInputEl?.focus())
  }

  const handleContentTap = (event: MouseEvent) => {
    const target = event.target
    if (!(target instanceof Element)) return
    if (target.closest('button, a, input, textarea, select, [contenteditable="true"]')) return

    settingsOpen = false
    contentSearchOpen = false
    toolbarVisible = !toolbarVisible
  }

  const submitContentSearch = async () => {
    const key = contentSearchKey.trim()
    const bookUrl = reading.book?.bookUrl
    if (!key) {
      toast('请输入搜索内容', 'error')
      return
    }
    if (!bookUrl) return

    const requestId = ++contentSearchRequestId
    contentSearchLoading = true
    contentSearchSubmitted = true
    contentSearchResults = []
    try {
      const resp = await api.searchBookContent(bookUrl, key)
      if (requestId !== contentSearchRequestId || reading.book?.bookUrl !== bookUrl) return
      if (!resp.isSuccess) {
        toast(resp.errorMsg, 'error')
        return
      }
      contentSearchResults = resp.data
    } catch {
      if (requestId === contentSearchRequestId) toast('搜索书籍内容失败', 'error')
    } finally {
      if (requestId === contentSearchRequestId) contentSearchLoading = false
    }
  }

  const jumpToSearchResult = (result: BookContentSearchResult) => {
    contentSearchOpen = false
    toChapter(result.chapterIndex, result.chapterPos)
  }

  /** HTML 转义，供 {@html} 搜索高亮使用，避免快照中的 <>& 注入。 */
  const escapeHtml = (s: string): string =>
    s
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')

  /** 高亮搜索快照里的命中关键字；先转义再包 <mark>。 */
  const highlightKey = (text: string, key: string): string => {
    const escaped = escapeHtml(text)
    if (!key) return escaped
    const safe = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    return escaped.replace(
      new RegExp(safe, 'gi'),
      match => `<mark class="search-hit">${match}</mark>`,
    )
  }

  // 目录项：按关键字过滤后展示。空关键字时返回全量，供卷折叠使用。
  const visibleCatalog = $derived.by(() => {
    const filter = catalogFilter.trim().toLocaleLowerCase()
    if (!filter) return reading.catalog.map((c, i) => ({ chapter: c, index: i }))
    return reading.catalog
      .map((c, i) => ({ chapter: c, index: i }))
      .filter(item => item.chapter.title.trim().toLocaleLowerCase().includes(filter))
  })

  // 卷分组：每个卷标题对应其后到下一卷前的章节区间，用于折叠/缩进展示。
  const volumeGroups = $derived.by(() => {
    const groups: {
      volumeIndex: number
      volume: BookChapter
      children: { chapter: BookChapter; index: number }[]
    }[] = []
    let current: { volumeIndex: number; volume: BookChapter; children: { chapter: BookChapter; index: number }[] } | null = null
    const filter = catalogFilter.trim().toLocaleLowerCase()
    reading.catalog.forEach((chapter, index) => {
      if (chapter.isVolume) {
        current = { volumeIndex: index, volume: chapter, children: [] }
        groups.push(current)
      } else if (current) {
        // 关键字过滤下：未命中的子章不入组；空卷稍后剔除。
        if (!filter || chapter.title.trim().toLocaleLowerCase().includes(filter)) {
          current.children.push({ chapter, index })
        }
      } else {
        // 卷前的散章：当作无卷子章，单独成组。
        if (!filter || chapter.title.trim().toLocaleLowerCase().includes(filter)) {
          groups.push({
            volumeIndex: index,
            volume: chapter,
            children: [],
          })
        }
      }
    })
    // 过滤模式下丢掉「卷标题本身不命中且无子章」的空卷，避免目录刷屏。
    if (filter) {
      return groups.filter(
        g =>
          g.children.length > 0 ||
          g.volume.title.trim().toLocaleLowerCase().includes(filter),
      )
    }
    return groups
  })

  const toggleVolume = (index: number) => {
    volumesCollapsed = { ...volumesCollapsed, [index]: !volumesCollapsed[index] }
  }

  // 当前章是否落在某卷的子章区间内（用于自动展开含当前章的卷）。
  const chapterVolumeIndex = $derived.by(() => {
    let vol = -1
    for (let i = 0; i <= reading.chapterIndex && i < reading.catalog.length; i++) {
      if (reading.catalog[i].isVolume) vol = i
    }
    return vol
  })

  // 章节是否可见：卷折叠时隐藏子章；当前章所在卷始终展开。
  const isChapterVisible = (volumeIndex: number) =>
    volumesCollapsed[volumeIndex] !== true || volumeIndex === chapterVolumeIndex

  const toChapter = async (index: number, pos = 0): Promise<boolean> => {
    if (index < 0) {
      toast('本章是第一章', 'error')
      return false
    }
    if (index >= reading.catalog.length) {
      toast('本章是最后一章', 'error')
      return false
    }
    catalogOpen = false
    const loaded = await loadChapter(index, pos)
    if (loaded) void saveProgress()
    return loaded
  }

  const previewProgressSeek = (event: Event) => {
    const value = (event.currentTarget as HTMLInputElement).valueAsNumber
    if (!Number.isNaN(value)) progressPreview = Math.round(value)
  }

  const commitProgressSeek = async (event: Event) => {
    const map = seekMap
    const value = (event.currentTarget as HTMLInputElement).valueAsNumber
    if (!map || Number.isNaN(value)) return

    const target = resolveSeekTarget(map, value)
    progressPreview = target.position
    await toChapter(target.chapterIndex, target.chapterPos)
    progressPreview = null
  }

  const selectDrawerTab = (tab: DrawerTab) => {
    drawerTab = tab
  }

  const handleDrawerTabKey = (event: KeyboardEvent) => {
    let tab: DrawerTab | null = null
    if (event.key === 'ArrowLeft' || event.key === 'Home') tab = 'catalog'
    if (event.key === 'ArrowRight' || event.key === 'End') tab = 'bookmarks'
    if (!tab) return

    event.preventDefault()
    event.stopPropagation()
    selectDrawerTab(tab)
    requestAnimationFrame(() => document.getElementById(`drawer-tab-${tab}`)?.focus())
  }

  // 目录标签激活时滚动到当前章节
  $effect(() => {
    if (!catalogOpen || drawerTab !== 'catalog' || !catalogListEl) return
    catalogListEl
      .querySelector('.catalog-item.active')
      ?.scrollIntoView({ block: 'center' })
  })

  const scrollPage = (direction: 1 | -1) => {
    if (!contentEl) return
    const delta = direction * (contentEl.clientHeight - 100)
    if (direction === -1 && contentEl.scrollTop === 0) {
      toast('已到达页面顶部')
      return
    }
    if (
      direction === 1 &&
      contentEl.scrollTop + contentEl.clientHeight >= contentEl.scrollHeight - 1
    ) {
      // 无限加载下滚到底会自动接下一章，这里只处理普通模式
      if (!reading.config.infiniteLoading) toast('已到达页面底部')
      return
    }
    contentEl.scrollBy({
      top: delta,
      behavior: reading.config.jumpDuration > 0 ? 'smooth' : 'auto',
    })
  }

  const toTop = () => contentEl?.scrollTo({ top: 0, behavior: 'smooth' })
  const toBottom = () =>
    contentEl?.scrollTo({ top: contentEl.scrollHeight, behavior: 'smooth' })

  // 键盘快捷键：←/→ 切章，↑/↓ 翻页
  const handleKey = (event: KeyboardEvent) => {
    if ((event.target as HTMLElement)?.tagName === 'INPUT') return
    switch (event.key) {
      case 'ArrowLeft':
        event.preventDefault()
        toChapter(reading.chapterIndex - 1)
        break
      case 'ArrowRight':
        event.preventDefault()
        toChapter(reading.chapterIndex + 1)
        break
      case 'ArrowUp':
        event.preventDefault()
        scrollPage(-1)
        break
      case 'ArrowDown':
        event.preventDefault()
        scrollPage(1)
        break
    }
  }

  const resolveInitialChapterIndex = (
    catalog: BookChapter[],
    savedIndex: number,
    savedTitle: string,
  ) => {
    const safeIndex = Number.isInteger(savedIndex) ? savedIndex : 0
    const clampedIndex = Math.max(0, Math.min(safeIndex, catalog.length - 1))
    const normalizedTitle = savedTitle.trim().toLocaleLowerCase()
    if (!normalizedTitle) return clampedIndex
    if (catalog[clampedIndex]?.title.trim().toLocaleLowerCase() === normalizedTitle) {
      return clampedIndex
    }

    let closestIndex = -1
    let closestDistance = Number.POSITIVE_INFINITY
    for (let index = 0; index < catalog.length; index++) {
      if (catalog[index].title.trim().toLocaleLowerCase() !== normalizedTitle) continue
      const distance = Math.abs(index - clampedIndex)
      if (distance < closestDistance) {
        closestIndex = index
        closestDistance = distance
      }
    }
    return closestIndex >= 0 ? closestIndex : clampedIndex
  }

  const init = async () => {
    if (!restoreReading()) {
      navigate('/')
      return
    }
    await loadConfig()
    customFontInput = reading.config.customFontName
    const book = reading.book!
    try {
      const resp = await api.getChapterList(book.bookUrl)
      if (!resp.isSuccess || resp.data.length === 0) {
        toast('获取目录失败', 'error')
        return
      }
      reading.catalog = resp.data
      void loadBookmarks().then(error => {
        if (error) toast(error, 'error')
      })
      const index = resolveInitialChapterIndex(
        resp.data,
        reading.chapterIndex,
        book.durChapterTitle ?? '',
      )
      if (await loadChapter(index, reading.chapterPos)) void saveProgress()
    } catch {
      toast('获取目录失败，请检查后端连接', 'error')
    }
  }

  // 只在挂载时跑一次：init() 会同步读 reading.book（restoreReading），又会异步写回
  // （saveProgress → persistProgressLocally），放在 $effect 里会自我触发成无限重载循环。
  onMount(() => {
    void init()
    const onVisibilityChange = () => {
      if (document.visibilityState === 'hidden') flushProgress()
    }
    const onPageHide = () => flushProgress()
    const onFullscreenChange = () => {
      isFullscreen = document.fullscreenElement !== null
    }
    document.addEventListener('visibilitychange', onVisibilityChange)
    window.addEventListener('pagehide', onPageHide)
    window.addEventListener('keydown', handleKey)
    document.addEventListener('fullscreenchange', onFullscreenChange)
    return () => {
      document.removeEventListener('visibilitychange', onVisibilityChange)
      window.removeEventListener('pagehide', onPageHide)
      window.removeEventListener('keydown', handleKey)
      document.removeEventListener('fullscreenchange', onFullscreenChange)
      flushProgress()
    }
  })

  $effect(() => {
    const mediaQuery = window.matchMedia('(max-width: 750px)')
    const updateToolbarVisibility = () => {
      toolbarVisible = !mediaQuery.matches
    }
    mediaQuery.addEventListener('change', updateToolbarVisibility)
    return () => mediaQuery.removeEventListener('change', updateToolbarVisibility)
  })

  $effect(() => {
    if (!settingsOpen && !contentSearchOpen) return
    const closeOnOutsidePointerDown = (event: PointerEvent) => {
      const target = event.target
      if (!(target instanceof Node)) return
      if (
        settingsSheetEl?.contains(target) ||
        settingsButtonEl?.contains(target) ||
        contentSearchSheetEl?.contains(target) ||
        contentSearchButtonEl?.contains(target)
      ) {
        return
      }
      settingsOpen = false
      contentSearchOpen = false
    }
    document.addEventListener('pointerdown', closeOnOutsidePointerDown, true)
    return () =>
      document.removeEventListener('pointerdown', closeOnOutsidePointerDown, true)
  })

  const backToShelf = async () => {
    const saved = await saveProgress()
    if (!saved) {
      toast('阅读进度保存失败，将在后台重试', 'error')
      flushProgress()
    }
    navigate('/')
  }

  const addBookmark = async () => {
    settingsOpen = false
    const error = await createCurrentBookmark()
    toast(error ?? '已添加书签', error ? 'error' : 'success')
  }

  const deleteBookmark = async (id: number) => {
    const bookmark = bookmarks.items.find(item => item.id === id)
    if (!bookmark) return
    const error = await removeBookmark(bookmark)
    if (error) toast(error, 'error')
  }

  const bookmarkTitle = (index: number) =>
    reading.catalog[index]?.title ?? `第 ${index + 1} 章`

  const bookmarkTime = (timestamp: number) =>
    new Date(timestamp).toLocaleString('zh-CN', {
      month: 'numeric',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    })

  const setCustomFont = () => {
    reading.config.font = -1
    reading.config.customFontName = customFontInput.trim()
    saveConfig()
  }

  /** 切换浏览器全屏（Fullscreen API，带 webkit 前缀回退）；不支持或被拒则静默。 */
  const toggleFullscreen = async () => {
    try {
      const doc = document as Document & {
        webkitFullscreenElement?: Element | null
        webkitExitFullscreen?: () => Promise<void>
      }
      const el = contentEl as HTMLElement | null
      const root = (el ?? document.documentElement) as HTMLElement & {
        webkitRequestFullscreen?: () => Promise<void>
      }
      const inFs = document.fullscreenElement || doc.webkitFullscreenElement
      if (inFs) {
        if (document.exitFullscreen) await document.exitFullscreen()
        else if (doc.webkitExitFullscreen) await doc.webkitExitFullscreen()
      } else if (root.requestFullscreen) {
        await root.requestFullscreen()
      } else if (root.webkitRequestFullscreen) {
        await root.webkitRequestFullscreen()
      }
    } catch {
      /* 用户手势/权限不足等场景静默忽略 */
    }
  }

  /** 数值设置项微调工具 */
  const adjust = (
    key: 'fontSize' | 'readWidth' | 'jumpDuration',
    delta: number,
    min: number,
    max: number,
  ) => {
    reading.config[key] = Math.min(max, Math.max(min, reading.config[key] + delta))
    saveConfig()
  }
  const adjustSpacing = (
    key: 'paragraph' | 'line' | 'letter',
    delta: number,
    min: number,
    max: number,
  ) => {
    reading.config.spacing[key] = Number(
      Math.min(max, Math.max(min, reading.config.spacing[key] + delta)).toFixed(2),
    )
    saveConfig()
  }
</script>

<!-- 夜间主题时挂 md-dark，底栏/抽屉/设置面板等 UI 跟随切深色令牌 -->
<div
  class="reader"
  style:background={theme.body}
  style:color={theme.text}
  class:md-dark={reading.config.theme === NIGHT_THEME_INDEX}
  class:toolbar-visible={toolbarVisible}
  class:book-progress-available={seekMap !== null}
>
  <!-- MD3 Modal navigation drawer：目录 -->
  {#if catalogOpen}
    <div class="scrim" role="presentation" onclick={() => (catalogOpen = false)}>
      <nav
        class="drawer"
        role="presentation"
        onclick={e => e.stopPropagation()}
      >
        <div class="drawer-title title-medium">阅读导航</div>
        <div class="drawer-tabs" role="tablist" aria-label="阅读导航">
          <button
            id="drawer-tab-catalog"
            class="drawer-tab label-large"
            class:active={drawerTab === 'catalog'}
            role="tab"
            aria-selected={drawerTab === 'catalog'}
            aria-controls="drawer-panel-catalog"
            tabindex={drawerTab === 'catalog' ? 0 : -1}
            onclick={() => selectDrawerTab('catalog')}
            onkeydown={handleDrawerTabKey}
          >
            目录（{reading.catalog.length}）
          </button>
          <button
            id="drawer-tab-bookmarks"
            class="drawer-tab label-large"
            class:active={drawerTab === 'bookmarks'}
            role="tab"
            aria-selected={drawerTab === 'bookmarks'}
            aria-controls="drawer-panel-bookmarks"
            tabindex={drawerTab === 'bookmarks' ? 0 : -1}
            onclick={() => selectDrawerTab('bookmarks')}
            onkeydown={handleDrawerTabKey}
          >
            书签（{bookmarks.items.length}）
          </button>
        </div>
        {#if drawerTab === 'catalog'}
          <div class="catalog-filter-row">
            <input
              class="catalog-filter"
              bind:this={catalogFilterEl}
              bind:value={catalogFilter}
              placeholder="过滤章节标题"
              aria-label="过滤章节标题"
              onclick={e => e.stopPropagation()}
              onkeydown={e => e.stopPropagation()}
            />
            {#if catalogFilter}
              <button
                class="btn-icon catalog-filter-clear"
                aria-label="清除过滤"
                onclick={e => { e.stopPropagation(); catalogFilter = '' }}
              >×</button>
            {/if}
          </div>
          <div
            id="drawer-panel-catalog"
            class="drawer-panel catalog-list"
            role="tabpanel"
            aria-labelledby="drawer-tab-catalog"
            bind:this={catalogListEl}
          >
            {#if volumeGroups.length > 0 && !catalogFilter.trim()}
              {#each volumeGroups as group (group.volumeIndex)}
                {#if group.children.length > 0}
                  <button
                    class="catalog-item body-medium volume-row"
                    class:active={group.volumeIndex === reading.chapterIndex}
                    aria-expanded={isChapterVisible(group.volumeIndex)}
                    onclick={() => toggleVolume(group.volumeIndex)}
                  >
                    <span class="catalog-caret">{isChapterVisible(group.volumeIndex) ? '▾' : '▸'}</span>
                    <span class="catalog-title">{group.volume.title}</span>
                  </button>
                  {#if isChapterVisible(group.volumeIndex)}
                    {#each group.children as child (child.index)}
                      <button
                        class="catalog-item body-medium child-chapter"
                        class:active={child.index === reading.chapterIndex}
                        onclick={() => toChapter(child.index)}
                      >
                        {child.chapter.title}
                      </button>
                    {/each}
                  {/if}
                {:else}
                  <button
                    class="catalog-item body-medium"
                    class:active={group.volumeIndex === reading.chapterIndex}
                    onclick={() => toChapter(group.volumeIndex)}
                  >
                    {group.volume.title}
                  </button>
                {/if}
              {/each}
            {:else}
              {#each visibleCatalog as item (item.chapter.url)}
                <button
                  class="catalog-item body-medium"
                  class:active={item.index === reading.chapterIndex}
                  class:volume={item.chapter.isVolume}
                  onclick={() => toChapter(item.index)}
                >
                  {item.chapter.title}
                </button>
              {/each}
            {/if}
          </div>
        {:else}
          <div
            id="drawer-panel-bookmarks"
            class="drawer-panel bookmark-panel"
            role="tabpanel"
            aria-labelledby="drawer-tab-bookmarks"
          >
            {#if bookmarks.items.length === 0}
              <p class="bookmark-empty body-medium">暂无书签</p>
            {:else}
              <div class="bookmark-list">
                {#each bookmarks.items as bookmark (bookmark.id)}
                  <div class="bookmark-item">
                    <button
                      class="bookmark-link"
                      onclick={() => toChapter(bookmark.chapterIndex, bookmark.chapterPos)}
                    >
                      <span>{bookmarkTitle(bookmark.chapterIndex)}</span>
                      <small>{bookmarkTime(bookmark.createdAt)}</small>
                    </button>
                    <button
                      class="bookmark-delete"
                      aria-label={`删除${bookmarkTitle(bookmark.chapterIndex)}的书签`}
                      onclick={event => {
                        event.stopPropagation()
                        deleteBookmark(bookmark.id)
                      }}
                    >
                      删除
                    </button>
                  </div>
                {/each}
              </div>
            {/if}
          </div>
        {/if}
      </nav>
    </div>
  {/if}

  <!-- 正文：点击非交互区域切换工具栏显隐 -->
  <div
    class="content-wrapper"
    bind:this={contentEl}
    role="presentation"
    onclick={handleContentTap}
  >
    <article
      class="content"
      style:background={theme.content}
      style:max-width="{reading.config.readWidth}px"
      style:font-size="{reading.config.fontSize}px"
      style:font-family={fontFamily}
      style:letter-spacing="{reading.config.spacing.letter}em"
      style:line-height={1 + reading.config.spacing.line}
      role="presentation"
    >
      {#if contentLoading}
        <p class="loading">加载中…</p>
      {:else}
        {#each chapters as chapter (chapter.index)}
          <h2 class="chapter-title">{chapter.title}</h2>
          {#each chapter.paragraphs as p (p.endPos)}
            {#if p.img}
              <p data-chapter={chapter.index} data-pos={p.endPos} class="img-p">
                <img
                  src={api.epubImageUrl(reading.book?.bookUrl ?? '', p.img)}
                  alt=""
                  loading="lazy"
                />
              </p>
            {:else if p.hr}
              <hr
                data-chapter={chapter.index}
                data-pos={p.endPos}
                class="content-hr"
              />
            {:else}
              <p
                data-chapter={chapter.index}
                data-pos={p.endPos}
                class:content-quote={p.quote}
                class:content-li={p.listItem}
                style:margin="{reading.config.spacing.paragraph}em 0"
              >
                {#if p.parts && p.parts.some(part => part.em || part.strong)}
                  {#each p.parts as part, i (`${p.endPos}-${i}`)}
                    {#if part.strong && part.em}
                      <strong><em>{part.text}</em></strong>
                    {:else if part.strong}
                      <strong>{part.text}</strong>
                    {:else if part.em}
                      <em>{part.text}</em>
                    {:else}
                      {part.text}
                    {/if}
                  {/each}
                {:else}
                  {p.text}
                {/if}
              </p>
            {/if}
          {/each}
        {/each}
        <div class="bottom-sentinel" bind:this={bottomSentinel}></div>
        {#if !reading.config.infiniteLoading}
          <div class="chapter-nav">
            <button class="btn-outlined" onclick={() => toChapter(reading.chapterIndex - 1)}
              >上一章</button
            >
            <button class="btn-outlined" onclick={() => toChapter(reading.chapterIndex + 1)}
              >下一章</button
            >
          </div>
        {/if}
      {/if}
    </article>
  </div>

  <!-- MD3 Bottom app bar 与 TXT 全书进度 -->
  {#if toolbarVisible}
    <div class="reader-controls">
      {#if seekMap}
        <div class="book-progress">
          <span class="book-progress-title label-medium" title={displayedProgressTitle}>
            {displayedProgressTitle}
          </span>
          <input
            type="range"
            min="0"
            max={seekMap.total - 1}
            step="1"
            value={displayedBookPosition}
            aria-label="全书阅读进度"
            aria-valuetext={`${displayedProgressTitle}，${displayedProgressPercent.toFixed(1)}%`}
            oninput={previewProgressSeek}
            onchange={commitProgressSeek}
            onpointercancel={() => (progressPreview = null)}
          />
          <span class="book-progress-percent label-medium">
            {displayedProgressPercent.toFixed(1)}%
          </span>
        </div>
      {/if}
      <div class="chapter-progress" aria-hidden="true">
        <div
          class="chapter-progress-bar"
          style:width="{chapterProgressPercent}%"
        ></div>
      </div>
      <div class="bottom-app-bar">
      <button class="bar-item" onclick={backToShelf} aria-label="返回书架">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-1 9H9V9h10v2zm-4 4H9v-2h6v2zm4-8H9V5h10v2z" />
        </svg>
        <span class="label-medium">书架</span>
      </button>
      <button
        class="bar-item"
        onclick={toggleFullscreen}
        aria-label={isFullscreen ? '退出全屏' : '进入全屏'}
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          {#if isFullscreen}
            <!-- 退出全屏 -->
            <path
              d="M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z"
            />
          {:else}
            <!-- 进入全屏 -->
            <path
              d="M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7v-3h-2v5h5v-2h-3zM14 5v2h3v3h2V5h-5z"
            />
          {/if}
        </svg>
        <span class="label-medium">{isFullscreen ? '退出' : '全屏'}</span>
      </button>
      <button
        class="bar-item"
        onclick={() => {
          settingsOpen = false
          contentSearchOpen = false
          drawerTab = 'catalog'
          catalogOpen = true
        }}
        aria-label="打开目录"
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M3 13h2v-2H3v2zm0 4h2v-2H3v2zm0-8h2V7H3v2zm4 4h14v-2H7v2zm0 4h14v-2H7v2zM7 7v2h14V7H7z" />
        </svg>
        <span class="label-medium">目录</span>
      </button>
      <button
        class="bar-item"
        bind:this={contentSearchButtonEl}
        onclick={openContentSearch}
        aria-label="全文搜索"
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M9.5 3a6.5 6.5 0 1 0 4.05 11.59l4.43 4.43 1.41-1.41-4.43-4.43A6.5 6.5 0 0 0 9.5 3zm0 2a4.5 4.5 0 1 1 0 9 4.5 4.5 0 0 1 0-9z" />
        </svg>
        <span class="label-medium">搜索</span>
      </button>
      <button class="bar-item" onclick={addBookmark} aria-label="添加书签">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M6 2a2 2 0 0 0-2 2v18l8-4 8 4V4a2 2 0 0 0-2-2H6zm0 2h12v14.76l-6-3-6 3V4z" />
        </svg>
        <span class="label-medium">书签</span>
      </button>
      <button class="bar-item" onclick={() => toChapter(reading.chapterIndex - 1)} aria-label="上一章">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z" />
        </svg>
        <span class="label-medium">上一章</span>
      </button>
      <button class="bar-item" onclick={() => toChapter(reading.chapterIndex + 1)} aria-label="下一章">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z" />
        </svg>
        <span class="label-medium">下一章</span>
      </button>
      <button class="bar-item" onclick={toTop} aria-label="回到顶部">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M7.41 15.41 12 10.83l4.59 4.58L18 14l-6-6-6 6z" />
        </svg>
        <span class="label-medium">顶部</span>
      </button>
      <button class="bar-item" onclick={toBottom} aria-label="滚到底部">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z" />
        </svg>
        <span class="label-medium">底部</span>
      </button>
      <button
        class="bar-item"
        bind:this={settingsButtonEl}
        onclick={() => {
          catalogOpen = false
          contentSearchOpen = false
          settingsOpen = !settingsOpen
        }}
        aria-label="打开设置"
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z" />
        </svg>
        <span class="label-medium">设置</span>
      </button>
      <button
        class="bar-item"
        aria-label="切换夜间模式"
        onclick={() => {
          reading.config.theme =
            reading.config.theme === NIGHT_THEME_INDEX ? 0 : NIGHT_THEME_INDEX
          saveConfig()
        }}
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          {#if reading.config.theme === NIGHT_THEME_INDEX}
            <path d="M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0 .39-.39.39-1.03 0-1.41l-1.06-1.06zm1.06-10.96c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z" />
          {:else}
            <path d="M9.37 5.51c-.18.64-.27 1.31-.27 1.99 0 4.08 3.32 7.4 7.4 7.4.68 0 1.35-.09 1.99-.27C17.45 17.19 14.93 19 12 19c-3.86 0-7-3.14-7-7 0-2.93 1.81-5.45 4.37-6.49zM12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9 9-4.03 9-9c0-.46-.04-.92-.1-1.36-.98 1.37-2.58 2.26-4.4 2.26-2.98 0-5.4-2.42-5.4-5.4 0-1.81.89-3.42 2.26-4.4-.44-.06-.9-.1-1.36-.1z" />
          {/if}
        </svg>
        <span class="label-medium">{reading.config.theme === NIGHT_THEME_INDEX ? '日间' : '夜间'}</span>
      </button>
      </div>
    </div>
  {/if}

  <!-- MD3 Bottom sheet：全文搜索 -->
  {#if contentSearchOpen}
    <section class="sheet search-sheet" bind:this={contentSearchSheetEl} aria-label="全文搜索">
      <div class="sheet-handle"></div>
      <form
        class="content-search-form"
        onsubmit={event => {
          event.preventDefault()
          submitContentSearch()
        }}
      >
        <input
          bind:this={contentSearchInputEl}
          bind:value={contentSearchKey}
          placeholder="搜索当前书籍"
          aria-label="搜索当前书籍"
        />
        <button class="btn-tonal" type="submit" disabled={contentSearchLoading}>
          {contentSearchLoading ? '搜索中…' : '搜索'}
        </button>
      </form>
      {#if contentSearchLoading}
        <p class="content-search-status body-medium">正在搜索全文…</p>
      {:else if !contentSearchSubmitted}
        <p class="content-search-status body-medium">输入关键词，搜索当前书籍的全部正文。</p>
      {:else if contentSearchResults.length === 0}
        <p class="content-search-status body-medium">未找到匹配内容。</p>
      {:else}
        <div class="content-search-results">
          {#each contentSearchResults as result, i (`${result.chapterIndex}-${result.chapterPos}-${i}`)}
            <button class="content-search-result" onclick={() => jumpToSearchResult(result)}>
              <span class="content-search-result-title label-medium">{result.chapterTitle}</span>
              <span class="content-search-result-snippet body-medium">
                {@html highlightKey(result.snippet, contentSearchKey.trim())}
              </span>
            </button>
          {/each}
        </div>
      {/if}
    </section>
  {/if}

  <!-- MD3 Bottom sheet：阅读设置 -->
  {#if settingsOpen}
    <div class="sheet" bind:this={settingsSheetEl}>
      <div class="sheet-handle"></div>
      <div class="setting-row">
        <span class="body-medium">阅读主题</span>
        <div class="theme-list">
          {#each themes as t, i (t.name)}
            <button
              class="theme-dot"
              class:active={reading.config.theme === i}
              style:background={t.content}
              title={t.name}
              aria-label={t.name}
              onclick={() => {
                reading.config.theme = i
                saveConfig()
              }}
            ></button>
          {/each}
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">正文字体</span>
        <div class="chip-list">
          {#each fonts as f, i (f.name)}
            <button
              class="chip"
              class:active={reading.config.font === i}
              onclick={() => {
                reading.config.font = i
                saveConfig()
              }}>{f.name}</button
            >
          {/each}
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">自定字体</span>
        <div class="custom-font">
          <input
            bind:value={customFontInput}
            placeholder="本机已安装的字体名"
            onclick={e => e.stopPropagation()}
          />
          <button
            class="chip"
            class:active={reading.config.font === -1}
            onclick={setCustomFont}>应用</button
          >
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">字体大小 {reading.config.fontSize}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小字号" onclick={() => adjust('fontSize', -1, 12, 36)}>−</button>
          <button class="btn-icon" aria-label="增大字号" onclick={() => adjust('fontSize', 1, 12, 36)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">字距 {reading.config.spacing.letter.toFixed(2)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小字距" onclick={() => adjustSpacing('letter', -0.01, 0, 0.5)}>−</button>
          <button class="btn-icon" aria-label="增大字距" onclick={() => adjustSpacing('letter', 0.01, 0, 0.5)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">行距 {reading.config.spacing.line.toFixed(1)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小行距" onclick={() => adjustSpacing('line', -0.1, 0.2, 3)}>−</button>
          <button class="btn-icon" aria-label="增大行距" onclick={() => adjustSpacing('line', 0.1, 0.2, 3)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">段距 {reading.config.spacing.paragraph.toFixed(1)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小段距" onclick={() => adjustSpacing('paragraph', -0.1, 0, 3)}>−</button>
          <button class="btn-icon" aria-label="增大段距" onclick={() => adjustSpacing('paragraph', 0.1, 0, 3)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">页面宽度 {reading.config.readWidth}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小页宽" onclick={() => adjust('readWidth', -160, 480, 1600)}>−</button>
          <button class="btn-icon" aria-label="增大页宽" onclick={() => adjust('readWidth', 160, 480, 1600)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">翻页动画</span>
        <button
          class="switch"
          class:on={reading.config.jumpDuration > 0}
          role="switch"
          aria-checked={reading.config.jumpDuration > 0}
          aria-label="翻页动画"
          onclick={() => {
            reading.config.jumpDuration = reading.config.jumpDuration > 0 ? 0 : 1000
            saveConfig()
          }}
        >
          <span class="thumb"></span>
        </button>
      </div>
      <div class="setting-row">
        <span class="body-medium">无限加载</span>
        <button
          class="switch"
          class:on={reading.config.infiniteLoading}
          role="switch"
          aria-checked={reading.config.infiniteLoading}
          aria-label="无限加载"
          onclick={() => {
            reading.config.infiniteLoading = !reading.config.infiniteLoading
            saveConfig()
            if (!reading.config.infiniteLoading)
              loadChapter(reading.chapterIndex, reading.chapterPos)
          }}
        >
          <span class="thumb"></span>
        </button>
      </div>
    </div>
  {/if}
</div>

<style>
  .reader {
    --reader-action-bar-height: 72px;
    --reader-progress-height: 0px;
    --reader-controls-height: calc(
      var(--reader-action-bar-height) + var(--reader-progress-height)
    );
    height: 100%;
    display: flex;
    flex-direction: column;
  }

  .reader.book-progress-available {
    --reader-progress-height: 52px;
  }

  .content-wrapper {
    flex: 1;
    overflow-y: auto;
    padding: 24px 16px;
  }

  .reader.toolbar-visible .content-wrapper {
    padding-bottom: calc(var(--reader-controls-height) + 28px);
  }

  .content {
    margin: 0 auto;
    padding: 40px 48px;
    border-radius: var(--md-shape-lg);
    min-height: 100%;
  }

  .chapter-title {
    margin-top: 0;
  }

  .content p {
    text-indent: 2em;
  }

  .img-p {
    text-indent: 0 !important;
    text-align: center;
  }

  .img-p img {
    max-width: 100%;
    border-radius: var(--md-shape-sm);
  }

  .content-hr {
    border: none;
    border-top: 1px solid var(--md-outline-variant);
    margin: 1.4em 0;
    opacity: 0.6;
  }

  .content em,
  .content strong {
    text-indent: 0;
  }

  /* blockquote：左侧强调条 + 略缩进，与正文区分。 */
  .content-quote {
    border-left: 3px solid var(--md-outline-variant);
    padding-left: 0.9em;
    margin-left: 0.2em;
    color: var(--md-on-surface-variant);
    text-indent: 0 !important;
  }

  /* 列表项：圆点前缀，取消首行缩进。 */
  .content-li {
    text-indent: 0 !important;
    padding-left: 1.2em;
    position: relative;
  }

  .content-li::before {
    content: '•';
    position: absolute;
    left: 0.25em;
    color: var(--md-on-surface-variant);
  }

  .loading {
    opacity: 0.6;
  }

  .bottom-sentinel {
    height: 1px;
  }

  .chapter-nav {
    display: flex;
    justify-content: space-between;
    margin-top: 40px;
  }

  /* Bottom app bar 与全书进度 */
  .reader-controls {
    position: fixed;
    right: 0;
    bottom: 0;
    left: 0;
    height: var(--reader-controls-height);
    display: flex;
    flex-direction: column;
    background: var(--md-surface-container);
    color: var(--md-on-surface-variant);
    box-shadow: var(--md-elevation-2);
    z-index: 40;
  }

  .book-progress {
    height: var(--reader-progress-height);
    display: grid;
    grid-template-columns: minmax(100px, 220px) minmax(160px, 640px) 64px;
    align-items: center;
    justify-content: center;
    gap: 12px;
    padding: 8px 20px;
    border-bottom: 1px solid var(--md-outline-variant);
  }

  .book-progress-title {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .book-progress-percent {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .book-progress input[type='range'] {
    width: 100%;
    height: 28px;
    margin: 0;
    padding: 0;
    border: 0;
    border-radius: 0;
    background: transparent;
    box-shadow: none;
    accent-color: var(--md-primary);
  }

  .book-progress input[type='range']:focus {
    padding: 0;
    border: 0;
    box-shadow: none;
  }

  .book-progress input[type='range']:focus-visible {
    outline: 2px solid var(--md-primary);
    outline-offset: 2px;
  }

  /* 章内进度细条：贴在工具栏上沿，随当前段落累计字数填充。 */
  .chapter-progress {
    height: 2px;
    width: 100%;
    background: var(--md-outline-variant);
    overflow: hidden;
    opacity: 0.85;
  }

  .chapter-progress-bar {
    height: 100%;
    background: var(--md-primary);
    transition: width 0.2s ease-out;
  }

  .bottom-app-bar {
    height: var(--reader-action-bar-height);
    display: flex;
    justify-content: center;
    gap: 4px;
    padding: 6px 8px;
    overflow-x: auto;
  }

  .bar-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    min-width: 60px;
    padding: 4px 8px;
    background: transparent;
    color: inherit;
    border-radius: var(--md-shape-lg);
    box-shadow: none;
  }

  .bar-item:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  /* Navigation drawer */
  .scrim {
    position: fixed;
    inset: 0;
    background: var(--md-scrim);
    z-index: 50;
  }

  .drawer {
    width: 320px;
    max-width: 85vw;
    height: 100%;
    background: var(--md-surface-container-low);
    color: var(--md-on-surface);
    border-radius: 0 var(--md-shape-lg) var(--md-shape-lg) 0;
    display: flex;
    flex-direction: column;
    box-shadow: var(--md-elevation-1);
    animation: drawer-in 0.25s cubic-bezier(0.2, 0, 0, 1);
  }

  @keyframes drawer-in {
    from {
      transform: translateX(-100%);
    }
    to {
      transform: translateX(0);
    }
  }

  .drawer-title {
    padding: 18px 24px 12px;
    color: var(--md-on-surface-variant);
  }

  .drawer-tabs {
    display: flex;
    gap: 4px;
    padding: 0 12px 8px;
    border-bottom: 1px solid var(--md-outline-variant);
  }

  /* 目录过滤框 + 卷折叠 */
  .catalog-filter-row {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 8px 12px;
    border-bottom: 1px solid var(--md-outline-variant);
  }

  .catalog-filter {
    flex: 1;
    min-width: 0;
    padding: 8px 12px;
    font-size: 13px;
  }

  .catalog-filter-clear {
    width: 32px;
    height: 32px;
    font-size: 18px;
    color: var(--md-on-surface-variant);
  }

  .catalog-caret {
    display: inline-block;
    width: 16px;
    text-align: center;
    color: var(--md-on-surface-variant);
    flex-shrink: 0;
  }

  .catalog-title {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .child-chapter {
    padding-left: 36px;
  }

  /* 搜索结果命中高亮 */
  :global(.search-hit) {
    background: color-mix(in srgb, var(--md-primary) 28%, transparent);
    color: var(--md-on-surface);
    border-radius: 2px;
    padding: 0 1px;
  }

  .drawer-tab {
    position: relative;
    flex: 1;
    padding: 12px 8px;
    background: transparent;
    color: var(--md-on-surface-variant);
    border-radius: var(--md-shape-md) var(--md-shape-md) 0 0;
    box-shadow: none;
  }

  .drawer-tab::after {
    position: absolute;
    right: 16px;
    bottom: 0;
    left: 16px;
    height: 3px;
    background: transparent;
    border-radius: var(--md-shape-full);
    content: '';
  }

  .drawer-tab:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  .drawer-tab.active {
    color: var(--md-primary);
  }

  .drawer-tab.active::after {
    background: var(--md-primary);
  }

  .drawer-tab:focus-visible {
    outline: 2px solid var(--md-primary);
    outline-offset: -2px;
  }

  .drawer-panel {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }

  .bookmark-panel {
    padding: 12px;
  }

  .bookmark-empty {
    margin: 4px 16px;
    color: var(--md-on-surface-variant);
  }

  .bookmark-list {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .bookmark-item {
    display: flex;
    align-items: center;
    gap: 4px;
  }

  .bookmark-link {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 2px;
    padding: 8px 16px;
    text-align: left;
    background: transparent;
    color: var(--md-on-surface-variant);
    border-radius: var(--md-shape-md);
    box-shadow: none;
  }

  .bookmark-link small {
    color: var(--md-on-surface-variant);
    opacity: 0.72;
  }

  .bookmark-link:hover,
  .bookmark-delete:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  .bookmark-delete {
    flex-shrink: 0;
    padding: 8px;
    background: transparent;
    color: var(--md-error);
    border-radius: var(--md-shape-md);
    box-shadow: none;
  }

  .catalog-list {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 12px 12px 12px;
  }

  .catalog-item {
    display: block;
    width: 100%;
    text-align: left;
    background: transparent;
    color: var(--md-on-surface-variant);
    padding: 12px 16px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    border-radius: var(--md-shape-full);
    box-shadow: none;
  }

  .catalog-item:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  .catalog-item.active {
    background: var(--md-secondary-container);
    color: var(--md-on-secondary-container);
    font-weight: 600;
  }

  .catalog-item.volume {
    font-weight: 600;
  }

  /* Bottom sheet 设置面板 */
  .sheet {
    position: fixed;
    bottom: 0;
    left: 50%;
    transform: translateX(-50%);
    width: 96%;
    max-width: 480px;
    max-height: 65vh;
    overflow-y: auto;
    border-radius: var(--md-shape-xl) var(--md-shape-xl) 0 0;
    padding: 8px 24px 20px;
    background: var(--md-surface-container-low);
    color: var(--md-on-surface);
    box-shadow: var(--md-elevation-3);
    display: flex;
    flex-direction: column;
    gap: 4px;
    z-index: 60;
  }

  .reader.toolbar-visible .sheet {
    bottom: var(--reader-controls-height);
  }

  .sheet-handle {
    width: 32px;
    height: 4px;
    border-radius: var(--md-shape-full);
    background: var(--md-outline-variant);
    margin: 8px auto 12px;
    flex-shrink: 0;
  }

  .search-sheet {
    gap: 12px;
  }

  .content-search-form {
    display: flex;
    gap: 8px;
  }

  .content-search-form input {
    flex: 1;
    min-width: 0;
    padding: 10px 12px;
  }

  .content-search-form button {
    flex-shrink: 0;
  }

  .content-search-status {
    margin: 4px 0 12px;
    color: var(--md-on-surface-variant);
  }

  .content-search-results {
    display: flex;
    flex-direction: column;
    gap: 4px;
    overflow-y: auto;
    min-height: 0;
  }

  .content-search-result {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 4px;
    width: 100%;
    padding: 12px;
    text-align: left;
    color: var(--md-on-surface);
    background: transparent;
    border-radius: var(--md-shape-md);
    box-shadow: none;
  }

  .content-search-result:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  .content-search-result-title {
    color: var(--md-primary);
  }

  .content-search-result-snippet {
    color: var(--md-on-surface-variant);
    text-indent: 0;
  }

  .setting-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    min-height: 48px;
  }

  .theme-list,
  .chip-list {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  .theme-dot {
    width: 28px;
    height: 28px;
    border-radius: var(--md-shape-full);
    padding: 0;
    border: 2px solid var(--md-outline-variant);
    box-shadow: none;
  }

  .theme-dot.active {
    border-color: var(--md-primary);
    outline: 2px solid var(--md-primary);
    outline-offset: 1px;
  }

  .chip {
    font-size: 13px;
    padding: 6px 14px;
    background: transparent;
    color: var(--md-on-surface-variant);
    border: 1px solid var(--md-outline);
    border-radius: var(--md-shape-sm);
    box-shadow: none;
    white-space: nowrap;
    flex-shrink: 0;
  }

  .chip:hover {
    box-shadow: none;
  }

  .chip.active {
    background: var(--md-secondary-container);
    color: var(--md-on-secondary-container);
    border-color: transparent;
  }

  .custom-font {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: 1;
    max-width: 260px;
    min-width: 0;
  }

  .custom-font input {
    padding: 8px 12px;
    font-size: 13px;
    min-width: 0;
  }

  .stepper {
    display: flex;
    gap: 4px;
  }

  .stepper .btn-icon {
    border: 1px solid var(--md-outline-variant);
    font-size: 18px;
    width: 36px;
    height: 36px;
  }

  /* MD3 Switch */
  .switch {
    width: 52px;
    height: 32px;
    padding: 0;
    border-radius: var(--md-shape-full);
    background: var(--md-surface-container-highest);
    border: 2px solid var(--md-outline);
    position: relative;
    box-shadow: none;
    flex-shrink: 0;
  }

  .switch:hover {
    box-shadow: none;
  }

  .switch .thumb {
    position: absolute;
    top: 50%;
    left: 4px;
    transform: translateY(-50%);
    width: 16px;
    height: 16px;
    border-radius: var(--md-shape-full);
    background: var(--md-outline);
    transition:
      left 0.15s,
      width 0.15s,
      height 0.15s,
      background 0.15s;
  }

  .switch.on {
    background: var(--md-primary);
    border-color: var(--md-primary);
  }

  .switch.on .thumb {
    left: 24px;
    width: 24px;
    height: 24px;
    background: var(--md-on-primary);
  }

  @media (max-width: 750px) {
    .reader {
      --reader-action-bar-height: calc(112px + env(safe-area-inset-bottom, 0px));
    }

    .reader.book-progress-available {
      --reader-progress-height: 64px;
    }

    .content {
      padding: 20px 16px;
      border-radius: 0;
    }

    .content-wrapper {
      padding: 0 0 calc(12px + env(safe-area-inset-bottom, 0px));
    }

    .reader.toolbar-visible .content-wrapper {
      padding-bottom: calc(var(--reader-controls-height) + 12px);
    }

    .book-progress {
      grid-template-columns: minmax(0, 1fr) auto;
      grid-template-rows: 20px 28px;
      gap: 4px 8px;
      padding: 6px 12px;
    }

    .book-progress input[type='range'] {
      grid-column: 1 / -1;
    }

    /* 11 个操作使用两行网格，保证窄屏点击目标不会过小或横向滚动。 */
    .bottom-app-bar {
      height: var(--reader-action-bar-height);
      display: grid;
      grid-template-columns: repeat(6, minmax(0, 1fr));
      grid-template-rows: repeat(2, 52px);
      gap: 0;
      padding: 4px 6px calc(4px + env(safe-area-inset-bottom, 0px));
      overflow: hidden;
    }

    .bar-item {
      min-width: 0;
      padding: 4px 0;
    }

    .bar-item .label-medium {
      display: none;
    }

    .sheet {
      bottom: env(safe-area-inset-bottom, 0px);
      width: 100%;
      max-height: 60vh;
    }

    .reader.toolbar-visible .sheet {
      bottom: var(--reader-controls-height);
    }

    .drawer {
      width: 300px;
    }
  }
</style>
