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

  // 全书进度百分比：与 BookCard 同口径，用章级估算做概览。
  const progressPercent = $derived.by(() => {
    if (!('durChapterIndex' in book)) return null
    const b = book as Book
    // 未打开过（无进度时间）不显示，避免未读书显示 1%。
    if (!b.durChapterTime || b.durChapterTime <= 0) return null
    // 超大 TXT 书架扫描时 totalChapterNum 可能为 0（延迟解析），此时不假装 100%。
    if (!b.totalChapterNum || b.totalChapterNum <= 0) return null
    // 章级概览：已抵达章（1-based）/ 总章数。读到第 1 章≈1/N，最后一章=100%。
    const idx = Math.max(0, Math.min(b.durChapterIndex ?? 0, b.totalChapterNum - 1))
    return Math.round(((idx + 1) / b.totalChapterNum) * 100)
  })
</script>

<!-- 封面宫格卡片：竖向封面 + 书名/作者 -->
<div
  class="card"
  class:selected
  role="button"
  tabindex="0"
  {onclick}
  onkeydown={e => e.key === 'Enter' && onclick()}
>
  <div class="cover-wrap">
    <img class="cover" src={cover} alt={book.name} loading="lazy" />
    {#if selecting}
      <span class="select-check" class:checked={selected} aria-hidden="true">
        {selected ? '✓' : ''}
      </span>
    {/if}
    {#if progressText}
      <span class="progress label-small">{progressText}</span>
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
    {#if progressPercent !== null && progressPercent > 0}
      <span
        class="progress-pct label-small"
        style="--p:{progressPercent}"
        aria-hidden="true"
      >{progressPercent}%</span>
    {/if}
  </div>
  <div class="name title-medium" title={book.name}>{book.name}</div>
  <div class="author label-medium">{book.author}</div>
</div>

<style>
  .card {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 6px;
    cursor: pointer;
  }

  .cover-wrap {
    position: relative;
    width: 100%;
    aspect-ratio: 3 / 4;
    border-radius: var(--md-shape-md);
    overflow: hidden;
    background: var(--md-surface-container-low);
    box-shadow: var(--md-elevation-1);
    transition: box-shadow 0.2s;
  }

  .card:hover .cover-wrap {
    box-shadow: var(--md-elevation-2);
  }

  .card.selected .cover-wrap {
    outline: 2px solid var(--md-primary);
    outline-offset: -2px;
  }

  .select-check {
    position: absolute;
    top: 6px;
    left: 6px;
    width: 22px;
    height: 22px;
    border-radius: var(--md-shape-full);
    border: 2px solid var(--md-outline);
    background: rgba(0, 0, 0, 0.45);
    color: #fff;
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
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
    background: var(--md-surface-container-highest);
  }

  .progress {
    position: absolute;
    left: 0;
    bottom: 0;
    right: 0;
    padding: 4px 6px;
    background: linear-gradient(transparent, rgba(0, 0, 0, 0.6));
    color: #fff;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-size: 11px;
  }

  /* 右下角进度百分比徽标：与封面角标互补，仅在已有阅读进度时出现。 */
  .progress-pct {
    position: absolute;
    right: 6px;
    top: 6px;
    min-width: 30px;
    height: 18px;
    padding: 0 6px;
    border-radius: var(--md-shape-full);
    background: color-mix(in srgb, var(--md-primary) 80%, transparent);
    color: var(--md-on-primary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 10px;
    font-weight: 600;
  }

  .name {
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    color: var(--md-on-surface);
    line-height: 1.3;
    /* 预留两行高度，避免有无副标题时卡片高度跳动 */
    min-height: 2.6em;
  }

  .author {
    color: var(--md-on-surface-variant);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-size: 12px;
  }

  .delete {
    position: absolute;
    top: 6px;
    right: 6px;
    width: 32px;
    height: 32px;
    opacity: 0;
    background: rgba(0, 0, 0, 0.45);
    color: #fff;
    transition: opacity 0.15s;
  }

  .card:hover .delete,
  .card:focus-within .delete {
    opacity: 1;
  }

  /* 触屏设备无 hover，删除按钮常显（半透明弱化） */
  @media (hover: none) {
    .delete {
      opacity: 0.7;
    }
  }

  .delete:hover {
    color: var(--md-error);
  }
</style>
