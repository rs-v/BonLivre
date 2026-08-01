<script lang="ts">
  import { getBaseUrl, getPassword, setBaseUrl, setPassword, getReadConfig } from '../lib/api'
  import { toast } from '../lib/toast.svelte'

  let {
    open = $bindable(false),
    onconnected,
  }: {
    open: boolean
    onconnected: () => void
  } = $props()

  let url = $state('')
  let password = $state('')
  let loading = $state(false)

  $effect(() => {
    if (open) {
      url = getBaseUrl()
      password = getPassword()
    }
  })

  const submit = async () => {
    let normalized: string
    try {
      normalized = new URL(url.trim()).toString()
    } catch {
      toast('后端地址格式不正确', 'error')
      return
    }

    loading = true
    // 校验前保存旧密码，失败回滚，避免错误密码覆盖后锁死后续请求
    const previousPassword = getPassword()
    setPassword(password.trim())
    try {
      await getReadConfig(normalized)
      setBaseUrl(normalized)
      open = false
      toast('连接成功', 'success')
      onconnected()
    } catch {
      setPassword(previousPassword)
      toast('连接失败，请检查地址与密码', 'error')
    } finally {
      loading = false
    }
  }
</script>

<!-- MD3 Basic dialog -->
{#if open}
  <div class="scrim" role="presentation" onclick={() => !loading && (open = false)}>
    <div
      class="dialog"
      role="dialog"
      aria-label="连接后端"
      tabindex="-1"
      onclick={e => e.stopPropagation()}
      onkeydown={e => e.key === 'Escape' && !loading && (open = false)}
    >
      <h3 class="title-large">连接后端</h3>
      <label>
        <span class="label-medium">后端地址</span>
        <input bind:value={url} placeholder="如：http://127.0.0.1:5000" disabled={loading} />
      </label>
      <label>
        <span class="label-medium">访问密码（未设置密码可留空）</span>
        <input
          type="password"
          bind:value={password}
          placeholder="后端未设置密码时留空"
          disabled={loading}
          onkeydown={e => e.key === 'Enter' && submit()}
        />
      </label>
      <div class="actions">
        <button class="btn-text" onclick={() => (open = false)} disabled={loading}
          >取消</button
        >
        <button class="btn-text" onclick={submit} disabled={loading}>
          {loading ? '校验中…' : '确定'}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .scrim {
    position: fixed;
    inset: 0;
    background: var(--md-scrim);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 100;
  }

  .dialog {
    width: 90%;
    min-width: 280px;
    max-width: 460px;
    background: var(--md-surface-container-high);
    border-radius: var(--md-shape-xl);
    box-shadow: var(--md-elevation-3);
    padding: 24px;
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  h3 {
    margin: 0;
    color: var(--md-on-surface);
  }

  label {
    display: flex;
    flex-direction: column;
    gap: 6px;
    color: var(--md-on-surface-variant);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
</style>
