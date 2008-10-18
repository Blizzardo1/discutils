﻿//
// Copyright (c) 2008, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;

namespace DiscUtils.Iso9660
{
    internal abstract class VolumeDescriptorDiskRegion : DiskRegion
    {
        byte[] readCache;

        public VolumeDescriptorDiskRegion(long start)
            : base(start)
        {
        }

        internal override void PrepareForRead()
        {
            readCache = GetBlockData();
        }

        internal override void ReadLogicalBlock(long diskOffset, byte[] block, int offset)
        {
            Array.Copy(readCache, 0, block, offset, 2048);
        }

        internal override void DisposeReadState()
        {
            readCache = null;
        }

        protected abstract byte[] GetBlockData();
    }

    internal class PrimaryVolumeDescriptorRegion : VolumeDescriptorDiskRegion
    {
        private PrimaryVolumeDescriptor descriptor;

        public PrimaryVolumeDescriptorRegion(PrimaryVolumeDescriptor descriptor, long start)
            : base(start)
        {
            this.descriptor = descriptor;
            DiskLength = 2048;
        }

        protected override byte[] GetBlockData()
        {
            byte[] buffer = new byte[2048];
            descriptor.WriteTo(buffer, 0);
            return buffer;
        }
    }

    internal class SupplementaryVolumeDescriptorRegion : VolumeDescriptorDiskRegion
    {
        private SupplementaryVolumeDescriptor descriptor;

        public SupplementaryVolumeDescriptorRegion(SupplementaryVolumeDescriptor descriptor, long start)
            : base(start)
        {
            this.descriptor = descriptor;
            DiskLength = 2048;
        }

        protected override byte[] GetBlockData()
        {
            byte[] buffer = new byte[2048];
            descriptor.WriteTo(buffer, 0);
            return buffer;
        }
    }

    internal class VolumeDescriptorSetTerminatorRegion : VolumeDescriptorDiskRegion
    {
        private VolumeDescriptorSetTerminator descriptor;

        public VolumeDescriptorSetTerminatorRegion(VolumeDescriptorSetTerminator descriptor, long start)
            : base(start)
        {
            this.descriptor = descriptor;
            DiskLength = 2048;
        }

        protected override byte[] GetBlockData()
        {
            byte[] buffer = new byte[2048];
            descriptor.WriteTo(buffer, 0);
            return buffer;
        }
    }
}