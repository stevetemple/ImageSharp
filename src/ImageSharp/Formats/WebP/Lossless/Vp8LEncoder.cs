// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using SixLabors.ImageSharp.Formats.WebP.BitWriter;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Formats.WebP.Lossless
{
    /// <summary>
    /// Encoder for lossless webp images.
    /// </summary>
    internal class Vp8LEncoder : IDisposable
    {
        /// <summary>
        /// Maximum number of reference blocks the image will be segmented into.
        /// </summary>
        private const int MaxRefsBlockPerImage = 16;

        /// <summary>
        /// Minimum block size for backward references.
        /// </summary>
        private const int MinBlockSize = 256;

        /// <summary>
        /// The <see cref="MemoryAllocator"/> to use for buffer allocations.
        /// </summary>
        private readonly MemoryAllocator memoryAllocator;

        /// <summary>
        /// A bit writer for writing lossless webp streams.
        /// </summary>
        private Vp8LBitWriter bitWriter;

        private const int ApplyPaletteGreedyMax = 4;

        private const int PaletteInvSizeBits = 11;

        private const int PaletteInvSize = 1 << PaletteInvSizeBits;

        /// <summary>
        /// Initializes a new instance of the <see cref="Vp8LEncoder"/> class.
        /// </summary>
        /// <param name="memoryAllocator">The memory allocator.</param>
        /// <param name="width">The width of the input image.</param>
        /// <param name="height">The height of the input image.</param>
        public Vp8LEncoder(MemoryAllocator memoryAllocator, int width, int height)
        {
            var pixelCount = width * height;
            int initialSize = pixelCount * 2;

            this.bitWriter = new Vp8LBitWriter(initialSize);
            this.Bgra = memoryAllocator.Allocate<uint>(pixelCount);
            this.Palette = memoryAllocator.Allocate<uint>(WebPConstants.MaxPaletteSize);
            this.Refs = new Vp8LBackwardRefs[3];
            this.HashChain = new Vp8LHashChain(pixelCount);
            this.memoryAllocator = memoryAllocator;

            // We round the block size up, so we're guaranteed to have at most MaxRefsBlockPerImage blocks used:
            int refsBlockSize = ((pixelCount - 1) / MaxRefsBlockPerImage) + 1;
            for (int i = 0; i < this.Refs.Length; ++i)
            {
                this.Refs[i] = new Vp8LBackwardRefs
                {
                    BlockSize = (refsBlockSize < MinBlockSize) ? MinBlockSize : refsBlockSize
                };
            }
        }

        /// <summary>
        /// Gets transformed image data.
        /// </summary>
        public IMemoryOwner<uint> Bgra { get; }

        /// <summary>
        /// Gets or sets the scratch memory for bgra rows used for prediction.
        /// </summary>
        public IMemoryOwner<uint> BgraScratch { get; set; }

        /// <summary>
        /// Gets or sets the packed image width.
        /// </summary>
        public int CurrentWidth { get; set; }

        /// <summary>
        /// Gets or sets the huffman image bits.
        /// </summary>
        public int HistoBits { get; set; }

        /// <summary>
        /// Gets or sets the bits used for the transformation.
        /// </summary>
        public int TransformBits { get; set; }

        /// <summary>
        /// Gets or sets the transform data.
        /// </summary>
        public IMemoryOwner<uint> TransformData { get; set; }

        /// <summary>
        /// Gets or sets the cache bits. If equal to 0, don't use color cache.
        /// </summary>
        public int CacheBits { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use the cross color transform.
        /// </summary>
        public bool UseCrossColorTransform { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use the substract green transform.
        /// </summary>
        public bool UseSubtractGreenTransform { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use the predictor transform.
        /// </summary>
        public bool UsePredictorTransform { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use color indexing transform.
        /// </summary>
        public bool UsePalette { get; set; }

        /// <summary>
        /// Gets or sets the palette size.
        /// </summary>
        public int PaletteSize { get; set; }

        /// <summary>
        /// Gets the palette.
        /// </summary>
        public IMemoryOwner<uint> Palette { get; }

        /// <summary>
        /// Gets the backward references.
        /// </summary>
        public Vp8LBackwardRefs[] Refs { get; }

        /// <summary>
        /// Gets the hash chain.
        /// </summary>
        public Vp8LHashChain HashChain { get; }

        /// <summary>
        /// Encodes the image to the specified stream from the <see cref="Image{TPixel}"/>.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The <see cref="Image{TPixel}"/> to encode from.</param>
        /// <param name="stream">The <see cref="Stream"/> to encode the image data to.</param>
        public void Encode<TPixel>(Image<TPixel> image, Stream stream)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            // Write the image size.
            int width = image.Width;
            int height = image.Height;
            this.WriteImageSize(width, height);

            // Write the non-trivial Alpha flag and lossless version.
            bool hasAlpha = false; // TODO: for the start, this will be always false.
            this.WriteAlphaAndVersion(hasAlpha);

            // Encode the main image stream.
            this.EncodeStream(image);

            // Write bytes from the bitwriter buffer to the stream.
            this.bitWriter.Finish();
            var numBytes = this.bitWriter.NumBytes();
            var vp8LSize = 1 + numBytes; // One byte extra for the VP8L signature.
            var pad = vp8LSize & 1;
            var riffSize = WebPConstants.TagSize + WebPConstants.ChunkHeaderSize + vp8LSize + pad;
            this.WriteRiffHeader(riffSize, vp8LSize, stream);
            this.bitWriter.WriteToStream(stream);
        }

        /// <summary>
        /// Writes the image size to the bitwriter buffer.
        /// </summary>
        /// <param name="inputImgWidth">The input image width.</param>
        /// <param name="inputImgHeight">The input image height.</param>
        private void WriteImageSize(int inputImgWidth, int inputImgHeight)
        {
            Guard.MustBeLessThan(inputImgWidth, WebPConstants.MaxDimension, nameof(inputImgWidth));
            Guard.MustBeLessThan(inputImgHeight, WebPConstants.MaxDimension, nameof(inputImgHeight));

            uint width = (uint)inputImgWidth - 1;
            uint height = (uint)inputImgHeight - 1;

            this.bitWriter.PutBits(width, WebPConstants.Vp8LImageSizeBits);
            this.bitWriter.PutBits(height, WebPConstants.Vp8LImageSizeBits);
        }

        /// <summary>
        /// Writes a flag indicating if alpha channel is used and the VP8L version to the bitwriter buffer.
        /// </summary>
        /// <param name="hasAlpha">Indicates if a alpha channel is present.</param>
        private void WriteAlphaAndVersion(bool hasAlpha)
        {
            this.bitWriter.PutBits(hasAlpha ? 1U : 0, 1);
            this.bitWriter.PutBits(WebPConstants.Vp8LVersion, WebPConstants.Vp8LVersionBits);
        }

        /// <summary>
        /// Writes the RIFF header to the stream.
        /// </summary>
        /// <param name="riffSize">The block length.</param>
        /// <param name="vp8LSize">The size in bytes of the compressed image.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteRiffHeader(int riffSize, int vp8LSize, Stream stream)
        {
            Span<byte> buffer = stackalloc byte[4];

            stream.Write(WebPConstants.RiffFourCc);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)riffSize);
            stream.Write(buffer);
            stream.Write(WebPConstants.WebPHeader);
            stream.Write(WebPConstants.Vp8LTag);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)vp8LSize);
            stream.Write(buffer);
            stream.WriteByte(WebPConstants.Vp8LMagicByte);
        }

        /// <summary>
        /// Encodes the image stream using lossless webp format.
        /// </summary>
        /// <typeparam name="TPixel">The pixel type.</typeparam>
        /// <param name="image">The image to encode.</param>
        private void EncodeStream<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            int width = image.Width;
            int height = image.Height;
            int bytePosition = this.bitWriter.NumBytes();

            // Convert image pixels to bgra array.
            Span<uint> bgra = this.Bgra.GetSpan();
            int idx = 0;
            for (int y = 0; y < height; y++)
            {
                Span<TPixel> rowSpan = image.GetPixelRowSpan(y);
                for (int x = 0; x < rowSpan.Length; x++)
                {
                    bgra[idx++] = ToBgra32(rowSpan[x]).PackedValue;
                }
            }

            // Analyze image (entropy, numPalettes etc).
            CrunchConfig[] crunchConfigs = this.EncoderAnalyze(image, out bool redAndBlueAlwaysZero);

            int quality = 75; // TODO: quality is hardcoded for now.

            Vp8LBitWriter bestBitWriter = null;

            // TODO : Do we want to do this multi-threaded, this will probably require a second class:
            // one which co-ordinates the threading and comparison and another which does the actual encoding
            foreach (CrunchConfig crunchConfig in crunchConfigs)
            {
                bool useCache = true;
                this.UsePalette = crunchConfig.EntropyIdx == EntropyIx.Palette || crunchConfig.EntropyIdx == EntropyIx.PaletteAndSpatial;
                this.UseSubtractGreenTransform = (crunchConfig.EntropyIdx == EntropyIx.SubGreen) ||
                                                 (crunchConfig.EntropyIdx == EntropyIx.SpatialSubGreen);
                this.UsePredictorTransform = (crunchConfig.EntropyIdx == EntropyIx.Spatial) ||
                                             (crunchConfig.EntropyIdx == EntropyIx.SpatialSubGreen);
                this.UseCrossColorTransform = redAndBlueAlwaysZero ? false : this.UsePredictorTransform;
                this.AllocateTransformBuffer(width, height);

                // Reset any parameter in the encoder that is set in the previous iteration.
                this.CacheBits = 0;
                this.ClearRefs();

                // TODO: Apply near-lossless preprocessing.

                // Encode palette.
                if (this.UsePalette)
                {
                    this.EncodePalette();
                    this.MapImageFromPalette(width, height);

                    // If using a color cache, do not have it bigger than the number of colors.
                    if (useCache && this.PaletteSize < (1 << WebPConstants.MaxColorCacheBits))
                    {
                        this.CacheBits = WebPCommonUtils.BitsLog2Floor((uint)this.PaletteSize) + 1;
                    }
                }

                // Apply transforms and write transform data.
                if (this.UseSubtractGreenTransform)
                {
                    this.ApplySubtractGreen(this.CurrentWidth, height);
                }

                if (this.UsePredictorTransform)
                {
                    this.ApplyPredictFilter(this.CurrentWidth, height, quality, this.UseSubtractGreenTransform);
                }

                if (this.UseCrossColorTransform)
                {
                    this.ApplyCrossColorFilter(this.CurrentWidth, height, quality);
                }

                this.bitWriter.PutBits(0, 1); // No more transforms.

                // Encode and write the transformed image.
                this.EncodeImage(
                    bgra,
                    this.HashChain,
                    this.Refs,
                    this.CurrentWidth,
                    height,
                    quality,
                    useCache,
                    crunchConfig,
                    this.CacheBits,
                    this.HistoBits,
                    bytePosition);

                if (bestBitWriter == null || this.bitWriter.NumBytes() < bestBitWriter.NumBytes())
                {
                    bestBitWriter = this.bitWriter.Clone();
                }
            }

            this.bitWriter = bestBitWriter;
        }

        /// <summary>
        /// Analyzes the image and decides what transforms should be used.
        /// </summary>
        private CrunchConfig[] EncoderAnalyze<TPixel>(Image<TPixel> image, out bool redAndBlueAlwaysZero)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var configQuality = 75; // TODO: hardcoded quality for now
            int method = 4; // TODO: method hardcoded to 4 for now.
            int width = image.Width;
            int height = image.Height;

            // Check if we only deal with a small number of colors and should use a palette.
            var usePalette = this.AnalyzeAndCreatePalette(image);

            // Empirical bit sizes.
            this.HistoBits = GetHistoBits(method, usePalette, width, height);
            this.TransformBits = GetTransformBits(method, this.HistoBits);

            // Try out multiple LZ77 on images with few colors.
            var nlz77s = (this.PaletteSize > 0 && this.PaletteSize <= 16) ? 2 : 1;
            EntropyIx entropyIdx = this.AnalyzeEntropy(image, usePalette, this.PaletteSize, this.TransformBits, out redAndBlueAlwaysZero);

            bool doNotCache = false;
            var crunchConfigs = new List<CrunchConfig>();

            if (method == 6 && configQuality == 100)
            {
                doNotCache = true;

                // Go brute force on all transforms.
                foreach (EntropyIx entropyIx in Enum.GetValues(typeof(EntropyIx)).Cast<EntropyIx>())
                {
                    // We can only apply kPalette or kPaletteAndSpatial if we can indeed use
                    // a palette.
                    if ((entropyIx != EntropyIx.Palette && entropyIx != EntropyIx.PaletteAndSpatial) || usePalette)
                    {
                        crunchConfigs.Add(new CrunchConfig { EntropyIdx = entropyIx });
                    }
                }
            }
            else
            {
                // Only choose the guessed best transform.
                crunchConfigs.Add(new CrunchConfig { EntropyIdx = entropyIdx });
                if (configQuality >= 75 && method == 5)
                {
                    // Test with and without color cache.
                    doNotCache = true;

                    // If we have a palette, also check in combination with spatial.
                    if (entropyIdx == EntropyIx.Palette)
                    {
                        crunchConfigs.Add(new CrunchConfig { EntropyIdx = EntropyIx.PaletteAndSpatial });
                    }
                }
            }

            // Fill in the different LZ77s.
            foreach (CrunchConfig crunchConfig in crunchConfigs)
            {
                for (var j = 0; j < nlz77s; ++j)
                {
                    crunchConfig.SubConfigs.Add(new CrunchSubConfig
                    {
                        Lz77 = (j == 0) ? (int)Vp8LLz77Type.Lz77Standard | (int)Vp8LLz77Type.Lz77Rle : (int)Vp8LLz77Type.Lz77Box,
                        DoNotCache = doNotCache
                    });
                }
            }

            return crunchConfigs.ToArray();
        }

        private void EncodeImage(Span<uint> bgra, Vp8LHashChain hashChain, Vp8LBackwardRefs[] refsArray, int width, int height, int quality, bool useCache, CrunchConfig config, int cacheBits, int histogramBits, int initBytePosition)
        {
            int histogramImageXySize = LosslessUtils.SubSampleSize(width, histogramBits) * LosslessUtils.SubSampleSize(height, histogramBits);
            var histogramSymbols = new short[histogramImageXySize];
            var huffTree = new HuffmanTree[3 * WebPConstants.CodeLengthCodes];
            for (int i = 0; i < huffTree.Length; i++)
            {
                huffTree[i] = new HuffmanTree();
            }

            if (useCache)
            {
                if (cacheBits == 0)
                {
                    cacheBits = WebPConstants.MaxColorCacheBits;
                }
            }
            else
            {
                cacheBits = 0;
            }

            // Calculate backward references from BGRA image.
            BackwardReferenceEncoder.HashChainFill(hashChain, bgra, quality, width, height);

            Vp8LBitWriter bitWriterInit = this.bitWriter.Clone();
            Vp8LBitWriter bitWriterBest = config.SubConfigs.Count > 1 ? null : this.bitWriter;

            foreach (CrunchSubConfig subConfig in config.SubConfigs)
            {
                Vp8LBackwardRefs refsBest = BackwardReferenceEncoder.GetBackwardReferences(width, height, bgra, quality, subConfig.Lz77, ref cacheBits, hashChain, refsArray[0], refsArray[1]); // TODO : Pass do not cache

                // Keep the best references aside and use the other element from the first
                // two as a temporary for later usage.
                Vp8LBackwardRefs refsTmp = refsArray[refsBest.Equals(refsArray[0]) ? 1 : 0];

                // TODO int cacheBitsBest = 0;

                for (int cache = 0; cache < (subConfig.DoNotCache ? 2 : 1); ++cache)
                {
                    // TODO: figure this out
                    /*// Speed-up: no need to study the no-cache case if it was already studied
                    // in cache == 0.
                    if (cache == 1 && cacheBitsBest == 0)
                    {
                        break;
                    }*/

                    this.bitWriter.Reset(bitWriterInit);

                    var tmpHisto = new Vp8LHistogram(cacheBits);
                    var histogramImage = new List<Vp8LHistogram>(histogramImageXySize);
                    for (int i = 0; i < histogramImageXySize; i++)
                    {
                        histogramImage.Add(new Vp8LHistogram(cacheBits));
                    }

                    // Build histogram image and symbols from backward references.
                    HistogramEncoder.GetHistoImageSymbols(width,
                        height,
                        refsBest,
                        quality,
                        histogramBits,
                        cacheBits,
                        histogramImage,
                        tmpHisto,
                        histogramSymbols);

                    // Create Huffman bit lengths and codes for each histogram image.
                    var histogramImageSize = histogramImage.Count;
                    var bitArraySize = 5 * histogramImageSize;
                    var huffmanCodes = new HuffmanTreeCode[bitArraySize];
                    for (int i = 0; i < huffmanCodes.Length; i++)
                    {
                        huffmanCodes[i] = new HuffmanTreeCode();
                    }

                    GetHuffBitLengthsAndCodes(histogramImage, huffmanCodes);

                    // Color Cache parameters.
                    if (cacheBits > 0)
                    {
                        this.bitWriter.PutBits(1, 1);
                        this.bitWriter.PutBits((uint)cacheBits, 4);
                    }
                    else
                    {
                        this.bitWriter.PutBits(0, 1);
                    }

                    // Huffman image + meta huffman.
                    bool writeHistogramImage = histogramImageSize > 1;
                    this.bitWriter.PutBits((uint)(writeHistogramImage ? 1 : 0), 1);
                    if (writeHistogramImage)
                    {
                        using IMemoryOwner<uint> histogramArgbBuffer =
                            this.memoryAllocator.Allocate<uint>(histogramImageXySize);
                        Span<uint> histogramArgb = histogramArgbBuffer.GetSpan();
                        int maxIndex = 0;
                        for (int i = 0; i < histogramImageXySize; i++)
                        {
                            int symbolIndex = histogramSymbols[i] & 0xffff;
                            histogramArgb[i] = (uint)(symbolIndex << 8);
                            if (symbolIndex >= maxIndex)
                            {
                                maxIndex = symbolIndex + 1;
                            }
                        }

                        this.bitWriter.PutBits((uint)(histogramBits - 2), 3);
                        this.EncodeImageNoHuffman(histogramArgb, hashChain, refsTmp, refsArray[2],
                            LosslessUtils.SubSampleSize(width, histogramBits),
                            LosslessUtils.SubSampleSize(height, histogramBits), quality);
                    }

                    // Store Huffman codes.
                    // Find maximum number of symbols for the huffman tree-set.
                    int maxTokens = 0;
                    for (int i = 0; i < 5 * histogramImage.Count; i++)
                    {
                        HuffmanTreeCode codes = huffmanCodes[i];
                        if (maxTokens < codes.NumSymbols)
                        {
                            maxTokens = codes.NumSymbols;
                        }
                    }

                    var tokens = new HuffmanTreeToken[maxTokens];
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        tokens[i] = new HuffmanTreeToken();
                    }

                    for (int i = 0; i < 5 * histogramImage.Count; i++)
                    {
                        HuffmanTreeCode codes = huffmanCodes[i];
                        this.StoreHuffmanCode(huffTree, tokens, codes);
                        ClearHuffmanTreeIfOnlyOneSymbol(codes);
                    }

                    // Store actual literals.
                    var hdrSizeTmp = (int)(this.bitWriter.NumBytes() - initBytePosition);
                    this.StoreImageToBitMask(width, histogramBits, refsBest, histogramSymbols, huffmanCodes);

                    if (bitWriterBest != null && this.bitWriter.NumBytes() < bitWriterBest.NumBytes())
                    {
                        // TODO : This was done in the reference by swapping references, this will be slower
                        bitWriterBest = this.bitWriter.Clone();
                    }
                }
            }

            this.bitWriter = bitWriterBest;
        }

        /// <summary>
        /// Save the palette to the bitstream.
        /// </summary>
        private void EncodePalette()
        {
            Span<uint> tmpPalette = new uint[WebPConstants.MaxPaletteSize];
            int paletteSize = this.PaletteSize;
            Span<uint> palette = this.Palette.Memory.Span;
            this.bitWriter.PutBits(WebPConstants.TransformPresent, 1);
            this.bitWriter.PutBits((uint)Vp8LTransformType.ColorIndexingTransform, 2);
            this.bitWriter.PutBits((uint)paletteSize - 1, 8);
            for (int i = paletteSize - 1; i >= 1; i--)
            {
                tmpPalette[i] = LosslessUtils.SubPixels(palette[i], palette[i - 1]);
            }

            tmpPalette[0] = palette[0];
            this.EncodeImageNoHuffman(tmpPalette, this.HashChain, this.Refs[0], this.Refs[1], width: paletteSize, height: 1, quality: 20);
        }

        /// <summary>
        /// Applies the subtract green transformation to the pixel data of the image.
        /// </summary>
        /// <param name="width">The width of the image.</param>
        /// <param name="height">The height of the image.</param>
        private void ApplySubtractGreen(int width, int height)
        {
            this.bitWriter.PutBits(WebPConstants.TransformPresent, 1);
            this.bitWriter.PutBits((uint)Vp8LTransformType.SubtractGreen, 2);
            LosslessUtils.SubtractGreenFromBlueAndRed(this.Bgra.GetSpan(), width * height);
        }

        private void ApplyPredictFilter(int width, int height, int quality, bool usedSubtractGreen)
        {
            int nearLosslessStrength = 100; // TODO: for now always 100
            bool exact = false; // TODO: always false for now.
            int predBits = this.TransformBits;
            int transformWidth = LosslessUtils.SubSampleSize(width, predBits);
            int transformHeight = LosslessUtils.SubSampleSize(height, predBits);

            PredictorEncoder.ResidualImage(width, height, predBits, this.Bgra.GetSpan(), this.BgraScratch.GetSpan(), this.TransformData.GetSpan(), nearLosslessStrength, exact, usedSubtractGreen);

            this.bitWriter.PutBits(WebPConstants.TransformPresent, 1);
            this.bitWriter.PutBits((uint)Vp8LTransformType.PredictorTransform, 2);
            this.bitWriter.PutBits((uint)(predBits - 2), 3);

            this.EncodeImageNoHuffman(this.TransformData.GetSpan(), this.HashChain, this.Refs[0], this.Refs[1], transformWidth, transformHeight, quality);
        }

        private void ApplyCrossColorFilter(int width, int height, int quality)
        {
            int colorTransformBits = this.TransformBits;
            int transformWidth = LosslessUtils.SubSampleSize(width, colorTransformBits);
            int transformHeight = LosslessUtils.SubSampleSize(height, colorTransformBits);

            PredictorEncoder.ColorSpaceTransform(width, height, colorTransformBits, quality, this.Bgra.GetSpan(), this.TransformData.GetSpan());

            this.bitWriter.PutBits(WebPConstants.TransformPresent, 1);
            this.bitWriter.PutBits((uint)Vp8LTransformType.CrossColorTransform, 2);
            this.bitWriter.PutBits((uint)(colorTransformBits - 2), 3);

            this.EncodeImageNoHuffman(this.TransformData.GetSpan(), this.HashChain, this.Refs[0], this.Refs[1], transformWidth, transformHeight, quality);
        }

        private void EncodeImageNoHuffman(Span<uint> bgra, Vp8LHashChain hashChain, Vp8LBackwardRefs refsTmp1, Vp8LBackwardRefs refsTmp2, int width, int height, int quality)
        {
            int cacheBits = 0;
            var histogramSymbols = new short[1]; // Only one tree, one symbol.
            var huffmanCodes = new HuffmanTreeCode[5];
            for (int i = 0; i < huffmanCodes.Length; i++)
            {
                huffmanCodes[i] = new HuffmanTreeCode();
            }

            var huffTree = new HuffmanTree[3UL * WebPConstants.CodeLengthCodes];
            for (int i = 0; i < huffTree.Length; i++)
            {
                huffTree[i] = new HuffmanTree();
            }

            // Calculate backward references from the image pixels.
            BackwardReferenceEncoder.HashChainFill(hashChain, bgra, quality, width, height);

            Vp8LBackwardRefs refs = BackwardReferenceEncoder.GetBackwardReferences(
                width,
                height,
                bgra,
                quality,
                (int)Vp8LLz77Type.Lz77Standard | (int)Vp8LLz77Type.Lz77Rle,
                ref cacheBits,
                hashChain,
                refsTmp1,
                refsTmp2);

            var histogramImage = new List<Vp8LHistogram>()
            {
                new Vp8LHistogram(cacheBits)
            };

            // Build histogram image and symbols from backward references.
            histogramImage[0].StoreRefs(refs);

            // Create Huffman bit lengths and codes for each histogram image.
            GetHuffBitLengthsAndCodes(histogramImage, huffmanCodes);

            // No color cache, no Huffman image.
            this.bitWriter.PutBits(0, 1);

            // Find maximum number of symbols for the huffman tree-set.
            int maxTokens = 0;
            for (int i = 0; i < 5; i++)
            {
                HuffmanTreeCode codes = huffmanCodes[i];
                if (maxTokens < codes.NumSymbols)
                {
                    maxTokens = codes.NumSymbols;
                }
            }

            var tokens = new HuffmanTreeToken[maxTokens];
            for (int i = 0; i < tokens.Length; i++)
            {
                tokens[i] = new HuffmanTreeToken();
            }

            // Store Huffman codes.
            for (int i = 0; i < 5; i++)
            {
                HuffmanTreeCode codes = huffmanCodes[i];
                this.StoreHuffmanCode(huffTree, tokens, codes);
                ClearHuffmanTreeIfOnlyOneSymbol(codes);
            }

            // Store actual literals.
            this.StoreImageToBitMask(width, 0, refs, histogramSymbols, huffmanCodes);
        }

        private void StoreHuffmanCode(HuffmanTree[] huffTree, HuffmanTreeToken[] tokens, HuffmanTreeCode huffmanCode)
        {
            int count = 0;
            int[] symbols = { 0, 0 };
            int maxBits = 8;
            int maxSymbol = 1 << maxBits;

            // Check whether it's a small tree.
            for (int i = 0; i < huffmanCode.NumSymbols && count < 3; ++i)
            {
                if (huffmanCode.CodeLengths[i] != 0)
                {
                    if (count < 2)
                    {
                        symbols[count] = i;
                    }

                    count++;
                }
            }

            if (count == 0)
            {
                // emit minimal tree for empty cases
                // bits: small tree marker: 1, count-1: 0, large 8-bit code: 0, code: 0
                this.bitWriter.PutBits(0x01, 4);
            }
            else if (count <= 2 && symbols[0] < maxSymbol && symbols[1] < maxSymbol)
            {
                this.bitWriter.PutBits(1, 1);  // Small tree marker to encode 1 or 2 symbols.
                this.bitWriter.PutBits((uint)(count - 1), 1);
                if (symbols[0] <= 1)
                {
                    this.bitWriter.PutBits(0, 1);  // Code bit for small (1 bit) symbol value.
                    this.bitWriter.PutBits((uint)symbols[0], 1);
                }
                else
                {
                    this.bitWriter.PutBits(1, 1);
                    this.bitWriter.PutBits((uint)symbols[0], 8);
                }

                if (count == 2)
                {
                    this.bitWriter.PutBits((uint)symbols[1], 8);
                }
            }
            else
            {
                this.StoreFullHuffmanCode(huffTree, tokens, huffmanCode);
            }
        }

        private void StoreFullHuffmanCode(HuffmanTree[] huffTree, HuffmanTreeToken[] tokens, HuffmanTreeCode tree)
        {
            int i;
            var codeLengthBitDepth = new byte[WebPConstants.CodeLengthCodes];
            var codeLengthBitDepthSymbols = new short[WebPConstants.CodeLengthCodes];
            var huffmanCode = new HuffmanTreeCode();
            huffmanCode.NumSymbols = WebPConstants.CodeLengthCodes;
            huffmanCode.CodeLengths = codeLengthBitDepth;
            huffmanCode.Codes = codeLengthBitDepthSymbols;

            this.bitWriter.PutBits(0, 1);
            var numTokens = HuffmanUtils.CreateCompressedHuffmanTree(tree, tokens);
            var histogram = new uint[WebPConstants.CodeLengthCodes + 1];
            var bufRle = new bool[WebPConstants.CodeLengthCodes + 1];
            for (i = 0; i < numTokens; i++)
            {
                histogram[tokens[i].Code]++;
            }

            HuffmanUtils.CreateHuffmanTree(histogram, 7, bufRle, huffTree, huffmanCode);
            this.StoreHuffmanTreeOfHuffmanTreeToBitMask(codeLengthBitDepth);
            ClearHuffmanTreeIfOnlyOneSymbol(huffmanCode);

            int trailingZeroBits = 0;
            int trimmedLength = numTokens;
            i = numTokens;
            while (i-- > 0)
            {
                int ix = tokens[i].Code;
                if (ix == 0 || ix == 17 || ix == 18)
                {
                    trimmedLength--;   // discount trailing zeros.
                    trailingZeroBits += codeLengthBitDepth[ix];
                    if (ix == 17)
                    {
                        trailingZeroBits += 3;
                    }
                    else if (ix == 18)
                    {
                        trailingZeroBits += 7;
                    }
                }
                else
                {
                    break;
                }
            }

            var writeTrimmedLength = trimmedLength > 1 && trailingZeroBits > 12;
            var length = writeTrimmedLength ? trimmedLength : numTokens;
            this.bitWriter.PutBits((uint)(writeTrimmedLength ? 1 : 0), 1);
            if (writeTrimmedLength)
            {
                if (trimmedLength == 2)
                {
                    this.bitWriter.PutBits(0, 3 + 2); // nbitpairs=1, trimmed_length=2
                }
                else
                {
                    int nbits = WebPCommonUtils.BitsLog2Floor((uint)trimmedLength - 2);
                    int nbitpairs = (nbits / 2) + 1;
                    this.bitWriter.PutBits((uint)nbitpairs - 1, 3);
                    this.bitWriter.PutBits((uint)trimmedLength - 2, nbitpairs * 2);
                }
            }

            this.StoreHuffmanTreeToBitMask(tokens, length, huffmanCode);
        }

        private void StoreHuffmanTreeToBitMask(HuffmanTreeToken[] tokens, int numTokens, HuffmanTreeCode huffmanCode)
        {
            for (int i = 0; i < numTokens; i++)
            {
                int ix = tokens[i].Code;
                int extraBits = tokens[i].ExtraBits;
                this.bitWriter.PutBits((uint)huffmanCode.Codes[ix], huffmanCode.CodeLengths[ix]);
                switch (ix)
                {
                    case 16:
                        this.bitWriter.PutBits((uint)extraBits, 2);
                        break;
                    case 17:
                        this.bitWriter.PutBits((uint)extraBits, 3);
                        break;
                    case 18:
                        this.bitWriter.PutBits((uint)extraBits, 7);
                        break;
                }
            }
        }

        private void StoreHuffmanTreeOfHuffmanTreeToBitMask(byte[] codeLengthBitDepth)
        {
            // RFC 1951 will calm you down if you are worried about this funny sequence.
            // This sequence is tuned from that, but more weighted for lower symbol count,
            // and more spiking histograms.
            byte[] storageOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

            // Throw away trailing zeros:
            int codesToStore = WebPConstants.CodeLengthCodes;
            for (; codesToStore > 4; codesToStore--)
            {
                if (codeLengthBitDepth[storageOrder[codesToStore - 1]] != 0)
                {
                    break;
                }
            }

            this.bitWriter.PutBits((uint)codesToStore - 4, 4);
            for (int i = 0; i < codesToStore; i++)
            {
                this.bitWriter.PutBits(codeLengthBitDepth[storageOrder[i]], 3);
            }
        }

        private void StoreImageToBitMask(int width, int histoBits, Vp8LBackwardRefs backwardRefs, short[] histogramSymbols, HuffmanTreeCode[] huffmanCodes)
        {
            int histoXSize = histoBits > 0 ? LosslessUtils.SubSampleSize(width, histoBits) : 1;
            int tileMask = (histoBits == 0) ? 0 : -(1 << histoBits);

            // x and y trace the position in the image.
            int x = 0;
            int y = 0;
            int tileX = x & tileMask;
            int tileY = y & tileMask;
            int histogramIx = histogramSymbols[0];
            Span<HuffmanTreeCode> codes = huffmanCodes.AsSpan(5 * histogramIx);
            using List<PixOrCopy>.Enumerator c = backwardRefs.Refs.GetEnumerator();
            while (c.MoveNext())
            {
                PixOrCopy v = c.Current;
                if ((tileX != (x & tileMask)) || (tileY != (y & tileMask)))
                {
                    tileX = x & tileMask;
                    tileY = y & tileMask;
                    histogramIx = histogramSymbols[((y >> histoBits) * histoXSize) + (x >> histoBits)];
                    codes = huffmanCodes.AsSpan(5 * histogramIx);
                }

                if (v.IsLiteral())
                {
                    byte[] order = { 1, 2, 0, 3 };
                    for (int k = 0; k < 4; k++)
                    {
                        int code = (int)v.Literal(order[k]);
                        this.bitWriter.WriteHuffmanCode(codes[k], code);
                    }
                }
                else if (v.IsCacheIdx())
                {
                    int code = (int)v.CacheIdx();
                    int literalIx = 256 + WebPConstants.NumLengthCodes + code;
                    this.bitWriter.WriteHuffmanCode(codes[0], literalIx);
                }
                else
                {
                    int bits = 0;
                    int nBits = 0;
                    int distance = (int)v.Distance();
                    int code = LosslessUtils.PrefixEncode(v.Len, ref nBits, ref bits);
                    this.bitWriter.WriteHuffmanCodeWithExtraBits(codes[0], 256 + code, bits, nBits);

                    // Don't write the distance with the extra bits code since
                    // the distance can be up to 18 bits of extra bits, and the prefix
                    // 15 bits, totaling to 33, and our PutBits only supports up to 32 bits.
                    code = LosslessUtils.PrefixEncode(distance, ref nBits, ref bits);
                    this.bitWriter.WriteHuffmanCode(codes[4], code);
                    this.bitWriter.PutBits((uint)bits, nBits);
                }

                x += v.Length();
                while (x >= width)
                {
                    x -= width;
                    y++;
                }
            }
        }

        /// <summary>
        /// Analyzes the entropy of the input image to determine which transforms to use during encoding the image.
        /// </summary>
        /// <typeparam name="TPixel">The pixel type of the image.</typeparam>
        /// <param name="image">The image to analyze.</param>
        /// <param name="usePalette">Indicates whether a palette should be used.</param>
        /// <param name="paletteSize">The palette size.</param>
        /// <param name="transformBits">The transformation bits.</param>
        /// <param name="redAndBlueAlwaysZero">Indicates if red and blue are always zero.</param>
        /// <returns>The entropy mode to use.</returns>
        private EntropyIx AnalyzeEntropy<TPixel>(Image<TPixel> image, bool usePalette, int paletteSize, int transformBits, out bool redAndBlueAlwaysZero)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            int width = image.Width;
            int height = image.Height;

            if (usePalette && paletteSize <= 16)
            {
                // In the case of small palettes, we pack 2, 4 or 8 pixels together. In
                // practice, small palettes are better than any other transform.
                redAndBlueAlwaysZero = true;
                return EntropyIx.Palette;
            }

            using IMemoryOwner<uint> histoBuffer = this.memoryAllocator.Allocate<uint>((int)HistoIx.HistoTotal * 256);
            Span<uint> histo = histoBuffer.Memory.Span;
            Bgra32 pixPrev = ToBgra32(image.GetPixelRowSpan(0)[0]); // Skip the first pixel.
            Span<TPixel> prevRow = null;
            for (int y = 0; y < height; y++)
            {
                Span<TPixel> currentRow = image.GetPixelRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    Bgra32 pix = ToBgra32(currentRow[x]);
                    uint pixDiff = LosslessUtils.SubPixels(pix.PackedValue, pixPrev.PackedValue);
                    pixPrev = pix;
                    if ((pixDiff == 0) || (prevRow != null && pix == ToBgra32(prevRow[x])))
                    {
                        continue;
                    }

                    AddSingle(
                        pix.PackedValue,
                        histo.Slice((int)HistoIx.HistoAlpha * 256),
                        histo.Slice((int)HistoIx.HistoRed * 256),
                        histo.Slice((int)HistoIx.HistoGreen * 256),
                        histo.Slice((int)HistoIx.HistoBlue * 256));
                    AddSingle(
                        pixDiff,
                        histo.Slice((int)HistoIx.HistoAlphaPred * 256),
                        histo.Slice((int)HistoIx.HistoRedPred * 256),
                        histo.Slice((int)HistoIx.HistoGreenPred * 256),
                        histo.Slice((int)HistoIx.HistoBluePred * 256));
                    AddSingleSubGreen(
                        pix.PackedValue,
                        histo.Slice((int)HistoIx.HistoRedSubGreen * 256),
                        histo.Slice((int)HistoIx.HistoBlueSubGreen * 256));
                    AddSingleSubGreen(
                        pixDiff,
                        histo.Slice((int)HistoIx.HistoRedPredSubGreen * 256),
                        histo.Slice((int)HistoIx.HistoBluePredSubGreen * 256));

                    // Approximate the palette by the entropy of the multiplicative hash.
                    uint hash = HashPix(pix.PackedValue);
                    histo[((int)HistoIx.HistoPalette * 256) + (int)hash]++;
                }

                prevRow = currentRow;
            }

            var entropyComp = new double[(int)HistoIx.HistoTotal];
            var entropy = new double[(int)EntropyIx.NumEntropyIx];
            int lastModeToAnalyze = usePalette ? (int)EntropyIx.Palette : (int)EntropyIx.SpatialSubGreen;

            // Let's add one zero to the predicted histograms. The zeros are removed
            // too efficiently by the pixDiff == 0 comparison, at least one of the
            // zeros is likely to exist.
            histo[(int)HistoIx.HistoRedPredSubGreen * 256]++;
            histo[(int)HistoIx.HistoBluePredSubGreen * 256]++;
            histo[(int)HistoIx.HistoRedPred * 256]++;
            histo[(int)HistoIx.HistoGreenPred * 256]++;
            histo[(int)HistoIx.HistoBluePred * 256]++;
            histo[(int)HistoIx.HistoAlphaPred * 256]++;

            for (int j = 0; j < (int)HistoIx.HistoTotal; ++j)
            {
                var bitEntropy = new Vp8LBitEntropy();
                Span<uint> curHisto = histo.Slice(j * 256, 256);
                bitEntropy.BitsEntropyUnrefined(curHisto, 256);
                entropyComp[j] = bitEntropy.BitsEntropyRefine();
            }

            entropy[(int)EntropyIx.Direct] = entropyComp[(int)HistoIx.HistoAlpha] +
                                             entropyComp[(int)HistoIx.HistoRed] +
                                             entropyComp[(int)HistoIx.HistoGreen] +
                                             entropyComp[(int)HistoIx.HistoBlue];
            entropy[(int)EntropyIx.Spatial] = entropyComp[(int)HistoIx.HistoAlphaPred] +
                                              entropyComp[(int)HistoIx.HistoRedPred] +
                                              entropyComp[(int)HistoIx.HistoGreenPred] +
                                              entropyComp[(int)HistoIx.HistoBluePred];
            entropy[(int)EntropyIx.SubGreen] = entropyComp[(int)HistoIx.HistoAlpha] +
                                               entropyComp[(int)HistoIx.HistoRedSubGreen] +
                                               entropyComp[(int)HistoIx.HistoGreen] +
                                               entropyComp[(int)HistoIx.HistoBlueSubGreen];
            entropy[(int)EntropyIx.SpatialSubGreen] = entropyComp[(int)HistoIx.HistoAlphaPred] +
                                                      entropyComp[(int)HistoIx.HistoRedPredSubGreen] +
                                                      entropyComp[(int)HistoIx.HistoGreenPred] +
                                                      entropyComp[(int)HistoIx.HistoBluePredSubGreen];
            entropy[(int)EntropyIx.Palette] = entropyComp[(int)HistoIx.HistoPalette];

            // When including transforms, there is an overhead in bits from
            // storing them. This overhead is small but matters for small images.
            // For spatial, there are 14 transformations.
            entropy[(int)EntropyIx.Spatial] += LosslessUtils.SubSampleSize(width, transformBits) *
                                               LosslessUtils.SubSampleSize(height, transformBits) *
                                               LosslessUtils.FastLog2(14);

            // For color transforms: 24 as only 3 channels are considered in a ColorTransformElement.
            entropy[(int)EntropyIx.SpatialSubGreen] += LosslessUtils.SubSampleSize(width, transformBits) *
                                                       LosslessUtils.SubSampleSize(height, transformBits) *
                                                       LosslessUtils.FastLog2(24);

            // For palettes, add the cost of storing the palette.
            // We empirically estimate the cost of a compressed entry as 8 bits.
            // The palette is differential-coded when compressed hence a much
            // lower cost than sizeof(uint32_t)*8.
            entropy[(int)EntropyIx.Palette] += paletteSize * 8;

            EntropyIx minEntropyIx = EntropyIx.Direct;
            for (int k = (int)EntropyIx.Direct + 1; k <= lastModeToAnalyze; k++)
            {
                if (entropy[(int)minEntropyIx] > entropy[k])
                {
                    minEntropyIx = (EntropyIx)k;
                }
            }

            redAndBlueAlwaysZero = true;

            // Let's check if the histogram of the chosen entropy mode has
            // non-zero red and blue values. If all are zero, we can later skip
            // the cross color optimization.
            var histoPairs = new byte[][]
            {
                new byte[] { (byte)HistoIx.HistoRed, (byte)HistoIx.HistoBlue },
                new byte[] { (byte)HistoIx.HistoRedPred, (byte)HistoIx.HistoBluePred },
                new byte[] { (byte)HistoIx.HistoRedSubGreen, (byte)HistoIx.HistoBlueSubGreen },
                new byte[] { (byte)HistoIx.HistoRedPredSubGreen, (byte)HistoIx.HistoBluePredSubGreen },
                new byte[] { (byte)HistoIx.HistoRed, (byte)HistoIx.HistoBlue }
            };
            Span<uint> redHisto = histo.Slice(256 * histoPairs[(int)minEntropyIx][0]);
            Span<uint> blueHisto = histo.Slice(256 * histoPairs[(int)minEntropyIx][1]);
            for (int i = 1; i < 256; i++)
            {
                if ((redHisto[i] | blueHisto[i]) != 0)
                {
                    redAndBlueAlwaysZero = false;
                    break;
                }
            }

            return minEntropyIx;
        }

        /// <summary>
        /// If number of colors in the image is less than or equal to MAX_PALETTE_SIZE,
        /// creates a palette and returns true, else returns false.
        /// </summary>
        /// <returns>true, if a palette should be used.</returns>
        private bool AnalyzeAndCreatePalette<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            Span<uint> palette = this.Palette.Memory.Span;
            this.PaletteSize = this.GetColorPalette(image, palette);
            if (this.PaletteSize > WebPConstants.MaxPaletteSize)
            {
                this.PaletteSize = 0;
                return false;
            }

            uint[] paletteArray = palette.Slice(0, this.PaletteSize).ToArray();
            Array.Sort(paletteArray);
            paletteArray.CopyTo(palette);

            if (PaletteHasNonMonotonousDeltas(palette, this.PaletteSize))
            {
                GreedyMinimizeDeltas(palette, this.PaletteSize);
            }

            return true;
        }

        /// <summary>
        /// Gets the color palette.
        /// </summary>
        /// <typeparam name="TPixel">The pixel type of the image.</typeparam>
        /// <param name="image">The image to get the palette from.</param>
        /// <param name="palette">The span to store the palette into.</param>
        /// <returns>The number of palette entries.</returns>
        private int GetColorPalette<TPixel>(Image<TPixel> image, Span<uint> palette)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var colors = new HashSet<TPixel>();
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> rowSpan = image.GetPixelRowSpan(y);
                for (int x = 0; x < rowSpan.Length; x++)
                {
                    colors.Add(rowSpan[x]);
                    if (colors.Count > WebPConstants.MaxPaletteSize)
                    {
                        // Exact count is not needed, because a palette will not be used then anyway.
                        return WebPConstants.MaxPaletteSize + 1;
                    }
                }
            }

            // Fill the colors into the palette.
            using HashSet<TPixel>.Enumerator colorEnumerator = colors.GetEnumerator();
            int idx = 0;
            while (colorEnumerator.MoveNext())
            {
                Bgra32 bgra = ToBgra32(colorEnumerator.Current);
                palette[idx++] = bgra.PackedValue;
            }

            return colors.Count;
        }

        private void MapImageFromPalette(int width, int height)
        {
            Span<uint> src = this.Bgra.GetSpan();
            int srcStride = this.CurrentWidth;
            Span<uint> dst = this.Bgra.GetSpan(); // Applying the palette will be done in place.
            Span<uint> palette = this.Palette.GetSpan();
            int paletteSize = this.PaletteSize;
            int xBits;

            // Replace each input pixel by corresponding palette index.
            // This is done line by line.
            if (paletteSize <= 4)
            {
                xBits = (paletteSize <= 2) ? 3 : 2;
            }
            else
            {
                xBits = (paletteSize <= 16) ? 1 : 0;
            }

            this.ApplyPalette(src, srcStride, dst, this.CurrentWidth, palette, paletteSize, width, height, xBits);
        }

        /// <summary>
        /// Remap argb values in src[] to packed palettes entries in dst[]
        /// using 'row' as a temporary buffer of size 'width'.
        /// We assume that all src[] values have a corresponding entry in the palette.
        /// Note: src[] can be the same as dst[]
        /// </summary>
        private void ApplyPalette(Span<uint> src, int srcStride, Span<uint> dst, int dstStride, Span<uint> palette, int paletteSize, int width, int height, int xBits)
        {
            using IMemoryOwner<byte> tmpRowBuffer = this.memoryAllocator.Allocate<byte>(width);
            Span<byte> tmpRow = tmpRowBuffer.GetSpan();

            if (paletteSize < ApplyPaletteGreedyMax)
            {
                // TODO: APPLY_PALETTE_FOR(SearchColorGreedy(palette, palette_size, pix));
            }
            else
            {
                uint[] buffer = new uint[PaletteInvSize];

                // Try to find a perfect hash function able to go from a color to an index
                // within 1 << PaletteInvSize in order to build a hash map to go from color to index in palette.
                int i;
                for (i = 0; i < 3; i++)
                {
                    bool useLUT = true;

                    // Set each element in buffer to max value.
                    buffer.AsSpan().Fill(uint.MaxValue);

                    for (int j = 0; j < paletteSize; j++)
                    {
                        uint ind = 0;
                        switch (i)
                        {
                            case 0:
                                ind = ApplyPaletteHash0(palette[j]);
                                break;
                            case 1:
                                ind = ApplyPaletteHash1(palette[j]);
                                break;
                            case 2:
                                ind = ApplyPaletteHash2(palette[j]);
                                break;
                        }

                        if (buffer[ind] != uint.MaxValue)
                        {
                            useLUT = false;
                            break;
                        }
                        else
                        {
                            buffer[ind] = (uint)j;
                        }
                    }

                    if (useLUT)
                    {
                        break;
                    }
                }

                if (i == 0 || i == 1 || i == 2)
                {
                    ApplyPaletteFor(width, height, palette, i, src, srcStride, dst, dstStride, tmpRow, buffer, xBits);
                }
                else
                {
                    var idxMap = new uint[paletteSize];
                    var paletteSorted = new uint[paletteSize];
                    PrepareMapToPalette(palette, paletteSize, paletteSorted, idxMap);
                    ApplyPaletteForWithIdxMap(width, height, palette, src, srcStride, dst, dstStride, tmpRow, idxMap, xBits, paletteSorted, paletteSize);
                }
            }
        }

        private static void ApplyPaletteFor(int width, int height, Span<uint> palette, int hashIdx, Span<uint> src, int srcStride, Span<uint> dst, int dstStride, Span<byte> tmpRow, uint[] buffer, int xBits)
        {
            uint prevPix = palette[0];
            uint prevIdx = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint pix = src[x];
                    if (pix != prevPix)
                    {
                        switch (hashIdx)
                        {
                            case 0:
                                prevIdx = buffer[ApplyPaletteHash0(pix)];
                                break;
                            case 1:
                                prevIdx = buffer[ApplyPaletteHash1(pix)];
                                break;
                            case 2:
                                prevIdx = buffer[ApplyPaletteHash2(pix)];
                                break;
                        }

                        prevPix = pix;
                    }

                    tmpRow[x] = (byte)prevIdx;
                }

                LosslessUtils.BundleColorMap(tmpRow, width, xBits, dst);

                src = src.Slice((int)srcStride);
                dst = dst.Slice((int)dstStride);
            }
        }

        private static void ApplyPaletteForWithIdxMap(int width, int height, Span<uint> palette, Span<uint> src, int srcStride, Span<uint> dst, int dstStride, Span<byte> tmpRow, uint[] idxMap, int xBits, uint[] paletteSorted, int paletteSize)
        {
            uint prevPix = palette[0];
            uint prevIdx = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint pix = src[x];
                    if (pix != prevPix)
                    {
                        prevIdx = idxMap[SearchColorNoIdx(paletteSorted, pix, paletteSize)];
                        prevPix = pix;
                    }

                    tmpRow[x] = (byte)prevIdx;
                }

                LosslessUtils.BundleColorMap(tmpRow, width, xBits, dst);

                src = src.Slice((int)srcStride);
                dst = dst.Slice((int)dstStride);
            }
        }

        /// <summary>
        /// Sort palette in increasing order and prepare an inverse mapping array.
        /// </summary>
        private static void PrepareMapToPalette(Span<uint> palette, int numColors, uint[] sorted, uint[] idxMap)
        {
            palette.Slice(numColors).CopyTo(sorted);
            Array.Sort(sorted, PaletteCompareColorsForSort);
            for (int i = 0; i < numColors; i++)
            {
                idxMap[SearchColorNoIdx(sorted, palette[i], numColors)] = (uint)i;
            }
        }

        private static int SearchColorNoIdx(uint[] sorted, uint color, int hi)
        {
            int low = 0;
            if (sorted[low] == color)
            {
                return low;  // loop invariant: sorted[low] != color
            }

            while (true)
            {
                int mid = (low + hi) >> 1;
                if (sorted[mid] == color)
                {
                    return mid;
                }
                else if (sorted[mid] < color)
                {
                    low = mid;
                }
                else
                {
                    hi = mid;
                }
            }
        }

        private static void ClearHuffmanTreeIfOnlyOneSymbol(HuffmanTreeCode huffmanCode)
        {
            int count = 0;
            for (int k = 0; k < huffmanCode.NumSymbols; k++)
            {
                if (huffmanCode.CodeLengths[k] != 0)
                {
                    count++;
                    if (count > 1)
                    {
                        return;
                    }
                }
            }

            for (int k = 0; k < huffmanCode.NumSymbols; k++)
            {
                huffmanCode.CodeLengths[k] = 0;
                huffmanCode.Codes[k] = 0;
            }
        }

        /// <summary>
        /// The palette has been sorted by alpha. This function checks if the other components of the palette
        /// have a monotonic development with regards to position in the palette.
        /// If all have monotonic development, there is no benefit to re-organize them greedily. A monotonic development
        /// would be spotted in green-only situations (like lossy alpha) or gray-scale images.
        /// </summary>
        /// <param name="palette">The palette.</param>
        /// <param name="numColors">Number of colors in the palette.</param>
        /// <returns>True, if the palette has no monotonous deltas.</returns>
        private static bool PaletteHasNonMonotonousDeltas(Span<uint> palette, int numColors)
        {
            uint predict = 0x000000;
            byte signFound = 0x00;
            for (int i = 0; i < numColors; ++i)
            {
                uint diff = LosslessUtils.SubPixels(palette[i], predict);
                byte rd = (byte)((diff >> 16) & 0xff);
                byte gd = (byte)((diff >> 8) & 0xff);
                byte bd = (byte)((diff >> 0) & 0xff);
                if (rd != 0x00)
                {
                    signFound |= (byte)((rd < 0x80) ? 1 : 2);
                }

                if (gd != 0x00)
                {
                    signFound |= (byte)((gd < 0x80) ? 8 : 16);
                }

                if (bd != 0x00)
                {
                    signFound |= (byte)((bd < 0x80) ? 64 : 128);
                }
            }

            return (signFound & (signFound << 1)) != 0;  // two consequent signs.
        }

        /// <summary>
        /// Find greedily always the closest color of the predicted color to minimize
        /// deltas in the palette. This reduces storage needs since the palette is stored with delta encoding.
        /// </summary>
        /// <param name="palette">The palette.</param>
        /// <param name="numColors">The number of colors in the palette.</param>
        private static void GreedyMinimizeDeltas(Span<uint> palette, int numColors)
        {
            uint predict = 0x00000000;
            for (int i = 0; i < numColors; ++i)
            {
                int bestIdx = i;
                uint bestScore = ~0U;
                for (int k = i; k < numColors; ++k)
                {
                    uint curScore = PaletteColorDistance(palette[k], predict);
                    if (bestScore > curScore)
                    {
                        bestScore = curScore;
                        bestIdx = k;
                    }
                }

                // Swap color(palette[bestIdx], palette[i]);
                uint best = palette[bestIdx];
                palette[bestIdx] = palette[i];
                palette[i] = best;
                predict = palette[i];
            }
        }

        private static void GetHuffBitLengthsAndCodes(List<Vp8LHistogram> histogramImage, HuffmanTreeCode[] huffmanCodes)
        {
            long totalLengthSize = 0;
            int maxNumSymbols = 0;

            // Iterate over all histograms and get the aggregate number of codes used.
            for (int i = 0; i < histogramImage.Count; i++)
            {
                Vp8LHistogram histo = histogramImage[i];
                int startIdx = 5 * i;
                for (int k = 0; k < 5; k++)
                {
                    int numSymbols =
                        (k == 0) ? histo.NumCodes() :
                        (k == 4) ? WebPConstants.NumDistanceCodes : 256;
                    huffmanCodes[startIdx + k].NumSymbols = numSymbols;
                    totalLengthSize += numSymbols;
                }
            }

            var end = 5 * histogramImage.Count;
            for (int i = 0; i < end; i++)
            {
                int bitLength = huffmanCodes[i].NumSymbols;
                huffmanCodes[i].Codes = new short[bitLength];
                huffmanCodes[i].CodeLengths = new byte[bitLength];
                if (maxNumSymbols < bitLength)
                {
                    maxNumSymbols = bitLength;
                }
            }

            // Create Huffman trees.
            var bufRle = new bool[maxNumSymbols];
            var huffTree = new HuffmanTree[3 * maxNumSymbols];
            for (int i = 0; i < huffTree.Length; i++)
            {
                huffTree[i] = new HuffmanTree();
            }

            for (int i = 0; i < histogramImage.Count; i++)
            {
                int codesStartIdx = 5 * i;
                Vp8LHistogram histo = histogramImage[i];
                HuffmanUtils.CreateHuffmanTree(histo.Literal, 15, bufRle, huffTree, huffmanCodes[codesStartIdx]);
                HuffmanUtils.CreateHuffmanTree(histo.Red, 15, bufRle, huffTree, huffmanCodes[codesStartIdx + 1]);
                HuffmanUtils.CreateHuffmanTree(histo.Blue, 15, bufRle, huffTree, huffmanCodes[codesStartIdx + 2]);
                HuffmanUtils.CreateHuffmanTree(histo.Alpha, 15, bufRle, huffTree, huffmanCodes[codesStartIdx + 3]);
                HuffmanUtils.CreateHuffmanTree(histo.Distance, 15, bufRle, huffTree, huffmanCodes[codesStartIdx + 4]);
            }
        }

        /// <summary>
        /// Computes a value that is related to the entropy created by the palette entry diff.
        /// </summary>
        /// <param name="col1">First color.</param>
        /// <param name="col2">Second color.</param>
        /// <returns>The color distance.</returns>
        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint PaletteColorDistance(uint col1, uint col2)
        {
            uint diff = LosslessUtils.SubPixels(col1, col2);
            uint moreWeightForRGBThanForAlpha = 9;
            uint score = PaletteComponentDistance((diff >> 0) & 0xff);
            score += PaletteComponentDistance((diff >> 8) & 0xff);
            score += PaletteComponentDistance((diff >> 16) & 0xff);
            score *= moreWeightForRGBThanForAlpha;
            score += PaletteComponentDistance((diff >> 24) & 0xff);

            return score;
        }

        /// <summary>
        /// Calculates the huffman image bits.
        /// </summary>
        private static int GetHistoBits(int method, bool usePalette, int width, int height)
        {
            // Make tile size a function of encoding method (Range: 0 to 6).
            int histoBits = (usePalette ? 9 : 7) - method;
            while (true)
            {
                int huffImageSize = LosslessUtils.SubSampleSize(width, histoBits) * LosslessUtils.SubSampleSize(height, histoBits);
                if (huffImageSize <= WebPConstants.MaxHuffImageSize)
                {
                    break;
                }

                histoBits++;
            }

            return (histoBits < WebPConstants.MinHuffmanBits) ? WebPConstants.MinHuffmanBits :
                (histoBits > WebPConstants.MaxHuffmanBits) ? WebPConstants.MaxHuffmanBits : histoBits;
        }

        /// <summary>
        /// Calculates the bits used for the transformation.
        /// </summary>
        [MethodImpl(InliningOptions.ShortMethod)]
        private static int GetTransformBits(int method, int histoBits)
        {
            int maxTransformBits = (method < 4) ? 6 : (method > 4) ? 4 : 5;
            int res = (histoBits > maxTransformBits) ? maxTransformBits : histoBits;
            return res;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static Bgra32 ToBgra32<TPixel>(TPixel color)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            Rgba32 rgba = default;
            color.ToRgba32(ref rgba);
            var bgra = new Bgra32(rgba.R, rgba.G, rgba.B, rgba.A);
            return bgra;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static void AddSingle(uint p, Span<uint> a, Span<uint> r, Span<uint> g, Span<uint> b)
        {
            a[(int)(p >> 24) & 0xff]++;
            r[(int)(p >> 16) & 0xff]++;
            g[(int)(p >> 8) & 0xff]++;
            b[(int)(p >> 0) & 0xff]++;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static void AddSingleSubGreen(uint p, Span<uint> r, Span<uint> b)
        {
            int green = (int)p >> 8;  // The upper bits are masked away later.
            r[(int)((p >> 16) - green) & 0xff]++;
            b[(int)((p >> 0) - green) & 0xff]++;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint ApplyPaletteHash0(uint color)
        {
            // Focus on the green color.
            return (color >> 8) & 0xff;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint ApplyPaletteHash1(uint color)
        {
            // Forget about alpha.
            return ((uint)((color & 0x00ffffffu) * 4222244071ul)) >> (32 - PaletteInvSizeBits);
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint ApplyPaletteHash2(uint color)
        {
            // Forget about alpha.
            return ((uint)((color & 0x00ffffffu) * ((1ul << 31) - 1))) >> (32 - PaletteInvSizeBits);
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint HashPix(uint pix)
        {
            // Note that masking with 0xffffffffu is for preventing an
            // 'unsigned int overflow' warning. Doesn't impact the compiled code.
            return (uint)((((long)pix + (pix >> 19)) * 0x39c5fba7L) & 0xffffffffu) >> 24;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static int PaletteCompareColorsForSort(uint p1, uint p2)
        {
            return (p1 < p2) ? -1 : 1;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static uint PaletteComponentDistance(uint v)
        {
            return (v <= 128) ? v : (256 - v);
        }

        public void AllocateTransformBuffer(int width, int height)
        {
            // VP8LResidualImage needs room for 2 scanlines of uint32 pixels with an extra
            // pixel in each, plus 2 regular scanlines of bytes.
            int argbScratchSize = this.UsePredictorTransform ? ((width + 1) * 2) + (((width * 2) + 4 - 1) / 4) : 0;
            int transformDataSize = (this.UsePredictorTransform || this.UseCrossColorTransform) ? LosslessUtils.SubSampleSize(width, this.TransformBits) * LosslessUtils.SubSampleSize(height, this.TransformBits) : 0;

            this.BgraScratch = this.memoryAllocator.Allocate<uint>(argbScratchSize);
            this.TransformData = this.memoryAllocator.Allocate<uint>(transformDataSize);
            this.CurrentWidth = width;
        }

        /// <summary>
        /// Clears the backward references.
        /// </summary>
        public void ClearRefs()
        {
            for (int i = 0; i < this.Refs.Length; i++)
            {
                this.Refs[i].Refs.Clear();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Bgra.Dispose();
            this.BgraScratch.Dispose();
            this.Palette.Dispose();
            this.TransformData.Dispose();
        }

        // TODO : Not a fan of private classes
        private class CrunchConfig
        {
            public EntropyIx EntropyIdx { get; set; }

            public List<CrunchSubConfig> SubConfigs { get; } = new List<CrunchSubConfig>();
        }

        // TODO : Not a fan of private classes
        private class CrunchSubConfig
        {
            public int Lz77 { get; set; }

            public bool DoNotCache { get; set; }
        }
    }
}
