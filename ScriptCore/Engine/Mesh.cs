using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NovaEngine.Collections;
using NovaEngine.Collections.LowLevel.Unsafe;
using NovaEngine.Interop;
using NovaEngine.Mathematics;
using NovaEngine.Rendering;

namespace NovaEngine.Rendering
{
    // UnityEngine.Rendering 과 같은 이름 (Mesh.MeshData · SetVertexBufferParams)
    public enum VertexAttribute { Position = 0, Normal = 1, Tangent = 2, Color = 3, TexCoord0 = 4, TexCoord1 = 5, TexCoord2 = 6, TexCoord3 = 7, TexCoord4 = 8, TexCoord5 = 9, TexCoord6 = 10, TexCoord7 = 11, BlendWeight = 12, BlendIndices = 13 }
    public enum VertexAttributeFormat { Float32 = 0, Float16 = 1, UNorm8 = 2, SNorm8 = 3, UNorm16 = 4, SNorm16 = 5, UInt8 = 6, SInt8 = 7, UInt16 = 8, SInt16 = 9, UInt32 = 10, SInt32 = 11 }
    public enum IndexFormat { UInt16 = 0, UInt32 = 1 }
    [Flags] public enum MeshUpdateFlags { Default = 0, DontValidateIndices = 1, DontResetBoneBounds = 2, DontNotifyMeshUsers = 4, DontRecalculateBounds = 8 }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexAttributeDescriptor
    {
        public VertexAttribute attribute;
        public VertexAttributeFormat format;
        public int dimension;
        public int stream;
        public VertexAttributeDescriptor(VertexAttribute attribute = VertexAttribute.Position, VertexAttributeFormat format = VertexAttributeFormat.Float32, int dimension = 3, int stream = 0)
        { this.attribute = attribute; this.format = format; this.dimension = dimension; this.stream = stream; }
        public override string ToString() => $"(attr={attribute} fmt={format} dim={dimension} stream={stream})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SubMeshDescriptor
    {
        public int indexStart;
        public int indexCount;
        public MeshTopology topology;
        public int baseVertex;
        public int firstVertex;
        public int vertexCount;
        public Bounds bounds;
        public SubMeshDescriptor(int indexStart, int indexCount, MeshTopology topology = MeshTopology.Triangles)
        { this.indexStart = indexStart; this.indexCount = indexCount; this.topology = topology; baseVertex = 0; firstVertex = 0; vertexCount = 0; bounds = default; }
    }
}

namespace NovaEngine
{
    public enum MeshTopology { Triangles = 0, Quads = 2, Lines = 3, LineStrip = 4, Points = 5 }

    /// <summary>
    /// 메시 (UnityEngine.Mesh) — <c>new Mesh()</c> 로 만들어 정점 · 삼각형을 넣고 <c>MeshFilter.mesh</c> 에. 정점 65535 개까지 (16 비트 색인).
    /// 바뀐 메시는 프레임마다 한 번 GPU 로 올라간다. <c>Mesh.AllocateWritableMeshData</c> → 잡에서 채우고 → <c>ApplyAndDisposeWritableMeshData</c>.
    /// </summary>
    public sealed unsafe partial class Mesh : Object
    {
        readonly ulong m_Owner;   // SkinnedMeshRenderer.sharedMesh (BlendShape 만) — 0 이면 m_Handle
        internal int m_Handle;
        bool m_Destroyed;

        static readonly Dictionary<int, WeakReference<Mesh>> s_ByHandle = new Dictionary<int, WeakReference<Mesh>>();
        static readonly ConcurrentQueue<int> s_Collected = new ConcurrentQueue<int>();

        internal Mesh(ulong owner) { m_Owner = owner; }
        public Mesh()
        {
            m_Handle = Native.Api.MESH_Create();
            lock (s_ByHandle) s_ByHandle[m_Handle] = new WeakReference<Mesh>(this);
        }
        Mesh(int handle, bool wrap) { m_Handle = handle; }
        ~Mesh() { if (m_Handle != 0 && !m_Destroyed) s_Collected.Enqueue(m_Handle); }   // 파이널라이저 스레드 — 메인이 푼다

        /// <summary>네이티브 손잡이 → 같은 C# 객체 (MeshFilter.sharedMesh 를 두 번 읽어도 같은 것)</summary>
        internal static Mesh FromHandle(int handle)
        {
            if (handle == 0) return null;
            lock (s_ByHandle)
            {
                if (s_ByHandle.TryGetValue(handle, out var w) && w.TryGetTarget(out Mesh m)) return m;
                var created = new Mesh(handle, true);
                s_ByHandle[handle] = new WeakReference<Mesh>(created);
                return created;
            }
        }
        internal static void ReleaseCollected()
        {
            while (s_Collected.TryDequeue(out int h))
            {
                lock (s_ByHandle) s_ByHandle.Remove(h);
                Native.Api.MESH_Release(h);
            }
        }
        internal void DestroyInternal()
        {
            if (m_Destroyed || m_Handle == 0) return;
            m_Destroyed = true;
            lock (s_ByHandle) s_ByHandle.Remove(m_Handle);
            Native.Api.MESH_Release(m_Handle);
            GC.SuppressFinalize(this);
        }

        internal override bool IsAlive() => m_Owner != 0 ? Native_IsValid(m_Owner) : !m_Destroyed && m_Handle != 0;
        public override bool Equals(object other) => ReferenceEquals(this, other) || (other is Mesh o && o.m_Handle == m_Handle && m_Handle != 0 && o.m_Owner == m_Owner);
        public override int GetHashCode() => m_Owner != 0 ? m_Owner.GetHashCode() : m_Handle;

        void CheckEditable()
        {
            if (m_Owner != 0 && m_Handle == 0) throw new InvalidOperationException("This SkinnedMeshRenderer mesh only exposes blend shapes in NOVA (use a MeshFilter mesh to edit vertices).");
            if (m_Destroyed) throw new ObjectDisposedException("Mesh", "The Mesh has been destroyed.");
        }

        public override string name
        {
            get => m_Handle != 0 ? Native.Str(Native.Api.MESH_GetName(m_Handle)) : "Mesh";
            set { CheckEditable(); fixed (byte* p = Native.Utf8(value)) Native.Api.MESH_SetName(m_Handle, p); }
        }
        public int vertexCount => m_Handle != 0 && !m_Destroyed ? Native.Api.MESH_GetInt(m_Handle, 0) : 0;
        public int subMeshCount { get => m_Handle != 0 && !m_Destroyed ? Native.Api.MESH_GetInt(m_Handle, 2) : 0; set { if (value > 1) throw new NotSupportedException("NOVA meshes have one sub mesh"); } }
        /// <summary>NOVA 는 16 비트 색인 (정점 65535 개까지)</summary>
        public IndexFormat indexFormat { get => IndexFormat.UInt16; set { if (value != IndexFormat.UInt16) throw new NotSupportedException("NOVA meshes use 16-bit indices (up to 65535 vertices)"); } }
        public bool isReadable => true;
        public Bounds bounds
        {
            get { CheckEditable(); Vector3 c, e; Native.Api.MESH_Bounds(m_Handle, &c, &e); return new Bounds(c, e * 2f); }
            set { }
        }

        // ---------------------------------------------------------------- 정점 속성
        void SetAttr(int attr, float* data, int count, int dim, int stride)
        {
            CheckEditable();
            int r = Native.Api.MESH_SetAttr(m_Handle, attr, data, count, dim, stride);
            if (r == -1) throw new ArgumentException("Mesh.vertices is too large: " + count + " vertices (NOVA meshes support up to 65535 — 16-bit indices)");
            if (r == 0) throw new ArgumentException("Mesh." + (attr == 1 ? "normals" : attr == 2 ? "uv" : "tangents") + " is out of bounds. The supplied array needs to be the same size as the Mesh.vertices array (" + vertexCount + ", got " + count + ").");
        }
        T[] GetAttr<T>(int attr, int dim) where T : unmanaged
        {
            CheckEditable();
            int n = vertexCount;
            var a = new T[n];
            fixed (T* p = a) Native.Api.MESH_GetAttr(m_Handle, attr, (float*)p, n, dim);
            return a;
        }
        void GetAttrList<T>(int attr, int dim, List<T> list) where T : unmanaged
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            list.Clear();
            list.AddRange(GetAttr<T>(attr, dim));
        }

        public Vector3[] vertices { get => GetAttr<Vector3>(0, 3); set => SetVertices(value); }
        public Vector3[] normals { get => GetAttr<Vector3>(1, 3); set => SetNormals(value); }
        public Vector4[] tangents { get => GetAttr<Vector4>(3, 4); set => SetTangents(value); }
        public Vector2[] uv { get => GetAttr<Vector2>(2, 2); set => SetUVs(0, value); }
        public int[] triangles { get => GetTriangles(0); set => SetTriangles(value, 0); }

        public void SetVertices(Vector3[] inVertices) { if (inVertices == null) throw new ArgumentNullException(nameof(inVertices)); fixed (Vector3* p = inVertices) SetAttr(0, (float*)p, inVertices.Length, 3, 12); }
        public void SetVertices(List<Vector3> inVertices) => SetVertices(inVertices?.ToArray());
        public void SetVertices<T>(NativeArray<T> inVertices) where T : unmanaged { inVertices.CheckRead(); SetAttr(0, (float*)inVertices.m_Buffer, inVertices.Length, sizeof(T) / 4, sizeof(T)); }
        public void SetNormals(Vector3[] inNormals) { if (inNormals == null) throw new ArgumentNullException(nameof(inNormals)); fixed (Vector3* p = inNormals) SetAttr(1, (float*)p, inNormals.Length, 3, 12); }
        public void SetNormals(List<Vector3> inNormals) => SetNormals(inNormals?.ToArray());
        public void SetNormals<T>(NativeArray<T> inNormals) where T : unmanaged { inNormals.CheckRead(); SetAttr(1, (float*)inNormals.m_Buffer, inNormals.Length, sizeof(T) / 4, sizeof(T)); }
        public void SetTangents(Vector4[] inTangents) { if (inTangents == null) throw new ArgumentNullException(nameof(inTangents)); fixed (Vector4* p = inTangents) SetAttr(3, (float*)p, inTangents.Length, 4, 16); }
        public void SetTangents(List<Vector4> inTangents) => SetTangents(inTangents?.ToArray());
        public void SetTangents<T>(NativeArray<T> inTangents) where T : unmanaged { inTangents.CheckRead(); SetAttr(3, (float*)inTangents.m_Buffer, inTangents.Length, sizeof(T) / 4, sizeof(T)); }
        public void SetUVs(int channel, Vector2[] uvs)
        {
            if (uvs == null) throw new ArgumentNullException(nameof(uvs));
            if (channel != 0) return;   // NOVA 정점은 UV 한 벌
            fixed (Vector2* p = uvs) SetAttr(2, (float*)p, uvs.Length, 2, 8);
        }
        public void SetUVs(int channel, List<Vector2> uvs) => SetUVs(channel, uvs?.ToArray());
        public void SetUVs<T>(int channel, NativeArray<T> uvs) where T : unmanaged { if (channel != 0) return; uvs.CheckRead(); SetAttr(2, (float*)uvs.m_Buffer, uvs.Length, sizeof(T) / 4, sizeof(T)); }
        public void GetVertices(List<Vector3> vertices) => GetAttrList(0, 3, vertices);
        public void GetNormals(List<Vector3> normals) => GetAttrList(1, 3, normals);
        public void GetTangents(List<Vector4> tangents) => GetAttrList(3, 4, tangents);
        public void GetUVs(int channel, List<Vector2> uvs) { if (channel == 0) GetAttrList(2, 2, uvs); else uvs?.Clear(); }

        // ---------------------------------------------------------------- 색인
        void SetIndexData(void* data, int count, bool is16, int submesh)
        {
            CheckEditable();
            if (submesh != 0) throw new ArgumentOutOfRangeException(nameof(submesh), "NOVA meshes have one sub mesh (0)");
            if (count % 3 != 0) throw new ArgumentException("Triangle index count must be a multiple of 3 (got " + count + ")");
            int r = Native.Api.MESH_SetIndices(m_Handle, data, count, is16 ? 1 : 0);
            if (r == -2) throw new ArgumentException("Mesh.triangles: some indices reference out of bounds vertices (vertexCount " + vertexCount + ")");
        }
        public void SetTriangles(int[] triangles, int submesh, bool calculateBounds = true, int baseVertex = 0)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (baseVertex != 0) { var t = (int[])triangles.Clone(); for (int i = 0; i < t.Length; i++) t[i] += baseVertex; triangles = t; }
            fixed (int* p = triangles) SetIndexData(p, triangles.Length, false, submesh);
        }
        public void SetTriangles(List<int> triangles, int submesh, bool calculateBounds = true, int baseVertex = 0) => SetTriangles(triangles?.ToArray(), submesh, calculateBounds, baseVertex);
        public void SetIndices(int[] indices, MeshTopology topology, int submesh, bool calculateBounds = true, int baseVertex = 0)
        {
            if (topology != MeshTopology.Triangles) throw new NotSupportedException("NOVA meshes support MeshTopology.Triangles");
            SetTriangles(indices, submesh, calculateBounds, baseVertex);
        }
        public void SetIndices<T>(NativeArray<T> indices, MeshTopology topology, int submesh, bool calculateBounds = true, int baseVertex = 0) where T : unmanaged
        {
            if (topology != MeshTopology.Triangles) throw new NotSupportedException("NOVA meshes support MeshTopology.Triangles");
            if (sizeof(T) != 2 && sizeof(T) != 4) throw new ArgumentException("indices must be ushort or int");
            if (baseVertex != 0) throw new NotSupportedException("baseVertex with NativeArray indices");
            indices.CheckRead();
            SetIndexData(indices.m_Buffer, indices.Length, sizeof(T) == 2, submesh);
        }
        public int[] GetTriangles(int submesh)
        {
            CheckEditable();
            if (submesh != 0) return Array.Empty<int>();
            var a = new int[Native.Api.MESH_GetInt(m_Handle, 1)];
            fixed (int* p = a) Native.Api.MESH_GetIndices(m_Handle, p, a.Length);
            return a;
        }
        public int[] GetIndices(int submesh) => GetTriangles(submesh);
        public void GetTriangles(List<int> triangles, int submesh) { triangles.Clear(); triangles.AddRange(GetTriangles(submesh)); }
        public uint GetIndexCount(int submesh) => submesh == 0 && m_Handle != 0 ? (uint)Native.Api.MESH_GetInt(m_Handle, 1) : 0u;
        public MeshTopology GetTopology(int submesh) => MeshTopology.Triangles;

        public void RecalculateNormals() { CheckEditable(); Native.Api.MESH_Op(m_Handle, 0); }
        public void RecalculateBounds() { CheckEditable(); Native.Api.MESH_Op(m_Handle, 1); }
        public void RecalculateTangents() { CheckEditable(); Native.Api.MESH_Op(m_Handle, 2); }
        public void Clear(bool keepVertexLayout = true) { CheckEditable(); Native.Api.MESH_Op(m_Handle, 3); }
        /// <summary>지금 GPU 로 (보통은 프레임마다 저절로)</summary>
        public void UploadMeshData(bool markNoLongerReadable) { CheckEditable(); Native.Api.MESH_Op(m_Handle, 4); }
        public void MarkDynamic() { }
        public void MarkModified() { if (m_Handle != 0) Native.Api.MESH_Op(m_Handle, 1); }

        /// <summary>정점 · 색인을 복사한 새 메시 (Instantiate · MeshFilter.mesh)</summary>
        internal Mesh CloneMesh()
        {
            CheckEditable();
            var m = new Mesh();
            m.name = name + " (Instance)";
            m.SetVertices(vertices);
            m.SetNormals(normals);
            m.SetUVs(0, uv);
            m.SetTangents(tangents);
            m.SetTriangles(triangles, 0);
            return m;
        }

        // ---------------------------------------------------------------- BlendShape (SkinnedMeshRenderer.sharedMesh)
        public int blendShapeCount => m_Owner != 0 ? Native.Api.SMR_Count(m_Owner) : 0;
        public string GetBlendShapeName(int index) => m_Owner != 0 ? Native.Str(Native.Api.SMR_Name(m_Owner, index)) : null;
        public int GetBlendShapeIndex(string name)
        {
            if (m_Owner == 0) return -1;
            fixed (byte* p = Native.Utf8(name)) return Native.Api.SMR_Index(m_Owner, p);
        }
    }

    /// <summary>렌더링할 메시 (UnityEngine.MeshFilter). NOVA: mesh 는 처음 읽을 때 sharedMesh 를 복사해 이 필터 것으로 (Unity 와 같다)</summary>
    public sealed unsafe class MeshFilter : Component
    {
        internal MeshFilter() { }
        static readonly Dictionary<ulong, Mesh> s_Instanced = new Dictionary<ulong, Mesh>();

        public Mesh sharedMesh
        {
            get => Mesh.FromHandle(Native.Api.MESH_FromFilter(m_Id));
            set
            {
                if (value is not null && value.m_Handle == 0) throw new ArgumentException("This mesh can not be assigned (SkinnedMeshRenderer blend-shape view)");
                Native.Api.MESH_SetToFilter(m_Id, value is null ? 0 : value.m_Handle);
                s_Instanced.Remove(m_Id);
            }
        }
        public Mesh mesh
        {
            get
            {
                if (s_Instanced.TryGetValue(m_Id, out Mesh own) && own == sharedMesh) return own;
                Mesh shared = sharedMesh;
                if (shared is null) return null;
                Mesh copy = shared.CloneMesh();
                Native.Api.MESH_SetToFilter(m_Id, copy.m_Handle);
                s_Instanced[m_Id] = copy;
                return copy;
            }
            set => sharedMesh = value;
        }
    }

    public sealed unsafe partial class Mesh
    {
        // ---------------------------------------------------------------- MeshData (Unity 의 Mesh.MeshData — 잡에서 메시를 만들고 읽기)
        internal const int kMaxAttributes = 16;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshDataBlock
        {
            public byte* Vertices;
            public int VertexCount;
            public int VertexStride;
            public int AttributeCount;
            public fixed int Attributes[kMaxAttributes * 5];   // attribute, format, dimension, stream, offset
            public void* Indices;
            public int IndexCount;
            public int IndexFormat;   // 0 UInt16, 1 UInt32
            public int SubMeshCount;
            public SubMeshDescriptor SubMesh0;
            public int ReadOnly;
        }

        /// <summary>메시 데이터 묶음 (Unity 의 Mesh.MeshDataArray) — 잡에 넘길 수 있다. 다 쓰면 ApplyAndDisposeWritableMeshData 또는 Dispose</summary>
        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer, NativeContainerIsAtomicWriteOnly]   // IJobParallelFor 에서 index 마다 자기 메시
        public unsafe struct MeshDataArray : IDisposable
        {
            internal MeshDataBlock* m_Blocks;
            internal int m_Length;
            internal AtomicSafetyHandle m_Safety;

            public int Length => m_Length;
            public MeshData this[int index]
            {
                get
                {
                    if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    if ((uint)index >= (uint)m_Length) CollectionHelper.ThrowIndex(index, m_Length);
                    return new MeshData { m_Block = m_Blocks + index, m_Safety = m_Safety };
                }
            }
            public void Dispose()
            {
                if (CollectionChecks.Enabled && !AtomicSafetyHandle.IsDefaultValue(m_Safety)) AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
                if (m_Blocks == null) return;
                if (CollectionChecks.Enabled)
                {
                    AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
                    AtomicSafetyHandle.Release(m_Safety);
                }
                for (int i = 0; i < m_Length; i++)
                {
                    if (m_Blocks[i].Vertices != null) UnsafeUtility.Free(m_Blocks[i].Vertices, Allocator.Persistent);
                    if (m_Blocks[i].Indices != null) UnsafeUtility.Free(m_Blocks[i].Indices, Allocator.Persistent);
                }
                UnsafeUtility.Free(m_Blocks, Allocator.Persistent);
                m_Blocks = null;
            }
        }

        /// <summary>메시 하나의 정점 · 색인 버퍼 (Unity 의 Mesh.MeshData)</summary>
        [StructLayout(LayoutKind.Sequential)]
        [NativeContainer]
        public unsafe struct MeshData
        {
            internal MeshDataBlock* m_Block;
            internal AtomicSafetyHandle m_Safety;

            void CheckWrite() { if (CollectionChecks.Enabled) { AtomicSafetyHandle.CheckWriteAndThrow(m_Safety); if (m_Block->ReadOnly != 0) throw new InvalidOperationException("This MeshData is read-only (AcquireReadOnlyMeshData)"); } }
            void CheckRead() { if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckReadAndThrow(m_Safety); }

            public int vertexCount { get { CheckRead(); return m_Block->VertexCount; } }
            public int vertexBufferCount => 1;
            public int GetVertexBufferStride(int stream) { CheckRead(); return stream == 0 ? m_Block->VertexStride : 0; }

            public void SetVertexBufferParams(int vertexCount, params VertexAttributeDescriptor[] attributes)
            {
                fixed (VertexAttributeDescriptor* p = attributes) SetParams(vertexCount, p, attributes?.Length ?? 0);
            }
            public void SetVertexBufferParams(int vertexCount, NativeArray<VertexAttributeDescriptor> attributes)
            {
                attributes.CheckRead();
                SetParams(vertexCount, (VertexAttributeDescriptor*)attributes.m_Buffer, attributes.Length);
            }
            void SetParams(int vertexCount, VertexAttributeDescriptor* attrs, int count)
            {
                CheckWrite();
                if (vertexCount < 0 || vertexCount > 65535) throw new ArgumentOutOfRangeException(nameof(vertexCount), "NOVA meshes support up to 65535 vertices");
                if (count > kMaxAttributes) throw new ArgumentException("Too many vertex attributes (max " + kMaxAttributes + ")");
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    VertexAttributeDescriptor a = attrs[i];
                    if (a.stream != 0) throw new NotSupportedException("NOVA MeshData uses one vertex stream (stream 0)");
                    if (a.dimension < 1 || a.dimension > 4) throw new ArgumentException("Vertex attribute dimension must be 1..4 (" + a + ")");
                    int size = FormatSize(a.format) * a.dimension;
                    m_Block->Attributes[i * 5 + 0] = (int)a.attribute;
                    m_Block->Attributes[i * 5 + 1] = (int)a.format;
                    m_Block->Attributes[i * 5 + 2] = a.dimension;
                    m_Block->Attributes[i * 5 + 3] = a.stream;
                    m_Block->Attributes[i * 5 + 4] = offset;
                    offset += (size + 3) & ~3;   // 4 바이트 정렬 (Unity 와 같다)
                }
                m_Block->AttributeCount = count;
                m_Block->VertexStride = offset;
                if (m_Block->Vertices != null) UnsafeUtility.Free(m_Block->Vertices, Allocator.Persistent);
                long bytes = (long)offset * vertexCount;
                m_Block->Vertices = (byte*)UnsafeUtility.Malloc(Math.Max(bytes, 1), 16, Allocator.Persistent);
                UnsafeUtility.MemClear(m_Block->Vertices, bytes);
                m_Block->VertexCount = vertexCount;
            }

            /// <summary>정점 버퍼를 T 의 배열로 (T 는 정점 한 개 struct 또는 그 일부 — 복사 없음)</summary>
            public NativeArray<T> GetVertexData<T>(int stream = 0) where T : unmanaged
            {
                CheckRead();
                if (stream != 0) throw new ArgumentOutOfRangeException(nameof(stream));
                long bytes = (long)m_Block->VertexStride * m_Block->VertexCount;
                if (bytes % sizeof(T) != 0) throw new InvalidOperationException("Vertex buffer of " + bytes + " bytes can not be viewed as " + typeof(T).Name + " (" + sizeof(T) + " bytes)");
                var a = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(m_Block->Vertices, (int)(bytes / sizeof(T)), Allocator.None);
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref a, m_Safety);
                return a;
            }

            public void SetIndexBufferParams(int indexCount, IndexFormat format)
            {
                CheckWrite();
                if (indexCount < 0) throw new ArgumentOutOfRangeException(nameof(indexCount));
                if (m_Block->Indices != null) UnsafeUtility.Free(m_Block->Indices, Allocator.Persistent);
                long bytes = (long)indexCount * (format == IndexFormat.UInt16 ? 2 : 4);
                m_Block->Indices = UnsafeUtility.Malloc(Math.Max(bytes, 1), 16, Allocator.Persistent);
                UnsafeUtility.MemClear(m_Block->Indices, bytes);
                m_Block->IndexCount = indexCount;
                m_Block->IndexFormat = (int)format;
            }
            public IndexFormat indexFormat { get { CheckRead(); return (IndexFormat)m_Block->IndexFormat; } }
            public NativeArray<T> GetIndexData<T>() where T : unmanaged
            {
                CheckRead();
                int size = m_Block->IndexFormat == 0 ? 2 : 4;
                if (sizeof(T) != size) throw new InvalidOperationException("Index data is " + (IndexFormat)m_Block->IndexFormat + "; use " + (size == 2 ? "ushort" : "int/uint"));
                var a = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(m_Block->Indices, m_Block->IndexCount, Allocator.None);
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref a, m_Safety);
                return a;
            }

            public int subMeshCount
            {
                get { CheckRead(); return m_Block->SubMeshCount; }
                set { CheckWrite(); if (value > 1) throw new NotSupportedException("NOVA meshes have one sub mesh"); m_Block->SubMeshCount = value; }
            }
            public void SetSubMesh(int index, SubMeshDescriptor desc, MeshUpdateFlags flags = MeshUpdateFlags.Default)
            {
                CheckWrite();
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index), "NOVA meshes have one sub mesh (0)");
                if (desc.topology != MeshTopology.Triangles) throw new NotSupportedException("NOVA meshes support MeshTopology.Triangles");
                m_Block->SubMesh0 = desc;
                if (m_Block->SubMeshCount == 0) m_Block->SubMeshCount = 1;
            }
            public SubMeshDescriptor GetSubMesh(int index) { CheckRead(); return index == 0 ? m_Block->SubMesh0 : default; }

            int Find(VertexAttribute attr)
            {
                for (int i = 0; i < m_Block->AttributeCount; i++) if (m_Block->Attributes[i * 5] == (int)attr) return i;
                return -1;
            }
            public bool HasVertexAttribute(VertexAttribute attr) { CheckRead(); return Find(attr) >= 0; }
            public int GetVertexAttributeDimension(VertexAttribute attr) { CheckRead(); int i = Find(attr); return i < 0 ? 0 : m_Block->Attributes[i * 5 + 2]; }
            public VertexAttributeFormat GetVertexAttributeFormat(VertexAttribute attr) { CheckRead(); int i = Find(attr); return i < 0 ? VertexAttributeFormat.Float32 : (VertexAttributeFormat)m_Block->Attributes[i * 5 + 1]; }
            public int GetVertexAttributeOffset(VertexAttribute attr) { CheckRead(); int i = Find(attr); return i < 0 ? -1 : m_Block->Attributes[i * 5 + 4]; }
            public int GetVertexAttributeStream(VertexAttribute attr) { CheckRead(); return Find(attr) < 0 ? -1 : 0; }

            // 읽기 도우미 (Float32 · Float16 → float)
            void ReadAttr(VertexAttribute attr, float* dst, int dim)
            {
                CheckRead();
                int i = Find(attr);
                int n = m_Block->VertexCount;
                if (i < 0) { UnsafeUtility.MemClear(dst, (long)n * dim * 4); return; }
                ReadInto(m_Block, i, dst, dim);
            }
            public void GetVertices(NativeArray<Vector3> outVertices) { CheckOut(outVertices.Length); outVertices.CheckWrite(); ReadAttr(VertexAttribute.Position, (float*)outVertices.m_Buffer, 3); }
            public void GetNormals(NativeArray<Vector3> outNormals) { CheckOut(outNormals.Length); outNormals.CheckWrite(); ReadAttr(VertexAttribute.Normal, (float*)outNormals.m_Buffer, 3); }
            public void GetTangents(NativeArray<Vector4> outTangents) { CheckOut(outTangents.Length); outTangents.CheckWrite(); ReadAttr(VertexAttribute.Tangent, (float*)outTangents.m_Buffer, 4); }
            public void GetUVs(int channel, NativeArray<Vector2> outUVs) { CheckOut(outUVs.Length); outUVs.CheckWrite(); ReadAttr(VertexAttribute.TexCoord0 + channel, (float*)outUVs.m_Buffer, 2); }
            void CheckOut(int length) { if (length < m_Block->VertexCount) throw new ArgumentException("The output array needs at least vertexCount (" + m_Block->VertexCount + ") elements"); }
            public void GetIndices(NativeArray<int> outIndices, int submesh, bool applyBaseVertex = true)
            {
                CheckRead();
                outIndices.CheckWrite();
                int start = 0, count = m_Block->IndexCount;
                if (m_Block->SubMeshCount > 0) { start = m_Block->SubMesh0.indexStart; count = m_Block->SubMesh0.indexCount; }
                if (outIndices.Length < count) throw new ArgumentException("outIndices needs " + count + " elements");
                int* dst = (int*)outIndices.m_Buffer;
                int baseVertex = applyBaseVertex && m_Block->SubMeshCount > 0 ? m_Block->SubMesh0.baseVertex : 0;
                for (int k = 0; k < count; k++)
                    dst[k] = (m_Block->IndexFormat == 0 ? ((ushort*)m_Block->Indices)[start + k] : ((int*)m_Block->Indices)[start + k]) + baseVertex;
            }
        }

        static int FormatSize(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16: case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                default: return 1;
            }
        }

        // 속성 하나를 float 로 (dim 개씩, 모자란 성분은 0)
        static void ReadInto(MeshDataBlock* b, int attrIndex, float* dst, int dim)
        {
            var fmt = (VertexAttributeFormat)b->Attributes[attrIndex * 5 + 1];
            int adim = b->Attributes[attrIndex * 5 + 2], off = b->Attributes[attrIndex * 5 + 4];
            for (int v = 0; v < b->VertexCount; v++)
            {
                byte* src = b->Vertices + (long)v * b->VertexStride + off;
                for (int k = 0; k < dim; k++)
                {
                    float x = 0f;
                    if (k < adim)
                    {
                        switch (fmt)
                        {
                            case VertexAttributeFormat.Float32: x = ((float*)src)[k]; break;
                            case VertexAttributeFormat.Float16: x = (float)((Half*)src)[k]; break;
                            case VertexAttributeFormat.UNorm8: x = src[k] / 255f; break;
                            case VertexAttributeFormat.SNorm8: x = Math.Max(((sbyte*)src)[k] / 127f, -1f); break;
                            case VertexAttributeFormat.UNorm16: x = ((ushort*)src)[k] / 65535f; break;
                            case VertexAttributeFormat.SNorm16: x = Math.Max(((short*)src)[k] / 32767f, -1f); break;
                            default: x = 0f; break;
                        }
                    }
                    dst[(long)v * dim + k] = x;
                }
            }
        }

        /// <summary>쓸 메시 데이터 (Unity 의 Mesh.AllocateWritableMeshData) — 잡에서 SetVertexBufferParams · GetVertexData 로 채운다</summary>
        public static MeshDataArray AllocateWritableMeshData(int meshCount)
        {
            if (meshCount < 1) throw new ArgumentOutOfRangeException(nameof(meshCount));
            var a = new MeshDataArray();
            a.m_Length = meshCount;
            a.m_Blocks = (MeshDataBlock*)UnsafeUtility.Malloc((long)sizeof(MeshDataBlock) * meshCount, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(a.m_Blocks, (long)sizeof(MeshDataBlock) * meshCount);
            a.m_Safety = CollectionHelper.CreateSafety(a.m_Blocks, Allocator.Persistent, "Mesh.MeshDataArray");
            return a;
        }

        public static void ApplyAndDisposeWritableMeshData(MeshDataArray data, Mesh mesh, MeshUpdateFlags flags = MeshUpdateFlags.Default)
        {
            if (data.Length != 1) throw new ArgumentException("MeshDataArray has " + data.Length + " meshes; pass a Mesh[]");
            ApplyAndDisposeWritableMeshData(data, new[] { mesh }, flags);
        }
        public static void ApplyAndDisposeWritableMeshData(MeshDataArray data, List<Mesh> meshes, MeshUpdateFlags flags = MeshUpdateFlags.Default) => ApplyAndDisposeWritableMeshData(data, meshes?.ToArray(), flags);
        public static void ApplyAndDisposeWritableMeshData(MeshDataArray data, Mesh[] meshes, MeshUpdateFlags flags = MeshUpdateFlags.Default)
        {
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (meshes.Length != data.Length) throw new ArgumentException("meshes (" + meshes.Length + ") must match MeshDataArray.Length (" + data.Length + ")");
            if (CollectionChecks.Enabled) AtomicSafetyHandle.CheckWriteAndThrow(data.m_Safety);   // 쓰는 잡이 Complete 되었나
            try
            {
                for (int i = 0; i < data.Length; i++) Apply(data.m_Blocks + i, meshes[i]);
            }
            finally { data.Dispose(); }
        }

        static void Apply(MeshDataBlock* b, Mesh mesh)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));
            mesh.CheckEditable();
            int n = b->VertexCount;
            float* tmp = (float*)UnsafeUtility.Malloc(Math.Max((long)n * 16, 16), 16, Allocator.Temp);
            int pos = -1;
            for (int i = 0; i < b->AttributeCount; i++) if (b->Attributes[i * 5] == (int)VertexAttribute.Position) pos = i;
            if (pos < 0) throw new InvalidOperationException("MeshData has no Position attribute (SetVertexBufferParams)");
            Put(b, mesh, tmp, pos, 0, 3);
            for (int i = 0; i < b->AttributeCount; i++)
            {
                switch ((VertexAttribute)b->Attributes[i * 5])
                {
                    case VertexAttribute.Normal: Put(b, mesh, tmp, i, 1, 3); break;
                    case VertexAttribute.TexCoord0: Put(b, mesh, tmp, i, 2, 2); break;
                    case VertexAttribute.Tangent: Put(b, mesh, tmp, i, 3, 4); break;
                }
            }
            int start = 0, count = b->IndexCount, baseVertex = 0;
            if (b->SubMeshCount > 0) { start = b->SubMesh0.indexStart; count = b->SubMesh0.indexCount; baseVertex = b->SubMesh0.baseVertex; }
            if (start < 0 || start + count > b->IndexCount) throw new ArgumentException("SubMesh index range " + start + " + " + count + " is outside the index buffer (" + b->IndexCount + ")");
            bool is16 = b->IndexFormat == 0;
            if (baseVertex == 0)
                mesh.SetIndexData((byte*)b->Indices + (long)start * (is16 ? 2 : 4), count, is16, 0);
            else
            {
                var idx = new int[count];
                for (int k = 0; k < count; k++) idx[k] = (is16 ? ((ushort*)b->Indices)[start + k] : ((int*)b->Indices)[start + k]) + baseVertex;
                fixed (int* p = idx) mesh.SetIndexData(p, count, false, 0);
            }
        }

        // 속성 하나를 메시에: Float32 이고 차원이 넉넉하면 간격째 그대로, 아니면 float 로 바꿔서
        static void Put(MeshDataBlock* b, Mesh mesh, float* tmp, int attrIndex, int nativeAttr, int dim)
        {
            var fmt = (VertexAttributeFormat)b->Attributes[attrIndex * 5 + 1];
            int adim = b->Attributes[attrIndex * 5 + 2], n = b->VertexCount;
            if (fmt == VertexAttributeFormat.Float32 && adim >= dim)
                mesh.SetAttr(nativeAttr, (float*)(b->Vertices + b->Attributes[attrIndex * 5 + 4]), n, dim, b->VertexStride);
            else
            {
                ReadInto(b, attrIndex, tmp, dim);
                mesh.SetAttr(nativeAttr, tmp, n, dim, dim * 4);
            }
        }

        /// <summary>읽을 메시 데이터 (Unity 의 Mesh.AcquireReadOnlyMeshData) — 잡에서 GetVertices · GetIndices. 다 쓰면 Dispose</summary>
        public static MeshDataArray AcquireReadOnlyMeshData(Mesh mesh) => AcquireReadOnlyMeshData(new[] { mesh });
        public static MeshDataArray AcquireReadOnlyMeshData(List<Mesh> meshes) => AcquireReadOnlyMeshData(meshes?.ToArray());
        public static MeshDataArray AcquireReadOnlyMeshData(Mesh[] meshes)
        {
            if (meshes == null || meshes.Length == 0) throw new ArgumentException("meshes is empty", nameof(meshes));
            MeshDataArray a = AllocateWritableMeshData(meshes.Length);
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh m = meshes[i];
                m.CheckEditable();
                MeshData d = a[i];
                int n = m.vertexCount;
                // NOVA 정점 배치 그대로: Position · Normal · TexCoord0 · Tangent (48 바이트)
                d.SetVertexBufferParams(n,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                    new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4));
                MeshDataBlock* b = a.m_Blocks + i;
                float* tmp = (float*)UnsafeUtility.Malloc(Math.Max((long)n * 16, 16), 16, Allocator.Temp);
                int[] offsets = { 0, 12, 24, 32 };
                int[] attrs = { 0, 1, 2, 3 };
                int[] dims = { 3, 3, 2, 4 };
                for (int k = 0; k < 4; k++)
                {
                    Native.Api.MESH_GetAttr(m.m_Handle, attrs[k], tmp, n, dims[k]);
                    for (int v = 0; v < n; v++)
                        UnsafeUtility.MemCpy(b->Vertices + (long)v * b->VertexStride + offsets[k], tmp + (long)v * dims[k], dims[k] * 4);
                }
                int ic = Native.Api.MESH_GetInt(m.m_Handle, 1);
                d.SetIndexBufferParams(ic, IndexFormat.UInt32);
                Native.Api.MESH_GetIndices(m.m_Handle, (int*)b->Indices, ic);
                d.subMeshCount = 1;
                d.SetSubMesh(0, new SubMeshDescriptor(0, ic));
                b->ReadOnly = 1;
            }
            return a;
        }
    }
}
