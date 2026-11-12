/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
// 
/////////////////////////////////////////////////////////////////////////
using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Unity.VisualScripting;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PicaVoxel
{
    /// <summary>
    /// Which type of mesh collider to use when generating meshes
    /// </summary>
    public enum CollisionMode
    {
        None,
        MeshColliderConvex,
        MeshColliderConcave
    }

    public enum MeshingMode
    {
        Greedy,
        Culled,
        Marching
    }

    [Serializable]
    public class CustomBlock
    {
        public int VoxelValue;
        public bool AllowOrientation;
        public bool HasTransparency;
        public bool UseTargetOrientationWhenSameValue;
        public bool UseBoxColliderForCursor;
        public Mesh Mesh;
    }

    public class CustomBlockData
    {
        public bool HasMesh;
        public Vector3[] Vertices;
        public Vector4[] UVs;
        public int[] Indices;
        public bool HasTransparency;
        public bool AllowOrientation;
        public bool UseTargetOrientationWhenSameValue;
        public bool UseBoxColliderForCursor;
    }

    /// <summary>
    /// The main parent script for a PicaVoxel volume
    /// </summary>
    [AddComponentMenu("PicaVoxel/PicaVoxel Volume")]
    [Serializable]
    [SelectionBase]
    [ExecuteAlways]
    public class Volume : MonoBehaviour
    {
        public GameObject ChunkPrefab;

        public int ChunkSize = 16;

        public float VoxelSize = 1f;

        public Vector3 Pivot = Vector3.zero;

        public Dictionary<(int, int, int), Chunk> Chunks = new();

        public int MaxValue;
        public CustomBlock[] CustomBlocks = Array.Empty<CustomBlock>();
        [DoNotSerialize] public Dictionary<int, CustomBlockData> CustomBlocksDict = new();
        public Color[] ColorPalette;

        public VoxelManipulator.ManipulatorMode ManipulatorMode = VoxelManipulator.ManipulatorMode.Color;

        // Infinite mode will be for infinite terrain, which will keep generating as you move
        // Non-infinite will only add chunks when edited, and all chunks will be visible/rendered at startup
        public bool IsInfinite = false;
        public int InfiniteChunkRadius;
        public Vector3Int InfiniteChunkBounds = Vector3Int.zero;
        public float InfiniteUpdateInterval = 0.25f;
        public int InfiniteUpdateSlicing = 8;
        public int InfiniteChunkRadiusUpdateMargin = 3;

        public int GenerationSeed;

        public bool IsDataReady => _voxelDataGenerator.IsReady;
        public bool IsPersisterReady => _voxelDataPersister.IsReady;

        public string Identifier;

        public float PersistenceInterval = 2f;

        public bool DelayGeneration;
        
        // Chunk generation settings
        public MeshingMode MeshingMode;
        public MeshingMode MeshColliderMeshingMode;
        public bool GenerateMeshColliderSeparately = false;
        public Material Material;
        public PhysicMaterial PhysicMaterial;
        public bool CollisionTrigger;
        public CollisionMode CollisionMode;
        public float SelfShadingIntensity = 0.2f;
        public ShadowCastingMode CastShadows = ShadowCastingMode.On;
        public bool ReceiveShadows = true;
        public int ChunkLayer;
        [HideInInspector] public I_ChunkProcessor[] ChunkProcessors;

        private Transform _cameraTransform;
        private float _infiniteUpdateTimer;
        private I_VoxelDataGenerator _voxelDataGenerator;
        private I_VoxelDataPersister _voxelDataPersister;
        private bool _isFirstPass;
        private Vector3 _lastCamOffset;

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(Identifier))
            {
                Identifier = Guid.NewGuid().ToString();
            }
        }

        private void Start()
        {
            if (!Application.isPlaying)
                return;

            _voxelDataGenerator = GetComponent<I_VoxelDataGenerator>();
            if (_voxelDataGenerator == null)
            {
                _voxelDataGenerator = gameObject.AddComponent<InfiniteSolidGenerator>();
            }

            _voxelDataGenerator.Seed = GenerationSeed;

            _voxelDataPersister = GetComponent<I_VoxelDataPersister>();
            if (_voxelDataPersister == null)
            {
                _voxelDataPersister = gameObject.AddComponent<DiskPersister>();
            }

            ChunkProcessors = GetComponentsInChildren<I_ChunkProcessor>();
            ChunkProcessors = ChunkProcessors.OrderBy(cp => cp.Order).ToArray();

            _cameraTransform = Camera.main.transform;
            _lastCamOffset = _cameraTransform.position -
                             (transform.position - (ChunkSize * VoxelSize * 0.5f * Vector3.one));

            _isFirstPass = true;

            CustomBlocksDict.Clear();
            foreach (CustomBlock cb in CustomBlocks)
            {
                CustomBlockData data = new CustomBlockData();
                if (cb.Mesh != null)
                {
                    data.Vertices = new Vector3[cb.Mesh.vertexCount];
                    data.UVs = new Vector4[cb.Mesh.vertexCount];
                    data.Indices = new int[cb.Mesh.triangles.Length];
                    for (var i = 0; i < cb.Mesh.vertices.Length; i++)
                        data.Vertices[i] = (cb.Mesh.vertices[i] * VoxelSize);
                    for (var i = 0; i < cb.Mesh.uv.Length; i++)
                        data.UVs[i] = new Vector4(cb.Mesh.uv[i].x, cb.Mesh.uv[i].y, cb.VoxelValue, 0);
                    for (var i = 0; i < cb.Mesh.triangles.Length; i++)
                        data.Indices[i] = cb.Mesh.triangles[i];
                    data.HasMesh = true;
                    data.AllowOrientation = cb.AllowOrientation;
                    data.UseTargetOrientationWhenSameValue = cb.UseTargetOrientationWhenSameValue;
                    data.UseBoxColliderForCursor = cb.UseBoxColliderForCursor;
                }

                data.HasTransparency = cb.HasTransparency;
                CustomBlocksDict.Add(cb.VoxelValue, data);
            }
        }

        Queue<Chunk> _freeChunks = new();
        private int slice = 0;
        private bool dataPass = true;
        Vector3Int thisUpdateccpos = Vector3Int.zero;
        int thisUpdateMargin = 1;

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (DelayGeneration)
                return;
            
            foreach (Chunk chunk in Chunks.Values)
            {
                chunk.CheckGeneration();
                chunk.Persist();
            }

            if (!IsInfinite && _isFirstPass && IsDataReady)
            {
                GenerateFiniteChunks();
                _isFirstPass = false;
                thisUpdateMargin = InfiniteChunkRadiusUpdateMargin;
            }

            if (!IsInfinite)
                return;

            _infiniteUpdateTimer += Time.deltaTime;
            if (_infiniteUpdateTimer >= InfiniteUpdateInterval)
            {
                _infiniteUpdateTimer = 0;

                float radunit = ChunkSize * VoxelSize;
                Vector3 halfchunk = radunit * 0.5f * Vector3.one;
                Vector3 camoffset = _cameraTransform.position - (transform.position - halfchunk);
                Vector3 camChunkPos = camoffset / radunit;
                Vector3Int ccpos = new Vector3Int(Mathf.RoundToInt(camChunkPos.x), Mathf.RoundToInt(camChunkPos.y),
                    Mathf.RoundToInt(camChunkPos.z));


                //Debug.Log(ccpos);
                //float rad2 = ((InfiniteChunkRadius*radunit)*(InfiniteChunkRadius*radunit)) + ((radunit * 0.5f) * (radunit * 0.5f));

                // If the camera moves more than a chunk in one go, regenerate the whole lot starting with the chunk at camera position
                if ((camoffset - _lastCamOffset).magnitude > radunit)
                {
                    // foreach (Chunk chunk in Chunks.Values)
                    // {
                    //     if (!_freeChunks.Contains(chunk))
                    //         _freeChunks.Enqueue(chunk);
                    // }
                    // foreach (Chunk chunk in _freeChunks)
                    //     Chunks.Remove(chunk.Position);
                    //_isFirstPass = true;
                    dataPass = true;
                    slice = 0;
                    thisUpdateccpos = ccpos;
                    thisUpdateMargin = InfiniteChunkRadius;
                    // if (_freeChunks.TryDequeue(out Chunk reuse))
                    // {
                    //     Chunks.Add((ccpos.x,ccpos.y,ccpos.z), reuse);
                    //     reuse.Initialize((ccpos.x,ccpos.y,ccpos.z), this);
                    // }
                }

                _lastCamOffset = camoffset;

                if (slice == 0)
                {
                    thisUpdateccpos = ccpos;

                    foreach (Chunk chunk in Chunks.Values)
                    {
                        Vector3Int cpos = new Vector3Int(chunk.Position.x, chunk.Position.y, chunk.Position.z);

                        if ((ccpos - cpos).sqrMagnitude > InfiniteChunkRadius * InfiniteChunkRadius)
                        {
                            if (!_freeChunks.Contains(chunk))
                                _freeChunks.Enqueue(chunk);
                        }
                    }

                    foreach (Chunk chunk in _freeChunks)
                        Chunks.Remove(chunk.Position);

                    slice++;
                    return;
                }

                int s = 1;
                for (int z = thisUpdateccpos.z - InfiniteChunkRadius; z < thisUpdateccpos.z + InfiniteChunkRadius; z++)
                    for (int y = thisUpdateccpos.y - InfiniteChunkRadius; y < thisUpdateccpos.y + InfiniteChunkRadius; y++)
                        for (int x = thisUpdateccpos.x - InfiniteChunkRadius; x < thisUpdateccpos.x + InfiniteChunkRadius; x++)
                        {
                            if ((InfiniteChunkBounds.x > 0 &&
                                 (x < -InfiniteChunkBounds.x || x > InfiniteChunkBounds.x)) ||
                                (InfiniteChunkBounds.y > 0 &&
                                 (y < -InfiniteChunkBounds.y || y > InfiniteChunkBounds.y)) ||
                                (InfiniteChunkBounds.z > 0 &&
                                 (z < -InfiniteChunkBounds.z || z > InfiniteChunkBounds.z)))
                                continue;

                            Vector3 cpos = new Vector3(x, y, z);
                            if ((thisUpdateccpos - cpos).sqrMagnitude > InfiniteChunkRadius * InfiniteChunkRadius)
                                continue;

                            if (!_isFirstPass)
                            {
                                s++;
                                if (s == InfiniteUpdateSlicing)
                                    s = 1;
                                if (s != slice)
                                    continue;
                                if ((thisUpdateccpos - cpos).sqrMagnitude <= (InfiniteChunkRadius - thisUpdateMargin) *
                                    (InfiniteChunkRadius - thisUpdateMargin))
                                    continue;
                            }

                            if (Chunks.ContainsKey((x, y, z)))
                            {
                                if (!dataPass)
                                {
                                    Chunks[(x, y, z)].SetMeshDirty();
                                }

                                continue;
                            }

                            if (_freeChunks.TryDequeue(out Chunk reuse))
                            {
                                Chunks.Add((x, y, z), reuse);
                                reuse.Initialize((x, y, z), this);
                            }
                            else
                            {
                                //Debug.Log($"No available chunks to re-use at {x}, {y}, {z}");
                                Chunks[(x, y, z)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                Chunks[(x, y, z)].Initialize((x, y, z), this);
                            }
                        }

                if (_isFirstPass)
                {
                    if (!dataPass)
                        _isFirstPass = false;
                }

                slice++;
                if (slice == InfiniteUpdateSlicing)
                {
                    if (!dataPass)
                        thisUpdateMargin = InfiniteChunkRadiusUpdateMargin;

                    slice = dataPass ? 1 : 0;

                    dataPass = !dataPass;
                }
            }
        }

        private void GenerateFiniteChunks()
        {
            if (_voxelDataGenerator is MagicaVoxelGenerator mvg)
            {
                Vector3Int size = mvg.ModelSize;
                int maxX = (size.x + ChunkSize - 1) / ChunkSize;
                int maxY = (size.y + ChunkSize - 1) / ChunkSize;
                int maxZ = (size.z + ChunkSize - 1) / ChunkSize;

                Voxel v = new Voxel();
                for (int cx = 0; cx < maxX; cx++)
                    for (int cy = 0; cy < maxY; cy++)
                        for (int cz = 0; cz < maxZ; cz++)
                        {
                            bool hasVoxel = false;
                            for (int x = cx * ChunkSize; x < (cx + 1) * ChunkSize; x++)
                            {
                                for (int y = cy * ChunkSize; y < (cy + 1) * ChunkSize; y++)
                                {
                                    for (int z = cz * ChunkSize; z < (cz + 1) * ChunkSize; z++)
                                    {
                                        if (mvg.GenerateVoxel(x, y, z, ref v))
                                        {
                                            hasVoxel = true;
                                            break;
                                        }
                                    }
                                    if (hasVoxel) break;
                                }
                                if (hasVoxel) break;
                            }

                            if (hasVoxel)
                            {
                                if (!Chunks.ContainsKey((cx, cy, cz)))
                                {
                                    Chunks[(cx, cy, cz)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                    Chunks[(cx, cy, cz)].Initialize((cx, cy, cz), this);
                                }
                            }
                        }
                return;
            }

            bool foundvox = true;

            int r = 1;

            Voxel v2 = new Voxel();
            if (_voxelDataGenerator.GenerateVoxel(0, 0, 0, ref v2))
            {
                if (!Chunks.ContainsKey((0, 0, 0)))
                    Chunks[(0, 0, 0)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                Chunks[(0, 0, 0)].Initialize((0, 0, 0), this);
            }

            while (foundvox)
            {
                foundvox = false;
                for (int x = -r; x <= r; x++)
                    for (int y = -r; y <= r; y++)
                        for (int z = -r; z <= r; z++)
                        {
                            if ((x != -r && x != r) && (y != -r && y != r) && (z != -r && z != r))
                                continue;

                            if (_voxelDataGenerator.GenerateVoxel(x, y, z, ref v2))
                            {
                                foundvox = true;

                                if (!Chunks.ContainsKey((x / ChunkSize, y / ChunkSize, z / ChunkSize)))
                                {
                                    Chunks[(x / ChunkSize, y / ChunkSize, z / ChunkSize)] =
                                        Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                    Chunks[(x / ChunkSize, y / ChunkSize, z / ChunkSize)]
                                        .Initialize((x / ChunkSize, y / ChunkSize, z / ChunkSize), this);
                                }
                            }
                        }

                r += ChunkSize;
            }
        }

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            _voxelDataGenerator.GenerateVoxel(x, y, z, ref voxel);
        }

        [CanBeNull]
        public Chunk GetChunk((int x, int y, int z) pos)
        {
            return Chunks.GetValueOrDefault(pos);
        }

        public Voxel? GetVoxelAtWorldPosition(Vector3 worldPos, out Chunk chunk, out (int x, int y, int z) pos)
        {
            pos = (0, 0, 0);
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            localPos -= Vector3.one * ChunkSize * 0.5f * VoxelSize;
            chunk = GetChunk(((int)(Mathf.Ceil(localPos.x / (ChunkSize * VoxelSize))),
                (int)(Mathf.Ceil(localPos.y / (ChunkSize * VoxelSize))),
                (int)Mathf.Ceil((localPos.z / (ChunkSize * VoxelSize)))));
            if (!chunk)
                return null;

            localPos -= chunk.transform.localPosition - (Vector3.one * ChunkSize * 0.5f * VoxelSize);

            pos = ((int)(localPos.x / (VoxelSize)), (int)(localPos.y / (VoxelSize)), (int)(localPos.z / (VoxelSize)));
            return chunk.GetVoxel(pos);
        }

        public Voxel? SetVoxelAtWorldPosition(Vector3 worldPos, Voxel newValue, out Chunk chunk,
            out (int x, int y, int z) pos, bool persist = true)
        {
            pos = (0, 0, 0);
            Vector3 localPos = transform.InverseTransformPoint(worldPos);
            localPos -= Vector3.one * ChunkSize * 0.5f * VoxelSize;
            chunk = GetChunk(((int)(Mathf.Ceil(localPos.x / (ChunkSize * VoxelSize))),
                (int)(Mathf.Ceil(localPos.y / (ChunkSize * VoxelSize))),
                (int)Mathf.Ceil((localPos.z / (ChunkSize * VoxelSize)))));
            if (!chunk)
                return null;

            localPos -= chunk.transform.localPosition - (Vector3.one * ChunkSize * 0.5f * VoxelSize);

            pos = ((int)(localPos.x / (VoxelSize)), (int)(localPos.y / (VoxelSize)), (int)(localPos.z / (VoxelSize)));
            Voxel? v = chunk.SetVoxel(pos, newValue, persist);

            return v;
        }

        public void SetVoxelByEditEvent(VoxelEditEventArgs e, bool persist = false)
        {
            if (e.VolumeId != Identifier)
                return;
            GetChunk((e.ChunkX, e.ChunkY, e.ChunkZ))?.SetVoxelByEditEvent(e, persist);
        }

        public bool LoadChunkChanges((int x, int y, int z) pos)
        {
            if (!_voxelDataPersister.IsReady)
                return false;

            return _voxelDataPersister.LoadChunk(this, pos.x, pos.y, pos.z);
        }

        public bool SaveChunkChanges((int x, int y, int z) pos, byte[] data)
        {
            if (!_voxelDataPersister.IsReady)
                return false;

            return _voxelDataPersister.SaveChunk(this, pos.x, pos.y, pos.z, data);
        }

        public void SetChunkChangesByChangesEvent(ChunkChangesEventArgs e)
        {
            if (e.VolumeId != Identifier)
                return;

            GetChunk((e.ChunkX, e.ChunkY, e.ChunkZ))?.LoadChanges(e.Data);
        }

        public void RegenerateMeshes(bool immediate = false)
        {
            foreach (Chunk c in Chunks.Values)
            {
                if (c)
                {
                    c.GenerateMesh(immediate);
                }
            }
        }
    }
}