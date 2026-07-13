# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

BonLivre is a .NET 10 backend that reimplements the HTTP/WebSocket API of the [legado (阅读)](https://github.com/gedoor/legado) reader app, paired with legado's Vue 3 web frontend (originally targeting an Android app's embedded WebView). The backend serves local EPUB/TXT files from the `books/` directory as if they were legado book sources, so the unmodified frontend can browse, read, and track progress against it.

## Commands

Backend (run from repo root):
```bash
dotnet restore BonLiver.sln
dotnet build BonLiver.sln -c Release
dotnet run                          # serves on http://0.0.0.0:5000 and :5001
dotnet format --verify-no-changes   # format check (CI runs this)
dotnet publish -c Release           # Native AOT publish
```

Frontend (run from `web/`, requires Node >= 20, pnpm >= 9):
```bash
pnpm install
pnpm dev            # dev server on http://localhost:8080
pnpm build          # type-check + build, then runs scripts/sync.js
pnpm type-check     # vue-tsc
pnpm lint:fix       # eslint --fix
pnpm format         # prettier
```

There is no test suite in this repo.

## Native AOT constraint (most important)

`BonLivre.csproj` sets `<PublishAot>true</PublishAot>`, so **reflection-based JSON serialization is unavailable**. Every type crossing the API boundary must be registered in `Configuration/AppJsonSerializerContext.cs` with a `[JsonSerializable(typeof(...))]` attribute, and serialization calls must pass the corresponding `AppJsonSerializerContext.Default.<Type>` metadata (see existing endpoints). Adding a new model or response shape without registering it there will compile but fail at runtime. Wrap API payloads in `LeagdoApiResponse<T>` and register both the inner type and the wrapped `LeagdoApiResponse<T>`.

## Backend architecture

- `Program.cs` — entry point. Configures CORS (AllowAll), WebSockets, static files, camelCase JSON with the AOT context, then calls the `Map*Endpoints` extension methods. Minimal API only, no controllers.
- `Endpoints/` — each file is a `static class` with a `Map...Endpoints(this IEndpointRouteBuilder)` extension registering routes inline.
  - `BookshelfEndpoints.cs` — book listing, chapter list, content, cover/image serving, read config, progress. Holds the bookshelf and read config in **static in-memory fields** (`_bookshelf`, `_readConfig`), so they reset on restart; only reading progress is persisted.
  - `SourceEndpoints.cs` — book-source stubs plus the `/searchBook` WebSocket, which searches local books by name/author and streams `SearchBook` results as JSON.
- `Services/`
  - `LocalBookService.cs` — the core. Scans `books/` for `.txt`/`.epub`, parses TXT chapters by Chinese chapter-heading regex, reads EPUB via VersOne.Epub (cached in a static `ConcurrentDictionary`), and rewrites EPUB image `src` to absolute in-archive paths. `GetEpubResource` resolves image requests against the archive with several fuzzy path-matching fallbacks.
  - `BookProgressStore.cs` — SQLite (`data/bookprogress.sqlite`, auto-created) keyed by `(Name, Author)`, upsert on save. `GetBookshelf` merges stored progress into the in-memory book list.
- `Models/Models.cs` — all records in one file (`Book`, `BookProgress`, `BookChapter`, `SearchBook`, `LeagdoApiResponse<T>`, etc.).

### URL scheme for local books

Local books use a `local://<filename>` URL. The fragment encodes chapter location:
- EPUB: `local://file.epub#epub#<internal/file/path.html>` — the part after `#epub#` is the reading-order file path.
- TXT: `local://file.txt#<index>` — index into regex-detected chapter matches; a book with no detected chapters is served as a single "正文" chapter.

## Frontend architecture

Vite + Vue 3 (Composition API) + Pinia + Vue Router (hash history) + Element Plus. It is legado's `web/` project largely unchanged.

- `pnpm build` runs `scripts/sync.js`, which copies `dist/` into `../../../app/src/main/assets/web/vue` — **only when `GITHUB_ENV` is set** (CI/the legado Android repo). It no-ops locally, so a plain local build is safe.
- API layer: `src/api/api.ts` defines all backend calls; `src/api/axios.ts` sets `baseURL` from `VITE_API` env, then `localStorage['remoteUrl']`, then `location.origin`. Field names must match the backend's camelCase records.
- Auto-imports: `vite.config.ts` auto-imports Vue/Router/Pinia APIs and everything under `src/components` and `src/store`, plus Element Plus components. Do not add manual imports for these — `src/auto-imports.d.ts` and `src/components.d.ts` are generated. Path aliases: `@` → `src`, `@api` → `src/api`.
- Routes (hash-based): `/` bookshelf, `/#/bookSource` book-source editor, `/#/rssSource` rss-source editor. `api.ts` branches on whether the URL contains `bookSource` to hit book vs. rss endpoints — several of those rss/source endpoints are frontend expectations the backend does not yet implement.

## Notes

- The frontend expects the full legado API surface; the backend implements the subset needed for local-file reading and stubs the rest (e.g. `saveBookSource` returns success without persisting, `getChapterList`/`getBookContent` return mock data for non-`local://` URLs). When wiring new frontend features, check whether the endpoint actually exists in `Endpoints/`.
- No authentication exists on any endpoint and CORS is fully open — intended for local/trusted-network use.
- `books/` (`.txt`) and `data/` (`.sqlite`) contents are gitignored.
