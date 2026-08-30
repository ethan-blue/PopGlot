// This project enables both WPF and WinForms (the tray icon needs WinForms), so
// a dozen type names — TextBox, Button, Clipboard, Color, Point — exist in both
// namespaces and every file used to repeat the same alias block to disambiguate.
// Declaring the WPF-first choice once keeps the rest of the shell readable.
//
// WinForms types are reached through the `Forms` / `Drawing` namespace aliases.

global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

global using Forms = System.Windows.Forms;
global using Drawing = System.Drawing;

global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using Clipboard = System.Windows.Clipboard;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using ComboBox = System.Windows.Controls.ComboBox;
global using Control = System.Windows.Controls.Control;
global using Cursors = System.Windows.Input.Cursors;
global using DataObject = System.Windows.DataObject;
global using FontFamily = System.Windows.Media.FontFamily;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using ListBox = System.Windows.Controls.ListBox;
global using MessageBox = System.Windows.MessageBox;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Orientation = System.Windows.Controls.Orientation;
global using Point = System.Windows.Point;
global using RadioButton = System.Windows.Controls.RadioButton;
global using Rectangle = System.Windows.Shapes.Rectangle;
global using RichTextBox = System.Windows.Controls.RichTextBox;
global using Size = System.Windows.Size;
global using TextBox = System.Windows.Controls.TextBox;
global using TextDataFormat = System.Windows.TextDataFormat;
global using VerticalAlignment = System.Windows.VerticalAlignment;
