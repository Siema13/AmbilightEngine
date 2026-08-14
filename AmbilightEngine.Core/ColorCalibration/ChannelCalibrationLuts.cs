namespace AmbilightEngine.Core.ColorCalibration;

/// <summary>
/// Przechowuje aktualne tablice LUT dla trzech kanałów RGB i udostępnia szybką
/// metodę zastosowania kalibracji na pojedynczym pikselu. Instancja jest
/// przebudowywana (Rebuild) każdorazowo, gdy użytkownik zmienia ustawienia
/// kalibracji - odczyt per piksel jest wyłącznie indeksowaniem tablicy.
/// </summary>
public sealed class ChannelCalibrationLuts
{
    private byte[] lutR = GammaLutBuilder.BuildChannelLut(1.0, 1.0, 0.0);
    private byte[] lutG = GammaLutBuilder.BuildChannelLut(1.0, 1.0, 0.0);
    private byte[] lutB = GammaLutBuilder.BuildChannelLut(1.0, 1.0, 0.0);

    public void Rebuild(
        double gainR, double gammaR, double offsetR,
        double gainG, double gammaG, double offsetG,
        double gainB, double gammaB, double offsetB)
    {
        lutR = GammaLutBuilder.BuildChannelLut(gainR, gammaR, offsetR);
        lutG = GammaLutBuilder.BuildChannelLut(gainG, gammaG, offsetG);
        lutB = GammaLutBuilder.BuildChannelLut(gainB, gammaB, offsetB);
    }

    public (byte r, byte g, byte b) Apply(byte r, byte g, byte b)
        => (lutR[r], lutG[g], lutB[b]);
}