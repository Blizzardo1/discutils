﻿//
// Copyright (c) 2008-2009, Kenneth Bell
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
using System.IO;

namespace DiscUtils.Vmdk
{
    /// <summary>
    /// Represents a single VMDK file.
    /// </summary>
    public class DiskImageFile : VirtualDiskLayer
    {
        private DescriptorFile _descriptor;
        private SparseStream _contentStream;
        private FileLocator _extentLocator;

        private FileAccess _access;

        /// <summary>
        /// Creates a new instance from a file on disk.
        /// </summary>
        /// <param name="path">The path to the disk</param>
        /// <param name="access">The desired access to the disk</param>
        public DiskImageFile(string path, FileAccess access)
        {
            _access = access;

            FileAccess fileAccess = FileAccess.Read;
            FileShare fileShare = FileShare.Read;
            if (_access != FileAccess.Read)
            {
                fileAccess = FileAccess.ReadWrite;
                fileShare = FileShare.None;
            }

            using (FileStream s = new FileStream(path, FileMode.Open, fileAccess, fileShare))
            {
                LoadDescriptor(s);
            }

            if (_descriptor.ParentContentId != uint.MaxValue)
            {
                throw new NotImplementedException("No support for differencing disks (yet)");
            }

            _extentLocator = new LocalFileLocator(Path.GetDirectoryName(path));
        }

        /// <summary>
        /// Disposes of this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> if disposing, <c>false</c> if in destructor</param>
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && _contentStream != null)
                {
                    _contentStream.Dispose();
                    _contentStream = null;
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Creates a new virtual disk at the specified path.
        /// </summary>
        /// <param name="path">The name of the VMDK to create.</param>
        /// <param name="capacity">The desired capacity of the new disk</param>
        /// <param name="type">The type of virtual disk to create</param>
        /// <returns>The newly created disk image</returns>
        public static DiskImageFile Initialize(string path, long capacity, DiskCreateType type)
        {
            if (type != DiskCreateType.MonolithicSparse)
            {
                throw new NotImplementedException("Only MonolithicSparse implemented");
            }

            using(FileStream fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                long descriptorStart;
                long actualSize = CreateExtent(fs, capacity, ExtentType.Sparse, 10 * Sizes.OneKiB, out descriptorStart);

                DescriptorFile descriptor = new DescriptorFile();
                descriptor.DiskGeometry = Geometry.FromCapacity(actualSize);
                descriptor.ContentId = (uint)new Random().Next();
                descriptor.CreateType = type;
                descriptor.Extents.Add(new ExtentDescriptor(ExtentAccess.ReadWrite, actualSize / Sizes.Sector, ExtentType.Sparse, Path.GetFileName(path), 0));
                descriptor.UniqueId = Guid.NewGuid();

                fs.Position = descriptorStart * Sizes.Sector;
                descriptor.Write(fs);
            }

            return new DiskImageFile(path, FileAccess.ReadWrite);
        }

        /// <summary>
        /// Gets an indication as to whether the disk file is sparse.
        /// </summary>
        public override bool IsSparse
        {
            get
            {
                return _descriptor.CreateType == DiskCreateType.MonolithicSparse
                    || _descriptor.CreateType == DiskCreateType.TwoGbMaxExtentSparse
                    || _descriptor.CreateType == DiskCreateType.VmfsSparse;
            }
        }

        /// <summary>
        /// Gets the capacity of this disk (in bytes).
        /// </summary>
        internal override long Capacity
        {
            get
            {
                long result = 0;
                foreach (var extent in _descriptor.Extents)
                {
                    result += extent.SizeInSectors * Sizes.Sector;
                }
                return result;
            }
        }

        /// <summary>
        /// Gets the Geometry of this disk.
        /// </summary>
        internal Geometry Geometry
        {
            get { return _descriptor.DiskGeometry; }
        }

        /// <summary>
        /// Gets the contents of this disk as a stream.
        /// </summary>
        internal SparseStream OpenContent(SparseStream parent, bool ownsParent)
        {
            if (parent != null && ownsParent)
            {
                parent.Dispose();
                parent = null;
            }

            long extentStart = 0;
            SparseStream[] streams = new SparseStream[_descriptor.Extents.Count];
            for (int i = 0; i < streams.Length; ++i)
            {
                streams[i] = OpenExtent(_descriptor.Extents[i], extentStart);
                extentStart += _descriptor.Extents[i].SizeInSectors * Sizes.Sector;
            }
            return new ConcatStream(streams);
        }

        private SparseStream OpenExtent(ExtentDescriptor extent, long extentStart)
        {
            FileAccess access = FileAccess.Read;
            FileShare share = FileShare.Read;
            if(extent.Access == ExtentAccess.ReadWrite && _access != FileAccess.Read)
            {
                access = FileAccess.ReadWrite;
                share = FileShare.None;
            }

            switch (extent.Type)
            {
                case ExtentType.Flat:
                case ExtentType.Vmfs:
                    return SparseStream.FromStream(
                        _extentLocator.Open(extent.FileName, FileMode.Open, access, share),
                        true);

                case ExtentType.Zero:
                    return new ZeroStream(extent.SizeInSectors * Utilities.SectorSize);

                case ExtentType.Sparse:
                    return new HostedSparseExtentStream(
                        _extentLocator.Open(extent.FileName, FileMode.Open, access, share),
                        true,
                        extentStart,
                        new ZeroStream(extent.SizeInSectors * Sizes.Sector),
                        true);

                default:
                    throw new NotSupportedException();
            }
        }

        private static long CreateExtent(Stream extentStream, long size, ExtentType type, long descriptorLength, out long descriptorStart)
        {
            if (type == ExtentType.Flat || type == ExtentType.Vmfs)
            {
                extentStream.SetLength(size);
                descriptorStart = 0;
                return size;
            }

            if (type != ExtentType.Sparse)
            {
                throw new NotImplementedException("Extent type not implemented");
            }

            // Figure out grain size and number of grain tables, and adjust actual extent size to be a multiple
            // of grain size
            int targetGrainTables = 256;
            const int gtesPerGt = 512;
            long grainSize = Math.Max(size / (targetGrainTables * gtesPerGt * Sizes.Sector), 8);
            int numGrainTables = (int)Utilities.Ceil(size, grainSize * gtesPerGt * Sizes.Sector);
            long actualSize = numGrainTables * grainSize * gtesPerGt * Sizes.Sector;

            descriptorLength = Utilities.RoundUp(descriptorLength, Sizes.Sector);
            descriptorStart = 0;
            if (descriptorLength != 0)
            {
                descriptorStart = 1;
            }

            long redundantGrainDirStart = descriptorStart + Utilities.Ceil(descriptorLength, Sizes.Sector);
            long redundantGrainDirLength = numGrainTables * 4;

            long redundantGrainTablesStart = redundantGrainDirStart + Utilities.Ceil(redundantGrainDirLength, Sizes.Sector);
            long redundantGrainTablesLength = numGrainTables * Utilities.RoundUp(gtesPerGt * 4, Sizes.Sector);

            long grainDirStart = redundantGrainTablesStart + Utilities.Ceil(redundantGrainTablesLength, Sizes.Sector);
            long grainDirLength = numGrainTables * 4;

            long grainTablesStart = grainDirStart + Utilities.Ceil(grainDirLength, Sizes.Sector);
            long grainTablesLength = numGrainTables * Utilities.RoundUp(gtesPerGt * 4, Sizes.Sector);

            long dataStart = Utilities.RoundUp(grainTablesStart + Utilities.Ceil(grainTablesLength, Sizes.Sector), grainSize);

            // Generate the header, and write it
            HostedSparseExtentHeader header = new HostedSparseExtentHeader();
            header.Flags = HostedSparseExtentFlags.ValidLineDetectionTest | HostedSparseExtentFlags.RedundantGrainTable;
            header.Capacity = actualSize / Sizes.Sector;
            header.GrainSize = grainSize;
            header.DescriptorOffset = descriptorStart;
            header.DescriptorSize = descriptorLength / Sizes.Sector;
            header.NumGTEsPerGT = gtesPerGt;
            header.RgdOffset = redundantGrainDirStart;
            header.GdOffset = grainDirStart;
            header.Overhead = dataStart;

            extentStream.Position = 0;
            extentStream.Write(header.GetBytes(), 0, Sizes.Sector);


            // Zero-out the descriptor space
            if (descriptorLength > 0)
            {
                byte[] descriptor = new byte[descriptorLength];
                extentStream.Position = descriptorStart * Sizes.Sector;
                extentStream.Write(descriptor, 0, descriptor.Length);
            }


            // Generate the redundant grain dir, and write it
            byte[] grainDir = new byte[numGrainTables * 4];
            for (int i = 0; i < numGrainTables; ++i)
            {
                Utilities.WriteBytesLittleEndian((uint)(redundantGrainTablesStart + (i * Utilities.Ceil(gtesPerGt * 4, Sizes.Sector))), grainDir, i * 4);
            }
            extentStream.Position = redundantGrainDirStart * Sizes.Sector;
            extentStream.Write(grainDir, 0, grainDir.Length);


            // Write out the blank grain tables
            byte[] grainTable = new byte[gtesPerGt * 4];
            for (int i = 0; i < numGrainTables; ++i)
            {
                extentStream.Position = (redundantGrainTablesStart * Sizes.Sector) + (i * Utilities.RoundUp(gtesPerGt * 4, Sizes.Sector));
                extentStream.Write(grainTable, 0, grainTable.Length);
            }


            // Generate the main grain dir, and write it
            for (int i = 0; i < numGrainTables; ++i)
            {
                Utilities.WriteBytesLittleEndian((uint)(grainTablesStart + (i * Utilities.Ceil(gtesPerGt * 4, Sizes.Sector))), grainDir, i * 4);
            }
            extentStream.Position = grainDirStart * Sizes.Sector;
            extentStream.Write(grainDir, 0, grainDir.Length);


            // Write out the blank grain tables
            for (int i = 0; i < numGrainTables; ++i)
            {
                extentStream.Position = (grainTablesStart * Sizes.Sector) + (i * Utilities.RoundUp(gtesPerGt * 4, Sizes.Sector));
                extentStream.Write(grainTable, 0, grainTable.Length);
            }

            // Make sure stream is correct length
            if (extentStream.Length != dataStart * Sizes.Sector)
            {
                extentStream.SetLength(dataStart * Sizes.Sector);
            }

            return actualSize;
        }

        private void LoadDescriptor(Stream s)
        {
            byte[] header = Utilities.ReadFully(s, (int)Math.Min(Sizes.Sector, s.Length));
            if (header.Length < Sizes.Sector || Utilities.ToUInt32LittleEndian(header, 0) != HostedSparseExtentHeader.VmdkMagicNumber)
            {
                s.Position = 0;
                _descriptor = new DescriptorFile(s);
                if (_access != FileAccess.Read)
                {
                    _descriptor.ContentId = (uint)new Random().Next();
                    s.Position = 0;
                    _descriptor.Write(s);
                    s.SetLength(s.Position);
                }
            }
            else
            {
                // This is a sparse disk extent, hopefully with embedded descriptor...
                HostedSparseExtentHeader hdr = HostedSparseExtentHeader.Read(header, 0);
                if (hdr.DescriptorOffset != 0)
                {
                    Stream descriptorStream = new SubStream(s, hdr.DescriptorOffset * Sizes.Sector, hdr.DescriptorSize * Sizes.Sector);
                    _descriptor = new DescriptorFile(descriptorStream);
                    if (_access != FileAccess.Read)
                    {
                        _descriptor.ContentId = (uint)new Random().Next();
                        descriptorStream.Position = 0;
                        _descriptor.Write(descriptorStream);
                        byte[] blank = new byte[descriptorStream.Length - descriptorStream.Position];
                        descriptorStream.Write(blank, 0, blank.Length);
                    }
                }
            }
        }

    }
}