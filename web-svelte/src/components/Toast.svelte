<script lang="ts">
  import { toasts } from '../lib/toast.svelte'
</script>

<!-- MD3 Snackbar：底部居中、inverse surface 配色 -->
<div class="snackbar-container">
  {#each toasts.items as item (item.id)}
    <div class="snackbar" class:error={item.type === 'error'}>
      {item.message}
    </div>
  {/each}
</div>

<style>
  .snackbar-container {
    position: fixed;
    bottom: 80px;
    left: 50%;
    transform: translateX(-50%);
    z-index: 1000;
    display: flex;
    flex-direction: column;
    gap: 8px;
    align-items: center;
    pointer-events: none;
  }

  .snackbar {
    min-width: 240px;
    max-width: 90vw;
    padding: 14px 16px;
    border-radius: var(--md-shape-xs);
    background: var(--md-inverse-surface);
    color: var(--md-inverse-on-surface);
    box-shadow: var(--md-elevation-3);
    font-size: 14px;
    animation: snackbar-in 0.25s cubic-bezier(0.2, 0, 0, 1);
  }

  .snackbar.error {
    background: var(--md-error);
    color: var(--md-on-error);
  }

  @keyframes snackbar-in {
    from {
      opacity: 0;
      transform: translateY(12px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  @media (max-width: 750px) {
    .snackbar-container {
      bottom: calc(120px + env(safe-area-inset-bottom, 0px));
    }
  }
</style>
