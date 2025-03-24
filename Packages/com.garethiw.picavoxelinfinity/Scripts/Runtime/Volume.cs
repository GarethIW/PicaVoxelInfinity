/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
// 
// Source code distributed under standard Asset Store licence:
// http://unity3d.com/legal/as_terms
//
/////////////////////////////////////////////////////////////////////////
using System;
using System.IO;
using System.Threading;
using UnityEngine;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;
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
    
#if UNITY_EDITOR
    public enum Importer
    {
        None,
        Magica,
        Image
    }
#endif

    /// <summary>
    /// The main parent script for a PicaVoxel volume
    /// </summary>
    [AddComponentMenu("PicaVoxel/PicaVoxel Volume")]
    [Serializable]
    [SelectionBase]
    public class Volume : MonoBehaviour
    {
        public GameObject ChunkPrefab;

        public int ChunkSize = 16;
        
        public float VoxelSize = 1f;

        public Vector3 Pivot = Vector3.zero;
        
        public Dictionary<(int,int,int), Chunk> Chunks = new();

        // Infinite mode will be for infinite terrain, which will keep generating as you move
        // Non-infinite will only add chunks when edited, and all chunks will be visible/rendered at startup
        public bool IsInfinite = false;
        public Vector3Int InfiniteChunkRadiusLODs = new Vector3Int(8,12,16);
        public Vector3Int InfiniteChunkBounds = Vector3Int.zero;
        public float InfiniteUpdateInterval = 0.25f;
        public int InfiniteUpdateSlicing = 8;
        public int InfiniteChunkRadiusUpdateMargin = 3;
        
        public int GenerationSeed;

        public bool IsDataReady => _voxelDataGenerator.IsReady;
        
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

#if UNITY_EDITOR
        public Importer ImportedFrom;
        public string ImportedFile;
        public int ImportedMode;
        public Color ImportedCutoutColor;
#endif

        private Transform _cameraTransform;
        private float _infiniteUpdateTimer;
        private I_VoxelDataGenerator _voxelDataGenerator;
        private bool _isFirstPass;
        private Vector3 _lastCamOffset;

        
        private void Awake()
        {
            //destructBatch = new Batch(this, XSize*YSize*ZSize);
        }

        private void Start()
        {
            _voxelDataGenerator = GetComponent<I_VoxelDataGenerator>();
            if (_voxelDataGenerator == null)
            {
                _voxelDataGenerator = gameObject.AddComponent<SolidGenerator>();
            }
            _voxelDataGenerator.Seed = GenerationSeed;

            _cameraTransform = Camera.main.transform;
            _lastCamOffset = _cameraTransform.position- (transform.position-(ChunkSize * VoxelSize * 0.5f * Vector3.one));

            if (!IsInfinite)
            {
                for (int z = -(InfiniteChunkBounds.z - 1); z <= InfiniteChunkBounds.z - 1; z++)
                    for (int y = -(InfiniteChunkBounds.y - 1); y <= InfiniteChunkBounds.y - 1; y++)
                        for (int x = -(InfiniteChunkBounds.x - 1); x <= InfiniteChunkBounds.x - 1; x++)
                        {
                            if (!Chunks.ContainsKey((x, y, z)))
                                Chunks[(x, y, z)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                            Chunks[(x, y, z)].Initialize((x, y, z), 0,this);
                        }
            }
            else _isFirstPass = true;
        }

        Queue<Chunk> _freeChunks = new();
        private int slice = 0;
        private bool dataPass = true;
        Vector3Int thisUpdateccpos = Vector3Int.zero;
        private void Update()
        {
            foreach (Chunk chunk in Chunks.Values)
            {
                chunk.CheckGeneration();
            }

            if (!IsInfinite)
                return;
            
            _infiniteUpdateTimer += Time.deltaTime;
            if (_infiniteUpdateTimer >= InfiniteUpdateInterval)
            {
                _infiniteUpdateTimer = 0;

                float radunit = ChunkSize * VoxelSize;
                Vector3 halfchunk = radunit * 0.5f * Vector3.one;
                Vector3 camoffset = _cameraTransform.position- (transform.position-halfchunk);
                Vector3 camChunkPos = camoffset / radunit;
                Vector3Int ccpos = new Vector3Int(Mathf.RoundToInt(camChunkPos.x), Mathf.RoundToInt(camChunkPos.y), Mathf.RoundToInt(camChunkPos.z));
                int[] lodrads = new[] { InfiniteChunkRadiusLODs.x, InfiniteChunkRadiusLODs.y, InfiniteChunkRadiusLODs.z };
                
                //Debug.Log(ccpos);
                //float rad2 = ((InfiniteChunkRadius*radunit)*(InfiniteChunkRadius*radunit)) + ((radunit * 0.5f) * (radunit * 0.5f));

                // If the camera moves more than a chunk in one go, regenerate the whole lot starting with the chunk at camera position
                if ((camoffset - _lastCamOffset).magnitude > radunit)
                {
                    foreach (Chunk chunk in Chunks.Values)
                    {
                        if (!_freeChunks.Contains(chunk))
                            _freeChunks.Enqueue(chunk);
                    }
                    foreach (Chunk chunk in _freeChunks)
                        Chunks.Remove(chunk.Position);
                    _isFirstPass = true;
                    dataPass = true;
                    slice = 1;
                    thisUpdateccpos=ccpos;
                    if (_freeChunks.TryDequeue(out Chunk reuse))
                    {
                        Chunks.Add((ccpos.x,ccpos.y,ccpos.z), reuse);
                        reuse.Initialize((ccpos.x,ccpos.y,ccpos.z), 2, this);
                    }
                }

                _lastCamOffset = camoffset;
                
                if (slice == 0)
                {
                    thisUpdateccpos=ccpos;
                    foreach (Chunk chunk in Chunks.Values)
                    {
                        Vector3Int cpos = new Vector3Int(chunk.Position.x, chunk.Position.y, chunk.Position.z);

                        if ((ccpos - cpos).sqrMagnitude > lodrads[2] * lodrads[2])
                        {
                            if (!_freeChunks.Contains(chunk))
                                _freeChunks.Enqueue(chunk);
                            //chunk.CanBeReused = true;
                        }
                    }
                    foreach (Chunk chunk in _freeChunks)
                        Chunks.Remove(chunk.Position);

                    slice++;
                    return;
                }
                
                int s = 1;
                for (int z = thisUpdateccpos.z-lodrads[2]; z < thisUpdateccpos.z +lodrads[2]; z++)
                    for (int y = thisUpdateccpos.y-lodrads[2]; y < thisUpdateccpos.y +lodrads[2]; y++)
                        for (int x = thisUpdateccpos.x - lodrads[2]; x < thisUpdateccpos.x + lodrads[2]; x++)
                        {
                            if((InfiniteChunkBounds.x>0 && ( x<-InfiniteChunkBounds.x || x>InfiniteChunkBounds.x)) || (InfiniteChunkBounds.y>0 && (y<-InfiniteChunkBounds.y || y>InfiniteChunkBounds.y)) || (InfiniteChunkBounds.z>0 && (z<-InfiniteChunkBounds.z || z>InfiniteChunkBounds.z)))
                                continue;

                            Vector3 cpos = new Vector3(x, y, z);
                            if ((thisUpdateccpos - cpos).sqrMagnitude > lodrads[2]*lodrads[2])
                                continue;
                            
                            if (!_isFirstPass)
                            {
                                s++;
                                if (s == InfiniteUpdateSlicing)
                                    s = 1;
                                if (s != slice)
                                    continue;
                                if ((thisUpdateccpos - cpos).sqrMagnitude < (lodrads[0] - InfiniteChunkRadiusUpdateMargin) * (lodrads[0] - InfiniteChunkRadiusUpdateMargin))
                                    continue;
                            }
                            
                            if (Chunks.TryGetValue((x, y, z), out Chunk chunk))
                            {
                                int origlod = chunk.LodLevel;
                                if ((thisUpdateccpos - cpos).sqrMagnitude <= (lodrads[0] * lodrads[0]) && chunk.LodLevel > 0)
                                {
                                    chunk.LodLevel = 0;
                                }
                                if ((thisUpdateccpos - cpos).sqrMagnitude > (lodrads[0] * lodrads[0]) && chunk.LodLevel != 1)
                                {
                                    chunk.LodLevel = 1;
                                }
                                if ((thisUpdateccpos - cpos).sqrMagnitude > (lodrads[1] * lodrads[1]) && chunk.LodLevel != 2)
                                {
                                    chunk.LodLevel = 2;
                                }
                                
                                if (chunk.LodLevel==origlod && !dataPass)
                                {
                                    Chunks[(x, y, z)].SetMeshDirty();
                                }
                                continue;
                            }

                            if (_freeChunks.TryDequeue(out Chunk reuse))
                            {
                                Chunks.Add((x, y, z), reuse);
                                reuse.Initialize((x, y, z), 2, this);
                            }
                            else
                            {
                                //Debug.Log($"No available chunks to re-use at {x}, {y}, {z}");
                                Chunks[(x, y, z)] = Instantiate(ChunkPrefab, transform, false).GetComponent<Chunk>();
                                Chunks[(x, y, z)].Initialize((x, y, z), 2, this);
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
                    slice = dataPass ? 1 : 0;

                    dataPass = !dataPass;
                }
            }
        }

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            _voxelDataGenerator.GenerateVoxel(x, y, z, ref voxel);
        }

        [CanBeNull]
        public Chunk GetChunk((int x, int y, int z) pos)
        {
            if (!Chunks.ContainsKey(pos))
                return null;
            
            return Chunks[pos];
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
        
        /// <summary>
        /// Returns a voxel contained in this volume, at a given world position
        /// </summary>
        /// <param name="pos">The world position in the scene</param>
        /// <returns>A voxel if position is within this volume, otherwise null</returns>
        // public Voxel? GetVoxelAtWorldPosition(Vector3 pos)
        // {
        //     return Frames[CurrentFrame].GetVoxelAtWorldPosition(pos);
        // }

        /// <summary>
        /// Returns a voxel contained in this volume's current frame,, at a given array position
        /// </summary>
        /// <param name="x">X array position</param>
        /// <param name="y">Y array position</param>
        /// <param name="z">Z array position</param>
        /// <returns>A voxel if position is within the array, otherwise null</returns>
        // public Voxel? GetVoxelAtArrayPosition(int x, int y, int z)
        // {
        //     return Frames[CurrentFrame].GetVoxelAtArrayPosition(x, y, z);
        // }

        /// <summary>
        /// Attempts to set a voxel within this volume's current frame, at a given world position, to the supplied voxel value
        /// </summary>
        /// <param name="pos">The world position in the scene</param>
        /// <param name="vox">The new voxel to set to</param>
        /// <returns>The array position of the voxel</returns>
        // public Vector3 SetVoxelAtWorldPosition(Vector3 pos, Voxel vox)
        // {
        //     return Frames[CurrentFrame].SetVoxelAtWorldPosition(pos, vox);
        // }

        /// <summary>
        /// Attempts to set a voxel's state within this volume's current frame, at a given world position, to the supplied value
        /// </summary>
        /// <param name="pos">The world position in the scene</param>
        /// <param name="state">The new voxel state to set to</param>
        /// <returns>The array position of the voxel</returns>
        // public Vector3 SetVoxelStateAtWorldPosition(Vector3 pos, VoxelState state)
        // {
        //     return Frames[CurrentFrame].SetVoxelStateAtWorldPosition(pos, state);
        // }

        /// <summary>
        /// Attempts to set a voxel within this volume's current frame, at a specified array position
        /// </summary>
        /// <param name="pos">A PicaVoxelPoint location within the 3D array of voxels</param>
        /// <param name="vox">The new voxel to set to</param>
        // public void SetVoxelAtArrayPosition(PicaVoxelPoint pos, Voxel vox)
        // {
        //     Frames[CurrentFrame].SetVoxelAtArrayPosition(pos, vox);
        // }

        /// <summary>
        /// Attempts to set a voxel's state within this volume's current frame, at a specified array position
        /// </summary>
        /// <param name="pos">A PicaVoxelPoint location within the 3D array of voxels</param>
        /// <param name="state">The new state to set to</param>
        // public void SetVoxelStateAtArrayPosition(PicaVoxelPoint pos, VoxelState state)
        // {
        //     Frames[CurrentFrame].SetVoxelStateAtArrayPosition(pos, state);
        // }

        /// <summary>
        /// Attempts to set a voxel within this volume's current frame, at a specified x,y,z array position
        /// </summary>
        /// <param name="x">X array position</param>
        /// <param name="y">Y array position</param>
        /// <param name="z">Z array position</param>
        /// <param name="vox">The new voxel to set to</param>
        // public void SetVoxelAtArrayPosition(int x, int y, int z, Voxel vox)
        // {
        //     Frames[CurrentFrame].SetVoxelAtArrayPosition(x, y, z, vox);
        // }

        /// <summary>
        /// Attempts to set a voxel's state within this volume's current frame, at a specified x,y,z array position
        /// </summary>
        /// <param name="x">X array position</param>
        /// <param name="y">Y array position</param>
        /// <param name="z">Z array position</param>
        /// <param name="state">The new state to set to</param>
        // public void SetVoxelStateAtArrayPosition(int x, int y, int z, VoxelState state)
        // {
        //     Frames[CurrentFrame].SetVoxelStateAtArrayPosition(x, y, z, state);
        // }

        /// <summary>
        /// Returns the local position of a voxel within this volume's current frame, at a specified world position
        /// </summary>
        /// <param name="pos">The world position in the scene</param>
        /// <returns>The local voxel position</returns>
        // public Vector3 GetVoxelPosition(Vector3 pos)
        // {
        //     return Frames[CurrentFrame].GetVoxelPosition(pos);
        // }

        /// <summary>
        /// Returns the array position of a voxel within this volume's current frame, at a specified world position
        /// </summary>
        /// <param name="pos">The world position in the scene</param>
        /// <returns>The array position of the voxel</returns>
        // public PicaVoxelPoint GetVoxelArrayPosition(Vector3 pos)
        // {
        //     return Frames[CurrentFrame].GetVoxelArrayPosition(pos);
        // }

        /// <summary>
        /// Returns the world position of a voxel given its array positions
        /// </summary>
        /// <param name="x">The X position of the voxel in the array</param>
        /// <param name="y">The Y position of the voxel in the array</param>
        /// <param name="z">The Z position of the voxel in the array</param>
        /// <returns>The world position of the center of the voxel</returns>
        // public Vector3 GetVoxelWorldPosition(int x, int y, int z)
        // {
        //     return Frames[CurrentFrame].GetVoxelWorldPosition(x,y,z);
        // }
        
        /// <summary>
        /// Update the pivot position. Use this after setting Pivot.
        /// </summary>
        public void UpdatePivot()
        {
            foreach (Chunk chunk in Chunks.Values)
            {
                if (chunk != null)
                    chunk.transform.localPosition = -Pivot;
            }
        }

        /// <summary>
        /// Generates a first chunk, filled according to fillmode
        /// </summary>
        // public void GenerateBasic(FillMode fillMode)
        // {
        //     Frames[0].GenerateBasic(fillMode);
        //     UpdateBoxCollider();
        // }

        /// <summary>
        /// Update only the chunks which have changed voxels on the current frame
        /// </summary>
        /// <param name="immediate">If true, don't use threading to perform this update</param>
        // public void UpdateChunks(bool immediate)
        // {
        //     //Debug.Log("Object UpdateChunks");
        //     Frames[CurrentFrame].UpdateChunks(immediate);
        // }
        //
        // /// <summary>
        // /// Immediately update all chunks on the current frame
        // /// </summary>
        // public void UpdateAllChunks()
        // {
        //     Frames[CurrentFrame].UpdateAllChunks();
        // }
        //
        // /// <summary>
        // /// Updates all chunks on the current animation frame next game frame (threaded)
        // /// </summary>
        // public void UpdateAllChunksNextFrame()
        // {
        //     Frames[CurrentFrame].UpdateAllChunksNextFrame();
        // }

        /// <summary>
        /// Re-create all chunks on all animation frames
        /// </summary>
        // public void CreateChunks()
        // {
        //     foreach (Frame frame in Frames) 
        //         if(frame!=null) frame.CreateChunks();
        //
        //     UpdateBoxCollider();
        // }

//         public void SaveChunkMeshes(bool forceNew)
//         {
// #if UNITY_EDITOR
//             if (RuntimeOnlyMesh) return;
//
//             if (string.IsNullOrEmpty(AssetGuid) || forceNew) AssetGuid = Guid.NewGuid().ToString();
//
//             string path = Path.Combine(Helper.GetMeshStorePath(), AssetGuid.ToString());
//             if (!Directory.Exists(path)) Directory.CreateDirectory(path);
//
// #if !UNITY_WEBPLAYER
//             DirectoryInfo di = new DirectoryInfo(path);
//             foreach (FileInfo f in di.GetFiles())
//                 f.Delete();
// #endif
//
//             foreach (Frame frame in Frames)
//                 if (frame != null) frame.SaveChunkMeshes(true);
// #endif
//         }

        /// <summary>
        /// Deactivates all particles in the current frame, within a supplied radius of a world position
        /// </summary>
        /// <param name="position">The world position of the centre of the explosion</param>
        /// <param name="explosionRadius">The radius of the explosion</param>
        /// <returns>A Batch of voxels that were destroyed by the explosion</returns>
        // public Batch Explode(Vector3 position, float explosionRadius, int valueFilter, Exploder.ExplodeValueFilterOperation valueFilterOperation)
        // {
        //     Batch batch = new Batch(this);
        //
        //     Color tint = Material.GetColor("_Tint");
        //
        //     Matrix4x4 transformMatrix = transform.worldToLocalMatrix;
        //
        //     position += (transform.rotation * (Pivot));
        //     position = transformMatrix.MultiplyPoint3x4(position);
        //
        //     for (float x = position.x - explosionRadius; x <= position.x + explosionRadius; x += VoxelSize * 0.5f)
        //         for (float y = position.y - explosionRadius; y <= position.y + explosionRadius; y += VoxelSize * 0.5f)
        //             for (float z = position.z - explosionRadius; z <= position.z + explosionRadius; z += VoxelSize * 0.5f)
        //             {
        //                 Vector3 checkPos = new Vector3(x, y, z);
        //                 if ((checkPos-  position).magnitude <= explosionRadius)
        //                 {
        //                     //Vector3 localPos =; //transform.InverseTransformPoint(pos);
        //                     //if (!Frames[CurrentFrame].IsLocalPositionInBounds(localPos)) continue;
        //
        //                     int testX = (int)(checkPos.x / VoxelSize);
        //                     int testY = (int)(checkPos.y / VoxelSize);
        //                     int testZ = (int)(checkPos.z / VoxelSize);
        //                     if (testX < 0 || testY < 0 || testZ < 0 || testX >= XSize || testY >= YSize || testZ >= ZSize) continue;
        //
        //                     if (Frames[CurrentFrame].Voxels[testX + XSize * (testY + YSize * testZ)].Active &&
        //                     FilterExplosion(Frames[CurrentFrame].Voxels[testX + XSize * (testY + YSize * testZ)].Value, valueFilter, valueFilterOperation))
        //                     {
        //                         Voxel v = Frames[CurrentFrame].Voxels[testX + XSize * (testY + YSize * testZ)];
        //                         v.Color *= tint;
        //                         batch.Add(v, testX, testY, testZ, transform.localToWorldMatrix.MultiplyPoint3x4(checkPos - Pivot));// );
        //                         SetVoxelStateAtArrayPosition(testX, testY, testZ, VoxelState.Hidden);
        //                     }
        //                 }
        //             }
        //
        //
        //     return batch;
        // }

        /// <summary>
        /// Adds particles to the PicaVoxel Particle System (if available) representing the shape of this volume
        /// Use it before destroying/deactivating the volume to leave particles behind
        /// </summary>
        /// <param name="particleVelocity">Initial velocity of the created particles (outward from center of volume)</param>
        /// <param name="actuallyDestroyVoxels">If true, will set all the voxels to inactive</param>
        // public void Destruct(float particleVelocity, bool actuallyDestroyVoxels)
        // {
        //     Vector3 posZero = transform.position + (transform.rotation * (-Pivot + (Vector3.one * (VoxelSize * 0.5f))));
        //     Vector3 oneX = transform.rotation * (new Vector3(VoxelSize, 0, 0));
        //     Vector3 oneY = transform.rotation * (new Vector3(0f, VoxelSize, 0));
        //     Vector3 oneZ = transform.rotation * (new Vector3(0, 0, VoxelSize));
        //
        //     Vector3 partPos = posZero;
        //     Color matColor = Material.GetColor("_Tint");
        //     for (int x = 0; x < XSize; x++)
        //     {
        //         Vector3 xmult = oneX * x;
        //         for (int y = 0; y < YSize; y++)
        //         {
        //             Vector3 ymult = oneY * y;
        //             for (int z = 0; z < ZSize; z++)
        //             {
        //                 Vector3 zmult = oneZ * z;
        //                 partPos.x = posZero.x + xmult.x + ymult.x + zmult.x;
        //                 partPos.y = posZero.y + xmult.y + ymult.y + zmult.y;
        //                 partPos.z = posZero.z + xmult.z + ymult.z + zmult.z;
        //
        //                 if (Frames[CurrentFrame].Voxels[x + XSize * (y + YSize * z)].Active)
        //                 {
        //                     Voxel v = Frames[CurrentFrame].Voxels[x + XSize * (y + YSize * z)];
        //                     v.Color *= matColor;
        //                     destructBatch.Add(v, x, y, z, partPos);
        //                     v.State = VoxelState.Hidden;
        //                     if (actuallyDestroyVoxels) SetVoxelAtArrayPosition(x, y, z, v);
        //                 }
        //             }
        //         }
        //     }
        //
        //     if (destructBatch.Voxels.Count > 0 && VoxelParticleSystem.Instance != null)
        //         VoxelParticleSystem.Instance.SpawnBatch(destructBatch,
        //             pos => (pos - transform.position).normalized * Random.Range(0f, particleVelocity * 2f));
        //
        //     destructBatch.Clear();
        // }

        /// <summary>
        /// Restore all voxels that have been destroyed (Voxel.State = VoxelState.Hidden) on all frames
        /// </summary>
        // public void Rebuild()
        // {
        //     foreach (Frame frame in Frames)
        //         frame.Rebuild();
        //
        // }

        // private bool FilterExplosion(byte value, int valueFilter, Exploder.ExplodeValueFilterOperation valueFilterOperation)
        // {
        //     switch (valueFilterOperation)
        //     {
        //         case Exploder.ExplodeValueFilterOperation.LessThan:
        //             return value < valueFilter;
        //         case Exploder.ExplodeValueFilterOperation.LessThanOrEqualTo:
        //             return value <= valueFilter;
        //         case Exploder.ExplodeValueFilterOperation.EqualTo:
        //             return value == valueFilter;
        //         case Exploder.ExplodeValueFilterOperation.GreaterThanOrEqualTo:
        //             return value >= valueFilter;
        //         case Exploder.ExplodeValueFilterOperation.GreaterThan:
        //             return value > valueFilter;
        //     }
        //
        //     return false;
        // }

        /// <summary>
        /// Rotate the entire volume around the X axis
        /// </summary>
        // public void RotateX()
        // {
        //     int tempSize = YSize;
        //     YSize = ZSize;
        //     ZSize = tempSize;
        //     foreach (Frame frame in Frames)
        //     {
        //         Helper.RotateVoxelArrayX(ref frame.Voxels,new PicaVoxelPoint(frame.XSize, frame.YSize, frame.ZSize));
        //         frame.XSize = XSize;
        //         frame.YSize = YSize;
        //         frame.ZSize = ZSize;
        //     }
        //
        //     CreateChunks();
        //     SaveForSerialize();
        // }

        /// <summary>
        /// Rotate the entire volume around the Y axis
        /// </summary>
        // public void RotateY()
        // {
        //     int tempSize = XSize;
        //     XSize = ZSize;
        //     ZSize = tempSize;
        //     foreach (Frame frame in Frames)
        //     {
        //         Helper.RotateVoxelArrayY(ref frame.Voxels, new PicaVoxelPoint(frame.XSize, frame.YSize, frame.ZSize));
        //         frame.XSize = XSize;
        //         frame.YSize = YSize;
        //         frame.ZSize = ZSize;
        //     }
        //
        //     CreateChunks();
        //     SaveForSerialize();
        // }

        /// <summary>
        /// Rotate the entire volume around the Z axis
        /// </summary>
        // public void RotateZ()
        // {
        //     int tempSize = YSize;
        //     YSize = XSize;
        //     XSize = tempSize;
        //
        //     foreach (Frame frame in Frames)
        //     {
        //         Helper.RotateVoxelArrayZ(ref frame.Voxels, new PicaVoxelPoint(frame.XSize, frame.YSize, frame.ZSize));
        //         frame.XSize = XSize;
        //         frame.YSize = YSize;
        //         frame.ZSize = ZSize;
        //     }
        //    
        //     CreateChunks();
        //     SaveForSerialize();
        // }

        // public void ScrollX(int amount, bool allFrames)
        // {
        //     if (allFrames)
        //         foreach (Frame frame in Frames)
        //             frame.ScrollX(amount);
        //     else
        //         GetCurrentFrame().ScrollX(amount);
        // }

        // public void ScrollY(int amount, bool allFrames)
        // {
        //     if (allFrames)
        //         foreach (Frame frame in Frames)
        //             frame.ScrollY(amount);
        //     else
        //         GetCurrentFrame().ScrollY(amount);
        // }
        //
        // public void ScrollZ(int amount, bool allFrames)
        // {
        //     if (allFrames)
        //         foreach (Frame frame in Frames)
        //             frame.ScrollZ(amount);
        //     else
        //         GetCurrentFrame().ScrollZ(amount);
        // }

        /// <summary>
        /// Serialise all frames to byte array ready for Unity serialisation
        /// </summary>
        // public void SaveForSerialize()
        // {
        //     //Debug.Log("Object SaveForSerialize");
        //     foreach (Frame frame in Frames) frame.SaveForSerialize();
        // }
        //
        // /// <summary>
        // /// Change the mesh collision mode of all frames
        // /// </summary>
        // /// <param name="collisionMode">The CollisonMode to change to</param>
        // public void ChangeCollisionMode(CollisionMode collisionMode)
        // {
        //     CollisionMode = collisionMode;
        //     foreach (Frame frame in Frames) frame.GenerateMeshColliders();
        // }
       
    }
}