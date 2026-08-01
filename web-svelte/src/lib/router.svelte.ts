/** 极简 hash 路由：'#/' 书架，'#/chapter' 阅读器 */

const parse = () => location.hash.replace(/^#/, '') || '/'

export const route = $state({ path: parse() })

window.addEventListener('hashchange', () => {
  route.path = parse()
})

export const navigate = (path: string) => {
  location.hash = path
}
