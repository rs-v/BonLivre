# Verify BonLivre

- Run the backend from the repository root with `BONLIVRE_PASSWORD=<safe-test-password> dotnet run --no-build -c Release`.
- Use `curl` against `http://127.0.0.1:5000` to drive protected API routes. `/health` is public; all other routes require `Authorization: Bearer <password>` (the only credential channel — there is no `?password=` query fallback). Data-carrying endpoints take POST JSON bodies, e.g. `curl -X POST -H "Authorization: Bearer $PW" -H "Content-Type: application/json" -d '{"url":"local://x.epub"}' http://127.0.0.1:5000/getChapterList`.
- For authentication throttling, make invalid requests up to the configured threshold, then inspect the next response status and `Retry-After` header. Confirm a valid Bearer request still succeeds.
- For upload boundary checks, use harmless repository files with a `.txt` multipart filename and a unique test name. Avoid deleting or overwriting real `books/` content.
- Stop the background backend when done. Native AOT publish on this Windows environment may require Visual Studio Build Tools / `vswhere.exe`.
