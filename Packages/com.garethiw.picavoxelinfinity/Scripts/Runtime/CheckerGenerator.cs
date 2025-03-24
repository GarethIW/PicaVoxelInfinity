using System;
using UnityEngine;

namespace PicaVoxel
{
    public class CheckerGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        private int _seed;
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }
        
        private bool _isReady = true;
        public bool IsReady => _isReady;

        private bool[] _active = new bool[16*16*16];

        private void Start()
        {
            for (int z = 0; z < 16; z++)
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        _active[x + 16 * (y + 16 * z)] = ((x + y + z) % 2 == 0);
                    }
        }

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            x = Math.Abs(x) % 16;
            y = Math.Abs(y) % 16;
            z = Math.Abs(z) % 16;
            voxel.Color = Color.white;
            voxel.Active = _active[x+16 *(y+16*z)]; //Random.Range(0,2)==1; //(x + y + z) % 2 == 0; //
            voxel.Value = 0;
        }
    }
}