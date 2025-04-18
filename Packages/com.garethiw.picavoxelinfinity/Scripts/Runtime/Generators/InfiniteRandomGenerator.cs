﻿using UnityEngine;

namespace PicaVoxel
{
    public class InfiniteRandomGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        private int _seed;
        private System.Random _random;

        public int Seed
        {
            get => _seed;
            set
            {
                _seed = value; 
                _random = new System.Random(_seed);
            }
        }
        
        private bool _isReady = true;
        public bool IsReady => _isReady;

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            voxel.Color = Color.white;
            voxel.State = (byte)((_random.Next(0,2)==1)?1:0); //(x + y + z) % 2 == 0; //
            voxel.Value = 0;

            return true;
        }
    }
}