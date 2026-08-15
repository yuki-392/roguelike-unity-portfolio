using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
#nullable enable

namespace Roguelike.Presentation
{
    public enum UiQaIssueKind
    {
        Overlap,
        OutsideViewport,
        BelowMinimumSize,
    }

    public readonly struct UiQaLayoutBox
    {
        public UiQaLayoutBox(
            string elementId,
            Rect bounds,
            bool isVisible = true,
            string? overlapGroup = null)
        {
            ElementId = elementId;
            Bounds = bounds;
            IsVisible = isVisible;
            OverlapGroup = overlapGroup;
        }

        public string ElementId { get; }
        public Rect Bounds { get; }
        public bool IsVisible { get; }
        public string? OverlapGroup { get; }
    }

    public readonly struct UiQaLayoutIssue
    {
        public UiQaLayoutIssue(
            UiQaIssueKind kind,
            string elementId,
            string? relatedElementId = null)
        {
            Kind = kind;
            ElementId = elementId;
            RelatedElementId = relatedElementId;
        }

        public UiQaIssueKind Kind { get; }
        public string ElementId { get; }
        public string? RelatedElementId { get; }
    }

    public static class UiQaLayoutRules
    {
        public static IReadOnlyList<UiQaLayoutIssue> FindIssues(
            IEnumerable<UiQaLayoutBox> elements,
            Rect viewport,
            Vector2? minimumSize = null)
        {
            var visibleElements = elements
                .Where(element => element.IsVisible && HasArea(element.Bounds))
                .ToArray();
            var issues = new List<UiQaLayoutIssue>();

            for (var firstIndex = 0;
                 firstIndex < visibleElements.Length;
                 firstIndex++)
            {
                var first = visibleElements[firstIndex];
                if (!Contains(viewport, first.Bounds))
                {
                    issues.Add(new UiQaLayoutIssue(
                        UiQaIssueKind.OutsideViewport,
                        first.ElementId));
                }
                if (minimumSize is { } size &&
                    (first.Bounds.width < size.x || first.Bounds.height < size.y))
                {
                    issues.Add(new UiQaLayoutIssue(
                        UiQaIssueKind.BelowMinimumSize,
                        first.ElementId));
                }

                for (var secondIndex = firstIndex + 1;
                     secondIndex < visibleElements.Length;
                     secondIndex++)
                {
                    var second = visibleElements[secondIndex];
                    if (first.OverlapGroup != null &&
                        first.OverlapGroup == second.OverlapGroup)
                        continue;
                    if (first.Bounds.Overlaps(second.Bounds))
                    {
                        issues.Add(new UiQaLayoutIssue(
                            UiQaIssueKind.Overlap,
                            first.ElementId,
                            second.ElementId));
                    }
                }
            }

            return issues;
        }

        private static bool HasArea(Rect bounds) =>
            bounds.width > 0f && bounds.height > 0f;

        private static bool Contains(Rect viewport, Rect bounds) =>
            bounds.xMin >= viewport.xMin &&
            bounds.yMin >= viewport.yMin &&
            bounds.xMax <= viewport.xMax &&
            bounds.yMax <= viewport.yMax;
    }

    public static class UiQaLayoutAudit
    {
        public static IReadOnlyList<UiQaLayoutIssue> Inspect(
            VisualElement root,
            Rect viewport)
        {
            var buttons = root.Query<Button>()
                .ToList()
                .Where(button => IsVisible(button) && button.worldBound.width > 0f)
                .Select(button =>
                {
                var scrollView = button.GetFirstAncestorOfType<ScrollView>();
                if (scrollView != null &&
                        !Contains(
                            scrollView.contentViewport.worldBound,
                            button.worldBound))
                {
                        // クリップされたカードのworldBoundは表示領域の外側まで残るため、
                        // 固定フッターとの重なりを実画面の重なりとして扱わない。
                        return (UiQaLayoutBox?)null;
                }

                    return new UiQaLayoutBox(
                        string.IsNullOrEmpty(button.name)
                            ? button.text
                            : button.name,
                        button.worldBound,
                        overlapGroup: button.ClassListContains(
                            "hand-fan-card")
                            ? "hand-fan"
                            : null);
                })
                .Where(box => box.HasValue)
                .Select(box => box!.Value)
                .ToArray();

            return UiQaLayoutRules.FindIssues(
                buttons,
                viewport);
        }

        private static bool IsVisible(VisualElement element) =>
            element.resolvedStyle.display != DisplayStyle.None &&
            element.resolvedStyle.visibility != Visibility.Hidden;

        private static bool Contains(Rect viewport, Rect bounds) =>
            bounds.xMin >= viewport.xMin &&
            bounds.yMin >= viewport.yMin &&
            bounds.xMax <= viewport.xMax &&
            bounds.yMax <= viewport.yMax;
    }
}
