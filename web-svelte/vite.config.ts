import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// 纯 SPA：hash 路由、相对资源路径，便于后端在任意路径下静态托管。
export default defineConfig({
  base: './',
  plugins: [svelte()],
  server: {
    port: 8080,
  },
})
