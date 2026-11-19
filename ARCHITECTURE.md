# ARCHITECTURE

## 1. Layers
```
 WPF App (MVVM, DI host)  ──►  Engine  ──►  Core
        │                        ▲           ▲
        ├──► Data (SQLite) ──────┘ (implements IDownloadStore from Core)
        └──► Infrastructure (tray, toast, clipboard, shell)
 NativeHost ──► local IPC ──► App        BrowserExtension ──► NativeHost
```
* **Core** – pure domain: `DownloadItem`, `SegmentInfo`, `DownloadStatus`, `DownloadStateMachine`, `PathSafety`,
  `IDownloadStore`, `IDownloadEngine`, request/progress records. No I/O dependencies except path checks.
* **Engine** – everything about moving bytes: `HttpProbe`, `SegmentPlanner`, `DownloadJob`, `RetryPolicy`,
  `TokenBucketSpeedLimiter`, `SpeedMeter`, `ConcurrencyGate`, and the orchestrator `DownloadEngine`.
* **Data** – `IDownloadStore` / `ISettingsStore` on SQLite (WAL, one transaction per save, `PRAGMA user_version` migrations).
* **Infrastructure** – rolling file logger (`ILoggerProvider`) that scrubs query strings, user-info and Authorization/Cookie values.
* **Presentation** – all UI logic with no WPF types: `MainViewModel`, `DownloadItemViewModel`, `AddDownloadViewModel`,
  `SettingsViewModel`, `Localizer` (AR/EN, RTL flag), `Format`, `AppSettings`. Engine events are marshalled with `IUiDispatcher`.
* **App** – thin WPF shell: XAML views bound to Presentation, theme dictionaries swapped at runtime, composition root, shell service.

## 2. Core data model
`DownloadItem`: Id, Url, FinalUrl, FileName, FilePath, Category, TotalBytes?, SupportsRanges, ETag?, LastModified?, ContentType?,
Status, ErrorMessage?, Connections, SpeedLimitBytesPerSecond, CreatedAt, CompletedAt?, `Segments[]`, SingleStreamBytes.
`SegmentInfo`: Index, Start, End (inclusive), Downloaded. Persistent state = exactly these fields, so recovery never depends on memory.

### State machine
```
Queued ─► Probing ─► Downloading ─► Completed
  │          │            │ ▲
  │          ▼            ▼ │(restart loops: Downloading → Probing)
  └──────► Paused ◄───────┘
Failed / Cancelled ─► Queued (retry / restart)
```
Illegal transitions throw (they indicate a bug, not a user error).

## 3. Engine design
1. **Probe** – `GET` with `Range: bytes=0-0`, headers only. 206 + `Content-Range: bytes 0-0/N` ⇒ ranges supported and N known.
   200 ⇒ no range support (length from Content-Length, may be unknown). 416 with `*/0` ⇒ empty file.
   `Accept-Encoding: identity` and automatic decompression off, otherwise byte offsets would be meaningless.
2. **Plan** – `min(connections, ceil(size / MinSegmentSize))` equal byte ranges. Unknown size or no ranges ⇒ single stream.
3. **Write** – one pre-sized `.part` file, a shared `SafeFileHandle`, `RandomAccess.WriteAsync` at absolute offsets (thread-safe, no seeks).
4. **Segment worker** – request `Range: start+downloaded..end` (+ `If-Range` with a strong ETag or Last-Modified). Validate:
   status 206, `Content-Range` start matches, end ≤ requested, total matches, validators unchanged.
   Any violation ⇒ `RangeNotHonoredException` (⇒ restart in single-stream mode) or `RemoteFileChangedException` (⇒ restart from zero).
   Short reads / dropped connections keep the bytes already written and retry from the new offset.
5. **Retry** – exponential backoff with jitter, honours `Retry-After`; the failure counter resets whenever a retry made progress.
   Transient: network errors, IO errors, timeouts, 408/429/5xx. Permanent: other 4xx, TLS validation errors, file-system errors.
6. **Persistence invariant** – a snapshot is captured *first*, the file is flushed *second*, the snapshot is stored *third*.
   Persisted progress therefore never exceeds bytes that are durably on disk; after a crash we may re-download a little, never skip.
7. **Completion** – all segments complete, file length == total, flush, close, atomic `File.Move` of `.part` to the final path
   (a fresh unique name if something appeared in the meantime – never overwrite).
8. **Resume validation** – length, ETag, Last-Modified, existence/size of `.part`, and range support must still match; otherwise the
   job discards progress and restarts safely. Non-range servers always restart from zero.
9. **Throttling** – token bucket per download + one global; the delay is `max(globalDelay, perDownloadDelay)`, so the effective rate is the lower limit.
10. **Concurrency** – `ConcurrencyGate` limits simultaneously running downloads and can be resized at runtime; waiting downloads stay `Queued`.

## 4. Security
See REQUIREMENTS §3. Implementation notes: only `SocketsHttpHandler` defaults for TLS; `UrlRedactor` is the only way URLs reach logs;
`PathSafety` is called on every server-supplied or user-supplied file name; `FinalUrl` (may contain tokens) lives only in the local DB.

## 5. Persistence plan (Data project)
`downloads(id PK, url, final_url, file_name, file_path, category, total_bytes, supports_ranges, etag, last_modified, content_type,
status, error, connections, speed_limit, single_stream_bytes, created_at, completed_at)`,
`segments(download_id, idx, start, end, downloaded, PK(download_id, idx))`, `settings(key PK, value)`; queues/schedules in Milestone 2.

## 6. Browser integration plan
Extension (MV3) ⇄ Native Messaging host (length-prefixed JSON on stdio) ⇄ named pipe with an ACL for the current user ⇄ App.
Messages: `add_download{url, referrer?, title?}`; strict JSON schema, max size 64 KB, scheme allow-list, rate limit.
The host exposes no file-system or command execution capability.

## 7. Decisions (ADR log)
* **ADR-001** Own segmented engine on `HttpClient`; no third-party download libraries.
* **ADR-002** `RandomAccess` + shared handle instead of per-segment `FileStream`s.
* **ADR-003** Probe with `Range: bytes=0-0` rather than `HEAD` (many servers mis-implement HEAD).
* **ADR-004** Engine uses the shared-framework logging abstractions because the build sandbox has no NuGet.
  On a NuGet-enabled machine replace the `FrameworkReference` by `Microsoft.Extensions.Logging.Abstractions` so the WPF app does not
  pull in the ASP.NET Core runtime.
* **ADR-005** Static equal-size segments in the MVP; dynamic re-splitting of slow segments is a later optimisation.
* **ADR-006** SQLite through a ~100-line P/Invoke layer over the OS library (`winsqlite3.dll` on Windows 10 1809+, `libsqlite3` elsewhere)
  instead of Microsoft.Data.Sqlite, because NuGet was unreachable while building. Tradeoffs: SQLite version follows the OS, no encryption.
  Swapping to Microsoft.Data.Sqlite only touches `SqliteDatabase`.
* **ADR-007** View-models live in a WPF-free project so they run under the same test runner as the engine (real engine + SQLite + a
  fake UI thread with a SynchronizationContext). Language switching uses `Loc[key]` bindings + `Item[]` change notification.
* **ADR-008** Manual composition root in `App.xaml.cs` instead of Microsoft.Extensions.Hosting (no benefit for a single-window app;
  fewer moving parts). DI can be added without touching Core/Engine/Presentation, which only use constructor injection.
* **ADR-009** The WPF app opens downloaded executables/scripts only after an explicit confirmation (`WindowsShell.RiskyExtensions`).

## 8. Technical risks
| Risk | Mitigation |
|---|---|
| Servers lie about ranges | Validate every 206 + Content-Range; automatic single-stream fallback; tests with lying servers. |
| Corruption on resume after crash | Flush-before-snapshot invariant; `.part` length check; validator check. |
| Remote file changes mid-download | `If-Range` + validator comparison on every response; restart from zero. |
| Compression changes byte semantics | Identity encoding, decompression disabled. |
| Authenticated / cookie-bound downloads | Out of MVP; browser integration will pass headers via a secure channel, never logged. |
| Disk full / antivirus locks | File errors are permanent failures with a clear message, never silent retries. |
| Speed limiter fairness with many segments | Shared token bucket with debt accounting; verified by timing tests. |

## 9. How to run the verification suite
```powershell
dotnet build DownloadManager.sln -c Release
dotnet run -c Release --project tests/DownloadManager.Verification            # everything
dotnet run -c Release --project tests/DownloadManager.Verification -- pause   # name filter
```
The runner starts an in-process Kestrel server (`tests/DownloadManager.TestServer`, behaviours listed in `TestServerHost`) and
generates deterministic content from `(offset, seed)`, so the "original file" never has to be stored and every byte can be checked.
