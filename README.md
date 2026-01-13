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

Publish for production:
```bash
dotnet publish -c Release
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

发布生产版本：
```bash
dotnet publish -c Release
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

### 🌐 浏览器兼容性

| ![Edge](https://cdn.jsdelivr.net/npm/@browser-logos/edge/edge_32x32.png) | ![Firefox](https://cdn.jsdelivr.net/npm/@browser-logos/firefox/firefox_32x32.png) | ![Chrome](https://cdn.jsdelivr.net/npm/@browser-logos/chrome/chrome_32x32.png) | ![Safari](https://cdn.jsdelivr.net/npm/@browser-logos/safari/safari_32x32.png) |
| ---------------------------------------------------------------------- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| Edge ≥ 85                                                              | Firefox ≥ 79                                                                      | Chrome ≥ 85                                                                    | Safari ≥ 14.1                                                                    |

### 📝 许可证

本项目为开源项目。请查看仓库以获取许可证信息。

### 🤝 贡献

欢迎贡献！请随时提交 Pull Request。
