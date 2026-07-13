# BonLivre

[English](#english) | [中文](#chinese)

<a name="english"></a>
## English

BonLivre is a lightweight book reading application built with .NET 10 and Vue 3. It supports local EPUB and TXT file reading with a modern web interface.

### ✨ Features

- 📚 **Local Book Management**: Support for EPUB and TXT format books
- 🌐 **Web-Based Reader**: Modern, responsive reading interface built with Vue 3
- 💾 **Reading Progress Tracking**: Automatically saves your reading position
- 🎨 **Customizable Reading Experience**: Adjustable font size, theme, and layout
- 🔍 **Book Search**: Search through your local book collection
- 🚀 **Native AOT**: Built with .NET Native AOT for fast startup and low memory footprint
- 📱 **Cross-Platform**: Works on Windows, Linux, and macOS

### 🛠️ Technology Stack

**Backend:**
- .NET 10 with Native AOT compilation
- ASP.NET Core Minimal APIs
- VersOne.Epub for EPUB file processing
- HtmlAgilityPack for HTML parsing
- SQLite for data storage

**Frontend:**
- Vue 3 with Composition API
- Vite for build tooling
- Element Plus UI framework
- Pinia for state management
- Vue Router for navigation

### 📋 Requirements

**Backend:**
- .NET 10 SDK or later

**Frontend:**
- Node.js >= 20
- pnpm >= 9

### 🚀 Quick Start

#### Backend Setup

1. Clone the repository:
```bash
git clone https://github.com/rs-v/BonLivre.git
cd BonLivre
```

2. Restore dependencies:
```bash
dotnet restore
```

3. Run the backend:
```bash
dotnet run
```

The backend will start on `http://localhost:5000` and `http://localhost:5001`.

#### Frontend Setup

1. Navigate to the web directory:
```bash
cd web
```

2. Install dependencies:
```bash
pnpm install
```

3. Start the development server:
```bash
pnpm dev
```

The frontend will be available at `http://localhost:8080`.

### 📁 Project Structure

```
BonLivre/
├── Configuration/        # Application configuration
├── Endpoints/           # API endpoint definitions
│   ├── BookshelfEndpoints.cs
│   └── SourceEndpoints.cs
├── Models/              # Data models
├── Services/            # Business logic services
│   ├── LocalBookService.cs
│   └── BookProgressStore.cs
├── books/               # Local book storage directory
├── web/                 # Vue 3 frontend application
│   ├── src/
│   │   ├── pages/
│   │   │   ├── bookshelf/
│   │   │   └── source/
│   │   └── ...
│   └── ...
├── Program.cs           # Application entry point
└── BonLivre.csproj     # Project file
```

### 📖 Usage

1. **Adding Books**: Place your EPUB or TXT files in the `books/` directory
2. **Access the Reader**: Navigate to `http://localhost:8080/` for the bookshelf
3. **Book Source Editor**: Access `http://localhost:8080/#/bookSource` for book source editing
4. **RSS Source Editor**: Access `http://localhost:8080/#/rssSource` for RSS source editing

### 🔐 Authentication (Optional)

BonLivre supports an optional single shared password to protect backend access. It is disabled by default (open mode) for backward compatibility.

To enable it, set the `BONLIVRE_PASSWORD` environment variable before starting the backend:

```bash
# Linux / macOS
BONLIVRE_PASSWORD=your-password dotnet run

# Windows (PowerShell)
$env:BONLIVRE_PASSWORD="your-password"; dotnet run
```

When a password is set, all API and WebSocket requests require it. Static frontend files and the root health-check path remain unprotected so the page can load. In the web interface, open the **连接 (Connect)** dialog and enter the address and password.

How the credential is transmitted:
- HTTP requests carry it via the `Authorization: Bearer <password>` header.
- WebSocket, image (`<img src>`), and `sendBeacon` requests carry it via a `?password=` query parameter, since browsers cannot set custom headers on those.

⚠️ **Security notes:**
- Without HTTPS, the password is transmitted in plaintext. Use only on a trusted LAN, or put the backend behind HTTPS / an encrypted tunnel.
- The query-parameter credential may appear in reverse-proxy or access logs.
- There is no login rate limiting or lockout. This is intended for LAN/personal use, not public exposure.

### 🔌 API Endpoints

#### Bookshelf Endpoints
- `GET /getBookshelf` - Get all books in the bookshelf
- `POST /saveBook` - Save a book to the bookshelf
- `POST /deleteBook` - Delete a book from the bookshelf
- `GET /getChapterList?url={url}` - Get chapter list for a book
- `GET /getBookContent?url={url}&index={index}` - Get content of a specific chapter
- `GET /cover?path={path}` - Get book cover image
- `GET /image?url={url}&path={path}` - Get images from EPUB files
- `GET /getReadConfig` - Get reading configuration
- `POST /saveReadConfig` - Save reading configuration
- `POST /saveBookProgress` - Save reading progress

#### Source Endpoints
- `GET /getBookSources` - Get all book sources
- `POST /saveBookSource` - Save a book source
- `WS /searchBook` - WebSocket endpoint for book search

### 🔧 Development

#### Backend Development

Build the project:
```bash
dotnet build
```

Publish for production (see [Deployment](#deployment) for details):
```bash
dotnet publish -c Release -r win-x64
```

#### Frontend Development

Development mode with hot-reload:
```bash
cd web
pnpm dev
```

Build for production:
```bash
pnpm build
```

Lint and fix code:
```bash
pnpm lint:fix
```

Format code:
```bash
pnpm format
```

<a name="deployment"></a>
### 📦 Deployment

#### Prebuilt binaries (CI artifacts)

Every push to `main` builds self-contained executables for **Windows x64** and **Linux x64** via GitHub Actions. To grab one without building locally:

1. Open the **Actions** tab on GitHub and pick the latest successful **CI** run.
2. Download the `bonlivre-win-x64` or `bonlivre-linux-x64` artifact from the run's **Artifacts** section.
3. Unzip it, then run the `BonLivre` executable (see [Running the published app](#running) below).

Each artifact is a native executable bundled with the frontend `wwwroot/`, so no .NET runtime or Node toolchain is needed on the target machine. Artifacts are retained for 7 days.

#### Building locally

The backend serves the frontend as static files, so a single Native AOT executable is all you need to run in production — no separate web server for the UI.

Publishing builds the frontend and bundles it automatically. The `BuildFrontend` MSBuild target (in `BonLivre.csproj`) runs `pnpm install` + `pnpm build` and copies `web/dist/` into `wwwroot/` before publish, so one command produces a self-contained executable plus its `wwwroot/`:

```bash
dotnet publish -c Release -r win-x64      # Windows x64
dotnet publish -c Release -r linux-x64    # Linux x64
dotnet publish -c Release -r linux-arm64  # ARM (e.g. Raspberry Pi)
```

The output is in `bin/Release/net10.0/<RID>/publish/`. Because Native AOT compiles to native machine code, the executable is platform-specific: build on (or for) the same OS/architecture you deploy to. The `pnpm` toolchain (Node >= 20, pnpm >= 9) must be available on the build machine, and a native C/C++ toolchain is required for the AOT step (MSVC + Windows SDK on Windows, clang/build-essential on Linux).

> **Windows:** the AOT link step invokes MSVC's `link.exe` and locates it via `vswhere.exe`. If publishing fails with `'vswhere.exe' is not recognized` or `link.exe ... exited with code 123`, the toolchain is not on `PATH`. Either run the publish command from the **"x64 Native Tools Command Prompt for VS 2022"** (which sets up the MSVC environment), or install the **"Desktop development with C++"** workload (MSVC v143 + Windows SDK) via the Visual Studio Installer. Skip the AOT step entirely with `-p:PublishAot=false` if you only need a framework-dependent build.

To publish the backend only and skip the frontend build (e.g. when iterating on the API):

```bash
dotnet publish -c Release -r win-x64 -p:SkipFrontend=true
```

<a name="running"></a>
Running the published app:

```bash
cd bin/Release/net10.0/win-x64/publish
./BonLivre                                        # open mode
BONLIVRE_PASSWORD=your-password ./BonLivre        # with authentication
```

It listens on `http://0.0.0.0:5000` and `:5001`. Open `http://<host>:5000/` in a browser — the frontend is served from the same origin, so no separate backend address needs to be configured. Static assets are cached with content-hash `immutable` headers while `index.html` is served `no-cache`, so redeploys take effect immediately.

### 🌐 Browser Compatibility

| ![Edge](https://cdn.jsdelivr.net/npm/@browser-logos/edge/edge_32x32.png) | ![Firefox](https://cdn.jsdelivr.net/npm/@browser-logos/firefox/firefox_32x32.png) | ![Chrome](https://cdn.jsdelivr.net/npm/@browser-logos/chrome/chrome_32x32.png) | ![Safari](https://cdn.jsdelivr.net/npm/@browser-logos/safari/safari_32x32.png) |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| Edge ≥ 85                                                              | Firefox ≥ 79                                                                      | Chrome ≥ 85                                                                    | Safari ≥ 14.1                                                                    |

### 📝 License

This project is open source. Please check the repository for license information.

### 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

---

<a name="chinese"></a>
## 中文

BonLivre 是一个使用 .NET 10 和 Vue 3 构建的轻量级图书阅读应用程序。它支持本地 EPUB 和 TXT 文件阅读，具有现代化的 Web 界面。

### ✨ 特性

- 📚 **本地图书管理**：支持 EPUB 和 TXT 格式的图书
- 🌐 **Web 端阅读器**：使用 Vue 3 构建的现代化、响应式阅读界面
- 💾 **阅读进度追踪**：自动保存您的阅读位置
- 🎨 **可定制的阅读体验**：可调节字体大小、主题和布局
- 🔍 **图书搜索**：搜索您的本地图书收藏
- 🚀 **Native AOT**：使用 .NET Native AOT 编译，实现快速启动和低内存占用
- 📱 **跨平台**：支持 Windows、Linux 和 macOS

### 🛠️ 技术栈

**后端：**
- .NET 10 与 Native AOT 编译
- ASP.NET Core Minimal APIs
- VersOne.Epub 用于 EPUB 文件处理
- HtmlAgilityPack 用于 HTML 解析
- SQLite 用于数据存储

**前端：**
- Vue 3 与 Composition API
- Vite 构建工具
- Element Plus UI 框架
- Pinia 状态管理
- Vue Router 路由导航

### 📋 系统要求

**后端：**
- .NET 10 SDK 或更高版本

**前端：**
- Node.js >= 20
- pnpm >= 9

### 🚀 快速开始

#### 后端设置

1. 克隆仓库：
```bash
git clone https://github.com/rs-v/BonLivre.git
cd BonLivre
```

2. 恢复依赖：
```bash
dotnet restore
```

3. 运行后端：
```bash
dotnet run
```

后端将在 `http://localhost:5000` 和 `http://localhost:5001` 上启动。

#### 前端设置

1. 进入 web 目录：
```bash
cd web
```

2. 安装依赖：
```bash
pnpm install
```

3. 启动开发服务器：
```bash
pnpm dev
```

前端将在 `http://localhost:8080` 上可用。

### 📁 项目结构

```
BonLivre/
├── Configuration/        # 应用程序配置
├── Endpoints/           # API 端点定义
│   ├── BookshelfEndpoints.cs
│   └── SourceEndpoints.cs
├── Models/              # 数据模型
├── Services/            # 业务逻辑服务
│   ├── LocalBookService.cs
│   └── BookProgressStore.cs
├── books/               # 本地图书存储目录
├── web/                 # Vue 3 前端应用程序
│   ├── src/
│   │   ├── pages/
│   │   │   ├── bookshelf/
│   │   │   └── source/
│   │   └── ...
│   └── ...
├── Program.cs           # 应用程序入口点
└── BonLivre.csproj     # 项目文件
```

### 📖 使用方法

1. **添加图书**：将您的 EPUB 或 TXT 文件放入 `books/` 目录
2. **访问阅读器**：导航到 `http://localhost:8080/` 查看书架
3. **书源编辑**：访问 `http://localhost:8080/#/bookSource` 进行书源编辑
4. **订阅源编辑**：访问 `http://localhost:8080/#/rssSource` 进行订阅源编辑

### 🔐 认证（可选）

BonLivre 支持可选的单一共享密码来保护后端访问。默认关闭（开放模式），以保持向后兼容。

启用方式：启动后端前设置环境变量 `BONLIVRE_PASSWORD`：

```bash
# Linux / macOS
BONLIVRE_PASSWORD=你的密码 dotnet run

# Windows (PowerShell)
$env:BONLIVRE_PASSWORD="你的密码"; dotnet run
```

设置密码后，所有 API 和 WebSocket 请求都需要携带密码。静态前端文件和根路径存活探测不受保护，以保证页面能正常加载。在 Web 界面点击**连接**对话框，填入地址和密码即可。

凭证传递方式：
- HTTP 请求通过 `Authorization: Bearer <密码>` 请求头携带。
- WebSocket、图片（`<img src>`）、`sendBeacon` 请求通过 `?password=` 查询参数携带，因为浏览器无法为这些请求设置自定义请求头。

⚠️ **安全提示：**
- 无 HTTPS 时密码为明文传输。请仅在可信局域网使用，或为后端套用 HTTPS / 加密隧道。
- 查询参数中的密码可能出现在反向代理或访问日志中。
- 不做登录失败限流或锁定。本功能面向局域网/个人使用，不适合公网暴露。

### 🔌 API 端点

#### 书架端点
- `GET /getBookshelf` - 获取书架中的所有图书
- `POST /saveBook` - 保存图书到书架
- `POST /deleteBook` - 从书架删除图书
- `GET /getChapterList?url={url}` - 获取图书的章节列表
- `GET /getBookContent?url={url}&index={index}` - 获取特定章节的内容
- `GET /cover?path={path}` - 获取图书封面图片
- `GET /image?url={url}&path={path}` - 从 EPUB 文件获取图片
- `GET /getReadConfig` - 获取阅读配置
- `POST /saveReadConfig` - 保存阅读配置
- `POST /saveBookProgress` - 保存阅读进度

#### 书源端点
- `GET /getBookSources` - 获取所有书源
- `POST /saveBookSource` - 保存书源
- `WS /searchBook` - 图书搜索的 WebSocket 端点

### 🔧 开发

#### 后端开发

构建项目：
```bash
dotnet build
```

发布生产版本（会自动构建前端并打包进 `wwwroot/`，详见下方「部署」章节）：
```bash
dotnet publish -c Release -r win-x64
```

#### 前端开发

开发模式（热重载）：
```bash
cd web
pnpm dev
```

构建生产版本：
```bash
pnpm build
```

代码检查和修复：
```bash
pnpm lint:fix
```

格式化代码：
```bash
pnpm format
```

<a name="deployment-zh"></a>
### 📦 部署

#### 预编译产物（CI 构建）

每次推送到 `main` 分支，GitHub Actions 都会为 **Windows x64** 和 **Linux x64** 构建自包含可执行文件。无需本地构建即可获取：

1. 打开 GitHub 的 **Actions** 标签页，选择最近一次成功的 **CI** 运行。
2. 在该运行的 **Artifacts** 区域下载 `bonlivre-win-x64` 或 `bonlivre-linux-x64` 产物。
3. 解压后运行其中的 `BonLivre` 可执行文件（见下方[运行已发布的程序](#running-zh)）。

每个产物都是原生可执行文件，且已打包前端 `wwwroot/`，目标机器无需安装 .NET 运行时或 Node 工具链。产物保留 7 天。

#### 本地构建

后端会以静态文件形式托管前端，因此生产环境只需运行一个 Native AOT 可执行文件即可——无需为界面另起 Web 服务器。

发布时会自动构建并打包前端。`BonLivre.csproj` 中的 `BuildFrontend` MSBuild target 会在 publish 前执行 `pnpm install` + `pnpm build`，并把 `web/dist/` 拷入 `wwwroot/`，因此一条命令即可产出「自包含可执行文件 + `wwwroot/`」：

```bash
dotnet publish -c Release -r win-x64      # Windows x64
dotnet publish -c Release -r linux-x64    # Linux x64
dotnet publish -c Release -r linux-arm64  # ARM（如树莓派）
```

产物位于 `bin/Release/net10.0/<RID>/publish/`。由于 Native AOT 编译为原生机器码，可执行文件与平台绑定：请在与部署目标相同的操作系统/架构上（或面向其）构建。构建机需具备 `pnpm` 工具链（Node >= 20、pnpm >= 9），AOT 步骤还需原生 C/C++ 工具链（Windows 上为 MSVC + Windows SDK，Linux 上为 clang/build-essential）。

> **Windows：** AOT 链接步骤会调用 MSVC 的 `link.exe`，并通过 `vswhere.exe` 定位它。若发布时报 `'vswhere.exe' is not recognized` 或 `link.exe ... 已退出，代码为 123`，说明工具链不在 `PATH` 中。解决方式二选一：从 **“x64 Native Tools Command Prompt for VS 2022”** 命令行运行发布命令（它会配好 MSVC 环境），或通过 Visual Studio Installer 安装 **“使用 C++ 的桌面开发”** 工作负载（MSVC v143 + Windows SDK）。若只需框架依赖版本、无需 AOT，可加 `-p:PublishAot=false` 跳过该步骤。

若只发布后端、跳过前端构建（例如仅调试 API 时）：

```bash
dotnet publish -c Release -r win-x64 -p:SkipFrontend=true
```

<a name="running-zh"></a>
运行已发布的程序：

```bash
cd bin/Release/net10.0/win-x64/publish
./BonLivre                                        # 开放模式
BONLIVRE_PASSWORD=你的密码 ./BonLivre              # 启用认证
```

程序监听 `http://0.0.0.0:5000` 和 `:5001`。浏览器打开 `http://<主机>:5000/` 即可——前端与后端同源，无需单独配置后端地址。静态资源带内容 hash 并以 `immutable` 头长期缓存，而 `index.html` 以 `no-cache` 提供，因此重新部署会立即生效。

### 🌐 浏览器兼容性

| ![Edge](https://cdn.jsdelivr.net/npm/@browser-logos/edge/edge_32x32.png) | ![Firefox](https://cdn.jsdelivr.net/npm/@browser-logos/firefox/firefox_32x32.png) | ![Chrome](https://cdn.jsdelivr.net/npm/@browser-logos/chrome/chrome_32x32.png) | ![Safari](https://cdn.jsdelivr.net/npm/@browser-logos/safari/safari_32x32.png) |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| Edge ≥ 85                                                              | Firefox ≥ 79                                                                      | Chrome ≥ 85                                                                    | Safari ≥ 14.1                                                                    |

### 📝 许可证

本项目为开源项目。请查看仓库以获取许可证信息。

### 🤝 贡献

欢迎贡献！请随时提交 Pull Request。
