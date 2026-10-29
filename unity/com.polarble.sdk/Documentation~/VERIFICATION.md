# Verification record

**Final verification is pending.** On 2026-10-30 the user requested implementation first, verification afterwards. Code added after the candidate below (feature initialization, broadcast batching, typed REST events, offline grouping, preservation fixes and sample changes) has not yet been validated. This file does not certify the current source or binary for production/hardware use.

## Earlier implementation checkpoints

| Evidence | Observed result | Boundary |
|---|---|---|
| C# baseline f73aca5 | 491 core checks passed | Baseline only; old implementation retained in Git/ignored comparison workspace |
| Native PMD corpus | 72 original binary frames matched expected values/timestamps | Parser corpus, not all high-level behaviors |
| Native suites, build17 | 2/2 suites passed, 3.53 sec | Before later implementation additions |
| ASan checkpoint | 2/2 suites passed, 3.84 sec; 20,000 parser mutations included | Before later implementation additions |
| Mono candidate build | Succeeded, 0 errors/0 warnings | DLL D29F7F4BF6C20BA1B2300573B39F025992A3C4799A3227D2B2A672CD7C25E53B; now superseded source |
| Native Verity HR | GATT status 1 Unreachable, stage 2, about 7.78 sec, 0 samples | HRESULT/ATT not supplied (zero fields); no root-cause claim |

Earlier isolated 30-second replay measurements used DLL **52D7B3A048017CD3A4D26EDCF8A7FAEB11CE0114D2D44AF778A799384B2D6427**, which is not the final candidate:

| Backend | FPS | SDK P99 ms | managed receive allocation | native ready P99 ms | host receive→consume P99 ms |
|---|---:|---:|---:|---:|---:|
| Mono | 119.98 | 0.0025 | 0 B/frame | 0.0489 | 8.2624 |
| IL2CPP | 119.85 | 0.0048 | 0 B/frame | 0.0384 | 8.3388 |

Both drained 1,824 observed/produced/consumed samples including warmup with no reported skip/ingress drop. One earlier Mono run during concurrent compilation failed the 10 ms latency target; that failure is retained in local artifacts. Short isolated results do not establish 30-minute/4-hour stability, real BLE latency, or VR headset rendering performance.

The direct parser comparison used 200,000 warmed iterations of the same 8-sample raw ACC packet: C# P99 4.9 microseconds / 3432 managed bytes per parse versus an earlier native parser P99 0.4 microseconds / zero measured C++ operator-new bytes. Allocator counters differ; these are parser-only observations, not whole-product speedups or final release claims.

## Reproducible validation entry points

- `scripts/build.ps1`: C++ compilation and native suites; `-Asan -NoGui -BuildDirectory artifacts/native/asan` enables ASan.
- `polar_benchmark`: direct decoder measurement; `polar_replay <seconds> <1..4> <json>`: bounded ingress/decode/batch replay.
- `scripts/prepare-native-unity.ps1`: copy the current DLL and validation sources to `artifacts/NativeUnity`.
- Unity execute methods `PolarValidation.NativeBuild.Editor`, `.Mono`, `.Il2Cpp`, `PolarValidation.PlayStopCycles.Run`.
- Player flags `--polar-seconds 1800` / `14400`, `--polar-load 1` / `4`, `--polar-results <folder>`, `--polar-sha256 <DLL hash>`. Duration excludes five-second warmup. Final stop drains native queues and checks sample sequence/counts.

Run performance trials on the same PC with vSync off, targetFrameRate=120 and a light scene, without concurrent compilers or other test players. Record Unity version/backend, DLL hash, duration, load, CPU/memory/copy metrics, queue watermark, skipped packets and P50/P95/P99. Do not substitute a configuration flag or generated log for an actually completed run.

Targets remain: SDK main-thread P99 ≤0.25 ms, 0 managed B/frame after warmup, native-ready P99 ≤1 ms, host receive→Unity consume P99 ≤10 ms at 120 FPS and no internal loss under normal collection load. Bluetooth radio and sensor batching delay occur before host receive timestamps and must be measured separately.
