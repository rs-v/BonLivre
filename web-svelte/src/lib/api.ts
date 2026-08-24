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

/**
 * WebSocket 入口：与 HTTP 同源同端口，仅升级协议。
 * 后端在 5000/5001 上跑的是同一套路由，`/searchBook` 在 baseUrl 的端口上就能连；
 * 早先写死「端口 +1」会让单端口部署（反向代理、HTTPS 443）永远连到不存在的 444/81。
 */
const wsEntryPoint = () => {
  const u = new URL(baseUrl)
  u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:'
  return u.toString().replace(/\/$/, '')
}

/**
 * 凭证只走 Authorization: Bearer header；敏感参数一律放请求体，
 * 避免 URL 进入浏览器历史与服务器访问日志。
 */

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

/** 二进制响应（封面、EPUB 图片）不走 LeagdoApiResponse 包装，直接返回 blob。 */
const postBlob = async (path: string, body: unknown, signal?: AbortSignal): Promise<Blob> => {
  const headers = new Headers({ 'Content-Type': 'application/json' })
  const password = getPassword()
  if (password) headers.set('Authorization', `Bearer ${password}`)

  const resp = await fetch(`${baseUrl}/${path}`, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
    signal,
  })
  if (resp.status === 401) throw new ApiError('密码错误或缺失', 401)
  if (!resp.ok) throw new ApiError(`请求失败（${resp.status}）`, resp.status)
  return await resp.blob()
}

// ---------- 书架 ----------

export const getBookshelf = () => request<Book[]>('getBookshelf')

export const getChapterList = (bookUrl: string) =>
  postJson<BookChapter[]>('getChapterList', { url: bookUrl })

export const getBookContent = (bookUrl: string, index: number) =>
  postJson<string>('getBookContent', { url: bookUrl, index })

export const searchBookContent = (bookUrl: string, key: string) =>
  postJson<BookContentSearchResult[]>('searchBookContent', { url: bookUrl, key })

export const saveBook = (book: Book | SearchBook) => postJson<string>('saveBook', book)
export const deleteBook = (book: Book) => postJson<string>('deleteBook', book)

export const uploadBook = async (files: File[], overwrite = false) => {
  const formData = new FormData()
  for (const file of files) formData.append('file', file, file.name)
  // overwrite 也走表单字段，保持「敏感/业务参数不进 URL」的约定
  if (overwrite) formData.append('overwrite', 'true')
  return request<string>('uploadBook', {
    method: 'POST',
    body: formData,
  })
}

/**
 * 下载整本原始文件（.txt/.epub）：凭证与 bookUrl 都不进 URL，
 * fetch 拿到 blob 后用一次性 <a download> 交给浏览器存盘。
 */
export const downloadBook = async (bookUrl: string): Promise<void> => {
  const headers = new Headers({ 'Content-Type': 'application/json' })
  const password = getPassword()
  if (password) headers.set('Authorization', `Bearer ${password}`)

  const resp = await fetch(`${baseUrl}/downloadBook`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ url: bookUrl }),
  })
  if (resp.status === 401) throw new ApiError('密码错误或缺失', 401)
  if (!resp.ok) throw new ApiError(`请求失败（${resp.status}）`, resp.status)

  const blob = await resp.blob()
  saveBlob(downloadFileName(resp.headers.get('Content-Disposition'), bookUrl), blob)
}

/** 从 Content-Disposition 解析文件名：RFC 5987 的 filename* 优先，回退 filename，再回退 bookUrl 里的原名。 */
const downloadFileName = (disposition: string | null, bookUrl: string): string => {
  if (disposition) {
    const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(disposition)?.[1]
    if (star) {
      try {
        return decodeURIComponent(star.trim())
      } catch {
        /* 百分号序列非法时走 filename 回退 */
      }
    }
    const plain = /filename\s*=\s*(?:"([^"]*)"|([^;\s]+))/i.exec(disposition)
    const name = (plain?.[1] ?? plain?.[2] ?? '').trim()
    if (name) return name
  }
  return bookUrl.replace(/^local:\/\//, '').split('#')[0] || 'book'
}

/** 生成临时 object URL 触发保存；延迟回收，给浏览器下载留出取引用的时间。 */
const saveBlob = (fileName: string, blob: Blob) => {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url))
}

// ---------- 书签、进度与配置 ----------

export const getBookmarks = (bookUrl: string) =>
  postJson<Bookmark[]>('getBookmarks', { bookUrl })

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

/**
 * 页面关闭/切后台时尽力保存进度。sendBeacon 无法携带 Authorization header，
 * 改用 keepalive fetch：header 与 JSON body 齐全，页面卸载后仍能完成请求。
 */
export const saveProgressKeepalive = (progress: BookProgress): void => {
  void fetch(`${baseUrl}/saveBookProgress`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getPassword()}` },
    body: JSON.stringify(progress),
    keepalive: true,
  }).catch(() => {})
}

// ---------- 认证资源加载（fetch+blob，参数与凭证均不进 URL） ----------

/** 书籍封面（EPUB 内嵌封面或按标题生成的 SVG 占位图）。 */
export const fetchCover = (path: string, signal?: AbortSignal) =>
  postBlob('cover', { path }, signal)

/** EPUB 内嵌图片资源。 */
export const fetchEpubImage = (bookUrl: string, src: string, signal?: AbortSignal) =>
  postBlob('image', { url: bookUrl, path: src }, signal)

// ---------- WebSocket 搜索 ----------

export const searchBooks = (
  key: string,
  onReceive: (books: SearchBook[]) => void,
  onFinish: (closeCode?: number) => void,
) => {
  // 浏览器无法在 WS 握手时携带自定义 header，密码随首条消息发送，
  // 不再拼进握手 URL；开放模式下 password 字段为 undefined，序列化时自动省略。
  const socket = new WebSocket(`${wsEntryPoint()}/searchBook`)
  socket.onopen = () => socket.send(JSON.stringify({ key, password: getPassword() || undefined }))
  socket.onmessage = event => {
    try {
      onReceive(JSON.parse(event.data))
    } catch {
      /* 非 JSON 消息忽略 */
    }
  }
  // closeCode 1008 = 密码错误/缺失（服务端 PolicyViolation）
  socket.onclose = event => onFinish(event.code)
  socket.onerror = () => onFinish()
  return () => socket.close()
}
