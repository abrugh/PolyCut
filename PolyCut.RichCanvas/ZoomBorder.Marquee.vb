Imports System.Windows.Documents

Imports PolyCut.Shared

' Drag-select (marquee) with the pointer tool: press on empty canvas and drag to draw a selection box. Everything the
' box touches is selected; holding Shift adds to the current selection instead (Shift-click still toggles single
' items). A press that doesn't move past a few pixels stays a plain click, which clears the selection as before.
Partial Public Class ZoomBorder

    Private Const MarqueeStartDistance As Double = 4.0

    Private _marqueeStart As Point?          ' press point in ZoomBorder coordinates, while a marquee may start
    Private _marqueeActive As Boolean
    Private _marqueeAdorner As MarqueeAdorner

    Private _marqueeWired As Boolean

    Private Sub EnsureMarqueeWiring()
        If _marqueeWired Then Return
        _marqueeWired = True
        AddHandler Me.PreviewMouseMove, AddressOf Marquee_PreviewMouseMove
        AddHandler Me.PreviewMouseUp, AddressOf Marquee_PreviewMouseUp
        AddHandler Me.LostMouseCapture, AddressOf Marquee_LostMouseCapture
    End Sub

    ''' <summary>Called on every preview mouse-down: arms a marquee when the press is on empty canvas.</summary>
    Private Sub MarqueeMouseDown(e As MouseButtonEventArgs)
        EnsureMarqueeWiring()
        _marqueeStart = Nothing
        If CanvasMode <> CanvasMode.Selection OrElse e.ChangedButton <> MouseButton.Left Then Return

        Dim pc = GetPolyCanvas()
        If pc Is Nothing Then Return
        If pc.IsClickOnCanvasChild(TryCast(e.OriginalSource, DependencyObject)) Then Return
        If GeometryHitTestHelper.HitTestTopmost(pc.ChildrenCollection, e.GetPosition(pc)) IsNot Nothing Then Return

        _marqueeStart = e.GetPosition(Me)
    End Sub

    Private Sub Marquee_PreviewMouseMove(sender As Object, e As MouseEventArgs)
        If Not _marqueeStart.HasValue Then Return
        If e.LeftButton <> MouseButtonState.Pressed Then
            EndMarquee()
            Return
        End If

        Dim p = e.GetPosition(Me)
        If Not _marqueeActive Then
            Dim d = p - _marqueeStart.Value
            If d.Length < MarqueeStartDistance Then Return
            _marqueeActive = True
            Me.CaptureMouse()
            Dim layer = AdornerLayer.GetAdornerLayer(Me)
            If layer IsNot Nothing Then
                _marqueeAdorner = New MarqueeAdorner(Me)
                layer.Add(_marqueeAdorner)
            End If
        End If

        If _marqueeAdorner IsNot Nothing Then _marqueeAdorner.Band = New Rect(_marqueeStart.Value, p)
        e.Handled = True
    End Sub

    Private Sub Marquee_PreviewMouseUp(sender As Object, e As MouseButtonEventArgs)
        If e.ChangedButton <> MouseButton.Left OrElse Not _marqueeStart.HasValue Then Return

        If _marqueeActive Then
            SelectInMarquee(_marqueeStart.Value, e.GetPosition(Me))
            e.Handled = True
        End If
        EndMarquee()
    End Sub

    Private Sub Marquee_LostMouseCapture(sender As Object, e As MouseEventArgs)
        ' Capture taken away mid-drag (e.g. alt-tab): drop the marquee without selecting
        If _marqueeActive AndAlso Mouse.Captured IsNot Me Then EndMarquee()
    End Sub

    Private Sub SelectInMarquee(a As Point, b As Point)
        Dim pc = GetPolyCanvas()
        If pc Is Nothing Then Return

        Dim band As New Rect(Me.TranslatePoint(a, pc), Me.TranslatePoint(b, pc))
        Dim hits As New List(Of IDrawable)
        For Each d In pc.ChildrenCollection
            If d Is Nothing OrElse d.IsHidden OrElse d.DrawableElement Is Nothing Then Continue For
            Dim wrapper = TryCast(d.DrawableElement.Parent, ContentControl)
            If wrapper Is Nothing OrElse Not wrapper.IsVisible OrElse d.DrawableElement.Visibility <> Visibility.Visible Then Continue For
            If GeometryHitTestHelper.TouchesRect(d, band) Then hits.Add(d)
        Next

        Dim additive = Keyboard.IsKeyDown(Key.LeftShift) OrElse Keyboard.IsKeyDown(Key.RightShift)
        pc.SelectionManager.SelectRange(hits, clearExisting:=Not additive)
    End Sub

    Private Sub EndMarquee()
        Dim wasActive = _marqueeActive
        _marqueeStart = Nothing
        _marqueeActive = False

        If _marqueeAdorner IsNot Nothing Then
            AdornerLayer.GetAdornerLayer(Me)?.Remove(_marqueeAdorner)
            _marqueeAdorner = Nothing
        End If
        If wasActive AndAlso Mouse.Captured Is Me Then Me.ReleaseMouseCapture()
    End Sub


    ''' <summary>Draws the selection box over the canvas (screen space, so it stays crisp at any zoom).</summary>
    Private NotInheritable Class MarqueeAdorner : Inherits Adorner

        Private Shared ReadOnly FillBrush As Brush = MakeFill()
        Private Shared ReadOnly OutlinePen As Pen = MakePen()
        Private _band As Rect

        Public Sub New(adorned As UIElement)
            MyBase.New(adorned)
            IsHitTestVisible = False
        End Sub

        Public Property Band As Rect
            Get
                Return _band
            End Get
            Set(value As Rect)
                _band = value
                InvalidateVisual()
            End Set
        End Property

        Protected Overrides Sub OnRender(dc As DrawingContext)
            If _band.IsEmpty Then Return
            dc.DrawRectangle(FillBrush, OutlinePen, _band)
        End Sub

        Private Shared Function MakeFill() As Brush
            Dim b As New SolidColorBrush(Color.FromArgb(40, 0, 120, 215))
            b.Freeze()
            Return b
        End Function

        Private Shared Function MakePen() As Pen
            Dim p As New Pen(New SolidColorBrush(Color.FromArgb(220, 0, 120, 215)), 1) With {.DashStyle = New DashStyle({4.0, 3.0}, 0)}
            p.Freeze()
            Return p
        End Function
    End Class

End Class
