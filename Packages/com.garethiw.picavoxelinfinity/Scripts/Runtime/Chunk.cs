/////////////////////////////////////////////////////////////////////////
// 
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System.Threading;

namespace PicaVoxel
{
    public class Chunk : MonoBehaviour
    {
        public Voxel[] Voxels;
        
        public Volume Volume
        {
            get => _volume;
        }
        private Volume _volume;

        // Position of chunk in volume
        public (int x, int y, int z) Position;
        
        // Calculation stuff ////////////////////////////////////////////
        private enum ChunkStatus
        {
            NoChange,
            CalculatingMesh,
            Ready
        }

        // Is chunk free to be reused?
        public bool CanBeReused;
        
        private volatile bool _isDataDirty = false;
        private bool _isChangesDirty = false;
        private volatile bool _isMeshDirty = false;
        private volatile ChunkStatus status = ChunkStatus.NoChange;

        // Incremented every time this chunk is (re)initialized or released. Background data/mesh jobs capture the
        // value they were started with and throw their results away if the chunk has since been recycled.
        private volatile int _generation = 0;
        private readonly object _generationLock = new object();
        // True while a background data generation job is running for this chunk (of any generation)
        private volatile bool _isGeneratingData = false;
        // True once voxel data for the current generation has been fully generated
        private volatile bool _dataReady = false;
        // Set when this chunk's voxels change, so face-adjacent neighbours re-mesh their culled border faces
        private volatile bool _notifyNeighbours = false;
        // Generation the in-flight / last mesh job was started for, and whether it failed
        private int _meshJobGeneration = -1;
        private volatile bool _meshJobFailed = false;

        private float _persistTime = 0f;
        
        private Dictionary<(int x, int y, int z), Voxel> _changes = new();

        private List<Vector3> vertices = new List<Vector3>(2048);
        private List<Vector4> uvs = new List<Vector4>(2048);
        private List<Color32> colors = new List<Color32>(2048);
        private List<int> indexes = new List<int>(2048);

        private MeshFilter mf;
        private MeshCollider mc;
        private MeshRenderer mr;

        private bool hasCreatedRuntimeMesh = false;
        private bool hasCreatedRuntimeColMesh = false;
        private bool updateColliderNextFrame = false;
        private volatile bool _disableMrNextFrame= false;
        /////////////////////////////////////////////////////////////////
        
        public void Initialize((int, int, int) pos, Volume parentVolume)
        {
            _volume = parentVolume;
            if (mf is null) mf = GetComponent<MeshFilter>();
            if (mr is null) mr = GetComponent<MeshRenderer>();
            if (mc is null) mc = GetComponent<MeshCollider>();
            // Invalidate any background jobs still running for this chunk's previous position, and clear out
            // everything left over from it (mesh, collider, pending edits) before it is moved.
            lock (_generationLock)
            {
                _generation++;
            }
            ResetState();
            Position = pos;
            transform.localPosition = new Vector3((Position.x*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.y*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.z*Volume.ChunkSize)-(Volume.ChunkSize*0.5f)) * Volume.VoxelSize;
            gameObject.layer = Volume.ChunkLayer;
            if(Voxels==null || Voxels.Length==0)
                Voxels = new Voxel[Volume.ChunkSize * Volume.ChunkSize * Volume.ChunkSize];
            if (mf.sharedMesh is null)
                mf.sharedMesh = new Mesh();
            _isDataDirty = true;
            #if UNITY_EDITOR
            gameObject.name = $"Chunk {Position.x},{Position.y},{Position.z}";
            #endif
        }

        private volatile bool _hasData = false;

        /// <summary>
        /// Resets all per-position state so a chunk can be safely recycled. Main thread only.
        /// </summary>
        private void ResetState()
        {
            if (mr is not null) mr.enabled = false;
            if (mc is not null)
            {
                mc.enabled = false;
                mc.sharedMesh = null;
            }
            if (mf is not null && mf.sharedMesh is not null)
                mf.sharedMesh.Clear();

            _hasData = false;
            _dataReady = false;
            _isDataDirty = false;
            _isMeshDirty = false;
            _notifyNeighbours = false;
            _disableMrNextFrame = false;
            updateColliderNextFrame = false;
            _changes.Clear();
            _persistTime = 0f;
        }

        /// <summary>
        /// Called by the volume when this chunk goes out of range and is put in the pool for re-use.
        /// Hides it, removes its collider and invalidates any background work still running for it.
        /// </summary>
        public void Release()
        {
            // Don't lose edits that haven't been persisted yet
            if (_changes.Count > 0 && _persistTime > 0f)
                SaveChanges();

            lock (_generationLock)
            {
                _generation++;
            }
            ResetState();
        }

        private void GenerateData(int generation, (int x, int y, int z) pos)
        {
            try
            {
                if (!Volume.IsDataReady)
                {
                    _isDataDirty = true;
                    return;
                }

                bool hasData = false;
                //Debug.Log($"Chunk {pos.x},{pos.y},{pos.z} is doing data gen");
                for (int z = 0; z < Volume.ChunkSize; z++)
                {
                    // Chunk was recycled while we were generating; abandon this job
                    if (generation != _generation)
                        return;

                    for (int y = 0; y < Volume.ChunkSize; y++)
                        for (int x = 0; x < Volume.ChunkSize; x++)
                        {
                            Volume.GenerateVoxel(x + (Volume.ChunkSize * pos.x), y + (Volume.ChunkSize * pos.y), z + (Volume.ChunkSize * pos.z), ref Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)]);
                            if (!hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                                hasData = true;
                        }
                }

                lock (_generationLock)
                {
                    if (generation != _generation)
                        return;
                    _hasData = hasData;
                }

                foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
                {
                    if (p.Schedule == ProcessingSchedule.AfterGeneration)
                        p.ProcessChunk(_volume, this);
                }
                //Debug.Log($"Chunk {pos.x},{pos.y},{pos.z} is finished data gen");

                // May apply persisted edits synchronously (which can also change _hasData)
                _volume.LoadChunkChanges(pos);

                lock (_generationLock)
                {
                    if (generation != _generation)
                        return;

                    _dataReady = true;

                    // Always queue a mesh once data is ready (previously only in finite mode, which left infinite
                    // chunks without a mesh/collider if the volume's mesh pass reached them before data finished).
                    if (_hasData)
                        _isMeshDirty = true;
                    else
                        _disableMrNextFrame = true;

                    // Neighbours may have meshed against this chunk's stale/incomplete voxels, re-mesh their borders
                    _notifyNeighbours = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // Retry on the next update rather than leaving the chunk permanently empty
                if (generation == _generation)
                    _isDataDirty = true;
            }
            finally
            {
                _isGeneratingData = false;
            }
        }

        public Voxel? GetVoxel((int x, int y, int z) pos)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            return Voxels[i];
        }
        
        public Voxel? SetVoxel((int x, int y, int z) pos, Voxel newValue, bool persist = true)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            Voxels[i] = newValue;
            _changes[pos] = newValue;
            if (persist)
            {
                _persistTime = _volume.PersistenceInterval;
            }

            EvaluateData();
            GenerateMesh(true);
            
            for(int xx=0;xx<3;xx++)
                for(int yy=0;yy<3;yy++)
                    for (int zz = 0; zz < 3; zz++)
                    {
                        if (xx == Position.x && yy == Position.y && zz == Position.z)
                            continue;
                        
                        // Only re-mesh neighbouring chunk if edited voxel was along the adjacent edge
                        if((xx==0 && pos.x==0) ||
                           (xx==2 && pos.x==Volume.ChunkSize-1) ||
                           (zz==0 && pos.z==0) ||
                           (zz==2 && pos.z==Volume.ChunkSize-1) ||
                           (yy==0 && pos.y==0) ||
                           (yy==2 && pos.y==Volume.ChunkSize-1))
                            Volume.GetChunk((Position.x + (xx - 1), Position.y + (yy - 1), Position.z + (zz - 1)))?.SetMeshDirty();
                    }


            return Voxels[i];
        }
        
        public void SetVoxelByEditEvent(VoxelEditEventArgs e, bool persist = false)
        {
            Voxel v = new Voxel()
            {
                State = e.VoxelState,
                Value = e.VoxelValue,
                Color = e.VoxelColor
            };
            
            SetVoxel((e.VoxelX,e.VoxelY,e.VoxelZ), v, persist);
        }

        public void Persist()
        {
            if (_changes.Count == 0)
                return;

            if (_persistTime <= 0f)
                return;
            
            _persistTime -= Time.deltaTime;
            if (_persistTime <= 0f)
            {
                if (SaveChanges())
                {
                    _persistTime = 0;
                }
            }
        }
        
        public void CheckGeneration()
        {
            if (!_hasData && mr.enabled)
                mr.enabled = false;
            
            // Only one data job per chunk at a time. If the chunk was recycled while an old job is still running,
            // wait for it to bail out before starting the new one (both would write into the same Voxels array).
            if (_isDataDirty && !_isGeneratingData)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty data");

                _isDataDirty = false;
                _isGeneratingData = true;
                int generation = _generation;
                (int x, int y, int z) pos = Position;

                if (!ThreadPool.QueueUserWorkItem(delegate { GenerateData(generation, pos); }))
                {
                    _isGeneratingData = false;
                    _isDataDirty = true;
                }
            }

            if (_notifyNeighbours)
            {
                _notifyNeighbours = false;
                NotifyNeighbours();
            }
            
            if (status == ChunkStatus.Ready)
            {
                status = ChunkStatus.NoChange;
                // Discard results from a job started before this chunk was recycled, or one that failed
                if (_meshJobGeneration == _generation && !_meshJobFailed)
                {
                    // Apply what we have even if another re-mesh is pending; it'll be replaced shortly, and it's
                    // better than showing nothing (or a stale mesh) in the meantime.
                    SetMesh();
                }
            }

            if (status == ChunkStatus.NoChange && _isMeshDirty && _dataReady && !_isGeneratingData)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty mesh");
                _isMeshDirty = false;
                GenerateMesh(false);
            }
            
            if (_disableMrNextFrame)
            {
                mr.enabled = false;
                mc.enabled = false;
                _disableMrNextFrame= false;
            }
            
            if (updateColliderNextFrame)
            {
                UpdateCollider();
            }
        }

        /// <summary>
        /// Flags the 6 face-adjacent chunks for re-meshing, since their culled border faces depend on our voxels.
        /// Main thread only.
        /// </summary>
        private void NotifyNeighbours()
        {
            Volume.GetChunk((Position.x - 1, Position.y, Position.z))?.SetMeshDirty();
            Volume.GetChunk((Position.x + 1, Position.y, Position.z))?.SetMeshDirty();
            Volume.GetChunk((Position.x, Position.y - 1, Position.z))?.SetMeshDirty();
            Volume.GetChunk((Position.x, Position.y + 1, Position.z))?.SetMeshDirty();
            Volume.GetChunk((Position.x, Position.y, Position.z - 1))?.SetMeshDirty();
            Volume.GetChunk((Position.x, Position.y, Position.z + 1))?.SetMeshDirty();
        }

        /// <summary>
        /// Snapshot neighbour chunk references on the main thread, so mesh jobs never touch the Chunks dictionary
        /// from a worker thread while the volume is adding/removing chunks.
        /// </summary>
        private void GatherNeighbours()
        {
            for(int x=0;x<3;x++)
                for(int y=0;y<3;y++)
                    for(int z=0;z<3;z++)
                        _neighbours[x + 3 * (y + 3 * z)] = Volume.GetChunk((Position.x+(x-1),Position.y+(y-1),Position.z+(z-1)));
        }
        
        public void GenerateMesh(bool immediate)
        {
            // Can't mesh while a background job is using the shared vertex lists, or while voxel data is still
            // being generated (we'd mesh half-written data). Defer; CheckGeneration will pick it up.
            if (status != ChunkStatus.NoChange || _isGeneratingData || !_dataReady)
            {
                _isMeshDirty = true;
                return;
            }

            GatherNeighbours();

            if (immediate)
            {
                GenerateMeshActual(Volume.MeshingMode);
                mr.enabled = _hasData;
                if (_hasData)
                {
                    bool hasMesh = SetMesh();
                    if (hasMesh && Volume.CollisionMode != CollisionMode.None)
                    {
                        if(Volume.MeshColliderMeshingMode!=Volume.MeshingMode)
                            GenerateMeshActual(Volume.MeshColliderMeshingMode);
                        UpdateCollider();
                    }
                }
                else
                {
                    mc.enabled = false;
                }
            }
            else
            {
                // Mark busy *before* queueing so a second job can't be started for this chunk in the meantime
                status = ChunkStatus.CalculatingMesh;
                _meshJobGeneration = _generation;
                _meshJobFailed = false;
                MeshingMode meshMode = Volume.MeshingMode;

                if (!ThreadPool.QueueUserWorkItem(delegate { GenerateMeshThreaded(meshMode); }))
                {
                    status = ChunkStatus.NoChange;
                    _isMeshDirty = true;
                }
            }
        }
        
        private void GenerateMeshThreaded(MeshingMode meshMode)
        {
            try
            {
                GenerateMeshActual(meshMode);
            }
            catch (Exception ex)
            {
                // Previously an exception here left status stuck at CalculatingMesh, so the chunk never meshed again
                _meshJobFailed = true;
                Debug.LogException(ex);
            }
            finally
            {
                status = ChunkStatus.Ready;
            }
        }

        private Chunk[] _neighbours = new Chunk[27];
        private void GenerateMeshActual(MeshingMode meshMode)
        {
            foreach (I_ChunkProcessor p in _volume.ChunkProcessors)
            {
                if (p.Schedule == ProcessingSchedule.BeforeMeshing)
                    p.ProcessChunk(_volume, this);
            }
            
            switch (meshMode)
            {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref Voxels, this, _neighbours, Volume.CustomBlocksDict, Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, 0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
            }    
        }

        /// <summary>
        /// Applies the generated vertex lists to the chunk's mesh. Returns false if there was nothing to show.
        /// </summary>
        private bool SetMesh()
        {
            if (vertices.Count == 0)
            {
                // Don't leave a previous mesh/collider behind (e.g. last block removed, or fully enclosed chunk)
                mf.sharedMesh.Clear();
                mr.enabled = false;
                mc.enabled = false;
                updateColliderNextFrame = false;
                return false;
            }

            mr.shadowCastingMode = Volume.CastShadows;
            mr.receiveShadows = Volume.ReceiveShadows;
 			mr.gameObject.isStatic = Volume.gameObject.isStatic;
            mr.sharedMaterial = Volume.Material;
            
            mf.sharedMesh.Clear();
            mf.sharedMesh.SetVertices(vertices);
            mf.sharedMesh.SetColors(colors);
            mf.sharedMesh.SetUVs(0, uvs);
            mf.sharedMesh.SetTriangles(indexes, 0);
            mf.sharedMesh.RecalculateNormals();

            if (Volume.CollisionMode != CollisionMode.None)
            {
                updateColliderNextFrame = true;
            }

            mr.enabled = _hasData;
            return true;
        }

        private void UpdateCollider()
        {
            updateColliderNextFrame = false;
            if (!_hasData || mf.sharedMesh.vertexCount == 0)
            {
                mc.enabled = false;
                return;
            }

            mf.sharedMesh.RecalculateBounds();
            // Enable before assigning: a mesh assigned to a disabled MeshCollider is silently dropped, which left
            // recycled chunks (disabled while empty or pooled) visible but with nothing to stand on.
            mc.enabled = true;
            // Re-assigning the same Mesh instance doesn't reliably rebake the collider, so clear it first
            mc.sharedMesh = null;
            mc.sharedMesh = mf.sharedMesh;
        }

        public void SetMeshDirty()
        {
            if (!_hasData)
                return;
            _isMeshDirty = true;
        }

        private void EvaluateData()
        {
            _hasData = false;
            for (int z = 0; z < Volume.ChunkSize; z++)
                for (int y = 0; y < Volume.ChunkSize; y++)
                    for (int x = 0; x < Volume.ChunkSize; x++)
                    {
                        if (!_hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                        {
                            _hasData = true;
                            return;
                        }
                    }
        }
        
        public void LoadChanges(byte[] data)
        {
            int bytelength = (sizeof(int) * 3);
            byte[] buff = new byte[bytelength];
            byte[] vbuff = new byte[Voxel.BYTE_SIZE];
            
            using (MemoryStream ms = new MemoryStream(data))
            {
                using (BinaryReader br = new BinaryReader(ms))
                {
                    while (ms.Position < ms.Length)
                    {
                        int x = br.ReadInt32();
                        int y = br.ReadInt32();
                        int z = br.ReadInt32();
                            
                        int l = ms.Read(vbuff, 0, Voxel.BYTE_SIZE);
                        if (l == Voxel.BYTE_SIZE)
                        {
                            Voxel v = new Voxel(vbuff);
                            Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)] = v;
                            _changes[(x, y, z)] = v;
                        }
                        
                    }
                }
            }
            
            EvaluateData();
            _isMeshDirty = true;
            // Loaded edits may touch our borders
            _notifyNeighbours = true;
        }
        
        private bool SaveChanges()
        {
            if (!_volume.IsPersisterReady)
                return false;

            byte[] outbytes;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    foreach(KeyValuePair<(int x, int y, int z), Voxel> change in _changes)
                    {
                        bw.Write(change.Key.x);                        
                        bw.Write(change.Key.y);                        
                        bw.Write(change.Key.z);
                        bw.Write(change.Value.ToBytes());
                    }
                    bw.Flush();
                }

                outbytes = ms.ToArray();
            }

            return _volume.SaveChunkChanges(Position, outbytes);
        }
    }
}
