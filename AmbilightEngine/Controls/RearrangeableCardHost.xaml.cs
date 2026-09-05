using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.Core;

namespace AmbilightEngine.Controls
{
    public sealed partial class RearrangeableCardHost : UserControl
    {
        private const double DragThreshold = 6.0;
        private const int NeighborShiftAnimationMs = 150;

        private sealed class CardEntry
        {
            public required string CardId { get; init; }
            public required Border CardBorder { get; init; }
            public Border? DragHandle { get; set; }
        }

        private readonly List<CardEntry> leftColumnCards = new();
        private readonly List<CardEntry> rightColumnCards = new();
        private readonly List<CardEntry> pendingCards = new();

        private bool isEditModeActive;
        private CardEntry? draggedCard;
        private Point dragStartPointerPosition;
        private bool dragThresholdExceeded;

        private List<CardEntry>? liveReorderColumn;
        private int liveReorderPreviewIndex = -1;

        public event Action<IReadOnlyList<string>>? LayoutChanged;

        public RearrangeableCardHost()
        {
            InitializeComponent();
        }

        public void RegisterCard(string cardId, Border cardBorder)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                throw new ArgumentException("cardId nie może być pusty.", nameof(cardId));
            }

            if (cardBorder.Parent is Panel currentParent)
            {
                currentParent.Children.Remove(cardBorder);
            }

            pendingCards.Add(new CardEntry { CardId = cardId, CardBorder = cardBorder });
        }

        public void ApplyInitialLayout(IReadOnlyList<string>? savedOrder)
        {
            leftColumnCards.Clear();
            rightColumnCards.Clear();
            LeftColumnPanel.Children.Clear();
            RightColumnPanel.Children.Clear();

            List<CardEntry> orderedCards = ResolveEffectiveOrder(savedOrder);

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

            foreach (CardEntry remaining in pendingCards.Where(c => byId.ContainsKey(c.CardId)))
            {
                ordered.Add(remaining);
            }

            return ordered;
        }

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
                VerticalAlignment = VerticalAlignment.Center
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
                draggedCard = null;
                return;
            }

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

                double targetOffsetY = i >= previewIndex
                    ? draggedEntry.CardBorder.ActualHeight + targetPanel.RowSpacingOrDefault()
                    : 0;

                AnimateNeighborOffset(candidate.CardBorder, targetOffsetY);
            }

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

        private void CompleteDrag(CardEntry draggedEntry, Point dropPosition)
        {
            bool dropInLeftColumn = IsPointOverColumn(LeftColumnPanel, dropPosition);
            bool dropInRightColumn = !dropInLeftColumn && IsPointOverColumn(RightColumnPanel, dropPosition);

            if (!dropInLeftColumn && !dropInRightColumn)
            {
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

            double effectiveRight = bottomRight.X > topLeft.X ? bottomRight.X : topLeft.X + 200;

            return pointInHostCoordinates.X >= topLeft.X && pointInHostCoordinates.X <= effectiveRight;
        }

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

    internal static class PanelExtensions
    {
        public static double RowSpacingOrDefault(this Panel panel)
        {
            return panel is StackPanel stackPanel ? stackPanel.Spacing : 24.0;
        }
    }
}
