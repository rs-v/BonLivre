/** 极简 toast：全局单例状态，Toast.svelte 负责渲染 */

export type ToastItem = {
  id: number
  message: string
  type: 'info' | 'success' | 'error'
}

let nextId = 1

export const toasts = $state<{ items: ToastItem[] }>({ items: [] })

export const toast = (
  message: string,
  type: ToastItem['type'] = 'info',
  duration = 2500,
) => {
  const item: ToastItem = { id: nextId++, message, type }
  toasts.items.push(item)
  setTimeout(() => {
    const idx = toasts.items.findIndex(t => t.id === item.id)
    if (idx >= 0) toasts.items.splice(idx, 1)
  }, duration)
}
