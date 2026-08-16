/** 与后端 Models/Models.cs 的 camelCase 序列化保持一致 */

export type Book = {
  name: string
  author: string
  bookUrl: string
  tocUrl: string
  origin: string
  originName: string
  type: number
  group: number
  latestChapterTitle: string
  totalChapterNum: number
  durChapterTitle: string
  durChapterIndex: number
  durChapterPos: number
  durChapterTime: number
  /** 本地书籍文件 mtime 的 Unix 毫秒，由后端 ImportedAt 填充；非本地书籍可能为 0。 */
  importedAt?: number
  coverUrl?: string
  intro?: string
}

export type SearchBook = {
  name: string
  author: string
  bookUrl: string
  origin: string
  originName: string
  type: number
  coverUrl?: string
  latestChapterTitle?: string
}

export type BookChapter = {
  title: string
  url: string
  index: number
  isVolume: boolean
  /** 与 chapterPos 同口径的 UTF-16 正文长度；不是文件字节数。 */
  contentLength?: number | null
}

export type BookContentSearchResult = {
  chapterIndex: number
  chapterTitle: string
  chapterPos: number
  snippet: string
}

export type BookProgress = {
  name: string
  author: string
  bookUrl: string
  durChapterIndex: number
  durChapterPos: number
  durChapterTime: number
  durChapterTitle: string
}

export type Bookmark = {
  id: number
  bookUrl: string
  chapterIndex: number
  chapterPos: number
  createdAt: number
}

export type CreateBookmarkRequest = {
  bookUrl: string
  chapterIndex: number
  chapterPos: number
}

export type DeleteBookmarkRequest = {
  bookUrl: string
  id: number
}

export type ApiResponse<T> = {
  isSuccess: boolean
  errorMsg: string
  data: T
}

/**
 * 阅读界面配置，经 /saveReadConfig 持久化到后端。
 * 字段结构与后端持久化的阅读配置保持一致：
 * font：预置字体序号，-1 表示使用 customFontName；
 * spacing：字距/段距以 em 计，行距为 line-height = 1 + line。
 */
export type ReadConfig = {
  theme: number
  font: number
  fontSize: number
  readWidth: number
  infiniteLoading: boolean
  customFontName: string
  jumpDuration: number
  spacing: {
    paragraph: number
    line: number
    letter: number
  }
}

export const defaultReadConfig: ReadConfig = {
  theme: 0,
  font: 0,
  fontSize: 18,
  readWidth: 800,
  infiniteLoading: false,
  customFontName: '',
  jumpDuration: 1000,
  spacing: {
    paragraph: 1,
    line: 0.8,
    letter: 0,
  },
}
