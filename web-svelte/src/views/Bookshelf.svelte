<script lang="ts">
  import type { Book, SearchBook } from '../lib/types'
  import * as api from '../lib/api'
  import { toast } from '../lib/toast.svelte'
  import { navigate } from '../lib/router.svelte'
  import { startReading, loadConfig, lastReadBook } from '../lib/reader.svelte'
  import { colorScheme, cycleColorScheme } from '../lib/colorScheme.svelte'
  import BookCard from '../components/BookCard.svelte'
  import BookGridCard from '../components/BookGridCard.svelte'
  import ConnectDialog from '../components/ConnectDialog.svelte'

  type ShelfView = 'list' | 'grid'
  type ShelfSort = 'recent' | 'imported'

  const SHELF_VIEW_KEY = 'shelfView'
  const SHELF_SORT_KEY = 'shelfSort'

  let shelf = $state<Book[]>([])
  let searchResults = $state<SearchBook[]>([])
  let searchWord = $state('')
  let searching = $state(false)
  let loading = $state(true)
  let connected = $state(false)
  let connectDialogOpen = $state(false)
  let uploading = $state(false)

  let shelfView = $state<ShelfView>(
    localStorage.getItem(SHELF_VIEW_KEY) === 'grid' ? 'grid' : 'list',
  )
  let shelfSort = $state<ShelfSort>(
    localStorage.getItem(SHELF_SORT_KEY) === 'imported' ? 'imported' : 'recent',
  )

  // 批量选择模式：进入后卡片显示勾选框，点击切换选中；FAB 切换为「删除选中」。
  let selectionMode = $state(false)
  let selectedUrls = $state<Set<string>>(new Set())

  const enterSelection = () => {
    selectionMode = true
    selectedUrls = new Set()
  }

  const exitSelection = () => {
    selectionMode = false
    selectedUrls = new Set()
  }

  const toggleSelect = (bookUrl: string) => {
    const next = new Set(selectedUrls)
    if (next.has(bookUrl)) next.delete(bookUrl)
    else next.add(bookUrl)
    selectedUrls = next
  }

  const deleteSelected = async () => {
    if (selectedUrls.size === 0) return
    if (!confirm(`删除选中的 ${selectedUrls.size} 本书籍？文件将移入回收站。`)) return
    uploading = true
    try {
      let failed = 0
      for (const book of shelf.filter(b => selectedUrls.has(b.bookUrl))) {
        try {
          const resp = await api.deleteBook(book)
          if (!resp.isSuccess) failed++
        } catch {
          failed++
        }
      }
      await loadShelf()
      exitSelection()
      if (failed > 0) toast(`${failed} 本删除失败`, 'error')
      else toast('已删除选中书籍', 'success')
    } finally {
      uploading = false
    }
  }

  const toggleShelfView = () => {
    shelfView = shelfView === 'list' ? 'grid' : 'list'
    localStorage.setItem(SHELF_VIEW_KEY, shelfView)
  }

  const cycleShelfSort = () => {
    shelfSort = shelfSort === 'recent' ? 'imported' : 'recent'
    localStorage.setItem(SHELF_SORT_KEY, shelfSort)
  }

  const shelfSortLabel = $derived(
    shelfSort === 'recent' ? '最近阅读' : '导入时间',
  )

  // 书架按选定维度排序；在线搜索结果（SearchBook 无时间字段）保持后端返回顺序。
  const sortedShelf = $derived.by<Book[]>(() => {
    if (shelfSort === 'imported') {
      return shelf.toSorted((a, b) => {
        const diff = (b.importedAt ?? 0) - (a.importedAt ?? 0)
        if (diff !== 0) return diff
        return a.name.localeCompare(b.name, 'zh')
      })
    }
    return shelf.toSorted((a, b) => b.durChapterTime - a.durChapterTime)
  })

  // 本地过滤：搜索词非空但未触发在线搜索时，按书名/作者过滤书架
  const visibleBooks = $derived.by<(Book | SearchBook)[]>(() => {
    if (searching) return searchResults
    if (!searchWord) return sortedShelf
    return sortedShelf.filter(
      b => b.name.includes(searchWord) || b.author.includes(searchWord),
    )
  })

  const loadShelf = async () => {
    loading = true
    try {
      const resp = await api.getBookshelf()
      if (resp.isSuccess) {
        shelf = resp.data
        connected = true
      } else {
        toast(resp.errorMsg, 'error')
      }
    } catch {
      connected = false
      toast('无法连接后端，请检查连接设置', 'error')
    } finally {
      loading = false
    }
  }

  $effect(() => {
    loadConfig()
    loadShelf()
  })

  let closeSearch: (() => void) | null = null
  const searchOnline = () => {
    if (!searchWord) return
    searching = true
    searchResults = []
    closeSearch?.()
    closeSearch = api.searchBooks(
      searchWord,
      books => {
        searchResults = [...searchResults, ...books]
      },
      () => {
        if (searchResults.length === 0) toast('搜索结果为空')
      },
    )
  }

  $effect(() => {
    // 清空搜索词时退出在线搜索模式
    if (searchWord === '' && searching) {
      searching = false
      searchResults = []
      closeSearch?.()
    }
  })

  const recentBook = lastReadBook()
  const openRecent = () => {
    if (!recentBook) return
    // 优先用书架上的最新条目（进度可能被其他设备更新过）
    const inShelf = shelf.find(b => b.bookUrl === recentBook.bookUrl)
    startReading(inShelf ?? recentBook)
    navigate('/chapter')
  }

  const openBook = async (book: Book | SearchBook) => {
    // 搜索结果先入库再打开（与 legado 行为一致）
    if (searching) {
      await api.saveBook(book).catch(() => {})
      await loadShelf()
      const inShelf = shelf.find(b => b.bookUrl === book.bookUrl)
      if (inShelf) {
        startReading(inShelf)
        navigate('/chapter')
      }
      return
    }
    startReading(book as Book)
    navigate('/chapter')
  }

  const removeBook = async (book: Book) => {
    if (!confirm(`删除《${book.name}》？文件将移入回收站（books/.trash/）。`)) return
    try {
      const resp = await api.deleteBook(book)
      if (resp.isSuccess) {
        toast('已删除', 'success')
        await loadShelf()
      } else {
        toast(resp.errorMsg, 'error')
      }
    } catch {
      toast('删除失败', 'error')
    }
  }

  const pickAndUpload = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.txt,.epub'
    input.multiple = true
    input.onchange = async () => {
      const files = Array.from(input.files ?? [])
      if (files.length === 0) return
      uploading = true
      try {
        // 导入前先列出已有书名，对重名给出明确提示而非「同名已存在」。
        const existing = new Set(shelf.map(b => b.bookUrl))
        const dupes = files.filter(f => existing.has(`local://${f.name}`))
        if (dupes.length > 0 && !confirm(`已有同名书籍 ${dupes.length} 本，是否覆盖导入？`)) {
          return
        }
        let resp = await api.uploadBook(files, dupes.length > 0)
        if (!resp.isSuccess) {
          if (!confirm(`${resp.errorMsg}\n是否覆盖导入？`)) return
          resp = await api.uploadBook(files, true)
          if (!resp.isSuccess) {
            toast(resp.errorMsg, 'error')
            return
          }
        }
        toast(`成功导入 ${files.length} 本书籍`, 'success')
        await loadShelf()
      } catch {
        toast('导入失败，请检查网络与后端状态', 'error')
      } finally {
        uploading = false
      }
    }
    input.click()
  }
</script>

<div class="page">
  <!-- MD3 Center-aligned top app bar -->
  <header class="top-app-bar">
    <span class="app-title title-large">阅读</span>
    <button
      class="btn-icon sort-btn"
      title={`排序：${shelfSortLabel}`}
      aria-label="切换书库排序"
      onclick={cycleShelfSort}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
        <path
          d="M3 6h13v2H3V6zm0 5h10v2H3v-2zm0 5h7v2H3v-2zm17-2.5 3.5 3.5-1.4 1.4-1.1-1.1V20h-2v-3.7l-1.1 1.1-1.4-1.4 3.5-3.5z"
        />
      </svg>
    </button>
    <button
      class="btn-icon view-btn"
      title={shelfView === 'list' ? '视图：列表' : '视图：宫格'}
      aria-label="切换书库视图"
      onclick={toggleShelfView}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
        {#if shelfView === 'list'}
          <!-- 列表 -->
          <path
            d="M3 5h18v2H3V5zm0 6h18v2H3v-2zm0 6h18v2H3v-2z"
          />
        {:else}
          <!-- 宫格 -->
          <path
            d="M3 3h7v7H3V3zm11 0h7v7h-7V3zM3 14h7v7H3v-7zm11 0h7v7h-7v-7z"
          />
        {/if}
      </svg>
    </button>
    <button
      class="btn-icon scheme-toggle"
      title={colorScheme.mode === 'auto'
        ? '配色：跟随系统'
        : colorScheme.mode === 'dark'
          ? '配色：深色'
          : '配色：浅色'}
      aria-label="切换深浅配色"
      onclick={cycleColorScheme}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
        {#if colorScheme.mode === 'auto'}
          <!-- 半明半暗：跟随系统 -->
          <path
            d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18V4c4.41 0 8 3.59 8 8s-3.59 8-8 8z"
          />
        {:else if colorScheme.mode === 'dark'}
          <!-- 月亮：深色 -->
          <path
            d="M9.37 5.51c-.18.64-.27 1.31-.27 1.99 0 4.08 3.32 7.4 7.4 7.4.68 0 1.35-.09 1.99-.27C17.45 17.19 14.93 19 12 19c-3.86 0-7-3.14-7-7 0-2.93 1.81-5.45 4.37-6.49zM12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9 9-4.03 9-9c0-.46-.04-.92-.1-1.36-.98 1.37-2.58 2.26-4.4 2.26-2.98 0-5.4-2.42-5.4-5.4 0-1.81.89-3.42 2.26-4.4-.44-.06-.9-.1-1.36-.1z"
          />
        {:else}
          <!-- 太阳：浅色 -->
          <path
            d="M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0 .39-.39.39-1.03 0-1.41l-1.06-1.06zm1.06-10.96c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z"
          />
        {/if}
      </svg>
    </button>
    <button
      class="btn-icon connect-btn"
      title={connected ? '已连接后端' : '连接后端'}
      aria-label="连接设置"
      onclick={() => (connectDialogOpen = true)}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
        {#if connected}
          <path
            d="M1 9l2 2c4.97-4.97 13.03-4.97 18 0l2-2C16.93 2.93 7.08 2.93 1 9zm8 8l3 3 3-3c-1.65-1.66-4.34-1.66-6 0zm-4-4l2 2c2.76-2.76 7.24-2.76 10 0l2-2C15.14 9.14 8.87 9.14 5 13z"
          />
        {:else}
          <path
            d="M22.99 9C19.15 5.16 13.8 3.76 8.84 4.78l2.52 2.52c3.47-.17 6.99 1.05 9.63 3.7l2-2zm-4 4c-1.29-1.29-2.84-2.13-4.49-2.56l3.53 3.53.96-.97zM2 3.05L5.07 6.1C3.6 6.82 2.22 7.78 1 9l1.99 2c1.24-1.24 2.67-2.16 4.2-2.77l2.24 2.24C7.81 10.89 6.27 11.73 5 13v.01L6.99 15c1.36-1.36 3.14-2.04 4.92-2.06L18.98 20l1.27-1.26L3.29 1.79 2 3.05zM9 17l3 3 3-3c-1.65-1.66-4.34-1.66-6 0z"
          />
        {/if}
      </svg>
    </button>
  </header>

  <!-- MD3 Search bar -->
  <div class="search-row">
    <div class="search-bar">
      <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor" class="search-icon">
        <path
          d="M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"
        />
      </svg>
      <input
        class="search-input"
        placeholder="搜索书籍，回车在线搜索"
        bind:value={searchWord}
        onkeydown={e => e.key === 'Enter' && searchOnline()}
      />
    </div>
  </div>

  {#if recentBook && !searchWord}
    <div class="recent-row">
      <span class="label-medium recent-label">最近阅读</span>
      <button class="btn-tonal recent-chip" onclick={openRecent}>
        {recentBook.name}
      </button>
    </div>
  {/if}

  <main class="shelf">
    {#if loading}
      <div class="empty body-medium">正在获取书籍信息…</div>
    {:else if visibleBooks.length === 0}
      <div class="empty body-medium">
        {searching ? '搜索中…' : '书架空空如也，点击右下角 + 导入书籍'}
      </div>
    {:else}
      <div class="grid" class:grid-cover={shelfView === 'grid'}>
        {#each visibleBooks as book (book.bookUrl)}
          {#if shelfView === 'grid'}
            <BookGridCard
              {book}
              selected={selectionMode && selectedUrls.has(book.bookUrl)}
              selecting={selectionMode}
              onclick={() =>
                selectionMode ? toggleSelect(book.bookUrl) : openBook(book)}
              ondelete={searching || selectionMode
                ? undefined
                : () => removeBook(book as Book)}
            />
          {:else}
            <BookCard
              {book}
              selected={selectionMode && selectedUrls.has(book.bookUrl)}
              selecting={selectionMode}
              onclick={() =>
                selectionMode ? toggleSelect(book.bookUrl) : openBook(book)}
              ondelete={searching || selectionMode
                ? undefined
                : () => removeBook(book as Book)}
            />
          {/if}
        {/each}
      </div>
    {/if}
  </main>

  {#if selectionMode}
    <div class="selection-bar">
      <button class="btn-text" onclick={exitSelection}>取消</button>
      <span class="selection-count body-medium">
        已选 {selectedUrls.size} 本
      </span>
      <button
        class="btn-text delete-selected"
        disabled={selectedUrls.size === 0 || uploading}
        onclick={deleteSelected}
      >删除</button>
    </div>
  {/if}

  <!-- MD3 FAB：导入书籍 / 批量管理 -->
  <button
    class="fab"
    title={selectionMode ? '批量管理' : '导入书籍（.txt / .epub）'}
    aria-label={selectionMode ? '批量管理' : '导入书籍'}
    disabled={uploading}
    onclick={() => (selectionMode ? exitSelection() : pickAndUpload())}
  >
    {#if uploading}
      <span class="label-medium">导入中…</span>
    {:else if selectionMode}
      <span class="label-medium">完成</span>
    {:else}
      <svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor">
        <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" />
      </svg>
    {/if}
  </button>

  <!-- 非选择模式 + 非搜索时显示批量入口 -->
  {#if !selectionMode && !searching && shelf.length > 0}
    <button
      class="fab fab-secondary"
      title="批量管理"
      aria-label="批量管理"
      onclick={enterSelection}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor">
        <path d="M3 5h2v2H3V5zm0 6h2v2H3v-2zm0 6h2v2H3v-2zM7 5h14v2H7V5zm0 6h14v2H7v-2zm0 6h14v2H7v-2z" />
      </svg>
    </button>
  {/if}
</div>

<ConnectDialog bind:open={connectDialogOpen} onconnected={loadShelf} />

<style>
  .page {
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--md-surface);
  }

  .top-app-bar {
    height: calc(64px + env(safe-area-inset-top, 0px));
    padding-top: env(safe-area-inset-top, 0px);
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
    background: var(--md-surface);
  }

  .app-title {
    color: var(--md-on-surface);
  }

  .top-app-bar .connect-btn {
    position: absolute;
    right: 12px;
  }

  .top-app-bar .scheme-toggle {
    position: absolute;
    right: 60px;
  }

  .top-app-bar .view-btn {
    position: absolute;
    right: 108px;
  }

  .top-app-bar .sort-btn {
    position: absolute;
    right: 156px;
  }

  .search-row {
    padding: 4px 16px 12px;
    display: flex;
    justify-content: center;
  }

  .search-bar {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    max-width: 560px;
    height: 48px;
    padding: 0 16px;
    background: var(--md-surface-container-high);
    border-radius: var(--md-shape-full);
  }

  .search-icon {
    color: var(--md-on-surface-variant);
    flex-shrink: 0;
  }

  .search-input {
    border: none;
    padding: 0;
    background: transparent;
    height: 100%;
  }

  .search-input:focus {
    border: none;
    padding: 0;
  }

  .recent-row {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 0 24px 8px;
    max-width: 1200px;
    margin: 0 auto;
    width: 100%;
  }

  .recent-label {
    color: var(--md-on-surface-variant);
  }

  .recent-chip {
    font-size: 13px;
    padding: 6px 16px;
    max-width: 60vw;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .shelf {
    flex: 1;
    overflow-y: auto;
    padding: 8px 24px 100px;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 12px;
    max-width: 1200px;
    margin: 0 auto;
  }

  .grid.grid-cover {
    grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
    gap: 16px;
  }

  /* grid 项默认 min-width:auto，卡片内 nowrap 文本会把卡片撑出视口；置 0 允许收缩出省略号 */
  .grid > :global(*) {
    min-width: 0;
  }

  .empty {
    color: var(--md-on-surface-variant);
    text-align: center;
    margin-top: 80px;
  }

  .fab {
    position: fixed;
    right: 24px;
    bottom: calc(24px + env(safe-area-inset-bottom, 0px));
    min-width: 56px;
    height: 56px;
    padding: 0 16px;
    border-radius: var(--md-shape-lg);
    background: var(--md-primary-container);
    color: var(--md-on-primary-container);
    box-shadow: var(--md-elevation-2);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 30;
  }

  .fab:hover {
    box-shadow: var(--md-elevation-3);
    opacity: 1;
  }

  /* 次级 FAB：批量管理入口，错开主 FAB */
  .fab-secondary {
    right: 24px;
    bottom: calc(88px + env(safe-area-inset-bottom, 0px));
    background: var(--md-secondary-container);
    color: var(--md-on-secondary-container);
    min-width: 48px;
    height: 48px;
    padding: 0 12px;
  }

  /* 选择模式底栏：取消 + 计数 + 删除 */
  .selection-bar {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    padding: 8px 16px calc(8px + env(safe-area-inset-bottom, 0px));
    background: var(--md-surface-container);
    color: var(--md-on-surface);
    box-shadow: var(--md-elevation-2);
    display: flex;
    align-items: center;
    justify-content: space-between;
    z-index: 40;
  }

  .selection-count {
    color: var(--md-on-surface-variant);
  }

  .selection-bar .delete-selected {
    color: var(--md-error);
  }

  @media (max-width: 750px) {
    .shelf {
      padding: 8px 12px 100px;
    }

    .grid {
      grid-template-columns: 1fr;
    }

    .grid.grid-cover {
      grid-template-columns: repeat(auto-fill, minmax(110px, 1fr));
      gap: 12px;
    }
  }
</style>
