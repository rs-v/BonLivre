<script lang="ts">
  import * as api from '../lib/api'
  import { toast } from '../lib/toast.svelte'
  import { navigate } from '../lib/router.svelte'
  import {
    reading,
    restoreReading,
    saveProgress,
    saveConfig,
    loadConfig,
  } from '../lib/reader.svelte'
  import {
    themes,
    themeAt,
    fonts,
    resolveFontFamily,
    NIGHT_THEME_INDEX,
  } from '../lib/themes'

  type Paragraph = { text: string; img?: string; endPos: number }
  type LoadedChapter = { index: number; title: string; paragraphs: Paragraph[] }

  // 已加载章节列表：无限加载模式下会持续追加；普通模式始终只有一个元素
  let chapters = $state<LoadedChapter[]>([])
  let contentLoading = $state(true)
  let loadingMore = false
  let catalogOpen = $state(false)
  let settingsOpen = $state(false)
  let toolbarVisible = $state(true)
  let customFontInput = $state('')
  let contentEl = $state<HTMLElement | null>(null)
  let bottomSentinel = $state<HTMLElement | null>(null)
  let catalogListEl = $state<HTMLElement | null>(null)

  const theme = $derived(themeAt(reading.config.theme))
  const fontFamily = $derived(
    resolveFontFamily(reading.config.font, reading.config.customFontName),
  )

  // 段落切分。图片行（后端 ExtractTextWithImages 生成的 <img src="...">）转为图片段。
  // 进度按累计字数计（与旧 Vue 前端一致），+1 计换行。
  const IMG_RE = /^<img src="([^"]+)">$/
  const splitContent = (raw: string): Paragraph[] => {
    let pos = -1
    return raw
      .split(/\n+/)
      .map(line => line.trim())
      .filter(line => line.length > 0)
      .map(line => {
        pos += line.length + 1
        const img = IMG_RE.exec(line)?.[1]
        return { text: line, img, endPos: pos }
      })
  }

  const fetchChapter = async (index: number): Promise<LoadedChapter | null> => {
    const book = reading.book
    const chapter = reading.catalog[index]
    if (!book || !chapter) return null
    const resp = await api.getBookContent(book.bookUrl, chapter.index)
    if (!resp.isSuccess) {
      toast(resp.errorMsg, 'error')
      return { index, title: chapter.title, paragraphs: [] }
    }
    return { index, title: chapter.title, paragraphs: splitContent(resp.data) }
  }

  /** 跳转到指定章节（替换已加载内容），pos 为章内累计字数 */
  const loadChapter = async (index: number, pos = 0) => {
    contentLoading = true
    reading.chapterIndex = index
    reading.chapterPos = pos
    document.title = reading.catalog[index]?.title ?? document.title

    try {
      const loaded = await fetchChapter(index)
      if (!loaded) return
      chapters = [loaded]
      restoreScroll(loaded, pos)
    } catch {
      toast('获取章节内容失败', 'error')
    } finally {
      contentLoading = false
    }
  }

  /** 无限加载：不清空已读内容，向后追加下一章 */
  const loadMore = async () => {
    if (loadingMore || contentLoading) return
    const last = chapters.at(-1)
    if (!last || last.index + 1 >= reading.catalog.length) return
    loadingMore = true
    try {
      const loaded = await fetchChapter(last.index + 1)
      if (loaded) chapters = [...chapters, loaded]
    } catch {
      /* 滚动重新触发时重试 */
    } finally {
      loadingMore = false
    }
  }

  const restoreScroll = (chapter: LoadedChapter, pos: number) => {
    requestAnimationFrame(() => {
      if (!contentEl) return
      if (pos <= 0) {
        contentEl.scrollTop = 0
        return
      }
      const target = chapter.paragraphs.find(p => p.endPos >= pos)
      contentEl
        .querySelector<HTMLElement>(
          `[data-chapter="${chapter.index}"][data-pos="${target?.endPos}"]`,
        )
        ?.scrollIntoView({ block: 'start' })
    })
  }

  // 滚动时用 IntersectionObserver 跟踪阅读位置（章节序号 + 章内字数）
  $effect(() => {
    if (!contentEl || chapters.length === 0) return
    const observer = new IntersectionObserver(
      entries => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          const el = entry.target as HTMLElement
          const pos = Number(el.dataset.pos)
          const chapterIdx = Number(el.dataset.chapter)
          if (Number.isNaN(pos) || Number.isNaN(chapterIdx)) continue
          if (reading.chapterIndex !== chapterIdx) {
            reading.chapterIndex = chapterIdx
            document.title = reading.catalog[chapterIdx]?.title ?? document.title
          }
          reading.chapterPos = pos
          saveProgress(60_000)
        }
      },
      { root: contentEl, rootMargin: '0px 0px -80% 0px' },
    )
    for (const el of contentEl.querySelectorAll('[data-pos]')) observer.observe(el)
    return () => observer.disconnect()
  })

  // 无限加载：底部哨兵进入视口时追加下一章
  $effect(() => {
    if (!reading.config.infiniteLoading || !bottomSentinel || !contentEl) return
    const observer = new IntersectionObserver(
      entries => {
        if (entries.some(e => e.isIntersecting)) loadMore()
      },
      { root: contentEl, rootMargin: '200px' },
    )
    observer.observe(bottomSentinel)
    return () => observer.disconnect()
  })

  const toChapter = (index: number, pos = 0) => {
    if (index < 0) {
      toast('本章是第一章', 'error')
      return
    }
    if (index >= reading.catalog.length) {
      toast('本章是最后一章', 'error')
      return
    }
    catalogOpen = false
    loadChapter(index, pos)
    saveProgress()
  }

  // 目录打开时滚动到当前章节
  $effect(() => {
    if (!catalogOpen || !catalogListEl) return
    catalogListEl
      .querySelector('.catalog-item.active')
      ?.scrollIntoView({ block: 'center' })
  })

  const scrollPage = (direction: 1 | -1) => {
    if (!contentEl) return
    const delta = direction * (contentEl.clientHeight - 100)
    if (direction === -1 && contentEl.scrollTop === 0) {
      toast('已到达页面顶部')
      return
    }
    if (
      direction === 1 &&
      contentEl.scrollTop + contentEl.clientHeight >= contentEl.scrollHeight - 1
    ) {
      // 无限加载下滚到底会自动接下一章，这里只处理普通模式
      if (!reading.config.infiniteLoading) toast('已到达页面底部')
      return
    }
    contentEl.scrollBy({
      top: delta,
      behavior: reading.config.jumpDuration > 0 ? 'smooth' : 'auto',
    })
  }

  const toTop = () => contentEl?.scrollTo({ top: 0, behavior: 'smooth' })
  const toBottom = () =>
    contentEl?.scrollTo({ top: contentEl.scrollHeight, behavior: 'smooth' })

  // 键盘快捷键：←/→ 切章，↑/↓ 翻页
  const handleKey = (event: KeyboardEvent) => {
    if ((event.target as HTMLElement)?.tagName === 'INPUT') return
    switch (event.key) {
      case 'ArrowLeft':
        event.preventDefault()
        toChapter(reading.chapterIndex - 1)
        break
      case 'ArrowRight':
        event.preventDefault()
        toChapter(reading.chapterIndex + 1)
        break
      case 'ArrowUp':
        event.preventDefault()
        scrollPage(-1)
        break
      case 'ArrowDown':
        event.preventDefault()
        scrollPage(1)
        break
    }
  }

  const init = async () => {
    if (!restoreReading()) {
      navigate('/')
      return
    }
    await loadConfig()
    customFontInput = reading.config.customFontName
    const book = reading.book!
    try {
      const resp = await api.getChapterList(book.bookUrl)
      if (!resp.isSuccess || resp.data.length === 0) {
        toast('获取目录失败', 'error')
        return
      }
      reading.catalog = resp.data
      const index = Math.min(reading.chapterIndex, resp.data.length - 1)
      await loadChapter(index, reading.chapterPos)
    } catch {
      toast('获取目录失败，请检查后端连接', 'error')
    }
  }

  $effect(() => {
    init()
    const onVisibilityChange = () => {
      if (document.visibilityState === 'hidden') saveProgress()
    }
    document.addEventListener('visibilitychange', onVisibilityChange)
    window.addEventListener('keydown', handleKey)
    return () => {
      document.removeEventListener('visibilitychange', onVisibilityChange)
      window.removeEventListener('keydown', handleKey)
      saveProgress()
    }
  })

  const backToShelf = () => {
    saveProgress()
    navigate('/')
  }

  const setCustomFont = () => {
    reading.config.font = -1
    reading.config.customFontName = customFontInput.trim()
    saveConfig()
  }

  /** 数值设置项微调工具 */
  const adjust = (
    key: 'fontSize' | 'readWidth' | 'jumpDuration',
    delta: number,
    min: number,
    max: number,
  ) => {
    reading.config[key] = Math.min(max, Math.max(min, reading.config[key] + delta))
    saveConfig()
  }
  const adjustSpacing = (
    key: 'paragraph' | 'line' | 'letter',
    delta: number,
    min: number,
    max: number,
  ) => {
    reading.config.spacing[key] = Number(
      Math.min(max, Math.max(min, reading.config.spacing[key] + delta)).toFixed(2),
    )
    saveConfig()
  }
</script>

<!-- 夜间主题时挂 md-dark，底栏/抽屉/设置面板等 UI 跟随切深色令牌 -->
<div
  class="reader"
  style:background={theme.body}
  style:color={theme.text}
  class:md-dark={reading.config.theme === NIGHT_THEME_INDEX}
>
  <!-- MD3 Modal navigation drawer：目录 -->
  {#if catalogOpen}
    <div class="scrim" role="presentation" onclick={() => (catalogOpen = false)}>
      <nav
        class="drawer"
        role="presentation"
        onclick={e => e.stopPropagation()}
      >
        <div class="drawer-title title-medium">目录（{reading.catalog.length}）</div>
        <div class="catalog-list" bind:this={catalogListEl}>
          {#each reading.catalog as chapter, i (chapter.url)}
            <button
              class="catalog-item body-medium"
              class:active={i === reading.chapterIndex}
              class:volume={chapter.isVolume}
              onclick={() => toChapter(i)}
            >
              {chapter.title}
            </button>
          {/each}
        </div>
      </nav>
    </div>
  {/if}

  <!-- 正文：点击空白区域切换工具栏显隐 -->
  <div
    class="content-wrapper"
    bind:this={contentEl}
    role="presentation"
    onclick={() => {
      settingsOpen = false
      toolbarVisible = !toolbarVisible
    }}
  >
    <article
      class="content"
      style:background={theme.content}
      style:max-width="{reading.config.readWidth}px"
      style:font-size="{reading.config.fontSize}px"
      style:font-family={fontFamily}
      style:letter-spacing="{reading.config.spacing.letter}em"
      style:line-height={1 + reading.config.spacing.line}
      role="presentation"
      onclick={e => e.stopPropagation()}
    >
      {#if contentLoading}
        <p class="loading">加载中…</p>
      {:else}
        {#each chapters as chapter (chapter.index)}
          <h2 class="chapter-title">{chapter.title}</h2>
          {#each chapter.paragraphs as p (p.endPos)}
            {#if p.img}
              <p data-chapter={chapter.index} data-pos={p.endPos} class="img-p">
                <img
                  src={api.epubImageUrl(reading.book?.bookUrl ?? '', p.img)}
                  alt=""
                  loading="lazy"
                />
              </p>
            {:else}
              <p
                data-chapter={chapter.index}
                data-pos={p.endPos}
                style:margin="{reading.config.spacing.paragraph}em 0"
              >
                {p.text}
              </p>
            {/if}
          {/each}
        {/each}
        <div class="bottom-sentinel" bind:this={bottomSentinel}></div>
        {#if !reading.config.infiniteLoading}
          <div class="chapter-nav">
            <button class="btn-outlined" onclick={() => toChapter(reading.chapterIndex - 1)}
              >上一章</button
            >
            <button class="btn-outlined" onclick={() => toChapter(reading.chapterIndex + 1)}
              >下一章</button
            >
          </div>
        {/if}
      {/if}
    </article>
  </div>

  <!-- MD3 Bottom app bar -->
  {#if toolbarVisible}
    <div class="bottom-app-bar">
      <button class="bar-item" onclick={backToShelf} aria-label="返回书架">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-1 9H9V9h10v2zm-4 4H9v-2h6v2zm4-8H9V5h10v2z" />
        </svg>
        <span class="label-medium">书架</span>
      </button>
      <button
        class="bar-item"
        onclick={() => {
          settingsOpen = false
          catalogOpen = true
        }}
        aria-label="打开目录"
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M3 13h2v-2H3v2zm0 4h2v-2H3v2zm0-8h2V7H3v2zm4 4h14v-2H7v2zm0 4h14v-2H7v2zM7 7v2h14V7H7z" />
        </svg>
        <span class="label-medium">目录</span>
      </button>
      <button class="bar-item" onclick={() => toChapter(reading.chapterIndex - 1)} aria-label="上一章">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z" />
        </svg>
        <span class="label-medium">上一章</span>
      </button>
      <button class="bar-item" onclick={() => toChapter(reading.chapterIndex + 1)} aria-label="下一章">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z" />
        </svg>
        <span class="label-medium">下一章</span>
      </button>
      <button class="bar-item" onclick={toTop} aria-label="回到顶部">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M7.41 15.41 12 10.83l4.59 4.58L18 14l-6-6-6 6z" />
        </svg>
        <span class="label-medium">顶部</span>
      </button>
      <button class="bar-item" onclick={toBottom} aria-label="滚到底部">
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z" />
        </svg>
        <span class="label-medium">底部</span>
      </button>
      <button
        class="bar-item"
        onclick={() => {
          catalogOpen = false
          settingsOpen = !settingsOpen
        }}
        aria-label="打开设置"
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          <path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z" />
        </svg>
        <span class="label-medium">设置</span>
      </button>
      <button
        class="bar-item"
        aria-label="切换夜间模式"
        onclick={() => {
          reading.config.theme =
            reading.config.theme === NIGHT_THEME_INDEX ? 0 : NIGHT_THEME_INDEX
          saveConfig()
        }}
      >
        <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
          {#if reading.config.theme === NIGHT_THEME_INDEX}
            <path d="M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0 .39-.39.39-1.03 0-1.41l-1.06-1.06zm1.06-10.96c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z" />
          {:else}
            <path d="M9.37 5.51c-.18.64-.27 1.31-.27 1.99 0 4.08 3.32 7.4 7.4 7.4.68 0 1.35-.09 1.99-.27C17.45 17.19 14.93 19 12 19c-3.86 0-7-3.14-7-7 0-2.93 1.81-5.45 4.37-6.49zM12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9 9-4.03 9-9c0-.46-.04-.92-.1-1.36-.98 1.37-2.58 2.26-4.4 2.26-2.98 0-5.4-2.42-5.4-5.4 0-1.81.89-3.42 2.26-4.4-.44-.06-.9-.1-1.36-.1z" />
          {/if}
        </svg>
        <span class="label-medium">{reading.config.theme === NIGHT_THEME_INDEX ? '日间' : '夜间'}</span>
      </button>
    </div>
  {/if}

  <!-- MD3 Bottom sheet：阅读设置 -->
  {#if settingsOpen}
    <div class="sheet">
      <div class="sheet-handle"></div>
      <div class="setting-row">
        <span class="body-medium">阅读主题</span>
        <div class="theme-list">
          {#each themes as t, i (t.name)}
            <button
              class="theme-dot"
              class:active={reading.config.theme === i}
              style:background={t.content}
              title={t.name}
              aria-label={t.name}
              onclick={() => {
                reading.config.theme = i
                saveConfig()
              }}
            ></button>
          {/each}
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">正文字体</span>
        <div class="chip-list">
          {#each fonts as f, i (f.name)}
            <button
              class="chip"
              class:active={reading.config.font === i}
              onclick={() => {
                reading.config.font = i
                saveConfig()
              }}>{f.name}</button
            >
          {/each}
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">自定字体</span>
        <div class="custom-font">
          <input
            bind:value={customFontInput}
            placeholder="本机已安装的字体名"
            onclick={e => e.stopPropagation()}
          />
          <button
            class="chip"
            class:active={reading.config.font === -1}
            onclick={setCustomFont}>应用</button
          >
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">字体大小 {reading.config.fontSize}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小字号" onclick={() => adjust('fontSize', -1, 12, 36)}>−</button>
          <button class="btn-icon" aria-label="增大字号" onclick={() => adjust('fontSize', 1, 12, 36)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">字距 {reading.config.spacing.letter.toFixed(2)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小字距" onclick={() => adjustSpacing('letter', -0.01, 0, 0.5)}>−</button>
          <button class="btn-icon" aria-label="增大字距" onclick={() => adjustSpacing('letter', 0.01, 0, 0.5)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">行距 {reading.config.spacing.line.toFixed(1)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小行距" onclick={() => adjustSpacing('line', -0.1, 0.2, 3)}>−</button>
          <button class="btn-icon" aria-label="增大行距" onclick={() => adjustSpacing('line', 0.1, 0.2, 3)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">段距 {reading.config.spacing.paragraph.toFixed(1)}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小段距" onclick={() => adjustSpacing('paragraph', -0.1, 0, 3)}>−</button>
          <button class="btn-icon" aria-label="增大段距" onclick={() => adjustSpacing('paragraph', 0.1, 0, 3)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">页面宽度 {reading.config.readWidth}</span>
        <div class="stepper">
          <button class="btn-icon" aria-label="减小页宽" onclick={() => adjust('readWidth', -160, 480, 1600)}>−</button>
          <button class="btn-icon" aria-label="增大页宽" onclick={() => adjust('readWidth', 160, 480, 1600)}>＋</button>
        </div>
      </div>
      <div class="setting-row">
        <span class="body-medium">翻页动画</span>
        <button
          class="switch"
          class:on={reading.config.jumpDuration > 0}
          role="switch"
          aria-checked={reading.config.jumpDuration > 0}
          aria-label="翻页动画"
          onclick={() => {
            reading.config.jumpDuration = reading.config.jumpDuration > 0 ? 0 : 1000
            saveConfig()
          }}
        >
          <span class="thumb"></span>
        </button>
      </div>
      <div class="setting-row">
        <span class="body-medium">无限加载</span>
        <button
          class="switch"
          class:on={reading.config.infiniteLoading}
          role="switch"
          aria-checked={reading.config.infiniteLoading}
          aria-label="无限加载"
          onclick={() => {
            reading.config.infiniteLoading = !reading.config.infiniteLoading
            saveConfig()
            if (!reading.config.infiniteLoading)
              loadChapter(reading.chapterIndex, reading.chapterPos)
          }}
        >
          <span class="thumb"></span>
        </button>
      </div>
    </div>
  {/if}
</div>

<style>
  .reader {
    height: 100%;
    display: flex;
    flex-direction: column;
  }

  .content-wrapper {
    flex: 1;
    overflow-y: auto;
    padding: 24px 16px 100px;
  }

  .content {
    margin: 0 auto;
    padding: 40px 48px;
    border-radius: var(--md-shape-lg);
    min-height: 100%;
  }

  .chapter-title {
    margin-top: 0;
  }

  .content p {
    text-indent: 2em;
  }

  .img-p {
    text-indent: 0 !important;
    text-align: center;
  }

  .img-p img {
    max-width: 100%;
    border-radius: var(--md-shape-sm);
  }

  .loading {
    opacity: 0.6;
  }

  .bottom-sentinel {
    height: 1px;
  }

  .chapter-nav {
    display: flex;
    justify-content: space-between;
    margin-top: 40px;
  }

  /* Bottom app bar */
  .bottom-app-bar {
    position: fixed;
    bottom: 0;
    left: 0;
    right: 0;
    height: 72px;
    display: flex;
    justify-content: center;
    gap: 4px;
    padding: 6px 8px;
    background: var(--md-surface-container);
    color: var(--md-on-surface-variant);
    box-shadow: var(--md-elevation-2);
    z-index: 40;
    overflow-x: auto;
  }

  .bar-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    min-width: 60px;
    padding: 4px 8px;
    background: transparent;
    color: inherit;
    border-radius: var(--md-shape-lg);
    box-shadow: none;
  }

  .bar-item:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  /* Navigation drawer */
  .scrim {
    position: fixed;
    inset: 0;
    background: var(--md-scrim);
    z-index: 50;
  }

  .drawer {
    width: 320px;
    max-width: 85vw;
    height: 100%;
    background: var(--md-surface-container-low);
    color: var(--md-on-surface);
    border-radius: 0 var(--md-shape-lg) var(--md-shape-lg) 0;
    display: flex;
    flex-direction: column;
    box-shadow: var(--md-elevation-1);
    animation: drawer-in 0.25s cubic-bezier(0.2, 0, 0, 1);
  }

  @keyframes drawer-in {
    from {
      transform: translateX(-100%);
    }
    to {
      transform: translateX(0);
    }
  }

  .drawer-title {
    padding: 18px 24px 12px;
    color: var(--md-on-surface-variant);
  }

  .catalog-list {
    flex: 1;
    overflow-y: auto;
    padding: 0 12px 12px;
  }

  .catalog-item {
    display: block;
    width: 100%;
    text-align: left;
    background: transparent;
    color: var(--md-on-surface-variant);
    padding: 12px 16px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    border-radius: var(--md-shape-full);
    box-shadow: none;
  }

  .catalog-item:hover {
    background: color-mix(in srgb, var(--md-on-surface) 8%, transparent);
    box-shadow: none;
    opacity: 1;
  }

  .catalog-item.active {
    background: var(--md-secondary-container);
    color: var(--md-on-secondary-container);
    font-weight: 600;
  }

  .catalog-item.volume {
    font-weight: 600;
  }

  /* Bottom sheet 设置面板 */
  .sheet {
    position: fixed;
    bottom: 72px;
    left: 50%;
    transform: translateX(-50%);
    width: 96%;
    max-width: 480px;
    max-height: 65vh;
    overflow-y: auto;
    border-radius: var(--md-shape-xl) var(--md-shape-xl) 0 0;
    padding: 8px 24px 20px;
    background: var(--md-surface-container-low);
    color: var(--md-on-surface);
    box-shadow: var(--md-elevation-3);
    display: flex;
    flex-direction: column;
    gap: 4px;
    z-index: 60;
  }

  .sheet-handle {
    width: 32px;
    height: 4px;
    border-radius: var(--md-shape-full);
    background: var(--md-outline-variant);
    margin: 8px auto 12px;
    flex-shrink: 0;
  }

  .setting-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    min-height: 48px;
  }

  .theme-list,
  .chip-list {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  .theme-dot {
    width: 28px;
    height: 28px;
    border-radius: var(--md-shape-full);
    padding: 0;
    border: 2px solid var(--md-outline-variant);
    box-shadow: none;
  }

  .theme-dot.active {
    border-color: var(--md-primary);
    outline: 2px solid var(--md-primary);
    outline-offset: 1px;
  }

  .chip {
    font-size: 13px;
    padding: 6px 14px;
    background: transparent;
    color: var(--md-on-surface-variant);
    border: 1px solid var(--md-outline);
    border-radius: var(--md-shape-sm);
    box-shadow: none;
    white-space: nowrap;
    flex-shrink: 0;
  }

  .chip:hover {
    box-shadow: none;
  }

  .chip.active {
    background: var(--md-secondary-container);
    color: var(--md-on-secondary-container);
    border-color: transparent;
  }

  .custom-font {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: 1;
    max-width: 260px;
    min-width: 0;
  }

  .custom-font input {
    padding: 8px 12px;
    font-size: 13px;
    min-width: 0;
  }

  .stepper {
    display: flex;
    gap: 4px;
  }

  .stepper .btn-icon {
    border: 1px solid var(--md-outline-variant);
    font-size: 18px;
    width: 36px;
    height: 36px;
  }

  /* MD3 Switch */
  .switch {
    width: 52px;
    height: 32px;
    padding: 0;
    border-radius: var(--md-shape-full);
    background: var(--md-surface-container-highest);
    border: 2px solid var(--md-outline);
    position: relative;
    box-shadow: none;
    flex-shrink: 0;
  }

  .switch:hover {
    box-shadow: none;
  }

  .switch .thumb {
    position: absolute;
    top: 50%;
    left: 4px;
    transform: translateY(-50%);
    width: 16px;
    height: 16px;
    border-radius: var(--md-shape-full);
    background: var(--md-outline);
    transition:
      left 0.15s,
      width 0.15s,
      height 0.15s,
      background 0.15s;
  }

  .switch.on {
    background: var(--md-primary);
    border-color: var(--md-primary);
  }

  .switch.on .thumb {
    left: 24px;
    width: 24px;
    height: 24px;
    background: var(--md-on-primary);
  }

  @media (max-width: 750px) {
    .content {
      padding: 20px 16px;
      border-radius: 0;
    }

    .content-wrapper {
      padding: 0 0 100px;
    }

    /* 8 个操作均分整条底栏，杜绝横向滚动 */
    .bar-item {
      min-width: 0;
      flex: 1;
      padding: 4px 0;
    }

    .bar-item .label-medium {
      display: none;
    }

    .bottom-app-bar {
      height: calc(56px + env(safe-area-inset-bottom, 0px));
      padding-bottom: calc(6px + env(safe-area-inset-bottom, 0px));
      gap: 0;
    }

    .sheet {
      bottom: calc(56px + env(safe-area-inset-bottom, 0px));
      width: 100%;
      max-height: 60vh;
    }

    .drawer {
      width: 300px;
    }
  }
</style>
