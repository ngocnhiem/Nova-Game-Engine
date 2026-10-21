# C# Job System (C# 고성능 경로 2 단계)

Unity 의 **Job System** (`Unity.Jobs`) 을 같은 이름 · 같은 규칙으로 옮겼습니다 (네임스페이스 `NovaEngine.Jobs`). C# 코드를 모든 코어에서 돌리고, 데이터는 [NativeArray 같은 비관리 컨테이너](MATHEMATICS_COLLECTIONS.md) 로 주고받습니다. 에디터에서는 Unity 와 같은 **안전 검사** 가 경쟁 상태를 예약할 때 잡아 줍니다.

```csharp
using NovaEngine;
using NovaEngine.Mathematics;
using NovaEngine.Collections;
using NovaEngine.Jobs;

public struct MoveJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> velocity;
    public NativeArray<float3> position;
    public float deltaTime;
    public void Execute(int i) => position[i] += velocity[i] * deltaTime;
}

public class Swarm : MonoBehaviour
{
    NativeArray<float3> m_Pos, m_Vel;
    JobHandle m_Handle;

    void Start() { m_Pos = new NativeArray<float3>(100000, Allocator.Persistent); m_Vel = new NativeArray<float3>(100000, Allocator.Persistent); }
    void Update() => m_Handle = new MoveJob { velocity = m_Vel, position = m_Pos, deltaTime = Time.deltaTime }.Schedule(m_Pos.Length, 256);
    void LateUpdate() => m_Handle.Complete();   // 그 사이 메인 스레드는 다른 일을
    void OnDestroy() { m_Handle.Complete(); m_Pos.Dispose(); m_Vel.Dispose(); }
}
```

## 잡 종류

| 인터페이스 | 예약 | 실행 |
|---|---|---|
| `IJob` | `Schedule(dependsOn)` · `Run()` | 작업 스레드에서 한 번 |
| `IJobFor` | `Schedule(n, dep)` (한 스레드 차례로 — 앞 원소에 기대도 된다) · `ScheduleParallel(n, batch, dep)` · `Run(n)` | index 0 … n-1 |
| `IJobParallelFor` | `Schedule(n, innerloopBatchCount, dep)` · `Run(n)` | index 마다, `batch` 개씩 여러 스레드 |
| `IJobParallelForBatch` | `ScheduleBatch(n, batch, dep)` · `RunBatch(n)` | `Execute(startIndex, count)` |

- `JobHandle`: `Complete()` (끝날 때까지 — 메인 스레드도 다른 잡을 돕는다), `IsCompleted`, `CombineDependencies(a, b[, c] | NativeArray<JobHandle>)`, `CompleteAll`. 기본값 = 끝난 것
- 컨테이너 `Dispose(JobHandle)` — 잡이 끝난 뒤 작업 스레드가 푼다. `[DeallocateOnJobCompletion]` — 잡이 끝나면 그 필드를 푼다
- 동시 쓰기: `NativeList.AsParallelWriter()` (`AddNoResize` — 미리 Capacity), `NativeQueue.AsParallelWriter()` (`Enqueue`), `NativeHashMap/HashSet.AsParallelWriter()` (`TryAdd` · `Add`)
- `JobsUtility.JobWorkerCount` (기본 = 코어 - 1, 0 이면 메인 스레드가 Complete 때 모두), `JobWorkerMaximumCount`, `ThreadIndex`, `IsExecutingJob`
- Profiler: 작업 스레드가 **Timeline 의 `C# Job Worker N` 줄** 에 잡 이름 구간으로 나온다. 직접 구간을 붙이려면 `NovaEngine.Profiling.ProfilerMarker` (`using (s_Marker.Auto()) { }`) · `Profiler.BeginSample/EndSample` — 메인 · 작업 스레드 어디서나

## 규칙과 안전 검사 (에디터)

잡은 **struct** 이고 **참조형 필드 (class · string · 관리 배열 · 델리게이트) 가 없어야** 한다 (예약할 때 거부 — GC 가 없는 데이터만 넘어간다). 잡 안에서 GameObject · 컴포넌트를 만지지 않는다.

에디터에서는 컨테이너마다 예약된 잡의 읽기 · 쓰기 잠금을 기록해 (Unity 의 Job Debugger 와 같은 문구로) 막는다:

| 하면 | 결과 |
|---|---|
| 잡이 쓰는 컨테이너를 Complete 전에 메인 스레드에서 읽기 · 쓰기 · Dispose | `InvalidOperationException: The previously scheduled job MoveJob writes to the native container MoveJob.position. You must call JobHandle.Complete() …` |
| 같은 컨테이너에 쓰는 두 잡을 의존성 없이 예약 (또는 읽는 잡이 있는데 쓰는 잡) | `… you must include MoveJob as a dependency of the newly scheduled job.` — `[ReadOnly]` 끼리는 함께 된다 |
| `Allocator.Temp` 컨테이너를 잡에 | 거부 (Temp 는 프레임 끝에 되돌린다 — TempJob · Persistent) |
| `IJobParallelFor` 에서 자기 묶음 밖 원소 쓰기 | `IndexOutOfRangeException: … out of restricted IJobParallelFor range` (`[NativeDisableParallelForRestriction]` 로 끈다) |
| `IJobParallelFor` 에서 `NativeList` 등에 그냥 쓰기 | 거부 — `[ReadOnly]` 또는 `ParallelWriter` |
| 한 잡 안에서 같은 배열을 입력 · 출력으로 (별칭) | 거부 |
| 잡 안의 예외 | 메인 스레드 Console 에 `Exception in job MoveJob: …` (Complete · 다음 프레임) — 잡은 끝난 것으로 |
| 잡 안에서 Schedule · Complete | 거부 (의존성으로) |

- 특성: `[ReadOnly]` · `[WriteOnly]` · `[NativeDisableParallelForRestriction]` · `[NativeDisableContainerSafetyRestriction]` · `[DeallocateOnJobCompletion]`
- Play 를 멈추면 도는 잡을 기다리고 모든 잠금을 푼 뒤 남은 메모리를 알린다 ([누수 알림](MATHEMATICS_COLLECTIONS.md#안전-검사-에디터)). 스크립트를 다시 읽을 때도 먼저 모든 잡을 끝낸다
- 플레이어 빌드 (`CollectionChecks.Enabled` = false) 는 검사 없이 돈다 — 잠금 기록 · 핸들 바꾸기를 하지 않는다

## 엔진 안

- `ScriptCore/Engine/Jobs/` — `Jobs.cs` (인터페이스 · `JobHandle` · 예약 확장 · `JobsUtility`), `JobScheduler.cs` (작업 스레드, 잡 기록 풀, 의존성, 잠금), `JobReflection.cs` (잡 형식마다 한 번: 안의 컨테이너 필드 (중첩 struct 까지) 의 `m_Safety` 위치를 찾고 특성을 읽는다)
- 작업 스레드는 **C# 전용** (`Nova C# Job Worker N`, 코어 - 1). 엔진 Job System ([JOB_SYSTEM](JOB_SYSTEM.md)) 은 Windows 에서 파이버로 잡을 갈아 끼우는데 CLR 은 파이버 위에서 관리 코드를 돌릴 수 없어서다. 큐는 `ConcurrentQueue` (잡마다 표 = min(묶음 수, 작업자 + 1), 표는 묶음을 원자 연산으로 가져가 다 떨어질 때까지), 쉬는 작업자는 세마포어
- 잡 데이터는 비관리 메모리에 그대로 복사 (struct 에 참조가 없으므로), `delegate*` 로 `IJobParallelForProducer<T>.Execute` 를 부른다 — 제약 호출이라 JIT 가 `Execute` 를 바로 부른다. 예약 하나에 GC 할당이 없다 (기록 · 표 되쓰기)
- 안전 노드 (`AtomicSafetyHandle`): 버전 · 잡 잠금 (읽기 수 + 쓰기) · Temp 표시. 잡 사본의 핸들에는 `kJob` 표시를 넣어 잠금을 건너뛴다. 의존성 검사는 잠근 잡이 새 잡의 의존성 조상인지 본다
- 네이티브: `ScriptBindings` `Prof_Begin/End/SetThreadName` (C# 에서 Profiler 구간 — 메인은 계층, 다른 스레드는 Timeline)

## 검사

`Tools/tests/run_tests.ps1 -Only csjobs` — 의존성 사슬 (더하기 → 곱하기 → 합), 여러 스레드 (12 스레드에서 돌았음), IJobFor 차례 · Run, 묶음 잡, ParallelWriter (목록 · 해시 맵), CombineDependencies, DeallocateOnJobCompletion, Dispose(JobHandle), 작업자 0, 안전 검사 9 가지 (위 표), 잡 안 예외 → Console, Profiler 작업자 줄, 배수, 잡이 도는 중 Play 멈추기.

결과 (12 스레드 PC, 에디터): 보이드 20 만 × 8 걸음 — Run (한 스레드) 21.2 ms, Schedule 2.4 ms → **약 8.7 배** (게임 스크립트 Release 빌드 — [Burst 1 단계](BURST.md). Debug 빌드 때는 118 → 12.6 ms).


## 아직

- B-5: `IJobParallelForTransform` · `RaycastCommand`, B-4: 네이티브 컴파일 (B-3 `[BurstCompile]` 1 단계는 [BURST](BURST.md))
- `IJobParallelForDefer` (NativeList 길이로 나중에), `[NativeSetThreadIndex]`, `JobHandle.ScheduleBatchedJobs` 묶어 늦추기 (지금은 예약 즉시 시작)
- 엔진 Job System 과 작업자 공유 (지금은 C# 작업자가 따로 — 둘 다 바쁘면 코어를 나눠 쓴다)
