using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    internal sealed class NoteMargin : IWpfTextViewMargin
    {
        internal const string Name = "OrcaNotesMargin";
        private readonly Canvas canvas = new Canvas { Width = 20, Background = Brushes.Transparent, ClipToBounds = true };
        private readonly IWpfTextView view;
        private readonly EditorNotes notes;
        private readonly Dictionary<int, Button> addButtons = new Dictionary<int, Button>();
        private int hoveredLine = -1;
        private bool disposed, pending;
        internal NoteMargin(IWpfTextView view, EditorNotes notes)
        {
            this.view = view;
            this.notes = notes;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = Strings.WorkspaceMenu };
            item.Click += (s, e) => EditorNotes.Try(notes.OpenQueue);
            menu.Items.Add(item);
            canvas.ContextMenu = menu;
            canvas.Loaded += Loaded;
            canvas.MouseMove += MouseMoved;
            canvas.MouseLeave += MouseLeft;
            view.VisualElement.MouseMove += MouseMoved;
            view.VisualElement.MouseLeave += MouseLeft;
            view.Caret.PositionChanged += CaretChanged;
            view.LayoutChanged += LayoutChanged;
            notes.MarkersChanged += Schedule;
            Schedule();
        }
        public FrameworkElement VisualElement => canvas;
        public double MarginSize => canvas.Width;
        public bool Enabled => !disposed;
        public ITextViewMargin GetTextViewMargin(string name) => name == Name ? this : null;
        private void Loaded(object s, RoutedEventArgs e) => Schedule();
        private void LayoutChanged(object s, TextViewLayoutChangedEventArgs e) => Schedule();
        private void Schedule()
        {
            if (disposed || pending) return;
            pending = true;
            _ = canvas.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Render));
        }
        private void CaretChanged(object s, CaretPositionChangedEventArgs e) => UpdateVisibility();
        private void MouseLeft(object s, MouseEventArgs e) { hoveredLine = -1; UpdateVisibility(); }
        private void MouseMoved(object s, MouseEventArgs e)
        {
            if (view.IsClosed || view.InLayout || view.TextViewLines == null || !view.TextViewLines.IsValid) return;
            double y = view.ViewportTop + e.GetPosition((IInputElement)s).Y;
            var visual = view.TextViewLines.GetTextViewLineContainingYCoordinate(y);
            int line = visual?.Start.GetContainingLine().LineNumber ?? -1;
            if (line != hoveredLine) { hoveredLine = line; UpdateVisibility(); }
        }
        private void UpdateVisibility()
        {
            if (disposed || view.IsClosed) return;
            int caret = view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            foreach (var pair in addButtons)
                pair.Value.Opacity = pair.Key == hoveredLine || pair.Key == caret ? 1 : 0;
        }
        private void Render()
        {
            pending = false;
            if (disposed || view.IsClosed || view.InLayout || !canvas.IsLoaded) return;
            canvas.Children.Clear();
            addButtons.Clear();
            var lines = view.TextViewLines;
            if (lines == null || !lines.IsValid) return;
            var displayed = new HashSet<int>();
            foreach (var visual in lines)
            {
                int n = visual.Start.GetContainingLine().LineNumber;
                if (!displayed.Add(n)) continue;
                var note = notes.At(n);
                string label = string.Format(note == null ? Strings.AddAtLine : Strings.EditAtLine, n + 1);
                var button = new Button { Width = 20, Height = Math.Max(12, visual.TextHeight), Padding = new Thickness(0),
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Foreground = note == null ? Brushes.Gray : Brushes.DodgerBlue, FontWeight = FontWeights.Bold,
                    Content = note == null ? "+" : "●", ToolTip = note?.Comment ?? label, Cursor = Cursors.Hand,
                    Focusable = false, IsTabStop = false };
                AutomationProperties.SetName(button, label);
                AutomationProperties.SetAutomationId(button, "OrcaNoteLine" + (n + 1));
                if (note == null) addButtons.Add(n, button);
                button.Click += (s,e) => { e.Handled = true; EditorNotes.Try(() => notes.OpenLine(n)); };
                Canvas.SetTop(button, visual.TextTop - view.ViewportTop);
                canvas.Children.Add(button);
            }
            UpdateVisibility();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            notes.MarkersChanged -= Schedule;
            view.LayoutChanged -= LayoutChanged;
            view.VisualElement.MouseMove -= MouseMoved;
            view.VisualElement.MouseLeave -= MouseLeft;
            canvas.MouseMove -= MouseMoved;
            canvas.MouseLeave -= MouseLeft;
            view.Caret.PositionChanged -= CaretChanged;
            canvas.Loaded -= Loaded;
            canvas.Children.Clear();
        }
    }
}
