using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    internal partial class SavedNoteCard : UserControl
    {
        internal SavedNoteCard(ReviewNote note, int firstLine, int lastLine, bool dark, double width,
            Action send, Action edit, Action delete)
        {
            InitializeComponent();
            Width = width;
            if (width < 240)
            {
                Frame.Padding = new Thickness(6, 10, 6, 10);
                Grid.SetColumn(Actions, 0);
                Grid.SetRow(Actions, 1);
                Actions.Margin = new Thickness(0, 4, 0, 0);
            }
            Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black;
            Frame.Background = new SolidColorBrush(dark ? Color.FromRgb(35,35,35) : Color.FromRgb(250,250,250));
            Frame.BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(70,70,70) : Color.FromRgb(203,203,203));
            string heading = firstLine == lastLine ? string.Format(Strings.SavedHeading, firstLine)
                : string.Format(Strings.SavedRangeHeading, firstLine, lastLine);
            Heading.Text = heading + (note.State == NoteStore.Checking ? Strings.NeedsReview : "");
            Body.Text = note.Comment;
            SetButtonText(Send, Strings.SendNote, Strings.SendThisNote);
            SetButtonText(Edit, Strings.EditNote);
            SetButtonText(Delete, Strings.DeleteNote);
            Send.Click += (s,e) => { e.Handled = true; EditorNotes.Try(send); };
            Edit.Click += (s,e) => { e.Handled = true; EditorNotes.Try(edit); };
            Delete.Click += (s,e) => { e.Handled = true; EditorNotes.Try(delete); };
        }

        private static void SetButtonText(Button button, string name, string toolTip = null)
        {
            button.ToolTip = toolTip ?? name;
            AutomationProperties.SetName(button, name);
        }
    }
}
