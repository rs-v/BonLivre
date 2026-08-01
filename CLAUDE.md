# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

BonLivre is a .NET 10 backend that reimplements the HTTP/WebSocket API of the [legado (阅读)](https://github.com/gedoor/legado) reader app, paired with a Svelte 5 web frontend in `web-svelte/` (the **active** frontend, built and bundled on publish). The backend serves local EPUB/TXT files from the `books/` directory as if they were legado book sources, so the frontend can browse, read, and track progress against it.

## Commands

Backend (run from repo root):
```bash
dotnet restore BonLiver.sln
dotnet build BonLiver.sln -c Release
dotnet run                          # serves on http://0.0.0.0:5000 and :5001
dotnet format --verify-no-changes   # format check (CI runs this)
dotnet publish -c Release           # Native AOT publish
```

Frontend (run from `web-svelte/`, requires Node >= 20, pnpm >= 9):
```bash
pnpm install
pnpm dev            # dev server on http://localhost:8080 (backend assumed at :5000)
pnpm build          # vite build → dist/
pnpm check          # svelte-check (CI runs this)
```

There is no test suite in this repo.

## Native AOT constraint (most important)

`BonLivre.csproj` sets `<PublishAot>true</PublishAot>`, so **reflection-based JSON serialization is unavailable**. Every type crossing the API boundary must be registered in `Configuration/AppJsonSerializerContext.cs` with a `[JsonSerializable(typeof(...))]` attribute, and serialization calls must pass the corresponding `AppJsonSerializerContext.Default.<Type>` metadata (see existing endpoints). Adding a new model or response shape without registering it there will compile but fail at runtime. Wrap API payloads in `LeagdoApiResponse<T>` and register both the inner type and the wrapped `LeagdoApiResponse<T>`.

## Backend architecture

- `Program.cs` — entry point. Configures CORS (AllowAll), WebSockets, static files, camelCase JSON with the AOT context, then calls the `Map*Endpoints` extension methods. Minimal API only, no controllers.
- `Endpoints/` — each file is a `static class` with a `Map...Endpoints(this IEndpointRouteBuilder)` extension registering routes inline.
  - `BookshelfEndpoints.cs` — book listing, chapter list, content, cover/image serving, read config, progress, and bookmarks. Holds the bookshelf in a **static in-memory field** (`_bookshelf`), so it resets on restart; reading progress, read config, and bookmarks are persisted.
  - `SourceEndpoints.cs` — book-source stubs plus the `/searchBook` WebSocket, which searches local books by name/author and streams `SearchBook` results as JSON.
- `Services/`
  - `LocalBookService.cs` — the core. Scans `books/` for `.txt`/`.epub`, parses TXT chapters by Chinese chapter-heading regex, reads EPUB via VersOne.Epub (cached in a static `ConcurrentDictionary`), and rewrites EPUB image `src` to absolute in-archive paths. `GetEpubResource` resolves image requests against the archive with several fuzzy path-matching fallbacks.
  - `LiteDbStore.cs` — application-lifetime LiteDB owner for `data/bonlivre.db`.
  - `BookProgressStore.cs`, `BookmarkStore.cs`, `SettingsStore.cs` — LiteDB collections for persisted progress, bookmarks, and read settings. Progress is keyed by `BookUrl`; `GetBookshelf` merges it into the in-memory book list.
- `Models/Models.cs` — all records in one file (`Book`, `BookProgress`, `BookChapter`, `SearchBook`, `LeagdoApiResponse<T>`, etc.).

### URL scheme for local books

Local books use a `local://<filename>` URL. The fragment encodes chapter location:
- EPUB: `local://file.epub#epub#<internal/file/path.html>` — the part after `#epub#` is the reading-order file path.
- TXT: `local://file.txt#<index>` — index into regex-detected chapter matches; a book with no detected chapters is served as a single "正文" chapter.

## Frontend architecture (`web-svelte/`)

Vite + Svelte 5 (runes) + TypeScript. No component library, no external router/store — plain CSS, a hash router in `src/lib/router.svelte.ts`, and rune-based shared state (`.svelte.ts` modules).

- `src/lib/api.ts` — all backend calls via `fetch`. Base URL from `localStorage['remoteUrl']`, else `location.origin` (dev: same host, port 5000). Password from `localStorage['remotePassword']`: HTTP requests use `Authorization: Bearer`, while WebSocket/`<img src>`/sendBeacon append `?password=`. Field names must match the backend's camelCase records (`src/lib/types.ts`).
- `src/lib/reader.svelte.ts` — reading session state (current book, catalog, chapterIndex/chapterPos) plus progress saving (sendBeacon, 60s throttle) and read-config load/save via `/getReadConfig`/`/saveReadConfig`.
- Views: `src/views/Bookshelf.svelte` (shelf grid, local filter + WS online search, connect dialog, upload, delete) and `src/views/Reader.svelte` (catalog drawer, content with EPUB image proxy, chapterPos tracking via IntersectionObserver, themes/font-size/width settings).
- Routes (hash-based): `/` bookshelf, `/#/chapter` reader. There is no source-editor UI — the backend only stubs those endpoints anyway.
- `chapterPos` semantics (compatible with legado web): cumulative character count of paragraphs read, +1 per paragraph for the newline.

## Notes

- The backend implements the subset of the legado API needed for local-file reading and stubs the rest (e.g. `saveBookSource` returns success without persisting, `getChapterList`/`getBookContent` return mock data for non-`local://` URLs). When wiring new frontend features, check whether the endpoint actually exists in `Endpoints/`.
- No authentication exists on any endpoint and CORS is fully open — intended for local/trusted-network use.
- `books/` (`.txt`) and `data/` (`bonlivre.db`) contents are gitignored. Changing from SQLite to LiteDB starts with fresh persisted data; legacy `.sqlite` files are not imported.
