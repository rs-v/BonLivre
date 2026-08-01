import type { Bookmark, Book, BookChapter, BookProgress, ReadConfig } from './types'
import { defaultReadConfig } from './types'
import * as api from './api'

/** 当前阅读中的书与配置：跨书架/阅读器两个视图共享 */
export const reading = $state<{
  book: Book | null
  catalog: BookChapter[]
  chapterIndex: number
  chapterPos: number
  config: ReadConfig
}>({
  book: null,
  catalog: [],
  chapterIndex: 0,
  chapterPos: 0,
  config: { ...defaultReadConfig, spacing: { ...defaultReadConfig.spacing } },
})

/** 当前书籍的持久化书签，按创建时间倒序排列。 */
export const bookmarks = $state<{ items: Bookmark[] }>({ items: [] })

const READING_KEY = 'readingBook'

/** 进入阅读器前调用：记录书籍并持久化，刷新后仍能恢复 */
export const startReading = (book: Book) => {
  reading.book = book
  reading.chapterIndex = book.durChapterIndex ?? 0
  reading.chapterPos = book.durChapterPos ?? 0
  bookmarks.items = []
  localStorage.setItem(READING_KEY, JSON.stringify(book))
}

/** 书架「最近阅读」入口：上次读的书（无记录返回 null） */
export const lastReadBook = (): Book | null => {
  const saved = localStorage.getItem(READING_KEY)
  if (!saved) return null
  try {
    return JSON.parse(saved) as Book
  } catch {
    return null
  }
}

/** 阅读器直接刷新（无书架跳转）时从 localStorage 恢复 */
export const restoreReading = (): boolean => {
  if (reading.book) return true
  const saved = localStorage.getItem(READING_KEY)
  if (!saved) return false
  try {
    startReading(JSON.parse(saved) as Book)
    return true
  } catch {
    return false
  }
}

export const loadBookmarks = async (): Promise<string | null> => {
  const bookUrl = reading.book?.bookUrl
  if (!bookUrl) {
    bookmarks.items = []
    return null
  }

  try {
    const resp = await api.getBookmarks(bookUrl)
    if (reading.book?.bookUrl !== bookUrl) return null
    if (!resp.isSuccess) return resp.errorMsg || '获取书签失败'
    bookmarks.items = resp.data
    return null
  } catch {
    return '获取书签失败'
  }
}

export const createCurrentBookmark = async (): Promise<string | null> => {
  const book = reading.book
  if (!book) return '未找到当前书籍'

  const { chapterIndex, chapterPos } = reading
  if (bookmarks.items.some(b => b.chapterIndex === chapterIndex && b.chapterPos === chapterPos)) {
    return '当前位置已有书签'
  }

  try {
    const resp = await api.createBookmark({
      bookUrl: book.bookUrl,
      chapterIndex,
      chapterPos,
    })
    if (!resp.isSuccess) return resp.errorMsg || '添加书签失败'
    if (reading.book?.bookUrl === book.bookUrl)
      bookmarks.items = [resp.data, ...bookmarks.items]
    return null
  } catch {
    return '添加书签失败'
  }
}

export const removeBookmark = async (bookmark: Bookmark): Promise<string | null> => {
  try {
    const resp = await api.deleteBookmark({ bookUrl: bookmark.bookUrl, id: bookmark.id })
    if (!resp.isSuccess) return resp.errorMsg || '删除书签失败'
    bookmarks.items = bookmarks.items.filter(item => item.id !== bookmark.id)
    return null
  } catch {
    return '删除书签失败'
  }
}

export const currentProgress = (): BookProgress | null => {
  const book = reading.book
  const title = reading.catalog[reading.chapterIndex]?.title
  if (!book || !title) return null
  return {
    name: book.name,
    author: book.author,
    bookUrl: book.bookUrl,
    durChapterIndex: reading.chapterIndex,
    durChapterPos: reading.chapterPos,
    durChapterTime: Date.now(),
    durChapterTitle: title,
  }
}

let lastSavedAt = 0

/** 保存进度（sendBeacon，页面关闭也可靠）；throttleMs 内重复调用会被跳过 */
export const saveProgress = (throttleMs = 0) => {
  const progress = currentProgress()
  if (!progress) return
  const now = Date.now()
  if (throttleMs > 0 && now - lastSavedAt < throttleMs) return
  lastSavedAt = now
  api.saveBookProgressWithBeacon(progress)
}

export const loadConfig = async () => {
  try {
    const config = await api.getReadConfig()
    if (config && typeof config.theme === 'number') {
      reading.config = {
        ...defaultReadConfig,
        ...config,
        spacing: { ...defaultReadConfig.spacing, ...config.spacing },
      }
    }
  } catch {
    /* 后端不可达时用默认配置 */
  }
}

export const saveConfig = () => {
  api.saveReadConfig($state.snapshot(reading.config)).catch(() => {})
}
