import type {
  ApiResponse,
  Book,
  BookChapter,
  Bookmark,
  BookContentSearchResult,
  BookProgress,
  CreateBookmarkRequest,
  DeleteBookmarkRequest,
  ReadConfig,
  SearchBook,
} from './types'

const BASE_URL_KEY = 'remoteUrl'
const PASSWORD_KEY = 'remotePassword'
/** 后端 HTTP 默认端口（Program.cs 的 UseUrls），WebSocket 端口为其 +1 */
const BACKEND_HTTP_PORT = 5000

/**
 * 推断默认后端地址：生产环境前端由后端静态托管，即 location.origin；
 * 开发环境 Vite 在 8080，后端在 5000，沿用主机名只换端口。
 */
const defaultBackendUrl = () =>
  import.meta.env.DEV
    ? `${location.protocol}//${location.hostname}:${BACKEND_HTTP_PORT}`
    : location.origin

let baseUrl = localStorage.getItem(BASE_URL_KEY) || defaultBackendUrl()

export const getBaseUrl = () => baseUrl
export const getPassword = () => localStorage.getItem(PASSWORD_KEY) ?? ''

/** 保存后端地址；与 location.origin 相同则清除记录（跟随部署地址） */
export const setBaseUrl = (url: string) => {
  baseUrl = new URL(url).toString().replace(/\/$/, '')
  if (baseUrl === location.origin) localStorage.removeItem(BASE_URL_KEY)
  else localStorage.setItem(BASE_URL_KEY, baseUrl)
}

export const setPassword = (password: string) => {
  if (password) localStorage.setItem(PASSWORD_KEY, password)
  else localStorage.removeItem(PASSWORD_KEY)
}

/** WebSocket 入口：http 端口 +1，协议对应升级 */
const wsEntryPoint = () => {
  const u = new URL(baseUrl)
  const port = u.port ? String(Number(u.port) + 1) : u.protocol === 'https:' ? '444' : '81'
  const protocol = u.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${u.hostname}:${port}`
}

/**
 * 为无法设置 header 的 URL（WebSocket、<img src>、sendBeacon）追加 ?password=。
 * 常规请求走 Authorization: Bearer header。
 */
const withPassword = (url: string): string => {
  const password = getPassword()
  if (!password) return url
  const u = new URL(url)
  u.searchParams.set('password', password)
  return u.toString()
}

class ApiError extends Error {
  constructor(
    message: string,
    public status?: number,
  ) {
    super(message)
  }
}

const request = async <T>(
  path: string,
  init?: RequestInit & { baseOverride?: string },
): Promise<ApiResponse<T>> => {
  const headers = new Headers(init?.headers)
  const password = getPassword()
  if (password) headers.set('Authorization', `Bearer ${password}`)

  const resp = await fetch(`${init?.baseOverride ?? baseUrl}/${path}`, {
    ...init,
    headers,
  })
  if (resp.status === 401) throw new ApiError('密码错误或缺失', 401)
  if (!resp.ok) throw new ApiError(`请求失败（${resp.status}）`, resp.status)
  return (await resp.json()) as ApiResponse<T>
}

const postJson = <T>(path: string, body: unknown) =>
  request<T>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

// ---------- 书架 ----------

export const getBookshelf = () => request<Book[]>('getBookshelf')

export const getChapterList = (bookUrl: string) =>
  request<BookChapter[]>(`getChapterList?url=${encodeURIComponent(bookUrl)}`)

export const getBookContent = (bookUrl: string, index: number) =>
  request<string>(
    `getBookContent?url=${encodeURIComponent(bookUrl)}&index=${index}`,
  )

export const searchBookContent = (bookUrl: string, key: string) =>
  request<BookContentSearchResult[]>(
    `searchBookContent?url=${encodeURIComponent(bookUrl)}&key=${encodeURIComponent(key)}`,
  )

export const saveBook = (book: Book | SearchBook) => postJson<string>('saveBook', book)
export const deleteBook = (book: Book) => postJson<string>('deleteBook', book)

export const uploadBook = async (files: File[], overwrite = false) => {
  const formData = new FormData()
  for (const file of files) formData.append('file', file, file.name)
  return request<string>(`uploadBook${overwrite ? '?overwrite=true' : ''}`, {
    method: 'POST',
    body: formData,
  })
}

// ---------- 书签、进度与配置 ----------

export const getBookmarks = (bookUrl: string) =>
  request<Bookmark[]>(`getBookmarks?bookUrl=${encodeURIComponent(bookUrl)}`)

export const createBookmark = (bookmark: CreateBookmarkRequest) =>
  postJson<Bookmark>('createBookmark', bookmark)

export const deleteBookmark = (bookmark: DeleteBookmarkRequest) =>
  postJson<string>('deleteBookmark', bookmark)

/** 校验连通性并读取阅读配置；可传入待校验地址（连接对话框用） */
export const getReadConfig = async (urlOverride?: string): Promise<ReadConfig | null> => {
  const resp = await request<string>('getReadConfig', {
    baseOverride: urlOverride?.replace(/\/$/, ''),
  })
  if (!resp.isSuccess) return null
  try {
    return JSON.parse(resp.data) as ReadConfig
  } catch {
    return null
  }
}

export const saveReadConfig = (config: ReadConfig) =>
  postJson<string>('saveReadConfig', config)

export const saveBookProgress = (progress: BookProgress) =>
  postJson<string>('saveBookProgress', progress)

/** 页面关闭/切后台时可靠保存进度 */
export const saveBookProgressWithBeacon = (progress: BookProgress) => {
  navigator.sendBeacon(
    withPassword(`${baseUrl}/saveBookProgress`),
    JSON.stringify(progress),
  )
}

// ---------- 资源 URL（<img src> 直连，带密码 query） ----------

export const coverUrl = (path: string) =>
  withPassword(`${baseUrl}/cover?path=${encodeURIComponent(path)}`)

export const epubImageUrl = (bookUrl: string, src: string) =>
  withPassword(
    `${baseUrl}/image?url=${encodeURIComponent(bookUrl)}&path=${encodeURIComponent(src)}&width=600`,
  )

// ---------- WebSocket 搜索 ----------

export const searchBooks = (
  key: string,
  onReceive: (books: SearchBook[]) => void,
  onFinish: () => void,
) => {
  const socket = new WebSocket(withPassword(`${wsEntryPoint()}/searchBook`))
  socket.onopen = () => socket.send(JSON.stringify({ key }))
  socket.onmessage = event => {
    try {
      onReceive(JSON.parse(event.data))
    } catch {
      /* 非 JSON 消息忽略 */
    }
  }
  socket.onclose = onFinish
  socket.onerror = onFinish
  return () => socket.close()
}
