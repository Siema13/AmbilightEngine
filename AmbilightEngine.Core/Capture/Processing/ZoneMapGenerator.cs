using System;
using System.Collections.Generic;
using System.Linq;

namespace AmbilightEngine.Core.Processing
{
    // Automatycznie rozkłada zadaną liczbę diod na 4 krawędziach ekranu,
    // proporcjonalnie do długości każdej krawędzi (dłuższa krawędź = więcej diod),
    // lub - w przeciążonej wersji - zgodnie z jawnym podziałem per-bok podanym przez użytkownika
    // w kreatorze geometrii. Wspiera dodatkowo przesunięcie mapowania (ShiftOffset)
    // oraz wykluczanie pojedynczych, martwych diod (ExcludedIndices).
    public static class ZoneMapGenerator
    {
        // Zachowana dla kompatybilności wstecznej - automatyczny, proporcjonalny podział.
        public static CaptureZone[] Generate(
            int screenWidth,
            int screenHeight,
            int totalLedCount,
            int samplingDepth = 80,
            StartCorner startCorner = StartCorner.BottomLeft,
            StripDirection direction = StripDirection.Clockwise)
        {
            if (totalLedCount <= 0)
                throw new ArgumentException("Liczba diod musi być większa od zera.");

            double perimeter = 2 * (screenWidth + screenHeight);
            int topCount = (int)Math.Round(totalLedCount * (screenWidth / perimeter));
            int bottomCount = topCount;
            int sideCount = (totalLedCount - topCount - bottomCount) / 2;
            int leftCount = sideCount;
            int rightCount = totalLedCount - topCount - bottomCount - leftCount;

            return Generate(
                screenWidth, screenHeight,
                topCount, bottomCount, leftCount, rightCount,
                samplingDepth, startCorner, direction,
                shiftOffset: 0, excludedIndices: null);
        }

        // Pełna wersja kreatora geometrii: jawny podział diod per bok, przesunięcie i wykluczenia.
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
            IReadOnlyCollection<int>? excludedIndices = null)
        {
            if (topCount < 0 || bottomCount < 0 || leftCount < 0 || rightCount < 0)
                throw new ArgumentException("Liczba diod na krawędzi nie może być ujemna.");

            int totalLedCount = topCount + bottomCount + leftCount + rightCount;
            if (totalLedCount <= 0)
                throw new ArgumentException("Suma diod na wszystkich krawędziach musi być większa od zera.");

            if (samplingDepth <= 0)
                throw new ArgumentException("Głębokość próbkowania musi być większa od zera.");

            var topZones = BuildEdge(0, 0, screenWidth, samplingDepth, topCount, horizontal: true);
            var rightZones = BuildEdge(screenWidth - samplingDepth, 0, samplingDepth, screenHeight, rightCount, horizontal: false);
            var bottomZonesRaw = BuildEdge(0, screenHeight - samplingDepth, screenWidth, samplingDepth, bottomCount, horizontal: true);
            var leftZonesRaw = BuildEdge(0, 0, samplingDepth, screenHeight, leftCount, horizontal: false);

            var bottomZones = new List<CaptureZone>(bottomZonesRaw);
            bottomZones.Reverse();

            var leftZones = new List<CaptureZone>(leftZonesRaw);
            leftZones.Reverse();

            // Kolejność krawędzi w łańcuchu zależy od wybranego narożnika startowego.
            // Domyślny łańcuch (BottomLeft, Clockwise) to: lewo -> góra -> prawo -> dół.
            var ordered = BuildOrderedChain(topZones, rightZones, bottomZones, leftZones, startCorner);

            if (direction == StripDirection.CounterClockwise)
                ordered.Reverse();

            if (shiftOffset != 0)
            {
                ordered = ApplyShift(ordered, shiftOffset);
            }

            if (excludedIndices != null && excludedIndices.Count > 0)
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
            // Łańcuch bazowy zawsze idzie w kierunku zgodnym z ruchem zegara, zaczynając od lewej-górnej.
            // Dobór punktu startowego realizujemy poprzez rotację tego samego łańcucha.
            var baseChain = new List<CaptureZone>();
            baseChain.AddRange(topZones);
            baseChain.AddRange(rightZones);
            baseChain.AddRange(bottomZones);
            baseChain.AddRange(leftZones);

            switch (startCorner)
            {
                case StartCorner.TopLeft:
                    return baseChain;

                case StartCorner.TopRight:
                    return RotateChain(baseChain, topZones.Count);

                case StartCorner.BottomRight:
                    return RotateChain(baseChain, topZones.Count + rightZones.Count);

                case StartCorner.BottomLeft:
                default:
                    return RotateChain(baseChain, topZones.Count + rightZones.Count + bottomZones.Count);
            }
        }

        private static List<CaptureZone> RotateChain(List<CaptureZone> chain, int rotateBy)
        {
            if (chain.Count == 0) return chain;

            int normalizedRotation = ((rotateBy % chain.Count) + chain.Count) % chain.Count;
            var rotated = new List<CaptureZone>(chain.Count);
            rotated.AddRange(chain.Skip(normalizedRotation));
            rotated.AddRange(chain.Take(normalizedRotation));
            return rotated;
        }

        // Przesuwa fizyczne mapowanie o N diod - kompensuje niedokładne fizyczne ułożenie
        // paska LED względem naszej wygenerowanej geometrii (np. kabel zasilający wymusił przesunięcie).
        private static List<CaptureZone> ApplyShift(List<CaptureZone> zones, int shiftOffset)
        {
            if (zones.Count == 0) return zones;

            int normalizedShift = ((shiftOffset % zones.Count) + zones.Count) % zones.Count;
            if (normalizedShift == 0) return zones;

            var shifted = new List<CaptureZone>(zones.Count);
            shifted.AddRange(zones.Skip(normalizedShift));
            shifted.AddRange(zones.Take(normalizedShift));
            return shifted;
        }

        // Oznacza wykluczone diody jako martwe strefy (zerowy rozmiar próbkowania).
        // ImageProcessor dla takiej strefy zwróci czarny kolor, bez potrzeby zmian w jego logice.
        private static void ApplyExclusions(List<CaptureZone> zones, IReadOnlyCollection<int> excludedIndices)
        {
            foreach (int index in excludedIndices)
            {
                if (index < 0 || index >= zones.Count) continue;
                zones[index] = CaptureZone.CreateDeadZone(zones[index].X, zones[index].Y);
            }
        }

        private static List<CaptureZone> BuildEdge(int startX, int startY, int totalWidth, int totalHeight, int count, bool horizontal)
        {
            var zones = new List<CaptureZone>();
            if (count <= 0) return zones;

            if (horizontal)
            {
                int segmentWidth = totalWidth / count;
                for (int i = 0; i < count; i++)
                {
                    zones.Add(new CaptureZone(startX + i * segmentWidth, startY, segmentWidth, totalHeight));
                }
            }
            else
            {
                int segmentHeight = totalHeight / count;
                for (int i = 0; i < count; i++)
                {
                    zones.Add(new CaptureZone(startX, startY + i * segmentHeight, totalWidth, segmentHeight));
                }
            }

            return zones;
        }
    }
}