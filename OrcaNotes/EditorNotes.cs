using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    [Export(typeof(IViewTaggerProvider)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    [TagType(typeof(InterLineAdornmentTag))]
    internal sealed class NoteTaggerProvider : IViewTaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        [Import] internal IWpfKeyboardTrackingService KeyboardTracking = null;
        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag =>
            view is IWpfTextView wpf && buffer == view.TextBuffer ? EditorNotes.Get(wpf, Documents, KeyboardTracking) as ITagger<T> : null;
    }

    [Export(typeof(IWpfTextViewMarginProvider)), Name(NoteMargin.Name)]
    [ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    [MarginContainer(PredefinedMarginNames.Left), Order(After = PredefinedMarginNames.LineNumber)]
    internal sealed class NoteMarginProvider : IWpfTextViewMarginProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        [Import] internal IWpfKeyboardTrackingService KeyboardTracking = null;
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
        {
            var editor = EditorNotes.Get(host.TextView, Documents, KeyboardTracking);
            return editor == null ? null : new NoteMargin(host.TextView, editor);
        }
    }

    internal sealed class EditorNotes : ITagger<InterLineAdornmentTag>
    {
        private readonly IWpfTextView view;
        private readonly ITextDocument document;
        private OrcaClient preloadedClient;
        private readonly IWpfKeyboardTrackingService keyboardTracking;
        private readonly NoteStore store = NoteStore.Shared;
        private readonly Dictionary<string, ITrackingSpan> anchors = new Dictionary<string, ITrackingSpan>();
        private readonly Dictionary<string, InterLineAdornmentTag> savedTags = new Dictionary<string, InterLineAdornmentTag>();
        private List<ReviewNote> saved = new List<ReviewNote>();
        private ReviewNote draft;
        private ITrackingSpan draftSpan;
        private InterLineAdornmentTag tag;
        private string originalComment;
        private bool focusEditor;
        private string loadError;
        internal event Action MarkersChanged;
        internal string FilePath => document.FilePath;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        internal static EditorNotes Get(IWpfTextView view, ITextDocumentFactoryService documents, IWpfKeyboardTrackingService keyboardTracking)
        {
            if (!documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var doc) ||
                doc.TextBuffer != view.TextBuffer || !Path.IsPathRooted(doc.FilePath)) return null;
            return view.Properties.GetOrCreateSingletonProperty(() => new EditorNotes(view, doc, keyboardTracking));
        }

        private EditorNotes(IWpfTextView view, ITextDocument document, IWpfKeyboardTrackingService keyboardTracking)
        {
            this.view = view;
            this.document = document;
            this.keyboardTracking = keyboardTracking;
            Reload();
            store.Changed += Reload;
            view.ViewportWidthChanged += WidthChanged;
            view.TextBuffer.Changed += TextChanged;
            view.Closed += (s, e) =>
            {
                store.Changed -= Reload;
                view.ViewportWidthChanged -= WidthChanged;
                view.TextBuffer.Changed -= TextChanged;
            };
        }

        private void Reload()
        {
            try
            {
                var snapshot = view.TextSnapshot;
                saved = store.All().Where(n => n.State != NoteStore.Sent &&
                    string.Equals(n.File, document.FilePath, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string id in anchors.Keys.Except(saved.Select(n => n.Id)).ToList()) anchors.Remove(id);
                foreach (var n in saved.Where(n => !anchors.ContainsKey(n.Id)))
                {
                    var span = Restore(snapshot, n);
                    if (span.HasValue) anchors[n.Id] = snapshot.CreateTrackingSpan(span.Value, SpanTrackingMode.EdgeExclusive);
                }
                loadError = null;
            }
            catch (Exception e) { loadError = e.Message; }
            InvalidateSavedTags();
        }

        private double CardWidth => Math.Max(180, Math.Min(520, view.ViewportWidth - 16));
        private bool DarkTheme => view.Background is SolidColorBrush bg && bg.Color.R * .30 + bg.Color.G * .59 + bg.Color.B * .11 < 128;
        private void WidthChanged(object sender, EventArgs args) => InvalidateSavedTags();
        private void TextChanged(object sender, TextContentChangedEventArgs args)
        {
            if (!args.Changes.Any(change => change.LineCountDelta != 0)) return;
            var dispatcher = view.VisualElement.Dispatcher;
            if (dispatcher.CheckAccess()) InvalidateSavedTags();
            else _ = dispatcher.InvokeAsync(InvalidateSavedTags);
        }
        private void InvalidateSavedTags()
        {
            if (view.IsClosed) return;
            savedTags.Clear();
            Refresh();
        }

        private static Span? Restore(ITextSnapshot snapshot, ReviewNote note)
        {
            if (note.Line > 0 && note.Line <= snapshot.LineCount)
            {
                var line = snapshot.GetLineFromLineNumber(note.Line - 1);
                int start = line.Start.Position + note.Column;
                if ((note.Code.Length > 0 || line.Length == 0) && note.Column <= line.Length && start + note.Code.Length <= snapshot.Length &&
                    snapshot.GetText(start, note.Code.Length) == note.Code) return new Span(start, note.Code.Length);
            }
            if (string.IsNullOrWhiteSpace(note.Code)) return null;
            var text = snapshot.GetText();
            int pos = text.IndexOf(note.Code, StringComparison.Ordinal);
            return pos >= 0 && text.IndexOf(note.Code, pos + 1, StringComparison.Ordinal) < 0
                ? new Span(pos, note.Code.Length) : (Span?)null;
        }

        internal ReviewNote At(int line) => saved.FirstOrDefault(n => anchors.TryGetValue(n.Id, out var span) &&
            span.GetStartPoint(view.TextSnapshot).GetContainingLine().LineNumber == line);

        internal void OpenLine(int lineNumber) => OpenEditor(lineNumber, At(lineNumber));

        private void OpenEditor(int lineNumber, ReviewNote note)
        {
            if (loadError != null) { MessageBox.Show(loadError, Strings.CannotReadNotes); return; }
            if (draft != null && draft.Comment != originalComment &&
                MessageBox.Show(Strings.DiscardChanges, Strings.AppName, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var snapshot = view.TextSnapshot;
            if (note == null)
            {
                var line = snapshot.GetLineFromLineNumber(lineNumber);
                SnapshotSpan span = line.Extent;
                if (view.Selection.Mode == TextSelectionMode.Stream && !view.Selection.IsEmpty)
                {
                    var selection = view.Selection.StreamSelectionSpan.SnapshotSpan;
                    if (selection.IntersectsWith(line.Extent) && line.Start < selection.End) span = selection;
                }
                var first = span.Start.GetContainingLine();
                var last = span.IsEmpty ? first : (span.End - 1).GetContainingLine();
                draft = new ReviewNote { Id = Guid.NewGuid().ToString("N"), File = document.FilePath, Line = first.LineNumber + 1,
                    LastLine = last.LineNumber + 1, Column = span.Start - first.Start, Code = span.GetText(), Comment = "", State = NoteStore.Pending };
                draftSpan = snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive);
            }
            else { draft = note.Copy(); draftSpan = anchors[note.Id]; }
            originalComment = draft.Comment;
            focusEditor = true;
            tag = new InterLineAdornmentTag((t, v, p) => MakeEditor(), false, 133, HorizontalPositioningMode.ViewRelative, 4, null);
            Refresh();
        }

        private void Refresh()
        {
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length)));
            MarkersChanged?.Invoke();
        }

        public IEnumerable<ITagSpan<InterLineAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            var snapshot = spans[0].Snapshot;
            if (tag != null)
            {
                var at = TagPosition(draftSpan.GetSpan(snapshot));
                if (spans.Any(s => s.IntersectsWith(at))) yield return new TagSpan<InterLineAdornmentTag>(at, tag);
            }
            foreach (var note in saved)
            {
                if (note.Id == draft?.Id || !anchors.TryGetValue(note.Id, out var anchor)) continue;
                var selected = anchor.GetSpan(snapshot);
                var at = TagPosition(selected);
                if (!spans.Any(s => s.IntersectsWith(at))) continue;
                if (!savedTags.TryGetValue(note.Id, out var card))
                    savedTags[note.Id] = card = CreateSavedTag(note, selected, at.Start.GetContainingLine().LineNumber);
                yield return new TagSpan<InterLineAdornmentTag>(at, card);
            }
        }

        private static SnapshotSpan TagPosition(SnapshotSpan selected)
        {
            var line = (selected.IsEmpty ? selected.Start : selected.End - 1).GetContainingLine();
            return new SnapshotSpan(line.Start, Math.Min(1, line.Length));
        }

        private InterLineAdornmentTag CreateSavedTag(ReviewNote note, SnapshotSpan selected, int lastLine)
        {
            int firstLine = selected.Start.GetContainingLine().LineNumber;
            SavedNoteCard Build() => new SavedNoteCard(note, firstLine + 1, lastLine + 1, DarkTheme, CardWidth, () => ShowQueue(note),
                () => OpenEditor(firstLine, note), () => store.Delete(note));

            // Measure with the actual font and width so wrapped notes reserve exactly the space they use.
            var measured = Build();
            measured.Measure(new Size(CardWidth, double.PositiveInfinity));
            double height = Math.Ceiling(measured.DesiredSize.Height);
            return new InterLineAdornmentTag((t, v, p) =>
            {
                var element = measured ?? Build();
                measured = null;
                return element;
            }, false, height, HorizontalPositioningMode.ViewRelative, 4, null);
        }

        private void CloseCard()
        {
            draft = null;
            draftSpan = null;
            tag = null;
            Refresh();
            view.VisualElement.Focus();
        }

        private void TrackNoteKeyboard(TextBox box)
        {
            // The VS shell translates edit keys into source commands before WPF receives them.
            const uint wmKeyDown = 0x0100, wmKeyUp = 0x0101, wmChar = 0x0102;
            bool tracking = false;
            box.GotKeyboardFocus += (s, e) =>
            {
                if (tracking || !(PresentationSource.FromVisual(box) is HwndSource source)) return;
                keyboardTracking.BeginTrackingKeyboard(source.Handle, new[] { wmKeyDown, wmKeyUp, wmChar });
                tracking = true;
            };
            void Stop()
            {
                if (!tracking) return;
                tracking = false;
                keyboardTracking.EndTrackingKeyboard();
            }
            box.LostKeyboardFocus += (s, e) => Stop();
            box.Unloaded += (s, e) => Stop();
        }

        private UIElement MakeEditor()
        {
            var editing = draft;
            bool existing = editing.Revision != null;
            bool dark = DarkTheme;
            var ink = dark ? Brushes.WhiteSmoke : Brushes.Black;
            var surface = dark ? new SolidColorBrush(Color.FromRgb(38, 38, 41)) : Brushes.WhiteSmoke;
            var box = new TextBox { Text = editing.Comment, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = ink, Background = surface,
                CaretBrush = ink, MinHeight = 52, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 4, 0, 5),
                ToolTip = Strings.NoteHint };
            System.Windows.Automation.AutomationProperties.SetName(box, Strings.NoteBody);
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "OrcaNoteBody");
            TrackNoteKeyboard(box);
            box.TextChanged += (s, e) => editing.Comment = box.Text;
            var stack = new StackPanel();
            string state = editing.State == NoteStore.Checking ? Strings.CheckPreviousDelivery : "";
            string range = editing.Line == editing.LastLine ? editing.Line.ToString() : editing.Line + "–" + editing.LastLine;
            string heading = editing.Line == editing.LastLine ? Strings.LineHeading : Strings.LinesHeading;
            stack.Children.Add(new TextBlock { Text = string.Format(heading, range) + state, Foreground = ink, FontWeight = FontWeights.SemiBold });
            box.Height = 54;
            stack.Children.Add(box);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(actions);
            void Add(string label, Action action, bool primary = false)
            {
                var b = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4,0,0,0),
                    Background = primary ? new SolidColorBrush(Color.FromRgb(0, 103, 175)) : surface,
                    Foreground = primary ? Brushes.White : ink, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand };
                b.Click += (s,e) => { e.Handled = true; Try(action); };
                actions.Children.Add(b);
            }
            void Save()
            {
                if (string.IsNullOrWhiteSpace(editing.Comment)) throw new InvalidOperationException(Strings.EnterNote);
                var copy = editing.Copy();
                copy.State = NoteStore.Pending;
                store.Save(copy, editing.Revision);
                anchors[copy.Id] = draftSpan;
                CloseCard();
                string directory = Path.GetDirectoryName(document.FilePath);
                if (preloadedClient?.HasRecentPreview(directory) != true)
                {
                    preloadedClient = new OrcaClient(directory);
                    _ = preloadedClient.PreloadAsync();
                }
            }
            Add(Strings.Cancel, CloseCard);
            Add(existing ? Strings.SaveNote : Strings.AddNote, Save, true);
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; CloseCard(); }
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Try(Save); }
            };
            box.Loaded += (s,e) =>
            {
                if (focusEditor && draft == editing) { focusEditor = false; box.Focus(); box.CaretIndex = box.Text.Length; }
            };
            return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6), Width = CardWidth,
                Background = surface, Child = stack, VerticalAlignment = VerticalAlignment.Center };
        }

        internal void OpenQueue() => ShowQueue(null);

        private void ShowQueue(ReviewNote note)
        {
            string directory = Path.GetDirectoryName(document.FilePath);
            var client = preloadedClient?.HasRecentPreview(directory) == true ? preloadedClient : new OrcaClient(directory);
            preloadedClient = null;
            var dialog = new SendWindow(client, note);
            var owner = Window.GetWindow(view.VisualElement);
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
            if (draft?.Revision != null && !saved.Any(n => n.Id == draft.Id && n.Revision == draft.Revision))
                CloseCard();
        }

        internal static void Try(Action action)
        {
            try { action(); } catch (Exception e) { MessageBox.Show(e.Message, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
