# Mathematics · Collections (C# 고성능 경로 1 단계)

Unity 의 **Burst · Job System** 이 빠른 바탕인 두 패키지 — `Unity.Mathematics` (셰이더 같은 `float3` · `math.*`) 와 `Unity.Collections` (GC 가 모르는 `NativeArray`) — 를 같은 이름 · 같은 쓰임으로 옮겼습니다. 네임스페이스만 `NovaEngine.Mathematics` · `NovaEngine.Collections` 입니다. 다음 단계 C# Job System (B-2) · `[BurstCompile]` (B-3 · B-4) 이 이 형식들 위에서 돕니다 — [로드맵](ENGINE_ROADMAP.md) "C# 고성능 경로".

```csharp
using NovaEngine;
using NovaEngine.Mathematics;
using NovaEngine.Collections;

public class Swarm : MonoBehaviour
{
    NativeArray<float3> m_Positions;

    void Start() => m_Positions = new NativeArray<float3>(10000, Allocator.Persistent);

    void Update()
    {
        quaternion spin = quaternion.RotateY(Time.deltaTime);
        for (int i = 0; i < m_Positions.Length; i++)
            m_Positions[i] = math.rotate(spin, m_Positions[i]) + math.up() * 0.01f;

        var near = new NativeList<int>(Allocator.Temp);   // 이 프레임만 — Dispose 하지 않아도 된다
        for (int i = 0; i < m_Positions.Length; i++)
            if (math.distancesq(m_Positions[i], transform.position) < 4f) near.Add(i);
    }

    void OnDestroy() => m_Positions.Dispose();
}
```

## Mathematics

| 형식 | 내용 |
|---|---|
| `float2/3/4` · `int2/3/4` · `uint2/3/4` · `bool2/3/4` | 성분 연산자 (`+ - * / %`, 비교는 `boolN`), 스위즐 (`v.xz`, `v.zyx = ...`, `v.xxyy`), 색인 `v[i]`, `float4(float3, w)` 같은 조합 생성자 |
| `quaternion` | `Euler` (**라디안**, 기본 ZXY = Transform · `Quaternion.Euler` 와 같은 회전) · `EulerXYZ` … · `AxisAngle` · `RotateX/Y/Z` · `LookRotation` · `LookRotationSafe`, `value` (float4) |
| `float2x2` · `float3x3` · `float4x4` | **열 우선** (`c0 … c3` = 열), 스칼라 생성자는 행 순서로 적는다. `float4x4.TRS` · `Translate` · `Scale` · `LookAt`, `float3x3(quaternion)` |
| `math` | 성분마다: `abs min max clamp saturate lerp unlerp smoothstep step sqrt rsqrt rcp sin cos tan … exp log pow floor ceil round frac fmod sign radians degrees mad isnan`. 벡터: `dot cross length lengthsq distance normalize normalizesafe reflect project csum cmin cmax any all select hash asuint asfloat`. 회전 · 행렬: `mul` (행렬 곱 · `quaternion` 곱 = b 다음 a) · `rotate` · `transform` · `inverse` · `fastinverse` · `transpose` · `determinant` · `conjugate` · `slerp` · `nlerp` · `angle` · `forward/up/right(q)`, 축 `up() forward() right() …` |
| `Random` | 값 형식 xorshift (공유 상태 없음 — 잡 안에서), `NextFloat/2/3/4`, `NextInt(min, max)`, `NextFloat3Direction`, `NextQuaternionRotation`, `Random.CreateFromIndex(i)` (일마다 다른 수열). 씨앗 0 은 안 된다 |

- `NovaEngine.Vector2/3/4` · `Quaternion` · `Vector2Int/3Int` 와 서로 **암묵 변환** (Unity 와 같다) — `transform.position = myFloat3;`
- `==` 는 성분마다 `bool3` 를 돌려준다 (Unity 와 같다): `if (math.all(a == b))`
- `using NovaEngine;` 과 함께 쓰면 `Random` 이 두 개 — `using Random = NovaEngine.Mathematics.Random;` 처럼 고른다 (Unity 도 같다)
- 벡터 형식 · `math` 성분 함수는 `Tools/gen_mathematics.py` 가 만든다 (`*.gen.cs` — 손으로 고치지 말 것). 회전 · 행렬 · 난수는 손으로 쓴 `Quaternion.cs` · `Matrix.cs` · `Random.cs`

## Collections

| 형식 | 내용 |
|---|---|
| `NativeArray<T>` | 고정 길이. `T[]` · 다른 NativeArray · Span 에서 복사, `CopyFrom/To`, `ToArray`, `GetSubArray`, `Reinterpret<U>()` (`Vector3` ↔ `float3`), `AsReadOnly`, `NativeArray<T>.Copy`, `AsSpan` (NOVA) |
| `NativeList<T>` | `Add` · `AddRange` · `AddNoResize` · `Insert` · `RemoveAt` · `RemoveAtSwapBack` · `RemoveRange` · `Resize` · `Capacity` · `ElementAt` (ref) · `AsArray` (복사 없음) · `ToArray` |
| `NativeHashMap<K, V>` | 키는 `IEquatable` 비관리 형식 (`int`, `int2`, …). `Add` · `TryAdd` · `TryGetValue` · `ContainsKey` · `Remove` · 색인 · `foreach (var kv in map)` (`kv.Key` · `kv.Value`) · `GetKeyArray` · `GetValueArray` |
| `NativeHashSet<T>` · `NativeQueue<T>` · `NativeReference<T>` | 집합 · 선입선출 · 값 하나 (잡 결과) |
| 잡과 함께 ([C# Job System](CSHARP_JOBS.md)) | `Dispose(JobHandle)`, `AsParallelWriter()` (`NativeList` · `NativeQueue` · `NativeHashMap` · `NativeHashSet`) |
| 확장 | `Sort` · `BinarySearch` · `Contains` · `IndexOf` · `Unique` |
| `LowLevel.Unsafe` | `UnsafeUtility` (`Malloc` · `Free` · `MemCpy` · `MemClear` · `SizeOf` · `AlignOf` …), `GetUnsafePtr()`, `AtomicSafetyHandle` |

### Allocator

| | 수명 | 메모리 |
|---|---|---|
| `Temp` | 이 프레임만 — **다음 프레임 시작에 저절로 풀린다** (Dispose 해도 되고 안 해도 된다), 잡에 넘기지 않는다 | 스레드마다 덩어리 메모리에서 잘라 쓴다 (가장 빠름) |
| `TempJob` | 잡에 넘길 짧은 것 — 4 프레임 안에 Dispose (넘으면 경고) | 운영체제 힙 (16 바이트 정렬) |
| `Persistent` | 직접 Dispose 할 때까지 | 운영체제 힙 |

### 안전 검사 (에디터)

Unity 의 `ENABLE_UNITY_COLLECTIONS_CHECKS` 와 같다 — 에디터에서 켜고 플레이어 빌드에서 끈다 (`CollectionChecks.Enabled`, 환경 변수 `NOVA_COLLECTIONS_CHECKS=0/1` 로 바꿈 · 시작할 때 한 번 정해져 JIT 가 검사를 지운다).

- 범위 밖 색인 → `IndexOutOfRangeException`
- Dispose 뒤 사용 — **복사본으로도** (값 형식이라 복사해도 같은 메모리), 두 번 Dispose, 지난 프레임의 Temp → `ObjectDisposedException` (컨테이너마다 버전 칸 하나, Dispose 하면 버전이 올라 모든 복사본이 맞지 않게 된다)
- `AsReadOnly` · `[ReadOnly]` 에 쓰기, 하위 배열 · `Reinterpret` · `AsArray` 를 Dispose → `InvalidOperationException`
- 예약된 잡이 쓰는 동안 메인 스레드 접근 · Dispose → `InvalidOperationException` (`JobHandle.Complete()` 먼저 — [C# Job System](CSHARP_JOBS.md))
- 누수 (`NativeLeakDetection.Mode` — 에디터 기본 Enabled): **Play 가 끝날 때** 남은 TempJob · Persistent 를 Console 오류로 알리고 (`A Native Collection has not been disposed …`, 컨테이너 이름 · 바이트) 풀어 준다 — 그 뒤 옛 복사본은 `ObjectDisposedException` (매달린 포인터 없음). `EnabledWithStackTrace` 면 할당한 곳의 호출 스택. 스크립트를 다시 읽을 때도 같다

### 엔진 안

- `ScriptCore/Engine/Mathematics/*` · `ScriptCore/Engine/Collections/*` (NovaScriptCore.dll — 게임 스크립트가 참조)
- 시점: `Bridge.BeginFrame` (Play 프레임 시작 — 메인 스레드 Temp 되돌림 · TempJob 나이), `Bridge.EngineEvent(0)` (`ScriptEngine::OnPlayModeChanged` — Play 끝 누수), `UnloadGameAssembly` (다시 읽기), `nova exec` 끝 (Play 밖이면 Temp 되돌림 — `App_Info(1)` = Play 중)

## 검사

`Tools/tests/run_tests.ps1 -Only mathcollections` — math (연산 · 스위즐 · 변환, `quaternion.Euler` = `Quaternion.Euler`, `rotate` = `Quaternion * Vector3`, `LookRotationSafe` = `Quaternion.LookRotation`, TRS · 역행렬 · 행렬식, 난수 씨앗 · 범위 · 평균), 컨테이너 기능 · 안전 검사, Temp 한 프레임, TempJob 4 프레임 경고, Play 끝 누수 알림 · 풀기 · 그 뒤 무효, 벤치마크 (정보).

## 아직

- 지금은 성분마다 스칼라 코드. **게임 스크립트가 Debug 로 빌드된다** — 위 벤치마크 (4 백만 덧셈) 는 `Vector3[]` 117 ms · `NativeArray<float3>` 165 ms · Span 129 ms 로 아직 느리다. B-3 (`[BurstCompile]` 1 단계) 에서 Release 빌드 · `AggressiveOptimization` · HPC# 분석기, B-4 에서 네이티브 SIMD
- `FixedString32Bytes` 같은 고정 문자열, `NativeParallelMultiHashMap`, `NativeBitArray` · `NativeStream`
- `AsArray` 로 얻은 배열은 목록이 커진 뒤 (버퍼가 옮겨짐) 검사 없이 무효 (Unity 는 따로 잡는다)
