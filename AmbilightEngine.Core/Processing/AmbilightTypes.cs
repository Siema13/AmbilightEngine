namespace AmbilightEngine.Core.Processing
{
    // Prosta struktura przechowująca ostateczny kolor wyliczony dla diody.
    public struct RgbColor
    {
        public byte R;
        public byte G;
        public byte B;

        public RgbColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    // Struktura definiująca wycinek (prostokąt) ekranu, na który patrzy dana dioda.
    public struct CaptureZone
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public CaptureZone(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        // Tworzy "martwą" strefę o zerowym rozmiarze próbkowania - używane przy wykluczaniu
        // uszkodzonych/niepotrzebnych diod w kreatorze geometrii. ImageProcessor dla takiej
        // strefy bezpiecznie zwróci czarny kolor (pętla skanująca nie wykona się, a wbudowane
        // zabezpieczenie przed dzieleniem przez zero zadba o resztę).
        public static CaptureZone CreateDeadZone(int x, int y)
        {
            return new CaptureZone(x, y, 0, 0);
        }
    }

    // Definiuje, od którego narożnika ekranu zaczyna się fizyczny pasek LED.
    public enum StartCorner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft
    }

    // Definiuje kierunek ułożenia paska LED względem ekranu.
    public enum StripDirection
    {
        Clockwise,
        CounterClockwise
    }
}