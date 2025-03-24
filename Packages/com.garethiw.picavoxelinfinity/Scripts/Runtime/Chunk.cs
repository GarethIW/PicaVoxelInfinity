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
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using System.Collections;
using System.Threading;
using JetBrains.Annotations;
using UnityEngine.UI;

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

        private int _lodLevel =0;

        public int LodLevel
        {
            get
            {
                return _lodLevel;
            } 
            set
            {
                _lodLevel = value;
                _isDataDirty = true;
                _isMeshDirty = true;
            }
        }

        private bool _isDataDirty = false;
        private bool _isMeshDirty = false;
        private ChunkStatus status = ChunkStatus.NoChange;

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
        private bool _disableMrNextFrame= false;
        /////////////////////////////////////////////////////////////////
        
        public void Initialize((int, int, int) pos, int lodLevel, Volume parentVolume)
        {
            _volume = parentVolume;
            if (mf is null) mf = GetComponent<MeshFilter>();
            if (mr is null) mr = GetComponent<MeshRenderer>();
            if (mc is null) mc = GetComponent<MeshCollider>();
            mr.enabled = false;
            Position = pos;
            transform.localPosition = new Vector3((Position.x*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.y*Volume.ChunkSize)-(Volume.ChunkSize*0.5f), (Position.z*Volume.ChunkSize)-(Volume.ChunkSize*0.5f)) * Volume.VoxelSize;
            if(Voxels==null || Voxels.Length==0)
                Voxels = new Voxel[Volume.ChunkSize * Volume.ChunkSize * Volume.ChunkSize];
            if (mf.sharedMesh is null)
                mf.sharedMesh = new Mesh();
            _isDataDirty = true;
            _lodLevel = lodLevel;
            #if UNITY_EDITOR
            gameObject.name = $"Chunk {Position.x},{Position.y},{Position.z}";
            #endif
        }

        private bool _hasData = false;
        private void GenerateData()
        {
            if (!Volume.IsDataReady)
            {
                _isDataDirty = true;
                return;
            }

            _hasData = false;
            Voxel v = new Voxel();
            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is doing data gen");
            int lodStep = _lodLevel+1;
            if (lodStep == 3) lodStep = 4;
            
            for (int z = 0; z < Volume.ChunkSize; z+=lodStep)
                for (int y = 0; y < Volume.ChunkSize; y+=lodStep)
                    for (int x = 0; x < Volume.ChunkSize; x+=lodStep)
                    {
                        if (_lodLevel == 0)
                        {
                            Volume.GenerateVoxel(x + (Volume.ChunkSize * Position.x), y + (Volume.ChunkSize * Position.y), z + (Volume.ChunkSize * Position.z), ref Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)]);
                        }
                        else
                        {
                            Volume.GenerateVoxel(x + (Volume.ChunkSize * Position.x), y + (Volume.ChunkSize * Position.y), z + (Volume.ChunkSize * Position.z), ref v);
                            for (int zz = z; zz < z+lodStep; zz++)
                                for (int yy = y; yy < y+lodStep; yy++)
                                    for (int xx = x; xx < x + lodStep; xx++)
                                    {
                                        if(xx>=Volume.ChunkSize)
                                            Debug.LogError($"oob: {xx} {x} {lodStep}");
                                        Voxels[xx + Volume.ChunkSize * (yy + Volume.ChunkSize * zz)] = v;
                                    }
                        }
                        
                        if(!_hasData && Voxels[x + Volume.ChunkSize * (y + Volume.ChunkSize * z)].Active)
                            _hasData = true;
                    }
            //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} is finished data gen");

            if (!_hasData)
                _disableMrNextFrame = true;
        }

        public Voxel? GetVoxel((int x, int y, int z) pos)
        {
            int i = pos.x + Volume.ChunkSize * (pos.y + Volume.ChunkSize * pos.z);
            if(i<0 || i>=Voxels.Length) return null;

            return Voxels[i];
        }
        
        public void CheckGeneration()
        {
            if (_isDataDirty)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty data");

                _isDataDirty = false;

                if (!ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            GenerateData();
                            // The volume will set the mesh to be regenerated once all chunks have queued data load
                            // if(!_hasData)
                            //     return;
                            // Thread.Sleep(25);
                            // GenerateMeshThreaded(Volume.MeshingMode);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    })
                   )
                    _isDataDirty = true;
            }
            
            if (_disableMrNextFrame)
            {
                mr.enabled = false;
                mc.enabled = false;
                _disableMrNextFrame= false;
            }
            
            if (status == ChunkStatus.NoChange && _isMeshDirty && !_isDataDirty)
            {
                //Debug.Log($"Chunk {Position.x},{Position.y},{Position.z} has dirty mesh");
                _isMeshDirty = false;
                GenerateMesh(false);
            }
            
            if (status == ChunkStatus.Ready)
            {
                status = ChunkStatus.NoChange;
                if (!_isMeshDirty)
                {
                    SetMesh();
                    mr.enabled = true;
                }
                else
                {
                    _isMeshDirty = false;
                    GenerateMesh(false);
                }
            }
            
            if (updateColliderNextFrame)
            {
                UpdateCollider();
            }
        }
        
        public void GenerateMesh(bool immediate)
        {
            if (!_hasData)
                return;
            
            if (immediate)
            {
                GenerateMeshActual(Volume.MeshingMode);
                SetMesh();

                if (Volume.CollisionMode != CollisionMode.None)
                {
                    if(Volume.MeshColliderMeshingMode!=Volume.MeshingMode)
                        GenerateMeshActual(Volume.MeshColliderMeshingMode);
                    SetColliderMesh();
                }
            }
            else
            {
                if (status != ChunkStatus.NoChange)
                {
                    _isMeshDirty = true;
                }
    
                if(!ThreadPool.QueueUserWorkItem(delegate
                    {
                        GenerateMeshThreaded(Volume.MeshingMode);
                    })
                ) 
                    GenerateMeshActual(Volume.MeshingMode);

            }
        }
        
        private void GenerateMeshThreaded(MeshingMode meshMode)
        {
            
            status = ChunkStatus.CalculatingMesh;

            GenerateMeshActual(meshMode);
         
            status = ChunkStatus.Ready;
        }

        private Chunk[] _neighbours = new Chunk[27];
        private void GenerateMeshActual(MeshingMode meshMode)
        {
            for(int x=0;x<3;x++)
                for(int y=0;y<3;y++)
                    for(int z=0;z<3;z++)
                        _neighbours[x + 3 * (y + 3 * z)] = Volume.GetChunk((Position.x+(x-1),Position.y+(y-1),Position.z+(z-1)));
            
            switch (meshMode)
            {
                case MeshingMode.Culled:
                    MeshGenerator.GenerateCulled(vertices, uvs, colors, indexes, ref Voxels, this, _neighbours, Volume.VoxelSize, _lodLevel, 0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Greedy:
                    MeshGenerator.GenerateGreedy(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, _lodLevel,0f,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
                case MeshingMode.Marching:
                    MeshGenerator.GenerateMarching(vertices, uvs, colors, indexes, ref Voxels, this,_neighbours,Volume.VoxelSize, _lodLevel,0, 0, 0, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.ChunkSize-1, Volume.SelfShadingIntensity);
                    break;
            }    
        }

        private void SetMesh()
        {
            if (vertices.Count == 0)
            {
                return;
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
        }

        private void UpdateCollider()
        {
            mf.sharedMesh.RecalculateBounds();
            mc.sharedMesh = mf.sharedMesh;
            mc.enabled = true;
            updateColliderNextFrame = false;
        }

        private void SetColliderMesh()
        {
            if (vertices.Count == 0)
            {
                if (mc.sharedMesh != null)
                {
                    mc.sharedMesh.Clear();
                    mc.sharedMesh = null;
                }
                return;
            }

            if (mc.sharedMesh == null || (Application.isPlaying && !hasCreatedRuntimeColMesh))
            {
                mc.sharedMesh = new Mesh();
                if (Application.isPlaying) hasCreatedRuntimeColMesh = true;
            }
            mc.sharedMesh.Clear();
            mc.sharedMesh.SetVertices(vertices);
            mc.sharedMesh.SetColors(colors);
            mc.sharedMesh.SetUVs(0, uvs);
            mc.sharedMesh.SetTriangles(indexes, 0);

            mc.sharedMesh.RecalculateNormals();
            mc.sharedMesh.RecalculateBounds();
        }

        public void SetMeshDirty()
        {
            if (!_hasData)
                return;
            _isMeshDirty = true;
        }
    }
}
