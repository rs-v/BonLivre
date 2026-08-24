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
const lastSavedAtByBook = new Map<string, number>()
const lastProgressTimeByBook = new Map<string, number>()

/** 进入阅读器前调用：记录书籍并持久化，刷新后仍能恢复 */
export const startReading = (book: Book) => {
  reading.book = book
  reading.catalog = []
  reading.chapterIndex = Math.max(0, book.durChapterIndex ?? 0)
  reading.chapterPos = Math.max(0, book.durChapterPos ?? 0)
  bookmarks.items = []
  lastSavedAtByBook.delete(book.bookUrl)
  lastProgressTimeByBook.set(book.bookUrl, book.durChapterTime ?? 0)
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
  if (!book) return null

  const chapterIndex = Math.max(0, reading.chapterIndex)
  const catalogTitle = reading.catalog[chapterIndex]?.title?.trim()
  const restoredTitle =
    book.durChapterIndex === chapterIndex ? book.durChapterTitle?.trim() : ''
  const previousTime = lastProgressTimeByBook.get(book.bookUrl) ?? book.durChapterTime ?? 0
  const progressTime = Math.max(Date.now(), previousTime + 1)
  lastProgressTimeByBook.set(book.bookUrl, progressTime)

  return {
    name: book.name,
    author: book.author,
    bookUrl: book.bookUrl,
    durChapterIndex: chapterIndex,
    durChapterPos: Math.max(0, reading.chapterPos),
    durChapterTime: progressTime,
    durChapterTitle: catalogTitle || restoredTitle || `第 ${chapterIndex + 1} 章`,
  }
}

const persistProgressLocally = (progress: BookProgress) => {
  const book = reading.book
  if (!book || book.bookUrl !== progress.bookUrl || book.durChapterTime > progress.durChapterTime)
    return
  const updated = {
    ...book,
    durChapterIndex: progress.durChapterIndex,
    durChapterPos: progress.durChapterPos,
    durChapterTime: progress.durChapterTime,
    durChapterTitle: progress.durChapterTitle,
  }
  reading.book = updated
  localStorage.setItem(READING_KEY, JSON.stringify(updated))
}

/** 常规保存进度；throttleMs 内同一本书的重复调用会被跳过。 */
export const saveProgress = async (throttleMs = 0): Promise<boolean> => {
  const bookUrl = reading.book?.bookUrl
  if (!bookUrl) return false

  const now = Date.now()
  const lastSavedAt = lastSavedAtByBook.get(bookUrl) ?? 0
  if (throttleMs > 0 && now - lastSavedAt < throttleMs) return true

  lastSavedAtByBook.set(bookUrl, now)
  const progress = currentProgress()
  if (!progress) return false

  try {
    const resp = await api.saveBookProgress(progress)
    if (!resp.isSuccess) {
      if (lastSavedAtByBook.get(bookUrl) === now) lastSavedAtByBook.delete(bookUrl)
      return false
    }
    persistProgressLocally(progress)
    return true
  } catch {
    if (lastSavedAtByBook.get(bookUrl) === now) lastSavedAtByBook.delete(bookUrl)
    return false
  }
}

/** 页面隐藏或卸载时用 keepalive 请求尽力刷新进度；本地已先落盘，请求失败也不丢进度。 */
export const flushProgress = (): void => {
  const progress = currentProgress()
  if (!progress) return
  persistProgressLocally(progress)
  api.saveProgressKeepalive(progress)
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
