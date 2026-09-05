using System.Collections.Generic;

namespace AmbilightEngine.Core.SystemState
{
    // Trwały zapis kolejności kart w kontenerze RearrangeableCardHost. Jeden globalny układ
    // dla całej aplikacji (NIE per-profil) - każda strona z kartami (Dashboard, Profiles,
    // Geometry, Calibration, Settings...) ma własny wpis w mapie kluczowanej nazwą strony,
    // żeby zmiana układu na jednej stronie nie nadpisywała układu innej.
    //
    // Karty NIEOBECNE na liście CardOrder (np. nowo dodana karta po aktualizacji aplikacji,
    // której użytkownik nigdy nie widział w trybie edycji) są dopisywane na koniec przez
    // RearrangeableCardHost przy wczytywaniu - patrz ResolveEffectiveOrder w kodzie kontrolki.
    public sealed class DashboardLayoutSettings
    {
        // Klucz = PageKey (stała tekstowa identyfikująca stronę, np. "Dashboard", "Profiles").
        // Wartość = lista CardId w kolejności ustawionej przez użytkownika metodą drag&drop.
        public Dictionary<string, List<string>> PageCardOrders { get; set; } = new();
    }
}
