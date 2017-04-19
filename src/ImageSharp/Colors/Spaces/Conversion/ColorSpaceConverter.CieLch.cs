﻿// <copyright file="ColorSpaceConverter.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Colors.Spaces.Conversion
{
    using ImageSharp.Colors.Spaces.Conversion.Implementation.CieLch;

    /// <summary>
    /// Converts between color spaces ensuring that the color is adapted using chromatic adaptation.
    /// </summary>
    public partial class ColorSpaceConverter
    {
        /// <summary>
        /// The converter for converting between CieLab to CieLch.
        /// </summary>
        private static readonly CieLabToCieLchConverter CieLabToCieLchConverter = new CieLabToCieLchConverter();

        /// <summary>
        /// Converts a <see cref="CieXyz"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(CieXyz color)
        {
            Guard.NotNull(color, nameof(color));

            CieLab labColor = this.ToCieLab(color);
            return this.ToCieLch(labColor);
        }

        /// <summary>
        /// Converts a <see cref="Rgb"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(Rgb color)
        {
            Guard.NotNull(color, nameof(color));

            CieLab labColor = this.ToCieLab(color);
            return this.ToCieLch(labColor);
        }

        /// <summary>
        /// Converts a <see cref="LinearRgb"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(LinearRgb color)
        {
            Guard.NotNull(color, nameof(color));

            CieLab labColor = this.ToCieLab(color);
            return this.ToCieLch(labColor);
        }

        /// <summary>
        /// Converts a <see cref="CieLab"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(CieLab color)
        {
            Guard.NotNull(color, nameof(color));

            // Adaptation
            CieLab adapted = this.IsChromaticAdaptationPerformed ? this.Adapt(color) : color;

            // Conversion
            return CieLabToCieLchConverter.Convert(adapted);
        }

        /// <summary>
        /// Converts a <see cref="Lms"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(Lms color)
        {
            Guard.NotNull(color, nameof(color));

            CieLab labColor = this.ToCieLab(color);
            return this.ToCieLch(labColor);
        }

        /// <summary>
        /// Converts a <see cref="HunterLab"/> into a <see cref="CieLch"/>
        /// </summary>
        /// <param name="color">The color to convert.</param>
        /// <returns>The <see cref="CieLch"/></returns>
        public CieLch ToCieLch(HunterLab color)
        {
            Guard.NotNull(color, nameof(color));

            CieLab labColor = this.ToCieLab(color);
            return this.ToCieLch(labColor);
        }
    }
}