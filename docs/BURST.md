# Burst (C# 고성능 경로 3 단계 — `[BurstCompile]` 1 단계)

Unity 의 **Burst** 는 `[BurstCompile]` 이 붙은 잡을 LLVM 으로 SIMD 네이티브 코드로 바꿉니다. 그 대가로 코드는 **HPC# (High Performance C#)** — 관리 객체 · GC 없는 부분 집합 — 이어야 합니다. NOVA 는 두 단계로 따라갑니다:

| 단계 | 하는 일 | 상태 |
|---|---|---|
| 1 (B-3) | `[BurstCompile]` · Burst API 를 받는다, **HPC# 규칙을 컴파일 때 검사** (Roslyn 분석기 — Console 에 `NB1xxx`), 게임 스크립트를 **Release** (JIT 최적화) 로 빌드, 잡 실행 함수는 처음부터 최적화 JIT | 2026-10-21 완료 |
| 2 (B-4) | HPC# 잡만 IL → C++ 로 옮겨 프로젝트 네이티브 DLL (MSVC · clang, AVX2 · NEON) — 범위 검사 · 별칭 없는 벡터화. 못 옮기는 잡은 1 단계처럼 JIT 로 | 로드맵 |

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
| `[BurstCompile]` (FloatMode · FloatPrecision · CompileSynchronously · DisableSafetyChecks · OptimizeFor …) | 규칙 검사 대상, Profiler 에 `잡 이름 (Burst)` |
| `[BurstDiscard]` · `[NoAlias]` | 받기만 (관리 코드라 BurstDiscard 메서드도 돈다) |
| `BurstCompiler.CompileFunctionPointer<T>(정적 메서드)` → `FunctionPointer<T>.Invoke` | 델리게이트를 그대로 (정적 메서드만 받는다 — Unity 규칙) |
| `SharedStatic<T>.GetOrCreate<TKey>()` → `.Data` | 비관리 메모리 한 칸 (잡에서 읽고 쓴다, 게임 어셈블리를 다시 읽어도 남는다) |
| `BurstCompiler.IsEnabled` | false (네이티브 컴파일은 2 단계) |
| `CompilerServices.Hint.Likely/Unlikely/Assume` · `Loop.ExpectVectorized` · `SkipLocalsInit` | 받기만 |

잡 실행 함수 (`IJobParallelForProducer<T>.Execute` · `Run`) 는 `AggressiveOptimization` — 첫 호출부터 최적화 JIT (Tier 0 을 건너뛴다).

## 검사

`Tools/tests/run_tests.ps1 -Only burst` — 기본 Release · 분석기 · 경고가 빌드를 막지 않음, NB1001 ~ NB1010 이 맞는 줄에 (부르는 메서드 따라가기 포함) · 허락된 곳 (Debug.Log · throw · 깨끗한 잡 · Burst 아닌 잡 · 특성) 에는 없음, Release 에서 JIT 최적화 켬 · FunctionPointer · SharedStatic, Debug 로 바꾸면 다시 빌드 (최적화 끔) · 되돌림, 배수, 설정 저장.

## 아직 (2 단계 — B-4)

- 네이티브 컴파일: HPC# 잡의 IL → C++ (`float3` · `math` → SIMD 내장 함수, NativeArray → 포인터 + 길이), 프로젝트별 DLL, 범위 검사는 에디터에서만, `[NoAlias]` · `Loop.ExpectVectorized` 검사
- `FixedString32Bytes` 등 고정 문자열 (NB1008 의 대신)
- `DisableSafetyChecks` (잡마다 안전 검사 끄기), `FloatMode.Fast`
