# Verification record

**Connection-flow verification updated on 2026-10-30. Full acceptance remains pending.** The connection/initialization/authentication changes have now been compiled and exercised with native tests, ASan and live Verity Sense trials. Results below distinguish successful short HR trials from failed authentication and unstable trials. They do not certify all APIs, sustained collection or production use.

## Current connection-flow candidate

Release DLL SHA-256: `F0EAC0DFEDCCA9928DD32BD68FDFAAA4A7643EA425FA561A0EC53B5735EDC8E5`. This candidate adds a bounded wait for Windows security negotiation to idempotent reads/CCCD operations and refreshes pairing-state snapshots. The native build, UPM, Mono Player and IL2CPP Player use this DLL. Unity is 6000.3.24f1. Device: Verity Sense, firmware **3.0.16 reported by the user**. Times below are KST. Phone apps were closed; the user also power-cycled the sensor before the comparison sequence. Windows bond removal was an explicit diagnostic recovery action, not an automatic CONNECT step.

| Evidence | Observed result | Boundary |
|---|---|---|
| MSVC Release | SDK, GUI and diagnostic tools built; CTest 2/2 passed, 3.37 s | Includes connection options, security retry/settlement ordering and bounds, stale/cancel guards, terminal auth error, reconnect options and explicit unpair cancellation |
| MSVC ASan | CTest 2/2 passed, 3.75 s | 72 golden frames, 20,000 parser mutations, service mocks, lifetime/cancellation tests; assertion count 20,286 is not legacy parity |
| Unity Mono / IL2CPP builds | Both succeeded, 0 errors / 0 warnings | Managed sources built before the final native-only change; Player DLLs then replaced with the current candidate. Build success does not prove radio reception |
| Native C++, HR, default mask 7 | **60 seconds / 62 samples**, max receive gap 1.088 s, exit 0 | Trial ended at 04:03:39; one uninterrupted short trial on the current DLL |
| Unity IL2CPP, HR, default mask 7 | **60 seconds / 62 samples**, max receive gap 1.142 s, success true | Ready preceded CONNECT completion; standalone headless Player, no rendering/performance claim |
| Unity Mono, HR, default mask 7 | Ready + HR stream start, then disconnect at 22.34 s elapsed / 16.90 s streaming, **0 samples** | Failed trial retained; result -3; no runtime-specific root cause inferred |
| Native HR+ACC, mask 15, 03:59:32 | HR/DIS/battery prepared (ready mask 7); PMD failed (mask 8), **0 ACC samples** | ATT 0x05, result -13, stage 6; session ended after 17.37 s despite the bounded security wait |
| Unity Editor HR+ACC, mask 15 | CONNECT failed after 16.70 s, **0 HR / 0 ACC samples** | ATT 0x05, result -13, stage 6; Ready was not emitted |

The address-matched HCI trace of the current native HR+ACC failure shows ATT 0x05 on the protected write and disconnect reason **0x08 (Connection Timeout)** approximately 11.3 seconds later. No successful Encryption Change event appears in that trace. This differs from the earlier stale-key trace below. Waiting longer for security negotiation did not resolve ACC reception.

### Intermediate connection-flow trials

The preceding DLL `0DC4462BB21A372049C737FE25CBA43A0D99CCEF9E993BF9BF198AA92C576971` produced these results before the bounded security-settlement change. They are retained as separate evidence, not current-DLL results:

| Trial | Observed result |
|---|---|
| Native HR, mask 1, 03:45:09 | 60 seconds / 60 samples, max gap 1.151 s |
| Unity IL2CPP HR, mask 1, 03:47:42 | 60 seconds / 60 samples, max gap 1.142 s |
| Unity Mono HR repeat, mask 1, 03:49:56 | 60 seconds / 61 samples, max gap 1.084 s |
| Earlier Unity Mono attempts, masks 7 and 1 | 0 samples; disconnected at 16.19 s and 21.86 s elapsed respectively |
| Native HR+ACC after sensor restart, 03:41:38 | Ready mask 7 / failed mask 8; ATT 0x05, explicit PairAsync status 19 (Failed), result -13, stage 6 |
| Unity Editor HR+ACC | ATT 0x05, PairAsync status 3 (AlreadyPaired), result -13; no samples |
| Independent WinRT pairing probe | Refreshed DeviceInformation state; basic PairAsync also returned 19 outside SDK protocol parsing |

Successful native, Mono and IL2CPP HR traces for that intermediate DLL contain successful Encryption Change events and HR notifications. Their earlier failed attempts prevent any claim of reliable cold-start behavior.

An earlier implementation checkpoint also received 60 HR samples over 60 seconds (03:21:37, DLL `1C0954BB39C0BDB6EA1D0A071AFEFDA54679519410532DDEDE687EA616FF3BFB`). Other earlier runs failed in discovery or initialization. Before explicit Windows unpair, an address-matched HCI trace recorded encryption status 0x06 (PIN or Key Missing). These outcomes must not be averaged into a blanket hardware-support claim.

The remaining PMD failure occurs while the Windows security negotiation/pairing path is active. The available evidence does not distinguish OS policy/API behavior, adapter/driver interoperability and peripheral-side behavior well enough to blame one component. The tests used a Qualcomm FastConnect 7800 Dual Bluetooth Adapter, driver 3.1.0.1647, on Windows 11 Pro 10.0.26200. `READY` means preparation completed; only observed sensor samples demonstrate reception. Failed trials are preserved alongside successes.

Local evidence is retained in `artifacts/native/bluetooth-diagnosis/` and `artifacts/native/connection-validation/` (logs, result JSON, local ETW). Current results are in `settled-native-hr.log`, `settled-mono-hr/Mono-live-hr.json`, `settled-il2cpp-hr/IL2CPP-live-hr.json`, `settled-editor-combined/Editor-live-hr-acc.json`, and the `settled-native-combined-20261030-035932` trace/log files. Raw Bluetooth traces and keys are not distributed. See [CONNECTION-FLOW.md](CONNECTION-FLOW.md) for the implementation, feature masks and reproduction commands. Long-duration 30-minute/4-hour trials, full original 491+49 behavior equivalence and GUI interaction validation remain outside these short tests.

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
