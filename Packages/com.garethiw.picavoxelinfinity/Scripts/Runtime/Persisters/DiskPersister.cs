using System;
using System.Net;
using System.IO;
using UnityEngine;

namespace PicaVoxel
{
    public class DiskPersister : MonoBehaviour, I_VoxelDataPersister
    {
        public string WorldName;
        
        private bool _isReady = true;

        public bool IsReady => _isReady;

        private string _basePath;
        
        private void OnEnable()
        {
            _basePath = Path.Combine(Application.persistentDataPath, "PicaVoxel", $"{WorldName}");
            if(!Directory.Exists(_basePath))
                Directory.CreateDirectory(_basePath);
        }

        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data)
        {
            Debug.Log($"DiskPersister SaveChunk ({WorldName}_{vol.Identifier}_{x}_{y}_{z}) byte length: {data.Length}");
            
            string fn = Path.Combine(_basePath, $"{vol.Identifier}_{x}_{y}_{z}.chunk");

            byte[] payload = new byte[12 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(z), 0, payload, 8, 4);
            Buffer.BlockCopy(data, 0, payload, 12, data.Length);

            try
            {
                File.WriteAllBytes(fn, payload);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }

            return true;
        }

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            Debug.Log($"DiskPersister LoadChunk ({WorldName}_{vol.Identifier}_{x}_{y}_{z})");

            string fn = Path.Combine(_basePath, $"{vol.Identifier}_{x}_{y}_{z}.chunk");

            if (!File.Exists(fn))
                return false;
            
            byte[] data;
            try
            {
                data = File.ReadAllBytes(fn);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }

            int recordSize = 12 + Voxel.BYTE_SIZE;
            int cx, cy, cz;
            byte[] strippedData;

            if (data.Length % recordSize == 0)
            {
                // Old format: coordinates are the expected x, y, z
                cx = x;
                cy = y;
                cz = z;
                strippedData = data;

                // Auto-migrate: save the new format back to disk
                SaveChunk(vol, x, y, z, data);
            }
            else if (data.Length >= 12 && (data.Length - 12) % recordSize == 0)
            {
                // New format: read coordinates from header
                cx = BitConverter.ToInt32(data, 0);
                cy = BitConverter.ToInt32(data, 4);
                cz = BitConverter.ToInt32(data, 8);

                if (cx != x || cy != y || cz != z)
                {
                    Debug.LogError($"DiskPersister LoadChunk coordinate mismatch! Expected ({x},{y},{z}) but got ({cx},{cy},{cz}) in file {fn}");
                    return false;
                }

                strippedData = new byte[data.Length - 12];
                Buffer.BlockCopy(data, 12, strippedData, 0, data.Length - 12);
            }
            else
            {
                Debug.LogError($"DiskPersister LoadChunk invalid file size: {data.Length} bytes in file {fn}");
                return false;
            }

            Chunk c = vol.GetChunk((x, y, z));
            if (!c)
                return false;
            
            c.LoadChanges(strippedData);

            return true;
        }
    }
}