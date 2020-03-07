// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Processing.Processors.Quantization
{
    // Based on https://www.drdobbs.com/3-d-lookup-table-color-matching/184403257
    // TODO: Add a new alpha component to the map with an accuracy of 5 bits.
    // The length of the match array becomes rval * gval * bval * aval
    // Will need help figuring out the new distribution and indexing rules.
    internal class PaletteMap<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private static readonly int Rbits = 5;
        private static readonly int Gbits = 6;
        private static readonly int Bbits = 5;
        private readonly ReadOnlyMemory<TPixel> palette;
        private readonly byte[] match;

        public PaletteMap(ReadOnlyMemory<TPixel> palette)
        {
            long rval, gval, bval, len;
            int r, g, b;
            int sqstep;
            long tp, maxtp;
            byte[] taken;

            rval = 1 << Rbits;
            gval = 1 << Gbits;
            bval = 1 << Bbits;

            len = rval * gval * bval;

            this.match = new byte[len];
            taken = new byte[len];

            // Select colors to use for fill
            long set = 0;
            this.palette = palette;
            ReadOnlySpan<TPixel> paletteSpan = palette.Span;
            int paletteSize = paletteSpan.Length;

            int[] entryr = new int[paletteSize];
            int[] entryg = new int[paletteSize];
            int[] entryb = new int[paletteSize];
            int[] same = new int[paletteSize];

            // Compute 3d-table coordinates of palette rgb color
            Rgba32 rgba = default;
            for (int i = 0; i < paletteSize; i++)
            {
                same[i] = 0;
                paletteSpan[i].ToRgba32(ref rgba);

                r = rgba.R;
                g = rgba.G;
                b = rgba.B;

                r >>= 8 - Rbits;
                g >>= 8 - Gbits;
                b >>= 8 - Bbits;

                // Put color in position
                if (taken[(b * rval * gval) + (g * rval) + r] == 0)
                {
                    set++;
                }
                else
                {
                    same[this.match[(b * rval * gval) + (g * rval) + r]] = 1;
                }

                this.match[(b * rval * gval) + (g * rval) + r] = (byte)i;
                taken[(b * rval * gval) + (g * rval) + r] = 1;
                entryr[i] = (byte)r;
                entryg[i] = (byte)g;
                entryb[i] = (byte)b;
            }

            /* @@@ Fill match_array by steps: @@@ */
            for (set = len - set, sqstep = 1; set > 0; sqstep++)
            {
                for (int i = 0; i < paletteSize && set > 0; i++)
                {
                    if (same[i] == 0)
                    {
                        // Fill all six sides of incremented cube (by pairs, 3 loops):
                        for (b = entryb[i] - sqstep; b <= entryb[i] + sqstep; b += sqstep * 2)
                        {
                            if (b >= 0 && b < bval)
                            {
                                for (r = entryr[i] - sqstep; r <= entryr[i] + sqstep; r++)
                                {
                                    if (r >= 0 && r < rval)
                                    {
                                        // Draw one 3d line:
                                        tp = (b * rval * gval) + ((entryg[i] - sqstep) * rval) + r;
                                        maxtp = (b * rval * gval) + ((entryg[i] + sqstep) * rval) + r;
                                        if (tp < (b * rval * gval) + (0 * rval) + r)
                                        {
                                            tp = (b * rval * gval) + (0 * rval) + r;
                                        }

                                        if (maxtp > (b * rval * gval) + ((gval - 1) * rval) + r)
                                        {
                                            maxtp = (b * rval * gval) + ((gval - 1) * rval) + r;
                                        }

                                        for (; tp <= maxtp; tp += rval)
                                        {
                                            if (taken[tp] == 0)
                                            {
                                                taken[tp] = 1;
                                                this.match[tp] = (byte)i;
                                                set--;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        for (g = entryg[i] - sqstep; g <= entryg[i] + sqstep; g += sqstep * 2)
                        {
                            if (g >= 0 && g < gval)
                            {
                                for (b = entryb[i] - sqstep; b <= entryb[i] + sqstep; b++)
                                {
                                    if (b >= 0 && b < bval)
                                    {
                                        // Draw one 3d line:
                                        tp = (b * rval * gval) + (g * rval) + (entryr[i] - sqstep);
                                        maxtp = (b * rval * gval) + (g * rval) + (entryr[i] + sqstep);
                                        if (tp < (b * rval * gval) + (g * rval) + 0)
                                        {
                                            tp = (b * rval * gval) + (g * rval) + 0;
                                        }

                                        if (maxtp > (b * rval * gval) + (g * rval) + (rval - 1))
                                        {
                                            maxtp = (b * rval * gval) + (g * rval) + (rval - 1);
                                        }

                                        for (; tp <= maxtp; tp++)
                                        {
                                            if (taken[tp] == 0)
                                            {
                                                taken[tp] = 1;
                                                this.match[tp] = (byte)i;
                                                set--;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        for (r = entryr[i] - sqstep; r <= entryr[i] + sqstep; r += sqstep * 2)
                        {
                            if (r >= 0 && r < rval)
                            {
                                for (g = entryg[i] - sqstep; g <= entryg[i] + sqstep; g++)
                                {
                                    if (g >= 0 && g < gval)
                                    {
                                        // Draw one 3d line:
                                        tp = ((entryb[i] - sqstep) * rval * gval) + (g * rval) + r;
                                        maxtp = ((entryb[i] + sqstep) * rval * gval) + (g * rval) + r;
                                        if (tp < (0 * rval * gval) + (g * rval) + r)
                                        {
                                            tp = (0 * rval * gval) + (g * rval) + r;
                                        }

                                        if (maxtp > ((bval - 1) * rval * gval) + (g * rval) + r)
                                        {
                                            maxtp = ((bval - 1) * rval * gval) + (g * rval) + r;
                                        }

                                        for (; tp <= maxtp; tp += rval * gval)
                                        {
                                            if (taken[tp] == 0)
                                            {
                                                taken[tp] = 1;
                                                this.match[tp] = (byte)i;
                                                set--;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public byte GetMatch(TPixel source, out TPixel match)
        {
            Rgba32 rgba = default;
            source.ToRgba32(ref rgba);
            int r = rgba.R;
            int g = rgba.G;
            int b = rgba.B;

            r >>= 8 - Rbits;
            g >>= 8 - Gbits;
            b >>= 8 - Bbits;

            byte result = this.match[(b << Gbits << Rbits) + (g << Rbits) + r];
            match = this.palette.Span[result];
            return result;
        }
    }
}
