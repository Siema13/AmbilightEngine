using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace AmbilightEngine.Controls
{
    // Reużywalny kontener kart z obsługą przenoszenia metodą "drag&drop" w trybie edycji.
    // Karty są rejestrowane przez stronę-właściciela jako już zbudowane elementy Border
    // (ze stylem CardStyle) - kontener TYLKO zarządza ich rozmieszczeniem w dwóch kolumnach
    // typu "masonry" (różne wysokości, nowa karta trafia do aktualnie krótszej kolumny) oraz
    // dodaje/usuwa nakładkę z uchwytem przeciągania. Ponieważ Border jest przenoszony między
    // kontenerami metodą Children.Remove/Insert (nie tworzony na nowo), wszystkie referencje
    // x:Name do kontrolek WEWNĄTRZ karty (np. StatusText, ToggleButton na Dashboardzie)
    // pozostają w pełni działające - x:Name wiąże pole klasy strony z obiektem w pamięci,
    // niezależnie od miejsca obiektu w drzewie wizualnym.
    public sealed partial class RearrangeableCardHost : UserControl
    {
        // Margines "martwej strefy" (px) - jeśli przeciągana karta nie przekroczyła tej
        // odległości od punktu startowego, traktujemy to jako kliknięcie, nie przesunięcie
        // (zapobiega przypadkowej zmianie kolejności przy drobnym drgnięciu myszki/dotyku).
        private const double DragThreshold = 6.0;

        // Czas trwania animacji "rozsuwania się" kart sąsiadujących podczas live-reorder.
        private const int NeighborShiftAnimationMs = 150;

        private sealed class CardEntry
        {
            public required string CardId { get; init; }
            public required Border CardBorder { get; init; }

            // Uchwyt przeciągania to teraz Border (nie Button) - unika kolizji z wewnętrzną
            // maszyną stanów ButtonBase, która w WinUI 3 potrafi "podkradać" capture wskaźnika
            // dla lewego przycisku myszy, blokując dalsze zdarzenia PointerMoved.
            public Border? DragHandle { get; set; }
        }

        // Kolejność odzwierciedla AKTUALNE rozmieszczenie: LeftColumnPanel.Children i
        // RightColumnPanel.Children są zawsze zsynchronizowane z tymi dwoma listami.
        private readonly List<CardEntry> leftColumnCards = new();
        private readonly List<CardEntry> rightColumnCards = new();
        private readonly List<CardEntry> pendingCards = new();

        private bool isEditModeActive;
        private CardEntry? draggedCard;
        private Point dragStartPointerPosition;
        private bool dragThresholdExceeded;

        // Migawka kolumny docelowej w trakcie przeciągania, używana do wizualnego
        // "rozsuwania" sąsiadów (live-reorder preview) - patrz UpdateLiveReorderPreview.
        private List<CardEntry>? liveReorderColumn;
        private int liveReorderPreviewIndex = -1;

        // Wywoływane po KAŻDEJ zmianie układu (przestawienie karty) - strona-właściciel
        // zapisuje wynikową listę CardId (w kolejności: najpierw lewa kolumna top->bottom,
        // potem prawa) do AmbilightSettings.CardLayout i wywołuje SettingsService.Save.
        public event Action<IReadOnlyList<string>>? LayoutChanged;

        public RearrangeableCardHost()
        {
            InitializeComponent();
        }

        // Rejestruje kartę w kontenerze. Musi być wywołane dla WSZYSTKICH kart PRZED
        // wywołaniem ApplyInitialLayout - kolejność rejestracji jest używana jako domyślny
        // układ, gdy zapisane ustawienia nie zawierają jeszcze danego CardId (np. nowa karta
        // dodana w aktualizacji aplikacji, której poprzednia wersja settings.json nie znała).
        public void RegisterCard(string cardId, Border cardBorder)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new ArgumentException("cardId nie może być pusty.", nameof(cardId));
            }

            // Karta pochodzi z niewidocznego kontenera-źrodła zdefiniowanego w XAML strony
            // (np. HiddenCardSourcePanel) i wciąż ma tam rodzica. Panel.Children.Add w innym
            // miejscu (ApplyInitialLayout) wymaga, aby element nie miał aktualnie rodzica -
            // usuwamy go tutaj z oryginalnego kontenera, zanim trafi do bufora pendingCards.
            if (cardBorder.Parent is Panel currentParent)
            {
                currentParent.Children.Remove(cardBorder);
            }

            pendingCards.Add(new CardEntry { CardId = cardId, CardBorder = cardBorder });
        }

        // Rozmieszcza zarejestrowane karty w dwóch kolumnach zgodnie z savedOrder (lista
        // CardId w zapisanej kolejności). Karty spoza savedOrder (nowe, nieznane wcześniej)
        // są dopisywane na koniec w kolejności rejestracji. Musi być wywołane raz, po
        // zarejestrowaniu wszystkich kart strony.
        public void ApplyInitialLayout(IReadOnlyList<string>? savedOrder)
        {
            leftColumnCards.Clear();
            rightColumnCards.Clear();
            LeftColumnPanel.Children.Clear();
            RightColumnPanel.Children.Clear();

            List<CardEntry> orderedCards = ResolveEffectiveOrder(savedOrder);

            // Algorytm masonry uproszczony: karty trafiają na przemian do lewej/prawej
            // kolumny w kolejności z listy - to nie mierzy realnej wysokości px (WinUI 3
            // nie daje tej informacji przed pierwszym layout passem), ale zachowuje
            // przewidywalność: użytkownik widzi kartę "po lewej" i "po prawej" zgodnie
            // z tym, jak sam je poprzednio poukładał (co jest zapisywane jako sekwencja
            // przeplotu przy każdym przeniesieniu - patrz CompleteDrag).
            for (int i = 0; i < orderedCards.Count; i++)
            {
                CardEntry entry = orderedCards[i];
                bool goesLeft = i % 2 == 0;

                if (goesLeft)
                {
                    leftColumnCards.Add(entry);
                    LeftColumnPanel.Children.Add(entry.CardBorder);
                }
                else
                {
                    rightColumnCards.Add(entry);
                    RightColumnPanel.Children.Add(entry.CardBorder);
                }
            }

            pendingCards.Clear();

            if (isEditModeActive)
            {
                foreach (CardEntry entry in leftColumnCards.Concat(rightColumnCards))
                {
                    AttachDragHandle(entry);
                }
            }
        }

        private List<CardEntry> ResolveEffectiveOrder(IReadOnlyList<string>? savedOrder)
        {
            if (savedOrder is null || savedOrder.Count == 0)
            {
                return new List<CardEntry>(pendingCards);
            }

            var byId = pendingCards.ToDictionary(c => c.CardId, c => c);
            var ordered = new List<CardEntry>();

            foreach (string cardId in savedOrder)
            {
                if (byId.TryGetValue(cardId, out CardEntry? entry))
                {
                    ordered.Add(entry);
                    byId.Remove(cardId);
                }
            }

            // Karty nieobecne w zapisanej kolejności (nowe od czasu ostatniego zapisu) -
            // dopisywane na koniec w kolejności rejestracji, żeby nigdy nie zniknęły z widoku.
            foreach (CardEntry remaining in pendingCards.Where(c => byId.ContainsKey(c.CardId)))
            {
                ordered.Add(remaining);
            }

            return ordered;
        }

        // Zwraca aktualną kolejność CardId (lewa kolumna, potem prawa) - wywoływane przez
        // stronę-właściciela z LayoutChanged, ale też dostępne do odczytu na żądanie
        // (np. przy zamykaniu strony, jako dodatkowe zabezpieczenie zapisu).
        public IReadOnlyList<string> GetCurrentOrder()
        {
            return leftColumnCards.Concat(rightColumnCards).Select(c => c.CardId).ToList();
        }

        private void ToggleEditModeButton_Click(object sender, RoutedEventArgs e)
        {
            isEditModeActive = !isEditModeActive;

            ToggleEditModeButton.Content = isEditModeActive ? "✅ Zakończ edycję" : "🔧 Dostosuj układ";
            EditModeHintText.Visibility = isEditModeActive ? Visibility.Visible : Visibility.Collapsed;

            foreach (CardEntry entry in leftColumnCards.Concat(rightColumnCards))
            {
                if (isEditModeActive)
                {
                    AttachDragHandle(entry);
                }
                else
                {
                    DetachDragHandle(entry);
                }
            }
        }

        // Wstawia uchwyt przeciągania jako PIERWSZY element wewnątrz karty. Zakładamy, że
        // bezpośrednim dzieckiem Border jest Panel (StackPanel/Grid) z co najmniej jednym
        // dzieckiem - wzorzec konsekwentnie używany we wszystkich kartach aplikacji. Jeśli
        // struktura się nie zgadza, po prostu nie dodajemy uchwytu (karta zachowuje się jak
        // w trybie normalnym) - błąd defensywnie ignorowany, nie wywala UI.
        //
        // WAŻNE: uchwyt to Border, nie Button. ButtonBase w WinUI 3 ma własną wewnętrzną
        // maszynę stanów wizualnych (Normal/PointerOver/Pressed), która dla LEWEGO przycisku
        // myszy przejmuje kontrolę nad wskaźnikiem przed naszym kodem - PointerPressed owszem
        // odpala się, ale kolejne PointerMoved bywają "zjadane" przez logikę Buttona, więc
        // CapturePointer w praktyce nie daje ciągłego strumienia zdarzeń. Środkowy/prawy
        // przycisk nie aktywują tej maszyny stanów, więc tam capture działał poprawnie - stąd
        // zgłoszona asymetria (lewy nie reaguje, środkowy/prawy przeciągają).
        //
        // Border (w odróżnieniu od Control) nie eksponuje właściwości Foreground - kolor
        // ikony "⠿" jest więc ustawiany bezpośrednio na wewnętrznym TextBlock.
        private void AttachDragHandle(CardEntry entry)
        {
            if (entry.DragHandle is not null)
            {
                return;
            }

            if (entry.CardBorder.Child is not Panel contentPanel)
            {
                return;
            }

            var handleIcon = new TextBlock
            {
                Text = "⠿",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["M3OnSecondaryContainerBrush"]
            };

            var handle = new Border
            {
                Style = (Style)Resources["DragHandleBorderStyle"],
                Child = handleIcon
            };

            handle.PointerPressed += (s, args) => DragHandle_PointerPressed(entry, args);
            handle.PointerMoved += (s, args) => DragHandle_PointerMoved(entry, args);
            handle.PointerReleased += (s, args) => DragHandle_PointerReleased(entry, args);
            handle.PointerCaptureLost += (s, args) => CancelDrag(entry);

            contentPanel.Children.Insert(0, handle);
            entry.DragHandle = handle;
        }

        private void DetachDragHandle(CardEntry entry)
        {
            if (entry.DragHandle is null)
            {
                return;
            }

            if (entry.CardBorder.Child is Panel contentPanel)
            {
                contentPanel.Children.Remove(entry.DragHandle);
            }

            entry.DragHandle = null;
        }

        private void DragHandle_PointerPressed(CardEntry entry, PointerRoutedEventArgs args)
        {
            if (entry.DragHandle is null)
            {
                return;
            }

            // Filtrowanie przycisku myszy: drag&drop reaguje WYŁĄCZNIE na lewy przycisk,
            // zgodnie ze standardowym zachowaniem systemowym Windows. Środkowy i prawy
            // przycisk są tutaj świadomie ignorowane (args.Handled pozostaje false, żeby
            // np. menu kontekstowe pod prawym przyciskiem wciąż mogło zadziałać gdzie indziej).
            //
            // PointerPoint w WinUI 3 (nie UWP) żyje w przestrzeni nazw Microsoft.UI.Input,
            // nie Windows.UI.Input/Windows.UI.Core - stąd using na początku pliku.
            PointerPoint point = args.GetCurrentPoint(entry.DragHandle);
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            draggedCard = entry;
            dragThresholdExceeded = false;
            dragStartPointerPosition = args.GetCurrentPoint(this).Position;
            liveReorderColumn = null;
            liveReorderPreviewIndex = -1;

            bool captured = entry.DragHandle.CapturePointer(args.Pointer);
            if (!captured)
            {
                // Capture się nie powiodło (np. pointer już przechwycony przez inny element) -
                // wychodzimy z trybu drag, żeby nie zostać w niekonsystentnym stanie.
                draggedCard = null;
                return;
            }

            // Podnosimy kartę na wierch Z-order w obrębie własnego panelu, żeby podczas
            // przeciągania wizualnie przechodziła NAD sąsiadującymi kartami, a nie pod nimi.
            entry.CardBorder.Translation = new System.Numerics.Vector3(0, 0, 32);

            args.Handled = true;
        }

        private void DragHandle_PointerMoved(CardEntry entry, PointerRoutedEventArgs args)
        {
            if (draggedCard != entry)
            {
                return;
            }

            Point currentPosition = args.GetCurrentPoint(this).Position;
            double deltaX = currentPosition.X - dragStartPointerPosition.X;
            double deltaY = currentPosition.Y - dragStartPointerPosition.Y;

            if (!dragThresholdExceeded)
            {
                double distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance < DragThreshold)
                {
                    return;
                }

                dragThresholdExceeded = true;

                // Podniesienie karty wizualnie (cień + lekkie przezroczystość), żeby dać
                // jasny sygnał, że przeciąganie faktycznie się zaczęło.
                entry.CardBorder.Opacity = 0.85;
                entry.CardBorder.RenderTransform = new TranslateTransform();

                bool startsInLeftColumn = leftColumnCards.Contains(entry);
                liveReorderColumn = startsInLeftColumn ? leftColumnCards : rightColumnCards;
                liveReorderPreviewIndex = liveReorderColumn.IndexOf(entry);
            }

            if (entry.CardBorder.RenderTransform is TranslateTransform transform)
            {
                transform.X = deltaX;
                transform.Y = deltaY;
            }

            UpdateLiveReorderPreview(entry, currentPosition);

            args.Handled = true;
        }

        // Rozsuwa wizualnie karty sąsiadujące w kolumnie docelowej, dając w czasie rzeczywistym
        // podgląd, gdzie trafi przeciągana karta po puszczeniu przycisku (analogicznie do
        // znanych bibliotek drag&drop typu masonry). Nie zmienia jeszcze faktycznej kolejności
        // w drzewie XAML - to dzieje się dopiero w CompleteDrag po puszczeniu przycisku.
        private void UpdateLiveReorderPreview(CardEntry draggedEntry, Point pointerPosition)
        {
            bool overLeftColumn = IsPointOverColumn(LeftColumnPanel, pointerPosition);
            bool overRightColumn = !overLeftColumn && IsPointOverColumn(RightColumnPanel, pointerPosition);

            List<CardEntry> targetColumn;
            Panel targetPanel;

            if (overLeftColumn)
            {
                targetColumn = leftColumnCards;
                targetPanel = LeftColumnPanel;
            }
            else if (overRightColumn)
            {
                targetColumn = rightColumnCards;
                targetPanel = RightColumnPanel;
            }
            else
            {
                // Wskaźnik jest poza obiema kolumnami - zerujemy podgląd, karty sąsiadów
                // wracają na swoje neutralne pozycje.
                ClearNeighborShiftPreview();
                return;
            }

            List<CardEntry> otherCards = targetColumn.Where(c => c != draggedEntry).ToList();
            int previewIndex = FindInsertIndex(otherCards, pointerPosition);

            liveReorderColumn = targetColumn;
            liveReorderPreviewIndex = previewIndex;

            for (int i = 0; i < otherCards.Count; i++)
            {
                CardEntry candidate = otherCards[i];

                // Karty PRZED miejscem wstawienia zostają na miejscu (offset 0), karty PO
                // miejscu wstawienia przesuwają się o wysokość przeciąganej karty + odstęp,
                // robiąc jej fizyczne miejsce w kolumnie.
                double targetOffsetY = i >= previewIndex
                    ? draggedEntry.CardBorder.ActualHeight + targetPanel.RowSpacingOrDefault()
                    : 0;

                AnimateNeighborOffset(candidate.CardBorder, targetOffsetY);
            }

            // Sąsiadujące karty w KOLUMNIE, z której przeciągana karta odjeżdża (jeśli inna
            // niż docelowa), wracają do neutralnej pozycji.
            List<CardEntry> otherColumn = targetColumn == leftColumnCards ? rightColumnCards : leftColumnCards;
            if (!ReferenceEquals(otherColumn, targetColumn))
            {
                foreach (CardEntry candidate in otherColumn.Where(c => c != draggedEntry))
                {
                    AnimateNeighborOffset(candidate.CardBorder, 0);
                }
            }
        }

        private void ClearNeighborShiftPreview()
        {
            foreach (CardEntry candidate in leftColumnCards.Concat(rightColumnCards))
            {
                if (candidate != draggedCard)
                {
                    AnimateNeighborOffset(candidate.CardBorder, 0);
                }
            }

            liveReorderColumn = null;
            liveReorderPreviewIndex = -1;
        }

        private void AnimateNeighborOffset(Border cardBorder, double targetOffsetY)
        {
            if (cardBorder.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                cardBorder.RenderTransform = transform;
            }

            var animation = new DoubleAnimation
            {
                To = targetOffsetY,
                Duration = new Duration(TimeSpan.FromMilliseconds(NeighborShiftAnimationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void DragHandle_PointerReleased(CardEntry entry, PointerRoutedEventArgs args)
        {
            if (draggedCard != entry)
            {
                return;
            }

            entry.DragHandle?.ReleasePointerCapture(args.Pointer);

            if (dragThresholdExceeded)
            {
                Point dropPosition = args.GetCurrentPoint(this).Position;
                CompleteDrag(entry, dropPosition);
            }

            ResetDragVisualState(entry);
            ClearNeighborShiftPreview();
            draggedCard = null;
            args.Handled = true;
        }

        private void CancelDrag(CardEntry entry)
        {
            if (draggedCard != entry)
            {
                return;
            }

            ResetDragVisualState(entry);
            ClearNeighborShiftPreview();
            draggedCard = null;
        }

        private void ResetDragVisualState(CardEntry entry)
        {
            entry.CardBorder.Opacity = 1.0;
            entry.CardBorder.RenderTransform = null;
            entry.CardBorder.Translation = System.Numerics.Vector3.Zero;
        }

        // Ustala nową pozycję przeciąganej karty na podstawie tego, nad którą kolumną i
        // nad którą sąsiadującą kartą znajdował się punkt puszczenia (dropPosition, we
        // współrzędnych całego kontenera - stąd TransformToVisual(this) dla każdej kandydatki).
        //
        // Kolejność operacji jest tu kluczowa dla naprawy zgłoszonego bugu "kafel wraca na
        // swoje miejsce": najpierw fizycznie przenosimy Border w drzewie XAML (Children.Insert),
        // a DOPIERO w PointerReleased (po powrocie z tej metody) resetujemy RenderTransform.
        // Dzięki temu w momencie zerowania transformacji karta już stoi na nowej, docelowej
        // pozycji w layoucie - nie ma więc żadnego "skoku z powrotem" do starego miejsca.
        private void CompleteDrag(CardEntry draggedEntry, Point dropPosition)
        {
            bool dropInLeftColumn = IsPointOverColumn(LeftColumnPanel, dropPosition);
            bool dropInRightColumn = !dropInLeftColumn && IsPointOverColumn(RightColumnPanel, dropPosition);

            if (!dropInLeftColumn && !dropInRightColumn)
            {
                // Puszczono poza obiema kolumnami (np. nad przyciskiem "Dostosuj układ") -
                // brak zmiany, karta wraca na swoje miejsce.
                return;
            }

            List<CardEntry> targetColumnList = dropInLeftColumn ? leftColumnCards : rightColumnCards;
            Panel targetColumnPanel = dropInLeftColumn ? LeftColumnPanel : RightColumnPanel;

            RemoveFromCurrentColumn(draggedEntry);

            List<CardEntry> otherCards = targetColumnList.Where(c => c != draggedEntry).ToList();
            int insertIndex = FindInsertIndex(otherCards, dropPosition);

            targetColumnList.Insert(insertIndex, draggedEntry);
            targetColumnPanel.Children.Insert(insertIndex, draggedEntry.CardBorder);

            LayoutChanged?.Invoke(GetCurrentOrder());
        }

        private void RemoveFromCurrentColumn(CardEntry entry)
        {
            if (leftColumnCards.Remove(entry))
            {
                LeftColumnPanel.Children.Remove(entry.CardBorder);
                return;
            }

            if (rightColumnCards.Remove(entry))
            {
                RightColumnPanel.Children.Remove(entry.CardBorder);
            }
        }

        private bool IsPointOverColumn(Panel columnPanel, Point pointInHostCoordinates)
        {
            GeneralTransform transform = columnPanel.TransformToVisual(this);
            Point topLeft = transform.TransformPoint(new Point(0, 0));
            Point bottomRight = transform.TransformPoint(
                new Point(columnPanel.ActualWidth, Math.Max(columnPanel.ActualHeight, 1)));

            // Szerokość kolumny bywa 0 przy pustej kolumnie (brak dzieci) - dajemy minimalny
            // obszar wykrywania (200px), żeby wciąż można było upuścić pierwszą kartę.
            double effectiveRight = bottomRight.X > topLeft.X ? bottomRight.X : topLeft.X + 200;

            return pointInHostCoordinates.X >= topLeft.X && pointInHostCoordinates.X <= effectiveRight;
        }

        // Znajduje indeks wstawienia w kolumnie docelowej na podstawie środków Y istniejących
        // kart - przeciągana karta trafia PRZED pierwszą kartą, której środek jest niżej niż
        // punkt upuszczenia (klasyczny algorytm "insert before nearest lower midpoint").
        private int FindInsertIndex(List<CardEntry> targetColumnList, Point dropPosition)
        {
            for (int i = 0; i < targetColumnList.Count; i++)
            {
                Border candidate = targetColumnList[i].CardBorder;
                GeneralTransform transform = candidate.TransformToVisual(this);
                Point topLeft = transform.TransformPoint(new Point(0, 0));
                double midpointY = topLeft.Y + (candidate.ActualHeight / 2.0);

                if (dropPosition.Y < midpointY)
                {
                    return i;
                }
            }

            return targetColumnList.Count;
        }
    }

    // Drobny helper na potrzeby UpdateLiveReorderPreview - StackPanel nie eksponuje
    // publicznie swojego Spacing w starszych wersjach WinUI jako prostej właściwości
    // odczytywalnej z bazowego typu Panel, więc podajemy bezpieczną wartość domyślną
    // zgodną z odstępem 24px zdefiniowanym w RearrangeableCardHost.xaml (Spacing="24").
    internal static class PanelExtensions
    {
        public static double RowSpacingOrDefault(this Panel panel)
        {
            return panel is StackPanel stackPanel ? stackPanel.Spacing : 24.0;
        }
    }
}
