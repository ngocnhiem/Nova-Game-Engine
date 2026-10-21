# Burst (C# 고성능 경로 — `[BurstCompile]` 1 · 2 단계)

Unity 의 **Burst** 는 `[BurstCompile]` 이 붙은 잡을 LLVM 으로 SIMD 네이티브 코드로 바꿉니다. 그 대가로 코드는 **HPC# (High Performance C#)** — 관리 객체 · GC 없는 부분 집합 — 이어야 합니다. NOVA 는 두 단계로 따라갑니다:

| 단계 | 하는 일 | 상태 |
|---|---|---|
| 1 (B-3) | `[BurstCompile]` · Burst API 를 받는다, **HPC# 규칙을 컴파일 때 검사** (Roslyn 분석기 — Console 에 `NB1xxx`), 게임 스크립트를 **Release** (JIT 최적화) 로 빌드, 잡 실행 함수는 처음부터 최적화 JIT | 2026-10-21 완료 |
| 2 (B-4) | HPC# 잡을 **IL → C++ 로 옮겨 MSVC 로 프로젝트 네이티브 DLL** (AVX2, 에디터는 안전 검사 · 플레이어는 없이) — 아래 [2 단계](#2-단계--네이티브-컴파일-il--c--msvc). 못 옮기는 잡은 이유와 함께 1 단계처럼 JIT 로 | 2026-10-21 완료 |

같은 코드가 Unity 에서도 NOVA 에서도 돈다 — 네임스페이스만 `NovaEngine.Burst` 입니다.

```csharp
using NovaEngine.Burst;
using NovaEngine.Collections;
using NovaEngine.Jobs;
using NovaEngine.Mathematics;

[BurstCompile]
public struct SteerJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> target;
    public NativeArray<float3> velocity;
    public float dt;
    public void Execute(int i) => velocity[i] = math.lerp(velocity[i], math.normalizesafe(target[i]) * 5f, dt);
}
```

## C# Code Optimization (Release · Debug)

**Project Settings > Player > Other Settings > Configuration > C# Code Optimization** (Unity 의 Code Optimization 과 같다). `ProjectSettings/PlayerSettings.json` 의 `scriptOptimization`.

| | 뜻 |
|---|---|
| **Release** (기본) | `dotnet build -c Release` — JIT 최적화 켬. 예외의 파일 · 줄 번호는 그대로 (portable PDB) |
| Debug | 최적화를 끈다 (몇 배 느리다 — 디버거로 한 줄씩 볼 때만). 개발 빌드가 아닌 **플레이어 빌드는 그래도 Release 로 따로 빌드** (Unity 와 같다) |

- 바꾸면 스크립트를 다시 빌드한다 (지문에 구성이 들어 있다). `nova exec` 도 같은 구성
- CLI: `nova scripting info` (optimization · compiling · hasErrors · burstAnalyzer), `nova scripting set --optimization release|debug`

재 보기 (12 스레드 PC, 에디터 — 안전 검사 켬):

| | Debug | Release | 배수 |
|---|---|---|---|
| `[BurstCompile]` 잡 `Run` (52 만 원소 × 4 걸음) | 120 ms | 15 ms | **8.0** |
| `float3` 덧셈 고리 (52 만) | 10.4 ms | 1.24 ms | **8.4** |
| 같은 잡 `Schedule` (모든 작업자) | — | 2.9 ms | Debug 한 스레드의 **41 배** |
| 4 백만 덧셈 `Vector3[]` · `NativeArray<float3>` · Span ([Mathematics](MATHEMATICS_COLLECTIONS.md)) | 116 · 165 · 129 ms | 4.3 · 8.2 · 3.8 ms | 27 · 20 · 34 |

`NativeArray` 색인이 Span 보다 느린 것은 에디터의 안전 검사 (범위 · Dispose) 때문 — 플레이어 빌드는 검사가 없다.

## HPC# 규칙 검사 (NovaBurstAnalyzer)

`[BurstCompile]` struct 의 `Execute` (잡) 와 `[BurstCompile]` 클래스의 `[BurstCompile]` 정적 메서드가 뿌리 — 거기서 부르는 같은 프로젝트의 메서드도 따라가 본다 (Burst 도 함께 컴파일한다, 경고 끝에 `(reached from BadJob.Execute)`). **경고만** — 코드는 그대로 JIT 로 돈다 (Unity 도 Burst 실패 시 관리 코드로 돈다).

| 코드 | 무엇 | 대신 |
|---|---|---|
| NB1001 | 관리 객체 `new List<int>()` 등 (GC 할당) | NativeList · 값 형식 |
| NB1002 | 관리 배열 `new int[n]` | `NativeArray<T>(n, Allocator.Temp)` · `stackalloc` |
| NB1003 | 박싱 (`object o = 5`, 인터페이스로 넘기기) | 제네릭 제약 |
| NB1004 | 델리게이트 · 람다 | `FunctionPointer<T>` · 잡 struct |
| NB1005 | 읽기 전용이 아닌 정적 필드 | `SharedStatic<T>` · 잡 필드로 넘기기 |
| NB1006 | try/catch (try/finally 는 된다) | 반환 값 · 검사 |
| NB1007 | 관리 형식을 쓰는 메서드 · 속성 (`transform.position`, `string.Length` …) | 잡 밖에서 읽어 넘기기 |
| NB1008 | 문자열 만들기 (`+` · `$""`) — `Debug.Log` 와 예외 메시지 안은 된다 | `FixedString` (로드맵) |
| NB1009 | `lock` | `Interlocked` · ParallelWriter |
| NB1010 | `[BurstCompile]` 잡 struct 의 관리 형식 필드 | NativeArray · NativeReference |

- 허락: `throw new XxxException("…" + i)`, `Debug.Log("…" + i)` (Burst 와 같다), 정적 `readonly` 필드, `math.*`, 컨테이너
- 분석기: `Tools/NovaBurstAnalyzer` (Roslyn 4.14, CMake 대상 `NovaBurstAnalyzer` → `Binaries/Scripting/Analyzers/NovaBurstAnalyzer.dll`). 에디터가 만드는 `Assembly-CSharp.csproj` 에 `<Analyzer>` 로 들어가 VS Code · Visual Studio 에서도 밑줄이 보인다

## Burst API (`NovaEngine.Burst`)

| | 1 단계 동작 |
|---|---|
| `[BurstCompile]` (FloatMode · FloatPrecision · CompileSynchronously · DisableSafetyChecks · OptimizeFor …) | 규칙 검사 · 네이티브 컴파일 대상 (DisableSafetyChecks · FloatMode.Fast · CompileSynchronously 는 2 단계가 따른다), Profiler 에 `잡 이름 (Burst)` |
| `[BurstDiscard]` · `[NoAlias]` | 받기만 (관리 코드라 BurstDiscard 메서드도 돈다) |
| `BurstCompiler.CompileFunctionPointer<T>(정적 메서드)` → `FunctionPointer<T>.Invoke` | 델리게이트를 그대로 (정적 메서드만 받는다 — Unity 규칙) |
| `SharedStatic<T>.GetOrCreate<TKey>()` → `.Data` | 비관리 메모리 한 칸 (잡에서 읽고 쓴다, 게임 어셈블리를 다시 읽어도 남는다) |
| `BurstCompiler.IsEnabled` | 옵션이 켜져 있고 네이티브 DLL 을 읽었으면 true |
| `CompilerServices.Hint.Likely/Unlikely/Assume` · `Loop.ExpectVectorized` · `SkipLocalsInit` | 받기만 |

잡 실행 함수 (`IJobParallelForProducer<T>.Execute` · `Run`) 는 `AggressiveOptimization` — 첫 호출부터 최적화 JIT (Tier 0 을 건너뛴다).

## 검사

`Tools/tests/run_tests.ps1 -Only burst` — 기본 Release · 분석기 · 경고가 빌드를 막지 않음, NB1001 ~ NB1010 이 맞는 줄에 (부르는 메서드 따라가기 포함) · 허락된 곳 (Debug.Log · throw · 깨끗한 잡 · Burst 아닌 잡 · 특성) 에는 없음, Release 에서 JIT 최적화 켬 · FunctionPointer · SharedStatic, Debug 로 바꾸면 다시 빌드 (최적화 끔) · 되돌림, 배수, 설정 저장.

## 2 단계 — 네이티브 컴파일 (IL → C++ → MSVC)

게임 스크립트를 읽으면 (에디터 시작 · 저장 · 핫 리로드) **`[BurstCompile]` 이 붙은 잡 struct** 를 찾아 C++ 로 옮기고, MSVC 로 프로젝트 DLL (`Library/Burst/<지문>/NovaBurst.dll`) 을 백그라운드에서 만든다. 다 되면 그 잡의 실행 함수가 네이티브로 바뀐다 — 그동안 · 못 옮기면 1 단계 (JIT) 그대로 (Unity Burst 의 비동기 컴파일과 같다). `[BurstCompile(CompileSynchronously = true)]` 이 하나라도 있으면 읽을 때 기다린다.

| | |
|---|---|
| 대상 | `IJob` · `IJobFor` · `IJobParallelFor` · `IJobParallelForBatch` · `IJobParallelForTransform` 잡 (Schedule · ScheduleParallel · Run 모두) |
| 옮기는 것 | 잡의 `Execute` 와 거기서 부르는 **모든 C# 코드** — 게임 스크립트 · ScriptCore (`float3` · `quaternion` · `float4x4` · `math.*` · `Random` · NativeArray · NativeList · ParallelWriter · NativeReference · TransformAccess · SharedStatic …) 를 IL 에서 그대로 (스택 형식을 따라가 임시 변수 + goto, struct 는 .NET 과 같은 배치 — 크기를 `static_assert`) |
| C++ 로 바로 | `MathF` · `Math` (같은 UCRT 함수 — 결과가 JIT 와 비트 단위로 같다), `Unsafe` · `Interlocked` · `Volatile` · `BitConverter` · `BitOperations` · `NativeMemory` · `Buffer.MemoryCopy` · `Half`, `Debug.Log("글자")` (Console 로), `JobsUtility.ThreadIndex`, `ProfilerMarker` (아무것도 안 함) |
| 예외 | `throw` 로 끝나는 블록 (문구 만들기 포함) 은 "실패 표시 후 돌아가기" — Complete 할 때 `Exception in job X: … (native Burst code)` |
| 안전 검사 | 에디터 = 켬 (범위 · Dispose 뒤 · 묶음 범위 — JIT 와 같은 문구), `[BurstCompile(DisableSafetyChecks = true)]` · `BurstCompiler.Options.EnableBurstSafetyChecks = false` 면 끔, **플레이어 = 늘 끔**. `CollectionChecks.Enabled` 는 번역할 때 상수로 접혀 꺼진 검사의 코드는 아예 없다 |
| 빠르게 | 잡의 `Execute` 는 고리 안에 펼친다 (`__forceinline`), 실패할 수 없는 호출 뒤에는 검사 없음 → 단순 고리는 AVX2 로 벡터화. `FloatMode.Fast` 잡은 그 부분만 fast-math (`float_control(precise, off)` · FMA) — 나머지는 `/fp:precise` (.NET 과 같은 값) |
| 캐시 | 지문 = 게임 어셈블리 바이트 + ScriptCore + 번역기 판 + 검사 · AVX2 · 옵션 — 같으면 다시 빌드하지 않는다 (DLL 을 바로 읽는다) |
| 못 옮김 → JIT | 관리 객체 · 배열 · 문자열 값 · 델리게이트 · `try/catch` · 쓰기 가능한 정적 필드 (`Time.deltaTime` 은 엔진 상태 — 값을 잡 필드로 넘긴다) · `ref struct` · 엔진 API … — `nova burst info` 에 잡마다 이유 |
| 플레이어 빌드 | Build 가 안전 검사 없이 (`/arch:AVX`) 따로 빌드해 `<게임>_Data/Library/Burst/` 에 — 플레이어는 시작할 때 읽어 묶는다. MSVC 가 없으면 JIT 로 |
| 도구 | Visual Studio (또는 Build Tools) 의 "C++ 데스크톱 개발" — `vswhere` · 설치 폴더에서 `vcvars64.bat` 을 찾는다 (`NOVA_VCVARS` 로 지정 가능) |

CLI:

```
nova burst info                      # 상태 · DLL · 잡마다 native / jit (이유) · 안전 검사
nova burst compile --wait true [--force true]   # 다시 번역 · 빌드 (force = 캐시 무시)
nova burst set --enabled false       # BurstCompiler.Options.EnableBurstCompilation (JIT 와 비교)
nova burst set --safetyChecks false  # 다음 compile 부터 에디터 네이티브 코드에서 검사 빼기
nova burst player --out <폴더>       # 플레이어용 (검사 없음) DLL + NovaBurst.json
nova burst source                    # 만든 C++ (nova_burst.cpp) 경로
```

재 보기 (12 스레드 · AVX2, 에디터):

| 잡 | JIT (Release, 검사 켬) | 네이티브 (검사 켬) | 네이티브 (`DisableSafetyChecks`) |
|---|---|---|---|
| `d[i] = d[i] * f + 0.5f` (2 백만, Run) | 4.5 ms | 5.0 ms | **0.28 ms (16 배 — AVX2 벡터화)** |
| 보이드 (52 만 × 8 걸음, `float3` · `normalizesafe` 분기, Run) | 52 ms | 53 ms | 51 ms (같음 — AoS `float3` + 분기는 MSVC 가 벡터화하지 못해 RyuJIT 와 같은 스칼라 코드) |
| 같은 보이드 Schedule (11 작업자) | 6.7 ms | — | 6.1 ms |

- 결과 비교: float 산술 · `sin` · `normalizesafe` · quaternion · TRS · 역행렬 · Random · long 합 · Transform 잡 모두 JIT 와 비트 단위로 같다 (검사 `burstnative`)
- 이점이 큰 것: SoA 배열 위의 단순 산술 (`NativeArray<float>` 여럿), 검사를 끈 잡, `FloatMode.Fast`. 이점이 작은 것: AoS `float3` 에 분기가 많은 코드 (Unity Burst 도 Mono 대비 빠른 것이지 RyuJIT 와는 차이가 작다)

### 엔진 안

- `ScriptCore/Engine/BurstCompiler/` — `BurstIL.cs` (IL 읽기 · 토큰 풀기), `BurstTypes.cs` (형식 → C++ · struct 배치 · 정적 값 바이트), `BurstMethodCompiler.cs` (스택 형식 추적 → 코드, try/finally, 상수 분기 접기, throw 블록), `BurstIntrinsics.cs` (C++ 로 바로), `BurstUnit.cs` (잡마다 namespace · 정적 값 · 실패 검사), `BurstNative.cs` (잡 찾기 · 앞부분 · vcvars + cl · 캐시 · 묶기 · CLI · 플레이어)
- 잡 실행 함수 (`IJobParallelForProducer<T>` 등 · `Run`) 는 `JobReflection<T>.s_NativePtr` 가 있으면 `BurstRuntime.Invoke` (NBContext — 실패 문구 · 스레드 번호 · Debug.Log 되부르기)
- 한 잡의 번역 · C++ 오류는 그 잡만 JIT 로 (C++ 가 깨지면 잡마다 따로 컴파일해 찾는다)
- `Bridge.LoadGameAssembly` → `BurstNative.OnGameAssemblyLoaded`, 다시 읽기 전 `Unload` (DLL 풀기 — 도는 빌드는 기다리지 않고 버린다), `Bridge.BurstCommand` (CLI · `BuildPipeline`)

## 아직

- `FixedString32Bytes` 등 고정 문자열 (NB1008 의 대신), `NativeHashMap` · `NativeQueue` 의 늘리기 (할당) 는 네이티브에서 못 한다 (그 잡은 JIT)
- `FunctionPointer<T>` 를 네이티브로 (지금은 델리게이트), `[NoAlias]` · `Loop.ExpectVectorized` 검사, 안드로이드 arm64 (NEON) · clang, AoS → SoA 벡터화 (Burst 처럼 LLVM)
