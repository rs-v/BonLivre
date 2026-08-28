<script lang="ts">
  /**
   * 认证图片。本地封面 / EPUB 内嵌图的参数走 POST body、凭证走 Authorization header，
   * fetch 回 blob 后用 objectURL 展示——URL 中不再出现密码与书名。
   *
   * - 传 `src`：外部直连地址（第三方封面），直接渲染 <img>；
   * - 传 `loadBlob`：认证加载器，进入视口前 300px 才发起请求（替代原生懒加载），
   *   离开视口 1500px 后回收 blob——无限滚动模式下内存有界，滚回来自动重载。
   *
   * 注意：内层元素属于本组件模板，消费方样式须用 `:global(.xxx)` 锚定。
   * 宿主 span 用 display: contents 不参与布局，且必须稳定存在（回收观察器挂在它上面）。
   */
  let {
    src,
    loadBlob,
    alt = '',
    class: className = '',
  }: {
    /** 外部直连 URL，与 loadBlob 二选一 */
    src?: string
    /** 认证 blob 加载器；失败后保持占位图，不做自动重试（防失败风暴） */
    loadBlob?: (signal?: AbortSignal) => Promise<Blob>
    alt?: string
    class?: string
  } = $props()

  let target = $state<HTMLElement | null>(null)
  let objectUrl = $state<string | null>(null)
  // 加载武装标记：必须是 $state——回收后复位它才能重新触发上方 effect（普通变量无响应性）
  let armed = $state(true)
  let controller: AbortController | null = null

  // 进入视口前 300px 开始加载
  $effect(() => {
    if (!armed || src !== undefined || objectUrl !== null || !target || !loadBlob) return
    const io = new IntersectionObserver(
      entries => {
        if (entries.some(e => e.isIntersecting)) {
          io.disconnect()
          start()
        }
      },
      { rootMargin: '300px' },
    )
    io.observe(target)
    return () => io.disconnect()
  })

  async function start() {
    if (!loadBlob) return
    armed = false
    controller = new AbortController()
    try {
      const blob = await loadBlob(controller.signal)
      if (controller.signal.aborted) return
      objectUrl = URL.createObjectURL(blob)
    } catch {
      // 请求被 abort 属于正常回收；真实失败保持占位，避免对故障端点反复轰炸
    }
  }

  // 离开视口 1500px 后回收；进 300 / 出 1500 的迟滞防止边界抖动反复加载
  $effect(() => {
    if (src !== undefined || objectUrl === null || !target) return
    const io = new IntersectionObserver(
      entries => {
        for (const entry of entries) {
          if (!entry.isIntersecting) unload()
        }
      },
      { rootMargin: '1500px' },
    )
    io.observe(target)
    return () => io.disconnect()
  })

  function unload() {
    controller?.abort()
    controller = null
    if (objectUrl !== null) URL.revokeObjectURL(objectUrl)
    objectUrl = null
    armed = true
  }

  // 组件卸载时的最终回收（上面的观察器只覆盖「滚远了」的场景）
  $effect(() => () => {
    if (objectUrl !== null) URL.revokeObjectURL(objectUrl)
  })
</script>

{#if src !== undefined}
  <img class={className} {src} {alt} loading="lazy" />
{:else if objectUrl !== null}
  <img bind:this={target} class={className} src={objectUrl} {alt} loading="lazy" />
{:else}
  <!-- 占位元素带上同名 class：加载前后尺寸一致，消费方背景色即占位底色；它也是懒加载观察目标。 -->
  <span bind:this={target} class={className} aria-hidden="true"></span>
{/if}
