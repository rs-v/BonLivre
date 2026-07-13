import axios from 'axios'

/** @type {string} localStorage保存自定义阅读http服务接口的键值 */
export const baseURL_localStorage_key = 'remoteUrl'
/** @type {string} localStorage保存后端访问密码的键值 */
export const password_localStorage_key = 'remotePassword'
const SECOND = 1000

/** 后端 HTTP 服务默认端口（见后端 Program.cs 的 UseUrls，WebSocket 端口为其 +1） */
const BACKEND_HTTP_PORT = 5000

/**
 * 推断默认后端地址。
 * 生产环境下前端由后端静态托管，location.origin 即后端地址；
 * 开发环境下 Vite 在 8080 提供前端、后端在 5000，
 * 因此沿用当前主机名（同时兼容 localhost 与局域网 IP），仅替换为后端端口。
 */
const defaultBackendUrl = () =>
  import.meta.env.DEV
    ? `${location.protocol}//${location.hostname}:${BACKEND_HTTP_PORT}`
    : location.origin

const ajax = axios.create({
  baseURL:
    import.meta.env.VITE_API ||
    localStorage.getItem(baseURL_localStorage_key) ||
    defaultBackendUrl(),
  timeout: 120 * SECOND,
})

/** 读取当前保存的后端访问密码（未设置返回空串） */
export const getAuthPassword = () =>
  localStorage.getItem(password_localStorage_key) ?? ''

/**
 * 设置后端访问密码。
 * HTTP 请求通过 Authorization: Bearer <pw> header 携带；
 * WebSocket、<img src>、sendBeacon 等无法设置 header 的场景由 api.ts 追加 ?password= query。
 * 传入空串则清除密码。
 */
export const setAuthPassword = (password: string) => {
  if (password) {
    localStorage.setItem(password_localStorage_key, password)
    ajax.defaults.headers.common['Authorization'] = `Bearer ${password}`
  } else {
    localStorage.removeItem(password_localStorage_key)
    delete ajax.defaults.headers.common['Authorization']
  }
}

// 初始化：应用已保存的密码
setAuthPassword(getAuthPassword())

export default ajax
