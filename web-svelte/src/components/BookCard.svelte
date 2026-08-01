<script lang="ts">
  import type { Book, SearchBook } from '../lib/types'
  import { coverUrl } from '../lib/api'

  let {
    book,
    onclick,
    ondelete,
  }: {
    book: Book | SearchBook
    onclick: () => void
    ondelete?: () => void
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
</script>

<!-- MD3 Elevated card -->
<div
  class="card"
  role="button"
  tabindex="0"
  {onclick}
  onkeydown={e => e.key === 'Enter' && onclick()}
>
  <img class="cover" src={cover} alt={book.name} loading="lazy" />
  <div class="info">
    <div class="name title-medium" title={book.name}>{book.name}</div>
    <div class="author body-medium">{book.author}</div>
    {#if progressText}
      <div class="progress label-medium">{progressText}</div>
    {/if}
    <div class="latest label-medium">最新：{book.latestChapterTitle ?? ''}</div>
  </div>
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
