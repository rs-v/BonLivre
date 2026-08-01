<script lang="ts">
  import { route } from './lib/router.svelte'
  import Bookshelf from './views/Bookshelf.svelte'
  import Reader from './views/Reader.svelte'
  import Toast from './components/Toast.svelte'
</script>

{#if route.path.startsWith('/chapter')}
  <Reader />
{:else}
  <Bookshelf />
{/if}

<Toast />
