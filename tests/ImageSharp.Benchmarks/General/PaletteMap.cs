// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace SixLabors.ImageSharp.Benchmarks.General
{
    [Config(typeof(Config.ShortClr))]
    public class PaletteMap
    {
        private Image<Rgba32> image;
        private Rgba32[] palette;
        private PaletteMap<Rgba32> map;

        [GlobalSetup]
        public void Setup()
        {
            this.image = new Image<Rgba32>(1024, 1024);

            var random = new Random();
            var palette = new Rgba32[256];
            Span<byte> paletteBytes = MemoryMarshal.AsBytes(palette.AsSpan());
            for (int i = 0; i < paletteBytes.Length; i++)
            {
                paletteBytes[i] = (byte)random.Next();
            }

            this.palette = palette;
            this.map = new PaletteMap<Rgba32>(palette);

            for (int y = 0; y < this.image.Height; y++)
            {
                Span<Rgba32> row = this.image.GetPixelRowSpan(y);
                Span<byte> rowBytes = MemoryMarshal.AsBytes(row);

                for (int i = 0; i < rowBytes.Length; i++)
                {
                    rowBytes[i] = (byte)random.Next();
                }
            }
        }

        [Benchmark]
        public byte BuildPalette()
        {
            var map = new PaletteMap<Rgba32>(this.palette);

            return this.palette[0].R;
        }

        [Benchmark]
        public byte GetPaletteIndex()
        {
            byte index = 0;
            for (int y = 0; y < this.image.Height; y++)
            {
                Span<Rgba32> row = this.image.GetPixelRowSpan(y);

                for (int x = 0; x < this.image.Width; x++)
                {
                    index = this.map.GetMatch(row[x], out Rgba32 match);
                }
            }

            return index;
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            this.image.Dispose();
            this.image = null;
        }
    }
}
