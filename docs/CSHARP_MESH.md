# C# Mesh API (런타임 메시 · Mesh.MeshData)

Unity 의 `Mesh` 와 같은 이름 · 같은 쓰임으로 C# 에서 메시를 만들고 고칩니다 — 절차 지형 조각, 파괴 · 변형, 잡으로 만드는 메시 (C# 고성능 경로 [B-5](ENGINE_ROADMAP.md)).

```csharp
var mesh = new Mesh { name = "Quad" };
mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };   // 시계 방향 = 앞면 (Unity 와 같은 왼손)
mesh.RecalculateNormals();
GetComponent<MeshFilter>().sharedMesh = mesh;
```

## Mesh

| | |
|---|---|
| 만들기 · 지우기 | `new Mesh()`, `Object.Destroy(mesh)`, `Instantiate(mesh)` (사본) — 버린 C# 객체는 네이티브 메시도 저절로 푼다 (MeshFilter 가 쓰는 동안은 남는다) |
| 정점 | `vertices` · `normals` · `uv` · `tangents` (배열), `SetVertices/SetNormals/SetUVs/SetTangents` (배열 · `List<T>` · **`NativeArray<Vector3>` · `NativeArray<float3>`**), `GetVertices(List)` …, `vertexCount` |
| 삼각형 | `triangles`, `SetTriangles(int[]/List, submesh)`, `SetIndices(int[] · NativeArray<int/ushort>, MeshTopology.Triangles, submesh)`, `GetTriangles` · `GetIndices` · `GetIndexCount` |
| 계산 | `RecalculateNormals()` (면적 가중), `RecalculateTangents()`, `RecalculateBounds()`, `bounds`, `Clear()`, `UploadMeshData()` (지금 GPU 로 — 보통은 프레임마다 저절로) |
| MeshFilter | `sharedMesh` (그 메시 그대로 — 내장 Cube 를 고치면 모든 Cube 가 바뀐다, Unity 와 같다), `mesh` (처음 읽을 때 이 필터만의 사본) |

- 정점 **65535 개까지** (16 비트 색인 — `indexFormat` 은 UInt16), 서브메시 1 개, 삼각형만, UV 한 벌, 정점 색 없음
- 범위 밖 색인 · 정점 수와 다른 노멀 배열은 `ArgumentException` (Unity 와 같은 문구)
- 바뀐 메시는 한 프레임에 한 번 GPU 로 (`ScriptBindings::Update` — 그리기 전). 런타임 메시는 씬에 저장되지 않는다

## Mesh.MeshData (잡에서 메시 만들기 · 읽기)

```csharp
var data = Mesh.AllocateWritableMeshData(1);
var md = data[0];
md.SetVertexBufferParams(vertexCount,
    new VertexAttributeDescriptor(VertexAttribute.Position),
    new VertexAttributeDescriptor(VertexAttribute.Normal),
    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
md.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
new BuildGridJob { data = data }.Schedule().Complete();   // 잡: md.GetVertexData<MyVertex>() · md.GetIndexData<ushort>() 를 채우고 SetSubMesh
Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
```

| | |
|---|---|
| `Mesh.AllocateWritableMeshData(n)` → `MeshDataArray` | 잡 필드로 넘긴다 (IJobParallelFor 에서 index 마다 메시 하나도 된다). 쓰지 않으면 `Dispose` (Play 끝 누수 알림에 잡힌다) |
| `MeshData.SetVertexBufferParams(n, attributes)` | 한 스트림에 차례로 (4 바이트 정렬). 형식 Float32 · Float16 · UNorm8/16 · SNorm8/16 — Position · Normal · Tangent · TexCoord0 를 메시에, 나머지는 무시 |
| `GetVertexData<T>()` · `GetIndexData<T>()` | 복사 없는 `NativeArray` 보기 (T = 정점 struct) |
| `subMeshCount` · `SetSubMesh(0, new SubMeshDescriptor(start, count))` | baseVertex 도 |
| `Mesh.ApplyAndDisposeWritableMeshData(data, mesh / Mesh[] / List)` | 쓰는 잡이 Complete 된 뒤 (아니면 안전 검사가 알린다) |
| `Mesh.AcquireReadOnlyMeshData(mesh)` | NOVA 정점 배치 그대로 (Position 0 · Normal 12 · TexCoord0 24 · Tangent 32, 48 바이트) + UInt32 색인. `GetVertices/GetNormals/GetUVs/GetTangents/GetIndices(NativeArray)` · `GetVertexAttributeOffset`. 쓰기는 거부 |

## 엔진 안

- `ScriptCore/Engine/Mesh.cs` (Mesh · MeshFilter · MeshData · `NovaEngine.Rendering` 의 VertexAttribute · VertexAttributeDescriptor · IndexFormat · SubMeshDescriptor · MeshUpdateFlags)
- 네이티브 `Source/Scripting/ScriptBindings.cpp` `MESH_*`: 손잡이 (int) → `shared_ptr<Mesh>` (같은 메시는 같은 손잡이), 속성 쓰기는 간격 (stride) 째 — 인터리브 정점 버퍼를 한 번에, `Mesh::Setup` 으로 올리고 `Component::s_BindingSerial` 로 컬링이 경계 구를 다시 본다

## 검사

`Tools/tests/run_tests.ps1 -Only enginejobs` — new Mesh · RecalculateNormals · bounds · 범위 밖 색인 거부, MeshFilter.mesh 사본, 잡이 만든 33 × 33 격자 (ApplyAndDispose) · 읽기 전용 MeshData 를 잡에서 · 쓰기 거부, NativeArray 정점, Destroy, 화면 (빨간 격자가 가운데의 90 %).

## 아직

- 32 비트 색인 (정점 65535 개 넘는 메시), 서브메시 여럿 · 재질 여럿, 정점 색 · UV 둘째 벌, 선 · 점 위상
- `Mesh.MeshData` 를 IJobParallelFor 로 여러 메시를 한꺼번에 (지금도 되지만 검사는 하나), `Physics.BakeMesh` (Mesh Collider 를 잡에서)
