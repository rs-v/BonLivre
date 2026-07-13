import type { AxiosResponse } from 'axios'
import type { LeagdoApiResponse } from './api'
import API, {
  setWebsocketOnError,
  setApiEntryPoint,
  legado_http_entry_point,
  setWebsocketOnMessage,
} from './api'
import ajax, { getAuthPassword } from './axios'
import { validatorHttpUrl } from '@/utils/utils'

import { createApp } from 'vue'
import App from '@/App.vue'
import store, { useConnectionStore } from '@/store'

createApp(App).use(store)
const connectionStore = useConnectionStore()

const LeagdoApiResponseKeys: string[] = Array.of('isSuccess', 'errorMsg')

const notification = ElMessage
/** Axios.Interceptor: check if resp is LeagaoLeagdoApiResponse*/
const responseCheckInterceptor = (resp: AxiosResponse) => {
  let isLeagdoApiResponse = true
  try {
    const data = resp.data

    for (const key of LeagdoApiResponseKeys) {
      if (!(key in data)) {
        isLeagdoApiResponse = false
        LeagdoApiResponseKeys.length = 0
      }
    }
    if ((data as LeagdoApiResponse<unknown>).isSuccess === true) {
      if (!('data' in data)) {
        isLeagdoApiResponse = false
      }
    }
  } catch {
    isLeagdoApiResponse = false
  }
  if (isLeagdoApiResponse === false) {
    notification.warning({ message: '后端返回内容格式错误', grouping: true })
    throw new Error()
  }
  connectionStore.setConnectType('primary')
  connectionStore.setConnectStatus('已连接 ' + legado_http_entry_point)
  return resp
}

const axiosErrorInterceptor = (err: unknown) => {
  // 区分 401（密码错误或未授权）与一般连接失败
  const status = (err as { response?: { status?: number } })?.response?.status
  if (status === 401) {
    notification.error({
      message: '未授权：后端密码错误或未设置，请在「连接」中填写正确密码',
      grouping: true,
    })
    connectionStore.setConnectType('danger')
    connectionStore.setConnectStatus('未授权')
  } else {
    notification.error({
      message: '后端连接失败，请检查阅读WEB服务或者设置其它可用链接',
      grouping: true,
    })
    connectionStore.setConnectType('danger')
    connectionStore.setConnectStatus('连接异常')
  }
  throw err
}
// http全局
ajax.interceptors.response.use(responseCheckInterceptor, axiosErrorInterceptor)
// websocket
// 浏览器出于安全不向 JS 暴露 WebSocket 握手的 HTTP 状态码（如 401），
// error 事件也没有 .response，因此无法像 HTTP 那样精确区分 401。
// 这里单独处理：已设置密码时，握手失败很可能是密码错误，给出针对性提示。
const websocketOnError: typeof WebSocket.prototype.onerror = event => {
  const message = getAuthPassword()
    ? '连接异常：可能是后端密码错误，或阅读WEB服务不可用'
    : '后端连接失败，请检查阅读WEB服务或者设置其它可用链接'
  notification.error({ message, grouping: true })
  connectionStore.setConnectType('danger')
  connectionStore.setConnectStatus('连接异常')
  return event
}
setWebsocketOnError(websocketOnError)
setWebsocketOnMessage(() => {
  connectionStore.setConnectType('primary')
  connectionStore.setConnectStatus('已连接 ' + legado_http_entry_point)
})
/**
 * 按照阅读的默认规则 解析阅读HTTP WebSocket API入口地址
 * @returns [http_url, webSocekt_url]
 */
export const parseLeagdoHttpUrlWithDefault = (
  http_url: string | URL,
): [string, string] => {
  let url = new URL(location.origin) //默认当前网址的origin部分
  if (validatorHttpUrl(http_url)) {
    url = new URL(http_url)
  }
  const { protocol, port } = url
  // websocket服务端口 为http服务端口 + 1
  let legado_webSocket_port
  if (port !== '') {
    legado_webSocket_port = String(Number(port) + 1)
  } else {
    legado_webSocket_port = protocol.startsWith('https:') ? '444' : '81'
  }
  // websocket协议是否为加密版本
  const legado_webSocket_protocol = protocol.startsWith('https:')
    ? 'wss://'
    : 'ws://'

  const http_entry_point = url.toString()

  url.protocol = legado_webSocket_protocol
  url.port = legado_webSocket_port
  const webSocket_entry_point = url.toString()

  console.info('legado_api_config:')
  console.table({
    'http API入口': http_entry_point,
    'webSocket API入口': webSocket_entry_point,
  })
  return [http_entry_point, webSocket_entry_point]
}

//export const useLeagdoRemoteUrlDialog = () => { }

setApiEntryPoint(
  ...parseLeagdoHttpUrlWithDefault(ajax.defaults.baseURL as string),
)

export default API
export * from './api'
