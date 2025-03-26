﻿using System;
using Unity.VisualScripting;
using UnityEngine;

namespace PicaVoxel
{
    [Serializable, Inspectable]
    public class VoxelManipulatorChangeEventArgs
    {
        [Inspectable]
        public string VolumeId;
        [Inspectable]
        public int ChunkX;
        [Inspectable]
        public int ChunkY;
        [Inspectable]
        public int ChunkZ;
        [Inspectable]
        public int VoxelX;
        [Inspectable]
        public int VoxelY;
        [Inspectable]
        public int VoxelZ;
        [Inspectable]
        public bool VoxelActive;
        [Inspectable]
        public byte VoxelValue;
        [Inspectable]
        public Color VoxelColor;
    }
}