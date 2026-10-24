# Rendering measurements for 1.19.0

The optimisation reuses native bitmap/DC resources for floating panels, reuses the precision preview's managed bitmap, and caches the review's static content. It adds no runtime dependency. The menu uses native button input and accessibility support with custom drawing, background folder checks, keyboard navigation, DPI scaling and a high contrast palette.

## Controlled comparison

Measured locally on 24 October 2026 using the original 1.18.1 source and the updated source, the same benchmark fixture, and the same .NET Framework compiler options. The benchmark warms each path for 30 calls, then records five rounds of 240 updates. Presentation uses a 310 by 194 pixel surface. Review uses the normal 257 by 189 pixel window with a fixed countdown fraction. Windows and compositor load can affect results.

| Path, 240 updates | Original median | Updated median | Reduction |
| --- | ---: | ---: | ---: |
| Native floating panel presentation | 111.84 ms | 20.20 ms | 81.9% |
| Complete review refresh | 229.62 ms | 24.09 ms | 89.5% |

Original presentation rounds: 96.42, 111.84, 125.63, 115.69, 106.72 ms.
Updated presentation rounds: 37.38, 24.20, 17.58, 19.51, 20.20 ms.
Original review rounds: 225.70, 229.72, 242.68, 229.62, 219.79 ms.
Updated review rounds: 21.86, 24.09, 24.30, 26.62, 20.75 ms.

These are rendering microbenchmarks. They do not establish an equivalent reduction in total capture time, resident memory, recording cost or battery use. Reusing buffers reduces allocation churn while keeping small buffers alive until their panel closes.

## Reproduce

Build and run the fixture in PowerShell:

```powershell
.\build.ps1 -Benchmark -OutputPath "$env:LOCALAPPDATA\OpenCaptureBuild\RenderingBenchmark.exe"
& "$env:LOCALAPPDATA\OpenCaptureBuild\RenderingBenchmark.exe"
```

It briefly displays floating panels. A successful run prints `PASS` and exits with code 0. For a comparison, run the same `tests/RenderingBenchmark.cs` fixture with the original and updated application sources on the same desktop, without other tests running at the same time.

## Regression coverage

The focused review suite checks alpha/channel preservation, resize and stale-pixel handling, native bitmap reuse, stable GDI handle use over repeated frames and panel reopenings, review buffer reuse, countdown endpoint rendering, obsolete OCR cancellation, menu state during blocked storage access, coalesced background refreshes, enabled/disabled navigation, Enter/Space activation, Escape dismissal and shutdown during a pending scan. Existing crop, clipboard, storage, recorder and visual-zone checks remain in place.
