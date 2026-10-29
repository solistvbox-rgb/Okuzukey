using System;

namespace Emby.Okuzukey.Helpers
{
    /// <summary>
    /// Range checks for numbers read from metadata files/Komga. <c>double.TryParse</c> with
    /// <c>NumberStyles.Float</c> also accepts <c>"NaN"</c>, <c>"Infinity"</c> and <c>"1e300"</c>;
    /// cast to <c>int</c> those become meaningless values (typically <c>int.MinValue</c>) and a
    /// <c>NaN</c> community rating breaks sorting.
    /// </summary>
    internal static class SafeNumbers
    {
        /// <summary>Largest issue/volume/series index accepted as <c>IndexNumber</c>.</summary>
        private const double MaxIndexNumber = 1000000;

        /// <summary>
        /// Rounds <paramref name="value"/> (away from zero, e.g. a series index of 1.5 becomes 2)
        /// to an <c>IndexNumber</c>, if it is a finite number between 0 and one million.
        /// </summary>
        public static bool TryToIndexNumber(double value, out int indexNumber)
        {
            indexNumber = 0;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > MaxIndexNumber)
            {
                return false;
            }

            indexNumber = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            return true;
        }

        /// <summary>
        /// Converts <paramref name="value"/> on a 0..<paramref name="scaleMax"/> scale to Emby's
        /// 0-10 community rating, if it is a finite number within that scale.
        /// </summary>
        public static bool TryToCommunityRating(float value, float scaleMax, out float rating)
        {
            rating = 0;
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > scaleMax)
            {
                return false;
            }

            rating = value * (10f / scaleMax);
            return true;
        }
    }
}
