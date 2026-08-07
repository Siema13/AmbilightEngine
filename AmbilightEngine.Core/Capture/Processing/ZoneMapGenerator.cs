using System;
using System.Collections.Generic;
using System.Linq;

namespace AmbilightEngine.Core.Processing
{
    public static class ZoneMapGenerator
    {
        public static CaptureZone[] Generate(
            int screenWidth,
            int screenHeight,
            int totalLedCount,
            int samplingDepth = 80,
            StartCorner startCorner = StartCorner.BottomLeft,
            StripDirection direction = StripDirection.Clockwise,
            int offsetX = 0,
            int offsetY = 0)
        {
            if (screenWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(screenWidth));
            }

            if (screenHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(screenHeight));
            }

            if (totalLedCount <= 0)
            {
                throw new ArgumentException(
                    "Liczba diod musi być większa od zera.",
                    nameof(totalLedCount));
            }

            double perimeter = 2 * (screenWidth + screenHeight);

            int topCount = (int)Math.Round(
                totalLedCount * (screenWidth / perimeter));

            int bottomCount = topCount;
            int sideCount = (totalLedCount - topCount - bottomCount) / 2;

            int leftCount = sideCount;
            int rightCount = totalLedCount - topCount - bottomCount - leftCount;

            return Generate(
                screenWidth,
                screenHeight,
                topCount,
                bottomCount,
                leftCount,
                rightCount,
                samplingDepth,
                startCorner,
                direction,
                shiftOffset: 0,
                excludedIndices: null,
                offsetX: offsetX,
                offsetY: offsetY);
        }

        public static CaptureZone[] Generate(
            int screenWidth,
            int screenHeight,
            int topCount,
            int bottomCount,
            int leftCount,
            int rightCount,
            int samplingDepth = 80,
            StartCorner startCorner = StartCorner.BottomLeft,
            StripDirection direction = StripDirection.Clockwise,
            int shiftOffset = 0,
            IReadOnlyCollection<int>? excludedIndices = null,
            int offsetX = 0,
            int offsetY = 0)
        {
            if (screenWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(screenWidth));
            }

            if (screenHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(screenHeight));
            }

            if (topCount < 0 ||
                bottomCount < 0 ||
                leftCount < 0 ||
                rightCount < 0)
            {
                throw new ArgumentException(
                    "Liczba diod na krawędzi nie może być ujemna.");
            }

            int totalLedCount = topCount + bottomCount + leftCount + rightCount;

            if (totalLedCount <= 0)
            {
                throw new ArgumentException(
                    "Suma diod na wszystkich krawędziach musi być większa od zera.");
            }

            if (samplingDepth <= 0)
            {
                throw new ArgumentException(
                    "Głębokość próbkowania musi być większa od zera.",
                    nameof(samplingDepth));
            }

            samplingDepth = Math.Min(
                samplingDepth,
                Math.Min(screenWidth, screenHeight));

            var topZones = BuildEdge(
                offsetX,
                offsetY,
                screenWidth,
                samplingDepth,
                topCount,
                horizontal: true);

            var rightZones = BuildEdge(
                offsetX + screenWidth - samplingDepth,
                offsetY,
                samplingDepth,
                screenHeight,
                rightCount,
                horizontal: false);

            var bottomZonesRaw = BuildEdge(
                offsetX,
                offsetY + screenHeight - samplingDepth,
                screenWidth,
                samplingDepth,
                bottomCount,
                horizontal: true);

            var leftZonesRaw = BuildEdge(
                offsetX,
                offsetY,
                samplingDepth,
                screenHeight,
                leftCount,
                horizontal: false);

            var bottomZones = new List<CaptureZone>(bottomZonesRaw);
            bottomZones.Reverse();

            var leftZones = new List<CaptureZone>(leftZonesRaw);
            leftZones.Reverse();

            var ordered = BuildOrderedChain(
                topZones,
                rightZones,
                bottomZones,
                leftZones,
                startCorner);

            if (direction == StripDirection.CounterClockwise)
            {
                ordered.Reverse();
            }

            if (shiftOffset != 0)
            {
                ordered = ApplyShift(ordered, shiftOffset);
            }

            if (excludedIndices is not null && excludedIndices.Count > 0)
            {
                ApplyExclusions(ordered, excludedIndices);
            }

            return ordered.ToArray();
        }

        private static List<CaptureZone> BuildOrderedChain(
            List<CaptureZone> topZones,
            List<CaptureZone> rightZones,
            List<CaptureZone> bottomZones,
            List<CaptureZone> leftZones,
            StartCorner startCorner)
        {
            var baseChain = new List<CaptureZone>();

            baseChain.AddRange(topZones);
            baseChain.AddRange(rightZones);
            baseChain.AddRange(bottomZones);
            baseChain.AddRange(leftZones);

            return startCorner switch
            {
                StartCorner.TopLeft => baseChain,

                StartCorner.TopRight => RotateChain(
                    baseChain,
                    topZones.Count),

                StartCorner.BottomRight => RotateChain(
                    baseChain,
                    topZones.Count + rightZones.Count),

                StartCorner.BottomLeft => RotateChain(
                    baseChain,
                    topZones.Count + rightZones.Count + bottomZones.Count),

                _ => baseChain
            };
        }

        private static List<CaptureZone> RotateChain(
            List<CaptureZone> zones,
            int rotateBy)
        {
            if (zones.Count == 0)
            {
                return zones;
            }

            int normalizedRotation =
                ((rotateBy % zones.Count) + zones.Count) % zones.Count;

            if (normalizedRotation == 0)
            {
                return zones;
            }

            var rotated = new List<CaptureZone>(zones.Count);

            rotated.AddRange(zones.Skip(normalizedRotation));
            rotated.AddRange(zones.Take(normalizedRotation));

            return rotated;
        }

        private static List<CaptureZone> ApplyShift(
            List<CaptureZone> zones,
            int shiftOffset)
        {
            if (zones.Count == 0)
            {
                return zones;
            }

            int normalizedShift =
                ((shiftOffset % zones.Count) + zones.Count) % zones.Count;

            if (normalizedShift == 0)
            {
                return zones;
            }

            var shifted = new List<CaptureZone>(zones.Count);

            shifted.AddRange(zones.Skip(normalizedShift));
            shifted.AddRange(zones.Take(normalizedShift));

            return shifted;
        }

        private static void ApplyExclusions(
            List<CaptureZone> zones,
            IReadOnlyCollection<int> excludedIndices)
        {
            foreach (int index in excludedIndices)
            {
                if (index < 0 || index >= zones.Count)
                {
                    continue;
                }

                zones[index] = CaptureZone.CreateDeadZone(
                    zones[index].X,
                    zones[index].Y);
            }
        }

        private static List<CaptureZone> BuildEdge(
            int startX,
            int startY,
            int totalWidth,
            int totalHeight,
            int count,
            bool horizontal)
        {
            var zones = new List<CaptureZone>();

            if (count <= 0)
            {
                return zones;
            }

            if (horizontal)
            {
                int baseSegmentWidth = totalWidth / count;
                int remainder = totalWidth % count;
                int currentX = startX;

                for (int index = 0; index < count; index++)
                {
                    int zoneWidth = baseSegmentWidth +
                        (index < remainder ? 1 : 0);

                    zones.Add(new CaptureZone(
                        currentX,
                        startY,
                        zoneWidth,
                        totalHeight));

                    currentX += zoneWidth;
                }
            }
            else
            {
                int baseSegmentHeight = totalHeight / count;
                int remainder = totalHeight % count;
                int currentY = startY;

                for (int index = 0; index < count; index++)
                {
                    int zoneHeight = baseSegmentHeight +
                        (index < remainder ? 1 : 0);

                    zones.Add(new CaptureZone(
                        startX,
                        currentY,
                        totalWidth,
                        zoneHeight));

                    currentY += zoneHeight;
                }
            }

            return zones;
        }
    }
}