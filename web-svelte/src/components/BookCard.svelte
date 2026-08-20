<script lang="ts">
  import type { Book, SearchBook } from '../lib/types'
  import { coverUrl } from '../lib/api'

  let {
    book,
    onclick,
    ondelete,
    selected = false,
    selecting = false,
  }: {
    book: Book | SearchBook
    onclick: () => void
    ondelete?: () => void
    selected?: boolean
    selecting?: boolean
  } = $props()

  const cover = $derived(
    book.coverUrl && /^https?:/.test(book.coverUrl)
      ? book.coverUrl
      : coverUrl(book.coverUrl || book.bookUrl),
  )

  const progressText = $derived.by(() => {
    if (!('durChapterTitle' in book)) return ''
    const b = book as Book
    return b.durChapterTime > 0 ? `读至：${b.durChapterTitle}` : '未开始阅读'
  })

  // 全书进度百分比：用 durChapterIndex 与 totalChapterNum 估算。
  // 与阅读器内的章内进度环（chapterPos/contentLength）不同口径，这里只到章级，
  // 用于卡片快速概览；阅读器内展示精确进度。
  const progressPercent = $derived.by(() => {
    if (!('durChapterIndex' in book)) return null
    const b = book as Book
    // 未打开过（无进度时间）不显示 0%/1%，避免「未读却像读过」。
    if (!b.durChapterTime || b.durChapterTime <= 0) return null
    // 超大 TXT 书架扫描时 totalChapterNum 可能为 0（延迟解析），此时不假装 100%。
    if (!b.totalChapterNum || b.totalChapterNum <= 0) return null
    // 章级概览：已抵达章（1-based）/ 总章数。读到第 1 章≈1/N，最后一章=100%。
    const idx = Math.max(0, Math.min(b.durChapterIndex ?? 0, b.totalChapterNum - 1))
    return Math.round(((idx + 1) / b.totalChapterNum) * 100)
  })
</script>

<!-- MD3 Elevated card -->
<div
  class="card"
  class:selected
  role="button"
  tabindex="0"
  {onclick}
  onkeydown={e => e.key === 'Enter' && onclick()}
>
  {#if selecting}
    <span class="select-check" class:checked={selected} aria-hidden="true">
      {selected ? '✓' : ''}
    </span>
  {/if}
  <img class="cover" src={cover} alt={book.name} loading="lazy" />
  <div class="info">
    <div class="name title-medium" title={book.name}>{book.name}</div>
    <div class="author body-medium">{book.author}</div>
    {#if progressText}
      <div class="progress label-medium">{progressText}</div>
    {/if}
    <div class="latest label-medium">最新：{book.latestChapterTitle ?? ''}</div>
  </div>
  {#if progressPercent !== null}
    <div
      class="progress-ring"
      style="--p:{progressPercent}"
      title={`阅读进度 ${progressPercent}%`}
      aria-hidden="true"
    >
      <span class="progress-ring-pct label-medium">{progressPercent}%</span>
    </div>
  {/if}
  {#if ondelete}
    <button
      class="btn-icon delete"
      title="删除书籍（移入回收站）"
      aria-label="删除书籍"
      onclick={e => {
        e.stopPropagation()
        ondelete()
      }}
    >
      <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
        <path
          d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"
        />
      </svg>
    </button>
  {/if}
</div>

<style>
  .card {
    position: relative;
    display: flex;
    gap: 16px;
    padding: 16px;
    background: var(--md-surface-container-low);
    border-radius: var(--md-shape-md);
    box-shadow: var(--md-elevation-1);
    cursor: pointer;
    transition:
      box-shadow 0.2s,
      background 0.2s;
  }

  .card:hover {
    box-shadow: var(--md-elevation-2);
    background: var(--md-surface-container);
  }

  /* 批量选择态：左上勾选框 + 选中高亮描边 */
  .card.selected {
    outline: 2px solid var(--md-primary);
    outline-offset: -2px;
  }

  .select-check {
    position: absolute;
    top: 8px;
    left: 8px;
    width: 22px;
    height: 22px;
    border-radius: var(--md-shape-full);
    border: 2px solid var(--md-outline);
    background: var(--md-surface-container-low);
    color: var(--md-on-primary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 13px;
    font-weight: 700;
    z-index: 1;
  }

  .select-check.checked {
    background: var(--md-primary);
    border-color: var(--md-primary);
  }

  .cover {
    width: 72px;
    height: 96px;
    object-fit: cover;
    border-radius: var(--md-shape-sm);
    flex-shrink: 0;
    background: var(--md-surface-container-highest);
  }

  .info {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
    /* 给右上角删除图标留出空间，长标题不与其重叠 */
    padding-right: 28px;
  }

  .name {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--md-on-surface);
  }

  .author,
  .latest {
    color: var(--md-on-surface-variant);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .progress {
    color: var(--md-primary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* 右下角进度环：conic-gradient 环形进度 + 中心百分比。
     仅在章级百分比可计算时显示，配合「读至：xxx」文字提供概览。 */
  .progress-ring {
    position: absolute;
    right: 12px;
    bottom: 12px;
    width: 40px;
    height: 40px;
    border-radius: var(--md-shape-full);
    background: conic-gradient(
      var(--md-primary) calc(var(--p) * 1%),
      var(--md-surface-container-highest) 0
    );
    display: flex;
    align-items: center;
    justify-content: center;
    box-shadow: var(--md-elevation-1);
  }

  .progress-ring::before {
    content: '';
    position: absolute;
    inset: 3px;
    border-radius: var(--md-shape-full);
    background: var(--md-surface-container-low);
  }

  .progress-ring-pct {
    position: relative;
    color: var(--md-on-surface-variant);
    font-size: 10px;
    font-weight: 600;
  }

  .delete {
    position: absolute;
    top: 8px;
    right: 8px;
    width: 32px;
    height: 32px;
    opacity: 0;
    transition: opacity 0.15s;
  }

  .card:hover .delete,
  .card:focus-within .delete {
    opacity: 1;
  }

  /* 触屏设备无 hover，删除按钮常显（半透明弱化，不喧宾夺主） */
  @media (hover: none) {
    .delete {
      opacity: 0.55;
    }
  }

  .delete:hover {
    color: var(--md-error);
  }
</style>
