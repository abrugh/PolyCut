Imports System.Linq
Imports System.Text.Json

Imports CommunityToolkit.Mvvm.Input

Imports PolyCut.RichCanvas
Imports PolyCut.Shared

' Tile: repeat the selection in a grid (e.g. fill a sheet with one sticker design), optionally with a cut box
' around each tile, and group the result. Copies go through the same project-data paste path as Ctrl+V, so they
' are full independent copies (groups, mirrors, styles). The whole thing is one undo step.
Partial Public Class SVGPageViewModel

    Private _TileBoxes As Boolean = False
    ''' <summary>Remember the "cut a box around each tile" choice between uses.</summary>
    Public Property TileBoxes As Boolean
        Get
            Return _TileBoxes
        End Get
        Set(value As Boolean)
            SetProperty(_TileBoxes, value)
        End Set
    End Property

    Public Property TileSelectionCommand As ICommand = New RelayCommand(Sub() TileSelection())

    ''' <summary>How many tiles of size (w, h) with this gap fit from the tile's top-left to the sheet's far edges.</summary>
    Private Function FillCounts(tile As Rect, gap As Double, boxMargin As Double) As (Rows As Integer, Cols As Integer)
        Dim sheet = MainVM.Printer.WorkingRect
        Dim cols = Math.Floor((sheet.Right - boxMargin - tile.Left + gap) / (tile.Width + gap))
        Dim rows = Math.Floor((sheet.Bottom - boxMargin - tile.Top + gap) / (tile.Height + gap))
        Return (CInt(Math.Max(1, rows)), CInt(Math.Max(1, cols)))
    End Function

    Private Shared Function BoxMarginFor(gap As Double) As Double
        ' Box sits a quarter of the gap outside the design, leaving a weedable strip between neighbouring boxes
        Return gap / 4
    End Function

    Private Async Sub TileSelection()
        Dim items = GetAlignableItems()
        If items.Count = 0 Then Return

        Dim tile = items(0).Bounds
        For Each it In items.Skip(1)
            tile.Union(it.Bounds)
        Next
        If tile.Width <= 0 OrElse tile.Height <= 0 Then Return

        ' ---- dialog ----
        Dim gapBox As New TextBox With {.Width = 70, .Text = DistributeGap.ToString("0.##")}
        Dim rowsBox As New TextBox With {.Width = 70}
        Dim colsBox As New TextBox With {.Width = 70}
        Dim boxesCheck As New CheckBox With {.Content = "Cut a box around each tile".LT(), .IsChecked = TileBoxes, .Margin = New Thickness(0, 10, 0, 0)}
        Dim summary As New TextBlock With {.Margin = New Thickness(0, 10, 0, 0), .Opacity = 0.7, .TextWrapping = TextWrapping.Wrap}

        Dim parseGap = Function() As Double
                           Dim g As Double
                           Return If(Double.TryParse(gapBox.Text, g) AndAlso g >= 0, g, 0)
                       End Function
        Dim refill = Sub()
                         Dim m = If(boxesCheck.IsChecked = True, BoxMarginFor(parseGap()), 0)
                         Dim c = FillCounts(tile, parseGap(), m)
                         rowsBox.Text = c.Rows.ToString()
                         colsBox.Text = c.Cols.ToString()
                     End Sub
        Dim updateSummary = Sub()
                                Dim r, c As Integer
                                If Integer.TryParse(rowsBox.Text, r) AndAlso Integer.TryParse(colsBox.Text, c) AndAlso r > 0 AndAlso c > 0 Then
                                    summary.Text = "{0} tiles of {1} x {2} mm, filling right and down from the selection. Tip: align the design to the sheet's top-left first.".LTF(r * c, tile.Width.ToString("0.#"), tile.Height.ToString("0.#"))
                                Else
                                    summary.Text = ""
                                End If
                            End Sub
        AddHandler gapBox.TextChanged, Sub() refill()
        AddHandler boxesCheck.Click, Sub() refill()
        AddHandler rowsBox.TextChanged, Sub() updateSummary()
        AddHandler colsBox.TextChanged, Sub() updateSummary()
        refill()
        updateSummary()

        Dim row = Function(label As String, box As TextBox, unit As String) As UIElement
                      Dim sp As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 4, 0, 0)}
                      sp.Children.Add(New TextBlock With {.Text = label, .Width = 90, .VerticalAlignment = VerticalAlignment.Center})
                      sp.Children.Add(box)
                      sp.Children.Add(New TextBlock With {.Text = unit, .Margin = New Thickness(6, 0, 0, 0), .VerticalAlignment = VerticalAlignment.Center})
                      Return sp
                  End Function
        Dim content As New StackPanel With {.MinWidth = 320}
        content.Children.Add(row("Gap".LT(), gapBox, "mm"))
        content.Children.Add(row("Rows".LT(), rowsBox, ""))
        content.Children.Add(row("Columns".LT(), colsBox, ""))
        content.Children.Add(boxesCheck)
        content.Children.Add(summary)

        Dim dlg As New Wpf.Ui.Controls.MessageBox With {
            .Title = "Tile Selection".LT(),
            .Content = content,
            .IsPrimaryButtonEnabled = True,
            .PrimaryButtonText = "Tile".LT(),
            .CloseButtonText = "Cancel".LT(),
            .Owner = Application.Current.MainWindow,
            .WindowStartupLocation = WindowStartupLocation.CenterOwner}

        If Await dlg.ShowDialogAsync() <> Wpf.Ui.Controls.MessageBoxResult.Primary Then Return

        Dim gap = parseGap()
        Dim rows, cols As Integer
        If Not Integer.TryParse(rowsBox.Text, rows) OrElse Not Integer.TryParse(colsBox.Text, cols) OrElse rows < 1 OrElse cols < 1 Then Return
        If rows * cols > 2000 Then Return
        DistributeGap = gap
        TileBoxes = boxesCheck.IsChecked = True

        ApplyTiling(items.Select(Function(it) it.Item).ToList(), tile, gap, rows, cols, TileBoxes)
    End Sub


    Private Sub ApplyTiling(source As List(Of IDrawable), tile As Rect, gap As Double, rows As Integer, cols As Integer, boxes As Boolean)
        If rows * cols < 2 AndAlso Not boxes Then Return

        Dim projectService = Application.GetService(Of ProjectSerializationService)()
        Dim designerStyle = TryCast(Application.Current?.TryFindResource("DesignerItemStyle"), Style)
        Dim json = JsonSerializer.Serialize(projectService.CreateProjectDataForClipboard(source, MainVM.DrawingGroup))

        Dim actions As New List(Of IUndoableAction)
        Dim everything As New List(Of IDrawable)(source)
        Dim stepX = tile.Width + gap
        Dim stepY = tile.Height + gap

        For r = 0 To rows - 1
            For c = 0 To cols - 1
                If r = 0 AndAlso c = 0 Then Continue For      ' the original stays where it is

                Dim paste As New PasteDrawablesAction(MainVM, projectService, JsonSerializer.Deserialize(Of ProjectData)(json), designerStyle)
                If Not paste.Execute() Then Continue For
                actions.Add(paste)

                ' The paste leaves exactly the new copies selected; move them into their cell
                Dim moved As New List(Of (IDrawable, TransformAction.Snapshot, TransformAction.Snapshot))
                For Each d In PolyCanvas.SelectedItems.ToList()
                    Dim w = TryCast(d?.DrawableElement?.Parent, ContentControl)
                    If w Is Nothing Then Continue For
                    Dim before = TransformAction.MakeSnapshotFromWrapper(w)
                    TransformAction.ApplyMove(w, c * stepX, r * stepY)
                    moved.Add((d, before, TransformAction.MakeSnapshotFromWrapper(w)))
                    everything.Add(d)
                Next
                actions.Add(New TransformAction(moved))
            Next
        Next

        If boxes Then
            Dim m = BoxMarginFor(gap)
            For r = 0 To rows - 1
                For c = 0 To cols - 1
                    Dim rect As New Rectangle With {
                        .Width = tile.Width + 2 * m, .Height = tile.Height + 2 * m,
                        .Stroke = Brushes.Black, .StrokeThickness = 0.5, .Fill = Brushes.Transparent,
                        .StrokeLineJoin = PenLineJoin.Round}
                    Canvas.SetLeft(rect, tile.Left - m + c * stepX)
                    Canvas.SetTop(rect, tile.Top - m + r * stepY)

                    Dim add As New AddDrawableAction(MainVM, rect)
                    If Not add.Execute() Then Continue For
                    actions.Add(add)
                    Dim d = MainVM.DrawableCollection.FirstOrDefault(Function(x) x.DrawableElement Is rect)
                    If d IsNot Nothing Then everything.Add(d)
                Next
            Next
        End If

        If everything.Count >= 2 Then
            Dim grp As New GroupDrawablesAction(MainVM, everything)
            If grp.Execute() Then actions.Add(grp)
        End If

        If actions.Count > 0 Then _undoRedoService.Push(New CompositeAction(actions))
    End Sub

End Class
