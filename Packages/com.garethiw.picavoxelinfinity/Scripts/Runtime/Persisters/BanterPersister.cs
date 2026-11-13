using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    public class BanterPersister : MonoBehaviour, I_VoxelDataPersister
    {
        private bool _isReady = true;

        public bool IsReady => _isReady;
        
        private HttpClient _httpClient;

        public string SpaceName;
        
        public string BaseURL;
        private string _baseUrl;

        public int BatchGetSize = 100;
        public float BatchGetInterval = 0.1f;
        
        private float _batchInterval;
        private ConcurrentQueue<string> _chunksToFetch = new ConcurrentQueue<string>();

        private Volume _volume;
        
        private void OnEnable()
        {
            _baseUrl = BaseURL.TrimEnd('/');
            _httpClient = new HttpClient();
            _volume = GetComponent<Volume>();
        }

        public bool SaveChunk(Volume vol, int x, int y, int z, byte[] data)
        {
            string key = $"{SpaceName}_{vol.Identifier.Replace("{", "").Replace("}", "")}_{x}_{y}_{z}";

            byte[] payload = new byte[12 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(z), 0, payload, 8, 4);
            Buffer.BlockCopy(data, 0, payload, 12, data.Length);

            _ = Task.Run(()=>PostDataAsync(key, payload));

            return true;
        }

        public bool LoadChunk(Volume vol, int x, int y, int z)
        {
            string key = $"{SpaceName}_{vol.Identifier.Replace("{", "").Replace("}", "")}_{x}_{y}_{z}";
            _chunksToFetch.Enqueue(key);
            
            return true;
        }

        private bool _ready = true;
        private void Update()
        {
            _batchInterval += Time.deltaTime;
            if (_batchInterval >= BatchGetInterval && _ready)
            {
                _batchInterval = 0;
                if (_chunksToFetch.Count > 0)
                {
                    StringBuilder batch = new StringBuilder();
                    for (int i = 0; i < BatchGetSize; i++)
                    {
                        if(_chunksToFetch.TryDequeue(out string key))
                        {
                            batch.Append(key);
                            batch.Append(",");
                        }
                    }
                    string keys = batch.ToString().TrimEnd(',');
                    
                    if (keys == string.Empty)
                        return;

                    _ready = false;
                    
                    _ = Task.Run(()=>GetDataAsync(keys)).ContinueWith((Task<byte[]> task) =>
                    {
                        if (task.IsFaulted || !task.IsCompletedSuccessfully)
                        {
                            var retry = keys.Split(',');
                            foreach (string s in retry)
                            {
                                _chunksToFetch.Enqueue(s);
                            }

                            return;
                        }
                        
                        if (task.Result.Length == 0)
                        {
                            _ready = true;
                            return;
                        }

                        try
                        {
                            string[] keysplit = keys.Split(',');
                            int recordSize = 12 + Voxel.BYTE_SIZE;

                            using (MemoryStream stream = new MemoryStream(task.Result))
                            {
                                using (BinaryReader reader = new BinaryReader(stream))
                                {
                                    int n = 0;
                                    while (reader.BaseStream.Position < reader.BaseStream.Length)
                                    {
                                        int l = reader.ReadInt32();
                                        if (l == 0)
                                        {
                                            n++;
                                            continue;
                                        }
                                        
                                        byte[] data = reader.ReadBytes(l);

                                        int cx = 0;
                                        int cy = 0;
                                        int cz = 0;
                                        byte[] strippedData = null;

                                        if (l % recordSize == 0)
                                        {
                                            // Old format: parse coordinates from the end of the key
                                            string key = keysplit[n];
                                            string[] parts = key.Split('_');
                                            cx = int.Parse(parts[parts.Length - 3]);
                                            cy = int.Parse(parts[parts.Length - 2]);
                                            cz = int.Parse(parts[parts.Length - 1]);
                                            strippedData = data;

                                            // Auto-migrate: save the new format back to the server
                                            SaveChunk(_volume, cx, cy, cz, data);
                                        }
                                        else if (l >= 12 && (l - 12) % recordSize == 0)
                                        {
                                            // New format: parse coordinates from header
                                            cx = BitConverter.ToInt32(data, 0);
                                            cy = BitConverter.ToInt32(data, 4);
                                            cz = BitConverter.ToInt32(data, 8);

                                            strippedData = new byte[l - 12];
                                            Buffer.BlockCopy(data, 12, strippedData, 0, l - 12);
                                        }
                                        else
                                        {
                                            n++;
                                            continue;
                                        }

                                        n++;

                                        Chunk c = _volume.GetChunk((cx, cy, cz));
                                        if (!c)
                                            continue;

                                        c.LoadChanges(strippedData);
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError(e);
                            var retry = keys.Split(',');
                            foreach (string s in retry)
                            {
                                _chunksToFetch.Enqueue(s);
                            }
                        }
                        
                        _ready = true;
                    });
                }
            }
        }

        public async Task<byte[]> GetDataAsync(string keys)
        {
            try
            {
                string url = $"{_baseUrl}/{keys}";
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in GetDataAsync: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> PostDataAsync(string key, byte[] data)
        {
            try
            {
                string url = $"{_baseUrl}/save/{key}";
                ByteArrayContent content = new ByteArrayContent(data);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in PostDataAsync: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}